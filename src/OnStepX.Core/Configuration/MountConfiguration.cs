using OnStepX.Core.Protocol;

namespace OnStepX.Core.Configuration;

/// <summary>
/// Pier side the firmware prefers when a target is reachable from both,
/// from <c>:GX96#</c>.
/// </summary>
public enum PreferredPierSide
{
    /// <summary>Not reported.</summary>
    Unknown,

    /// <summary>East.</summary>
    East,

    /// <summary>West.</summary>
    West,

    /// <summary>Whichever gives the longest run before a flip.</summary>
    Best,

    /// <summary>Decided by the firmware.</summary>
    Automatic,
}

/// <summary>
/// Mount type from <c>:GXEM#</c>, which is also what <c>:SXEM,n#</c> writes for the
/// next restart.
/// </summary>
/// <remarks>
/// The numeric values are the firmware's own constants and must not be renumbered.
/// </remarks>
public enum OnStepMountType
{
    /// <summary>Not reported.</summary>
    Unknown = 0,

    /// <summary>German equatorial.</summary>
    Gem = 1,

    /// <summary>Fork.</summary>
    Fork = 2,

    /// <summary>Alt azimuth.</summary>
    AltAzm = 3,

    /// <summary>Alt alt.</summary>
    AltAlt = 4,

    /// <summary>German equatorial with a tangent arm.</summary>
    GemTangentArm = 5,

    /// <summary>German equatorial with a tangent arm and a cross axis.</summary>
    GemTangentArmCross = 6,

    /// <summary>Fork with a tangent arm.</summary>
    ForkTangentArm = 7,

    /// <summary>Fork with a tangent arm and a cross axis.</summary>
    ForkTangentArmCross = 8,

    /// <summary>Alt azimuth with unlimited azimuth travel.</summary>
    AltAzmUnlimited = 9,
}

/// <summary>Site location and clock, as the controller holds them.</summary>
/// <remarks>
/// Every field here lives in the controller's own non volatile storage, not in the
/// driver settings file. That is deliberate: the mount needs its site and time to slew
/// correctly whether or not a driver is attached, so a second copy in the driver would
/// only create the question of which one is right.
/// </remarks>
public sealed record SiteConfiguration
{
    /// <summary>Latitude in degrees, positive north. Same sign in both worlds.</summary>
    public FirmwareValue<double> Latitude { get; init; }

    /// <summary>
    /// Longitude in degrees, <b>positive east</b>, already converted from the
    /// controller's west positive convention by <see cref="OnStepClock"/>.
    /// </summary>
    public FirmwareValue<double> Longitude { get; init; }

    /// <summary>Elevation above sea level in metres.</summary>
    public FirmwareValue<double> Elevation { get; init; }

    /// <summary>
    /// Offset in hours to <b>add</b> to local time to reach UT1, which is the negative
    /// of the timezone offset as normally written.
    /// </summary>
    public FirmwareValue<double> UtcOffsetHours { get; init; }

    /// <summary>Local standard date and time as the controller reports them.</summary>
    public FirmwareValue<DateTime> LocalStandardTime { get; init; }

    /// <summary>UT1 date and time, from <c>:GX81#</c> and <c>:GX80#</c>.</summary>
    public FirmwareValue<DateTime> UniversalTime { get; init; }

    /// <summary>Local apparent sidereal time in hours, from <c>:GS#</c>.</summary>
    public FirmwareValue<double> SiderealTime { get; init; }

    /// <summary>
    /// The controller considers its date and time usable. <c>:GX89#</c> is inverted:
    /// it answers <c>0</c> when ready.
    /// </summary>
    public FirmwareValue<bool> ClockReady { get; init; }

    /// <summary>Active site slot, 0 to 3, from <c>:W?#</c>.</summary>
    public FirmwareValue<int> ActiveSiteSlot { get; init; }
}

/// <summary>Meridian flip behaviour.</summary>
public sealed record MeridianConfiguration
{
    /// <summary>Automatic flip when the meridian limit is crossed.</summary>
    public bool AutoFlip { get; init; }

    /// <summary>What the mount does on its way to the other side.</summary>
    public MeridianFlipHomeMode HomeMode { get; init; }

