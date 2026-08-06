using ASCOM;
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging.Abstractions;
using OnStepX.Core.Configuration;
using OnStepX.Core.Simulation;
using OnStepX.Devices;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// The ASCOM switch device over the simulated controller's auxiliary features.
/// </summary>
/// <remarks>
/// The default simulated controller is a deliberately awkward one: a switch, an analog output,
/// a dew heater with a temperature sensor, a dew heater without one, an intervalometer and a
/// hidden switch. So the expected channel list is six entries drawn from four slots, and most
/// of what these tests check is what is <b>not</b> there.
/// </remarks>
public sealed class OnStepSwitchTests : DeviceTestBase
{
    private OnStepSwitch Create() => new(Connection, () => Settings, NullLoggerFactory.Instance);

    private OnStepSwitch Connected()
    {
        OnStepSwitch device = Create();
        device.Connected = true;

        return device;
    }

    [Fact]
    public void TheChannelListIsBuiltFromTheExposableSlotsOnly()
    {
        using OnStepSwitch device = Connected();

        // FANS, FLATPANEL, MAINDEW, MAINDEW DeltaT, GUIDEDEW. The intervalometer and the
        // hidden switch contribute nothing, and GUIDEDEW has no delta to report.
        Assert.Equal(5, device.MaxSwitch);

        Assert.Equal(
            ["FANS", "FLATPANEL", "MAINDEW", "MAINDEW DeltaT", "GUIDEDEW"],
            Enumerable.Range(0, device.MaxSwitch).Select(id => device.GetSwitchName((short)id)));
    }

    [Fact]
    public void ADewHeaterWithATemperatureSensorGetsADeltaChannel()
    {
        using OnStepSwitch device = Connected();

        Assert.Equal("MAINDEW DeltaT", device.GetSwitchName(3));
        Assert.False(device.CanWrite(3));
        Assert.Equal(4.5, device.GetSwitchValue(3));
    }

    [Fact]
    public void ADewHeaterWithoutATemperatureSensorGetsNoDeltaChannel()
    {
        using OnStepSwitch device = Connected();

        // A channel that could only ever answer "not a number" is worse than no channel.
        Assert.DoesNotContain(
            "GUIDEDEW DeltaT",
            Enumerable.Range(0, device.MaxSwitch).Select(id => device.GetSwitchName((short)id)));
    }

    [Fact]
    public void AnIntervalometerIsNotExposed()
    {
        using OnStepSwitch device = Connected();

        Assert.DoesNotContain(
            "SHUTTER",
            Enumerable.Range(0, device.MaxSwitch).Select(id => device.GetSwitchName((short)id)));
    }

    [Fact]
    public void AHiddenSwitchIsNotExposed()
    {
        using OnStepSwitch device = Connected();

        Assert.DoesNotContain(
            "BOOTPIN",
            Enumerable.Range(0, device.MaxSwitch).Select(id => device.GetSwitchName((short)id)));
    }

    [Fact]
    public void OnlyTheExposedSlotsArePolled()
    {
        using OnStepSwitch device = Connected();

        // Connecting reads every configured slot once, to find out what it is. After that the
        // loop must only ask about the slots that became channels, because the shared serial
        // line is the scarce resource here.
        Device.ReceivedCommands.Clear();

        device.InvalidateSnapshot();
        _ = device.GetSwitchValue(0);

        Assert.Contains("GXX1", Device.ReceivedCommands);
        Assert.Contains("GXX4", Device.ReceivedCommands);
        Assert.DoesNotContain("GXX5", Device.ReceivedCommands);
        Assert.DoesNotContain("GXX6", Device.ReceivedCommands);
        Assert.DoesNotContain("GXY0", Device.ReceivedCommands);
    }

    [Fact]
    public void ASwitchRangeIsZeroToOne()
    {
        using OnStepSwitch device = Connected();

        Assert.Equal(0, device.MinSwitchValue(0));
        Assert.Equal(1, device.MaxSwitchValue(0));
        Assert.Equal(1, device.SwitchStep(0));
        Assert.True(device.CanWrite(0));
    }

    [Fact]
    public void AnAnalogOutputRangeIsTheWholeByte()
    {
        using OnStepSwitch device = Connected();

        Assert.Equal(0, device.MinSwitchValue(1));
        Assert.Equal(255, device.MaxSwitchValue(1));
        Assert.Equal(96, device.GetSwitchValue(1));

        // Any power at all reads as on.
        Assert.True(device.GetSwitch(1));
    }

