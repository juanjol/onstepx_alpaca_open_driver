using OnStepX.Core.Protocol;

namespace OnStepX.Core.Simulation;

/// <summary>
/// Simulated mount state.
/// </summary>
public sealed class SimulatedMount
{
    /// <summary>Right ascension axis, in hours.</summary>
    public SimulatedMotion RightAscension { get; } = new(0.0, 15.0 / 3600.0 * 240);

    /// <summary>Declination axis, in degrees.</summary>
    public SimulatedMotion Declination { get; } = new(45.0, 3.0);

    /// <summary>Whether it is tracking the object.</summary>
    public bool IsTracking { get; set; }

    /// <summary>Parked state.</summary>
    public ParkState ParkState { get; set; } = ParkState.Unparked;

    /// <summary>Whether it is at home.</summary>
    public bool IsAtHome { get; set; }

    /// <summary>Whether it is heading to home.</summary>
    public bool IsHoming { get; set; }

    /// <summary>Auto home on boot.</summary>
    public bool AutoHomeAtBoot { get; set; }

    /// <summary>Mount type, values from <c>:GXEM#</c>.</summary>
    public int MountTypeCode { get; set; } = 1;

    /// <summary>Compensation mode.</summary>
    public TrackingCompensation Compensation { get; set; } = TrackingCompensation.None;

    /// <summary>Tracking rate.</summary>
    public MountTrackingRate TrackingRate { get; set; } = MountTrackingRate.Sidereal;

    /// <summary>
    /// Pier side forced by <c>:MNe#</c> or <c>:MNw#</c>, if any.
    /// </summary>
    /// <remarks>
    /// A German mount can reach the same sky position from either side, so being told
    /// which side to use overrides the natural choice until the next goto picks again.
    /// </remarks>
    public PierSide? ForcedPierSide { get; set; }

    /// <summary>
    /// Pier side for a given hour angle.
    /// </summary>
    /// <remarks>
    /// <b>Derived, not stored.</b> On a German equatorial the side follows from where the
    /// telescope points: east of the meridian the tube looks east and the mount sits on
    /// the west side, and west of the meridian it is the other way round. A fixed value
    /// here made the whole meridian flip model untestable, because a conformance check
    /// slews to specific hour angles and expects the side to follow.
    /// </remarks>
    public PierSide PierSideForHourAngle(double hourAngleHours)
    {
        if (ForcedPierSide is PierSide forced)
        {
            return forced;
        }

        // Bring the hour angle into -12 to +12.
        double ha = hourAngleHours % 24.0;
        if (ha > 12)
        {
            ha -= 24;
        }
        else if (ha < -12)
        {
            ha += 24;
        }

        // Only a German equatorial has sides at all.
        if (MountTypeCode is not (1 or 5 or 6))
        {
            return PierSide.None;
        }

        return ha < 0 ? PierSide.West : PierSide.East;
    }

    /// <summary>Automatic meridian flip.</summary>
    public bool AutoMeridianFlip { get; set; }

    /// <summary>Home mode during the flip.</summary>
    public MeridianFlipHomeMode MeridianFlipHomeMode { get; set; } = MeridianFlipHomeMode.DirectSlew;

    /// <summary>Preferred pier side: <c>E</c>, <c>W</c>, <c>B</c> or <c>A</c>.</summary>
    public char PreferredPierSide { get; set; } = 'B';

    /// <summary>Buzzer.</summary>
    public bool BuzzerEnabled { get; set; }

