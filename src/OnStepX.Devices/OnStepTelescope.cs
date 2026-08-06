using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using OnStepX.Core.Config;
using OnStepX.Core.Devices;
using OnStepX.Core.Hardware;
using OnStepX.Core.Protocol;

namespace OnStepX.Devices;

/// <summary>
/// ASCOM telescope device backed by an OnStepX controller.
/// </summary>
/// <remarks>
/// <para>
/// Property reads are served from <see cref="MountPoller"/>'s cached snapshot, so
/// they cost no serial traffic. Commands go straight to the shared channel and then
/// force a refresh, because Conform checks that state such as <c>Slewing</c> changes
/// the instant a command is accepted.
/// </para>
/// <para>
/// Two sign conventions differ between ASCOM and OnStep and are converted in exactly
/// one place each, in the site properties: <b>longitude</b> (ASCOM east positive,
/// OnStep west positive) and the <b>UTC offset</b> (OnStep stores the value to add
/// to local time to reach UT1, the negative of the usual timezone offset). OnStep
/// also never applies daylight saving: its clock is always standard time.
/// </para>
/// </remarks>
public sealed partial class OnStepTelescope : OnStepDeviceBase, ITelescopeV4
{
    private readonly MountPoller _poller;

    // Values ASCOM expects the driver to remember rather than read back.
    private bool _targetRightAscensionSet;
    private bool _targetDeclinationSet;
    private double _targetRightAscension;
    private double _targetDeclination;
    private short _slewSettleTime;
    private double _guideRateRightAscension = DefaultGuideRateDegreesPerSecond;
    private double _guideRateDeclination = DefaultGuideRateDegreesPerSecond;

    // MoveAxis motion is not visible in the status word, so the driver tracks it.
    private bool _primaryAxisMoving;
    private bool _secondaryAxisMoving;

    /// <summary>
    /// Sidereal rate expressed in degrees per second, which is the ASCOM unit for
    /// guide rates. One sidereal revolution is 360 degrees in 86164.0905 seconds.
    /// </summary>
    private const double SiderealDegreesPerSecond = 360.0 / 86164.0905;

    /// <summary>
    /// Default guide rate. OnStep's <c>:RG#</c> preset is one times sidereal.
    /// </summary>
    private const double DefaultGuideRateDegreesPerSecond = SiderealDegreesPerSecond;

    /// <summary>Creates the telescope device.</summary>
    public OnStepTelescope(
        OnStepXConnection connection,
        Func<OnStepXSettings> settingsProvider,
        ILoggerFactory loggerFactory)
        : base(connection, settingsProvider, RequireFactory(loggerFactory).CreateLogger<OnStepTelescope>())
    {
        _poller = new MountPoller(
            () => Channel,
            TimeSpan.FromMilliseconds(settingsProvider().Connection.PollIntervalMilliseconds),
            loggerFactory.CreateLogger<MountPoller>());
    }

    private static ILoggerFactory RequireFactory(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory;
    }

    /// <inheritdoc />
    protected override string DeviceKey => "Telescope";

    /// <inheritdoc />
    public override void InvalidateSnapshot() => _poller.Invalidate();

    /// <inheritdoc />
    public override string Name => "OnStepX Telescope";

    /// <inheritdoc />
    public override string Description =>
        Connection.Identity is { } identity
            ? $"OnStepX mount, firmware {identity.FirmwareVersion}"
            : "OnStepX mount";

    /// <summary>
    /// Interface version 4, matching <see cref="ITelescopeV4"/> and the Platform 7
    /// asynchronous connection model.
    /// </summary>
    public override short InterfaceVersion => 4;

    /// <summary>Latest snapshot, refreshed by the background poller.</summary>
    private MountSnapshot Snapshot => _poller.Current;

    /// <summary>
    /// Snapshot that is guaranteed to hold real data, for members that cannot
    /// sensibly answer from defaults.
    /// </summary>
    private MountSnapshot ValidSnapshot
    {
        get
        {
            RequireConnected();

            // GetFresh only does I/O if the background poller has fallen behind, so
            // the common case is still a free read of the cache.
            MountSnapshot snapshot = _poller.GetFresh();

            if (!snapshot.IsValid)
            {
                throw new ASCOM.NotConnectedException(
                    "No status has been read from the mount yet.");
            }

            return snapshot;
        }
    }

    /// <inheritdoc />
    protected override async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        OnStepXSettings settings = Settings;

        _poller.PollInterval = TimeSpan.FromMilliseconds(
            Math.Clamp(settings.Connection.PollIntervalMilliseconds, 100, 10_000));

        if (settings.Telescope.SetDateTimeOnConnect)
        {
            await PushDateTimeAsync(DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        await _poller.StartAsync(cancellationToken).ConfigureAwait(false);

        // Seed the cached tracking rate from whatever the mount reports, so a client
        // reading TrackingRate before setting it gets the truth rather than a guess.
        MountTrackingRate reported = _poller.Current.Status.TrackingRate;
        if (reported != MountTrackingRate.Unknown)
        {
            _trackingRate = ToDriveRate(reported);
        }

        Logger.LogInformation(
            "Telescope connected to {Identity}", Connection.Identity?.ToString() ?? "unknown");
    }

    /// <inheritdoc />
    protected override async Task OnDisconnectingAsync()
    {
        await _poller.StopAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override List<StateValue> DeviceState
    {
        get
        {
            MountSnapshot snapshot = Snapshot;

            if (!snapshot.IsValid)
            {
                return [];
            }

            // Every value comes from one snapshot, so a client reading DeviceState
            // gets coordinates and flags that were all true at the same instant.
            var state = new List<StateValue>
            {
                new(nameof(ITelescopeV4.Altitude), snapshot.Altitude),
                new(nameof(ITelescopeV4.AtHome), snapshot.Status.IsAtHome),
                new(nameof(ITelescopeV4.AtPark), snapshot.Status.IsParked),
                new(nameof(ITelescopeV4.Azimuth), snapshot.Azimuth),
                new(nameof(ITelescopeV4.Declination), snapshot.Declination),
                new(nameof(ITelescopeV4.IsPulseGuiding), snapshot.Status.PulseGuideActive),
                new(nameof(ITelescopeV4.RightAscension), snapshot.RightAscension),
                new(nameof(ITelescopeV4.SideOfPier), ToPointingState(snapshot.Status.PierSide)),
                new(nameof(ITelescopeV4.SiderealTime), snapshot.SiderealTime),
                new(nameof(ITelescopeV4.Slewing), snapshot.IsSlewing),
                new(nameof(ITelescopeV4.Tracking), snapshot.Status.IsTracking),
                new("TimeStamp", snapshot.Timestamp),
            };

            return state;
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _poller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }

    /// <summary>
    /// Runs a command and then refreshes the snapshot, so the next property read
    /// already reflects what the command did.
    /// </summary>
    private void RunCommandAndRefresh(Func<Task> command)
    {
        RunSync(async () =>
        {
            await command().ConfigureAwait(false);
            await _poller.RefreshNowAsync(CancellationToken.None).ConfigureAwait(false);
        });
    }
}
