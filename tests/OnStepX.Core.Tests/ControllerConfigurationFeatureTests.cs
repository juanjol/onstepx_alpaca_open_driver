using OnStepX.Core.Configuration;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// Reading and writing the auxiliary feature slots.
/// </summary>
public sealed class ControllerConfigurationFeatureTests : FakeDeviceTestBase
{
    private readonly ControllerConfiguration _configuration;

    private int _invalidations;

    public ControllerConfigurationFeatureTests() =>
        _configuration = new ControllerConfiguration(() => Channel, () => _invalidations++);

    [Fact]
    public async Task OnlyConfiguredSlotsAreListed()
    {
        IReadOnlyList<FeatureSlot> slots = await _configuration.ReadFeatureSlotsAsync();

        Assert.Equal([1, 2, 3, 4, 5, 6], slots.Select(static slot => slot.Slot));
    }

    [Fact]
    public async Task EachSlotCarriesItsNameAndPurpose()
    {
        IReadOnlyList<FeatureSlot> slots = await _configuration.ReadFeatureSlotsAsync();

        Assert.Equal("FANS", slots[0].Name);
        Assert.Equal(FeaturePurpose.Switch, slots[0].Purpose);

        Assert.Equal("FLATPANEL", slots[1].Name);
        Assert.Equal(FeaturePurpose.AnalogOutput, slots[1].Purpose);

        Assert.Equal("MAINDEW", slots[2].Name);
        Assert.Equal(FeaturePurpose.DewHeater, slots[2].Purpose);

        Assert.Equal(FeaturePurpose.Intervalometer, slots[4].Purpose);
        Assert.Equal(FeaturePurpose.HiddenSwitch, slots[5].Purpose);
    }

    [Fact]
    public async Task AGapInTheMiddleIsSkippedWithoutShiftingTheRest()
    {
        Device.Features[2].Purpose = FeaturePurpose.None;

        IReadOnlyList<FeatureSlot> slots = await _configuration.ReadFeatureSlotsAsync();

        Assert.Equal([1, 2, 4, 5, 6], slots.Select(static slot => slot.Slot));
        Assert.Equal("GUIDEDEW", slots.Single(static slot => slot.Slot == 4).Name);
    }

    [Fact]
    public async Task AFirmwareWithoutFeaturesListsNothing()
    {
        Device.FeaturesPresent = false;

        Assert.Empty(await _configuration.ReadFeatureSlotsAsync());
    }

    [Fact]
    public async Task ANameContainingACommaDoesNotSwallowThePurpose()
    {
        // The firmware copies the configured name verbatim, so splitting on the first comma
        // rather than the last would read "DEW" as the name and fail to parse "MAIN,3".
        Device.Features[0].Name = "FANS,AUX";

        IReadOnlyList<FeatureSlot> slots = await _configuration.ReadFeatureSlotsAsync();

        Assert.Equal("FANS,AUX", slots[0].Name);
        Assert.Equal(FeaturePurpose.Switch, slots[0].Purpose);
    }

    [Fact]
    public async Task ASwitchStateIsItsValue()
    {
        Device.Features[0].Value = 1;

        FeatureState? state = await _configuration.ReadFeatureStateAsync(1, FeaturePurpose.Switch);

        Assert.NotNull(state);
        Assert.Equal(1, state.Value);
    }

    [Fact]
    public async Task ADewHeaterStateCarriesAllFourFields()
    {
        FeatureState? state = await _configuration
            .ReadFeatureStateAsync(3, FeaturePurpose.DewHeater);

        Assert.NotNull(state);
        Assert.True(state.DewHeaterEnabled);
        Assert.Equal(1.5, state.Zero);
        Assert.Equal(8.0, state.Span);
        Assert.Equal(4.5, state.DeltaT);
    }

    [Fact]
    public async Task AnUnavailableDeltaIsNullAndNotZero()
    {
        FeatureState? state = await _configuration
            .ReadFeatureStateAsync(4, FeaturePurpose.DewHeater);

        // Zero degrees above the dew point is the moment a heater matters most, so reading NAN
        // as zero would turn "no sensor" into the most alarming reading the device can give.
        Assert.NotNull(state);
        Assert.Null(state.DeltaT);
        Assert.Equal(-5.0, state.Zero);
    }

    [Fact]
    public async Task AnIntervalometerStateCarriesItsCounters()
    {
        FeatureState? state = await _configuration
            .ReadFeatureStateAsync(5, FeaturePurpose.Intervalometer);

        Assert.NotNull(state);
        Assert.Equal(0, state.CurrentCount);
        Assert.Equal(30.0, state.Exposure);
        Assert.Equal(3.0, state.Delay);
        Assert.Equal(10, state.Count);
    }

    [Fact]
    public async Task AHiddenSwitchStateIsEmptyRatherThanAValue()
    {
        FeatureState? state = await _configuration
            .ReadFeatureStateAsync(6, FeaturePurpose.HiddenSwitch);

        // The controller answered the numeric failure, which is a reply, so there is a state
        // record. It just has nothing in it, and the raw reply says why.
        Assert.NotNull(state);
        Assert.Null(state.Value);
        Assert.Equal("0", state.Raw);
    }

