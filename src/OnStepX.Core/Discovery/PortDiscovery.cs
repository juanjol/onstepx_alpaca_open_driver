using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OnStepX.Core.Protocol;
using OnStepX.Core.Transport;

namespace OnStepX.Core.Discovery;

/// <summary>Autodiscovery settings.</summary>
public sealed record PortDiscoveryOptions
{
    /// <summary>
    /// Configured baud rate, tried <b>first</b> on each port. If null,
    /// <see cref="BaudRates"/> is used directly.
    /// </summary>
    public int? PreferredBaudRate { get; init; } = 9600;

    /// <summary>
    /// Sweep baud rates, in order. Default is OnStepX's own, sorted from
    /// most to least likely.
    /// </summary>
    public IReadOnlyList<int> BaudRates { get; init; } =
        [9600, 19200, 115200, 57600, 38400, 230400, 460800];

    /// <summary>
    /// Deadline for each attempt. Short on purpose: ports that are not a
    /// match must be discarded quickly.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Ports probed at the same time. More than two or three causes
    /// problems with some USB to serial drivers.
    /// </summary>
    public int MaxConcurrency { get; init; } = 2;

    /// <summary>
    /// Stops as soon as it finds the first one. Useful when connecting,
    /// not when listing.
    /// </summary>
    public bool StopAtFirstMatch { get; init; }

    /// <summary>
    /// Ports that must not be touched, in addition to the ones
    /// <see cref="PortRanking"/> excludes.
    /// </summary>
    public IReadOnlyCollection<string> ExcludedPorts { get; init; } = [];

    /// <summary>
    /// Probes with checksum framing. Disabled by default: normal mode is
    /// the lowest common denominator and is enough for identification.
    /// </summary>
    public bool UseErrorCorrection { get; init; }

    /// <summary>
    /// Hands the matching port back still open instead of closing it.
    /// </summary>
    /// <remarks>
    /// Only for connecting, which is why it is internal. Closing a serial port
    /// and reopening it pulses DTR and RTS, resetting the boards that wire
    /// those lines to EN and GPIO0, so a probe that closes leaves the caller
    /// talking to a controller that is busy booting.
    /// </remarks>
    internal bool KeepTransportOpen { get; init; }
}

/// <summary>
/// A controller found by autodiscovery, with its port still open.
/// </summary>
/// <remarks>
/// The transport is already open and has just held a successful conversation
/// with the controller, so the caller must use it as it is rather than
/// reopening the port. Disposing this disposes the transport.
/// </remarks>
public sealed class DiscoveredConnection : IAsyncDisposable
{
    /// <summary>What answered, and where.</summary>
    public required DiscoveredController Controller { get; init; }

    /// <summary>The open transport that answered.</summary>
    public required ITransport Transport { get; init; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Transport.DisposeAsync();
}

/// <summary>Discovery progress, for the UI.</summary>
public sealed record PortDiscoveryProgress
{
    /// <summary>Ports already processed.</summary>
    public required int PortsProcessed { get; init; }

    /// <summary>Total ports to process.</summary>
    public required int PortsTotal { get; init; }

    /// <summary>Port currently being processed.</summary>
    public string? CurrentPort { get; init; }

    /// <summary>Baud rate currently being tried.</summary>
    public int? CurrentBaudRate { get; init; }

    /// <summary>Controller found in this step, if any.</summary>
    public DiscoveredController? Found { get; init; }
}

/// <summary>
/// Autodiscovery of OnStepX controllers on serial ports.
/// </summary>
/// <remarks>
/// Procedure, in this order:
/// <list type="number">
///   <item>Enumerate ports with whatever metadata is available.</item>
///   <item>
///     Classify and filter <b>before</b> opening anything, to avoid
///     blocking on Bluetooth ports or virtual modems.
///   </item>
///   <item>
///     Probe with <c>:GVP#</c> and confirm with <c>:GVN#</c>. Both are read
///     only, so it is safe to send them even to a mount that is slewing.
///   </item>
///   <item>Sweep baud rates, starting with the configured one.</item>
/// </list>
/// Confirming with a second command is what avoids false positives: at the
/// wrong baud rate, the garbage received can look like a valid response,
/// but two different commands both returning exactly what is expected is
/// very unlikely.
/// </remarks>
public sealed class PortDiscovery
{
    private readonly ISerialPortEnumerator _enumerator;
    private readonly Func<string, int, ITransport> _transportFactory;
    private readonly ILogger _logger;

