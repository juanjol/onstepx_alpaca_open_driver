using OnStepX.Core.Configuration;
using OnStepX.Core.Protocol;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// Configuration service against the simulated controller.
/// </summary>
/// <remarks>
/// Two things matter more than the round trips: that a command this build does not have
/// reports itself as unsupported instead of as zero, and that the sign conventions come
/// out the right way round.
/// </remarks>
public class ControllerConfigurationTests : FakeDeviceTestBase
{
    private readonly ControllerConfiguration _configuration;
    private int _invalidations;

    public ControllerConfigurationTests() =>
        _configuration = new ControllerConfiguration(() => Channel, () => _invalidations++);

    [Fact]
    public async Task ReadsTheWholeMountConfigurationInOnePass()
    {
        MountConfiguration config = await _configuration.ReadMountAsync();

        Assert.True(config.Site.Latitude.IsSupported);
        Assert.Equal(40.4168, config.Site.Latitude.Value, precision: 2);

        Assert.Equal(OnStepMountType.Gem, config.MountType.Value);
        Assert.Equal(-10, config.Limits.HorizonDegrees.Value, precision: 1);
        Assert.Equal(90, config.Limits.OverheadDegrees.Value, precision: 1);
        Assert.True(config.Home.HasSensors.Value);
        Assert.True(config.Pec.IsSupported);
        Assert.NotEqual(string.Empty, config.StatusRaw);
    }

    [Fact]
    public async Task LongitudeIsReportedPositiveEastAndWrittenPositiveWest()
    {
        // The simulator holds Madrid as 3.7038 degrees west, in OnStep's own convention.
        SiteConfiguration site = await _configuration.ReadSiteAsync();

        Assert.Equal(-3.7038, site.Longitude.Value, precision: 2);

        // Writing a longitude east of Greenwich has to reach the firmware negated.
        await _configuration.WriteLongitudeAsync(12.5);

        Assert.Equal(-12.5, Device.Mount.LongitudeWestPositive, precision: 2);

        SiteConfiguration readBack = await _configuration.ReadSiteAsync();
        Assert.Equal(12.5, readBack.Longitude.Value, precision: 2);
    }

    [Fact]
    public async Task LatitudeAndElevationRoundTrip()
    {
        await _configuration.WriteLatitudeAsync(-33.45);
        await _configuration.WriteElevationAsync(1250);

        SiteConfiguration site = await _configuration.ReadSiteAsync();

        Assert.Equal(-33.45, site.Latitude.Value, precision: 2);
        Assert.Equal(1250, site.Elevation.Value, precision: 1);
    }

    [Fact]
    public async Task ClockIsWrittenAsLocalStandardTime()
    {
        var localStandard = new DateTime(2026, 8, 6, 22, 45, 30, DateTimeKind.Unspecified);

        await _configuration.WriteClockAsync(localStandard);

        SiteConfiguration site = await _configuration.ReadSiteAsync();

        Assert.True(site.LocalStandardTime.IsSupported);

        // The protocol carries whole seconds, so a second of slack is the format's own
        // resolution and not a tolerance for a wrong conversion.
        Assert.True(
            Math.Abs((site.LocalStandardTime.Value - localStandard).TotalSeconds) <= 1,
            $"Expected {localStandard} and got {site.LocalStandardTime.Value}.");
    }

    [Fact]
    public async Task UtcOffsetKeepsTheFirmwareSignConvention()
    {
        // OnStep stores the value to add to local time to reach UT1, so a site one hour
        // east of Greenwich is minus one and not plus one.
        await _configuration.WriteUtcOffsetAsync(-2);

        SiteConfiguration site = await _configuration.ReadSiteAsync();

        Assert.Equal(-2, site.UtcOffsetHours.Value, precision: 2);
    }

    [Fact]
    public async Task LimitsRoundTrip()
    {
        await _configuration.WriteHorizonLimitAsync(-5);
        await _configuration.WriteOverheadLimitAsync(85);
        await _configuration.WriteMeridianLimitEastAsync(15);
        await _configuration.WriteMeridianLimitWestAsync(20);

        LimitConfiguration limits = await _configuration.ReadLimitsAsync();

        Assert.Equal(-5, limits.HorizonDegrees.Value, precision: 1);
        Assert.Equal(85, limits.OverheadDegrees.Value, precision: 1);
        Assert.Equal(15, limits.MeridianEastMinutes.Value);
        Assert.Equal(20, limits.MeridianWestMinutes.Value);

        // Axis travel is read only and has to be reported, not invented.
        Assert.True(limits.Axis1MinimumDegrees.IsSupported);
        Assert.True(limits.Axis2MaximumDegrees.IsSupported);
    }

