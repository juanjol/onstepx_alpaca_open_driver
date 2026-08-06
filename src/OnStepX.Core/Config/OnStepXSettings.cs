namespace OnStepX.Core.Config;

/// <summary>Transport type toward the controller.</summary>
public enum TransportKind
{
    /// <summary>Serial port, with or without autodiscovery.</summary>
    Serial,

    /// <summary>TCP, for the WiFi addon.</summary>
    Tcp,

    /// <summary>
    /// Simulated controller, with no hardware. This is what allows ConformU
    /// to pass on any platform and with no mount connected.
    /// </summary>
    Simulated,
}

/// <summary>Connection settings.</summary>
public sealed record ConnectionSettings
{
    /// <summary>Transport to use.</summary>
    public TransportKind Kind { get; set; } = TransportKind.Serial;

    /// <summary>Serial port. If empty, autodiscovery is required.</summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>Serial port baud rate.</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>
    /// Searches for the port automatically when connecting. If
    /// <see cref="PortName"/> is empty, this applies regardless of whether
    /// it is disabled, because there is no alternative.
    /// </summary>
    public bool AutoDiscoverPort { get; set; } = true;

    /// <summary>TCP host name or address.</summary>
    public string Host { get; set; } = "192.168.0.1";

    /// <summary>TCP port.</summary>
    public int TcpPort { get; set; } = 9999;

    /// <summary>
    /// Deadline for each command, in milliseconds. Equivalent to the
    /// "Timeout" slider of the old forms.
    /// </summary>
    public int TimeoutMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Framing with checksum. This is the "Use Error Correction" checkbox
    /// of the old forms.
    /// </summary>
    public bool UseErrorCorrection { get; set; } = true;

    /// <summary>Retries on a failed command.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Status polling period, in milliseconds.
    /// </summary>
    /// <remarks>
    /// 250 ms is about the practical floor for a serial link. One poll cycle is seven
    /// commands, which at 9600 baud already costs roughly 110 ms, so polling much
    /// faster than this would saturate the port and leave no room for the commands a
    /// client actually wants to send.
    /// <para>
    /// It also bounds how stale a coordinate read can be, which matters more than it
    /// sounds: a client that measures a tracking rate by sampling the position twice
    /// sees this interval as measurement error.
    /// </para>
    /// </remarks>
    public int PollIntervalMilliseconds { get; set; } = 250;
}

/// <summary>Mount settings.</summary>
public sealed record TelescopeSettings
{
    /// <summary>
    /// Sends date and time when connecting. This is "Set Date/Time on
    /// Connect" of the old form.
    /// </summary>
    public bool SetDateTimeOnConnect { get; set; }

    // There is deliberately no stored site here, and no flag to push one on connect.
    //
    // The controller keeps its own site in non volatile memory and needs it to slew correctly
    // whether or not a driver is attached, so a second copy in the driver would only raise the
    // question of which one is right. The setup UI reads and writes the controller's copy
    // directly.

    /// <summary>Telescope aperture, in meters.</summary>
    public double ApertureDiameter { get; set; }

    /// <summary>Aperture area, in square meters.</summary>
    public double ApertureArea { get; set; }

    /// <summary>Focal length, in meters.</summary>
    public double FocalLength { get; set; }

    // There is no setting to slew below the horizon limit either. The firmware enforces that
    // limit itself and rejects the goto, so a driver level flag could only lie about it. The
    // limit is editable on the mount setup page, where it belongs.
}

/// <summary>Focuser settings.</summary>
public sealed record FocuserSettings
{
    /// <summary>
    /// Focuser to select when connecting, from 1 to 6. Equivalent to
    /// "Focuser selection" of the old form.
    /// </summary>
    public int FocuserNumber { get; set; } = 1;

    /// <summary>Moves to a specific position when connecting.</summary>
    /// <remarks>
    /// New feature. Disabled by default on purpose: a device that moves
    /// right after connecting surprises some clients and can interfere
    /// with conformance checks.
    /// </remarks>
    public bool MoveToPositionOnConnect { get; set; }

