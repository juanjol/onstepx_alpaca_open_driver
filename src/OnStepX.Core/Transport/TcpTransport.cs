using System.Net.Sockets;

namespace OnStepX.Core.Transport;

/// <summary>
/// TCP transport, for OnStepX's WiFi addon.
/// </summary>
/// <remarks>
/// The ecosystem's default port for commands is 9999. The old forms
/// showed 9998, which is the second channel. It is left configurable and
/// defaults to 9999.
/// </remarks>
public sealed class TcpTransport : ITransport
{
    /// <summary>WiFi addon's usual command port.</summary>
    public const int DefaultPort = 9999;

    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _connectTimeout;

    private TcpClient? _client;
    private NetworkStream? _stream;

    /// <summary>Creates the transport. Does not connect yet.</summary>
    public TcpTransport(string host, int port = DefaultPort, TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        _host = host;
        _port = port;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc />
    public string Description => $"{_host}:{_port}";

    /// <inheritdoc />
    public bool IsOpen => _client?.Connected == true && _stream is not null;

    /// <inheritdoc />
    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        if (IsOpen)
        {
            throw new InvalidOperationException($"{Description} is already connected.");
        }

        var client = new TcpClient
        {
            // The protocol consists of short messages with an immediate
            // response, so batching packets only adds latency.
            NoDelay = true,
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);

            await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException(
                $"Could not connect to {Description} within {_connectTimeout.TotalSeconds:0.#} s.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = client.GetStream();
    }

    /// <inheritdoc />
    public ValueTask CloseAsync()
    {
        NetworkStream? stream = _stream;
        TcpClient? client = _client;

        _stream = null;
        _client = null;

        stream?.Dispose();
        client?.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        NetworkStream stream = RequireStream();

        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        NetworkStream stream = RequireStream();

        return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void DiscardInputBuffer()
    {
        NetworkStream? stream = _stream;
        if (stream is null)
        {
            return;
        }

        try
        {
            // DataAvailable does not block, so it can be flushed with no deadline.
            Span<byte> scratch = stackalloc byte[256];
            while (stream.DataAvailable)
            {
                if (stream.Read(scratch) <= 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Connection lost. Discarding no longer makes sense.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private NetworkStream RequireStream()
    {
        NetworkStream? stream = _stream;

        if (stream is null || _client?.Connected != true)
        {
            throw new InvalidOperationException($"{Description} is not connected.");
        }

        return stream;
    }
}
