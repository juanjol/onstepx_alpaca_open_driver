using OnStepX.Core.Protocol;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// The algorithm is checked against the firmware code:
/// <c>src/lib/commands/BufferCmds.cpp</c> for the request and
/// <c>appendChecksum</c> from <c>src/libApp/commands/ProcessCmds.cpp</c> for
/// the response.
/// </summary>
public class OnStepFramingTests
{
    [Fact]
    public void NormalFrameWrapsWithColonAndHash()
    {
        Assert.Equal(":GVP#", OnStepFraming.BuildRequest("GVP", 'a', useChecksum: false));
    }

    [Fact]
    public void ChecksumMatchesFirmwareWorkedExample()
    {
        // The firmware itself documents the example ";111111CCS#".
        // '1' is 0x31, six times is 294, and 294 modulo 256 is 0x26.
        Assert.Equal("26", OnStepFraming.Checksum("111111"));
    }

    [Fact]
    public void ChecksummedFrameHasSemicolonPayloadChecksumSequenceAndHash()
    {
        Assert.Equal(";11111126a#", OnStepFraming.BuildRequest("111111", 'a', useChecksum: true));
    }

    [Fact]
    public void ChecksumOfEmptyPayloadIsZero()
    {
        Assert.Equal("00", OnStepFraming.Checksum(string.Empty));
    }

    [Theory]
    // Sums of ASCII bytes modulo 256.
    [InlineData("GVP", "ED")]   // 71 + 86 + 80 = 237 = 0xED
    [InlineData("GU", "9C")]    // 71 + 85 = 156 = 0x9C
    [InlineData("GR", "99")]    // 71 + 82 = 153 = 0x99
    [InlineData("1", "31")]     // 49 = 0x31
    public void ChecksumIsSumOfBytesModulo256(string payload, string expected)
    {
        // The expected value is recalculated here so the test documents
        // the algorithm instead of pinning opaque constants.
        byte sum = 0;
        foreach (char c in payload)
        {
            sum += (byte)c;
        }

        Assert.Equal(sum.ToString("X2"), OnStepFraming.Checksum(payload));
        Assert.Equal(expected, OnStepFraming.Checksum(payload));
    }

    [Fact]
    public void ChecksumWrapsAtByteBoundary()
    {
        // Two U+0080 characters sum to 0x100, which truncated to a byte
        // gives 0x00. They are written escaped on purpose: literals
        // would be invisible.
        Assert.Equal("00", OnStepFraming.Checksum("\u0080\u0080"));

        // "GVPGVP" sums to 474, which modulo 256 gives 218, that is 0xDA.
        Assert.Equal("DA", OnStepFraming.Checksum("GVPGVP"));
    }

    [Fact]
    public void MaxFrameLengthIsOneLessThanTheBuffer()
    {
        // Buffer::add clamps with cbp > bufferSize - 2, leaving index 78 as
        // the last usable one and 79 for the NUL. That is 79 characters, not 80.
        Assert.Equal(79, OnStepFraming.MaxFrameLength);
        Assert.Equal(80, OnStepFraming.CommandBufferSize);
    }

    [Fact]
    public void RequestLongerThanTheFirmwareLimitIsRejected()
    {
        // One character more than the maximum. The firmware would accept it
        // by silently overwriting the last one, which is exactly the failure
        // that must be avoided.
        string tooLong = new string('X', OnStepFraming.MaxFrameLength - 1);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => OnStepFraming.BuildRequest(tooLong, 'a', useChecksum: false));

        Assert.Contains("overwrites", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestExactlyAtTheFirmwareLimitIsAccepted()
    {
        // Normal frame: two delimiter characters plus the payload.
        string payload = new string('X', OnStepFraming.MaxFrameLength - 2);

        string frame = OnStepFraming.BuildRequest(payload, 'a', useChecksum: false);

        Assert.Equal(OnStepFraming.MaxFrameLength, frame.Length);
    }

    [Fact]
    public void ChecksumFrameLimitAccountsForItsFiveExtraCharacters()
    {
        // ';' + payload + XX + sequence + '#' is five frame characters plus
        // the payload, three more than the two of the normal frame.
        string payload = new string('X', OnStepFraming.MaxFrameLength - 5);

        string frame = OnStepFraming.BuildRequest(payload, 'a', useChecksum: true);
        Assert.Equal(OnStepFraming.MaxFrameLength, frame.Length);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => OnStepFraming.BuildRequest(payload + "X", 'a', useChecksum: true));
    }

