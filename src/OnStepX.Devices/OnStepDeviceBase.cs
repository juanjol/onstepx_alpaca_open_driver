using ASCOM;
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using OnStepX.Core.Config;
using OnStepX.Core.Hardware;
using OnStepX.Core.Protocol;

namespace OnStepX.Devices;

/// <summary>
/// Shared plumbing for the five ASCOM devices: connection lifecycle, the
/// synchronous over asynchronous bridge, and translation of protocol failures into
/// ASCOM exceptions.
/// </summary>
/// <remarks>
/// <para>
/// All five devices share one <see cref="OnStepXConnection"/>, so connecting here
/// only registers this device with it. The transport is opened by whichever device
/// connects first and closed by whichever disconnects last.
/// </para>
/// <para>
/// ASCOM's interface is synchronous while everything underneath is asynchronous, so
/// commands are bridged with <see cref="RunSync"/>. This is safe because the whole
/// core uses <c>ConfigureAwait(false)</c>, so nothing tries to resume on a captured
/// context and there is no deadlock to hit. Property reads avoid the bridge
/// entirely by serving a cached snapshot.
/// </para>
/// </remarks>
/// <remarks>
/// <see cref="IDisposable"/> is declared explicitly. ASCOM's own interface carries a
/// <c>Dispose</c> member without deriving from <see cref="IDisposable"/>, so without
/// this the type would have the method but could not be used in a <c>using</c>
/// statement, and callers would leak the device registration in the shared connection.
/// </remarks>
public abstract class OnStepDeviceBase : IAscomDeviceV2, IDisposable
{
    private readonly object _connectingGate = new();
    private Task? _connectTask;

    /// <summary>Creates the device.</summary>
    protected OnStepDeviceBase(
        OnStepXConnection connection,
        Func<OnStepXSettings> settingsProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settingsProvider);
        ArgumentNullException.ThrowIfNull(logger);