    /// <summary>Creates the discoverer.</summary>
    /// <param name="enumerator">
    /// Port enumerator. If null, the platform's own is used.
    /// </param>
    /// <param name="transportFactory">
    /// Transport factory. Injectable to allow testing the whole flow with
    /// no hardware.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public PortDiscovery(
        ISerialPortEnumerator? enumerator = null,
        Func<string, int, ITransport>? transportFactory = null,
        ILogger? logger = null)
    {
        _enumerator = enumerator ?? SerialPortEnumerators.CreateDefault();
        _transportFactory = transportFactory
            ?? ((port, baud) => new SerialTransport(port, baud));
        _logger = logger
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PortDiscovery>.Instance;
    }

    /// <summary>
    /// Lists the ports with their classification, including the excluded
    /// ones and the reason. Opens none of them.
    /// </summary>
    public IReadOnlyList<SerialPortInfo> ListPorts() =>
        PortRanking.RankAll(_enumerator.Enumerate());

    /// <summary>
    /// Searches for OnStepX controllers.
    /// </summary>
    /// <returns>
    /// All the ones found, from highest to lowest port priority. All are
    /// returned, not just the first, so the user can choose if there are
    /// several.
    /// </returns>
    public async Task<IReadOnlyList<DiscoveredController>> DiscoverAsync(
        PortDiscoveryOptions? options = null,
        IProgress<PortDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProbeOutcome> outcomes = await DiscoverCoreAsync(
            options ?? new PortDiscoveryOptions(), progress, cancellationToken)
            .ConfigureAwait(false);

        return [.. outcomes.Select(o => o.Controller)];
    }

