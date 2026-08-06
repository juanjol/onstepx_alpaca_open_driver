using Microsoft.Extensions.Logging;

namespace OnStepX.Core.Devices;

/// <summary>
/// Keeps one cached snapshot of a device fresh by polling in the background.
/// </summary>
/// <typeparam name="T">Snapshot type. Should be immutable.</typeparam>
/// <remarks>
/// <para>
/// The same shape as the mount poller, generalised for the focuser, the rotator and
/// the weather sensors. It exists for the same reason: ASCOM properties are
/// synchronous and clients read them in tight loops, so each one cannot afford its own
/// serial round trip, and values read one at a time would not agree with each other.
/// </para>
/// <para>
/// Callers must invalidate or refresh after any command that changes device state, or
/// the very next read will still describe the world as it was before the command.
/// </para>
/// </remarks>
public sealed class SnapshotPoller<T> : IAsyncDisposable
    where T : class
{
    private readonly Func<CancellationToken, Task<T>> _read;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _name;

    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;
    private T? _current;
    private DateTimeOffset _takenAt;

    /// <summary>Creates the poller.</summary>
    /// <param name="name">Device name, for log messages.</param>
    /// <param name="read">Reads one snapshot from the device.</param>
    /// <param name="pollInterval">Gap between background polls.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock, injectable for tests.</param>
    public SnapshotPoller(
        string name,
        Func<CancellationToken, Task<T>> read,
        TimeSpan? pollInterval = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(read);

        _name = name;
        _read = read;
        PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gap between background polls.</summary>
    public TimeSpan PollInterval { get; set; }

    /// <summary>Latest snapshot, or null before the first successful poll.</summary>
    public T? Current => _current;

    /// <summary>Consecutive failed polls, for diagnostics.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Last polling error, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// How many poll intervals a snapshot may age before a read forces a refresh.
    /// </summary>
    private const int StalenessFactor = 4;

    /// <summary>Starts the background loop after taking a first snapshot.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            return;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);

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

    /// <summary>Stops the background loop and drops the snapshot.</summary>
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
        _current = null;
    }

    /// <summary>Polls now and publishes the result.</summary>
    public async Task<T> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            T snapshot = await _read(cancellationToken).ConfigureAwait(false);

            _current = snapshot;
            _takenAt = _time.GetUtcNow();
            ConsecutiveFailures = 0;
            LastError = null;

            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Latest snapshot, refreshing synchronously if it has gone stale or does not exist
    /// yet.
    /// </summary>
    public T GetFresh()
    {
        T? snapshot = _current;

        if (snapshot is not null
            && _time.GetUtcNow() - _takenAt <= PollInterval * StalenessFactor)
        {
            return snapshot;
        }

        return RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Forces the next read to hit the device.
    /// </summary>
    /// <remarks>
    /// Cheaper than refreshing straight away when several commands run back to back:
    /// only the following read pays for the round trip.
    /// </remarks>
    public void Invalidate() => _takenAt = DateTimeOffset.MinValue;

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One failed poll must not kill the loop, or every property would stay
                // frozen for the rest of the session.
                ConsecutiveFailures++;
                LastError = ex.Message;

                if (ConsecutiveFailures is 1 or 10 or 100)
                {
                    _logger.LogWarning(
                        ex, "{Device} poll failed {Count} time(s) in a row",
                        _name, ConsecutiveFailures);
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
