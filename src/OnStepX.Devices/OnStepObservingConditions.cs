using ASCOM;
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using OnStepX.Core.Config;
using OnStepX.Core.Devices;
using OnStepX.Core.Hardware;
using OnStepX.Core.Protocol;

namespace OnStepX.Devices;

/// <summary>One reading of the environmental sensors.</summary>
public sealed record WeatherSnapshot
{
    /// <summary>Ambient temperature in degrees Celsius, if a sensor is present.</summary>
    public double? Temperature { get; init; }

    /// <summary>Barometric pressure in hectopascals, if a sensor is present.</summary>
    public double? Pressure { get; init; }

    /// <summary>Relative humidity in percent, if a sensor is present.</summary>
    public double? Humidity { get; init; }

    /// <summary>Dew point in degrees Celsius, if it can be determined.</summary>
    public double? DewPoint { get; init; }

    /// <summary>When the reading was taken.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// ASCOM observing conditions device backed by OnStepX's environmental sensors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absent sensors must not read as zero.</b> OnStep answers <c>:GX9A#</c> and its
/// siblings with a plain <c>0</c> when the sensor is not compiled into the firmware, and
/// that is not a measurement. Zero degrees, zero percent humidity and a zero dew point
/// are all perfectly believable values, so a client would use them: a dew point of zero
/// against a real temperature of five degrees reads as an imminent dew alarm, and a
/// safety monitor would close the roof for no reason.
/// </para>
/// <para>
/// So every absent sensor throws <see cref="PropertyNotImplementedException"/>, which is
/// what ASCOM defines and what clients already know how to handle. Which sensors exist
/// is probed once at connect.
/// </para>
/// </remarks>
public sealed class OnStepObservingConditions : OnStepDeviceBase, IObservingConditionsV2
{
    private readonly SnapshotPoller<WeatherSnapshot> _poller;

    private bool _hasTemperature;
    private bool _hasPressure;
    private bool _hasHumidity;

    /// <summary>Creates the observing conditions device.</summary>
    public OnStepObservingConditions(
        OnStepXConnection connection,
        Func<OnStepXSettings> settingsProvider,
        ILoggerFactory loggerFactory)
        : base(connection, settingsProvider,
            Require(loggerFactory).CreateLogger<OnStepObservingConditions>())
    {
        _poller = new SnapshotPoller<WeatherSnapshot>(
            "ObservingConditions",
            ReadSnapshotAsync,

            // Weather changes slowly, so polling it as fast as the mount would waste
            // serial bandwidth that the mount needs.
            TimeSpan.FromSeconds(5),
            loggerFactory.CreateLogger<OnStepObservingConditions>());
    }

    private static ILoggerFactory Require(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory;
    }

    /// <inheritdoc />
    protected override string DeviceKey => "ObservingConditions";

    /// <inheritdoc />
    public override void InvalidateSnapshot() => _poller.Invalidate();

    /// <inheritdoc />
    public override string Name => "OnStepX Observing Conditions";

    /// <inheritdoc />
    public override string Description =>
        Connection.Identity is { } identity
            ? $"OnStepX environmental sensors, firmware {identity.FirmwareVersion}"
            : "OnStepX environmental sensors";

    /// <inheritdoc />
    public override short InterfaceVersion => 2;

    private WeatherSnapshot Snapshot
    {
        get
        {
            RequireConnected();

            return _poller.GetFresh()
                ?? throw new NotConnectedException("No sensor reading has been taken yet.");
        }
    }

    /// <inheritdoc />
    protected override async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        await ProbeSensorsAsync(cancellationToken).ConfigureAwait(false);

        if (!_hasTemperature && !_hasPressure && !_hasHumidity)
        {
            throw new NotConnectedException(
                "This OnStepX build reports no environmental sensors at all. Enable a " +
                "weather sensor in the firmware configuration, or do not connect this device.");
        }

        await _poller.StartAsync(cancellationToken).ConfigureAwait(false);

        Logger.LogInformation(
            "Observing conditions connected. Sensors present: temperature {T}, pressure {P}, humidity {H}",
            _hasTemperature, _hasPressure, _hasHumidity);
    }

