using OnStepX.Core.Protocol;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// The channel requests blocks from the transport and consumes characters
/// from its own buffer. That is needed for performance, but introduces a
/// risk: already read bytes left over after a transaction must be
/// discarded, or the next one would interpret them as its own response.
/// </summary>
public class OnStepChannelBufferingTests
{
    private static OnStepChannelOptions Plain() => new()
    {
        UseErrorCorrection = false,
        Timeout = TimeSpan.FromMilliseconds(300),
        MaxRetries = 0,
    };

    [Fact]
    public async Task WholeReplyArrivingInASingleBlockIsRead()
    {
        var t = new ScriptedTransport { MaxBytesPerRead = 256 };
        t.Reply("OnStep 10.21b#");

        await using var channel = new OnStepChannel(t, Plain());

        Assert.Equal("OnStep 10.21b", await channel.GetStringAsync("GVP"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(256)]
    public async Task ReplyIsReassembledWhateverTheBlockSize(int blockSize)
    {
        // Chunking depends on the system timer and the serial driver, so
        // the channel cannot assume any block size.
        var t = new ScriptedTransport { MaxBytesPerRead = blockSize };
        t.Reply("nNPHrzaKo440#");

        await using var channel = new OnStepChannel(t, Plain());

        MountStatus status = await channel.GetStatusAsync();

        Assert.Equal(ParkState.Parked, status.ParkState);
        Assert.Equal(MountKind.Fork, status.MountKind);
        Assert.Equal(4, status.PulseGuideRateSelect);
    }

    [Fact]
    public async Task LeftoverBytesFromAPreviousReplyDoNotLeakIntoTheNext()
    {
        // Real scenario: the firmware sends a lagging response and the
        // current one arrives stuck to it in the same block. Reading in
        // blocks, the channel ends up with "second#" in its internal
        // buffer. If it is not discarded when the next transaction
        // starts, it would return "second" instead of "third".
        //
        // Normal mode is used on purpose, with no sequence character: that
        // way a leak would not be caught by any other means, and the test
        // genuinely fails if the channel's buffer is not cleared.
        var t = new ScriptedTransport { MaxBytesPerRead = 256 };
        t.Reply("first#second#");
        t.Reply("third#");

        await using var channel = new OnStepChannel(t, Plain());

        Assert.Equal("first", await channel.GetStringAsync("GVP"));
        Assert.Equal("third", await channel.GetStringAsync("GVN"));
    }

    [Fact]
    public async Task DiscardingClearsBothTheTransportAndTheChannelBuffer()
    {
        var t = new ScriptedTransport { MaxBytesPerRead = 256 };
        t.Reply("one#two#three#");
        t.Reply("four#");

        await using var channel = new OnStepChannel(t, Plain());

        Assert.Equal("one", await channel.GetStringAsync("GVP"));

        // "two#three#" were left over, already read in the channel's
        // buffer. Neither must survive into the next transaction.
        Assert.Equal("four", await channel.GetStringAsync("GVN"));

        // Two transactions, two discards.
        Assert.Equal(2, t.DiscardCount);
    }

    [Fact]
    public async Task ManyTransactionsInARowStayInSyncWithBlockReads()
    {
        var t = new ScriptedTransport { MaxBytesPerRead = 64 };
        for (int i = 0; i < 50; i++)
        {
            t.Reply($"value{i}#");
        }

        await using var channel = new OnStepChannel(t, Plain());

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal($"value{i}", await channel.GetStringAsync("GVP"));
        }
    }

    [Fact]
    public async Task BlockReadsAlsoWorkInChecksumMode()
    {
        static string DeviceReply(string body, char seq) =>
            body + OnStepFraming.Checksum(body) + seq + "#";

        var t = new ScriptedTransport { MaxBytesPerRead = 256 };
        t.Reply(DeviceReply("OnStep 10.21b", 'a'));
        t.Reply(DeviceReply("1", 'b'));

        await using var channel = new OnStepChannel(t, new OnStepChannelOptions
        {
            UseErrorCorrection = true,
            Timeout = TimeSpan.FromMilliseconds(300),
            MaxRetries = 0,
        });

        Assert.Equal("OnStep 10.21b", await channel.GetStringAsync("GVP"));
        Assert.True(await channel.GetBoolAsync("Te"));
    }
}
