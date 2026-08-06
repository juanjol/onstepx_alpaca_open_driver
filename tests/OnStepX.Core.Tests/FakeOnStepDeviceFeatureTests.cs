using OnStepX.Core.Configuration;
using OnStepX.Core.Protocol;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// The simulated auxiliary features, exercised through the channel.
/// </summary>
/// <remarks>
/// These tests are about matching the firmware rather than about being convenient. Three of
/// them pin behaviour that looks like a bug and is not: a momentary switch reporting itself as
/// a plain switch, a hidden switch reporting itself present and then refusing to be read, and a
/// dew heater answering the literal <c>NAN</c>. If any of those ever starts looking tidy, the
/// simulator has stopped resembling the controller and the driver above it is being tested
/// against a fiction.
/// </remarks>
public class FakeDeviceFeatureTests : FakeDeviceTestBase
{
    [Fact]
    public async Task TheBitmapMarksConfiguredSlots()
    {
        // The default configuration fills slots 1 to 6 and leaves 7 and 8 alone.
        Assert.Equal("11111100", await Channel.GetStringAsync("GXY0"));
    }

    [Fact]
    public async Task AnUnconfiguredSlotIsAbsentFromTheBitmap()
    {
        Device.Features[0].Purpose = FeaturePurpose.None;

        Assert.Equal("01111100", await Channel.GetStringAsync("GXY0"));
    }

    [Fact]
    public async Task WithoutFeaturesCompiledInTheBitmapIsTheNumericFailure()
    {
        Device.FeaturesPresent = false;

        // Not an eight character bitmap, which is exactly how a consumer tells the two apart.
        Assert.Equal("0", await Channel.GetStringAsync("GXY0"));
    }

    [Fact]
    public async Task ASlotReportsItsNameAndPurpose()
    {
        Assert.Equal("FANS,1", await Channel.GetStringAsync("GXY1"));
        Assert.Equal("FLATPANEL,2", await Channel.GetStringAsync("GXY2"));
        Assert.Equal("MAINDEW,3", await Channel.GetStringAsync("GXY3"));
        Assert.Equal("SHUTTER,4", await Channel.GetStringAsync("GXY5"));
    }

    [Fact]
    public async Task AMomentaryOrCoverSwitchIsReportedAsAPlainSwitch()
    {
        Device.Features[0].Purpose = FeaturePurpose.MomentarySwitch;
        Device.Features[1].Purpose = FeaturePurpose.CoverSwitch;

        // The firmware flattens both before answering, so a driver cannot distinguish them
        // and a cover's "1 means closed" is invisible on the wire.
        Assert.Equal("FANS,1", await Channel.GetStringAsync("GXY1"));
        Assert.Equal("FLATPANEL,1", await Channel.GetStringAsync("GXY2"));
    }

    [Fact]
    public async Task AHiddenSwitchKeepsItsOwnPurpose()
    {
        // Unlike the momentary and cover switches, this one is not flattened.
        Assert.Equal("BOOTPIN,6", await Channel.GetStringAsync("GXY6"));
    }

    [Fact]
    public async Task AnUnconfiguredSlotAnswersTheNumericFailure()
    {
        Assert.Equal("0", await Channel.GetStringAsync("GXY7"));
    }

    [Fact]
    public async Task ANameLongerThanTenCharactersIsTruncated()
    {
        Device.Features[0].Name = "DEHUMIDIFIER";

        Assert.Equal("DEHUMIDIFI,1", await Channel.GetStringAsync("GXY1"));
    }

    [Fact]
    public async Task ASwitchReportsItsValue()
    {
        Assert.Equal("0", await Channel.GetStringAsync("GXX1"));

        Device.Features[0].Value = 1;

        Assert.Equal("1", await Channel.GetStringAsync("GXX1"));
    }

    [Fact]
    public async Task AnAnalogOutputReportsItsValue()
    {
        Assert.Equal("96", await Channel.GetStringAsync("GXX2"));
    }

    [Fact]
    public async Task ADewHeaterReportsEnabledZeroSpanAndDelta()
    {
        Assert.Equal("1,1.5,8.0,4.5", await Channel.GetStringAsync("GXX3"));
    }

    [Fact]
    public async Task ADewHeaterWithoutASensorReportsNanForItsDelta()
    {
        Assert.Equal("0,-5.0,15.0,NAN", await Channel.GetStringAsync("GXX4"));
    }

    [Fact]
    public async Task AnIntervalometerReportsItsCounters()
    {
        Assert.Equal("0,30,3.00,10", await Channel.GetStringAsync("GXX5"));
    }

    [Fact]
    public async Task AHiddenSwitchRefusesToReportItsState()
    {
        // The slot is present in the bitmap and names itself, and then this. It is the reason
        // a hidden switch must never become a channel.
        Assert.Equal("0", await Channel.GetStringAsync("GXX6"));
    }

    [Fact]
    public async Task AnUnconfiguredSlotHasNoState()
    {
        Assert.Equal("0", await Channel.GetStringAsync("GXX8"));
    }