    /// <summary>Pulse guiding in progress until this instant.</summary>
    public DateTimeOffset PulseGuideUntil { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Pulse guide rate index.</summary>
    public int PulseGuideRateSelect { get; set; } = 2;

    /// <summary>Guide rate index.</summary>
    public int GuideRateSelect { get; set; } = 5;

    /// <summary>Last error code.</summary>
    public CommandError LastError { get; set; } = CommandError.None;

    /// <summary>A goto is in progress.</summary>
    public bool GotoActive { get; set; }

    // Pending target.

    /// <summary>Target right ascension, in hours.</summary>
    public double TargetRightAscension { get; set; }

    /// <summary>Target declination, in degrees.</summary>
    public double TargetDeclination { get; set; } = 45.0;

    /// <summary>Target altitude, in degrees.</summary>
    public double TargetAltitude { get; set; }

    /// <summary>Target azimuth, in degrees.</summary>
    public double TargetAzimuth { get; set; }

    // Site and time.

    /// <summary>Latitude, positive north.</summary>
    public double Latitude { get; set; } = 40.4168;

    /// <summary>
    /// Longitude in OnStep's convention, <b>positive west</b>.
    /// </summary>
    /// <remarks>
    /// This is the opposite of the usual sign and of what ASCOM expects,
    /// which uses positive east. The conversion is the device layer's
    /// responsibility, not the simulator's, which mirrors the firmware here.
    /// </remarks>
    public double LongitudeWestPositive { get; set; } = 3.7038;

    /// <summary>Elevation above sea level, in meters.</summary>
    public double Elevation { get; set; } = 650;

    /// <summary>
    /// UTC offset in OnStep's convention, which is the <b>opposite</b> of
    /// the usual time zone value.
    /// </summary>
    public double UtcOffset { get; set; } = -1;

    /// <summary>
    /// Offset between the simulated clock and real time.
    /// </summary>
    /// <remarks>
    /// Normally zero, so the simulated mount's clock runs with the real one. A fixed
    /// clock looked simpler but was wrong: sidereal time then drifted away from
    /// reality, and Conform rightly complained that the mount and the computer
    /// disagreed by hours. Setting the date or time through the protocol moves this
    /// offset, which is also how a deliberately wrong mount clock is simulated so
    /// that <c>SetDateTimeOnConnect</c> has something to correct.
    /// </remarks>
    public TimeSpan ClockOffset { get; set; }

    /// <summary>
    /// Local standard time of the device at a given instant.
    /// </summary>
    /// <remarks>
    /// OnStep <b>never</b> applies daylight saving: its clock is always standard
    /// time. With the OnStep convention that <see cref="UtcOffset"/> is added to
    /// local time to reach UT1, local time is UTC minus that offset.
    /// </remarks>
    public DateTime LocalStandardTimeAt(DateTimeOffset utcNow) =>
        utcNow.UtcDateTime.AddHours(-UtcOffset) + ClockOffset;

    /// <summary>
    /// Moves <see cref="ClockOffset"/> so that the local standard time at
    /// <paramref name="utcNow"/> becomes <paramref name="desiredLocal"/>.
    /// </summary>
    public void SetLocalStandardTime(DateTime desiredLocal, DateTimeOffset utcNow)
    {
        ClockOffset = desiredLocal - utcNow.UtcDateTime.AddHours(-UtcOffset);
    }

    // Limits.

    /// <summary>Horizon limit, in degrees.</summary>
    public int HorizonLimit { get; set; } = -10;

    /// <summary>Zenith limit, in degrees.</summary>
    public int OverheadLimit { get; set; } = 90;

    /// <summary>East meridian limit, in minutes.</summary>
    public int MeridianLimitEast { get; set; } = 1;

    /// <summary>West meridian limit, in minutes.</summary>
    public int MeridianLimitWest { get; set; } = 1;

    // Backlash and rates.

    /// <summary>RA or azimuth backlash, in arcseconds.</summary>
    public int BacklashRightAscension { get; set; }

    /// <summary>Declination or altitude backlash, in arcseconds.</summary>
    public int BacklashDeclination { get; set; }

    /// <summary>Current slew period, in microseconds per step.</summary>
    public double SlewPeriod { get; set; } = 107.0;

    /// <summary>Base slew period.</summary>
    public double BaseSlewPeriod { get; set; } = 107.0;

    /// <summary>Fastest slew period allowed.</summary>
    public double FastestSlewPeriod { get; set; } = 53.5;

    /// <summary>RA rate offset, arcseconds per sidereal second.</summary>
    public double TrackingRateOffsetRa { get; set; }

    /// <summary>Dec rate offset.</summary>
    public double TrackingRateOffsetDec { get; set; }

    /// <summary>Steps per degree of axis 1.</summary>
    public long StepsPerDegreeAxis1 { get; set; } = 12800;

    /// <summary>Steps per degree of axis 2.</summary>
    public long StepsPerDegreeAxis2 { get; set; } = 12800;

    /// <summary>Encoder counts per degree, for the derived encoder readings.</summary>
    public long EncoderCountsPerDegree { get; set; } = 4096;

    /// <summary>Stepper driver telemetry of axis 1.</summary>
    public SimulatedDriverStatus DriverAxis1 { get; } = new();

    /// <summary>Stepper driver telemetry of axis 2.</summary>
    public SimulatedDriverStatus DriverAxis2 { get; } = new();

    // Axis travel limits, read only in the protocol.

    /// <summary>Axis 1 minimum, in degrees.</summary>
    public double Axis1MinimumDegrees { get; set; } = -180;

    /// <summary>Axis 1 maximum, in degrees.</summary>
    public double Axis1MaximumDegrees { get; set; } = 180;

    /// <summary>Axis 2 minimum, in degrees.</summary>
    public double Axis2MinimumDegrees { get; set; } = -90;

    /// <summary>Axis 2 maximum, in degrees.</summary>
    public double Axis2MaximumDegrees { get; set; } = 90;

    /// <summary>Park position set.</summary>
    public bool ParkPositionSet { get; set; } = true;

    // Home sensors.

    /// <summary>The mount reports home sensors, first field of <c>:h?#</c>.</summary>
    public bool HasHomeSensors { get; set; } = true;

    /// <summary>Axis 1 home offset, in arcseconds.</summary>
    public int HomeOffsetAxis1 { get; set; }

    /// <summary>Axis 2 home offset, in arcseconds.</summary>
    public int HomeOffsetAxis2 { get; set; }

    /// <summary>Axis 1 home sensor sense reversed.</summary>
    public bool HomeSenseReversedAxis1 { get; set; }

    /// <summary>Axis 2 home sensor sense reversed.</summary>
    public bool HomeSenseReversedAxis2 { get; set; }

    // Periodic error correction.

    /// <summary>
    /// PEC is compiled into this build.
    /// </summary>
    /// <remarks>
    /// Kept switchable because the absence of PEC is a real firmware configuration and
    /// the driver has to face it: the status string then carries no PEC character at all,
    /// which is the only way to tell a build without PEC from one whose PEC is idle.
    /// </remarks>
    public bool PecSupported { get; set; } = true;

    /// <summary>PEC playback and recording state.</summary>
    public PecState PecState { get; set; } = PecState.Ignore;

    /// <summary>There is recorded PEC data.</summary>
    public bool PecRecorded { get; set; }

    /// <summary>Worm rotation, in steps.</summary>
    public long WormSteps { get; set; } = 25_600;

    /// <summary>PEC buffer size, in sidereal seconds.</summary>
    public long PecBufferSeconds { get; set; } = 200;

    /// <summary>Index sense position, in sidereal seconds.</summary>
    public long PecIndexPosition { get; set; } = 42;

    /// <summary>Non volatile storage is marked to be cleared on the next boot.</summary>
    public bool NonVolatileResetPending { get; set; }

    /// <summary>The controller has been asked to restart.</summary>
    public bool ResetRequested { get; set; }

    /// <summary>
    /// Rate used by manual moves, in degrees per second.
    /// </summary>
    /// <remarks>
    /// Set by the guide rate presets (<c>:RG#</c> and friends) and by the custom axis
    /// rate commands (<c>:RAn.n#</c>, <c>:REn.n#</c>) that the driver uses to implement
    /// <c>MoveAxis</c>. Starts at one times sidereal, matching <c>:RG#</c>.
    /// </remarks>
    public double ManualRateDegreesPerSecond { get; set; } = 360.0 / 86164.0905;

    /// <summary>Builds the <c>:GU#</c> response the same way the firmware does.</summary>
    /// <remarks>
    /// The concatenation order mirrors that of
    /// <c>src/telescope/mount/status/Status.command.cpp</c>, so the parser
    /// is tested against realistic strings and not a made up order.
    /// </remarks>
    public string BuildStatusString(DateTimeOffset now, double localSiderealTimeHours)
    {
        PierSide pierSide = PierSideForHourAngle(
            localSiderealTimeHours - RightAscension.PositionAt(now));

        var sb = new System.Text.StringBuilder(24);

        if (!IsTracking) sb.Append('n');
        if (!IsSlewing(now)) sb.Append('N');

        sb.Append(ParkState switch
        {
            ParkState.Unparked => "p",
            ParkState.Parking => "I",
            ParkState.Parked => "P",
            ParkState.ParkFailed => "F",
            _ => string.Empty,
        });

        if (IsAtHome) sb.Append('H');
        if (IsHoming) sb.Append('h');
        if (AutoHomeAtBoot) sb.Append('B');
        if (now < PulseGuideUntil) sb.Append('G');

        switch (Compensation)
        {
            case TrackingCompensation.RefractionSingleAxis: sb.Append("rs"); break;
            case TrackingCompensation.RefractionDualAxis: sb.Append('r'); break;
            case TrackingCompensation.ModelSingleAxis: sb.Append("ts"); break;
            case TrackingCompensation.ModelDualAxis: sb.Append('t'); break;
            default:
                // Rate characters are only emitted without compensation.
                sb.Append(TrackingRate switch
                {
                    MountTrackingRate.Lunar => "(",
                    MountTrackingRate.Solar => "O",
                    MountTrackingRate.King => "k",
                    _ => string.Empty,
                });
                break;
        }

        if (MeridianFlipHomeMode == MeridianFlipHomeMode.VisitHome) sb.Append('v');
        if (MeridianFlipHomeMode == MeridianFlipHomeMode.PauseAtHome) sb.Append('u');
        if (BuzzerEnabled) sb.Append('z');
        if (AutoMeridianFlip) sb.Append('a');

        // PEC characters are emitted only when the feature is compiled in, and only on a
        // mount with a worm to correct. A build without PEC says nothing at all here
        // instead of reporting an idle state, and that silence is the only way a driver
        // can tell "no PEC in this firmware" from "PEC present but doing nothing".
        if (PecSupported && MountTypeCode is 1 or 2 or 5 or 6 or 7 or 8)
        {
            if (PecRecorded) sb.Append('R');

            sb.Append(PecState switch
            {
                PecState.ReadyPlaying => ',',
                PecState.Playing => '~',
                PecState.ReadyRecording => ';',
                PecState.Recording => '^',
                _ => '/',
            });
        }

        sb.Append(MountTypeCode switch
        {
            2 or 7 or 8 => 'K',
            3 or 9 => 'A',
            4 => 'L',
            _ => 'E',
        });

        sb.Append(pierSide switch
        {
            PierSide.East => 'T',
            PierSide.West => 'W',
            _ => 'o',
        });

        sb.Append((char)('0' + PulseGuideRateSelect));
        sb.Append((char)('0' + GuideRateSelect));
        sb.Append((char)('0' + (int)MapErrorToStatusDigit()));

        return sb.ToString();
    }

    /// <summary>
    /// A goto, a homing run or a park is in progress.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> derived from raw axis motion. Real OnStep keeps
    /// reporting "no goto active" while an axis moves under a manual move command or a
    /// pulse guide, because from the firmware's point of view neither is a goto. Basing
    /// this on axis motion would make the simulator claim a goto during a pulse guide,
    /// and ASCOM specifically forbids pulse guiding from setting <c>Slewing</c>.
    /// <para>
    /// It is also what forces the driver to track <c>MoveAxis</c> motion itself, which
    /// is the behaviour a real mount demands.
    /// </para>
    /// </remarks>
    public bool IsSlewing(DateTimeOffset now) =>
        GotoActive
        || IsHoming
        || ParkState == ParkState.Parking;

    /// <summary>
    /// Updates the state derived from the passage of time: closes the
    /// goto or the homing run once the axes have arrived.
    /// </summary>
    public void Advance(DateTimeOffset now)
    {
        bool axesMoving = RightAscension.IsMovingAt(now) || Declination.IsMovingAt(now);

        if (axesMoving)
        {
            return;
        }

        if (GotoActive)
        {
            GotoActive = false;

            // When a goto finishes the mount resumes tracking, which is
            // what OnStep does except in alt azimuth mode with no tracking.
            IsTracking = true;
        }

        if (IsHoming)
        {
            IsHoming = false;
            IsAtHome = true;
        }

        if (ParkState == ParkState.Parking)
        {
            ParkState = ParkState.Parked;
            IsTracking = false;
        }
    }

    private CommandError MapErrorToStatusDigit() =>
        LastError == CommandError.None ? CommandError.None : CommandError.False;
}