    /// <summary>Target position when connecting, in <b>steps</b>.</summary>
    public int PositionOnConnect { get; set; }
}

/// <summary>Rotator settings.</summary>
public sealed record RotatorSettings
{
    /// <summary>Moves to a specific angle when connecting.</summary>
    public bool MoveToPositionOnConnect { get; set; }

    /// <summary>Target mechanical angle when connecting, in degrees.</summary>
    public double PositionOnConnect { get; set; }

    /// <summary>Reverses the rotation direction seen by the client.</summary>
    public bool Reverse { get; set; }

    /// <summary>
    /// Offset between the mechanical angle and the sky angle, in degrees.
    /// It is set by the client's sync operation.
    /// </summary>
    public double SyncOffset { get; set; }
}

/// <summary>Environmental sensor settings.</summary>
public sealed record ObservingConditionsSettings
{
    /// <summary>Averaging period the driver reports, in hours.</summary>
    public double AveragePeriod { get; set; }

    /// <summary>
    /// Pushes data from an external weather station to OnStepX with
    /// <c>:SX9A#</c> and friends. This is an extra, not a requirement.
    /// </summary>
    public bool PushWeatherToController { get; set; }
}

/// <summary>Alpaca server settings.</summary>
public sealed record ServerSettings
{
    /// <summary>HTTP port. The Alpaca standard is 11111.</summary>
    public int Port { get; set; } = 11111;

    /// <summary>Accepts connections from outside this machine.</summary>
    public bool AllowRemoteAccess { get; set; } = true;

    /// <summary>Responds to Alpaca discovery over UDP.</summary>
    public bool AllowDiscovery { get; set; } = true;

    /// <summary>Publishes the Swagger interface, useful for debugging.</summary>
    public bool RunSwagger { get; set; } = true;

    /// <summary>Requires authentication.</summary>
    public bool UseAuthentication { get; set; }

    /// <summary>Username, if authentication is used.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Password, if authentication is used.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Site location description, for discovery.</summary>
    public string Location { get; set; } = "Observatory";

    /// <summary>
    /// Alpaca strict mode. When enabled, the server rejects whatever the
    /// specification does not allow, which helps when passing ConformU.
    /// </summary>
    public bool StrictAlpacaMode { get; set; }

    /// <summary>
    /// Stops a remote client from disconnecting a device another client is using.
    /// </summary>
    /// <remarks>
    /// <b>Off by default, deliberately.</b> When this is on, the Alpaca REST layer
    /// swallows a <c>Connected = false</c> and never passes it to the device, so a
    /// following read still reports connected. That breaks the ASCOM contract and
    /// Conform rejects it outright.
    /// <para>
    /// It is also unnecessary here: the shared connection already reference counts
    /// devices, so the serial port stays open exactly as long as somebody is using
    /// it. Protection at the REST layer would be a second, conflicting mechanism.
    /// </para>
    /// </remarks>
    public bool PreventRemoteDisconnects { get; set; }
}

/// <summary>
/// Complete driver configuration, and the unit of export and import.
/// </summary>
/// <remarks>
/// Serialized to JSON as is. This is what covers the goal of having a
/// configuration that is portable between installations.
/// </remarks>
public sealed record OnStepXSettings
{
    /// <summary>Format version, for future migrations.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Connection.</summary>
    public ConnectionSettings Connection { get; set; } = new();

    /// <summary>Mount.</summary>
    public TelescopeSettings Telescope { get; set; } = new();

    /// <summary>Focuser.</summary>
    public FocuserSettings Focuser { get; set; } = new();

    /// <summary>Rotator.</summary>
    public RotatorSettings Rotator { get; set; } = new();

    /// <summary>Environmental sensors.</summary>
    public ObservingConditionsSettings ObservingConditions { get; set; } = new();

    /// <summary>Server.</summary>
    public ServerSettings Server { get; set; } = new();

    /// <summary>Detailed command trace. This is "Trace on" of the old form.</summary>
    public bool TraceEnabled { get; set; }
}
