using System.Globalization;
using OnStepX.Core.Astronomy;
using OnStepX.Core.Protocol;

namespace OnStepX.Core.Simulation;

/// <summary>
/// Mount commands of the simulated device.
/// </summary>
public sealed partial class FakeOnStepDevice
{
    /// <summary>
    /// Longitude with the usual sign, positive east. OnStep stores it the
    /// other way round, and this property does the conversion in a single
    /// place.
    /// </summary>
    private double LongitudeEastPositive => -Mount.LongitudeWestPositive;

    private double LocalSiderealTimeHours
    {
        get
        {
            // The device clock is local standard time. Convert to UTC with the OnStep
            // offset, which is the opposite sign to the usual timezone value.
            DateTime utc = Mount.LocalStandardTimeAt(Now).AddHours(Mount.UtcOffset);

            return Coordinates.LocalSiderealTime(
                new DateTimeOffset(utc, TimeSpan.Zero),
                LongitudeEastPositive);
        }
    }

    private (double Altitude, double Azimuth) CurrentHorizontal()
    {
        DateTimeOffset now = Now;

        return Coordinates.EquatorialToHorizontal(
            Mount.RightAscension.PositionAt(now),
            Mount.Declination.PositionAt(now),
            Mount.Latitude,
            LocalSiderealTimeHours);
    }

