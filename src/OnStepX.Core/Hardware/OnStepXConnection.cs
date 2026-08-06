using Microsoft.Extensions.Logging;
using OnStepX.Core.Config;
using OnStepX.Core.Discovery;
using OnStepX.Core.Protocol;
using OnStepX.Core.Simulation;
using OnStepX.Core.Transport;

namespace OnStepX.Core.Hardware;

/// <summary>State of the shared connection.</summary>
public enum ConnectionState
{
    /// <summary>Disconnected.</summary>
    Disconnected,

    /// <summary>Connecting, including autodiscovery.</summary>
    Connecting,

    /// <summary>Connected and operational.</summary>
    Connected,

    /// <summary>Failed to connect. See <see cref="OnStepXConnection.LastError"/>.</summary>
    Failed,
}

/// <summary>Controller data after connecting.</summary>
public sealed record ControllerIdentity
{
    /// <summary>Product name, from <c>:GVP#</c>.</summary>
    public required string ProductName { get; init; }

    /// <summary>Version, from <c>:GVN#</c>.</summary>
    public required string FirmwareVersion { get; init; }

    /// <summary>Name and version together, from <c>:GVM#</c>.</summary>
    public string? FullName { get; init; }

    /// <summary>Build date, from <c>:GVD#</c>.</summary>
    public string? BuildDate { get; init; }

    /// <summary>Configuration description, from <c>:GVC#</c>.</summary>
    public string? Configuration { get; init; }

    /// <summary>Hardware or pinmap string, from <c>:GVH#</c>.</summary>
    public string? Hardware { get; init; }

    /// <summary>Description of the transport it was reached through.</summary>
    public required string TransportDescription { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        FullName ?? $"{ProductName} {FirmwareVersion}";
}

/// <summary>
/// Single shared connection to the OnStepX controller.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the piece that replaces the external hub.</b> The five ASCOM
/// devices request the connection here, and only the first one opens the
/// transport and only the last one closes it. Underneath,
/// <see cref="OnStepChannel"/> serializes commands, so mount, focuser,
/// rotator and sensors can be connected at the same time over a single
/// serial port without stepping on each other.
/// </para>
/// <para>
/// The count is per device and not per call: a client that calls
/// <c>Connected = true</c> twice must not require two calls to disconnect,
/// because many clients are not symmetric and the port would stay open
/// forever.
/// </para>
/// </remarks>
public sealed class OnStepXConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _connectedDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;
    private readonly Func<OnStepXSettings> _settingsProvider;
    private readonly Func<ITransport>? _transportOverride;

    private OnStepChannel? _channel;
    private bool _disposed;

    /// <summary>Creates the connection manager.</summary>
    /// <param name="settingsProvider">
    /// Provides the settings in effect at any given time. It is a function
    /// and not a value so that a configuration change applies to the next
    /// connection without needing to rebuild anything.
    /// </param>
    /// <param name="transportOverride">
    /// Fixed transport, for tests. If given, the transport configuration is
    /// ignored.
    /// </param>
    /// <param name="logger">Logger.</param>
    public OnStepXConnection(
        Func<OnStepXSettings> settingsProvider,
        Func<ITransport>? transportOverride = null,
        ILogger<OnStepXConnection>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settingsProvider);

