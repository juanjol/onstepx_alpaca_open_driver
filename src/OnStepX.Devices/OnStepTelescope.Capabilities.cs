using ASCOM.Common.DeviceInterfaces;
using OnStepX.Core.Protocol;

namespace OnStepX.Devices;

/// <summary>
/// Capability flags and static description of the mount.
/// </summary>
public sealed partial class OnStepTelescope
{
    /// <summary>
    /// OnStep works in the equinox of date, so coordinates are topocentric JNow and
    /// never J2000. Reporting anything else would make clients precess twice.
    /// </summary>
    public EquatorialCoordinateType EquatorialSystem => EquatorialCoordinateType.Topocentric;

    /// <summary>
    /// Mount geometry, from the type reported in <c>:GU#</c>.
    /// </summary>
    /// <remarks>
    /// ASCOM offers only three geometries, so alt alt mounts, which have two
    /// altitude axes and are neither equatorial nor azimuthal, are reported as
    /// <see cref="AlignmentMode.AltAz"/>. It is the least wrong of the three for a
    /// non equatorial mount, and it keeps clients from applying pier flip logic that
    /// makes no sense there.
    /// </remarks>
    public AlignmentMode AlignmentMode => Snapshot.Status.MountKind switch
    {
        MountKind.Gem => AlignmentMode.GermanPolar,
        MountKind.Fork => AlignmentMode.Polar,
        MountKind.AltAzm => AlignmentMode.AltAz,
        MountKind.AltAlt => AlignmentMode.AltAz,

        // Before the first poll, assume the most common case rather than throwing:
        // clients read this during connection setup.
        _ => AlignmentMode.GermanPolar,
    };

    /// <summary>True for a German equatorial, where meridian flips apply.</summary>
    private bool IsGermanEquatorial => Snapshot.Status.MountKind == MountKind.Gem;

    /// <summary>Aperture diameter in metres, from configuration.</summary>
    /// <remarks>
    /// The controller knows nothing about the optics, so these come from the setup
    /// page, exactly as in the old driver's Optics box.
    /// </remarks>
    public double ApertureDiameter => Settings.Telescope.ApertureDiameter;

    /// <summary>
    /// Effective aperture area in square metres.
    /// </summary>
    /// <remarks>
    /// If the user has not entered one it is derived from the diameter, since a
    /// plain circle is a better answer than zero for a client computing exposure.
    /// </remarks>
    public double ApertureArea
    {
        get
        {
            double configured = Settings.Telescope.ApertureArea;
            if (configured > 0)
            {
                return configured;
            }

            double radius = Settings.Telescope.ApertureDiameter / 2.0;
            return Math.PI * radius * radius;
        }
    }

    /// <summary>Focal length in metres, from configuration.</summary>
    public double FocalLength => Settings.Telescope.FocalLength;

    /// <summary>OnStep can always find home.</summary>
    public bool CanFindHome => true;

    /// <summary>OnStep supports park and unpark.</summary>
    public bool CanPark => true;

    /// <summary>Park position can be set from the current position with <c>:hQ#</c>.</summary>
    public bool CanSetPark => true;

    /// <summary>Unpark is supported.</summary>
    public bool CanUnpark => true;

    /// <summary>Pulse guiding is supported through <c>:Mg#</c>.</summary>
    public bool CanPulseGuide => true;

    /// <summary>Tracking can be started and stopped with <c>:Te#</c> and <c>:Td#</c>.</summary>
    public bool CanSetTracking => true;

    /// <summary>Tracking rate offsets are supported through <c>:SXTD#</c>.</summary>
    public bool CanSetDeclinationRate => true;

    /// <summary>Tracking rate offsets are supported through <c>:SXTR#</c>.</summary>
    public bool CanSetRightAscensionRate => true;

    /// <summary>Guide rates can be set with <c>:RAn.n#</c> and <c>:REn.n#</c>.</summary>
    public bool CanSetGuideRates => true;

    /// <summary>
    /// Forcing a pier side only makes sense on a German equatorial, where
    /// <c>:MNe#</c> and <c>:MNw#</c> reach the same sky position from either side.
    /// </summary>
    public bool CanSetPierSide => IsGermanEquatorial;

    /// <summary>Equatorial slews are supported.</summary>
    public bool CanSlew => true;

    /// <summary>Equatorial slews are supported asynchronously.</summary>
    public bool CanSlewAsync => true;

    /// <summary>Alt az slews are supported through <c>:Sa#</c>, <c>:Sz#</c> and <c>:MA#</c>.</summary>
    public bool CanSlewAltAz => true;

    /// <summary>Alt az slews are supported asynchronously.</summary>
    public bool CanSlewAltAzAsync => true;

    /// <summary>Sync to equatorial coordinates is supported through <c>:CS#</c>.</summary>
    public bool CanSync => true;

    /// <summary>
    /// Sync to alt az coordinates. Supported by converting to equatorial first, since
    /// OnStep syncs on the target and the target can be set in either frame.
    /// </summary>
    public bool CanSyncAltAz => true;

    /// <summary>
    /// Both mount axes can be driven directly.
    /// </summary>
    public bool CanMoveAxis(TelescopeAxis axis) => axis switch
    {
        TelescopeAxis.Primary => true,
        TelescopeAxis.Secondary => true,

        // The tertiary axis is the rotator, which is its own ASCOM device here.
        TelescopeAxis.Tertiary => false,

        _ => false,
    };

    /// <summary>
    /// Rates available to <see cref="MoveAxis"/>, in degrees per second.
    /// </summary>
    /// <remarks>
    /// The upper bound is the mount's current slew rate from <c>:GX97#</c>, so it
    /// reflects the configured maximum goto speed rather than an invented number. A
    /// single range from just above zero to that maximum is correct because OnStep
    /// accepts any rate in between through <c>:RAn.n#</c>.
    /// </remarks>
    public IAxisRates AxisRates(TelescopeAxis axis)
    {
        if (!CanMoveAxis(axis))
        {
            return new TelescopeAxisRates([]);
        }

        double maximum = MaximumSlewDegreesPerSecond();

        return new TelescopeAxisRates([new TelescopeRate(0.0, maximum)]);
    }

    /// <summary>
    /// Reads the mount's current slew rate in degrees per second.
    /// </summary>
    private double MaximumSlewDegreesPerSecond()
    {
        try
        {
            double rate = RunSync(() => Channel.GetDoubleAsync("GX97", CancellationToken.None));

            // A mount that reports nonsense should not produce an empty rate list,
            // which would make MoveAxis unusable.
            return rate > 0 ? rate : 3.0;
        }
        catch (ASCOM.DriverException)
        {
            return 3.0;
        }
    }

    /// <summary>Drive rates OnStep supports.</summary>
    public ITrackingRates TrackingRates { get; } = new TelescopeTrackingRates(
    [
        DriveRate.Sidereal,
        DriveRate.Lunar,
        DriveRate.Solar,
        DriveRate.King,
    ]);
}