    private SimReply? DispatchMount(string cmd)
    {
        DateTimeOffset now = Now;

        switch (cmd)
        {
            // Current position. The simulator always answers in high
            // precision, which is OnStepX's default mode.
            case "GR":
                return SimReply.Text(Lx200Format.FormatHoursHigh(Mount.RightAscension.PositionAt(now)));
            case "GRH":
                return SimReply.Text(Lx200Format.FormatHoursHighest(Mount.RightAscension.PositionAt(now)));
            case "GD":
                return SimReply.Text(Lx200Format.FormatDegreesHigh(Mount.Declination.PositionAt(now)));
            case "GDH":
                return SimReply.Text(Lx200Format.FormatDegreesHighest(Mount.Declination.PositionAt(now)));
            case "GA":
                return SimReply.Text(Lx200Format.FormatAltitudeHigh(CurrentHorizontal().Altitude));
            case "GAH":
                return SimReply.Text(Lx200Format.FormatAltitudeHighest(CurrentHorizontal().Altitude));
            case "GZ":
                return SimReply.Text(Lx200Format.FormatAzimuthHigh(CurrentHorizontal().Azimuth));
            case "GZH":
                return SimReply.Text(Lx200Format.FormatAzimuthHighest(CurrentHorizontal().Azimuth));

            // Target.
            case "Gr":
                return SimReply.Text(Lx200Format.FormatHoursHigh(Mount.TargetRightAscension));
            case "GrH":
                return SimReply.Text(Lx200Format.FormatHoursHighest(Mount.TargetRightAscension));
            case "Gd":
                return SimReply.Text(Lx200Format.FormatDegreesHigh(Mount.TargetDeclination));
            case "GdH":
                return SimReply.Text(Lx200Format.FormatDegreesHighest(Mount.TargetDeclination));

            // Status.
            case "GU":
                return SimReply.Text(Mount.BuildStatusString(now, LocalSiderealTimeHours));
            case "GW":
                return SimReply.Text(BuildGwStatus());
            case "Gm":
                return SimReply.Text(CurrentPierSide(now) switch
                {
                    PierSide.East => "E",
                    PierSide.West => "W",
                    _ => "N",
                });

            // Tracking rate in hertz, or 0 if not tracking.
            case "GT":
                return SimReply.Number(Mount.IsTracking ? TrackingHz() : 0.0, "0.00000");

            // Start and stop tracking.
            case "Te":
                if (Mount.ParkState == ParkState.Parked)
                {
                    Mount.LastError = CommandError.SlewErrorInPark;
                    return SimReply.Bool(false);
                }

                Mount.IsTracking = true;
                return SimReply.Bool(true);

            case "Td":
                Mount.IsTracking = false;
                return SimReply.Bool(true);

            // Rate selection. Answer nothing.
            case "TQ": Mount.TrackingRate = MountTrackingRate.Sidereal; return SimReply.None();
            case "TS": Mount.TrackingRate = MountTrackingRate.Solar; return SimReply.None();
            case "TL": Mount.TrackingRate = MountTrackingRate.Lunar; return SimReply.None();
            case "TK": Mount.TrackingRate = MountTrackingRate.King; return SimReply.None();

            // Compensation.
            case "Tn": Mount.Compensation = TrackingCompensation.None; return SimReply.Bool(true);
            case "Tr": Mount.Compensation = TrackingCompensation.RefractionDualAxis; return SimReply.Bool(true);
            case "To": Mount.Compensation = TrackingCompensation.ModelDualAxis; return SimReply.Bool(true);

            case "T1":
                Mount.Compensation = Mount.Compensation switch
                {
                    TrackingCompensation.RefractionDualAxis => TrackingCompensation.RefractionSingleAxis,
                    TrackingCompensation.ModelDualAxis => TrackingCompensation.ModelSingleAxis,
                    _ => Mount.Compensation,
                };
                return SimReply.Bool(true);

            case "T2":
                Mount.Compensation = Mount.Compensation switch
                {
                    TrackingCompensation.RefractionSingleAxis => TrackingCompensation.RefractionDualAxis,
                    TrackingCompensation.ModelSingleAxis => TrackingCompensation.ModelDualAxis,
                    _ => Mount.Compensation,
                };
                return SimReply.Bool(true);

            // Force a pier side. The mount reaches the same sky position from the other
            // side and stays there until the next goto chooses again.
            case "MNe":
                Mount.ForcedPierSide = PierSide.East;
                return SimReply.Digit((int)GotoResult.Accepted);
            case "MNw":
                Mount.ForcedPierSide = PierSide.West;
                return SimReply.Digit((int)GotoResult.Accepted);

            // Goto and sync.
            case "MS": return StartGoto(equatorial: true);
            case "MA": return StartGoto(equatorial: false);
            case "MD": return SimReply.Digit(DestinationPierSideDigit());

            case "CS":
                Mount.RightAscension.SetPosition(Mount.TargetRightAscension);
                Mount.Declination.SetPosition(Mount.TargetDeclination);
                Mount.IsAtHome = false;
                return SimReply.None();

            // Stop. Answer nothing.
            case "Q":
                Mount.RightAscension.Stop(now);
                Mount.Declination.Stop(now);
                Mount.GotoActive = false;
                Mount.IsHoming = false;
                if (Mount.ParkState == ParkState.Parking)
                {
                    Mount.ParkState = ParkState.Unparked;
                }

                return SimReply.None();

            // Axis stops. No reply.
            case "Qe" or "Qw":
                Mount.RightAscension.Stop(now);
                return SimReply.None();
            case "Qn" or "Qs":
                Mount.Declination.Stop(now);
                return SimReply.None();

            // Manual motion at the current guide rate. No reply.
            //
            // These really move the axis rather than being accepted and ignored: the
            // driver has to report Slewing during a MoveAxis, and a simulator that
            // stood still could not tell a working implementation from a broken one.
            // West lowers right ascension: with hour angle equal to sidereal time minus
            // right ascension, a target further west has a smaller right ascension.
            case "Mw":
                Mount.RightAscension.MoveTo(
                    Mount.RightAscension.PositionAt(now) - 6.0, now, ManualRateHoursPerSecond());
                return SimReply.None();
            case "Me":
                Mount.RightAscension.MoveTo(
                    Mount.RightAscension.PositionAt(now) + 6.0, now, ManualRateHoursPerSecond());
                return SimReply.None();
            case "Mn":
                Mount.Declination.MoveTo(90.0, now, Mount.ManualRateDegreesPerSecond);
                return SimReply.None();
            case "Ms":
                Mount.Declination.MoveTo(-90.0, now, Mount.ManualRateDegreesPerSecond);
                return SimReply.None();

            // Guide rate presets. No reply.
            case "RG": Mount.ManualRateDegreesPerSecond = SiderealDegreesPerSecond; return SimReply.None();
            case "RC": Mount.ManualRateDegreesPerSecond = 8 * SiderealDegreesPerSecond; return SimReply.None();
            case "RM": Mount.ManualRateDegreesPerSecond = 20 * SiderealDegreesPerSecond; return SimReply.None();
            case "RF": Mount.ManualRateDegreesPerSecond = 48 * SiderealDegreesPerSecond; return SimReply.None();
            case "RS": Mount.ManualRateDegreesPerSecond = SlewDegreesPerSecond() / 2.0; return SimReply.None();

            // Park and home.
            case "hP":
                // In a mount integrated build, :hP# parks THE MOUNT.
                // The focuser and rotator handlers never get to see it.
                if (Mount.ParkState == ParkState.Parked)
                {
                    Mount.LastError = CommandError.Parked;
                    return SimReply.Bool(false);
                }

                if (!Mount.ParkPositionSet)
                {
                    Mount.LastError = CommandError.NoParkPositionSet;
                    return SimReply.Bool(false);
                }

                Mount.ParkState = ParkState.Parking;
                Mount.GotoActive = false;
                Mount.RightAscension.MoveTo(LocalSiderealTimeHours, now);
                Mount.Declination.MoveTo(Mount.Latitude >= 0 ? 90 : -90, now);
                return SimReply.Bool(true);

            case "hR":
                if (Mount.ParkState != ParkState.Parked)
                {
                    Mount.LastError = CommandError.NotParked;
                    return SimReply.Bool(false);
                }

                Mount.ParkState = ParkState.Unparked;
                Mount.IsTracking = true;
                return SimReply.Bool(true);

            case "hQ":
                Mount.ParkPositionSet = true;
                return SimReply.Bool(true);

            case "hC":
                Mount.IsHoming = true;
                Mount.IsAtHome = false;
                Mount.GotoActive = false;
                Mount.RightAscension.MoveTo(LocalSiderealTimeHours, now);
                Mount.Declination.MoveTo(Mount.Latitude >= 0 ? 90 : -90, now);
                return SimReply.None();

            case "hF":
                Mount.RightAscension.SetPosition(LocalSiderealTimeHours);
                Mount.Declination.SetPosition(Mount.Latitude >= 0 ? 90 : -90);
                Mount.IsAtHome = true;
                Mount.IsHoming = false;
                return SimReply.None();

            // Home sensors and offsets. Three fields, as the current firmware returns.
            case "h?":
                return SimReply.Text(string.Join(
                    ',',
                    Mount.HasHomeSensors ? "1" : "0",
                    Mount.HomeOffsetAxis1.ToString(CultureInfo.InvariantCulture),
                    Mount.HomeOffsetAxis2.ToString(CultureInfo.InvariantCulture)));

            case "hA0": Mount.AutoHomeAtBoot = false; return SimReply.None();
            case "hA1": Mount.AutoHomeAtBoot = true; return SimReply.None();

            // Limits.
            case "Gh": return SimReply.Text($"{Mount.HorizonLimit:+00;-00}*");
            case "Go": return SimReply.Text($"{Mount.OverheadLimit:00}*");
            case "GXE9": return SimReply.Int(Mount.MeridianLimitEast);
            case "GXEA": return SimReply.Int(Mount.MeridianLimitWest);

            // Axis travel, read only.
            case "GXEe": return SimReply.Number(Mount.Axis1MinimumDegrees, "0.0");
            case "GXEw": return SimReply.Number(Mount.Axis1MaximumDegrees, "0.0");
            case "GXEB": return SimReply.Number(Mount.Axis1MaximumDegrees / 15.0, "0.0");
            case "GXEC": return SimReply.Number(Mount.Axis2MinimumDegrees, "0.0");
            case "GXED": return SimReply.Number(Mount.Axis2MaximumDegrees, "0.0");

            // Goto and meridian configuration.
            case "GX92": return SimReply.Number(Mount.SlewPeriod, "0.000");
            case "GX93": return SimReply.Number(Mount.BaseSlewPeriod, "0.000");
            case "GX99": return SimReply.Number(Mount.FastestSlewPeriod, "0.000");
            case "GX97": return SimReply.Number(SlewDegreesPerSecond(), "0.0");
            case "GX94": return SimReply.Text(CurrentPierSide(now) switch
            {
                PierSide.East => "1",
                PierSide.West => "2",
                _ => "0",
            });
            case "GX95": return SimReply.Text(Mount.AutoMeridianFlip ? "1" : "0");
            case "GX96": return SimReply.Text(Mount.PreferredPierSide.ToString());
            case "GX98": return SimReply.Text(RotatorPresent ? Rotator.Capability.ToString() : "N");

            // Mount type and diagnostics.
            case "GXEM": return SimReply.Int(Mount.MountTypeCode);
            case "GXEE": return SimReply.Int(Mount.MountTypeCode - 1);
            case "GXE4": return SimReply.Int(Mount.StepsPerDegreeAxis1);
            case "GXE5": return SimReply.Int(Mount.StepsPerDegreeAxis2);
            case "GXTR": return SimReply.Number(Mount.TrackingRateOffsetRa, "0.00000000");
            case "GXTD": return SimReply.Number(Mount.TrackingRateOffsetDec, "0.00000000");

            // Backlash.
            case "%BR": return SimReply.Int(Mount.BacklashRightAscension);
            case "%BD": return SimReply.Int(Mount.BacklashDeclination);

            // Instrument angles. These are the mechanical angles of the two axes, which
            // for a German equatorial pointing east of the meridian are the hour angle
            // and the declination.
            case "GX40":
                return SimReply.Text(Lx200Format.FormatAzimuthHigh(
                    Lx200Format.NormalizeDegrees(InstrumentAngleAxis1(now))));
            case "GX41":
                return SimReply.Text(Lx200Format.FormatAzimuthHigh(
                    Lx200Format.NormalizeDegrees(InstrumentAngleAxis2(now))));
            case "GX42":
                return SimReply.Number(InstrumentAngleAxis1(now), "+0.000000;-0.000000");
            case "GX43":
                return SimReply.Number(InstrumentAngleAxis2(now), "+0.000000;-0.000000");

            // Encoder counts, derived from the axis position so that they track a move
            // instead of sitting at a constant that would hide a wiring mistake.
            case "GX44":
                return SimReply.Int((long)Math.Round(
                    InstrumentAngleAxis1(now) * Mount.EncoderCountsPerDegree));
            case "GX45":
                return SimReply.Int((long)Math.Round(
                    InstrumentAngleAxis2(now) * Mount.EncoderCountsPerDegree));

            // Step frequency of each axis.
            case "GXF3":
                return SimReply.Number(StepFrequency(Mount.RightAscension, 15.0 *
                    Mount.StepsPerDegreeAxis1, now), "+0.000000;-0.000000");
            case "GXF4":
                return SimReply.Number(StepFrequency(Mount.Declination,
                    Mount.StepsPerDegreeAxis2, now), "+0.000000;-0.000000");

            // Periodic error correction.
            case "VW": return SimReply.Int(Mount.WormSteps);
            case "VH": return SimReply.Int(Mount.PecIndexPosition);
            case "GXE7": return SimReply.Int(Mount.WormSteps);
            case "GXE8": return SimReply.Int(Mount.PecBufferSeconds);
            case "GXE6":
                return SimReply.Number(
                    Mount.StepsPerDegreeAxis1 * (360.0 / 86164.0905), "0.000000");

            case "$QZ+":
                // Playback needs data to play. Without a recording the firmware stays
                // idle, and reporting otherwise would let a driver claim PEC was running
                // on a mount that had never recorded a worm cycle.
                Mount.PecState = Mount.PecRecorded ? PecState.Playing : PecState.Ignore;
                return SimReply.None();

            case "$QZ-":
                Mount.PecState = PecState.Ignore;
                return SimReply.None();

            case "$QZ/":
                // Recording is armed here and starts at the next index pulse, so the
                // state is "ready to record" and not "recording".
                Mount.PecState = PecState.ReadyRecording;
                return SimReply.None();

            case "$QZZ":
                Mount.PecRecorded = false;
                Mount.PecState = PecState.Ignore;
                return SimReply.None();

            case "$QZ!":
                // Saving to non volatile storage leaves the state alone.
                return SimReply.None();

            // PEC state through its own command.
            //
            // Note that this reports the five states with a completely different set of
            // characters from the ones :GU# uses for the same states. Reusing either
            // parser for the other command yields "unknown" for every value.
            case "$QZ?":
                return SimReply.Text(Mount.PecState switch
                {
                    PecState.ReadyPlaying => "p",
                    PecState.Playing => "P",
                    PecState.ReadyRecording => "r",
                    PecState.Recording => "R",
                    _ => "I",
                });

            // Driver telemetry.
            case "GXU1":
                return Mount.DriverAxis1.ReportsStatus
                    ? SimReply.Text(Mount.DriverAxis1.Flags)
                    : SimReply.Bool(false);
            case "GXU2":
                return Mount.DriverAxis2.ReportsStatus
                    ? SimReply.Text(Mount.DriverAxis2.Flags)
                    : SimReply.Bool(false);

            case "GXSG1": return StallGuardReply(Mount.DriverAxis1);
            case "GXSG2": return StallGuardReply(Mount.DriverAxis2);

            // Restart and non volatile storage.
            case "ERESET":
                Mount.ResetRequested = true;
                return SimReply.None();

            case "ENVRESET":
                Mount.NonVolatileResetPending = true;
                return SimReply.Text("NV RESET");

            default:
                return DispatchMountWithParameters(cmd, now);
        }
    }

