using System.IO.Ports;

namespace OnStepX.Core.Transport;

/// <summary>
/// Serial transport. Works the same on Windows over <c>COMn</c> and on
/// Linux over <c>/dev/ttyUSB0</c> or <c>/dev/ttyACM0</c>, thanks to the
/// <c>System.IO.Ports</c> package.
/// </summary>
public sealed class SerialTransport : ITransport
{
    /// <summary>
    /// Poll interval when there is no data. At 2 ms Windows's scheduler
    /// rounds it up, but since reading happens in blocks, that cost is
    /// paid once per response, not once per character.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);

    private readonly string _portName;
    private readonly int _baudRate;

    /// <summary>
    /// Intermediate buffer because <see cref="SerialPort.Read(byte[], int, int)"/>
    /// only accepts arrays. Its use is serialized by the channel, which
    /// never issues two reads at the same time.
    /// </summary>
    private readonly byte[] _scratch = new byte[512];

    private SerialPort? _port;

    /// <summary>
    /// Baud rates OnStepX offers, from <c>:SB0#</c> to <c>:SB9#</c> plus
    /// the two high ones <c>:SBA#</c> and <c>:SBB#</c>.
    /// </summary>
    /// <remarks>
    /// Sorted from most to least likely, for the autodiscovery sweep. 9600
    /// goes first because it is what most profiles ship with from the factory.
    /// </remarks>
    public static readonly int[] SupportedBaudRates =
    [
        9600, 19200, 115200, 57600, 38400, 230400, 460800, 28800, 14400, 4800, 2400, 1200,
    ];

    /// <summary>Creates the transport. Opens nothing yet.</summary>
    public SerialTransport(string portName, int baudRate = 9600)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);

        _portName = portName;
        _baudRate = baudRate;
    }

    /// <inheritdoc />
    public string Description => $"{_portName} at {_baudRate} baud";

    /// <inheritdoc />
    public bool IsOpen => _port?.IsOpen == true;

    /// <inheritdoc />
    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        if (IsOpen)
        {
            throw new InvalidOperationException($"{Description} is already open.");
        }

        var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            // The protocol uses no flow control.
            Handshake = Handshake.None,

            // The real deadlines are governed by the channel's
            // CancellationToken. Wide values are set here so the
            // underlying driver does not abort before the layer above does.
            ReadTimeout = 5000,
            WriteTimeout = 5000,

            // OnStepX over USB CDC needs DTR active on several boards, and
            // on ESP32 pulsing DTR and RTS can trigger a reset. Both are
            // left active, which is what the rest of the ecosystem does.
            DtrEnable = true,
            RtsEnable = true,
        };

        port.Open();
        _port = port;

        // A freshly opened board can have boot up garbage in the buffer.
        DiscardInputBuffer();

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask CloseAsync()
    {
        var port = _port;
        _port = null;

        if (port is not null)
        {
            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch (IOException)
            {
                // Closing a port whose device has already disconnected
                // throws. This is not a failure that should propagate on close.
            }
            finally
            {
                port.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        SerialPort port = RequireOpenPort();

        await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Does not use <c>BaseStream.ReadAsync</c> on purpose. On Windows that
    /// call <b>does not reliably honor the CancellationToken</b>: it stays
    /// blocked until data arrives, and at that point the channel's
    /// deadline stops having any effect, right on the transport that
    /// matters most. This is a known issue with <c>SerialPort</c>.
    /// </para>
    /// <para>
    /// Instead, <see cref="SerialPort.BytesToRead"/> is polled, which does
    /// not block, and when there is data <b>all of what is available is
    /// read at once</b>. This block reading is what makes Windows's coarse
    /// timer granularity, on the order of 15 ms, get paid once per
    /// response and not once per character.
    /// </para>
    /// </remarks>
    public async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SerialPort port = RequireOpenPort();

            int available = port.BytesToRead;
            if (available > 0)
            {
                int count = Math.Min(available, buffer.Length);
                int read = port.Read(_scratch, 0, count);

                if (read > 0)
                {
                    _scratch.AsSpan(0, read).CopyTo(buffer.Span);
                    return read;
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void DiscardInputBuffer()
    {
        try
        {
            _port?.DiscardInBuffer();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // If the port was just lost, discarding makes no sense and
            // must not break the recovery flow either.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private SerialPort RequireOpenPort()
    {
        SerialPort? port = _port;

        if (port is null || !port.IsOpen)
        {
            throw new InvalidOperationException($"{Description} is not open.");
        }

        return port;
    }
}