    /// <summary>Pier side chosen when a target is reachable from both.</summary>
    public FirmwareValue<PreferredPierSide> PreferredSide { get; init; }

    /// <summary>The mount is parked at home waiting for permission to continue.</summary>
    public bool WaitingAtHome { get; init; }
}

/// <summary>Motion limits held by the firmware.</summary>
public sealed record LimitConfiguration
{
    /// <summary>Horizon limit in degrees of altitude, from <c>:Gh#</c>.</summary>
    public FirmwareValue<double> HorizonDegrees { get; init; }

    /// <summary>Overhead limit in degrees of altitude, from <c>:Go#</c>.</summary>
    public FirmwareValue<double> OverheadDegrees { get; init; }

    /// <summary>East meridian limit in minutes past the meridian.</summary>
    public FirmwareValue<int> MeridianEastMinutes { get; init; }

    /// <summary>West meridian limit in minutes past the meridian.</summary>
    public FirmwareValue<int> MeridianWestMinutes { get; init; }

    /// <summary>Axis 1 minimum, in degrees. Read only.</summary>
    public FirmwareValue<double> Axis1MinimumDegrees { get; init; }

    /// <summary>Axis 1 maximum, in degrees. Read only.</summary>
    public FirmwareValue<double> Axis1MaximumDegrees { get; init; }

    /// <summary>Axis 1 maximum expressed in hours. Read only.</summary>
    public FirmwareValue<double> Axis1MaximumHours { get; init; }

    /// <summary>Axis 2 minimum, in degrees. Read only.</summary>
    public FirmwareValue<double> Axis2MinimumDegrees { get; init; }

    /// <summary>Axis 2 maximum, in degrees. Read only.</summary>
    public FirmwareValue<double> Axis2MaximumDegrees { get; init; }
}

/// <summary>Tracking compensation and rate offsets.</summary>
public sealed record TrackingConfiguration
{
    /// <summary>Compensation model in force, read from <c>:GU#</c>.</summary>
    public TrackingCompensation Compensation { get; init; }

    /// <summary>Tracking is running.</summary>
    public bool IsTracking { get; init; }

    /// <summary>Selected drive rate.</summary>
    public MountTrackingRate Rate { get; init; }

    /// <summary>Right ascension offset in arcseconds per sidereal second.</summary>
    /// <remarks>
    /// These are the firmware's own units. The ASCOM
    /// <c>RightAscensionRate</c> property is in seconds of right ascension per sidereal
    /// second instead, a factor of fifteen apart, and the conversion belongs to the
    /// telescope device and not here. The setup page shows the firmware value with its
    /// unit spelled out, so that the two are never confused.
    /// </remarks>
    public FirmwareValue<double> RightAscensionOffset { get; init; }

    /// <summary>Declination offset in arcseconds per sidereal second.</summary>
    public FirmwareValue<double> DeclinationOffset { get; init; }
}

/// <summary>Axis backlash compensation, in arcseconds.</summary>
public sealed record BacklashConfiguration
{
    /// <summary>Right ascension or azimuth axis.</summary>
    public FirmwareValue<int> RightAscensionArcseconds { get; init; }

    /// <summary>Declination or altitude axis.</summary>
    public FirmwareValue<int> DeclinationArcseconds { get; init; }
}

/// <summary>Goto speed.</summary>
/// <remarks>
/// The firmware expresses this as a step period in microseconds, so a <b>smaller</b>
/// period is a faster slew. The old setup form showed both that and the resulting
/// degrees per second, and so does this, but the degrees per second is derived and read
/// only: converting between the two needs the steps per degree of the axis, and a field
/// that silently recomputes the other one is a bug surface for no gain.
/// </remarks>
public sealed record SlewRateConfiguration
{
    /// <summary>Current slew period in microseconds per step, from <c>:GX92#</c>.</summary>
    public FirmwareValue<double> CurrentPeriodMicroseconds { get; init; }

    /// <summary>Default slew period in microseconds per step, from <c>:GX93#</c>.</summary>
    public FirmwareValue<double> BasePeriodMicroseconds { get; init; }

