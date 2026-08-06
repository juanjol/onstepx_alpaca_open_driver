using System.Globalization;
using OnStepX.Core.Protocol;

namespace OnStepX.Core.Configuration;

/// <summary>
/// Mount side of the configuration: site and clock, limits, meridian flip, tracking,
/// backlash, goto speed, home, park, PEC, buzzer and the destructive commands.
/// </summary>
public sealed partial class ControllerConfiguration
{
    /// <summary>
    /// Reads everything the mount setup page shows, in one pass over the channel.
    /// </summary>
    /// <remarks>
    /// The single <c>:GU#</c> read at the start carries the compensation mode, the
    /// meridian flip mode, the buzzer, the park state, the auto home flag and the PEC
    /// state, so those cost nothing extra. That also settles the awkward cases: the
    /// buzzer has no getter of its own, and PEC omits its characters entirely when the
    /// feature is not compiled in, which is the only way to tell a build without PEC
    /// from one whose PEC is merely idle.
    /// </remarks>
    public async Task<MountConfiguration> ReadMountAsync(
        CancellationToken cancellationToken = default)
    {
        MountStatus status = await Channel.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        return new MountConfiguration
        {
            Site = await ReadSiteAsync(cancellationToken).ConfigureAwait(false),
            Meridian = await ReadMeridianAsync(status, cancellationToken).ConfigureAwait(false),
            Limits = await ReadLimitsAsync(cancellationToken).ConfigureAwait(false),
            Tracking = await ReadTrackingAsync(status, cancellationToken).ConfigureAwait(false),
            Backlash = await ReadBacklashAsync(cancellationToken).ConfigureAwait(false),
            SlewRate = await ReadSlewRateAsync(cancellationToken).ConfigureAwait(false),
            Home = await ReadHomeAsync(status, cancellationToken).ConfigureAwait(false),
            Pec = await ReadPecAsync(status, cancellationToken).ConfigureAwait(false),
            MountType = await ReadMountTypeAsync(cancellationToken).ConfigureAwait(false),
            BuzzerEnabled = status.BuzzerEnabled,
            ParkState = status.ParkState,
            StatusRaw = status.Raw,
        };
    }

    // Site and clock