    [Fact]
    public void SwitchingASlotOnSendsTheCommandTheFirmwareExpects()
    {
        using OnStepSwitch device = Connected();

        device.SetSwitch(0, true);

        Assert.Contains("SXX1,V1", Device.ReceivedCommands);
        Assert.Equal(1, Device.Features[0].Value);
        Assert.True(device.GetSwitch(0));

        device.SetSwitch(0, false);

        Assert.Contains("SXX1,V0", Device.ReceivedCommands);
        Assert.False(device.GetSwitch(0));
    }

    [Fact]
    public void SwitchingAnAnalogOutputOnRunsItAtFullPower()
    {
        using OnStepSwitch device = Connected();

        device.SetSwitch(1, true);

        Assert.Equal(255, Device.Features[1].Value);
        Assert.Equal(255, device.GetSwitchValue(1));
    }

    [Fact]
    public void AnAnalogOutputTakesAnIntermediateValue()
    {
        using OnStepSwitch device = Connected();

        device.SetSwitchValue(1, 128);

        Assert.Contains("SXX2,V128", Device.ReceivedCommands);
        Assert.Equal(128, device.GetSwitchValue(1));
    }

    [Fact]
    public void ADewHeaterIsSwitchedByItsEnabledFlagAndNotByItsOutputValue()
    {
        using OnStepSwitch device = Connected();

        Assert.False(device.GetSwitch(4));

        device.SetSwitch(4, true);

        Assert.Contains("SXX4,V1", Device.ReceivedCommands);
        Assert.True(Device.Features[3].DewHeaterEnabled);
        Assert.True(device.GetSwitch(4));
    }

