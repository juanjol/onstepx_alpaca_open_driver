using System.Globalization;
using ASCOM;
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using OnStepX.Core.Devices;
using OnStepX.Core.Protocol;

namespace OnStepX.Devices;

/// <summary>
/// Position, site and time members.
/// </summary>
public sealed partial class OnStepTelescope
{
    /// <summary>Right ascension in hours, from the cached snapshot.</summary>
    public double RightAscension => ValidSnapshot.RightAscension;

    /// <summary>Declination in degrees, from the cached snapshot.</summary>
    public double Declination => ValidSnapshot.Declination;

    /// <summary>Altitude in degrees, from the cached snapshot.</summary>
    public double Altitude => ValidSnapshot.Altitude;

    /// <summary>Azimuth in degrees, from the cached snapshot.</summary>
    public double Azimuth => ValidSnapshot.Azimuth;

    /// <summary>Local apparent sidereal time in hours.</summary>
    public double SiderealTime => ValidSnapshot.SiderealTime;

    /// <summary>
    /// The mount is moving in response to a slew method or to
    /// <see cref="MoveAxis"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ASCOM defines this as motion caused by a slew method <b>or by MoveAxis</b>,
    /// and explicitly not by tracking or pulse guiding.
    /// </para>
    /// <para>
    /// The status word cannot answer this on its own. OnStep reports "no goto
    /// active" during a <c>:Mw#</c> style manual move, because from the firmware's
    /// point of view it is not a goto at all. So the driver has to remember that it
    /// started an axis move, otherwise <c>Slewing</c> stays false while the mount is
    /// visibly moving, and a client that waits for it to clear never waits at all.
    /// </para>
    /// </remarks>
    public bool Slewing => ValidSnapshot.IsSlewing || IsMovingAnyAxis || IsConvergingOnAltAz;

    /// <summary>An axis is being driven by <see cref="MoveAxis"/>.</summary>
    private bool IsMovingAnyAxis => _primaryAxisMoving || _secondaryAxisMoving;

    /// <summary>The mount is at its home position.</summary>
    public bool AtHome => ValidSnapshot.Status.IsAtHome;

    /// <summary>The mount is parked.</summary>
    public bool AtPark => ValidSnapshot.Status.IsParked;

    /// <summary>A pulse guide is in progress.</summary>
    public bool IsPulseGuiding => ValidSnapshot.Status.PulseGuideActive;

    /// <summary>
    /// Which side of the pier the mount is on.
    /// </summary>
    /// <remarks>
    /// ASCOM's naming is confusing: <see cref="PointingState.Normal"/> is the state
    /// historically called <c>pierEast</c>, and
    /// <see cref="PointingState.ThroughThePole"/> is <c>pierWest</c>. OnStep reports
    /// east and west directly, so the mapping is east to normal and west to through
    /// the pole.
    /// </remarks>
    public PointingState SideOfPier
    {
        get => ToPointingState(ValidSnapshot.Status.PierSide);

        set
        {
            if (!CanSetPierSide)
            {
                throw new PropertyNotImplementedException(
                    "Only a German equatorial mount can be told which side of the pier to use.");
            }

            if (value == SideOfPier)
            {
                return;
            }

            // :MNe# and :MNw# slew to the current sky position from the requested
            // side, which is exactly what setting SideOfPier means.
            string command = value switch
            {
                PointingState.Normal => "MNe",
                PointingState.ThroughThePole => "MNw",
                _ => throw new InvalidValueException(
                    $"{value} is not a pier side the mount can be sent to."),
            };

            RunCommandAndRefresh(async () =>
            {
                GotoResult result = await Channel
                    .GetGotoResultAsync(command, CancellationToken.None)
                    .ConfigureAwait(false);

                ThrowIfGotoRejected(result);
            });
        }
    }

    private static PointingState ToPointingState(PierSide side) => side switch
    {
        PierSide.East => PointingState.Normal,
        PierSide.West => PointingState.ThroughThePole,
        _ => PointingState.Unknown,
    };

