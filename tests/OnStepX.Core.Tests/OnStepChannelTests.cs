using OnStepX.Core.Protocol;
using Xunit;

namespace OnStepX.Core.Tests;

public class OnStepChannelPlainModeTests
{
    private static OnStepChannelOptions Plain(int retries = 0) => new()
    {
        UseErrorCorrection = false,
        Timeout = TimeSpan.FromMilliseconds(300),
        MaxRetries = retries,
        RetryDelay = TimeSpan.Zero,
    };

    [Fact]
    public async Task TerminatedReplyIsReadUntilHash()
    {
        var t = new ScriptedTransport().Reply("OnStep 10.21b#");
        await using var channel = new OnStepChannel(t, Plain());

        string reply = await channel.GetStringAsync("GVP");

        Assert.Equal("OnStep 10.21b", reply);
        Assert.Equal([":GVP#"], t.Written);
    }

    [Fact]
    public async Task BooleanReplyIsASingleCharacterWithoutTerminator()
    {
        // Without checksum, a boolean command returns a single character
        // and sends no '#'. Reading until the terminator would hang the
        // channel.
        var t = new ScriptedTransport().Reply("1");
        await using var channel = new OnStepChannel(t, Plain());

        Assert.True(await channel.GetBoolAsync("Te"));
    }

    [Fact]
    public async Task BooleanFalseIsRecognised()
    {
        var t = new ScriptedTransport().Reply("0");
        await using var channel = new OnStepChannel(t, Plain());

        Assert.False(await channel.GetBoolAsync("hP"));
    }

    [Fact]
    public async Task CommandWithoutReplyDoesNotWaitForAnything()
    {
        // :FQ# answers nothing in normal mode. If the channel tried to
        // read, this test would time out instead of completing.
        var t = new ScriptedTransport().ReplyNothing();
        await using var channel = new OnStepChannel(t, Plain());

        await channel.SendAsync("FQ");

        Assert.Equal([":FQ#"], t.Written);
    }

    [Fact]
    public async Task SingleDigitReplyIsReadAsOneCharacter()
    {
        var t = new ScriptedTransport().Reply("0");
        await using var channel = new OnStepChannel(t, Plain());

        Assert.Equal(GotoResult.Accepted, await channel.GetGotoResultAsync("MS"));
    }

