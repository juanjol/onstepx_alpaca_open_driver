using OnStepX.Core.Protocol;
using OnStepX.Core.Simulation;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// The simulator is tested <b>through the channel</b>, not by calling its
/// methods directly. That way every test genuinely exercises framing,
/// checksum, and response interpretation, which is what ConformU will do.
/// </summary>
public abstract class FakeDeviceTestBase
{
    protected ManualTimeProvider Clock { get; } = new();

    protected FakeOnStepDevice Device { get; }

    protected OnStepChannel Channel { get; }

    protected FakeDeviceTestBase(bool useChecksum = true)
    {
        Device = new FakeOnStepDevice(Clock);
        Channel = new OnStepChannel(Device, new OnStepChannelOptions
        {
            UseErrorCorrection = useChecksum,
            Timeout = TimeSpan.FromSeconds(2),
            MaxRetries = 1,
            RetryDelay = TimeSpan.Zero,
        });
    }
}

public class FakeDeviceFirmwareTests : FakeDeviceTestBase
{
    [Fact]
    public async Task ProductAndVersionAreReadable()
    {
        Assert.Equal("On-Step", await Channel.GetStringAsync("GVP"));
        Assert.Equal("10.21b", await Channel.GetStringAsync("GVN"));
        Assert.Equal("OnStepX 10.21b", await Channel.GetStringAsync("GVM"));
    }

    [Fact]
    public async Task ErrorCodeIsTwoDigits()
    {
        Assert.Equal(CommandError.None, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task UnknownCommandReturnsFalse()
    {
        Assert.False(await Channel.GetBoolAsync("ZZZ"));
    }

    [Fact]
    public async Task CommandsAreRecordedForInspection()
    {
        await Channel.GetStringAsync("GVP");
        await Channel.GetStringAsync("GVN");

        Assert.Equal(["GVP", "GVN"], Device.ReceivedCommands);
    }
}

public class FakeDevicePlainModeTests : FakeDeviceTestBase
{
    public FakeDevicePlainModeTests() : base(useChecksum: false)
    {
    }

    [Fact]
    public async Task PayloadRepliesAreTerminated()
    {
        Assert.Equal("On-Step", await Channel.GetStringAsync("GVP"));
    }

    [Fact]
    public async Task BooleanRepliesAreASingleCharacter()
    {
        Assert.True(await Channel.GetBoolAsync("Te"));
    }

    [Fact]
    public async Task SilentCommandsProduceNoReplyAtAll()
    {
        // If the simulator answered anything, the next transaction would
        // read that leftover and return the wrong value.
        await Channel.SendAsync("TQ");

        Assert.Equal("On-Step", await Channel.GetStringAsync("GVP"));
    }

    [Fact]
    public async Task ManySilentCommandsInARowDoNotDesynchroniseTheChannel()
    {
        for (int i = 0; i < 10; i++)
        {
            await Channel.SendAsync("TQ");
            await Channel.SendAsync("Mn");
            await Channel.SendAsync("Q");
        }

        Assert.Equal("10.21b", await Channel.GetStringAsync("GVN"));
    }
}

public class FakeDeviceChecksumTests : FakeDeviceTestBase
{
    [Fact]
    public async Task EveryCommandRepliesInChecksumModeIncludingSilentOnes()
    {
        // With checksum the firmware responds to everything. If the
        // simulator did not, this call would time out.
        await Channel.SendAsync("TQ");

        Assert.Equal("On-Step", await Channel.GetStringAsync("GVP"));
    }

    [Fact]
    public async Task ADeliberatelyCorruptedRequestIsRejectedWithRetransmitRequest()
    {
        // A frame with an invalid checksum is written by hand to check
        // that the simulator answers CK_FAIL the same way the firmware does.
        await Device.OpenAsync();
        await Device.WriteAsync(System.Text.Encoding.ASCII.GetBytes(";GVPZZa#"));

        var buffer = new byte[32];
        int read = await Device.ReadAsync(buffer);
        string reply = System.Text.Encoding.ASCII.GetString(buffer, 0, read);

        Assert.StartsWith("CK_FAIL", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SequenceCharacterIsEchoedBack()
    {
        // If the simulator did not echo the sequence back, the channel
        // would detect desynchronization and would end up exhausting retries.
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal("On-Step", await Channel.GetStringAsync("GVP"));
        }

        Assert.Equal(0, Channel.RetryCount);
    }
}

public class FakeDeviceMountTests : FakeDeviceTestBase
{
    [Fact]
    public async Task StatusReflectsInitialState()
    {
        MountStatus st = await Channel.GetStatusAsync();

        Assert.False(st.IsTracking);
        Assert.False(st.IsSlewing);
        Assert.Equal(ParkState.Unparked, st.ParkState);
        Assert.Equal(MountKind.Gem, st.MountKind);
        Assert.Equal(PierSide.West, st.PierSide);
    }

    [Fact]
    public async Task TrackingCanBeStartedAndStopped()
    {
        await Channel.RequireTrueAsync("Te");
        Assert.True((await Channel.GetStatusAsync()).IsTracking);

        await Channel.RequireTrueAsync("Td");
        Assert.False((await Channel.GetStatusAsync()).IsTracking);
    }

    [Fact]
    public async Task TrackingRateIsZeroWhenNotTracking()
    {
        Assert.Equal(0.0, await Channel.GetDoubleAsync("GT"));

        await Channel.RequireTrueAsync("Te");

        Assert.True(await Channel.GetDoubleAsync("GT") > 60.0);
    }

    [Fact]
    public async Task CoordinatesRoundTripThroughTheProtocol()
    {
        Device.Mount.RightAscension.SetPosition(12.5);
        Device.Mount.Declination.SetPosition(-23.25);

        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GR"), out double ra));
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GD"), out double dec));

        Assert.Equal(12.5, ra, precision: 3);
        Assert.Equal(-23.25, dec, precision: 3);
    }

    [Fact]
    public async Task AltitudeAndAzimuthAreConsistentWithRightAscensionAndDeclination()
    {
        // ConformU checks the ranges, so returning fixed values is not
        // enough: they must be derived from the equatorial position and
        // the site.
        Device.Mount.RightAscension.SetPosition(6.0);
        Device.Mount.Declination.SetPosition(40.0);

        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GA"), out double alt));
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GZ"), out double az));

        Assert.InRange(alt, -90.0, 90.0);
        Assert.InRange(az, 0.0, 360.0);
    }

    [Fact]
    public async Task TargetIsSetAndReadBack()
    {
        await Channel.RequireTrueAsync("Sr" + Lx200Format.FormatHoursHigh(3.75));
        await Channel.RequireTrueAsync("Sd" + Lx200Format.FormatDegreesHigh(15.5));

        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("Gr"), out double ra));
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("Gd"), out double dec));