        Connection = connection;
        SettingsProvider = settingsProvider;
        Logger = logger;
    }

    /// <summary>Shared connection to the controller.</summary>
    protected OnStepXConnection Connection { get; }

    /// <summary>Current settings.</summary>
    protected Func<OnStepXSettings> SettingsProvider { get; }

    /// <summary>Settings snapshot, for convenience.</summary>
    protected OnStepXSettings Settings => SettingsProvider();

    /// <summary>Logger.</summary>
    protected ILogger Logger { get; }

    /// <summary>Command channel. Throws <see cref="NotConnectedException"/> if closed.</summary>
    protected OnStepChannel Channel
    {
        get
        {
            if (!Connection.IsConnected)
            {
                throw new NotConnectedException($"{DeviceKey} is not connected.");
            }

            return Connection.Channel;
        }
    }

    /// <summary>
    /// Key identifying this device in the shared connection, for example
    /// <c>Telescope</c>.
    /// </summary>
    protected abstract string DeviceKey { get; }

    /// <summary>
    /// Marks this device's cached snapshot as stale.
    /// </summary>
    /// <remarks>
    /// The setup UI writes to the controller through its own path, and the polling loop
    /// has no way of knowing that happened. Without this, a client would keep reading the
    /// old tracking mode or park state for up to a poll interval after the user changed
    /// it, and the change would look as if it had been ignored.
    /// </remarks>
    public virtual void InvalidateSnapshot()
    {
    }

    /// <summary>
    /// Throws unless this device is connected.
    /// </summary>
    /// <remarks>
    /// Every property has to check. It is tempting to rely on the command channel
    /// throwing when it is closed, but a device whose reads are all served from a cache
    /// would answer happily while disconnected: the observing conditions device with no
    /// sensors probed reads nothing at all, so nothing fails, and it would report
    /// "sensor not fitted" when the truth is "not connected". Two very different
    /// problems for whoever is debugging.
    /// </remarks>
    protected void RequireConnected()
    {
        if (!Connected)
        {
            throw new NotConnectedException($"{DeviceKey} is not connected.");
        }
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract short InterfaceVersion { get; }

    /// <inheritdoc />
    public string DriverInfo =>
        $"OnStepX ASCOM Alpaca driver. Controller: " +
        (Connection.Identity?.ToString() ?? "not connected");

    /// <inheritdoc />
    public string DriverVersion =>
        typeof(OnStepDeviceBase).Assembly.GetName().Version?.ToString(2) ?? "0.1";

    /// <inheritdoc />
    public virtual IList<string> SupportedActions => [];

    /// <inheritdoc />
    public bool Connected
    {
        get => Connection.IsDeviceConnected(DeviceKey) && Connection.IsConnected;

        set
        {
            if (value)
            {
                RunSync(() => ConnectCoreAsync(CancellationToken.None));
            }
            else
            {
                RunSync(() => DisconnectCoreAsync());
            }
        }
    }

    /// <summary>
    /// Platform 7 asynchronous connect. Returns immediately; completion is signalled
    /// by <see cref="Connecting"/> going false.
    /// </summary>
    /// <remarks>
    /// This is the path that makes port auto discovery usable: a sweep across
    /// several ports and baud rates can take seconds, and a blocking connect would
    /// freeze the client's user interface for all of it.
    /// </remarks>
    public void Connect()
    {
        lock (_connectingGate)
        {
            if (_connectTask is { IsCompleted: false })
            {
                return;
            }

            _connectTask = Task.Run(() => ConnectCoreAsync(CancellationToken.None));
        }
    }

    /// <summary>Platform 7 asynchronous disconnect.</summary>
    public void Disconnect()
    {
        lock (_connectingGate)
        {
            if (_connectTask is { IsCompleted: false })
            {
                return;
            }

            _connectTask = Task.Run(DisconnectCoreAsync);
        }
    }

    /// <inheritdoc />
    public bool Connecting
    {
        get
        {
            lock (_connectingGate)
            {
                return _connectTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Platform 7 bulk state read: every operational value in one call.
    /// </summary>
    /// <remarks>
    /// This is what removes any concern about Alpaca's per property HTTP cost. A
    /// modern client fetches the whole state in a single request, and because it is
    /// served from the polling cache it is also internally consistent, unlike
    /// reading twenty properties one at a time.
    /// </remarks>
    public abstract List<StateValue> DeviceState { get; }

    /// <summary>Connects and runs whatever this device needs on connect.</summary>
    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (Connected)
        {
            return;
        }

        try
        {
            await Connection.ConnectAsync(DeviceKey, cancellationToken).ConfigureAwait(false);
            await OnConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never leave the device half connected: release the shared connection
            // so the port is not pinned open by a device that failed to start.
            await Connection.DisconnectAsync(DeviceKey).ConfigureAwait(false);

            throw Translate(ex, $"Could not connect {DeviceKey}");
        }
    }

    private async Task DisconnectCoreAsync()
    {
        if (!Connection.IsDeviceConnected(DeviceKey))
        {
            return;
        }

        try
        {
            await OnDisconnectingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failure while tidying up must not prevent the port being released.
            Logger.LogWarning(ex, "{Device} failed during disconnect cleanup", DeviceKey);
        }

        await Connection.DisconnectAsync(DeviceKey).ConfigureAwait(false);
    }

    /// <summary>Hook for work needed once the connection is up.</summary>
    protected virtual Task OnConnectedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Hook for work needed before releasing the connection.</summary>
    protected virtual Task OnDisconnectingAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public virtual string Action(string actionName, string actionParameters) =>
        throw new ActionNotImplementedException(
            $"This driver implements no custom action named '{actionName}'.");

    /// <summary>
    /// Sends a raw command with no reply.
    /// </summary>
    /// <remarks>
    /// <paramref name="raw"/> selects whether the text is sent verbatim or wrapped
    /// in the usual frame. Passing the payload without delimiters and letting the
    /// channel frame it is the safer option, because the channel also applies the
    /// checksum when error correction is on.
    /// </remarks>
    public void CommandBlind(string command, bool raw = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        RunSync(() => Channel.SendAsync(Strip(command, raw), CancellationToken.None));
    }

    /// <inheritdoc />
    public bool CommandBool(string command, bool raw = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return RunSync(() => Channel.GetBoolAsync(Strip(command, raw), CancellationToken.None));
    }

    /// <inheritdoc />
    public string CommandString(string command, bool raw = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return RunSync(() => Channel.GetStringAsync(Strip(command, raw), CancellationToken.None));
    }

    /// <summary>
    /// Removes the frame delimiters a caller may have included, since the channel
    /// adds them itself.
    /// </summary>
    private static string Strip(string command, bool raw)
    {
        if (raw)
        {
            return command;
        }

        string trimmed = command.Trim();

        if (trimmed.StartsWith(':') || trimmed.StartsWith(';'))
        {
            trimmed = trimmed[1..];
        }

        return trimmed.TrimEnd('#');
    }

    /// <inheritdoc />
    public virtual void Dispose()
    {
        // The shared connection outlives individual devices, so the device only
        // releases its own registration and never disposes the connection itself.
        try
        {
            if (Connection.IsDeviceConnected(DeviceKey))
            {
                RunSync(() => Connection.DisconnectAsync(DeviceKey));
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Device} failed while disposing", DeviceKey);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Runs an asynchronous action from a synchronous ASCOM member.</summary>
    protected T RunSync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return operation().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw Translate(ex, null);
        }
    }

    /// <summary>Runs an asynchronous action from a synchronous ASCOM member.</summary>
    protected void RunSync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            operation().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw Translate(ex, null);
        }
    }

    /// <summary>
    /// Maps a protocol failure onto the ASCOM exception a client expects.
    /// </summary>
    /// <remarks>
    /// This matters more than it looks: clients react to exception type, not to
    /// message text. Reporting a parked mount as a generic driver error means an
    /// imaging sequence logs "unknown failure" instead of unparking and carrying on.
    /// </remarks>
    protected static Exception Translate(Exception ex, string? context)
    {
        string prefix = context is null ? string.Empty : context + ": ";

        switch (ex)
        {
            // Already an ASCOM exception, pass it through untouched.
            case ASCOM.NotConnectedException:
            case ASCOM.InvalidValueException:
            case ASCOM.InvalidOperationException:
            case ASCOM.ParkedException:
            case ASCOM.PropertyNotImplementedException:
            case ASCOM.MethodNotImplementedException:
            case ASCOM.ActionNotImplementedException:
            case ASCOM.NotImplementedException:
            case ASCOM.ValueNotSetException:
            case ASCOM.DriverException:
                return ex;

            case OnStepCommandException command:
                return command.Error switch
                {
                    CommandError.Parked or CommandError.SlewErrorInPark =>
                        new ParkedException(prefix + command.Error.Describe()),

                    CommandError.ParameterRange or CommandError.ParameterForm =>
                        new InvalidValueException(prefix + command.Error.Describe()),

                    CommandError.NotParked
                        or CommandError.NotParkedOrAtHome
                        or CommandError.NoParkPositionSet
                        or CommandError.SlewInSlew
                        or CommandError.SlewInMotion
                        or CommandError.SlewErrorInStandby =>
                        new ASCOM.InvalidOperationException(prefix + command.Error.Describe()),

                    CommandError.CommandUnknown =>
                        new ASCOM.NotImplementedException(
                            prefix + "This firmware build does not support that command."),

                    _ => new DriverException(prefix + command.Error.Describe()),
                };

            case OnStepProtocolException protocol:
                return new DriverException(prefix + protocol.Message, protocol);

            case TimeoutException timeout:
                return new DriverException(
                    prefix + "The controller did not answer in time: " + timeout.Message, timeout);

            case OperationCanceledException:
                return ex;

            default:
                return new DriverException(prefix + ex.Message, ex);
        }
    }
}