    private SimReply? DispatchMountWithParameters(string cmd, DateTimeOffset now)
    {
        // Set target.
        if (cmd.StartsWith("Sr", StringComparison.Ordinal))
        {
            if (!Lx200Format.TryParse(cmd[2..], out double hours) || hours is < 0 or >= 24)
            {
                Mount.LastError = CommandError.ParameterForm;
                return SimReply.Bool(false);
            }

            Mount.TargetRightAscension = hours;
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("Sd", StringComparison.Ordinal))
        {
            if (!Lx200Format.TryParse(cmd[2..], out double degrees) || Math.Abs(degrees) > 90)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Mount.TargetDeclination = degrees;
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("Sa", StringComparison.Ordinal))
        {
            if (!Lx200Format.TryParse(cmd[2..], out double alt) || Math.Abs(alt) > 90)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Mount.TargetAltitude = alt;
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("Sz", StringComparison.Ordinal))
        {
            if (!Lx200Format.TryParse(cmd[2..], out double az) || az is < 0 or >= 360)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Mount.TargetAzimuth = az;
            return SimReply.Bool(true);
        }

        // Pulse guide: :MgdNNNN#
        if (cmd.StartsWith("Mg", StringComparison.Ordinal) && cmd.Length >= 4)
        {
            char direction = cmd[2];
            if (direction is not ('n' or 's' or 'e' or 'w')
                || !TryInt(cmd[3..], out int ms))
            {
                return SimReply.None();
            }

            Mount.PulseGuideUntil = now.AddMilliseconds(ms);

            // The axis has to actually move, by rate times duration. Recording only
            // the end time would let a driver that never guides look correct: Conform
            // measures the position change and compares it against the guide rate the
            // driver reports.
            double seconds = ms / 1000.0;
            double degrees = Mount.ManualRateDegreesPerSecond * seconds;

            switch (direction)
            {
                case 'n':
                    Mount.Declination.MoveTo(
                        Mount.Declination.PositionAt(now) + degrees,
                        now, Mount.ManualRateDegreesPerSecond);
                    break;
                case 's':
                    Mount.Declination.MoveTo(
                        Mount.Declination.PositionAt(now) - degrees,
                        now, Mount.ManualRateDegreesPerSecond);
                    break;
                case 'w':
                    Mount.RightAscension.MoveTo(
                        Mount.RightAscension.PositionAt(now) - (degrees / 15.0),
                        now, ManualRateHoursPerSecond());
                    break;
                default:
                    Mount.RightAscension.MoveTo(
                        Mount.RightAscension.PositionAt(now) + (degrees / 15.0),
                        now, ManualRateHoursPerSecond());
                    break;
            }

            return SimReply.None();
        }

        // Limits.
        if (cmd.StartsWith("Sh", StringComparison.Ordinal) && TryInt(cmd[2..], out int horizon))
        {
            if (horizon is < -30 or > 30)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Mount.HorizonLimit = horizon;
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("So", StringComparison.Ordinal) && TryInt(cmd[2..], out int overhead))
        {
            if (overhead is < 60 or > 90)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Mount.OverheadLimit = overhead;
            return SimReply.Bool(true);
        }

        if (TryParam(cmd, "SXE9,", out string v) && TryInt(v, out int east))
        {
            Mount.MeridianLimitEast = east;
            return SimReply.Bool(true);
        }

        if (TryParam(cmd, "SXEA,", out v) && TryInt(v, out int west))
        {
            Mount.MeridianLimitWest = west;
            return SimReply.Bool(true);
        }

        // Meridian flip.
        if (TryParam(cmd, "SX94,", out v) && TryInt(v, out int homeMode))
        {
            Mount.MeridianFlipHomeMode = homeMode switch
            {
                1 => MeridianFlipHomeMode.VisitHome,
                2 => MeridianFlipHomeMode.PauseAtHome,
                _ => MeridianFlipHomeMode.DirectSlew,
            };
            return SimReply.Bool(true);
        }

        if (TryParam(cmd, "SX95,", out v) && TryInt(v, out int autoFlip))
        {
            Mount.AutoMeridianFlip = autoFlip == 1;
            return SimReply.Bool(true);
        }

        if (TryParam(cmd, "SX96,", out v) && v.Length == 1 && "EWBA".Contains(v[0]))
        {
            Mount.PreferredPierSide = v[0];
            return SimReply.Bool(true);
        }

        // Buzzer.
        if (TryParam(cmd, "SX97,", out v) && TryInt(v, out int sound))
        {
            if (sound is 0 or 1)
            {
                Mount.BuzzerEnabled = sound == 1;
            }

            return SimReply.Bool(true);
        }

        // Goto speed.
        if (TryParam(cmd, "SX92,", out v) && TryDouble(v, out double period))
        {
            if (period < Mount.FastestSlewPeriod)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Mount.SlewPeriod = period;
            return SimReply.Bool(true);
        }

        if (TryParam(cmd, "SX93,", out v) && TryInt(v, out int preset))
        {
            Mount.SlewPeriod = Mount.BaseSlewPeriod * preset switch
            {
                1 => 0.5,
                2 => 1.0 / 1.5,
                3 => 1.0,
                4 => 1.5,
                5 => 2.0,
                _ => 1.0,
            };
            return SimReply.None();
        }

        // Rate offsets.
        if (TryParam(cmd, "SXTR,", out v) && TryDouble(v, out double ra))
        {
            Mount.TrackingRateOffsetRa = ra;

            // OnStep takes arcseconds per sidereal second, while the axis is in hours.
            // One second of right ascension is fifteen arcseconds, and an hour is 3600
            // seconds, so hours per second is arcsec / 15 / 3600.
            Mount.RightAscension.SetDriftRate(ra / 15.0 / 3600.0, now);
            return SimReply.Bool(true);
        }

        if (TryParam(cmd, "SXTD,", out v) && TryDouble(v, out double dec))
        {
            Mount.TrackingRateOffsetDec = dec;

            // Arcseconds per second on an axis measured in degrees.
            Mount.Declination.SetDriftRate(dec / 3600.0, now);
            return SimReply.Bool(true);
        }

        // Mount type for the next boot.
        if (TryParam(cmd, "SXEM,", out v) && TryInt(v, out int mountType))
        {
            if (mountType is < 1 or > 9)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Mount.MountTypeCode = mountType;
            return SimReply.Bool(true);
        }

        // Custom axis rates used by MoveAxis, in degrees per second.
        if (cmd.StartsWith("RA", StringComparison.Ordinal) && TryDouble(cmd[2..], out double axis1Rate))
        {
            Mount.ManualRateDegreesPerSecond = axis1Rate;
            return SimReply.None();
        }

        if (cmd.StartsWith("RE", StringComparison.Ordinal) && TryDouble(cmd[2..], out double axis2Rate))
        {
            Mount.ManualRateDegreesPerSecond = axis2Rate;
            return SimReply.None();
        }

        // Home sensor offsets and sense reversal. Both answer nothing.
        if (TryParam(cmd, "hC1,", out v))
        {
            if (v == "R")
            {
                Mount.HomeSenseReversedAxis1 = !Mount.HomeSenseReversedAxis1;
                return SimReply.None();
            }

            if (TryInt(v, out int offset1))
            {
                Mount.HomeOffsetAxis1 = offset1;
                return SimReply.None();
            }

            return SimReply.None();
        }

        if (TryParam(cmd, "hC2,", out v))
        {
            if (v == "R")
            {
                Mount.HomeSenseReversedAxis2 = !Mount.HomeSenseReversedAxis2;
                return SimReply.None();
            }

            if (TryInt(v, out int offset2))
            {
                Mount.HomeOffsetAxis2 = offset2;
                return SimReply.None();
            }

            return SimReply.None();
        }

        // Worm rotation steps.
        if (TryParam(cmd, "SXE7,", out v) && TryInt(v, out int wormSteps))
        {
            if (wormSteps <= 0)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Mount.WormSteps = wormSteps;
            return SimReply.Bool(true);
        }

        // Backlash.
        if (cmd.StartsWith("$BR", StringComparison.Ordinal) && TryInt(cmd[3..], out int brl))
        {
            Mount.BacklashRightAscension = brl;
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("$BD", StringComparison.Ordinal) && TryInt(cmd[3..], out int bdl))
        {
            Mount.BacklashDeclination = bdl;
            return SimReply.Bool(true);
        }

        return null;
    }

