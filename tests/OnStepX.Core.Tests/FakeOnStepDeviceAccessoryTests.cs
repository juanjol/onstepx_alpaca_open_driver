using OnStepX.Core.Protocol;
using Xunit;

namespace OnStepX.Core.Tests;

public class FakeDeviceFocuserTests : FakeDeviceTestBase
{
    [Fact]
    public async Task PrimaryFocuserIsPresentAndSelected()
    {
        Assert.Equal("1", await Channel.GetStringAsync("Fa"));
        Assert.Equal("1", await Channel.GetStringAsync("FA"));
    }

    [Fact]
    public async Task SelectingAFocuserBeyondThePresentCountFails()
    {
        // Only one is present by default.
        Assert.False(await Channel.GetBoolAsync("FA3"));

        Device.FocuserCount = 3;
        Assert.True(await Channel.GetBoolAsync("FA3"));
        Assert.Equal("3", await Channel.GetStringAsync("FA"));
    }

    [Fact]
    public async Task EachFocuserKeepsItsOwnState()
    {
        Device.FocuserCount = 2;

        await Channel.RequireTrueAsync("FA1");
        await Channel.RequireTrueAsync("Fs1000");
        Clock.AdvanceSeconds(60);

        await Channel.RequireTrueAsync("FA2");
        await Channel.RequireTrueAsync("Fs5000");
        Clock.AdvanceSeconds(60);

        Assert.Equal(5000, await Channel.GetInt64Async("Fg"));

        await Channel.RequireTrueAsync("FA1");
        Assert.Equal(1000, await Channel.GetInt64Async("Fg"));
    }

    /// <summary>
    /// The most important focuser test.
    /// </summary>
    [Fact]
    public async Task UppercaseCommandsAreMicronsAndLowercaseAreSteps()
    {
        // The simulator's factor is 1.13507 microns per step, deliberately
        // different from 1: with a factor of 1, confusing the two scales
        // would go unnoticed.
        double micronsPerStep = await Channel.GetDoubleAsync("Fu");
        Assert.Equal(1.13507, micronsPerStep, precision: 5);

        await Channel.RequireTrueAsync("Fs10000");
        Clock.AdvanceSeconds(60);

        long steps = await Channel.GetInt64Async("Fg");
        long microns = await Channel.GetInt64Async("FG");

        Assert.Equal(10000, steps);
        Assert.Equal((long)Math.Round(10000 * micronsPerStep), microns);

        // And they are not equal, which is what gives away the confusion.
        Assert.NotEqual(steps, microns);
    }

    [Fact]
    public async Task MaximumPositionDiffersBetweenTheTwoScales()
    {
        long maxSteps = await Channel.GetInt64Async("Fm");
        long maxMicrons = await Channel.GetInt64Async("FM");

        Assert.Equal(77500, maxSteps);
        Assert.NotEqual(maxSteps, maxMicrons);

        // The limit in microns is the one in steps multiplied by the
        // factor. If a driver used :FM# for MaxStep, it would report a
        // travel range 13% larger than the real one.
        double factor = await Channel.GetDoubleAsync("Fu");
        Assert.Equal((long)Math.Round(maxSteps * factor), maxMicrons);
    }

    [Fact]
    public async Task AbsoluteGotoInStepsMovesOverTime()
    {
        await Channel.RequireTrueAsync("Fs20000");

        Assert.StartsWith("M", await Channel.GetStringAsync("FT"), StringComparison.Ordinal);

        Clock.AdvanceSeconds(60);

        Assert.StartsWith("S", await Channel.GetStringAsync("FT"), StringComparison.Ordinal);
        Assert.Equal(20000, await Channel.GetInt64Async("Fg"));
    }

    [Fact]
    public async Task GotoBeyondTheLimitsIsRejected()
    {
        Assert.False(await Channel.GetBoolAsync("Fs99999"));
        Assert.False(await Channel.GetBoolAsync("Fs-100"));
    }

    [Fact]
    public async Task HaltStopsTheFocuserWhereItIs()
    {
        await Channel.RequireTrueAsync("Fs70000");
        Clock.AdvanceSeconds(1);

        await Channel.SendAsync("FQ");

        long stopped = await Channel.GetInt64Async("Fg");

        Assert.StartsWith("S", await Channel.GetStringAsync("FT"), StringComparison.Ordinal);
        Assert.InRange(stopped, 1, 69999);

        // And it does not keep moving after stopping.
        Clock.AdvanceSeconds(60);
        Assert.Equal(stopped, await Channel.GetInt64Async("Fg"));
    }

    [Fact]
    public async Task RelativeGotoInStepsIsAppliedToTheCurrentPosition()
    {
        await Channel.RequireTrueAsync("Fs5000");
        Clock.AdvanceSeconds(60);

        await Channel.SendAsync("Fr1500");
        Clock.AdvanceSeconds(60);

        Assert.Equal(6500, await Channel.GetInt64Async("Fg"));
    }