    /// <summary>Reads the site location and the controller clock.</summary>
    public async Task<SiteConfiguration> ReadSiteAsync(
        CancellationToken cancellationToken = default)
    {
        FirmwareValue<double> latitude = await ReadAngleAsync("Gt", cancellationToken)
            .ConfigureAwait(false);

        FirmwareValue<double> westPositive = await ReadAngleAsync("Gg", cancellationToken)
            .ConfigureAwait(false);

        FirmwareValue<double> longitude = westPositive.IsSupported
            ? FirmwareValue<double>.Present(
                OnStepClock.ToAscomLongitude(westPositive.Value), westPositive.Raw)
            : FirmwareValue<double>.Absent(westPositive.Raw);

        FirmwareValue<string> offsetReply = await ReadTextAsync("GG", cancellationToken)
            .ConfigureAwait(false);

        FirmwareValue<double> utcOffset =
            offsetReply.IsSupported
            && OnStepClock.TryParseUtcOffsetHours(offsetReply.Value, out double offsetHours)
                ? FirmwareValue<double>.Present(offsetHours, offsetReply.Raw)
                : FirmwareValue<double>.Absent(offsetReply.Raw);

        return new SiteConfiguration
        {
            Latitude = latitude,
            Longitude = longitude,
            Elevation = await ReadDoubleAsync("Gv", cancellationToken).ConfigureAwait(false),
            UtcOffsetHours = utcOffset,
            LocalStandardTime = await ReadClockAsync("GC", "GL", cancellationToken)
                .ConfigureAwait(false),
            UniversalTime = await ReadClockAsync("GX81", "GX80", cancellationToken)
                .ConfigureAwait(false),
            SiderealTime = await ReadAngleAsync("GS", cancellationToken).ConfigureAwait(false),

            // :GX89# is inverted: it answers 0 when the clock is usable.
            ClockReady = await ReadFlagAsync("GX89", '0', cancellationToken).ConfigureAwait(false),
            ActiveSiteSlot = await ReadInt32Async("W?", cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<FirmwareValue<DateTime>> ReadClockAsync(
        string dateCommand,
        string timeCommand,
        CancellationToken cancellationToken)
    {
        string? date = await Channel.TryGetStringAsync(dateCommand, cancellationToken)
            .ConfigureAwait(false);
        string? time = await Channel.TryGetStringAsync(timeCommand, cancellationToken)
            .ConfigureAwait(false);

        string raw = $"{date} {time}";

        return OnStepClock.TryParseLocalStandard(date, time, out DateTime value)
            ? FirmwareValue<DateTime>.Present(value, raw)
            : FirmwareValue<DateTime>.Absent(raw);
    }

    /// <summary>Writes the site latitude, in degrees positive north.</summary>
    public Task WriteLatitudeAsync(double degrees, CancellationToken cancellationToken = default)
    {
        if (degrees is < -90 or > 90 || double.IsNaN(degrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(degrees), degrees, "Latitude must be between -90 and 90 degrees.");
        }

        return WriteAsync("St" + Lx200Format.FormatDegreesHigh(degrees), cancellationToken);
    }

    /// <summary>
    /// Writes the site longitude, given in degrees <b>positive east</b> as ASCOM and every
    /// map use it. The flip to the controller's west positive convention happens here.
    /// </summary>
    public Task WriteLongitudeAsync(double degrees, CancellationToken cancellationToken = default)
    {
        if (degrees is < -180 or > 180 || double.IsNaN(degrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(degrees), degrees, "Longitude must be between -180 and 180 degrees.");
        }

        string parameter = Lx200Format.FormatRotatorAngle(OnStepClock.ToOnStepLongitude(degrees));

        return WriteAsync("Sg" + parameter, cancellationToken);
    }

    /// <summary>Writes the site elevation in metres.</summary>
    public Task WriteElevationAsync(double metres, CancellationToken cancellationToken = default)
    {
        if (metres is < -300 or > 10000 || double.IsNaN(metres))
        {
            throw new ArgumentOutOfRangeException(
                nameof(metres), metres, "Elevation must be between -300 and 10000 metres.");
        }

        return WriteAsync("Sv" + Decimal(metres, "+0.0;-0.0"), cancellationToken);
    }

    /// <summary>
    /// Writes the offset to add to local time to reach UT1.
    /// </summary>
    /// <remarks>
    /// This is the negative of the timezone offset as normally written, so central Europe
    /// in winter is <c>-1</c> and not <c>+1</c>. The setup page states that on the field
    /// itself, because getting it backwards moves the mount two timezones away and every
    /// goto lands thirty degrees off.
    /// </remarks>
    public Task WriteUtcOffsetAsync(double hours, CancellationToken cancellationToken = default)
    {
        if (hours is < -14 or > 14 || double.IsNaN(hours))
        {
            throw new ArgumentOutOfRangeException(
                nameof(hours), hours, "The UTC offset must be between -14 and 14 hours.");
        }

        return WriteAsync("SG" + OnStepClock.FormatUtcOffset(hours), cancellationToken);
    }

    /// <summary>
    /// Writes the controller clock from a local <b>standard</b> time.
    /// </summary>
    /// <remarks>
    /// OnStep never applies daylight saving. Sending a summer wall clock reading leaves
    /// the mount an hour out, so the caller is responsible for having removed it, and the
    /// setup page keeps that warning next to the field.
    /// </remarks>
    public async Task WriteClockAsync(
        DateTime localStandard,
        CancellationToken cancellationToken = default)
    {
        await Channel.RequireTrueAsync(
            "SC" + OnStepClock.FormatDate(localStandard), cancellationToken).ConfigureAwait(false);

        await Channel.RequireTrueAsync(
            "SL" + OnStepClock.FormatTime(localStandard), cancellationToken).ConfigureAwait(false);

        InvalidateCaches();
    }

    /// <summary>Selects the active site slot, 0 to 3.</summary>
    public Task SelectSiteSlotAsync(int slot, CancellationToken cancellationToken = default)
    {
        if (slot is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "The site slot is 0 to 3.");
        }

        return SendAsync("W" + Integer(slot), cancellationToken);
    }

    // Meridian flip

    /// <summary>Reads the meridian flip configuration.</summary>
    public async Task<MeridianConfiguration> ReadMeridianAsync(
        MountStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        FirmwareValue<string> preferred = await ReadTextAsync("GX96", cancellationToken)
            .ConfigureAwait(false);

        return new MeridianConfiguration
        {
            AutoFlip = status.AutoMeridianFlip,
            HomeMode = status.MeridianFlipHomeMode,
            WaitingAtHome = status.WaitingAtHome,
            PreferredSide = preferred.IsSupported
                ? FirmwareValue<PreferredPierSide>.Present(
                    ParsePreferredSide(preferred.Value!), preferred.Raw)
                : FirmwareValue<PreferredPierSide>.Absent(preferred.Raw),
        };
    }

    private static PreferredPierSide ParsePreferredSide(string reply) =>
        reply.Trim() switch
        {
            "E" => PreferredPierSide.East,
            "W" => PreferredPierSide.West,
            "B" => PreferredPierSide.Best,
            "A" => PreferredPierSide.Automatic,
            _ => PreferredPierSide.Unknown,
        };

    /// <summary>Enables or disables the automatic meridian flip.</summary>
    public Task WriteAutoMeridianFlipAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        WriteAsync("SX95," + (enabled ? "1" : "0"), cancellationToken);

    /// <summary>Sets what the mount does on its way to the other side of the pier.</summary>
    public Task WriteMeridianFlipHomeModeAsync(
        MeridianFlipHomeMode mode,
        CancellationToken cancellationToken = default)
    {
        int code = mode switch
        {
            MeridianFlipHomeMode.DirectSlew => 0,
            MeridianFlipHomeMode.VisitHome => 1,
            MeridianFlipHomeMode.PauseAtHome => 2,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode), mode, "Unsupported meridian flip mode."),
        };

        return WriteAsync("SX94," + Integer(code), cancellationToken);
    }