    [Theory]
    [InlineData("SrHH MM SS")]
    [InlineData("GVP\n")]
    [InlineData("GVP\r")]
    public void PayloadWithCharactersTheFirmwareDiscardsIsRejected(string payload)
    {
        // The parser discards space, LF and CR before storing them, so the
        // command would arrive mangled with no warning at all.
        var ex = Assert.Throws<ArgumentException>(
            () => OnStepFraming.BuildRequest(payload, 'a', useChecksum: false));

        Assert.Equal("payload", ex.ParamName);
    }

    [Fact]
    public void ChecksummedReplyRoundTrips()
    {
        // A response with checksum is formed the same way as the request
        // but without the leading ';': payload + XX + sequence. Here it is
        // already passed without the '#'.
        const string ReplyPayload = "OnStep 10.21b";
        string framed = ReplyPayload + OnStepFraming.Checksum(ReplyPayload) + "q";

        bool ok = OnStepFraming.TryUnwrapChecksummedReply(framed, out string payload, out char seq);

        Assert.True(ok);
        Assert.Equal(ReplyPayload, payload);
        Assert.Equal('q', seq);
    }

    [Fact]
    public void ChecksummedBooleanReplyRoundTrips()
    {
        // With checksum enabled, even boolean responses carry a frame.
        string framed = "1" + OnStepFraming.Checksum("1") + "b";

        bool ok = OnStepFraming.TryUnwrapChecksummedReply(framed, out string payload, out char seq);

        Assert.True(ok);
        Assert.Equal("1", payload);
        Assert.Equal('b', seq);
    }

    [Fact]
    public void CorruptedChecksummedReplyIsRejected()
    {
        const string ReplyPayload = "OnStep 10.21b";
        string framed = ReplyPayload + OnStepFraming.Checksum(ReplyPayload) + "q";

        // A byte of the payload is altered without touching the checksum.
        string corrupted = framed.Replace("21b", "22b");

        bool ok = OnStepFraming.TryUnwrapChecksummedReply(corrupted, out string payload, out char seq);

        Assert.False(ok);
        Assert.Empty(payload);
        Assert.Equal('\0', seq);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("AB")]
    public void ReplyTooShortToCarryAChecksumIsRejected(string framed)
    {
        Assert.False(OnStepFraming.TryUnwrapChecksummedReply(framed, out _, out _));
    }

    [Fact]
    public void EmptyPayloadWithValidChecksumRoundTrips()
    {
        // Real edge case: with checksum enabled, a command that normally
        // returns nothing responds with an empty payload but a complete
        // frame.
        string framed = OnStepFraming.Checksum(string.Empty) + "z";

        bool ok = OnStepFraming.TryUnwrapChecksummedReply(framed, out string payload, out char seq);

        Assert.True(ok);
        Assert.Empty(payload);
        Assert.Equal('z', seq);
    }

    [Theory]
    [InlineData("CK_FAIL", true)]
    [InlineData("CK_FAILS", true)]
    [InlineData("1", false)]
    [InlineData("OnStep 10.21b", false)]
    public void ChecksumFailureIsDetected(string payload, bool expected)
    {
        Assert.Equal(expected, OnStepFraming.IsChecksumFailure(payload));
    }

    [Fact]
    public void SequenceCyclesInsideTheSafeRange()
    {
        char c = OnStepFraming.NextSequence('\0');
        Assert.Equal('a', c);

        // Full traversal and back to the start.
        var seen = new List<char>();
        for (int i = 0; i < 26; i++)
        {
            seen.Add(c);
            c = OnStepFraming.NextSequence(c);
        }

        Assert.Equal('a', c);
        Assert.Equal(26, seen.Distinct().Count());
        Assert.All(seen, x => Assert.InRange(x, 'a', 'z'));
    }

    [Fact]
    public void DescribeEscapesNonPrintableCharacters()
    {
        Assert.Equal(":\\x06" + "0#", OnStepFraming.Describe(":0#"));
        Assert.Equal("<empty>", OnStepFraming.Describe(string.Empty));
    }
}
