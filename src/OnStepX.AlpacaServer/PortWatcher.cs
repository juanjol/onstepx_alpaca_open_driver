using OnStepX.Core.Config;
using OnStepX.Core.Discovery;

namespace OnStepX.AlpacaServer;

/// <summary>
/// Finds the controller before a client asks for it, and again whenever a new
/// serial port appears.
/// </summary>
/// <remarks>
/// <para>
/// A first, blind autodiscovery takes longer than some clients wait. NINA gives
/// up on a connect after a few seconds and retries, and each retry used to
/// restart the sweep from nothing, so a mount that was plainly there never got
/// connected. Doing that work up front means the port is already known and the
/// connect is a single open.
/// </para>
/// <para>
/// <b>What this deliberately does not do is probe on a timer.</b> Opening a
/// serial port resets the boards that wire DTR and RTS to EN and GPIO0, so a
/// probe every few seconds is a mount rebooting every few seconds, possibly
/// while it is tracking or while another program is talking to it. What runs on
/// the timer is <see cref="PortDiscovery.ListPorts"/>, which only enumerates
/// and opens nothing at all. Hardware is touched only when that enumeration
/// shows a port that was not there before, which is the moment somebody plugged
/// something in.
/// </para>
/// </remarks>
public sealed class PortWatcher(
    ILogger<PortWatcher> logger) : BackgroundService
{
    /// <summary>
    /// How often the port list is looked at. Cheap: it opens nothing.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Grace period before the first look, so startup is not competing with a
    /// client that connects immediately.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);

            var discovery = new PortDiscovery(logger: logger);

            HashSet<string> lastSeen = Enumerate(discovery);

            // The startup pass, gated on there being anything worth looking for.
            await ScanIfNeededAsync(lastSeen, "startup", stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(PollInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                HashSet<string> present = Enumerate(discovery);

                // Only ports that have appeared matter. One going away is a
                // mount being unplugged, which is not a reason to go looking.
                bool appeared = present.Except(lastSeen).Any();

                lastSeen = present;

                if (appeared)
                {
                    await ScanIfNeededAsync(present, "a new port appeared", stoppingToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private static HashSet<string> Enumerate(PortDiscovery discovery)
    {
        try
        {
            return discovery.ListPorts()
                .Select(p => p.PortName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Enumeration can fail transiently while a driver is installing.
            return [];
        }
    }

    private async Task ScanIfNeededAsync(
        HashSet<string> present,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!ShouldScan(present, out string? why))
        {
            return;
        }

        logger.LogInformation("Looking for the controller ({Reason}): {Why}", reason, why);

        await ServerRuntime.Connection.WarmUpAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether it is worth opening anything, decided without opening anything.
    /// </summary>
    private static bool ShouldScan(HashSet<string> present, out string? why)
    {
        why = null;

        ConnectionSettings connection = ServerRuntime.Settings.Connection;

        // Nothing to search for, or the user asked not to search.
        if (connection.Kind != TransportKind.Serial || !connection.AutoDiscoverPort)
        {
            return false;
        }

        // A client owns the port. Leave a working session alone.
        if (ServerRuntime.Connection.IsConnected)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(connection.PortName))
        {
            why = "no port is configured yet";
            return true;
        }

        // The remembered port is still there, so a connect will go straight to
        // it and there is nothing to work out. This is the case that keeps the
        // watcher free once it has succeeded once.
        if (present.Contains(connection.PortName))
        {
            return false;
        }

        why = $"the configured port {connection.PortName} is not present";
        return true;
    }
}