    private SimReply StartGoto(bool equatorial)
    {
        DateTimeOffset now = Now;

        if (Mount.ParkState == ParkState.Parked)
        {
            Mount.LastError = CommandError.SlewErrorInPark;
            return SimReply.Digit((int)GotoResult.MountParked);
        }

        if (Mount.GotoActive)
        {
            Mount.LastError = CommandError.SlewInSlew;
            return SimReply.Digit((int)GotoResult.GotoInProgress);
        }

        double targetRa;
        double targetDec;

        if (equatorial)
        {
            targetRa = Mount.TargetRightAscension;
            targetDec = Mount.TargetDeclination;
        }
        else
        {
            (targetRa, targetDec) = Coordinates.HorizontalToEquatorial(
                Mount.TargetAltitude,
                Mount.TargetAzimuth,
                Mount.Latitude,
                LocalSiderealTimeHours);
        }

        // Limit check, so the goto return codes can genuinely be exercised.
        var (altitude, _) = Coordinates.EquatorialToHorizontal(
            targetRa, targetDec, Mount.Latitude, LocalSiderealTimeHours);

        if (altitude < Mount.HorizonLimit)
        {
            Mount.LastError = CommandError.SlewErrorBelowHorizon;
            return SimReply.Digit((int)GotoResult.BelowHorizonLimit);
        }

        if (altitude > Mount.OverheadLimit)
        {
            Mount.LastError = CommandError.SlewErrorAboveOverhead;
            return SimReply.Digit((int)GotoResult.AboveOverheadLimit);
        }

        Mount.GotoActive = true;
        Mount.IsAtHome = false;
        Mount.ForcedPierSide = null;
        Mount.LastError = CommandError.None;
        Mount.RightAscension.MoveTo(targetRa, now);
        Mount.Declination.MoveTo(targetDec, now);

        return SimReply.Digit((int)GotoResult.Accepted);
    }