        _settingsProvider = settingsProvider;
        _transportOverride = transportOverride;
        _logger = logger
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OnStepXConnection>.Instance;
    }

    /// <summary>Current state.</summary>
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>Last error, if <see cref="State"/> is <see cref="ConnectionState.Failed"/>.</summary>
    public string? LastError { get; private set; }

    /// <summary>Controller data, available once connected.</summary>
    public ControllerIdentity? Identity { get; private set; }

    /// <summary>Devices currently connected.</summary>
    public IReadOnlyCollection<string> ConnectedDevices
    {
        get
        {
            lock (_connectedDevices)
            {
                return _connectedDevices.ToArray();
            }
        }
    }

    /// <summary>
    /// Command channel. Throws if there is no connection, instead of
    /// returning null, so the failure surfaces at the point of use with a
    /// clear message.
    /// </summary>
    public OnStepChannel Channel =>
        _channel ?? throw new InvalidOperationException(
            "No connection to the OnStepX controller.");

    /// <summary>Whether there is a connection.</summary>
    public bool IsConnected => _channel is not null && State == ConnectionState.Connected;

    /// <summary>
    /// Connects on behalf of a device. The first call opens the transport,
    /// subsequent ones just register.
    /// </summary>
    /// <param name="deviceKey">
    /// Device identifier, for example <c>Telescope</c>. Repeating it is
    /// idempotent.
    /// </param>
    public async Task ConnectAsync(string deviceKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_connectedDevices)
            {
                _connectedDevices.Add(deviceKey);
            }

            if (_channel is not null)
            {
                _logger.LogDebug(
                    "{Device} joins the already open connection with {Transport}",
                    deviceKey, _channel.Transport.Description);
                return;
            }

            State = ConnectionState.Connecting;
            LastError = null;

            try
            {
                _channel = await OpenChannelAsync(cancellationToken).ConfigureAwait(false);
                Identity = await ReadIdentityAsync(_channel, cancellationToken).ConfigureAwait(false);

                State = ConnectionState.Connected;

                _logger.LogInformation(
                    "Connected to {Identity} via {Transport}, first device {Device}",
                    Identity, Identity.TransportDescription, deviceKey);
            }
            catch (Exception ex)
            {
                // If opening fails the state must be left clean, or the
                // next attempt would believe a connection already exists.
                lock (_connectedDevices)
                {
                    _connectedDevices.Remove(deviceKey);
                }

                if (_channel is not null)
                {
                    await _channel.DisposeAsync().ConfigureAwait(false);
                    _channel = null;
                }

                State = ConnectionState.Failed;
                LastError = ex.Message;
                Identity = null;

                _logger.LogError(ex, "Failed to connect for {Device}", deviceKey);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Disconnects a device. Only closes the transport when none remain.
    /// </summary>
    public async Task DisconnectAsync(string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool empty;
            lock (_connectedDevices)
            {
                _connectedDevices.Remove(deviceKey);
                empty = _connectedDevices.Count == 0;
            }

            if (!empty)
            {
                _logger.LogDebug(
                    "{Device} disconnects, {Count} devices still using the port",
                    deviceKey, ConnectedDevices.Count);
                return;
            }

            if (_channel is not null)
            {
                _logger.LogInformation(
                    "{Device} was the last one, closing {Transport}",
                    deviceKey, _channel.Transport.Description);

                await _channel.DisposeAsync().ConfigureAwait(false);
                _channel = null;
            }

            State = ConnectionState.Disconnected;
            Identity = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Indicates whether a specific device is connected.</summary>
    public bool IsDeviceConnected(string deviceKey)
    {
        lock (_connectedDevices)
        {
            return _connectedDevices.Contains(deviceKey);
        }
    }

    private async Task<OnStepChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        OnStepXSettings settings = _settingsProvider();
        ConnectionSettings connection = settings.Connection;

        var channelOptions = new OnStepChannelOptions
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(100, connection.TimeoutMilliseconds)),
            UseErrorCorrection = connection.UseErrorCorrection,
            MaxRetries = Math.Max(0, connection.MaxRetries),
        };

        ITransport transport = await CreateTransportAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (!transport.IsOpen)
            {
                await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new OnStepChannel(transport, channelOptions);
    }

    private async Task<ITransport> CreateTransportAsync(
        ConnectionSettings connection,
        CancellationToken cancellationToken)
    {
        if (_transportOverride is not null)
        {
            return _transportOverride();
        }

        switch (connection.Kind)
        {
            case TransportKind.Simulated:
                return new FakeOnStepDevice();

            case TransportKind.Tcp:
                return new TcpTransport(connection.Host, connection.TcpPort);

            case TransportKind.Serial:
                {
                    // Autodiscovery runs if it was requested, or if there is
                    // no configured port, because then there is no alternative.
                    bool needsDiscovery = connection.AutoDiscoverPort
                        || string.IsNullOrWhiteSpace(connection.PortName);

                    if (!needsDiscovery)
                    {
                        return new SerialTransport(connection.PortName, connection.BaudRate);
                    }

                    DiscoveredController? found = await DiscoverAsync(connection, cancellationToken)
                        .ConfigureAwait(false);

                    if (found is null)
                    {
                        throw new OnStepProtocolException(
                            "Autodiscovery did not find any OnStepX controller. " +
                            "Check that it is powered on and connected, or set the port manually.");
                    }

                    _logger.LogInformation("Autodiscovery: {Found}", found);

                    return new SerialTransport(found.PortName, found.BaudRate);
                }

            default:
                throw new InvalidOperationException(
                    $"Unsupported transport type: {connection.Kind}");
        }
    }

    private async Task<DiscoveredController?> DiscoverAsync(
        ConnectionSettings connection,
        CancellationToken cancellationToken)
    {
        var discovery = new PortDiscovery(logger: _logger);

        var options = new PortDiscoveryOptions
        {
            PreferredBaudRate = connection.BaudRate,
            ProbeTimeout = TimeSpan.FromMilliseconds(
                Math.Clamp(connection.TimeoutMilliseconds, 200, 2000)),

            // When connecting, only the first responder matters, not the
            // full list.
            StopAtFirstMatch = true,
            UseErrorCorrection = false,
        };

        // If a port is configured it is tried before anything else: this is
        // the normal case and saves scanning everything.
        if (!string.IsNullOrWhiteSpace(connection.PortName))
        {
            DiscoveredController? direct = await discovery
                .ProbeAsync(connection.PortName, connection.BaudRate, options, cancellationToken)
                .ConfigureAwait(false);

            if (direct is not null)
            {
                return direct;
            }

            _logger.LogInformation(
                "Configured port {Port} did not respond, searching the others",
                connection.PortName);
        }

        IReadOnlyList<DiscoveredController> results = await discovery
            .DiscoverAsync(options, progress: null, cancellationToken)
            .ConfigureAwait(false);

        return results.Count > 0 ? results[0] : null;
    }

    private static async Task<ControllerIdentity> ReadIdentityAsync(
        OnStepChannel channel,
        CancellationToken cancellationToken)
    {
        string product = await channel.GetStringAsync("GVP", cancellationToken).ConfigureAwait(false);
        string version = await channel.GetStringAsync("GVN", cancellationToken).ConfigureAwait(false);

        return new ControllerIdentity
        {
            ProductName = product,
            FirmwareVersion = version,
            FullName = await TryGetAsync(channel, "GVM", cancellationToken).ConfigureAwait(false),
            BuildDate = await TryGetAsync(channel, "GVD", cancellationToken).ConfigureAwait(false),
            Configuration = await TryGetAsync(channel, "GVC", cancellationToken).ConfigureAwait(false),
            Hardware = await TryGetAsync(channel, "GVH", cancellationToken).ConfigureAwait(false),
            TransportDescription = channel.Transport.Description,
        };
    }

    /// <summary>
    /// Reads an optional command. The firmware description ones do not
    /// exist in every build, and their absence must not prevent connecting.
    /// </summary>
    private static async Task<string?> TryGetAsync(
        OnStepChannel channel,
        string payload,
        CancellationToken cancellationToken)
    {
        try
        {
            string value = await channel.GetStringAsync(payload, cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(value) || value == "0" ? null : value;
        }
        catch (OnStepProtocolException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
        }

        lock (_connectedDevices)
        {
            _connectedDevices.Clear();
        }

        State = ConnectionState.Disconnected;
        _gate.Dispose();
    }
}
