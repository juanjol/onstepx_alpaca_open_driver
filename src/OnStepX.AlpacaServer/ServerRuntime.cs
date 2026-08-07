using Microsoft.Extensions.Logging;
using OnStepX.Core.Config;
using OnStepX.Core.Hardware;

namespace OnStepX.AlpacaServer;

/// <summary>
/// Live server state: settings and the shared controller connection.
/// </summary>
/// <remarks>
/// <para>
/// Static on purpose. The official <c>ASCOM.Alpaca.Razor</c> REST layer resolves
/// devices through <c>DeviceManager</c>, which is itself static and has to be
/// populated before the host starts accepting requests, so the devices cannot receive
/// their dependencies from the container. A single obvious static entry point is more
/// honest than pretending to use injection and ending up with hidden global state.
/// </para>
/// <para>
/// Everything underneath is instantiable and testable, in particular
/// <see cref="OnStepXConnection"/>, which is covered separately with injected
/// transports.
/// </para>
/// </remarks>
public static class ServerRuntime
{
    private static readonly object Gate = new();

    private static SettingsStore? _store;
    private static OnStepXSettings _settings = new();
    private static OnStepXConnection? _connection;

    /// <summary>Settings currently in force.</summary>
    public static OnStepXSettings Settings
    {
        get
        {
            lock (Gate)
            {
                return _settings;
            }
        }
    }

    /// <summary>Settings store.</summary>
    public static SettingsStore Store =>
        _store ?? throw new InvalidOperationException("The server is not initialised yet.");

    /// <summary>Shared connection to the controller.</summary>
    public static OnStepXConnection Connection =>
        _connection ?? throw new InvalidOperationException("The server is not initialised yet.");

    /// <summary>Warning raised while loading settings, if there was one.</summary>
    public static string? SettingsWarning { get; private set; }

    /// <summary>Options the process was started with.</summary>
    public static CommandLineOptions Options { get; private set; } = new();

    /// <summary>
    /// The process is running against a simulated transport. The user interface makes
    /// this loud, so that nobody spends an evening wondering why the mount will not move.
    /// </summary>
    public static bool IsSimulated => Settings.Connection.Kind == TransportKind.Simulated;

    /// <summary>Initialises server state. Called once at startup.</summary>
    public static void Initialise(
        CommandLineOptions options,
        ILoggerFactory loggerFactory,
        Func<Core.Transport.ITransport>? transportOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        lock (Gate)
        {
            Options = options;

            _store = new SettingsStore(options.SettingsPath ?? SettingsStore.DefaultPath());
            _settings = CommandLine.Apply(_store.Load(out string? warning), options);
            SettingsWarning = warning;

            _connection = new OnStepXConnection(
                () => Settings,
                transportOverride,
                loggerFactory.CreateLogger<OnStepXConnection>());

            // Autodiscovery updates the port and speed in memory when it finds
            // the controller. Writing them out here is what keeps the slow
            // first connect to genuinely once, instead of once per restart.
            _connection.PortRemembered += SaveSettings;
        }
    }

    /// <summary>
    /// Replaces the settings and saves them.
    /// </summary>
    /// <remarks>
    /// Connection changes do not affect a session already under way, because the
    /// transport is chosen at connect time. Reconnecting is what applies them, which is
    /// far better than dropping the connection of a client that is halfway through an
    /// imaging run.
    /// </remarks>
    public static void UpdateSettings(OnStepXSettings settings, bool persist = true)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (Gate)
        {
            _settings = settings;

            if (persist && _store is not null)
            {
                _store.Save(settings);
            }
        }
    }

    /// <summary>Saves the settings currently in force.</summary>
    public static void SaveSettings()
    {
        lock (Gate)
        {
            _store?.Save(_settings);
        }
    }

    /// <summary>Closes the connection when the server shuts down.</summary>
    public static async Task ShutdownAsync()
    {
        OnStepXConnection? connection;

        lock (Gate)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Resets all state. Tests only.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _store = null;
            _settings = new OnStepXSettings();
            _connection = null;
            SettingsWarning = null;
            Options = new CommandLineOptions();
        }
    }
}