    [Fact]
    public void ARampTemperatureIsNotAChannel()
    {
        using OnStepSwitch device = Connected();

        // Deliberate. A client walking the list with SetSwitch(id, false) would write the
        // lowest possible ramp start and destroy the calibration, and would spend a non
        // volatile storage cell doing it.
        IEnumerable<string> names = Enumerable
            .Range(0, device.MaxSwitch)
            .Select(id => device.GetSwitchName((short)id));

        Assert.DoesNotContain(names, name => name.Contains("Zero", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("Span", StringComparison.Ordinal));
    }

    [Fact]
    public void AReadingCannotBeWritten()
    {
        using OnStepSwitch device = Connected();

        Assert.Throws<MethodNotImplementedException>(() => device.SetSwitch(3, true));
        Assert.Throws<MethodNotImplementedException>(() => device.SetSwitchValue(3, 2.0));
    }

    [Fact]
    public void AValueOutsideTheChannelRangeIsRefused()
    {
        using OnStepSwitch device = Connected();

        Assert.Throws<InvalidValueException>(() => device.SetSwitchValue(1, 256));
        Assert.Throws<InvalidValueException>(() => device.SetSwitchValue(1, -1));
        Assert.Throws<InvalidValueException>(() => device.SetSwitchValue(0, 2));
    }

    [Fact]
    public void AnIdentifierOutsideTheListIsRefused()
    {
        using OnStepSwitch device = Connected();

        Assert.Throws<InvalidValueException>(() => device.GetSwitch(-1));
        Assert.Throws<InvalidValueException>(() => device.GetSwitch(device.MaxSwitch));
        Assert.Throws<InvalidValueException>(() => device.GetSwitchName(99));
        Assert.Throws<InvalidValueException>(() => device.CanAsync(99));
    }

    [Fact]
    public void ANameCannotBeChangedFromAClient()
    {
        using OnStepSwitch device = Connected();

        Assert.Throws<MethodNotImplementedException>(() => device.SetSwitchName(0, "Fans"));
    }

    [Fact]
    public void ThereIsNoAsynchronousForm()
    {
        using OnStepSwitch device = Connected();

        Assert.False(device.CanAsync(0));
        Assert.Throws<MethodNotImplementedException>(() => device.SetAsync(0, true));
        Assert.Throws<MethodNotImplementedException>(() => device.SetAsyncValue(1, 100));
        Assert.Throws<MethodNotImplementedException>(() => device.StateChangeComplete(0));
        Assert.Throws<MethodNotImplementedException>(() => device.CancelAsync(0));
    }

    [Fact]
    public void ABrokenTemperatureSensorIsClampedIntoTheDeclaredRange()
    {
        using OnStepSwitch device = Connected();

        // A failed DS18B20 reports 85 degrees. The reading is real, but a value outside the
        // declared range breaks the ASCOM contract, so it is clamped rather than passed on.
        Device.Features[2].DeltaT = 85.0;
        device.InvalidateSnapshot();

        Assert.Equal(50.0, device.GetSwitchValue(3));
        Assert.InRange(device.GetSwitchValue(3), device.MinSwitchValue(3), device.MaxSwitchValue(3));
    }

    [Fact]
    public void DeviceStateCarriesEveryChannelAndAgreesWithTheProperties()
    {
        using OnStepSwitch device = Connected();

        List<StateValue> state = device.DeviceState;

        for (short id = 0; id < device.MaxSwitch; id++)
        {
            string suffix = id.ToString(System.Globalization.CultureInfo.InvariantCulture);

            StateValue value = Assert.Single(state, entry => entry.Name == "GetSwitchValue" + suffix);
            StateValue boolean = Assert.Single(state, entry => entry.Name == "GetSwitch" + suffix);

            Assert.Equal(device.GetSwitchValue(id), Assert.IsType<double>(value.Value));
            Assert.Equal(device.GetSwitch(id), Assert.IsType<bool>(boolean.Value));
        }

        Assert.Contains(state, entry => entry.Name == "TimeStamp");

        // CanAsync is false everywhere, and StateChangeComplete refuses to answer in that case,
        // so it must not appear here either.
        Assert.DoesNotContain(
            state,
            entry => entry.Name.StartsWith("StateChangeComplete", StringComparison.Ordinal));
    }

    [Fact]
    public void AFirmwareWithoutAuxiliaryFeaturesRefusesToConnect()
    {
        Device.FeaturesPresent = false;

        using OnStepSwitch device = Create();

        NotConnectedException error = Assert.Throws<NotConnectedException>(
            () => device.Connected = true);

        Assert.Contains("FEATURE1_PURPOSE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFirmwareWhereNoSlotCanBeExposedRefusesToConnect()
    {
        // Slots exist, and every one of them is something this device will not pretend to
        // control. Connecting and then answering nothing would be worse than refusing.
        foreach (SimulatedFeature slot in Device.Features)
        {
            slot.Purpose = FeaturePurpose.None;
        }

        Device.Features[0].Purpose = FeaturePurpose.HiddenSwitch;
        Device.Features[1].Purpose = FeaturePurpose.Intervalometer;

        using OnStepSwitch device = Create();

        Assert.Throws<NotConnectedException>(() => device.Connected = true);
    }

    [Fact]
    public void NothingCanBeReadBeforeConnecting()
    {
        using OnStepSwitch device = Create();

        Assert.Throws<NotConnectedException>(() => _ = device.MaxSwitch);
        Assert.Throws<NotConnectedException>(() => _ = device.GetSwitch(0));
        Assert.Throws<NotConnectedException>(() => _ = device.GetSwitchName(0));
    }

    [Fact]
    public void NothingCanBeReadAfterDisconnecting()
    {
        using OnStepSwitch device = Connected();

        device.Connected = false;

        Assert.Throws<NotConnectedException>(() => _ = device.MaxSwitch);
        Assert.Throws<NotConnectedException>(() => _ = device.GetSwitchValue(0));
    }

    [Fact]
    public void TwoSlotsSharingANameAreStillTellableApart()
    {
        Device.Features[0].Name = "HEATER";
        Device.Features[1].Name = "HEATER";

        using OnStepSwitch device = Connected();

        Assert.Equal("HEATER", device.GetSwitchName(0));
        Assert.Equal("HEATER (2)", device.GetSwitchName(1));
    }

    [Fact]
    public void ASlotWithNoConfiguredNameStillGetsOne()
    {
        Device.Features[0].Name = string.Empty;

        using OnStepSwitch device = Connected();

        Assert.Equal("Feature 1", device.GetSwitchName(0));
    }

    [Fact]
    public void PowerMonitoringDoesNotChangeWhatTheChannelsReport()
    {
        Device.PowerMonitoringPresent = true;

        using OnStepSwitch device = Connected();

        Assert.Equal(5, device.MaxSwitch);
        Assert.Equal(96, device.GetSwitchValue(1));
        Assert.Equal(4.5, device.GetSwitchValue(3));
    }
}
