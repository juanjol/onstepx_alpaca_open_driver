using Microsoft.Extensions.Logging;
using OnStepX.Core.Protocol;

namespace OnStepX.Core.Devices;

/// <summary>
/// Keeps a fresh <see cref="MountSnapshot"/> by polling the controller in the
/// background.
/// </summary>
/// <remarks>
/// <para>
/// One poll cycle issues a handful of commands and publishes a single coherent
/// snapshot. Property reads then cost nothing, which matters because ASCOM
/// properties are synchronous and clients read them in tight loops.
/// </para>
/// <para>
/// Commands that change motion state must call
/// <see cref="RefreshNowAsync"/> before returning. Conform checks that
/// <c>Slewing</c> is true immediately after a slew starts, and a snapshot from
/// before the command would still say false.
/// </para>
/// </remarks>
public sealed class MountPoller : IAsyncDisposable
{
    private readonly Func<OnStepChannel> _channelProvider;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;
    private MountSnapshot _current = MountSnapshot.Empty;
    private bool _highPrecision = true;

    /// <summary>Creates the poller.</summary>
    /// <param name="channelProvider">
    /// Supplies the live channel. A function rather than a value so that
    /// reconnecting does not require rebuilding the poller.
    /// </param>
    /// <param name="pollInterval">Gap between background polls.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock, injectable for tests.</param>
    public MountPoller(
        Func<OnStepChannel> channelProvider,
        TimeSpan? pollInterval = null,
        ILogger<MountPoller>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(channelProvider);

        _channelProvider = channelProvider;
        PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _logger = logger
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MountPoller>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gap between background polls.</summary>
    public TimeSpan PollInterval { get; set; }

    /// <summary>Latest snapshot. Never null, but may be invalid before the first poll.</summary>
    public MountSnapshot Current => _current;

    /// <summary>
    /// Latest snapshot, refreshing synchronously if it has gone stale.
    /// </summary>
    /// <remarks>
    /// A safety net rather than the normal path. While the background loop is healthy
    /// the snapshot is always newer than this limit, so reads never block. If the loop
    /// has wedged, this is what stops the driver serving minutes old coordinates as if
    /// they were current, which is far worse than a slow read.
    /// </remarks>
    public MountSnapshot GetFresh()
    {
        MountSnapshot snapshot = _current;

        TimeSpan limit = PollInterval * StalenessFactor;

        if (snapshot.IsValid && _time.GetUtcNow() - snapshot.Timestamp <= limit)
        {
            return snapshot;
        }

        return RefreshNowAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// How many poll intervals a snapshot may age before a read forces a refresh.
    /// </summary>
    private const int StalenessFactor = 4;

    /// <summary>
    /// Marks the snapshot as stale, so the next read refreshes instead of serving it.
    /// </summary>
    /// <remarks>
    /// This exists for writers the polling loop knows nothing about, in particular the
    /// setup UI changing tracking compensation or the park position behind a client's
    /// back. The values are kept and only the timestamp is pushed back, so a caller
    /// reading <see cref="Current"/> still gets the last known state rather than an
    /// invalid snapshot.
    /// </remarks>
    public void Invalidate() => _current = _current with { Timestamp = DateTimeOffset.MinValue };

    /// <summary>Consecutive failed polls, for diagnostics.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Last polling error, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Starts the background loop and waits for the first snapshot.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            return;
        }

        // A first synchronous refresh means callers never see an invalid snapshot
        // once Connected has returned true.
        await RefreshNowAsync(cancellationToken).ConfigureAwait(false);

        // Capture the token in a local before queueing the loop.
        //
        // Reading _loopCancellation inside the lambda is a race: a device that connects
        // and immediately disconnects nulls the field before the queued task ever runs,
        // and the loop then dereferences null on a thread pool thread where nothing
        // useful catches it.
        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;

        _loopCancellation = cancellation;
        _loop = Task.Run(() => PollLoopAsync(token), CancellationToken.None);
    }

    /// <summary>Stops the background loop.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation = _loopCancellation;
        Task? loop = _loop;

