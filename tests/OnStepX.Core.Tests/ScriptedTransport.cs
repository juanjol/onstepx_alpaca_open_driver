using System.Text;
using OnStepX.Core.Transport;

namespace OnStepX.Core.Tests;

/// <summary>
/// Test transport with byte by byte control. Lets responses be scripted as
/// corrupted, desynchronized, or absent, which is exactly what cannot be
/// triggered at will with real hardware nor with a well behaved simulator.
/// </summary>
internal sealed class ScriptedTransport : ITransport
{
    private readonly Queue<byte[]> _responses = new();
    private readonly List<string> _written = [];
    private readonly Queue<byte> _pending = new();

    private bool _open;

    public string Description => "scripted transport";

    public bool IsOpen => _open;

    /// <summary>Frames the channel has written, in order.</summary>
    public IReadOnlyList<string> Written => _written;

    /// <summary>Number of times the input has been discarded.</summary>
    public int DiscardCount { get; private set; }

    /// <summary>
    /// Queues a raw response, as is, adding nothing. The script controls
    /// the exact byte the channel sees.
    /// </summary>
    public ScriptedTransport Reply(string raw)
    {
        _responses.Enqueue(Encoding.ASCII.GetBytes(raw));
        return this;
    }

    /// <summary>
    /// Queues an absence of response, to trigger an expired deadline.
    /// </summary>
    public ScriptedTransport ReplyNothing()
    {
        _responses.Enqueue([]);
        return this;
    }

    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        _open = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync()
    {
        _open = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        _written.Add(Encoding.ASCII.GetString(data.Span));

        // The response is staged on write, mimicking a device that
        // answers every command.
        if (_responses.Count > 0)
        {
            foreach (byte b in _responses.Dequeue())
            {
                _pending.Enqueue(b);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Bytes delivered at most per read. At 1 it mimics a slow byte by
    /// byte device. At a high value it mimics real block reading, which is
    /// how a serial port behaves when data is already sitting in the
    /// driver's buffer.
    /// </summary>
    public int MaxBytesPerRead { get; set; } = 1;

    public async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        // If nothing is staged it waits indefinitely, so that the
        // channel's own deadline is what cuts it off. This is the only
        // way to genuinely test the expiration path.
        while (_pending.Count == 0)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        int count = Math.Min(Math.Min(_pending.Count, buffer.Length), MaxBytesPerRead);

        for (int i = 0; i < count; i++)
        {
            buffer.Span[i] = _pending.Dequeue();
        }

        return count;
    }

    public void DiscardInputBuffer()
    {
        DiscardCount++;
        _pending.Clear();
    }

    public ValueTask DisposeAsync()
    {
        _open = false;
        return ValueTask.CompletedTask;
    }
}