    /// <summary>
    /// Works out which sensors this firmware actually has.
    /// </summary>
    /// <remarks>
    /// Done once, at connect. A sensor cannot appear or disappear while the controller is
    /// running, and probing on every read would triple the traffic for nothing.
    /// </remarks>
    private async Task ProbeSensorsAsync(CancellationToken cancellationToken)
    {
        _hasTemperature = await ProbeAsync("GX9A", cancellationToken).ConfigureAwait(false);
        _hasPressure = await ProbeAsync("GX9B", cancellationToken).ConfigureAwait(false);
        _hasHumidity = await ProbeAsync("GX9C", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks one sensor whether it exists.
    /// </summary>
    /// <remarks>
    /// A bare <c>0</c> reply means "not fitted". It is distinguishable from a genuine
    /// reading of zero because the firmware formats real values with a decimal point,
    /// so a present sensor at freezing answers <c>0.0</c> and an absent one answers
    /// <c>0</c>.
    /// </remarks>
    private async Task<bool> ProbeAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            string reply = await Channel.GetStringAsync(command, cancellationToken)
                .ConfigureAwait(false);

            return reply.Trim() is not ("0" or "" or "1");
        }
        catch (OnStepProtocolException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task OnDisconnectingAsync() =>
        await _poller.StopAsync().ConfigureAwait(false);

    private async Task<WeatherSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        double? temperature = _hasTemperature
            ? await ReadAsync("GX9A", cancellationToken).ConfigureAwait(false)
            : null;

        double? pressure = _hasPressure
            ? await ReadAsync("GX9B", cancellationToken).ConfigureAwait(false)
            : null;

        double? humidity = _hasHumidity
            ? await ReadAsync("GX9C", cancellationToken).ConfigureAwait(false)
            : null;

        // The dew point needs both temperature and humidity, so it exists only when they
        // both do.
        double? dewPoint = _hasTemperature && _hasHumidity
            ? await ReadAsync("GX9E", cancellationToken).ConfigureAwait(false)
            : null;

        if (Settings.ObservingConditions.PushWeatherToController)
        {
            await PushWeatherAsync(cancellationToken).ConfigureAwait(false);
        }

        return new WeatherSnapshot
        {
            Temperature = temperature,
            Pressure = pressure,
            Humidity = humidity,
            DewPoint = dewPoint,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private async Task<double?> ReadAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            return await Channel.GetDoubleAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OnStepProtocolException)
        {
            // A sensor that answered at connect and now does not is a fault, not a
            // measurement, so it is reported as unavailable rather than as a number.
            return null;
        }
    }

    /// <summary>
    /// Optional extra: pushes readings from an external station into the controller.
    /// </summary>
    /// <remarks>
    /// OnStep uses ambient temperature and pressure for its own refraction model, so
    /// feeding it better numbers than its onboard sensor improves pointing. Off by
    /// default because it writes to the mount, and nobody expects a weather driver to do
    /// that.
    /// </remarks>
    private async Task PushWeatherAsync(CancellationToken cancellationToken)
    {
        // Nothing to push yet: this is the hook an external station will use once the
        // setup UI can supply the values. Kept here so the setting has one obvious home.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Ambient temperature in degrees Celsius.</summary>
    public double Temperature =>
        Snapshot.Temperature ?? throw NotFitted(nameof(Temperature));

    /// <summary>Barometric pressure in hectopascals.</summary>
    /// <remarks>
    /// OnStep reports millibars, which are the same unit as the hectopascals ASCOM asks
    /// for, so no conversion is needed. Note ASCOM wants the pressure <b>at the
    /// observing site</b>, not reduced to sea level.
    /// </remarks>
    public double Pressure =>
        Snapshot.Pressure ?? throw NotFitted(nameof(Pressure));

    /// <summary>Relative humidity in percent.</summary>
    public double Humidity =>
        Snapshot.Humidity ?? throw NotFitted(nameof(Humidity));

    /// <summary>Dew point in degrees Celsius.</summary>
    public double DewPoint =>
        Snapshot.DewPoint ?? throw NotFitted(nameof(DewPoint));

    // OnStep has no sensor for any of the following. Each one throws rather than
    // returning zero, because zero is a plausible reading for most of them and a client
    // cannot tell an invented value from a real one.

    /// <summary>Not available: OnStep has no cloud sensor.</summary>
    public double CloudCover => throw NotFitted(nameof(CloudCover));

    /// <summary>Not available: OnStep has no rain sensor.</summary>
    public double RainRate => throw NotFitted(nameof(RainRate));

    /// <summary>Not available: OnStep has no sky brightness sensor.</summary>
    public double SkyBrightness => throw NotFitted(nameof(SkyBrightness));

    /// <summary>Not available: OnStep has no sky quality meter.</summary>
    public double SkyQuality => throw NotFitted(nameof(SkyQuality));

    /// <summary>Not available: OnStep has no sky temperature sensor.</summary>
    public double SkyTemperature => throw NotFitted(nameof(SkyTemperature));

    /// <summary>Not available: OnStep does not measure seeing.</summary>
    public double StarFWHM => throw NotFitted(nameof(StarFWHM));

    /// <summary>Not available: OnStep has no anemometer.</summary>
    public double WindDirection => throw NotFitted(nameof(WindDirection));

    /// <summary>Not available: OnStep has no anemometer.</summary>
    public double WindGust => throw NotFitted(nameof(WindGust));

    /// <summary>Not available: OnStep has no anemometer.</summary>
    public double WindSpeed => throw NotFitted(nameof(WindSpeed));

    /// <summary>
    /// Averaging period in hours.
    /// </summary>
    /// <remarks>
    /// The driver reports instantaneous readings, so the only value it can honestly
    /// accept is zero. ASCOM allows exactly this, and requires anything else to be
    /// refused rather than silently ignored.
    /// </remarks>
    public double AveragePeriod
    {
        get => 0.0;

        set
        {
            if (value != 0.0)
            {
                throw new InvalidValueException(
                    "This driver reports instantaneous readings, so the only supported " +
                    "averaging period is 0.");
            }
        }
    }

    /// <summary>
    /// How long ago a reading was taken, in seconds.
    /// </summary>
    /// <remarks>
    /// An empty property name asks about the most recently updated sensor, which here is
    /// all of them at once because one poll reads the lot.
    /// </remarks>
    public double TimeSinceLastUpdate(string propertyName)
    {
        // An unsupported sensor has no update time either, and saying so is more useful
        // than reporting an age for a reading that does not exist.
        if (!string.IsNullOrWhiteSpace(propertyName) && !IsSupported(propertyName))
        {
            throw NotFitted(propertyName);
        }

        return (DateTimeOffset.UtcNow - Snapshot.Timestamp).TotalSeconds;
    }

    /// <summary>Describes one sensor.</summary>
    public string SensorDescription(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        if (!IsSupported(propertyName))
        {
            throw NotFitted(propertyName);
        }

        return propertyName switch
        {
            nameof(Temperature) => "Ambient temperature from the OnStepX weather sensor",
            nameof(Pressure) => "Barometric pressure at the site from the OnStepX weather sensor",
            nameof(Humidity) => "Relative humidity from the OnStepX weather sensor",
            nameof(DewPoint) => "Dew point derived by the controller from temperature and humidity",
            _ => "Not available on OnStepX",
        };
    }

    /// <summary>Forces a fresh reading.</summary>
    public void Refresh() =>
        RunSync(() => _poller.RefreshAsync(CancellationToken.None));

    /// <inheritdoc />
    public override List<StateValue> DeviceState
    {
        get
        {
            WeatherSnapshot? snapshot = _poller.Current;

            if (snapshot is null)
            {
                return [];
            }

            var state = new List<StateValue>();

            // Only sensors that exist appear, which is what lets a client distinguish
            // "not fitted" from "reading zero".
            if (snapshot.Temperature is double temperature)
            {
                state.Add(new StateValue(nameof(Temperature), temperature));
            }

            if (snapshot.Pressure is double pressure)
            {
                state.Add(new StateValue(nameof(Pressure), pressure));
            }

            if (snapshot.Humidity is double humidity)
            {
                state.Add(new StateValue(nameof(Humidity), humidity));
            }

            if (snapshot.DewPoint is double dewPoint)
            {
                state.Add(new StateValue(nameof(DewPoint), dewPoint));
            }

            state.Add(new StateValue("TimeStamp", snapshot.Timestamp.UtcDateTime));

            return state;
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _poller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }

    private bool IsSupported(string propertyName) => propertyName switch
    {
        nameof(Temperature) => _hasTemperature,
        nameof(Pressure) => _hasPressure,
        nameof(Humidity) => _hasHumidity,
        nameof(DewPoint) => _hasTemperature && _hasHumidity,
        _ => false,
    };

    private static PropertyNotImplementedException NotFitted(string propertyName) =>
        new($"OnStepX does not provide {propertyName}.");
}
