using OnStepX.Core.Configuration;
using OnStepX.Core.Hardware;
using OnStepX.Core.Protocol;

namespace OnStepX.AlpacaServer;

/// <summary>
/// Access to the controller for the setup pages.
/// </summary>
/// <remarks>
/// <para>
/// Every operation borrows the shared connection under its own device key, does its work
/// and releases it again. The pages deliberately do not hold a connection open:
/// </para>
/// <list type="bullet">
/// <item>
/// A browser tab can vanish at any moment. A Blazor circuit that dies while holding the
/// port would keep the serial port open until the server was restarted, and the mount
/// would refuse every client in the meantime.
/// </item>
/// <item>
/// If a client is already connected, the reference counted connection means these
/// operations simply join it and releasing does not close anything. So a setup page never
/// disturbs an imaging session.
/// </item>
/// </list>
/// <para>
/// The cost is one connection attempt per operation on an otherwise idle server, which for
/// a page the user visits by hand is the right trade.
/// </para>
/// </remarks>
public sealed class SetupSession
{
    /// <summary>
    /// Prefix of the keys the setup UI connects under. Separate from the device keys so that
    /// releasing one can never close a port a real client is using.
    /// </summary>
    public const string DeviceKeyPrefix = "SetupUI";

    private static int _sequence;

    /// <summary>
    /// Key this session connects under.
    /// </summary>
    /// <remarks>
    /// One per session rather than one for the whole UI. The shared connection counts
    /// <b>keys</b>, so two browser tabs sharing a key would register as a single holder: the
    /// tab that finished first would release it, and the port would close underneath the other
    /// one in the middle of a command.
    /// </remarks>
    private readonly string _deviceKey =
        $"{DeviceKeyPrefix}:{Interlocked.Increment(ref _sequence)}";

    private readonly ControllerConfiguration _configuration = new(
        () => ServerRuntime.Connection.Channel,

        // A write from the UI is invisible to the polling loops, so their snapshots have to
        // be marked stale or a connected client would keep reading the old value.
        DeviceRegistration.InvalidateAllSnapshots);

    /// <summary>Configuration service bound to the shared connection.</summary>
    public ControllerConfiguration Configuration => _configuration;

    /// <summary>A client or the UI currently holds the connection.</summary>
    public static bool IsConnected => ServerRuntime.Connection.IsConnected;

    /// <summary>
    /// A real client is holding the connection, as opposed to the setup UI alone.
    /// </summary>
    /// <remarks>
    /// This is what gates the operations that would disturb somebody: port autodiscovery
    /// opens serial ports one by one, and one of them is the port already in use.
    /// </remarks>
    public static bool IsClientConnected =>
        ServerRuntime.Connection.ConnectedDevices.Any(
            d => !d.StartsWith(DeviceKeyPrefix, StringComparison.Ordinal));

    /// <summary>Controller identity, once connected.</summary>
    public static ControllerIdentity? Identity => ServerRuntime.Connection.Identity;

    /// <summary>
    /// Runs an operation against the controller, connecting first if needed and releasing
    /// afterwards.
    /// </summary>
    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await ServerRuntime.Connection.ConnectAsync(_deviceKey, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Releases the port only if nobody else is holding it. Deliberately not
            // cancellable: skipping this would leak the port.
            await ServerRuntime.Connection.DisconnectAsync(_deviceKey).ConfigureAwait(false);
        }
    }

    /// <summary>Runs an operation that returns nothing.</summary>
    public Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return RunAsync<object?>(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return null;
            },
            cancellationToken);
    }

    /// <summary>
    /// Turns an exception into a message worth showing to somebody standing at a telescope
    /// in the dark.
    /// </summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            OnStepCommandException command =>
                $"The controller refused the command: {command.Message}",

            TimeoutException =>
                "The controller did not answer in time. Check the cable, the power and the "
                + "configured baud rate.",

            OnStepProtocolException protocol =>
                $"Protocol error: {protocol.Message}",

            OperationCanceledException => "Cancelled.",

            _ => exception.Message,
        };
    }
}