    [Fact]
    public async Task ZeroResetsThePosition()
    {
        await Channel.RequireTrueAsync("Fs5000");
        Clock.AdvanceSeconds(60);

        await Channel.SendAsync("FZ");

        Assert.Equal(0, await Channel.GetInt64Async("Fg"));
    }

    [Fact]
    public async Task TemperatureCompensationRoundTrips()
    {
        Assert.Equal("0", await Channel.GetStringAsync("Fc"));

        await Channel.RequireTrueAsync("Fc1");
        Assert.Equal("1", await Channel.GetStringAsync("Fc"));

        await Channel.RequireTrueAsync("FC-2.50000");
        Assert.Equal(-2.5, await Channel.GetDoubleAsync("FC"), precision: 4);

        await Channel.RequireTrueAsync("Fc0");
        Assert.Equal("0", await Channel.GetStringAsync("Fc"));
    }

    [Fact]
    public async Task BacklashRoundTripsInBothScales()
    {
        await Channel.RequireTrueAsync("Fb250");

        Assert.Equal(250, await Channel.GetInt64Async("Fb"));

        double factor = await Channel.GetDoubleAsync("Fu");
        Assert.Equal((long)Math.Round(250 * factor), await Channel.GetInt64Async("FB"));
    }

    [Fact]
    public async Task DeadbandRoundTrips()
    {
        await Channel.RequireTrueAsync("Fd5");

        Assert.Equal(5, await Channel.GetInt64Async("Fd"));
    }

    [Fact]
    public async Task DcPowerIsRangeChecked()
    {
        await Channel.RequireTrueAsync("FP50");
        Assert.Equal(50, await Channel.GetInt64Async("FP"));

        Assert.False(await Channel.GetBoolAsync("FP150"));
    }

    [Fact]
    public async Task FocuserTemperatureIsReadable()
    {
        Assert.Equal(12.5, await Channel.GetDoubleAsync("Ft"), precision: 1);
    }
}

public class FakeDeviceRotatorTests : FakeDeviceTestBase
{
    [Fact]
    public async Task RotatorReportsItselfActiveAndCapable()
    {
        Assert.True(await Channel.GetBoolAsync("rA"));
        Assert.Equal("D", await Channel.GetStringAsync("GX98"));
    }

    [Fact]
    public async Task AbsentRotatorReportsNoCapability()
    {
        Device.RotatorPresent = false;

        Assert.Equal("N", await Channel.GetStringAsync("GX98"));
    }

