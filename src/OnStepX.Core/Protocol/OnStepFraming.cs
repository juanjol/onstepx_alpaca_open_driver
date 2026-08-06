using System.Globalization;
using System.Text;

namespace OnStepX.Core.Protocol;

/// <summary>
/// Building and validation of frames for OnStepX's extended LX200 protocol,
/// with and without error correction.
/// </summary>
/// <remarks>
/// <para>
/// Normal frame: <c>:CC...#</c>. The payload is the command plus its
/// parameters.
/// </para>
/// <para>
/// Frame with checksum: <c>;CC...SS S #</c>, that is
/// <c>';' + payload + XX + sequence + '#'</c>, where <c>XX</c> is two
/// uppercase hexadecimal digits of the sum of the payload bytes modulo 256,
/// and <c>sequence</c> is an arbitrary character that the firmware echoes
/// back unchanged, which allows the request and the response to be paired
/// up.
/// </para>
/// <para>
/// The response with checksum follows the same scheme but without the
/// leading <c>;</c>: <c>payload + XX + sequence + '#'</c>. The checksum of
/// the response is calculated the same way, over the response payload.
/// </para>
/// <para>
/// Sources: <c>src/lib/commands/BufferCmds.cpp</c> (request validation) and
/// <c>src/libApp/commands/ProcessCmds.cpp</c>, function
/// <c>appendChecksum</c> (response generation) of the OnStepX firmware.
/// </para>
/// </remarks>
public static class OnStepFraming
{
    /// <summary>
    /// Capacity of the firmware's command buffer, in
    /// <c>src/lib/commands/BufferCmds.h</c>: <c>bufferSize = 80</c>.
    /// </summary>
    public const int CommandBufferSize = 80;

    /// <summary>
    /// Actual maximum length of a frame, delimiters included.
    /// </summary>
    /// <remarks>
    /// It is not the 80 of the buffer. <c>Buffer::add</c> does
    /// <c>if (cbp &gt; bufferSize - 2) cbp = bufferSize - 2;</c> before
    /// writing, so the last usable index is 78 and the NUL occupies index
    /// 79. Result: 79 characters fit, and beyond that the firmware
    /// <b>silently overwrites the last one</b> instead of rejecting the
    /// command. That is why it is worth validating it here and failing
    /// explicitly.
    /// </remarks>
    public const int MaxFrameLength = CommandBufferSize - 1;

    /// <summary>
    /// Response from the firmware when the request checksum does not
    /// match. It means "retransmit".
    /// </summary>
    public const string ChecksumFailurePayload = "CK_FAIL";

    /// <summary>
    /// OnStepX checksum: sum of the bytes modulo 256, in two uppercase
    /// hexadecimal digits.
    /// </summary>
    public static string Checksum(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        byte sum = 0;
        foreach (char c in payload)
        {
            // The firmware sums bytes of an 8 bit char. Anything outside
            // ASCII makes no sense in this protocol.
            sum += (byte)c;
        }

        return sum.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the frame to send for a given payload.
    /// </summary>
    /// <param name="payload">
    /// Command and parameters, without delimiters. For example <c>GVP</c> or
    /// <c>SrHH:MM:SS</c>.
    /// </param>
    /// <param name="sequence">
    /// Sequence character, only used with <paramref name="useChecksum"/>.
    /// </param>
    /// <param name="useChecksum">Enables error correction.</param>
    public static string BuildRequest(string payload, char sequence, bool useChecksum)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // The firmware's parser discards space, LF and CR before storing
        // the character in the buffer, so a parameter containing them
        // would arrive mangled with no warning at all. Better to fail
        // here.
        foreach (char c in payload)
        {
            if (c is ' ' or '\n' or '\r')
            {
                throw new ArgumentException(
                    "The firmware discards space, LF and CR while parsing, so the " +
                    $"payload cannot contain them. Received: {Describe(payload)}",
                    nameof(payload));
            }
        }

        string frame = useChecksum
            ? string.Concat(";", payload, Checksum(payload), sequence.ToString(), "#")
            : string.Concat(":", payload, "#");

        if (frame.Length > MaxFrameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"The resulting frame takes up {frame.Length} characters and the " +
                $"firmware only accepts {MaxFrameLength}. Above that limit it " +
                "silently overwrites the last character.");
        }

        return frame;
    }

    /// <summary>
    /// Validates and unwraps a response with checksum.
    /// </summary>
    /// <param name="framed">
    /// Full response without the trailing <c>#</c>, that is
    /// <c>payload + XX + sequence</c>.
    /// </param>
    /// <param name="payload">Verified payload.</param>
    /// <param name="sequence">Sequence character received.</param>
    /// <returns>
    /// <c>true</c> if the checksum matches. If not, <paramref name="payload"/>
    /// is left empty.
    /// </returns>
    public static bool TryUnwrapChecksummedReply(
        string framed,
        out string payload,
        out char sequence)
    {
        payload = string.Empty;
        sequence = '\0';

        if (framed is null || framed.Length < 3)
        {
            // At least the two checksum digits and the sequence character
            // are needed.
            return false;
        }

        sequence = framed[^1];
        string expected = framed.Substring(framed.Length - 3, 2);
        string candidate = framed[..^3];

        if (!string.Equals(Checksum(candidate), expected, StringComparison.Ordinal))
        {
            sequence = '\0';
            return false;
        }

        payload = candidate;
        return true;
    }

    /// <summary>
    /// Indicates whether a payload is the firmware's retransmission
    /// request.
    /// </summary>
    /// <remarks>
    /// The firmware's own comment announces a response with checksum
    /// (<c>CK_FAILS#</c>) but the code path rewrites the buffer and loses
    /// the checksum frame, so in practice <c>CK_FAIL#</c> is what arrives.
    /// Both forms are accepted on purpose.
    /// </remarks>
    public static bool IsChecksumFailure(string payload) =>
        payload is not null &&
        payload.StartsWith(ChecksumFailurePayload, StringComparison.Ordinal);

    /// <summary>
    /// Generates the sequence of sequence characters. Characters that have
    /// meaning in the protocol are avoided so that a read offset does not
    /// get confused with a delimiter.
    /// </summary>
    public static char NextSequence(char current)
    {
        // Safe printable range, from 'a' to 'z'. Not ':', ';', '#', nor
        // uppercase hexadecimal digits, which could overlap with the
        // checksum itself while debugging.
        if (current < 'a' || current >= 'z')
        {
            return 'a';
        }

        return (char)(current + 1);
    }

    /// <summary>
    /// Readable representation of a frame for traces, with non printable
    /// characters escaped.
    /// </summary>
    public static string Describe(string frame)
    {
        if (string.IsNullOrEmpty(frame))
        {
            return "<empty>";
        }

        var sb = new StringBuilder(frame.Length + 8);
        foreach (char c in frame)
        {
            if (c < 0x20 || c > 0x7E)
            {
                sb.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:X2}");
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