    /// <summary>
    /// Fastest period the firmware will accept, from <c>:GX99#</c>. Anything below this
    /// is rejected, which is what stops the UI offering a rate the mount cannot drive.
    /// </summary>
    public FirmwareValue<double> FastestPeriodMicroseconds { get; init; }

    /// <summary>Resulting speed in degrees per second, from <c>:GX97#</c>.</summary>
    public FirmwareValue<double> DegreesPerSecond { get; init; }
}

/// <summary>Home position configuration.</summary>
public sealed record HomeConfiguration
{
    /// <summary>The mount has home sensors, from the first field of <c>:h?#</c>.</summary>
    public FirmwareValue<bool> HasSensors { get; init; }

    /// <summary>Axis 1 home offset in arcseconds.</summary>
    public FirmwareValue<int> Axis1OffsetArcseconds { get; init; }

    /// <summary>Axis 2 home offset in arcseconds.</summary>
    public FirmwareValue<int> Axis2OffsetArcseconds { get; init; }

    /// <summary>Automatic homing when the controller powers up, read from <c>:GU#</c>.</summary>
    public bool AutoHomeAtBoot { get; init; }

    /// <summary>The mount is at its home position.</summary>
    public bool IsAtHome { get; init; }

    /// <summary>A homing move is under way.</summary>
    public bool IsHoming { get; init; }
}

/// <summary>Periodic error correction.</summary>
public sealed record PecConfiguration
{
    /// <summary>
    /// Whether this build has PEC at all. Derived from <c>:GU#</c>, which omits the PEC
    /// characters entirely when the feature is not compiled in.
    /// </summary>
    public bool IsSupported { get; init; }

    /// <summary>Playback and recording state.</summary>
    public PecState State { get; init; }

    /// <summary>There is recorded data in the buffer.</summary>
    public bool HasRecordedData { get; init; }

    /// <summary>Worm rotation steps, from <c>:VW#</c>.</summary>
    public FirmwareValue<long> WormSteps { get; init; }

    /// <summary>Worm rotation steps stored in non volatile memory, from <c>:GXE7#</c>.</summary>
    public FirmwareValue<long> WormStepsStored { get; init; }

    /// <summary>Buffer size in sidereal seconds, from <c>:GXE8#</c>.</summary>
    public FirmwareValue<long> BufferSeconds { get; init; }

    /// <summary>Steps per sidereal second, from <c>:GXE6#</c>.</summary>
    public FirmwareValue<double> StepsPerSiderealSecond { get; init; }

    /// <summary>Index sense position in sidereal seconds, from <c>:VH#</c>.</summary>
    public FirmwareValue<long> IndexSensePosition { get; init; }
}

/// <summary>
/// Everything the mount page reads in one go, so that a page load is one pass over the
/// channel rather than one round trip per control.
/// </summary>
public sealed record MountConfiguration
{
    /// <summary>Site and clock.</summary>
    public SiteConfiguration Site { get; init; } = new();

    /// <summary>Meridian flip.</summary>
    public MeridianConfiguration Meridian { get; init; } = new();

    /// <summary>Limits.</summary>
    public LimitConfiguration Limits { get; init; } = new();

    /// <summary>Tracking.</summary>
    public TrackingConfiguration Tracking { get; init; } = new();

    /// <summary>Backlash.</summary>
    public BacklashConfiguration Backlash { get; init; } = new();

    /// <summary>Goto speed.</summary>
    public SlewRateConfiguration SlewRate { get; init; } = new();

    /// <summary>Home.</summary>
    public HomeConfiguration Home { get; init; } = new();

    /// <summary>Periodic error correction.</summary>
    public PecConfiguration Pec { get; init; } = new();

    /// <summary>Mount type currently in force.</summary>
    public FirmwareValue<OnStepMountType> MountType { get; init; }

    /// <summary>Buzzer enabled, read from <c>:GU#</c>.</summary>
    public bool BuzzerEnabled { get; init; }

    /// <summary>Park state.</summary>
    public ParkState ParkState { get; init; }

    /// <summary>Status string the whole snapshot was derived from, for diagnostics.</summary>
    public string StatusRaw { get; init; } = string.Empty;
}