    [Fact]
    public async Task AngleIsReportedInMechanicalDegrees()
    {
        Device.Rotator.Angle.SetPosition(45.5);

        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("rG"), out double angle));
        Assert.Equal(45.5, angle, precision: 2);
    }

    [Fact]
    public async Task LimitsAreReadable()
    {
        Assert.Equal(-180, await Channel.GetInt64Async("rI"));
        Assert.Equal(180, await Channel.GetInt64Async("rM"));
    }

    [Fact]
    public async Task AbsoluteGotoMovesOverTime()
    {
        await Channel.RequireTrueAsync("rS" + Lx200Format.FormatRotatorAngle(90.0));

        Assert.StartsWith("M", await Channel.GetStringAsync("rT"), StringComparison.Ordinal);

        Clock.AdvanceSeconds(120);

        Assert.StartsWith("S", await Channel.GetStringAsync("rT"), StringComparison.Ordinal);
        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("rG"), out double angle));
        Assert.Equal(90.0, angle, precision: 2);
    }

    [Fact]
    public async Task NegativeAngleRoundTrips()
    {
        await Channel.RequireTrueAsync("rS" + Lx200Format.FormatRotatorAngle(-90.0));
        Clock.AdvanceSeconds(120);

        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("rG"), out double angle));
        Assert.Equal(-90.0, angle, precision: 2);
    }

    [Fact]
    public async Task GotoBeyondTheLimitsIsRejected()
    {
        Assert.False(await Channel.GetBoolAsync("rS+200*00"));
        Assert.Equal(CommandError.ParameterRange, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task HaltStopsTheRotator()
    {
        await Channel.RequireTrueAsync("rS" + Lx200Format.FormatRotatorAngle(180.0));
        Clock.AdvanceSeconds(1);

        await Channel.SendAsync("rQ");

        Assert.StartsWith("S", await Channel.GetStringAsync("rT"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelativeMoveIsAppliedToTheCurrentAngle()
    {
        await Channel.RequireTrueAsync("rS" + Lx200Format.FormatRotatorAngle(30.0));
        Clock.AdvanceSeconds(120);

        await Channel.SendAsync("rr" + Lx200Format.FormatRotatorAngle(15.0));
        Clock.AdvanceSeconds(120);

        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("rG"), out double angle));
        Assert.Equal(45.0, angle, precision: 2);
    }

    [Fact]
    public async Task DerotationIsReflectedInTheStatusString()
    {
        Assert.DoesNotContain("D", await Channel.GetStringAsync("rT"), StringComparison.Ordinal);

        await Channel.SendAsync("r+");
        Assert.Contains("D", await Channel.GetStringAsync("rT"), StringComparison.Ordinal);

        await Channel.SendAsync("r-");
        Assert.DoesNotContain("D", await Channel.GetStringAsync("rT"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HalfTravelSetsTheMidpoint()
    {
        await Channel.SendAsync("rF");

        Assert.True(Lx200Format.TryParse(await Channel.GetStringAsync("rG"), out double angle));
        Assert.Equal(0.0, angle, precision: 2);
    }

    [Fact]
    public async Task BacklashRoundTrips()
    {
        await Channel.RequireTrueAsync("rb42");

        Assert.Equal(42, await Channel.GetInt64Async("rb"));
    }
}

public class FakeDeviceWeatherTests : FakeDeviceTestBase
{
    [Fact]
    public async Task AllSensorsPresentReturnValues()
    {
        Assert.Equal(14.2, await Channel.GetDoubleAsync("GX9A"), precision: 1);
        Assert.Equal(942.5, await Channel.GetDoubleAsync("GX9B"), precision: 1);
        Assert.Equal(61.0, await Channel.GetDoubleAsync("GX9C"), precision: 1);
    }

    [Fact]
    public async Task DewPointIsDerivedFromTemperatureAndHumidity()
    {
        double dewPoint = await Channel.GetDoubleAsync("GX9E");

        // At 14.2 degrees and 61% humidity, the dew point stays clearly
        // below ambient temperature but not far off.
        Assert.InRange(dewPoint, 5.0, 14.2);
    }

    [Fact]
    public async Task DewPointFollowsHumidityChanges()
    {
        double dry = await Channel.GetDoubleAsync("GX9E");

        await Channel.RequireTrueAsync("SX9C,95.0");
        double humid = await Channel.GetDoubleAsync("GX9E");

        Assert.True(humid > dry, "more humidity should raise the dew point");
    }

    /// <summary>
    /// The behavior that is key for ConformU and for client safety.
    /// </summary>
    [Fact]
    public async Task AnAbsentSensorReturnsFalseNotZero()
    {
        // Firmware without the sensor compiled in answers with the
        // boolean false, not with a value of zero. A driver that
        // interprets this as 0 mbar reports made up data, and with the
        // dew point that poisons clients' safety logic.
        //
        // The raw payload is compared on purpose. Using GetBoolAsync here
        // would be a vacuous test: it returns false for any response other
        // than "1", so it would pass just the same if the sensor answered
        // 942.5.
        string present = await Channel.ExecuteAsync("GX9B", ReplyKind.Terminated);
        Assert.Equal("942.5", present);

        Device.Weather.HasPressure = false;

        string absent = await Channel.ExecuteAsync("GX9B", ReplyKind.Terminated);
        Assert.Equal("0", absent);
    }

    [Fact]
    public async Task DewPointNeedsBothTemperatureAndHumidity()
    {
        string present = await Channel.ExecuteAsync("GX9E", ReplyKind.Terminated);
        Assert.NotEqual("0", present);

        Device.Weather.HasHumidity = false;

        Assert.Equal("0", await Channel.ExecuteAsync("GX9E", ReplyKind.Terminated));
    }

    [Fact]
    public async Task EachSensorCanBeAbsentIndependently()
    {
        Device.Weather.HasTemperature = false;

        Assert.Equal("0", await Channel.ExecuteAsync("GX9A", ReplyKind.Terminated));

        // Pressure and humidity keep answering.
        Assert.Equal("942.5", await Channel.ExecuteAsync("GX9B", ReplyKind.Terminated));
        Assert.Equal("61.0", await Channel.ExecuteAsync("GX9C", ReplyKind.Terminated));

        // Dew point needs temperature, so it falls along with it.
        Assert.Equal("0", await Channel.ExecuteAsync("GX9E", ReplyKind.Terminated));
    }

    [Fact]
    public async Task WeatherCanBePushedIntoTheController()
    {
        await Channel.RequireTrueAsync("SX9A,-3.5");
        await Channel.RequireTrueAsync("SX9B,1013.2");
        await Channel.RequireTrueAsync("SX9C,88.0");

        Assert.Equal(-3.5, await Channel.GetDoubleAsync("GX9A"), precision: 1);
        Assert.Equal(1013.2, await Channel.GetDoubleAsync("GX9B"), precision: 1);
        Assert.Equal(88.0, await Channel.GetDoubleAsync("GX9C"), precision: 1);
    }

    [Fact]
    public async Task NegativeTemperatureIsFormattedWithItsSign()
    {
        await Channel.RequireTrueAsync("SX9A,-12.5");

        string raw = await Channel.GetStringAsync("GX9A");

        Assert.StartsWith("-", raw, StringComparison.Ordinal);
        Assert.Equal(-12.5, await Channel.GetDoubleAsync("GX9A"), precision: 1);
    }

    [Fact]
    public async Task McuTemperatureIsReadable()
    {
        Assert.Equal(38.0, await Channel.GetDoubleAsync("GX9F"), precision: 0);
    }
}