    /// <summary>
    /// Which side of the pier the mount would end up on for a given target.
    /// </summary>
    /// <remarks>
    /// OnStep answers this with <c>:MD#</c>, which reports the destination for the
    /// <b>currently set target</b>. There is no side effect free way to ask, so the
    /// existing target is saved and restored around the query. Without that, calling
    /// this property would silently overwrite a target the client had already set,
    /// and the next <c>SlewToTarget</c> would go somewhere else entirely.
    /// </remarks>
    public PointingState DestinationSideOfPier(double rightAscension, double declination)
    {
        ValidateRightAscension(rightAscension);
        ValidateDeclination(declination);

        return RunSync(async () =>
        {
            string savedRa = await Channel.GetStringAsync("Gr", CancellationToken.None)
                .ConfigureAwait(false);
            string savedDec = await Channel.GetStringAsync("Gd", CancellationToken.None)
                .ConfigureAwait(false);

            try
            {
                await SetTargetAsync(rightAscension, declination, CancellationToken.None)
                    .ConfigureAwait(false);

                int digit = await Channel.GetDigitAsync("MD", CancellationToken.None)
                    .ConfigureAwait(false);

                return digit switch
                {
                    1 => PointingState.Normal,
                    2 => PointingState.ThroughThePole,
                    _ => PointingState.Unknown,
                };
            }
            finally
            {
                // Restore whatever the client had set, so this really is read only.
                if (Lx200Format.TryParse(savedRa, out double ra)
                    && Lx200Format.TryParse(savedDec, out double dec))
                {
                    await SetTargetAsync(ra, dec, CancellationToken.None).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>
    /// Target right ascension in hours.
    /// </summary>
    /// <remarks>
    /// ASCOM requires a read before any write to fail with
    /// <see cref="ValueNotSetException"/>. OnStep would happily return whatever is in
    /// its target registers, so the driver tracks whether this session has set it.
    /// </remarks>
    public double TargetRightAscension
    {
        get => _targetRightAscensionSet
            ? _targetRightAscension
            : throw new ValueNotSetException("No target right ascension has been set.");

        set
        {
            ValidateRightAscension(value);

            RunSync(() => Channel.RequireTrueAsync(
                "Sr" + Lx200Format.FormatHoursHigh(value), CancellationToken.None));

            _targetRightAscension = value;
            _targetRightAscensionSet = true;
        }
    }

    /// <summary>Target declination in degrees.</summary>
    public double TargetDeclination
    {
        get => _targetDeclinationSet
            ? _targetDeclination
            : throw new ValueNotSetException("No target declination has been set.");

        set
        {
            ValidateDeclination(value);

            RunSync(() => Channel.RequireTrueAsync(
                "Sd" + Lx200Format.FormatDegreesHigh(value), CancellationToken.None));

            _targetDeclination = value;
            _targetDeclinationSet = true;
        }
    }

    /// <summary>Site latitude in degrees, positive north. Same sign in both worlds.</summary>
    public double SiteLatitude
    {
        get => RunSync(async () =>
        {
            string reply = await Channel.GetStringAsync("Gt", CancellationToken.None)
                .ConfigureAwait(false);

            return Lx200Format.TryParse(reply, out double value)
                ? value
                : throw new DriverException($"Could not parse the site latitude: {reply}");
        });

        set
        {
            if (value is < -90 or > 90)
            {
                throw new InvalidValueException(
                    $"Latitude {value} is outside the range -90 to 90 degrees.");
            }

            RunSync(() => Channel.RequireTrueAsync(
                "St" + Lx200Format.FormatDegreesHigh(value), CancellationToken.None));
        }
    }

    /// <summary>
    /// Site longitude in degrees, <b>positive east</b> as ASCOM requires.
    /// </summary>
    /// <remarks>
    /// OnStep stores longitude <b>positive west</b>, the opposite of the usual
    /// geographic convention and of ASCOM. Getting this wrong puts the mount on the
    /// other side of the planet and every goto lands somewhere else, so the flip lives
    /// in <see cref="OnStepClock"/> and every caller goes through it. The setup UI is
    /// the second caller.
    /// </remarks>
    public double SiteLongitude
    {
        get => RunSync(async () =>
        {
            string reply = await Channel.GetStringAsync("Gg", CancellationToken.None)
                .ConfigureAwait(false);

            return Lx200Format.TryParse(reply, out double westPositive)
                ? OnStepClock.ToAscomLongitude(westPositive)
                : throw new DriverException($"Could not parse the site longitude: {reply}");
        });

        set
        {
            if (value is < -180 or > 180)
            {
                throw new InvalidValueException(
                    $"Longitude {value} is outside the range -180 to 180 degrees.");
            }

            RunSync(() => Channel.RequireTrueAsync(
                "Sg" + Lx200Format.FormatRotatorAngle(OnStepClock.ToOnStepLongitude(value)),
                CancellationToken.None));
        }
    }

    /// <summary>Site elevation in metres.</summary>
    public double SiteElevation
    {
        get => RunSync(() => Channel.GetDoubleAsync("Gv", CancellationToken.None));

        set
        {
            if (value is < -300 or > 10000)
            {
                throw new InvalidValueException(
                    $"Elevation {value} is outside the range -300 to 10000 metres.");
            }

            RunSync(() => Channel.RequireTrueAsync(
                "Sv" + value.ToString("+0.0;-0.0", CultureInfo.InvariantCulture),
                CancellationToken.None));
        }
    }

    /// <summary>
    /// Mount clock as UTC.
    /// </summary>
    /// <remarks>
    /// Two OnStep quirks are handled here. First, its clock is <b>always standard
    /// time</b> and never has daylight saving applied. Second, the offset from
    /// <c>:GG#</c> is the value to <b>add</b> to local time to reach UT1, which is the
    /// negative of the timezone offset people are used to writing. So UTC is local
    /// plus that offset, not minus.
    /// </remarks>
    public DateTime UTCDate
    {
        get => RunSync(async () =>
        {
            string date = await Channel.GetStringAsync("GC", CancellationToken.None)
                .ConfigureAwait(false);
            string time = await Channel.GetStringAsync("GL", CancellationToken.None)
                .ConfigureAwait(false);
            string offset = await Channel.GetStringAsync("GG", CancellationToken.None)
                .ConfigureAwait(false);

            if (!TryParseLocalStandard(date, time, out DateTime local))
            {
                throw new DriverException(
                    $"Could not parse the mount clock: date '{date}', time '{time}'.");
            }

            if (!TryParseUtcOffsetHours(offset, out double offsetHours))
            {
                throw new DriverException($"Could not parse the UTC offset: '{offset}'.");
            }

            return DateTime.SpecifyKind(local.AddHours(offsetHours), DateTimeKind.Utc);
        });

        set
        {
            if (value == default)
            {
                throw new InvalidValueException("UTCDate cannot be the default DateTime value.");
            }

            RunSync(() => PushDateTimeAsync(value, CancellationToken.None));
        }
    }

    /// <summary>
    /// Writes a UTC instant to the mount as local standard time.
    /// </summary>
    private async Task PushDateTimeAsync(DateTime utc, CancellationToken cancellationToken)
    {
        DateTime utcValue = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();

        string offsetReply = await Channel.GetStringAsync("GG", cancellationToken)
            .ConfigureAwait(false);

        if (!TryParseUtcOffsetHours(offsetReply, out double offsetHours))
        {
            throw new DriverException($"Could not parse the UTC offset: '{offsetReply}'.");
        }

        // UTC = local + offset, so local = UTC - offset.
        DateTime local = utcValue.AddHours(-offsetHours);

        await Channel.RequireTrueAsync(
            "SC" + local.ToString("MM/dd/yy", CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);

        await Channel.RequireTrueAsync(
            "SL" + local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);

        Logger.LogInformation(
            "Mount clock set to {Local} local standard time, offset {Offset} hours",
            local, offsetHours);
    }

    private static bool TryParseLocalStandard(string date, string time, out DateTime local) =>
        OnStepClock.TryParseLocalStandard(date, time, out local);

    /// <summary>
    /// Parses <c>:GG#</c>, which OnStep formats as <c>sHH</c> or <c>sHH:MM</c>.
    /// </summary>
    private static bool TryParseUtcOffsetHours(string reply, out double hours) =>
        OnStepClock.TryParseUtcOffsetHours(reply, out hours);

    private async Task SetTargetAsync(
        double rightAscension,
        double declination,
        CancellationToken cancellationToken)
    {
        await Channel.RequireTrueAsync(
            "Sr" + Lx200Format.FormatHoursHigh(rightAscension), cancellationToken)
            .ConfigureAwait(false);

        await Channel.RequireTrueAsync(
            "Sd" + Lx200Format.FormatDegreesHigh(declination), cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateRightAscension(double hours)
    {
        if (hours is < 0 or >= 24 || double.IsNaN(hours))
        {
            throw new InvalidValueException(
                $"Right ascension {hours} is outside the range 0 to 24 hours.");
        }
    }

    private static void ValidateDeclination(double degrees)
    {
        if (degrees is < -90 or > 90 || double.IsNaN(degrees))
        {
            throw new InvalidValueException(
                $"Declination {degrees} is outside the range -90 to 90 degrees.");
        }
    }

    private static void ValidateAltitude(double degrees)
    {
        if (degrees is < -90 or > 90 || double.IsNaN(degrees))
        {
            throw new InvalidValueException(
                $"Altitude {degrees} is outside the range -90 to 90 degrees.");
        }
    }

    private static void ValidateAzimuth(double degrees)
    {
        if (degrees is < 0 or >= 360 || double.IsNaN(degrees))
        {
            throw new InvalidValueException(
                $"Azimuth {degrees} is outside the range 0 to 360 degrees.");
        }
    }
}