    [Fact]
    public async Task PowerTelemetryIsSetAsideRatherThanCountedAsAField()
    {
        // A build with power monitoring appends ";volts,amps,flags". Splitting the whole reply
        // on commas would make a dew heater look as if it had seven fields and would report the
        // supply voltage as the delta above the dew point.
        Device.PowerMonitoringPresent = true;

        FeatureState? state = await _configuration
            .ReadFeatureStateAsync(3, FeaturePurpose.DewHeater);

        Assert.NotNull(state);
        Assert.Equal(4.5, state.DeltaT);
        Assert.Equal("12.1,0.4,!!!!!", state.PowerTelemetry);
        Assert.Equal("1,1.5,8.0,4.5;12.1,0.4,!!!!!", state.Raw);
    }

    [Fact]
    public async Task PowerTelemetryDoesNotDisturbASwitchValue()
    {
        Device.PowerMonitoringPresent = true;
        Device.Features[1].Value = 200;

        FeatureState? state = await _configuration
            .ReadFeatureStateAsync(2, FeaturePurpose.AnalogOutput);

        Assert.NotNull(state);
        Assert.Equal(200, state.Value);
    }

    [Fact]
    public async Task ACommandTheFirmwareDoesNotHaveLeavesNoStateBehind()
    {
        Device.UnsupportedCommands.Add("GXX3");

        FeatureState? state = await _configuration
            .ReadFeatureStateAsync(3, FeaturePurpose.DewHeater);

        // The controller answered the numeric failure, which is still a reply, so there is a
        // record. Every field is null and the raw reply says why.
        Assert.NotNull(state);
        Assert.Equal("0", state.Raw);
        Assert.Null(state.DeltaT);
        Assert.Null(state.Zero);
    }

    [Fact]
    public async Task SeveralSlotsCanBeReadAtOnce()
    {
        IReadOnlyList<FeatureSlot> slots = await _configuration.ReadFeatureSlotsAsync();

        IReadOnlyDictionary<int, FeatureState> states = await _configuration
            .ReadFeatureStatesAsync(slots);

        Assert.Equal(6, states.Count);
        Assert.Equal(96, states[2].Value);
        Assert.Equal(4.5, states[3].DeltaT);
    }

    [Fact]
    public async Task WritingAValueReachesTheControllerAndMarksTheCachesStale()
    {
        _invalidations = 0;

        await _configuration.WriteFeatureValueAsync(2, 200);

        Assert.Equal(200, Device.Features[1].Value);
        Assert.Equal(1, _invalidations);
    }

    [Fact]
    public async Task WritingADewHeaterEnableReachesTheController()
    {
        await _configuration.WriteDewHeaterEnabledAsync(4, enabled: true);

        Assert.True(Device.Features[3].DewHeaterEnabled);
        Assert.Contains("SXX4,V1", Device.ReceivedCommands);
    }

    [Fact]
    public async Task WritingARampSendsAnInvariantDecimal()
    {
        await _configuration.WriteDewHeaterZeroAsync(3, -2.5);
        await _configuration.WriteDewHeaterSpanAsync(3, 6.5);

        // A comma here instead of a point would be refused by the firmware's strtof, and the
        // machine's culture must not decide that.
        Assert.Contains("SXX3,Z-2.5", Device.ReceivedCommands);
        Assert.Contains("SXX3,S6.5", Device.ReceivedCommands);

        Assert.Equal(-2.5, Device.Features[2].Zero);
        Assert.Equal(6.5, Device.Features[2].Span);
    }

    [Fact]
    public async Task ARampWriteIsRejectedBeforeItReachesTheController()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _configuration.WriteDewHeaterZeroAsync(3, 25.0));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _configuration.WriteDewHeaterSpanAsync(3, -6.0));

        Assert.DoesNotContain(
            Device.ReceivedCommands,
            static command => command.StartsWith("SXX3,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AValueOutsideTheByteRangeIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _configuration.WriteFeatureValueAsync(2, 256));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _configuration.WriteFeatureValueAsync(2, -1));
    }

    [Theory]
    [InlineData(FeaturePurpose.Switch, true)]
    [InlineData(FeaturePurpose.MomentarySwitch, true)]
    [InlineData(FeaturePurpose.CoverSwitch, true)]
    [InlineData(FeaturePurpose.AnalogOutput, true)]
    [InlineData(FeaturePurpose.DewHeater, true)]
    [InlineData(FeaturePurpose.Intervalometer, false)]
    [InlineData(FeaturePurpose.HiddenSwitch, false)]
    [InlineData(FeaturePurpose.None, false)]
    public void WhichPurposesCanBeControlledIsPinned(FeaturePurpose purpose, bool controllable)
    {
        // Two consumers depend on this answer, the Switch device that hides a slot and the setup
        // page that explains why, so it is stated once and pinned here. Widening it silently is
        // how a client ends up with a channel that reports an error on every read.
        Assert.Equal(controllable, purpose.IsControllable());
    }

    [Fact]
    public void EveryUncontrollablePurposeExplainsItself()
    {
        foreach (FeaturePurpose purpose in Enum.GetValues<FeaturePurpose>())
        {
            if (purpose.IsControllable())
            {
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(purpose.UncontrollableReason()));
        }
    }

    [Fact]
    public async Task ASlotNumberOutsideTheRangeIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _configuration.WriteFeatureValueAsync(9, 1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _configuration.ReadFeatureStateAsync(0, FeaturePurpose.Switch));
    }
}