    private async Task<IReadOnlyList<ProbeOutcome>> DiscoverCoreAsync(
        PortDiscoveryOptions options,
        IProgress<PortDiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SerialPortInfo> candidates = PortRanking
            .Prioritise(_enumerator.Enumerate())
            .Where(p => !options.ExcludedPorts.Contains(p.PortName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        _logger.LogInformation(
            "Autodiscovery with {Enumerator}: {Count} candidate ports",
            _enumerator.Description, candidates.Count);

        if (candidates.Count == 0)
        {
            progress?.Report(new PortDiscoveryProgress { PortsProcessed = 0, PortsTotal = 0 });
            return [];
        }

        int[] baudRates = BuildBaudSweep(options);

        var found = new List<ProbeOutcome>();
        var foundLock = new object();
        int processed = 0;

        using var stopEarly = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var throttle = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));

        IEnumerable<Task> probes = candidates.Select(async port =>
        {
            try
            {
                await throttle.WaitAsync(stopEarly.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // It stopped before this port got its turn. This is not a
                // failure, so it must not propagate: if it did,
                // DiscoverAsync would throw instead of returning what was
                // already found, exactly in the StopAtFirstMatch case with
                // more ports than concurrency slots.
                return;
            }

            try
            {
                ProbeOutcome? result = await ProbePortAsync(
                    port, baudRates, options, progress, candidates.Count, stopEarly.Token)
                    .ConfigureAwait(false);

                lock (foundLock)
                {
                    processed++;

                    if (result is not null)
                    {
                        found.Add(result);
                    }

                    progress?.Report(new PortDiscoveryProgress
                    {
                        PortsProcessed = processed,
                        PortsTotal = candidates.Count,
                        CurrentPort = port.PortName,
                        Found = result?.Controller,
                    });
                }

                if (result is not null && options.StopAtFirstMatch)
                {
                    await stopEarly.CancelAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Early stop or user cancellation.
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(probes).ConfigureAwait(false);

        // If the user cancelled, it must propagate. An early stop from
        // having found something must not.
        if (cancellationToken.IsCancellationRequested)
        {
            // Throwing here would strand any port kept open for the caller,
            // and a held serial port stays held for the life of the process,
            // so the next attempt would find it busy and never see the mount.
            foreach (ProbeOutcome stranded in found)
            {
                if (stranded.Transport is not null)
                {
                    await stranded.Transport.DisposeAsync().ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        // Reordered by port priority, because concurrency alters the
        // completion order.
        var order = candidates
            .Select((p, i) => (p.PortName, Index: i))
            .ToDictionary(x => x.PortName, x => x.Index, StringComparer.OrdinalIgnoreCase);

        return found
            .OrderBy(f => order.TryGetValue(f.Controller.PortName, out int i) ? i : int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Checks whether a specific port and baud rate have an OnStepX.
    /// </summary>
    public async Task<DiscoveredController?> ProbeAsync(
        string portName,
        int baudRate,
        PortDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PortDiscoveryOptions();

        ProbeOutcome? outcome = await ProbeOneAsync(
            new SerialPortInfo { PortName = portName },
            baudRate,
            options,
            cancellationToken).ConfigureAwait(false);

        return outcome?.Controller;
    }

    /// <summary>
    /// Finds a controller to connect to and hands its port back still open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configured port is tried first, then everything else, which is the
    /// same order the listing uses. What is different is that the port that
    /// answered is never closed: closing it and reopening it pulses DTR and
    /// RTS, and on the boards that wire those to EN and GPIO0 that resets the
    /// controller, so the caller would be talking to a board that is booting.
    /// One connect, one open.
    /// </para>
    /// <para>
    /// The caller owns the returned transport and must dispose it.
    /// </para>
    /// </remarks>
    public async Task<DiscoveredConnection?> ConnectAsync(
        string? preferredPort,
        int preferredBaudRate,
        PortDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        PortDiscoveryOptions probeOptions = (options ?? new PortDiscoveryOptions()) with
        {
            KeepTransportOpen = true,
        };

        if (!string.IsNullOrWhiteSpace(preferredPort))
        {
            ProbeOutcome? direct = await ProbeOneAsync(
                new SerialPortInfo { PortName = preferredPort },
                preferredBaudRate,
                probeOptions,
                cancellationToken).ConfigureAwait(false);

            if (direct?.Transport is not null)
            {
                return new DiscoveredConnection
                {
                    Controller = direct.Controller,
                    Transport = direct.Transport,
                };
            }

            _logger.LogInformation(
                "Configured port {Port} did not respond, searching the others", preferredPort);
        }

        IReadOnlyList<ProbeOutcome> outcomes = await DiscoverCoreAsync(
            probeOptions, progress: null, cancellationToken).ConfigureAwait(false);

        ProbeOutcome? winner = outcomes.FirstOrDefault(o => o.Transport is not null);

        // StopAtFirstMatch races: two ports can both answer before the early
        // stop lands. Everything that is not the winner gets closed here, or
        // the losing ports would stay held open for the life of the process.
        foreach (ProbeOutcome other in outcomes)
        {
            if (!ReferenceEquals(other, winner) && other.Transport is not null)
            {
                await other.Transport.DisposeAsync().ConfigureAwait(false);
            }
        }

        return winner?.Transport is null
            ? null
            : new DiscoveredConnection
            {
                Controller = winner.Controller,
                Transport = winner.Transport,
            };
    }

    private static int[] BuildBaudSweep(PortDiscoveryOptions options)
    {
        var sweep = new List<int>();

        // The configured one goes first: in the normal case it hits on the
        // first try.
        if (options.PreferredBaudRate is int preferred)
        {
            sweep.Add(preferred);
        }

        foreach (int baud in options.BaudRates)
        {
            if (!sweep.Contains(baud))
            {
                sweep.Add(baud);
            }
        }

        return [.. sweep];
    }

    private async Task<ProbeOutcome?> ProbePortAsync(
        SerialPortInfo port,
        int[] baudRates,
        PortDiscoveryOptions options,
        IProgress<PortDiscoveryProgress>? progress,
        int totalPorts,
        CancellationToken cancellationToken)
    {
        ITransport probe = _transportFactory(port.PortName, baudRates[0]);

        // Retuning an open port beats reopening it once per speed. Each reopen
        // pulses DTR and RTS, and on the boards wiring those to EN and GPIO0
        // that is a reset, so the controller is still booting when the next
        // speed is tried: it answers at none of them and restarts audibly
        // throughout, which reads as "no controller here" at every speed.
        if (!probe.TrySetBaudRate(baudRates[0]))
        {
            // Handed straight on rather than thrown away: it was built for the
            // first speed in the list, which is the first thing that path tries.
            return await ProbeByReopeningAsync(
                port, baudRates, options, progress, totalPorts, cancellationToken, probe)
                .ConfigureAwait(false);
        }

        return await SweepOpenPortAsync(
            probe, port, baudRates, options, progress, totalPorts, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Tries every speed over one open port, which is one board reset.</summary>
    private async Task<ProbeOutcome?> SweepOpenPortAsync(
        ITransport transport,
        SerialPortInfo port,
        int[] baudRates,
        PortDiscoveryOptions options,
        IProgress<PortDiscoveryProgress>? progress,
        int totalPorts,
        CancellationToken cancellationToken)
    {
        ITransport? unowned = transport;
        OnStepChannel? channel = null;

        try
        {
            using (var openTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // Opening can hang on a problematic port, so it gets a deadline
                // of its own rather than eating into the conversation's.
                openTimeout.CancelAfter(options.ProbeTimeout * 2);

                await transport.OpenAsync(openTimeout.Token).ConfigureAwait(false);
            }

            channel = new OnStepChannel(
                transport, ProbeChannelOptions(options, mayStillBeBooting: true));

            // The transport becomes owned by the channel.
            unowned = null;

            foreach (int baud in baudRates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new PortDiscoveryProgress
                {
                    PortsProcessed = 0,
                    PortsTotal = totalPorts,
                    CurrentPort = port.PortName,
                    CurrentBaudRate = baud,
                });

                if (!channel.Transport.TrySetBaudRate(baud))
                {
                    // Retuning stopped working part way through, so the rest
                    // of the list is better left to the reopening path than
                    // silently skipped.
                    break;
                }

                // Whatever the previous speed produced is noise at this one.
                channel.DiscardPending();

                DiscoveredController? found = await TryIdentifyAsync(
                    channel, port, baud, options, cancellationToken).ConfigureAwait(false);

                if (found is not null)
                {
                    return new ProbeOutcome(
                        found,
                        options.KeepTransportOpen ? channel.DetachTransport() : null);
                }

                // That first attempt already waited out the boot the open
                // caused, so the rest of the speeds do not have to pay for it
                // again. This is what stops a full sweep taking twice as long
                // as it needs to.
                channel.Options = ProbeChannelOptions(options, mayStillBeBooting: false);
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sweeping {Port} yielded nothing", port.PortName);
            return null;
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            else if (unowned is not null)
            {
                await unowned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Tries every speed by reopening the port for each one.</summary>
    /// <remarks>
    /// The fallback for transports with no notion of speed, which is the
    /// simulator and the scripted ones in the tests. Real serial ports take
    /// the single open path, because for them each reopen costs a board reset.
    /// </remarks>
    private async Task<ProbeOutcome?> ProbeByReopeningAsync(
        SerialPortInfo port,
        int[] baudRates,
        PortDiscoveryOptions options,
        IProgress<PortDiscoveryProgress>? progress,
        int totalPorts,
        CancellationToken cancellationToken,
        ITransport? alreadyBuilt = null)
    {
        ITransport? pending = alreadyBuilt;

        try
        {
            foreach (int baud in baudRates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new PortDiscoveryProgress
                {
                    PortsProcessed = 0,
                    PortsTotal = totalPorts,
                    CurrentPort = port.PortName,
                    CurrentBaudRate = baud,
                });

                // Only the first pass can inherit one, and once handed over
                // it belongs to the probe.
                ITransport? inherited = pending;
                pending = null;

                ProbeOutcome? result = await ProbeOneAsync(
                    port, baud, options, cancellationToken, inherited).ConfigureAwait(false);

                if (result is not null)
                {
                    return result;
                }
            }

            return null;
        }
        finally
        {
            // Only reachable if the loop never ran or threw before using it.
            if (pending is not null)
            {
                await pending.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>A match, plus its port if the caller asked to keep it open.</summary>
    private sealed record ProbeOutcome(DiscoveredController Controller, ITransport? Transport);

    /// <summary>Channel settings for a probe.</summary>
    /// <param name="options">Discovery settings the timeout comes from.</param>
    /// <param name="mayStillBeBooting">
    /// Whether the port was just opened, so the controller may not be up yet.
    /// </param>
    /// <remarks>
    /// The retry exists only to cover a board booting: on boards that reset on
    /// DTR/RTS assertion (common on ESP32/CH340) the port can still be mid boot
    /// when the first command is written, so it is silently dropped and a
    /// second write is what lands.
    /// <para>
    /// Which makes it worth paying exactly once. A sweep over one open port
    /// boots the board at the open and not again, so retrying every subsequent
    /// speed only doubles the time spent proving each wrong one wrong.
    /// </para>
    /// </remarks>
    private static OnStepChannelOptions ProbeChannelOptions(
        PortDiscoveryOptions options,
        bool mayStillBeBooting) => new()
    {
        UseErrorCorrection = options.UseErrorCorrection,
        Timeout = options.ProbeTimeout,
        MaxRetries = mayStillBeBooting ? 1 : 0,
    };

    /// <summary>
    /// Asks an open channel who it is, at whatever speed it is set to.
    /// </summary>
    private async Task<DiscoveredController?> TryIdentifyAsync(
        OnStepChannel channel,
        SerialPortInfo port,
        int baudRate,
        PortDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Room for one retry per command: two full timeouts for GVP and two
        // for GVN.
        timeout.CancelAfter(options.ProbeTimeout * 4);

        try
        {
            string product = await channel.GetStringAsync("GVP", timeout.Token)
                .ConfigureAwait(false);

            if (!LooksLikeOnStep(product))
            {
                _logger.LogDebug(
                    "{Port} at {Baud} answered something that does not look like OnStep: {Reply}",
                    port.PortName, baudRate, OnStepFraming.Describe(product));
                return null;
            }

            // Confirmation with a second command. This is what rules out
            // false positives from garbage at the wrong baud rate.
            string version = await channel.GetStringAsync("GVN", timeout.Token)
                .ConfigureAwait(false);

            if (!LooksLikeVersion(version))
            {
                _logger.LogDebug(
                    "{Port} at {Baud} gave a plausible product but an invalid version: {Reply}",
                    port.PortName, baudRate, OnStepFraming.Describe(version));
                return null;
            }

            stopwatch.Stop();

            _logger.LogInformation(
                "Found {Product} {Version} on {Port} at {Baud} baud",
                product, version, port.PortName, baudRate);

            return new DiscoveredController
            {
                PortName = port.PortName,
                BaudRate = baudRate,
                ProductName = product,
                FirmwareVersion = version,
                FriendlyName = port.FriendlyName,
                ProbeDuration = stopwatch.Elapsed,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex, "Probing {Port} at {Baud} yielded nothing", port.PortName, baudRate);
            return null;
        }
    }

    private async Task<ProbeOutcome?> ProbeOneAsync(
        SerialPortInfo port,
        int baudRate,
        PortDiscoveryOptions options,
        CancellationToken cancellationToken,
        ITransport? alreadyBuilt = null)
    {
        ITransport? transport = null;
        OnStepChannel? channel = null;

        try
        {
            transport = alreadyBuilt ?? _transportFactory(port.PortName, baudRate);

            using (var openTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // Opening can also hang on a problematic port.
                openTimeout.CancelAfter(options.ProbeTimeout * 2);

                await transport.OpenAsync(openTimeout.Token).ConfigureAwait(false);
            }

            channel = new OnStepChannel(
                transport, ProbeChannelOptions(options, mayStillBeBooting: true));

            // The transport becomes owned by the channel.
            transport = null;

            DiscoveredController? found = await TryIdentifyAsync(
                channel, port, baudRate, options, cancellationToken).ConfigureAwait(false);

            if (found is null)
            {
                return null;
            }

            // Handing the live port over is what stops the controller being
            // reset a second time: the caller carries on over this same open
            // port instead of closing it and pulsing DTR and RTS again.
            return new ProbeOutcome(
                found,
                options.KeepTransportOpen ? channel.DetachTransport() : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any failure while probing means "this is not the right
            // port". A port that is busy, nonexistent, or unresponsive
            // must not abort the search.
            _logger.LogDebug(
                ex, "Probing {Port} at {Baud} yielded nothing", port.PortName, baudRate);
            return null;
        }
        finally
        {
            // The channel owns the transport once it exists, and closes it
            // unless DetachTransport handed it to the caller above.
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            else if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Checks that the response to <c>:GVP#</c> is plausible.
    /// </summary>
    /// <remarks>
    /// Any short printable text is accepted, not just "On-Step": there are
    /// forks and versions that change the product name, and rejecting them
    /// would leave perfectly valid mounts without autodiscovery. The strong
    /// filter is the confirmation with <c>:GVN#</c>.
    /// </remarks>
    internal static bool LooksLikeOnStep(string? product)
    {
        if (string.IsNullOrWhiteSpace(product) || product.Length > 32)
        {
            return false;
        }

        return product.All(c => c is >= (char)0x20 and <= (char)0x7E);
    }

    /// <summary>
    /// Checks that the response to <c>:GVN#</c> looks like a version.
    /// </summary>
    internal static bool LooksLikeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 16)
        {
            return false;
        }

        // Must start with a digit and contain only digits, dots and suffix
        // letters. Covers "10.21b", "4.24s" and "3.16".
        if (!char.IsAsciiDigit(version[0]))
        {
            return false;
        }

        return version.All(c => char.IsAsciiDigit(c) || c == '.' || char.IsAsciiLetter(c))
            && version.Contains('.', StringComparison.Ordinal);
    }
}