        _loopCancellation = null;
        _loop = null;

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        cancellation?.Dispose();
        _current = MountSnapshot.Empty;
    }

    /// <summary>
    /// Polls immediately and publishes the result.
    /// </summary>
    /// <remarks>
    /// Call this straight after any command that changes motion or park state, so
    /// the very next property read already reflects it.
    /// </remarks>
    public async Task<MountSnapshot> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MountSnapshot snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);

            _current = snapshot;
            ConsecutiveFailures = 0;
            LastError = null;

            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed poll must not kill the loop: a mount can be briefly
                // unresponsive while writing to its own non volatile storage, and
                // giving up would leave every property frozen for good.
                ConsecutiveFailures++;
                LastError = ex.Message;

                if (ConsecutiveFailures is 1 or 10 or 100)
                {
                    _logger.LogWarning(
                        ex, "Mount poll failed {Count} time(s) in a row", ConsecutiveFailures);
                }
            }
        }
    }

    private async Task<MountSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        OnStepChannel channel = _channelProvider();

        // One :GU# drives Slewing, AtPark, AtHome, Tracking and SideOfPier, so the
        // whole ASCOM surface comes from a single command rather than one per
        // property.
        MountStatus status = await channel.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        double ra = await ReadAngleAsync(channel, "GR", "GRH", cancellationToken).ConfigureAwait(false);
        double dec = await ReadAngleAsync(channel, "GD", "GDH", cancellationToken).ConfigureAwait(false);
        double alt = await ReadAngleAsync(channel, "GA", "GAH", cancellationToken).ConfigureAwait(false);
        double az = await ReadAngleAsync(channel, "GZ", "GZH", cancellationToken).ConfigureAwait(false);
        double lst = await ReadAngleAsync(channel, "GS", "GSH", cancellationToken).ConfigureAwait(false);

        double trackingHz = 0;
        try
        {
            trackingHz = await channel.GetDoubleAsync("GT", cancellationToken).ConfigureAwait(false);
        }
        catch (OnStepProtocolException)
        {
            // Not fatal: the rate is a nicety, the status flags are what matter.
        }

        return new MountSnapshot
        {
            Timestamp = _time.GetUtcNow(),
            Status = status,
            RightAscension = ra,
            Declination = dec,
            Altitude = alt,
            Azimuth = az,
            SiderealTime = lst,
            TrackingHz = trackingHz,
            IsValid = true,
        };
    }

    /// <summary>
    /// Reads a coordinate, preferring the highest precision variant.
    /// </summary>
    /// <remarks>
    /// The high precision commands (<c>:GRH#</c> and friends) are not present on
    /// every firmware build. The first failure switches this poller to the standard
    /// commands for good, rather than paying a failed command on every cycle
    /// forever.
    /// </remarks>
    private async Task<double> ReadAngleAsync(
        OnStepChannel channel,
        string standard,
        string highPrecision,
        CancellationToken cancellationToken)
    {
        if (_highPrecision)
        {
            try
            {
                string reply = await channel
                    .GetStringAsync(highPrecision, cancellationToken)
                    .ConfigureAwait(false);

                if (Lx200Format.TryParse(reply, out double precise))
                {
                    return precise;
                }

                _highPrecision = false;
                _logger.LogInformation(
                    "This firmware does not answer {Command}, falling back to {Fallback}",
                    highPrecision, standard);
            }
            catch (OnStepProtocolException)
            {
                _highPrecision = false;
                _logger.LogInformation(
                    "This firmware rejected {Command}, falling back to {Fallback}",
                    highPrecision, standard);
            }
        }

        string standardReply = await channel
            .GetStringAsync(standard, cancellationToken)
            .ConfigureAwait(false);

        return Lx200Format.TryParse(standardReply, out double value)
            ? value
            : throw new OnStepProtocolException(
                $"Could not parse the reply to {standard}: " +
                OnStepFraming.Describe(standardReply));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _refreshGate.Dispose();
    }
}