    /// <summary>Pier side the mount is on right now.</summary>
    private PierSide CurrentPierSide(DateTimeOffset now) =>
        Mount.PierSideForHourAngle(
            LocalSiderealTimeHours - Mount.RightAscension.PositionAt(now));

    /// <summary>
    /// Pier side the mount would end up on for the current target, as <c>:MD#</c>
    /// reports it: 1 for east, 2 for west.
    /// </summary>
    /// <remarks>
    /// East of the meridian, meaning a negative hour angle, the tube looks east and the
    /// mount sits on the west side. The reverse west of the meridian. This had the two
    /// the wrong way round.
    /// </remarks>
    private int DestinationPierSideDigit()
    {
        double ha = Coordinates.NormalizeHours(
            LocalSiderealTimeHours - Mount.TargetRightAscension);

        if (ha >= 12)
        {
            ha -= 24;
        }

        return ha < 0 ? 2 : 1;
    }

    /// <summary>Sidereal rate in degrees per second.</summary>
    private const double SiderealDegreesPerSecond = 360.0 / 86164.0905;

    /// <summary>
    /// Manual slew rate converted to hours of right ascension per second, since the
    /// right ascension axis is measured in hours while rates are in degrees.
    /// </summary>
    private double ManualRateHoursPerSecond() => Mount.ManualRateDegreesPerSecond / 15.0;