    [Theory]
    [InlineData(-40)]
    [InlineData(40)]
    public async Task AnOutOfRangeHorizonLimitIsRejectedBeforeItReachesTheMount(int degrees)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _configuration.WriteHorizonLimitAsync(degrees));

        Assert.DoesNotContain(
            Device.ReceivedCommands,
            c => c.StartsWith("Sh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompensationSetsTheModelBeforeTheAxisCount()
    {
        await _configuration.WriteCompensationAsync(TrackingCompensation.RefractionSingleAxis);

        MountStatus status = await Channel.GetStatusAsync();
        Assert.Equal(TrackingCompensation.RefractionSingleAxis, status.Compensation);

        // The order matters: selecting the model resets the axis count to dual, so the
        // model command has to be sent first or the single axis choice is lost.
        int model = Device.ReceivedCommands.ToList().FindLastIndex(c => c == "Tr");
        int axis = Device.ReceivedCommands.ToList().FindLastIndex(c => c == "T1");

        Assert.True(model >= 0 && axis > model);
    }

    [Fact]
    public async Task CompensationCanBeTurnedOff()
    {
        await _configuration.WriteCompensationAsync(TrackingCompensation.ModelDualAxis);
        await _configuration.WriteCompensationAsync(TrackingCompensation.None);

        MountStatus status = await Channel.GetStatusAsync();

        Assert.Equal(TrackingCompensation.None, status.Compensation);
    }

    [Fact]
    public async Task TrackingOffsetsUseTheFirmwareUnits()
    {
        await _configuration.WriteRightAscensionOffsetAsync(1.5);
        await _configuration.WriteDeclinationOffsetAsync(-0.25);

        MountStatus status = await Channel.GetStatusAsync();
        TrackingConfiguration tracking = await _configuration.ReadTrackingAsync(status);

        // Arcseconds per sidereal second, exactly as written. The factor of fifteen
        // between this and the ASCOM property belongs to the telescope device.
        Assert.Equal(1.5, tracking.RightAscensionOffset.Value, precision: 3);
        Assert.Equal(-0.25, tracking.DeclinationOffset.Value, precision: 3);
    }

    [Fact]
    public async Task BacklashRoundTripsIncludingZero()
    {
        await _configuration.WriteRightAscensionBacklashAsync(120);
        await _configuration.WriteDeclinationBacklashAsync(0);

        BacklashConfiguration backlash = await _configuration.ReadBacklashAsync();

        Assert.Equal(120, backlash.RightAscensionArcseconds.Value);

        // Zero is a real value here and must not be mistaken for an absent command.
        Assert.True(backlash.DeclinationArcseconds.IsSupported);
        Assert.Equal(0, backlash.DeclinationArcseconds.Value);
    }

    [Fact]
    public async Task MeridianFlipRoundTrips()
    {
        await _configuration.WriteAutoMeridianFlipAsync(true);
        await _configuration.WriteMeridianFlipHomeModeAsync(MeridianFlipHomeMode.PauseAtHome);
        await _configuration.WritePreferredPierSideAsync(PreferredPierSide.West);

        MountStatus status = await Channel.GetStatusAsync();
        MeridianConfiguration meridian = await _configuration.ReadMeridianAsync(status);

        Assert.True(meridian.AutoFlip);
        Assert.Equal(MeridianFlipHomeMode.PauseAtHome, meridian.HomeMode);
        Assert.Equal(PreferredPierSide.West, meridian.PreferredSide.Value);
    }

    [Fact]
    public async Task SlewPeriodIsRejectedBelowTheFastestTheFirmwareAllows()
    {
        SlewRateConfiguration rates = await _configuration.ReadSlewRateAsync();

        Assert.True(rates.FastestPeriodMicroseconds.IsSupported);

        double tooFast = rates.FastestPeriodMicroseconds.Value / 2;

        // A smaller period is a faster slew, and the firmware refuses to go beyond its
        // own limit. The refusal has to surface as an error and not be swallowed.
        await Assert.ThrowsAsync<OnStepCommandException>(
            () => _configuration.WriteSlewPeriodAsync(tooFast));
    }

    [Fact]
    public async Task SlewPeriodRoundTripsAndTheDerivedSpeedFollows()
    {
        await _configuration.WriteSlewPeriodAsync(200);

        SlewRateConfiguration rates = await _configuration.ReadSlewRateAsync();

        Assert.Equal(200, rates.CurrentPeriodMicroseconds.Value, precision: 1);
        Assert.True(rates.DegreesPerSecond.Value > 0);
    }

    [Fact]
    public async Task SlewPresetOneIsFasterThanPresetFive()
    {
        await _configuration.WriteSlewPresetAsync(1);
        SlewRateConfiguration fast = await _configuration.ReadSlewRateAsync();

        await _configuration.WriteSlewPresetAsync(5);
        SlewRateConfiguration slow = await _configuration.ReadSlewRateAsync();

        // The parameter scales the step period, so the mapping is inverted with respect
        // to the number: preset 1 is the fast one.
        Assert.True(
            fast.CurrentPeriodMicroseconds.Value < slow.CurrentPeriodMicroseconds.Value,
            "Preset 1 should give a shorter step period than preset 5.");
    }

    [Fact]
    public async Task HomeOffsetsRoundTrip()
    {
        await _configuration.WriteHomeOffsetAsync(1, 120);
        await _configuration.WriteHomeOffsetAsync(2, -45);
        await _configuration.WriteAutoHomeAtBootAsync(true);

        MountStatus status = await Channel.GetStatusAsync();
        HomeConfiguration home = await _configuration.ReadHomeAsync(status);

        Assert.Equal(120, home.Axis1OffsetArcseconds.Value);
        Assert.Equal(-45, home.Axis2OffsetArcseconds.Value);
        Assert.True(home.AutoHomeAtBoot);
    }

    [Fact]
    public async Task BuzzerStateComesFromTheStatusStringBecauseThereIsNoGetter()
    {
        await _configuration.WriteBuzzerAsync(true);
        Assert.True((await _configuration.ReadMountAsync()).BuzzerEnabled);

        await _configuration.WriteBuzzerAsync(false);
        Assert.False((await _configuration.ReadMountAsync()).BuzzerEnabled);
    }

    [Fact]
    public async Task PecPlaybackNeedsARecordingFirst()
    {
        MountStatus status = await Channel.GetStatusAsync();
        PecConfiguration pec = await _configuration.ReadPecAsync(status);

        Assert.True(pec.IsSupported);
        Assert.Equal(PecState.Ignore, pec.State);

        // Asking for playback with an empty buffer leaves it idle rather than pretending
        // to correct anything.
        await _configuration.StartPecPlaybackAsync();

        status = await Channel.GetStatusAsync();
        Assert.Equal(PecState.Ignore, MountStatus.Parse(status.Raw).PecState);

        Device.Mount.PecRecorded = true;
        await _configuration.StartPecPlaybackAsync();

        status = await Channel.GetStatusAsync();
        Assert.Equal(PecState.Playing, status.PecState);
    }

    [Fact]
    public async Task PecReportsUnsupportedWhenTheBuildHasNoPec()
    {
        Device.Mount.PecSupported = false;

        MountStatus status = await Channel.GetStatusAsync();
        PecConfiguration pec = await _configuration.ReadPecAsync(status);

        // The difference between "no PEC in this firmware" and "PEC idle" only exists in
        // the status string, and it has to survive to the page.
        Assert.False(pec.IsSupported);
        Assert.Equal(PecState.Unknown, pec.State);
    }

    [Fact]
    public async Task WormStepsRoundTrip()
    {
        await _configuration.WriteWormStepsAsync(38400);

        MountStatus status = await Channel.GetStatusAsync();
        PecConfiguration pec = await _configuration.ReadPecAsync(status);

        Assert.Equal(38400, pec.WormSteps.Value);
        Assert.Equal(38400, pec.WormStepsStored.Value);
    }

    [Fact]
    public async Task MountTypeIsWrittenForTheNextRestart()
    {
        await _configuration.WriteMountTypeAsync(OnStepMountType.AltAzm);

        Assert.Equal(
            OnStepMountType.AltAzm,
            (await _configuration.ReadMountTypeAsync()).Value);
    }

    [Fact]
    public async Task ClearingNonVolatileStorageIsAcknowledged()
    {
        await _configuration.ClearNonVolatileStorageAsync();

        Assert.True(Device.Mount.NonVolatileResetPending);
    }

    [Fact]
    public async Task EveryWriteInvalidatesTheDeviceSnapshots()
    {
        _invalidations = 0;

        await _configuration.WriteBuzzerAsync(true);
        await _configuration.WriteAutoHomeAtBootAsync(false);
        await _configuration.WriteMeridianLimitEastAsync(30);

        // A write the polling loop knows nothing about would otherwise keep serving the
        // old value to a connected client for up to a poll interval.
        Assert.Equal(3, _invalidations);
    }

    // Absent commands

    [Fact]
    public async Task AnAbsentCommandIsReportedUnsupportedRatherThanZero()
    {
        // A build with no horizon or overhead limit compiled in.
        Device.UnsupportedCommands.Add("Gh");
        Device.UnsupportedCommands.Add("Go");
        Device.UnsupportedCommands.Add("GX92");

        LimitConfiguration limits = await _configuration.ReadLimitsAsync();
        SlewRateConfiguration rates = await _configuration.ReadSlewRateAsync();

        // These carry a sign or a degree mark when they are real, so the bare 0 of the
        // failure reply is unmistakable.
        Assert.False(limits.HorizonDegrees.IsSupported);
        Assert.False(limits.OverheadDegrees.IsSupported);
        Assert.False(rates.CurrentPeriodMicroseconds.IsSupported);

        // And the raw reply is kept, so an unexpected answer stays diagnosable.
        Assert.Equal("0", limits.HorizonDegrees.Raw);
    }

    [Fact]
    public async Task AZeroFromAPlainIntegerFieldIsReportedAsTheValueZero()
    {
        // Pinning the known ambiguity rather than pretending it away. :GXE9# prints a
        // plain integer, so zero minutes of meridian limit and the failure reply of a
        // firmware without meridian limits are the same three bytes on the wire.
        //
        // The field therefore reports what the firmware said. Erring the other way would
        // hide a legitimate zero backlash, which is the common case, behind a
        // "not supported" that the user cannot act on.
        Device.UnsupportedCommands.Add("GXE9");

        LimitConfiguration limits = await _configuration.ReadLimitsAsync();

        Assert.True(limits.MeridianEastMinutes.IsSupported);
        Assert.Equal(0, limits.MeridianEastMinutes.Value);
    }

    [Fact]
    public async Task AnAbsentTextCommandIsReportedUnsupported()
    {
        Device.UnsupportedCommands.Add("GX96");

        MountStatus status = await Channel.GetStatusAsync();
        MeridianConfiguration meridian = await _configuration.ReadMeridianAsync(status);

        Assert.False(meridian.PreferredSide.IsSupported);
    }

    [Fact]
    public async Task ReadingTheMountSurvivesAFirmwareMissingHalfTheCommands()
    {
        foreach (string command in new[]
                 {
                     "GXE9", "GXEA", "GXEe", "GXEw", "GXEB", "GXEC", "GXED",
                     "GX92", "GX93", "GX99", "GX97", "GX96", "h?", "%BR", "%BD",
                     "GXTR", "GXTD", "Gv", "W?",
                 })
        {
            Device.UnsupportedCommands.Add(command);
        }

        MountConfiguration config = await _configuration.ReadMountAsync();

        // Nothing throws, and every field the firmware marks unmistakably says so
        // instead of reading as zero.
        Assert.False(config.SlewRate.CurrentPeriodMicroseconds.IsSupported);
        Assert.False(config.SlewRate.DegreesPerSecond.IsSupported);
        Assert.False(config.Limits.Axis1MinimumDegrees.IsSupported);
        Assert.False(config.Meridian.PreferredSide.IsSupported);
        Assert.False(config.Home.HasSensors.IsSupported);
        Assert.False(config.Site.Elevation.IsSupported);
        Assert.False(config.Tracking.RightAscensionOffset.IsSupported);

        // What is still there is still read.
        Assert.True(config.Site.Latitude.IsSupported);
        Assert.Equal(OnStepMountType.Gem, config.MountType.Value);
    }

    [Fact]
    public async Task MountTypeZeroCountsAsAbsentBecauseThereIsNoSuchType()
    {
        Device.UnsupportedCommands.Add("GXEM");

        FirmwareValue<OnStepMountType> type = await _configuration.ReadMountTypeAsync();

        Assert.False(type.IsSupported);
    }
}

/// <summary>Focuser, rotator and sensor configuration.</summary>
public class ControllerConfigurationAccessoryTests : FakeDeviceTestBase
{
    private readonly ControllerConfiguration _configuration;

    public ControllerConfigurationAccessoryTests() =>
        _configuration = new ControllerConfiguration(() => Channel);

    [Fact]
    public async Task FocuserConfigurationIsReadInStepsAndNotMicrons()
    {
        await Channel.RequireTrueAsync("Fs10000");
        Clock.AdvanceSeconds(60);

        FocuserConfiguration focuser = await _configuration.ReadFocuserAsync();

        // The simulator's factor is 1.13507 microns per step, deliberately not 1, so
        // reading the microns command by mistake gives a visibly different number.
        Assert.Equal(10000, focuser.PositionSteps.Value);
        Assert.Equal(1.13507, focuser.MicronsPerStep.Value, precision: 5);
        Assert.True(focuser.IsPresent.Value);
    }

    [Fact]
    public async Task FocuserBacklashAndDeadbandAreWrittenInSteps()
    {
        await _configuration.WriteFocuserBacklashAsync(37);
        await _configuration.WriteFocuserDeadbandAsync(12);

        FocuserConfiguration focuser = await _configuration.ReadFocuserAsync();

        Assert.Equal(37, focuser.BacklashSteps.Value);
        Assert.Equal(12, focuser.DeadbandSteps.Value);

        // Reading the same values through the microns commands has to give the scaled
        // number, which is what proves the lowercase forms were used.
        Assert.Equal(42, await Channel.GetInt64Async("FB"));
    }

    [Fact]
    public async Task FocuserCompensationRoundTrips()
    {
        await _configuration.WriteFocuserCoefficientAsync(-12.5);
        await _configuration.WriteFocuserTemperatureCompensationAsync(true);
        await _configuration.WriteFocuserDcPowerAsync(75);

        FocuserConfiguration focuser = await _configuration.ReadFocuserAsync();

        Assert.Equal(-12.5, focuser.Coefficient.Value, precision: 2);
        Assert.True(focuser.TemperatureCompensation.Value);
        Assert.Equal(75, focuser.DcMotorPowerPercent.Value);
    }

    [Fact]
    public async Task RotatorConfigurationIsRead()
    {
        RotatorConfiguration rotator = await _configuration.ReadRotatorAsync();

        Assert.Equal(RotatorCapability.Derotate, rotator.Capability.Value);
        Assert.True(rotator.MinimumDegrees.IsSupported);
        Assert.True(rotator.MaximumDegrees.IsSupported);
        Assert.False(rotator.IsMoving.Value);
    }

    [Fact]
    public async Task RotatorBacklashRoundTrips()
    {
        await _configuration.WriteRotatorBacklashAsync(9);

        Assert.Equal(9, (await _configuration.ReadRotatorAsync()).BacklashSteps.Value);
    }

    [Fact]
    public async Task DerotationCanBeTurnedOnAndOff()
    {
        await _configuration.WriteDerotationAsync(true);
        Assert.True((await _configuration.ReadRotatorAsync()).IsDerotating.Value);

        await _configuration.WriteDerotationAsync(false);
        Assert.False((await _configuration.ReadRotatorAsync()).IsDerotating.Value);
    }

    [Fact]
    public async Task AbsentSensorsAreReportedAbsentAndNeverAsZero()
    {
        Device.Weather.HasTemperature = false;
        Device.Weather.HasHumidity = false;

        WeatherConfiguration weather = await _configuration.ReadWeatherAsync();

        Assert.False(weather.Temperature.IsSupported);
        Assert.False(weather.Humidity.IsSupported);

        // The dew point needs both, so it goes too. A believable zero here is what makes
        // a client close an observatory roof for no reason.
        Assert.False(weather.DewPoint.IsSupported);

        Assert.True(weather.Pressure.IsSupported);
    }

    [Fact]
    public async Task McuTemperatureOfZeroMeansThereIsNoSensor()
    {
        // The command reference documents a bare zero as the reply on a board with no
        // internal sensor, and no running microcontroller sits at exactly zero degrees.
        Device.Weather.McuTemperature = 0;

        Assert.False((await _configuration.ReadWeatherAsync()).McuTemperature.IsSupported);
    }

    [Fact]
    public async Task WeatherCanBePushedToTheController()
    {
        Device.Weather.HasTemperature = true;

        await _configuration.PushWeatherAsync(temperature: 5.5, pressure: 1013.2, humidity: 42);

        WeatherConfiguration weather = await _configuration.ReadWeatherAsync();

        Assert.Equal(5.5, weather.Temperature.Value, precision: 1);
        Assert.Equal(1013.2, weather.Pressure.Value, precision: 1);
        Assert.Equal(42, weather.Humidity.Value, precision: 1);
    }
}

/// <summary>Diagnostics telemetry.</summary>
public class ControllerDiagnosticsTests : FakeDeviceTestBase
{
    private readonly ControllerConfiguration _configuration;

    public ControllerDiagnosticsTests() =>
        _configuration = new ControllerConfiguration(() => Channel);

    [Fact]
    public async Task DiagnosticsCoverBothAxes()
    {
        ControllerDiagnostics diagnostics = await _configuration.ReadDiagnosticsAsync();

        Assert.Equal(OnStepMountType.Gem, diagnostics.MountType.Value);
        Assert.True(diagnostics.Axis1StepsPerDegree.Value > 0);
        Assert.True(diagnostics.Axis1InstrumentDegrees.IsSupported);
        Assert.Equal(2, diagnostics.DriverStatus.Count);
        Assert.Equal(2, diagnostics.StallGuard.Count);
        Assert.Equal(CommandError.None, diagnostics.LastError);
    }

    [Fact]
    public async Task StandstillIsNotAFault()
    {
        ControllerDiagnostics diagnostics = await _configuration.ReadDiagnosticsAsync();

        Assert.All(diagnostics.DriverStatus, status => Assert.False(status.HasFault));
    }

    [Fact]
    public async Task ADriverFaultIsFlagged()
    {
        Device.Mount.DriverAxis1.Flags = "ST,OT";

        ControllerDiagnostics diagnostics = await _configuration.ReadDiagnosticsAsync();

        AxisDriverStatus axis1 = diagnostics.DriverStatus.Single(s => s.Axis == 1);

        Assert.True(axis1.HasFault);
        Assert.Contains("OT", axis1.Flags);
    }

    [Fact]
    public async Task ADriverThatReportsNothingIsSimplyAbsent()
    {
        Device.Mount.DriverAxis2.ReportsStatus = false;
        Device.Mount.DriverAxis2.ReportsStallGuard = false;

        ControllerDiagnostics diagnostics = await _configuration.ReadDiagnosticsAsync();

        // A plain step and direction driver reports no status at all, and that is not an
        // error worth showing as a row of zeroes.
        Assert.Single(diagnostics.DriverStatus);
        Assert.Single(diagnostics.StallGuard);
        Assert.Equal(1, diagnostics.DriverStatus[0].Axis);
    }

    [Fact]
    public async Task StallGuardTelemetryIsParsedFieldByField()
    {
        Device.Mount.DriverAxis1.StallGuardValue = 150;
        Device.Mount.DriverAxis1.StallGuardTrip = 70;
        Device.Mount.DriverAxis1.StallGuardLatched = true;

        StallGuardStatus stall = (await _configuration.ReadDiagnosticsAsync())
            .StallGuard.Single(s => s.Axis == 1);

        Assert.Equal(150, stall.Value);
        Assert.Equal(70, stall.TripLevel);
        Assert.True(stall.Armed);
        Assert.True(stall.Latched);
    }

    [Fact]
    public async Task FirmwareIdentityIsReadable()
    {
        IReadOnlyDictionary<string, string> identity =
            await _configuration.ReadFirmwareIdentityAsync();

        Assert.Equal("On-Step", identity["Product"]);
        Assert.Equal("10.21b", identity["Version"]);
        Assert.True(identity.ContainsKey("Build date"));
    }

    [Fact]
    public async Task EncoderCountsFollowTheAxisPosition()
    {
        ControllerDiagnostics before = await _configuration.ReadDiagnosticsAsync();

        Device.Mount.Declination.SetPosition(Device.Mount.Declination.PositionAt(Clock.GetUtcNow()) - 10);

        ControllerDiagnostics after = await _configuration.ReadDiagnosticsAsync();

        Assert.NotEqual(before.Axis2EncoderCount.Value, after.Axis2EncoderCount.Value);
    }
}