    [Fact]
    public async Task GotoFailureCodeIsTranslated()
    {
        var t = new ScriptedTransport().Reply("4");
        await using var channel = new OnStepChannel(t, Plain());

        GotoResult result = await channel.GetGotoResultAsync("MS");

        Assert.Equal(GotoResult.MountParked, result);
        Assert.False(result.IsAccepted());
        Assert.Contains("parked", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoubleIsParsedWithInvariantCulture()
    {
        // The firmware always writes the decimal point. If the channel used
        // the system culture, on a Windows machine set to Spanish 1.13507
        // would be read as 113507.
        var t = new ScriptedTransport().Reply("1.13507#");
        await using var channel = new OnStepChannel(t, Plain());

        Assert.Equal(1.13507, await channel.GetDoubleAsync("Fu"), precision: 6);
    }

    [Fact]
    public async Task SignedIntegerIsParsed()
    {
        var t = new ScriptedTransport().Reply("+12345#");
        await using var channel = new OnStepChannel(t, Plain());

        Assert.Equal(12345, await channel.GetInt64Async("Fg"));
    }

    [Fact]
    public async Task NegativeIntegerIsParsed()
    {
        var t = new ScriptedTransport().Reply("-500#");
        await using var channel = new OnStepChannel(t, Plain());

        Assert.Equal(-500, await channel.GetInt64Async("Fg"));
    }

    [Fact]
    public async Task NonNumericReplyToANumericCommandThrows()
    {
        var t = new ScriptedTransport().Reply("garbage#");
        await using var channel = new OnStepChannel(t, Plain());

        await Assert.ThrowsAsync<OnStepProtocolException>(() => channel.GetDoubleAsync("Fu"));
    }

    [Fact]
    public async Task StatusIsParsedThroughTheChannel()
    {
        var t = new ScriptedTransport().Reply("NpEW250#");
        await using var channel = new OnStepChannel(t, Plain());

        MountStatus status = await channel.GetStatusAsync();

        Assert.True(status.IsTracking);
        Assert.Equal(MountKind.Gem, status.MountKind);
        Assert.Equal(PierSide.West, status.PierSide);
    }

    [Fact]
    public async Task CarriageReturnAndLineFeedAreStrippedFromReplies()
    {
        var t = new ScriptedTransport().Reply("OnStep\r\n 10.21b#");
        await using var channel = new OnStepChannel(t, Plain());

        Assert.Equal("OnStep 10.21b", await channel.GetStringAsync("GVP"));
    }

    [Fact]
    public async Task ReplyWithoutTerminatorEventuallyTimesOut()
    {
        var t = new ScriptedTransport().Reply("no terminator");
        await using var channel = new OnStepChannel(t, Plain());

        await Assert.ThrowsAsync<OnStepProtocolException>(() => channel.GetStringAsync("GVP"));
    }

    [Fact]
    public async Task InputIsDiscardedBeforeEveryTransaction()
    {
        // Essential to avoid reading the leftovers of a previous
        // transaction that timed out halfway through.
        var t = new ScriptedTransport().Reply("1").Reply("1");
        await using var channel = new OnStepChannel(t, Plain());

        await channel.GetBoolAsync("Te");
        await channel.GetBoolAsync("Td");

        Assert.Equal(2, t.DiscardCount);
    }
}

public class OnStepChannelChecksumModeTests
{
    private static OnStepChannelOptions WithChecksum(int retries = 0) => new()
    {
        UseErrorCorrection = true,
        Timeout = TimeSpan.FromMilliseconds(300),
        MaxRetries = retries,
        RetryDelay = TimeSpan.Zero,
    };

    /// <summary>
    /// Builds the response the way the firmware emits it with checksum:
    /// payload plus two hexadecimal digits plus the sequence character plus
    /// <c>#</c>.
    /// </summary>
    private static string DeviceReply(string body, char sequence) =>
        body + OnStepFraming.Checksum(body) + sequence + "#";

    [Fact]
    public async Task RequestUsesSemicolonFrameWithChecksumAndSequence()
    {
        var t = new ScriptedTransport().Reply(DeviceReply("OnStep 10.21b", 'a'));
        await using var channel = new OnStepChannel(t, WithChecksum());

        string reply = await channel.GetStringAsync("GVP");

        Assert.Equal("OnStep 10.21b", reply);
        Assert.Equal([";GVP" + OnStepFraming.Checksum("GVP") + "a#"], t.Written);
    }

    [Fact]
    public async Task SequenceAdvancesBetweenTransactions()
    {
        var t = new ScriptedTransport()
            .Reply(DeviceReply("1", 'a'))
            .Reply(DeviceReply("1", 'b'))
            .Reply(DeviceReply("1", 'c'));
        await using var channel = new OnStepChannel(t, WithChecksum());

        await channel.GetBoolAsync("Te");
        await channel.GetBoolAsync("Td");
        await channel.GetBoolAsync("Te");

        Assert.Equal(3, t.Written.Count);
        Assert.EndsWith("a#", t.Written[0], StringComparison.Ordinal);
        Assert.EndsWith("b#", t.Written[1], StringComparison.Ordinal);
        Assert.EndsWith("c#", t.Written[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BooleanRepliesCarryAFullFrameInChecksumMode()
    {
        // This is the advantage of error correction mode: even what would
        // be a stray character in normal mode arrives framed and
        // terminated in '#'.
        var t = new ScriptedTransport().Reply(DeviceReply("1", 'a'));
        await using var channel = new OnStepChannel(t, WithChecksum());

        Assert.True(await channel.GetBoolAsync("Te"));
    }

    [Fact]
    public async Task CommandsThatAreSilentInPlainModeStillReplyInChecksumMode()
    {
        // With checksum the firmware's condition is
        // strlen(reply) > 0 || buffer.checksum, so even :FQ# answers, with
        // an empty payload but a complete frame.
        var t = new ScriptedTransport().Reply(DeviceReply(string.Empty, 'a'));
        await using var channel = new OnStepChannel(t, WithChecksum());

        await channel.SendAsync("FQ");

        Assert.Single(t.Written);
    }

    [Fact]
    public async Task CorruptedReplyIsRetriedAndThenSucceeds()
    {
        var t = new ScriptedTransport()
            .Reply("OnStep 10.21bZZa#")                    // invalid checksum
            .Reply(DeviceReply("OnStep 10.21b", 'b'));     // the retry succeeds
        await using var channel = new OnStepChannel(t, WithChecksum(retries: 1));

        string reply = await channel.GetStringAsync("GVP");

        Assert.Equal("OnStep 10.21b", reply);
        Assert.Equal(2, t.Written.Count);
        Assert.Equal(1, channel.RetryCount);
    }

    [Fact]
    public async Task ChecksumFailureFromTheFirmwareTriggersRetransmission()
    {
        // When the firmware does not validate our checksum it asks for a
        // retransmission.
        var t = new ScriptedTransport()
            .Reply("CK_FAIL#")
            .Reply(DeviceReply("1", 'b'));
        await using var channel = new OnStepChannel(t, WithChecksum(retries: 1));

        Assert.True(await channel.GetBoolAsync("Te"));
        Assert.Equal(2, t.Written.Count);
    }

    [Fact]
    public async Task DesynchronisedSequenceIsDetectedAndRetried()
    {
        // The response to a previous transaction arrives. Without checking
        // the sequence, the channel would return data that does not
        // correspond to the command, which is the worst possible failure
        // because it goes unnoticed.
        var t = new ScriptedTransport()
            .Reply(DeviceReply("old value", 'z'))
            .Reply(DeviceReply("good value", 'b'));
        await using var channel = new OnStepChannel(t, WithChecksum(retries: 1));

        string reply = await channel.GetStringAsync("GVP");

        Assert.Equal("good value", reply);
    }

    [Fact]
    public async Task ExhaustedRetriesThrowWithTheOriginalCauseAttached()
    {
        var t = new ScriptedTransport()
            .Reply("garbageZZa#")
            .Reply("garbageZZb#")
            .Reply("garbageZZc#");
        await using var channel = new OnStepChannel(t, WithChecksum(retries: 2));

        var ex = await Assert.ThrowsAsync<OnStepProtocolException>(
            () => channel.GetStringAsync("GVP"));

        Assert.Equal("GVP", ex.Payload);
        Assert.Contains("3 attempts", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
        Assert.Equal(3, t.Written.Count);
    }

    [Fact]
    public async Task TimeoutIsRetriedAndReportedWhenExhausted()
    {
        var t = new ScriptedTransport().ReplyNothing().ReplyNothing();
        await using var channel = new OnStepChannel(t, WithChecksum(retries: 1));

        await Assert.ThrowsAsync<OnStepProtocolException>(() => channel.GetStringAsync("GVP"));

        Assert.Equal(2, t.Written.Count);
    }
}

public class OnStepChannelBehaviourTests
{
    [Fact]
    public async Task FailedBooleanCommandQueriesTheErrorCodeAndReportsIt()
    {
        var t = new ScriptedTransport()
            .Reply("0")     // the command returns false
            .Reply("09#");  // :GE# responds CE_PARKED
        await using var channel = new OnStepChannel(t, new OnStepChannelOptions
        {
            UseErrorCorrection = false,
            Timeout = TimeSpan.FromMilliseconds(300),
            MaxRetries = 0,
        });

        var ex = await Assert.ThrowsAsync<OnStepCommandException>(
            () => channel.RequireTrueAsync("MS"));

        Assert.Equal(CommandError.Parked, ex.Error);
        Assert.Contains("parked", ex.Message, StringComparison.Ordinal);
        Assert.Equal([":MS#", ":GE#"], t.Written);
    }

    [Fact]
    public async Task SuccessfulBooleanCommandDoesNotQueryTheErrorCode()
    {
        var t = new ScriptedTransport().Reply("1");
        await using var channel = new OnStepChannel(t, new OnStepChannelOptions
        {
            UseErrorCorrection = false,
            Timeout = TimeSpan.FromMilliseconds(300),
            MaxRetries = 0,
        });

        await channel.RequireTrueAsync("Te");

        Assert.Equal([":Te#"], t.Written);
    }

    [Fact]
    public async Task ConcurrentCallersAreSerialisedNotInterleaved()
    {
        // This is the property that allows the four ASCOM devices to share
        // a single port without an external hub.
        var t = new ScriptedTransport();
        for (int i = 0; i < 20; i++)
        {
            t.Reply($"value{i}#");
        }

        await using var channel = new OnStepChannel(t, new OnStepChannelOptions
        {
            UseErrorCorrection = false,
            Timeout = TimeSpan.FromSeconds(5),
            MaxRetries = 0,
        });

        Task<string>[] calls = Enumerable.Range(0, 20)
            .Select(_ => channel.GetStringAsync("GVP"))
            .ToArray();

        string[] results = await Task.WhenAll(calls);

        // Twenty commands written and twenty distinct responses, with no mixing.
        Assert.Equal(20, t.Written.Count);
        Assert.Equal(20, results.Distinct().Count());
        Assert.Equal(20, channel.TransactionCount);
    }

    [Fact]
    public async Task EmptyPayloadIsRejected()
    {
        var t = new ScriptedTransport();
        await using var channel = new OnStepChannel(t);

        await Assert.ThrowsAsync<ArgumentException>(() => channel.GetStringAsync(string.Empty));
    }

    [Fact]
    public async Task UsingAChannelAfterDisposeThrows()
    {
        var t = new ScriptedTransport();
        var channel = new OnStepChannel(t);

        await channel.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.GetStringAsync("GVP"));
    }

    [Fact]
    public async Task CallerCancellationIsNotSwallowedByTheRetryLoop()
    {
        var t = new ScriptedTransport().ReplyNothing();
        await using var channel = new OnStepChannel(t, new OnStepChannelOptions
        {
            UseErrorCorrection = false,
            Timeout = TimeSpan.FromSeconds(30),
            MaxRetries = 5,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => channel.GetStringAsync("GVP", cts.Token));

        // A single attempt: caller cancellation is not retried.
        Assert.Single(t.Written);
    }
}