    /// <summary>Sets the pier side the firmware prefers.</summary>
    public Task WritePreferredPierSideAsync(
        PreferredPierSide side,
        CancellationToken cancellationToken = default)
    {
        char code = side switch
        {
            PreferredPierSide.East => 'E',
            PreferredPierSide.West => 'W',
            PreferredPierSide.Best => 'B',
            PreferredPierSide.Automatic => 'A',
            _ => throw new ArgumentOutOfRangeException(
                nameof(side), side, "Unsupported preferred pier side."),
        };

        return WriteAsync("SX96," + code, cancellationToken);
    }

    /// <summary>
    /// Releases a flip that is paused at home. Only meaningful while
    /// <see cref="MeridianConfiguration.WaitingAtHome"/> is set.
    /// </summary>
    public Task ContinueAfterPauseAsync(CancellationToken cancellationToken = default) =>
        WriteAsync("SX99,1", cancellationToken);

    // Limits

    /// <summary>Reads the motion limits.</summary>
    public async Task<LimitConfiguration> ReadLimitsAsync(
        CancellationToken cancellationToken = default) =>
        new()
        {
            HorizonDegrees = await ReadAngleAsync("Gh", cancellationToken).ConfigureAwait(false),
            OverheadDegrees = await ReadAngleAsync("Go", cancellationToken).ConfigureAwait(false),
            MeridianEastMinutes = await ReadInt32Async("GXE9", cancellationToken)
                .ConfigureAwait(false),
            MeridianWestMinutes = await ReadInt32Async("GXEA", cancellationToken)
                .ConfigureAwait(false),
            Axis1MinimumDegrees = await ReadDoubleAsync("GXEe", cancellationToken)
                .ConfigureAwait(false),
            Axis1MaximumDegrees = await ReadDoubleAsync("GXEw", cancellationToken)
                .ConfigureAwait(false),
            Axis1MaximumHours = await ReadDoubleAsync("GXEB", cancellationToken)
                .ConfigureAwait(false),
            Axis2MinimumDegrees = await ReadDoubleAsync("GXEC", cancellationToken)
                .ConfigureAwait(false),
            Axis2MaximumDegrees = await ReadDoubleAsync("GXED", cancellationToken)
                .ConfigureAwait(false),
        };

    /// <summary>Writes the horizon limit, in whole degrees of altitude.</summary>
    public Task WriteHorizonLimitAsync(int degrees, CancellationToken cancellationToken = default)
    {
        if (degrees is < -30 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(degrees), degrees, "The horizon limit is -30 to 30 degrees.");
        }