    [Fact]
    public async Task ASwitchCanBeSwitchedOnAndOff()
    {
        Assert.True(await Channel.GetBoolAsync("SXX1,V1"));
        Assert.Equal("1", await Channel.GetStringAsync("GXX1"));

        Assert.True(await Channel.GetBoolAsync("SXX1,V0"));
        Assert.Equal("0", await Channel.GetStringAsync("GXX1"));
    }

    [Fact]
    public async Task ASwitchRefusesAValueAboveOne()
    {
        Assert.False(await Channel.GetBoolAsync("SXX1,V2"));
        Assert.Equal(CommandError.ParameterRange, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task AnAnalogOutputAcceptsTheWholeByteRange()
    {
        Assert.True(await Channel.GetBoolAsync("SXX2,V255"));
        Assert.Equal("255", await Channel.GetStringAsync("GXX2"));

        Assert.False(await Channel.GetBoolAsync("SXX2,V256"));
        Assert.Equal(CommandError.ParameterRange, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task ADewHeaterCanBeEnabledAndDisabled()
    {
        Assert.True(await Channel.GetBoolAsync("SXX4,V1"));
        Assert.Equal("1,-5.0,15.0,NAN", await Channel.GetStringAsync("GXX4"));

        Assert.True(await Channel.GetBoolAsync("SXX4,V0"));
        Assert.Equal("0,-5.0,15.0,NAN", await Channel.GetStringAsync("GXX4"));
    }

    [Fact]
    public async Task ADewHeaterRampCanBeMoved()
    {
        Assert.True(await Channel.GetBoolAsync("SXX3,Z2.5"));
        Assert.True(await Channel.GetBoolAsync("SXX3,S9.5"));

        Assert.Equal("1,2.5,9.5,4.5", await Channel.GetStringAsync("GXX3"));
    }

    [Fact]
    public async Task ADewHeaterRampStartIsKeptBelowItsEnd()
    {
        // Writing a start above the end succeeds and then quietly is not the value that was
        // sent, which is why the setup page has to read the slot back after writing.
        Assert.True(await Channel.GetBoolAsync("SXX3,Z12.0"));

        Assert.Equal("1,7.9,8.0,4.5", await Channel.GetStringAsync("GXX3"));
    }

    [Fact]
    public async Task ADewHeaterRampEndIsKeptAboveItsStart()
    {
        Assert.True(await Channel.GetBoolAsync("SXX3,S0.5"));

        Assert.Equal("1,1.5,1.6,4.5", await Channel.GetStringAsync("GXX3"));
    }

    [Fact]
    public async Task ADewHeaterRampIsRefusedOutsideTheFirmwareRange()
    {
        Assert.False(await Channel.GetBoolAsync("SXX3,Z25.0"));
        Assert.Equal(CommandError.ParameterRange, await Channel.GetLastErrorAsync());

        Assert.False(await Channel.GetBoolAsync("SXX3,S-9.0"));
        Assert.Equal(CommandError.ParameterRange, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task ADewHeaterRefusesASelectorItDoesNotHave()
    {
        Assert.False(await Channel.GetBoolAsync("SXX3,E5.0"));
        Assert.Equal(CommandError.ParameterForm, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task AnIntervalometerAcceptsItsOwnSelectors()
    {
        Assert.True(await Channel.GetBoolAsync("SXX5,E0.5"));
        Assert.True(await Channel.GetBoolAsync("SXX5,D12.0"));
        Assert.True(await Channel.GetBoolAsync("SXX5,C25"));

        // The firmware prints fewer decimals the larger a duration gets, so a half second
        // exposure carries three and a twelve second delay carries one.
        Assert.Equal("0,0.500,12.0,25", await Channel.GetStringAsync("GXX5"));
    }

    [Fact]
    public async Task AnIntervalometerRefusesADelayBelowOneSecond()
    {
        Assert.False(await Channel.GetBoolAsync("SXX5,D0.5"));
        Assert.Equal(CommandError.ParameterRange, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task AHiddenSwitchAcceptsAWriteThatDoesNothing()
    {
        // Success, and no observable effect. This is the firmware's behaviour, and it is why a
        // client must never be given a hidden switch to write to: it would look like it worked.
        Assert.True(await Channel.GetBoolAsync("SXX6,V1"));
        Assert.Equal("0", await Channel.GetStringAsync("GXX6"));
    }

    [Fact]
    public async Task WritingAnUnconfiguredSlotFails()
    {
        Assert.False(await Channel.GetBoolAsync("SXX8,V1"));
        Assert.Equal(CommandError.CommandUnknown, await Channel.GetLastErrorAsync());
    }

    [Fact]
    public async Task WithoutFeaturesCompiledInEverySlotCommandFails()
    {
        Device.FeaturesPresent = false;

        Assert.Equal("0", await Channel.GetStringAsync("GXY1"));
        Assert.Equal("0", await Channel.GetStringAsync("GXX1"));
        Assert.False(await Channel.GetBoolAsync("SXX1,V1"));
    }

    [Fact]
    public async Task ASlotNumberOutsideTheRangeIsNotASlot()
    {
        Assert.Equal("0", await Channel.GetStringAsync("GXX9"));
        Assert.Equal("0", await Channel.GetStringAsync("GXY9"));
    }
}
