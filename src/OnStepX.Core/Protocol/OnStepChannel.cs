using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using OnStepX.Core.Transport;

namespace OnStepX.Core.Protocol;

/// <summary>
/// Command channel toward OnStepX. Serializes transactions, frames
/// requests, interprets responses, and retries when needed.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the sole owner of the transport.</b> All transactions go
/// through a single permit semaphore, so the four ASCOM devices can share
/// one serial port without stepping on each other. This is what makes an
/// external hub with named pipes unnecessary.
/// </para>
/// <para>
/// The shape of the response cannot be inferred from the response itself,
/// it must be declared per command with <see cref="ReplyKind"/>. See that
/// enumeration's documentation for why.
/// </para>
/// </remarks>
public sealed class OnStepChannel : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Receive buffer owned by the channel.
    /// </summary>
    /// <remarks>
    /// The channel requests blocks from the transport and consumes
    /// characters from here, instead of requesting one byte at a time.
    /// This is what makes the cost of the serial transport's polling, with
    /// its coarse timer granularity, paid once per response and not once
    /// per character. Its use is protected by <see cref="_gate"/>.
    /// </remarks>
    private readonly byte[] _rx = new byte[256];
    private int _rxStart;
    private int _rxEnd;

    private char _sequence = 'a';
    private bool _disposed;

    /// <summary>Creates the channel over an already built transport.</summary>
    public OnStepChannel(
        ITransport transport,
        OnStepChannelOptions? options = null,
        ILogger<OnStepChannel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        Options = options ?? new OnStepChannelOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OnStepChannel>.Instance;
    }

    /// <summary>Settings in effect. Can be replaced on the fly.</summary>
    public OnStepChannelOptions Options { get; set; }

    /// <summary>Underlying transport, for querying state and description.</summary>
    public ITransport Transport => _transport;

    /// <summary>Number of completed transactions, for diagnostics.</summary>
    public long TransactionCount { get; private set; }

    /// <summary>Number of retries consumed, for diagnostics.</summary>
    public long RetryCount { get; private set; }

    /// <summary>
    /// Executes a command with no response. Examples: <c>:FQ#</c>, <c>:TQ#</c>.
    /// </summary>
    public async Task SendAsync(string payload, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(payload, ReplyKind.None, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Executes a command whose payload ends in <c>#</c>. Examples:
    /// <c>:GVP#</c>, <c>:GU#</c>, <c>:Fg#</c>.
    /// </summary>
    public async Task<string> GetStringAsync(
        string payload,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(payload, ReplyKind.Terminated, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Executes a boolean command. Examples: <c>:Te#</c>, <c>:hP#</c>.
    /// </summary>
    public async Task<bool> GetBoolAsync(
        string payload,
        CancellationToken cancellationToken = default)
    {
        string reply = await ExecuteAsync(payload, ReplyKind.Boolean, cancellationToken)
            .ConfigureAwait(false);

        return reply == "1";
    }

    /// <summary>
    /// Executes a boolean command and throws if the firmware returns false,
    /// querying <c>:GE#</c> first to give a specific message.
    /// </summary>
    public async Task RequireTrueAsync(
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (await GetBoolAsync(payload, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        CommandError error = await GetLastErrorAsync(cancellationToken).ConfigureAwait(false);
        throw new OnStepCommandException(error, payload);
    }

    /// <summary>
    /// Executes a command that returns a single digit. This is the case
    /// for gotos: <c>:MS#</c>, <c>:MA#</c>, <c>:MN#</c>, <c>:MP#</c> and
    /// <c>:MD#</c>.
    /// </summary>
    public async Task<int> GetDigitAsync(
        string payload,
        CancellationToken cancellationToken = default)
    {
        string reply = await ExecuteAsync(payload, ReplyKind.SingleDigit, cancellationToken)
            .ConfigureAwait(false);

        if (reply.Length != 1 || !char.IsAsciiDigit(reply[0]))
        {
            throw new OnStepProtocolException(
                $"Expected a digit and got {OnStepFraming.Describe(reply)}.")
            {
                Payload = payload,
            };
        }

        return reply[0] - '0';
    }

    /// <summary>
    /// Issues a goto and translates the return code.
    /// </summary>
    public async Task<GotoResult> GetGotoResultAsync(
        string payload,
        CancellationToken cancellationToken = default)
    {
        int digit = await GetDigitAsync(payload, cancellationToken).ConfigureAwait(false);

        return (GotoResult)digit;
    }

    /// <summary>
    /// Reads a decimal value from a command with a payload response.
    /// Always uses invariant culture, because the firmware writes the
    /// decimal point as separator.
    /// </summary>
    public async Task<double> GetDoubleAsync(
        string payload,
        CancellationToken cancellationToken = default)
    {
        string reply = await GetStringAsync(payload, cancellationToken).ConfigureAwait(false);

        if (!double.TryParse(
                reply,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value))
        {
            throw new OnStepProtocolException(
                $"Expected a number and got {OnStepFraming.Describe(reply)}.")
            {
                Payload = payload,
            };
        }

        return value;
    }

    /// <summary>
    /// Reads an integer from a command with a payload response.
    /// </summary>
    public async Task<long> GetInt64Async(
        string payload,
        CancellationToken cancellationToken = default)
    {
        string reply = await GetStringAsync(payload, cancellationToken).ConfigureAwait(false);

        // The firmware prefixes the sign with '+' in several position commands.
        if (!long.TryParse(
                reply,
                NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long value))
        {
            throw new OnStepProtocolException(
                $"Expected an integer and got {OnStepFraming.Describe(reply)}.")
            {
                Payload = payload,
            };
        }

        return value;
    }

    /// <summary>
    /// Reads a command that may not exist in this firmware build, returning
    /// <see langword="null"/> instead of throwing when it does not answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Most of the configuration command set is compile time gated, so a build
    /// without PEC, without encoders or without StallGuard simply has no handler for
    /// those frames. How that absence looks depends on the framing mode, and both
    /// cases end up here:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// With error correction the firmware answers everything, so an unknown command
    /// arrives as the body <c>0</c>, which is <c>CE_CMD_UNKNOWN</c> reported through
    /// the ordinary numeric reply path.
    /// </item>
    /// <item>
    /// Without error correction nothing terminated ever arrives and the read expires.
    /// </item>
    /// </list>
    /// <para>
    /// Retries are switched off deliberately. They exist to survive line noise, and
    /// a command the firmware does not implement will not start existing on the
    /// second attempt. Leaving them on multiplies the cost of every absent field by
    /// the retry count, which is how a diagnostics page turns into a minute of
    /// waiting on real hardware.
    /// </para>
    /// <para>
    /// The body is returned raw, including a literal <c>0</c>, because the caller is
    /// the only one that knows whether zero is a legitimate value for that field.
    /// Backlash of zero is normal; a mount type of zero is not.
    /// </para>
    /// </remarks>
    public async Task<string?> TryGetStringAsync(
        string payload,
        CancellationToken cancellationToken = default)
    {
        OnStepChannelOptions probe = Options with { MaxRetries = 0 };

        try
        {
            string reply = await ExecuteAsync(
                    payload, ReplyKind.Terminated, probe, cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrEmpty(reply) ? null : reply;
        }
        catch (Exception ex) when (ex is OnStepProtocolException or TimeoutException or IOException)
        {
            _logger.LogDebug("{Payload} is not available in this build: {Reason}", payload, ex.Message);
            return null;
        }
    }

    /// <summary>Reads the last error code with <c>:GE#</c>.</summary>
    public async Task<CommandError> GetLastErrorAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            string reply = await GetStringAsync("GE", cancellationToken).ConfigureAwait(false);

            return CommandErrors.TryParse(reply, out CommandError error)
                ? error
                : CommandError.ReplyUnknown;
        }
        catch (OnStepProtocolException)
        {
            // If even :GE# does not respond, nothing more than "something
            // failed" can be said.
            return CommandError.ReplyUnknown;
        }
    }

    /// <summary>Reads the mount status with <c>:GU#</c>.</summary>
    public async Task<MountStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        string reply = await GetStringAsync("GU", cancellationToken).ConfigureAwait(false);

        return MountStatus.Parse(reply);
    }

    /// <summary>
    /// Executes a complete transaction: frames, sends, reads, and retries.
    /// </summary>
    /// <param name="payload">Command and parameters, without delimiters.</param>
    /// <param name="kind">Expected shape of the response.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>
    /// Response payload. Empty string for <see cref="ReplyKind.None"/>.
    /// </returns>
    public Task<string> ExecuteAsync(
        string payload,
        ReplyKind kind,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(payload, kind, optionsOverride: null, cancellationToken);

    /// <summary>
    /// Executes a transaction with settings that apply to this call alone.
    /// </summary>
    /// <remarks>
    /// The override is taken inside the queue, so it cannot disturb a concurrent
    /// caller: the polling loop keeps the configured deadline and retry count while a
    /// one off probe runs with its own.
    /// </remarks>
    /// <param name="payload">Command and parameters, without delimiters.</param>
    /// <param name="kind">Expected shape of the response.</param>
    /// <param name="optionsOverride">
    /// Settings for this transaction, or <see langword="null"/> to use
    /// <see cref="Options"/>.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    public async Task<string> ExecuteAsync(
        string payload,
        ReplyKind kind,
        OnStepChannelOptions? optionsOverride,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OnStepChannelOptions options = optionsOverride ?? Options;
            int attempts = Math.Max(1, options.MaxRetries + 1);
            Exception? last = null;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (attempt > 1)
                {
                    RetryCount++;
                    _logger.LogDebug(
                        "Retry {Attempt} of {Attempts} for {Payload}",
                        attempt, attempts, payload);

                    await Task.Delay(options.RetryDelay, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    string reply = await TransactAsync(payload, kind, options, cancellationToken)
                        .ConfigureAwait(false);

                    TransactionCount++;
                    return reply;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Caller cancellation, not retried.
                    throw;
                }
                catch (Exception ex) when (ex is OnStepProtocolException or TimeoutException or IOException)
                {
                    last = ex;

                    // Before retrying the channel must be left clean: half
                    // of the previous attempt's response could still be in
                    // the buffer.
                    DiscardInput();
                }
            }

            throw new OnStepProtocolException(
                $"The command {payload} failed after {attempts} attempts.",
                last ?? new OnStepProtocolException("No cause recorded."))
            {
                Payload = payload,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> TransactAsync(
        string payload,
        ReplyKind kind,
        OnStepChannelOptions options,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        char sequence = _sequence;
        _sequence = OnStepFraming.NextSequence(_sequence);

        bool checksum = options.UseErrorCorrection;
        string frame = OnStepFraming.BuildRequest(payload, sequence, checksum);

        // Any leftover from a previous transaction that ended badly would
        // make us read the wrong response.
        DiscardInput();

        _logger.LogTrace("Sending {Frame}", OnStepFraming.Describe(frame));
        await _transport.WriteAsync(Encoding.ASCII.GetBytes(frame), timeout.Token)
            .ConfigureAwait(false);

        try
        {
            return checksum
                ? await ReadChecksummedAsync(payload, sequence, timeout.Token).ConfigureAwait(false)
                : await ReadPlainAsync(payload, kind, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{payload} did not respond within {options.Timeout.TotalMilliseconds:0} ms.");
        }
    }

    /// <summary>
    /// With checksum enabled the firmware responds to everything and
    /// always ends in <c>#</c>, so reading is uniform regardless of the
    /// <see cref="ReplyKind"/>.
    /// </summary>
    private async Task<string> ReadChecksummedAsync(
        string payload,
        char expectedSequence,
        CancellationToken cancellationToken)
    {
        string framed = await ReadUntilHashAsync(cancellationToken).ConfigureAwait(false);

        if (!OnStepFraming.TryUnwrapChecksummedReply(framed, out string body, out char sequence))
        {
            throw new OnStepProtocolException(
                $"Invalid checksum in the response to {payload}: " +
                OnStepFraming.Describe(framed))
            {
                Payload = payload,
            };
        }

        if (OnStepFraming.IsChecksumFailure(body))
        {
            throw new OnStepProtocolException(
                $"The firmware rejected the checksum of {payload} and is asking for " +
                "retransmission.")
            {
                Payload = payload,
            };
        }

        if (sequence != expectedSequence)
        {
            // The response from a previous transaction arrived. Retrying
            // is the right call: the channel resynchronizes by discarding
            // the input.
            throw new OnStepProtocolException(
                $"Desynchronized response in {payload}: sequence " +
                $"'{expectedSequence}' was expected and '{sequence}' arrived.")
            {
                Payload = payload,
            };
        }

        return body;
    }

    private async Task<string> ReadPlainAsync(
        string payload,
        ReplyKind kind,
        CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case ReplyKind.None:
                // Without checksum the firmware sends nothing, so there is
                // nothing to read. If a command's ReplyKind is declared
                // wrong, the input discard of the next transaction absorbs
                // it.
                return string.Empty;

            case ReplyKind.Boolean:
            case ReplyKind.SingleDigit:
                {
                    char c = await ReadCharAsync(cancellationToken).ConfigureAwait(false);
                    return c.ToString();
                }

            case ReplyKind.Terminated:
                return await ReadUntilHashAsync(cancellationToken).ConfigureAwait(false);

            default:
                throw new OnStepProtocolException($"Unsupported ReplyKind: {kind}.")
                {
                    Payload = payload,
                };
        }
    }

    private async Task<string> ReadUntilHashAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(32);

        while (true)
        {
            char c = await ReadCharAsync(cancellationToken).ConfigureAwait(false);

            if (c == '#')
            {
                return sb.ToString();
            }

            // The firmware ignores space, LF and CR while reading, and by
            // symmetry they should not appear in a response either. If they
            // do appear, they are discarded instead of contaminating the
            // payload.
            if (c is '\r' or '\n')
            {
                continue;
            }

            sb.Append(c);

            if (sb.Length > OnStepFraming.CommandBufferSize)
            {
                throw new OnStepProtocolException(
                    "The response exceeded the firmware buffer size without " +
                    $"finding the terminator: {OnStepFraming.Describe(sb.ToString())}");
            }
        }
    }

    private async Task<char> ReadCharAsync(CancellationToken cancellationToken)
    {
        // The same buffer is reused instead of allocating one per
        // character. It is safe because every transaction goes through the
        // semaphore, so there are never two simultaneous reads. This
        // matters because the :GU# polling reads dozens of characters per
        // cycle, several times per second.
        if (_rxStart >= _rxEnd)
        {
            _rxStart = 0;
            _rxEnd = await _transport.ReadAsync(_rx, cancellationToken).ConfigureAwait(false);

            if (_rxEnd <= 0)
            {
                _rxEnd = 0;
                throw new OnStepProtocolException("The transport closed while reading.");
            }
        }

        return (char)_rx[_rxStart++];
    }

    /// <summary>
    /// Discards pending input, both the transport's and the already read
    /// input still sitting in the channel's buffer.
    /// </summary>
    /// <remarks>
    /// Flushing only the transport is not enough: because it reads in
    /// blocks, the channel can have in <see cref="_rx"/> characters from a
    /// previous response that timed out halfway through. If they are not
    /// discarded, the next transaction would read them as if they were its
    /// own response and the channel would be permanently desynchronized.
    /// </remarks>
    private void DiscardInput()
    {
        _rxStart = 0;
        _rxEnd = 0;
        _transport.DiscardInputBuffer();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _transport.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