        return WriteAsync(
            "Sh" + degrees.ToString("+00;-00", CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>Writes the overhead limit, in whole degrees of altitude.</summary>
    /// <remarks>
    /// Unlike the horizon limit this parameter carries no sign, since a limit below the
    /// horizon would be meaningless.
    /// </remarks>
    public Task WriteOverheadLimitAsync(int degrees, CancellationToken cancellationToken = default)
    {
        if (degrees is < 60 or > 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(degrees), degrees, "The overhead limit is 60 to 90 degrees.");
        }

        return WriteAsync(
            "So" + degrees.ToString("00", CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>Writes the east meridian limit, in minutes past the meridian.</summary>
    public Task WriteMeridianLimitEastAsync(
        int minutes,
        CancellationToken cancellationToken = default) =>
        WriteAsync("SXE9," + Integer(ValidateMeridianLimit(minutes)), cancellationToken);

    /// <summary>Writes the west meridian limit, in minutes past the meridian.</summary>
    public Task WriteMeridianLimitWestAsync(
        int minutes,
        CancellationToken cancellationToken = default) =>
        WriteAsync("SXEA," + Integer(ValidateMeridianLimit(minutes)), cancellationToken);

    private static int ValidateMeridianLimit(int minutes) =>
        minutes is < -270 or > 270
            ? throw new ArgumentOutOfRangeException(
                nameof(minutes), minutes, "The meridian limit is -270 to 270 minutes.")
            : minutes;

    // Tracking

    /// <summary>Reads the tracking configuration.</summary>
    public async Task<TrackingConfiguration> ReadTrackingAsync(
        MountStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new TrackingConfiguration
        {
            Compensation = status.Compensation,
            IsTracking = status.IsTracking,
            Rate = status.TrackingRate,
            RightAscensionOffset = await ReadDoubleAsync("GXTR", cancellationToken)
                .ConfigureAwait(false),
            DeclinationOffset = await ReadDoubleAsync("GXTD", cancellationToken)
                .ConfigureAwait(false),
        };
    }

    /// <summary>Sets the tracking compensation model and the number of axes it drives.</summary>
    /// <remarks>
    /// The firmware splits this across two commands: <c>:Tn#</c>, <c>:Tr#</c> and
    /// <c>:To#</c> choose the model, and <c>:T1#</c> and <c>:T2#</c> choose single or dual
    /// axis. The model has to go first, because selecting it resets the axis count to
    /// dual, so the two commands in the other order silently lose the single axis choice.
    /// </remarks>
    public async Task WriteCompensationAsync(
        TrackingCompensation compensation,
        CancellationToken cancellationToken = default)
    {
        string model = compensation switch
        {
            TrackingCompensation.None => "Tn",
            TrackingCompensation.RefractionSingleAxis or TrackingCompensation.RefractionDualAxis
                => "Tr",
            TrackingCompensation.ModelSingleAxis or TrackingCompensation.ModelDualAxis => "To",
            _ => throw new ArgumentOutOfRangeException(
                nameof(compensation), compensation, "Unsupported compensation model."),
        };

        await Channel.RequireTrueAsync(model, cancellationToken).ConfigureAwait(false);

        if (compensation is TrackingCompensation.RefractionSingleAxis
            or TrackingCompensation.ModelSingleAxis)
        {
            await Channel.RequireTrueAsync("T1", cancellationToken).ConfigureAwait(false);
        }
        else if (compensation is not TrackingCompensation.None)
        {
            await Channel.RequireTrueAsync("T2", cancellationToken).ConfigureAwait(false);
        }

        InvalidateCaches();
    }

    /// <summary>
    /// Writes the right ascension tracking rate offset, in <b>arcseconds</b> per sidereal
    /// second, which is the firmware's unit and not the ASCOM one.
    /// </summary>
    public Task WriteRightAscensionOffsetAsync(
        double arcsecondsPerSecond,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "SXTR," + Decimal(ValidateOffset(arcsecondsPerSecond), "0.0####"), cancellationToken);

    /// <summary>
    /// Writes the declination tracking rate offset, in arcseconds per sidereal second.
    /// </summary>
    public Task WriteDeclinationOffsetAsync(
        double arcsecondsPerSecond,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "SXTD," + Decimal(ValidateOffset(arcsecondsPerSecond), "0.0####"), cancellationToken);

    private static double ValidateOffset(double arcsecondsPerSecond) =>
        double.IsNaN(arcsecondsPerSecond) || Math.Abs(arcsecondsPerSecond) > 100
            ? throw new ArgumentOutOfRangeException(
                nameof(arcsecondsPerSecond),
                arcsecondsPerSecond,
                "The rate offset must be within 100 arcseconds per second.")
            : arcsecondsPerSecond;

    // Backlash

    /// <summary>Reads the axis backlash, in arcseconds.</summary>
    public async Task<BacklashConfiguration> ReadBacklashAsync(
        CancellationToken cancellationToken = default) =>
        new()
        {
            RightAscensionArcseconds = await ReadInt32Async("%BR", cancellationToken)
                .ConfigureAwait(false),
            DeclinationArcseconds = await ReadInt32Async("%BD", cancellationToken)
                .ConfigureAwait(false),
        };

    /// <summary>Writes the right ascension or azimuth backlash, in arcseconds.</summary>
    public Task WriteRightAscensionBacklashAsync(
        int arcseconds,
        CancellationToken cancellationToken = default) =>
        WriteAsync("$BR" + Integer(ValidateBacklash(arcseconds)), cancellationToken);

    /// <summary>Writes the declination or altitude backlash, in arcseconds.</summary>
    public Task WriteDeclinationBacklashAsync(
        int arcseconds,
        CancellationToken cancellationToken = default) =>
        WriteAsync("$BD" + Integer(ValidateBacklash(arcseconds)), cancellationToken);

    private static int ValidateBacklash(int arcseconds) =>
        arcseconds is < 0 or > 3600
            ? throw new ArgumentOutOfRangeException(
                nameof(arcseconds), arcseconds, "Backlash is 0 to 3600 arcseconds.")
            : arcseconds;

    // Goto speed

    /// <summary>Reads the goto speed.</summary>
    public async Task<SlewRateConfiguration> ReadSlewRateAsync(
        CancellationToken cancellationToken = default) =>
        new()
        {
            CurrentPeriodMicroseconds = await ReadDoubleAsync("GX92", cancellationToken)
                .ConfigureAwait(false),
            BasePeriodMicroseconds = await ReadDoubleAsync("GX93", cancellationToken)
                .ConfigureAwait(false),
            FastestPeriodMicroseconds = await ReadDoubleAsync("GX99", cancellationToken)
                .ConfigureAwait(false),
            DegreesPerSecond = await ReadDoubleAsync("GX97", cancellationToken)
                .ConfigureAwait(false),
        };

    /// <summary>
    /// Writes the slew step period in microseconds. A <b>smaller</b> period is a faster
    /// slew, and the firmware refuses anything below the value it reports as fastest.
    /// </summary>
    public Task WriteSlewPeriodAsync(
        double microseconds,
        CancellationToken cancellationToken = default)
    {
        if (double.IsNaN(microseconds) || microseconds is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(microseconds),
                microseconds,
                "The slew period must be above zero and no more than 1000 microseconds.");
        }

        return WriteAsync("SX92," + Decimal(microseconds, "0.000"), cancellationToken);
    }

    /// <summary>
    /// Applies one of the firmware's five speed presets, relative to the base rate.
    /// </summary>
    /// <remarks>
    /// The mapping is inverted with respect to the number: preset 1 is 200 percent of the
    /// base speed and preset 5 is 50 percent, because the parameter scales the step
    /// period rather than the speed. This command reports no result, so the page reads the
    /// rate back afterwards.
    /// </remarks>
    public Task WriteSlewPresetAsync(int preset, CancellationToken cancellationToken = default)
    {
        if (preset is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preset), preset, "The slew preset is 1 to 5.");
        }

        return SendAsync("SX93," + Integer(preset), cancellationToken);
    }

    // Home

    /// <summary>Reads the home configuration.</summary>
    public async Task<HomeConfiguration> ReadHomeAsync(
        MountStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        // :h?# packs three fields: whether home sensors exist, and the two axis offsets.
        FirmwareValue<string> reply = await ReadTextAsync("h?", cancellationToken)
            .ConfigureAwait(false);

        FirmwareValue<bool> hasSensors = FirmwareValue<bool>.Absent(reply.Raw);
        FirmwareValue<int> axis1 = FirmwareValue<int>.Absent(reply.Raw);
        FirmwareValue<int> axis2 = FirmwareValue<int>.Absent(reply.Raw);

        if (reply.IsSupported)
        {
            string[] fields = reply.Value!.Split(',');

            if (fields.Length > 0 && int.TryParse(
                    fields[0], CultureInfo.InvariantCulture, out int sense))
            {
                hasSensors = FirmwareValue<bool>.Present(sense != 0, reply.Raw);
            }

            if (fields.Length > 1 && int.TryParse(
                    fields[1], CultureInfo.InvariantCulture, out int offset1))
            {
                axis1 = FirmwareValue<int>.Present(offset1, reply.Raw);
            }

            if (fields.Length > 2 && int.TryParse(
                    fields[2], CultureInfo.InvariantCulture, out int offset2))
            {
                axis2 = FirmwareValue<int>.Present(offset2, reply.Raw);
            }
        }

        return new HomeConfiguration
        {
            HasSensors = hasSensors,
            Axis1OffsetArcseconds = axis1,
            Axis2OffsetArcseconds = axis2,
            AutoHomeAtBoot = status.AutoHomeAtBoot,
            IsAtHome = status.IsAtHome,
            IsHoming = status.IsHoming,
        };
    }

    /// <summary>Enables or disables automatic homing when the controller powers up.</summary>
    public Task WriteAutoHomeAtBootAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        SendAsync(enabled ? "hA1" : "hA0", cancellationToken);

    /// <summary>Writes a home sensor offset, in arcseconds.</summary>
    /// <param name="axis">Axis, 1 or 2.</param>
    /// <param name="arcseconds">Offset.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    public Task WriteHomeOffsetAsync(
        int axis,
        int arcseconds,
        CancellationToken cancellationToken = default)
    {
        if (axis is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(axis), axis, "The axis is 1 or 2.");
        }