    private double TrackingHz() => Mount.TrackingRate switch
    {
        MountTrackingRate.Lunar => 57.900,
        MountTrackingRate.Solar => 60.000,
        MountTrackingRate.King => 60.136,
        _ => 60.164,
    };

    private double SlewDegreesPerSecond()
    {
        // Degrees per second from the period in microseconds per step.
        double stepsPerSecond = 1_000_000.0 / Mount.SlewPeriod;

        return stepsPerSecond / Mount.StepsPerDegreeAxis1;
    }

    /// <summary>
    /// Axis 1 instrument angle in degrees, which on a German equatorial is the hour
    /// angle. Derived from the axis position so that it follows a move.
    /// </summary>
    private double InstrumentAngleAxis1(DateTimeOffset now) =>
        (LocalSiderealTimeHours - Mount.RightAscension.PositionAt(now)) * 15.0;

    /// <summary>Axis 2 instrument angle in degrees, the declination axis.</summary>
    private double InstrumentAngleAxis2(DateTimeOffset now) =>
        Mount.Declination.PositionAt(now);

    /// <summary>
    /// Step frequency of an axis, in steps per second, positive in the direction the axis
    /// is travelling.
    /// </summary>
    private static double StepFrequency(
        SimulatedMotion axis,
        double stepsPerUnit,
        DateTimeOffset now)
    {
        if (!axis.IsMovingAt(now))
        {
            return axis.DriftRatePerSecond * stepsPerUnit;
        }

        double direction = axis.Target >= axis.PositionAt(now) ? 1 : -1;

        return direction * axis.DefaultRatePerSecond * stepsPerUnit;
    }

    private static SimReply StallGuardReply(SimulatedDriverStatus driver) =>
        driver.ReportsStallGuard
            ? SimReply.Text(string.Join(
                ',',
                driver.StallGuardValue.ToString(CultureInfo.InvariantCulture),
                driver.StallGuardTrip.ToString(CultureInfo.InvariantCulture),
                driver.StallGuardBadMilliseconds.ToString(CultureInfo.InvariantCulture),
                driver.StallGuardArmed ? "1" : "0",
                driver.StallGuardLatched ? "1" : "0"))
            : SimReply.Bool(false);

    private string BuildGwStatus()
    {
        // Four characters: type, tracking, parked or at home, aligned.
        char type = Mount.MountTypeCode switch
        {
            2 or 7 or 8 => 'A',
            3 or 9 => 'A',
            _ => 'G',
        };

        char tracking = Mount.IsTracking ? 'T' : 'N';
        char parked = Mount.ParkState == ParkState.Parked ? 'P'
            : Mount.IsAtHome ? 'H'
            : '0';

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{type}{tracking}{parked}1");
    }
}
