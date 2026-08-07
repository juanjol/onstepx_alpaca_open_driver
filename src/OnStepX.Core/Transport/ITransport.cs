namespace OnStepX.Core.Transport;

/// <summary>
/// Byte channel toward an OnStepX controller, whether serial, TCP, or simulated.
/// </summary>
/// <remarks>
/// Deliberately minimal: only open, close, write, read, and discard
/// pending input. All knowledge of the protocol lives in
/// <see cref="Protocol.OnStepChannel"/>, so the simulator can replace the
/// hardware without reimplementing any of the framing.
/// </remarks>
public interface ITransport : IAsyncDisposable
{
    /// <summary>
    /// Readable description for traces and the UI. For example
    /// <c>COM7 at 9600 baud</c> or <c>192.168.0.1:9999</c>.
    /// </summary>
    string Description { get; }

    /// <summary>Whether the transport is open.</summary>
    bool IsOpen { get; }

    /// <summary>Opens the channel. It is an error to open it if it already is.</summary>
    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the channel. Idempotent.</summary>
    ValueTask CloseAsync();

    /// <summary>Writes bytes, with no interpretation whatsoever.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads at least one byte. Returns <c>0</c> only if the channel was
    /// closed from the other end. Deadline expiration is expressed by
    /// cancelling <paramref name="cancellationToken"/>, not with a return
    /// value.
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards whatever is pending to be read.
    /// </summary>
    /// <remarks>
    /// Key to robustness: after an expired deadline a half response can be
    /// left in the buffer, and reading it as if it belonged to the next
    /// command permanently desynchronizes the channel.
    /// </remarks>
    void DiscardInputBuffer();

    /// <summary>
    /// Changes the speed of an already open channel, if that means anything here.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the speed was changed, <c>false</c> if this transport has
    /// no such concept, which is the default.
    /// </returns>
    /// <remarks>
    /// Autodiscovery sweeps a dozen speeds looking for the controller. Doing
    /// that by reopening the port once per speed pulses DTR and RTS every
    /// time, and on the boards wiring those lines to EN and GPIO0 that is a
    /// reset: the controller is thrown back into booting before it ever gets
    /// to answer, so the sweep reports nothing at any speed while the board
    /// audibly restarts over and over. Changing the speed in place keeps the
    /// whole sweep down to the single open the port needed anyway.
    /// </remarks>
    bool TrySetBaudRate(int baudRate) => false;
}