        return SendAsync(
            $"hC{Integer(axis)},{Integer(arcseconds)}", cancellationToken);
    }

    /// <summary>
    /// Flips the sense of a home switch. The command is a toggle rather than a value, so
    /// the page has to describe it as such.
    /// </summary>
    public Task ToggleHomeSenseAsync(int axis, CancellationToken cancellationToken = default)
    {
        if (axis is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(axis), axis, "The axis is 1 or 2.");
        }

        return SendAsync($"hC{Integer(axis)},R", cancellationToken);
    }

    /// <summary>Starts a move to the home position.</summary>
    public Task FindHomeAsync(CancellationToken cancellationToken = default) =>
        SendAsync("hC", cancellationToken);

    /// <summary>
    /// Declares the current position to be home, as a cold start would.
    /// </summary>
    /// <remarks>
    /// Only meaningful with the mount physically at its home position. Doing it anywhere
    /// else tells the firmware a wrong orientation, and every goto afterwards is off by
    /// the difference.
    /// </remarks>
    public Task ResetAtHomeAsync(CancellationToken cancellationToken = default) =>
        SendAsync("hF", cancellationToken);

    // Park

    /// <summary>Records the current position as the park position.</summary>
    public Task SetParkPositionAsync(CancellationToken cancellationToken = default) =>
        WriteAsync("hQ", cancellationToken);

    /// <summary>Parks the mount.</summary>
    public Task ParkAsync(CancellationToken cancellationToken = default) =>
        WriteAsync("hP", cancellationToken);

    /// <summary>Unparks the mount.</summary>
    public Task UnparkAsync(CancellationToken cancellationToken = default) =>
        WriteAsync("hR", cancellationToken);

    // Periodic error correction

    /// <summary>Reads the PEC configuration.</summary>
    /// <remarks>
    /// Whether PEC exists at all comes from the status string and not from these
    /// commands. A build without PEC has no PEC characters in <c>:GU#</c>, which is the
    /// only reliable way to tell it apart from a build whose PEC is simply idle.
    /// </remarks>
    public async Task<PecConfiguration> ReadPecAsync(
        MountStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        bool supported = status.PecState != PecState.Unknown;

        if (!supported)
        {
            return new PecConfiguration { IsSupported = false, State = PecState.Unknown };
        }

        return new PecConfiguration
        {
            IsSupported = true,
            State = status.PecState,
            HasRecordedData = status.PecRecorded,
            WormSteps = await ReadInt64Async("VW", cancellationToken).ConfigureAwait(false),
            WormStepsStored = await ReadInt64Async("GXE7", cancellationToken).ConfigureAwait(false),
            BufferSeconds = await ReadInt64Async("GXE8", cancellationToken).ConfigureAwait(false),
            StepsPerSiderealSecond = await ReadDoubleAsync("GXE6", cancellationToken)
                .ConfigureAwait(false),
            IndexSensePosition = await ReadInt64Async("VH", cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>Starts PEC playback.</summary>
    public Task StartPecPlaybackAsync(CancellationToken cancellationToken = default) =>
        SendAsync("$QZ+", cancellationToken);

    /// <summary>Stops PEC playback.</summary>
    public Task StopPecAsync(CancellationToken cancellationToken = default) =>
        SendAsync("$QZ-", cancellationToken);

    /// <summary>
    /// Arms PEC recording, which begins on the next index pulse rather than immediately.
    /// </summary>
    public Task ArmPecRecordingAsync(CancellationToken cancellationToken = default) =>
        SendAsync("$QZ/", cancellationToken);

    /// <summary>Clears the PEC buffer.</summary>
    public Task ClearPecAsync(CancellationToken cancellationToken = default) =>
        SendAsync("$QZZ", cancellationToken);

    /// <summary>
    /// Writes the PEC buffer to non volatile storage, without which a recording is lost
    /// at the next power cycle.
    /// </summary>
    public Task SavePecAsync(CancellationToken cancellationToken = default) =>
        SendAsync("$QZ!", cancellationToken);

    /// <summary>Writes the worm rotation period, in steps.</summary>
    public Task WriteWormStepsAsync(long steps, CancellationToken cancellationToken = default)
    {
        if (steps <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steps), steps, "The worm rotation must be a positive number of steps.");
        }

        return WriteAsync("SXE7," + Integer(steps), cancellationToken);
    }

    // Buzzer

    /// <summary>
    /// Enables or disables the buzzer. There is no getter: the current state comes from
    /// the <c>z</c> character of <c>:GU#</c>.
    /// </summary>
    public Task WriteBuzzerAsync(bool enabled, CancellationToken cancellationToken = default) =>
        WriteAsync("SX97," + (enabled ? "1" : "0"), cancellationToken);

    /// <summary>Sounds a single beep, so the user can confirm there is a buzzer fitted.</summary>
    public Task TestBuzzerAsync(CancellationToken cancellationToken = default) =>
        WriteAsync("SX97,2", cancellationToken);

    // Mount type and destructive commands

    /// <summary>Reads the mount type in force.</summary>
    public async Task<FirmwareValue<OnStepMountType>> ReadMountTypeAsync(
        CancellationToken cancellationToken = default)
    {
        FirmwareValue<int> code = await ReadInt32Async("GXEM", cancellationToken)
            .ConfigureAwait(false);

        // Zero is not a mount type, so here a zero really does mean the command is
        // missing rather than a value.
        return code.IsSupported && code.Value is >= 1 and <= 9
            ? FirmwareValue<OnStepMountType>.Present((OnStepMountType)code.Value, code.Raw)
            : FirmwareValue<OnStepMountType>.Absent(code.Raw);
    }

    /// <summary>
    /// Sets the mount type that will apply <b>after the next restart</b>.
    /// </summary>
    /// <remarks>
    /// Destructive: it changes how the firmware interprets both axes, so a wrong value
    /// makes the mount drive itself into its own mechanics on the next goto. The setup
    /// page asks for explicit confirmation.
    /// </remarks>
    public Task WriteMountTypeAsync(
        OnStepMountType type,
        CancellationToken cancellationToken = default)
    {
        if (type is OnStepMountType.Unknown || !Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported mount type.");
        }

        return WriteAsync("SXEM," + Integer((int)type), cancellationToken);
    }

    /// <summary>
    /// Restarts the controller.
    /// </summary>
    /// <remarks>
    /// The reply never arrives, because the MCU resets while sending it, so this is issued
    /// as a command with no response and the caller must expect the connection to drop.
    /// Every connected client loses its device.
    /// </remarks>
    public Task ResetControllerAsync(CancellationToken cancellationToken = default) =>
        SendAsync("ERESET", cancellationToken);

    /// <summary>
    /// Marks the controller's non volatile storage to be wiped on the next boot.
    /// </summary>
    /// <remarks>
    /// Destructive and not reversible: it discards the site, the park position, the
    /// alignment model, the PEC data and every axis setting. The mount comes back with
    /// compiled in defaults and has to be commissioned from scratch.
    /// </remarks>
    public async Task ClearNonVolatileStorageAsync(CancellationToken cancellationToken = default)
    {
        // Answers with text rather than a result code, so there is nothing to require.
        await Channel.GetStringAsync("ENVRESET", cancellationToken).ConfigureAwait(false);
        InvalidateCaches();
    }
}