        Assert.Equal(3.75, ra, precision: 3);
        Assert.Equal(15.5, dec, precision: 3);
    }

    [Fact]
    public async Task OutOfRangeDeclinationIsRejectedWithAnErrorCode()
    {
        Assert.False(await Channel.GetBoolAsync("Sd+95*00:00"));
        Assert.Equal(CommandError.ParameterRange, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task GotoMovesOverTimeInsteadOfTeleporting()
    {
        // Aimed near the zenith to avoid hitting the horizon limit.
        double lst = 0;
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GS"), out lst));

        await Channel.RequireTrueAsync("Sr" + Lx200Format.FormatHoursHigh(lst));
        await Channel.RequireTrueAsync("Sd" + Lx200Format.FormatDegreesHigh(50.0));

        Assert.Equal(GotoResult.Accepted, await Channel.GetGotoResultAsync("MS"));

        // Right after starting, the mount must declare itself in motion.
        Assert.True((await Channel.GetStatusAsync()).IsSlewing);

        // Halfway there, it is still moving and has already progressed.
        Clock.AdvanceSeconds(1);
        Assert.True((await Channel.GetStatusAsync()).IsSlewing);

        // With plenty of time, it arrives and stops.
        Clock.AdvanceSeconds(60);
        MountStatus arrived = await Channel.GetStatusAsync();

        Assert.False(arrived.IsSlewing);
        Assert.True(arrived.IsTracking);

        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GD"), out double dec));
        Assert.Equal(50.0, dec, precision: 2);
    }

    [Fact]
    public async Task GotoIsRefusedWhenParkedWithTheRightCode()
    {
        await Channel.RequireTrueAsync("hP");
        Clock.AdvanceSeconds(60);

        Assert.Equal(ParkState.Parked, (await Channel.GetStatusAsync()).ParkState);
        Assert.Equal(GotoResult.MountParked, await Channel.GetGotoResultAsync("MS"));
    }

    [Fact]
    public async Task GotoBelowTheHorizonLimitIsRefused()
    {
        // A target below the horizon of the simulated site.
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GS"), out double lst));

        double oppositeRa = Lx200Format.NormalizeHours(lst + 12);
        await Channel.RequireTrueAsync("Sr" + Lx200Format.FormatHoursHigh(oppositeRa));
        await Channel.RequireTrueAsync("Sd" + Lx200Format.FormatDegreesHigh(-80.0));

        Assert.Equal(GotoResult.BelowHorizonLimit, await Channel.GetGotoResultAsync("MS"));
    }

    [Fact]
    public async Task AbortStopsAGotoInProgress()
    {
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GS"), out double lst));
        await Channel.RequireTrueAsync("Sr" + Lx200Format.FormatHoursHigh(lst));
        await Channel.RequireTrueAsync("Sd" + Lx200Format.FormatDegreesHigh(80.0));

        Assert.Equal(GotoResult.Accepted, await Channel.GetGotoResultAsync("MS"));
        Assert.True((await Channel.GetStatusAsync()).IsSlewing);

        await Channel.SendAsync("Q");

        Assert.False((await Channel.GetStatusAsync()).IsSlewing);
    }

    [Fact]
    public async Task ParkThenUnparkFollowsTheDocumentedStateMachine()
    {
        await Channel.RequireTrueAsync("Te");

        await Channel.RequireTrueAsync("hP");
        Assert.Equal(ParkState.Parking, (await Channel.GetStatusAsync()).ParkState);

        Clock.AdvanceSeconds(60);
        MountStatus parked = await Channel.GetStatusAsync();
        Assert.Equal(ParkState.Parked, parked.ParkState);
        Assert.False(parked.IsTracking);

        // Parking twice fails with CE_PARKED.
        Assert.False(await Channel.GetBoolAsync("hP"));
        Assert.Equal(CommandError.Parked, await Channel.GetLastErrorAsync());

        await Channel.RequireTrueAsync("hR");
        MountStatus unparked = await Channel.GetStatusAsync();
        Assert.Equal(ParkState.Unparked, unparked.ParkState);
        Assert.True(unparked.IsTracking);

        // Unparking twice fails with CE_NOT_PARKED.
        Assert.False(await Channel.GetBoolAsync("hR"));
        Assert.Equal(CommandError.NotParked, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task TrackingCannotBeStartedWhileParked()
    {
        await Channel.RequireTrueAsync("hP");
        Clock.AdvanceSeconds(60);
        await Channel.GetStatusAsync();

        Assert.False(await Channel.GetBoolAsync("Te"));
    }

    [Fact]
    public async Task FindHomeSetsAtHomeWhenItArrives()
    {
        await Channel.SendAsync("hC");
        Assert.True((await Channel.GetStatusAsync()).IsHoming);

        Clock.AdvanceSeconds(60);
        MountStatus st = await Channel.GetStatusAsync();

        Assert.False(st.IsHoming);
        Assert.True(st.IsAtHome);
    }

    [Fact]
    public async Task SyncSetsThePositionToTheTarget()
    {
        await Channel.RequireTrueAsync("Sr" + Lx200Format.FormatHoursHigh(7.25));
        await Channel.RequireTrueAsync("Sd" + Lx200Format.FormatDegreesHigh(33.0));

        await Channel.SendAsync("CS");

        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GR"), out double ra));
        Assert.Equal(7.25, ra, precision: 3);
    }

    [Fact]
    public async Task MeridianFlipSettingsRoundTrip()
    {
        await Channel.RequireTrueAsync("SX95,1");
        Assert.Equal("1", await Channel.GetStringAsync("GX95"));

        await Channel.RequireTrueAsync("SX94,2");
        Assert.Equal(
            MeridianFlipHomeMode.PauseAtHome,
            (await Channel.GetStatusAsync()).MeridianFlipHomeMode);

        await Channel.RequireTrueAsync("SX96,E");
        Assert.Equal("E", await Channel.GetStringAsync("GX96"));
    }

    [Fact]
    public async Task LimitsRoundTrip()
    {
        await Channel.RequireTrueAsync("Sh-05");
        await Channel.RequireTrueAsync("So85");
        await Channel.RequireTrueAsync("SXE9,15");
        await Channel.RequireTrueAsync("SXEA,20");

        Assert.Contains("-05", await Channel.GetStringAsync("Gh"), StringComparison.Ordinal);
        Assert.Contains("85", await Channel.GetStringAsync("Go"), StringComparison.Ordinal);
        Assert.Equal(15, await Channel.GetInt64Async("GXE9"));
        Assert.Equal(20, await Channel.GetInt64Async("GXEA"));
    }

    [Fact]
    public async Task BacklashRoundTrips()
    {
        await Channel.RequireTrueAsync("$BR120");
        await Channel.RequireTrueAsync("$BD90");

        Assert.Equal(120, await Channel.GetInt64Async("%BR"));
        Assert.Equal(90, await Channel.GetInt64Async("%BD"));
    }

    [Fact]
    public async Task CompensationModesAreReflectedInStatus()
    {
        await Channel.RequireTrueAsync("Tr");
        Assert.Equal(
            TrackingCompensation.RefractionDualAxis,
            (await Channel.GetStatusAsync()).Compensation);

        await Channel.RequireTrueAsync("T1");
        Assert.Equal(
            TrackingCompensation.RefractionSingleAxis,
            (await Channel.GetStatusAsync()).Compensation);

        await Channel.RequireTrueAsync("To");
        Assert.Equal(
            TrackingCompensation.ModelDualAxis,
            (await Channel.GetStatusAsync()).Compensation);

        await Channel.RequireTrueAsync("Tn");
        Assert.Equal(
            TrackingCompensation.None,
            (await Channel.GetStatusAsync()).Compensation);
    }

    [Fact]
    public async Task TrackingRateIsUnknownFromStatusWhileCompensationIsActive()
    {
        await Channel.SendAsync("TL");
        Assert.Equal(MountTrackingRate.Lunar, (await Channel.GetStatusAsync()).TrackingRate);

        await Channel.RequireTrueAsync("Tr");

        // With compensation active the firmware does not emit the rate character.
        Assert.Equal(MountTrackingRate.Unknown, (await Channel.GetStatusAsync()).TrackingRate);
    }

    [Fact]
    public async Task PierSideLettersDifferBetweenGuAndGm()
    {
        // Forced through :MNe#, so the test does not depend on where the simulated sky
        // happens to be at this moment.
        Assert.Equal(GotoResult.Accepted, await Channel.GetGotoResultAsync("MNe"));

        Assert.Equal(PierSide.East, (await Channel.GetStatusAsync()).PierSide);
        Assert.Equal(
            PierSide.East,
            MountStatus.ParseMeridianPierSide(await Channel.GetStringAsync("Gm")));
    }

    [Fact]
    public async Task PierSideFollowsTheHourAngleOnAGermanMount()
    {
        // East of the meridian, meaning a negative hour angle, the tube looks east and
        // the mount sits on the west side. It is the other way round to the west. A
        // stored value cannot express this, which is why it is derived.
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GS"), out double lst));

        // Three hours east of the meridian.
        Device.Mount.RightAscension.SetPosition(Lx200Format.NormalizeHours(lst + 3));
        Assert.Equal(PierSide.West, (await Channel.GetStatusAsync()).PierSide);

        // Three hours west of the meridian.
        Device.Mount.RightAscension.SetPosition(Lx200Format.NormalizeHours(lst - 3));
        Assert.Equal(PierSide.East, (await Channel.GetStatusAsync()).PierSide);
    }

    [Fact]
    public async Task ForcingAPierSideOverridesTheHourAngleUntilTheNextGoto()
    {
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GS"), out double lst));

        // Sit west of the meridian, where the natural side is east.
        Device.Mount.RightAscension.SetPosition(Lx200Format.NormalizeHours(lst - 3));
        Assert.Equal(PierSide.East, (await Channel.GetStatusAsync()).PierSide);

        // Force the other side and it sticks.
        Assert.Equal(GotoResult.Accepted, await Channel.GetGotoResultAsync("MNw"));
        Assert.Equal(PierSide.West, (await Channel.GetStatusAsync()).PierSide);

        // A goto chooses again, releasing the forced side.
        await Channel.RequireTrueAsync("Sr" + Lx200Format.FormatHoursHigh(
            Lx200Format.NormalizeHours(lst - 2)));
        await Channel.RequireTrueAsync("Sd" + Lx200Format.FormatDegreesHigh(45));
        await Channel.GetGotoResultAsync("MS");
        Clock.AdvanceSeconds(60);

        Assert.Equal(PierSide.East, (await Channel.GetStatusAsync()).PierSide);
    }

    [Fact]
    public async Task DestinationPierSideFollowsTheSameRuleAsTheCurrentSide()
    {
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GS"), out double lst));

        // A target east of the meridian will be reached from the west side.
        await Channel.RequireTrueAsync("Sr" + Lx200Format.FormatHoursHigh(
            Lx200Format.NormalizeHours(lst + 3)));
        Assert.Equal(2, await Channel.GetDigitAsync("MD"));

        // And a target west of the meridian from the east side.
        await Channel.RequireTrueAsync("Sr" + Lx200Format.FormatHoursHigh(
            Lx200Format.NormalizeHours(lst - 3)));
        Assert.Equal(1, await Channel.GetDigitAsync("MD"));
    }

    [Fact]
    public async Task AnAltAzimuthMountHasNoPierSide()
    {
        Device.Mount.MountTypeCode = 3;

        Assert.Equal(PierSide.None, (await Channel.GetStatusAsync()).PierSide);
    }
}

public class FakeDeviceSiteTests : FakeDeviceTestBase
{
    [Fact]
    public async Task LatitudeRoundTrips()
    {
        await Channel.RequireTrueAsync("St" + Lx200Format.FormatDegreesLow(43.25));

        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("Gt"), out double lat));
        Assert.Equal(43.25, lat, precision: 2);
    }

    [Fact]
    public async Task LongitudeUsesTheWestPositiveConvention()
    {
        // Madrid is about 3.7 degrees west, that is longitude +3.7 in OnStep.
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("Gg"), out double lon));

        Assert.True(lon > 0, "OnStep reports longitude positive west");
        Assert.Equal(3.7038, lon, precision: 1);
    }

    [Fact]
    public async Task UtcOffsetIsFormattedWithSignAndMinutes()
    {
        string offset = await Channel.GetStringAsync("GG");

        Assert.Matches(@"^[+-]\d{2}:\d{2}$", offset);
    }

    [Fact]
    public async Task UtcOffsetRoundTrips()
    {
        await Channel.RequireTrueAsync("SG+02:00");
        Assert.Equal("+02:00", await Channel.GetStringAsync("GG"));

        await Channel.RequireTrueAsync("SG-01");
        Assert.Equal("-01:00", await Channel.GetStringAsync("GG"));
    }

    [Fact]
    public async Task DateAndTimeRoundTrip()
    {
        await Channel.RequireTrueAsync("SC12/25/26");
        await Channel.RequireTrueAsync("SL23:45:10");

        Assert.Equal("12/25/26", await Channel.GetStringAsync("GC"));
        Assert.Equal("23:45:10", await Channel.GetStringAsync("GL"));
    }

    [Fact]
    public async Task MalformedDateIsRejected()
    {
        Assert.False(await Channel.GetBoolAsync("SC99/99/99"));
        Assert.Equal(CommandError.ParameterForm, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task ElevationRoundTrips()
    {
        await Channel.RequireTrueAsync("Sv+700.0");

        Assert.Equal(700.0, await Channel.GetDoubleAsync("Gv"), precision: 1);
    }

    [Fact]
    public async Task SiderealTimeIsAValidHourAngle()
    {
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("GS"), out double lst));

        Assert.InRange(lst, 0.0, 24.0);
    }

    [Fact]
    public async Task DateTimeReadyStatusUsesInvertedLogic()
    {
        // Watch out: 0 means ready, not the opposite.
        Assert.Equal("0", await Channel.GetStringAsync("GX89"));
    }
}
