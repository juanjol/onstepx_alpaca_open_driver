using System.Globalization;
using ASCOM;
using ASCOM.Common.DeviceInterfaces;
using OnStepX.Core.Astronomy;
using OnStepX.Core.Devices;
using OnStepX.Core.Protocol;

namespace OnStepX.Devices;

/// <summary>
/// Slewing, syncing, parking, homing, guiding and tracking.
/// </summary>
public sealed partial class OnStepTelescope
{
    private DriveRate _trackingRate = DriveRate.Sidereal;

    /// <summary>
    /// How long to wait after a slew before reporting it finished.
    /// </summary>
    /// <remarks>
    /// OnStep has no equivalent setting, so the driver honours it itself in the
    /// blocking slew variants. Storing it and ignoring it would be worse than not
    /// implementing it at all.
    /// </remarks>
    public short SlewSettleTime
    {
        get => _slewSettleTime;

        set
        {
            if (value < 0)
            {
                throw new InvalidValueException("SlewSettleTime cannot be negative.");
            }

            _slewSettleTime = value;
        }
    }

    /// <summary>Tracking on or off.</summary>
    public bool Tracking
    {
        get => ValidSnapshot.Status.IsTracking;

        set => RunCommandAndRefresh(() =>
            Channel.RequireTrueAsync(value ? "Te" : "Td", CancellationToken.None));
    }

    /// <summary>
    /// Selected drive rate.
    /// </summary>
    /// <remarks>
    /// Read from the mount when possible, but OnStep only reports the rate character
    /// in <c>:GU#</c> while tracking compensation is off. With compensation active
    /// the value is not observable, so the driver returns the last rate it set rather
    /// than pretending it is sidereal.
    /// </remarks>
    public DriveRate TrackingRate
    {
        get
        {
            MountTrackingRate reported = Snapshot.Status.TrackingRate;

            return reported == MountTrackingRate.Unknown
                ? _trackingRate
                : ToDriveRate(reported);
        }

        set
        {
            string command = value switch
            {
                DriveRate.Sidereal => "TQ",
                DriveRate.Lunar => "TL",
                DriveRate.Solar => "TS",
                DriveRate.King => "TK",
                _ => throw new InvalidValueException($"{value} is not a drive rate OnStep supports."),
            };

            RunCommandAndRefresh(() => Channel.SendAsync(command, CancellationToken.None));
            _trackingRate = value;
        }
    }

    private static DriveRate ToDriveRate(MountTrackingRate rate) => rate switch
    {
        MountTrackingRate.Lunar => DriveRate.Lunar,
        MountTrackingRate.Solar => DriveRate.Solar,
        MountTrackingRate.King => DriveRate.King,
        _ => DriveRate.Sidereal,
    };

    /// <summary>
    /// Right ascension tracking offset, in <b>seconds of right ascension per sidereal
    /// second</b>, which is the ASCOM unit.
    /// </summary>
    /// <remarks>
    /// OnStep works in arcseconds per sidereal second, so the two differ by a factor
    /// of fifteen: one second of right ascension is fifteen arcseconds. Skipping that
    /// conversion would make every offset fifteen times too large, which is the kind
    /// of error that only shows up as slowly trailing stars.
    /// </remarks>
    public double RightAscensionRate
    {
        get => RunSync(async () =>
        {
            double arcsecPerSecond = await Channel
                .GetDoubleAsync("GXTR", CancellationToken.None)
                .ConfigureAwait(false);

            return arcsecPerSecond / 15.0;
        });

        set
        {
            RequireSiderealForRateOffset(nameof(RightAscensionRate));

            double arcsecPerSecond = value * 15.0;

            RunSync(() => Channel.RequireTrueAsync(
                "SXTR," + arcsecPerSecond.ToString("0.00000", CultureInfo.InvariantCulture),
                CancellationToken.None));
        }
    }

    /// <summary>
    /// Declination tracking offset, in arcseconds per second, the same unit OnStep
    /// uses so no conversion is needed.
    /// </summary>
    public double DeclinationRate
    {
        get => RunSync(() => Channel.GetDoubleAsync("GXTD", CancellationToken.None));

        set
        {
            RequireSiderealForRateOffset(nameof(DeclinationRate));

            RunSync(() => Channel.RequireTrueAsync(
                "SXTD," + value.ToString("0.00000", CultureInfo.InvariantCulture),
                CancellationToken.None));
        }
    }

    /// <summary>
    /// Rate offsets are only meaningful on top of the sidereal rate.
    /// </summary>
    /// <remarks>
    /// ASCOM requires an <see cref="ASCOM.InvalidOperationException"/> when a rate
    /// offset is written while a non sidereal drive rate is selected. The offsets are
    /// defined as departures from sidereal, so combining them with a lunar or solar
    /// rate has no defined meaning, and quietly accepting the write would leave the
    /// client believing in a correction that is not being applied.
    /// </remarks>
    private void RequireSiderealForRateOffset(string propertyName)
    {
        DriveRate current = TrackingRate;

        if (current != DriveRate.Sidereal)
        {
            throw new ASCOM.InvalidOperationException(
                $"{propertyName} can only be set while the drive rate is sidereal. " +
                $"The current rate is {current}.");
        }
    }

    /// <summary>
    /// Guide rate on the primary axis, in degrees per second.
    /// </summary>
    /// <remarks>
    /// OnStep accepts a custom rate with <c>:RAn.n#</c> but offers no matching read
    /// back, so the driver remembers what it set. The initial value is one times
    /// sidereal, which is OnStep's <c>:RG#</c> default.
    /// </remarks>
    public double GuideRateRightAscension
    {
        get => _guideRateRightAscension;

        set
        {
            ValidateGuideRate(value);

            RunSync(() => Channel.SendAsync(
                "RA" + value.ToString("0.0000", CultureInfo.InvariantCulture),
                CancellationToken.None));

            _guideRateRightAscension = value;
        }
    }

    /// <summary>Guide rate on the secondary axis, in degrees per second.</summary>
    public double GuideRateDeclination
    {
        get => _guideRateDeclination;

        set
        {
            ValidateGuideRate(value);

            RunSync(() => Channel.SendAsync(
                "RE" + value.ToString("0.0000", CultureInfo.InvariantCulture),
                CancellationToken.None));

            _guideRateDeclination = value;
        }
    }

    private static void ValidateGuideRate(double degreesPerSecond)
    {
        if (degreesPerSecond < 0 || double.IsNaN(degreesPerSecond))
        {
            throw new InvalidValueException("A guide rate cannot be negative.");
        }

        // Anything past a few degrees per second is a slew, not guiding, and would be
        // silently clamped by the firmware.
        if (degreesPerSecond > 10)
        {
            throw new InvalidValueException(
                $"A guide rate of {degreesPerSecond} degrees per second is not a guide rate.");
        }
    }

    /// <summary>
    /// Whether the mount applies refraction correction.
    /// </summary>
    /// <remarks>
    /// OnStep has four compensation modes rather than a simple on and off. Turning
    /// this on selects dual axis refraction, and turning it off disables compensation
    /// entirely. If the user has configured the full pointing model, reading this
    /// still reports true, because the mount is indeed correcting.
    /// </remarks>
    public bool DoesRefraction
    {
        get => Snapshot.Status.Compensation != TrackingCompensation.None;

        set => RunCommandAndRefresh(() =>
            Channel.RequireTrueAsync(value ? "Tr" : "Tn", CancellationToken.None));
    }

    /// <summary>Starts an asynchronous slew to equatorial coordinates.</summary>
    public void SlewToCoordinatesAsync(double rightAscension, double declination)
    {
        ValidateRightAscension(rightAscension);
        ValidateDeclination(declination);
        RequireUnparked();
        RequireTrackingForEquatorialSlew();

        ClearAxisMotion();

        RunCommandAndRefresh(async () =>
        {
            await SetTargetAsync(rightAscension, declination, CancellationToken.None)
                .ConfigureAwait(false);

            GotoResult result = await Channel
                .GetGotoResultAsync("MS", CancellationToken.None)
                .ConfigureAwait(false);

            ThrowIfGotoRejected(result);
        });

        _targetRightAscension = rightAscension;
        _targetDeclination = declination;
        _targetRightAscensionSet = true;
        _targetDeclinationSet = true;
    }

    /// <summary>Slews to equatorial coordinates and waits for it to finish.</summary>
    public void SlewToCoordinates(double rightAscension, double declination)
    {
        SlewToCoordinatesAsync(rightAscension, declination);
        WaitForSlewToComplete();
    }

    /// <summary>Starts an asynchronous slew to the current target.</summary>
    public void SlewToTargetAsync()
    {
        if (!_targetRightAscensionSet || !_targetDeclinationSet)
        {
            throw new ValueNotSetException(
                "Set TargetRightAscension and TargetDeclination before slewing to the target.");
        }

        SlewToCoordinatesAsync(_targetRightAscension, _targetDeclination);
    }

    /// <summary>Slews to the current target and waits.</summary>
    public void SlewToTarget()
    {
        SlewToTargetAsync();
        WaitForSlewToComplete();
    }

    /// <summary>Syncs the mount to equatorial coordinates.</summary>
    public void SyncToCoordinates(double rightAscension, double declination)
    {
        ValidateRightAscension(rightAscension);
        ValidateDeclination(declination);
        RequireUnparked();

        RunCommandAndRefresh(async () =>
        {
            await SetTargetAsync(rightAscension, declination, CancellationToken.None)
                .ConfigureAwait(false);

            await Channel.SendAsync("CS", CancellationToken.None).ConfigureAwait(false);
        });

        _targetRightAscension = rightAscension;
        _targetDeclination = declination;
        _targetRightAscensionSet = true;
        _targetDeclinationSet = true;
    }

    /// <summary>Syncs to the current target.</summary>
    public void SyncToTarget()
    {
        if (!_targetRightAscensionSet || !_targetDeclinationSet)
        {
            throw new ValueNotSetException(
                "Set TargetRightAscension and TargetDeclination before syncing to the target.");
        }

        SyncToCoordinates(_targetRightAscension, _targetDeclination);
    }

    /// <summary>
    /// Syncs the mount to horizontal coordinates.
    /// </summary>
    /// <remarks>
    /// Converted to equatorial first, using the mount's own sidereal time and
    /// latitude so the result agrees with what the controller believes rather than
    /// with this machine's clock.
    /// </remarks>
    public void SyncToAltAz(double azimuth, double altitude)
    {
        ValidateAzimuth(azimuth);
        ValidateAltitude(altitude);

        MountSnapshot snapshot = ValidSnapshot;
        double latitude = SiteLatitude;

        (double ra, double dec) = Coordinates.HorizontalToEquatorial(
            altitude, azimuth, latitude, snapshot.SiderealTime);

        SyncToCoordinates(ra, dec);
    }

    /// <summary>Stops any slew in progress.</summary>
    public void AbortSlew()
    {
        if (AtPark)
        {
            throw new ParkedException("A parked mount has no slew to abort.");
        }

        // :Q# stops every kind of motion, including axis moves, so the driver's own
        // MoveAxis flags have to be cleared too or Slewing would never go false. The
        // same goes for an alt az slew that is still re-aiming.
        bool wasMovingAnAxis = _primaryAxisMoving || _secondaryAxisMoving;

        _primaryAxisMoving = false;
        _secondaryAxisMoving = false;
        _altAzConverging = false;

        RunCommandAndRefresh(() => Channel.SendAsync("Q", CancellationToken.None));

        if (wasMovingAnAxis)
        {
            RestoreGuideRates();
        }
    }

    /// <summary>Starts an asynchronous park.</summary>
    public void Park()
    {
        if (AtPark)
        {
            // Already parked is success, not an error: ASCOM treats Park as
            // idempotent and clients call it defensively at end of session.
            return;
        }

        ClearAxisMotion();

        RunCommandAndRefresh(() => Channel.RequireTrueAsync("hP", CancellationToken.None));
    }

    /// <summary>Unparks the mount.</summary>
    public void Unpark()
    {
        if (!AtPark)
        {
            return;
        }

        RunCommandAndRefresh(() => Channel.RequireTrueAsync("hR", CancellationToken.None));
    }

    /// <summary>Stores the current position as the park position.</summary>
    public void SetPark() =>
        RunCommandAndRefresh(() => Channel.RequireTrueAsync("hQ", CancellationToken.None));

    /// <summary>Starts an asynchronous move to the home position.</summary>
    public void FindHome()
    {
        RequireUnparked();

        ClearAxisMotion();

        RunCommandAndRefresh(() => Channel.SendAsync("hC", CancellationToken.None));
    }

    /// <summary>
    /// Drives one axis at a given rate, in degrees per second.
    /// </summary>
    /// <remarks>
    /// Mapped onto OnStep's custom guide rate plus a directional move: the rate is set
    /// with <c>:RAn.n#</c> or <c>:REn.n#</c> and then motion starts with the matching
    /// move command. A rate of zero stops that axis, which is what ASCOM specifies.
    /// </remarks>
    public void MoveAxis(TelescopeAxis axis, double rate)
    {
        if (!CanMoveAxis(axis))
        {
            throw new InvalidValueException($"Axis {axis} cannot be moved by this driver.");
        }

        RequireUnparked();

        double magnitude = Math.Abs(rate);

        if (magnitude > 0)
        {
            double maximum = MaximumSlewDegreesPerSecond();

            if (magnitude > maximum)
            {
                throw new InvalidValueException(
                    $"Rate {rate} exceeds the mount's maximum of {maximum} degrees per second.");
            }
        }

        // A positive rate drives towards increasing right ascension, which is east.
        // Hour angle is sidereal time minus right ascension, so westward motion lowers
        // right ascension and eastward motion raises it.
        (string rateCommand, string positive, string negative, string stopA, string stopB) =
            axis switch
            {
                TelescopeAxis.Primary => ("RA", "Me", "Mw", "Qe", "Qw"),
                _ => ("RE", "Mn", "Ms", "Qn", "Qs"),
            };

        double guideRate = axis == TelescopeAxis.Primary
            ? _guideRateRightAscension
            : _guideRateDeclination;

        RunCommandAndRefresh(async () =>
        {
            if (magnitude == 0)
            {
                // Stopping needs both directions cleared: only one of them is moving,
                // and the firmware ignores a stop for an axis that is idle.
                await Channel.SendAsync(stopA, CancellationToken.None).ConfigureAwait(false);
                await Channel.SendAsync(stopB, CancellationToken.None).ConfigureAwait(false);

                // Put the guide rate back.
                //
                // OnStep keeps a single rate per axis, shared by pulse guiding and by
                // manual moves, so driving an axis overwrites the guide rate. Leaving it
                // overwritten means the next PulseGuide moves at the slew rate the
                // client last used for MoveAxis, which on a guiding run is a large and
                // very confusing error.
                await Channel.SendAsync(
                    rateCommand + guideRate.ToString("0.0000", CultureInfo.InvariantCulture),
                    CancellationToken.None).ConfigureAwait(false);

                SetAxisMoving(axis, false);
                return;
            }

            await Channel.SendAsync(
                rateCommand + magnitude.ToString("0.0000", CultureInfo.InvariantCulture),
                CancellationToken.None).ConfigureAwait(false);

            await Channel.SendAsync(
                rate > 0 ? positive : negative, CancellationToken.None).ConfigureAwait(false);

            SetAxisMoving(axis, true);
        });
    }

    /// <summary>
    /// Re-sends both stored guide rates.
    /// </summary>
    /// <remarks>
    /// Needed after anything that may have left an axis rate overwritten, since OnStep
    /// shares one rate per axis between guiding and manual motion.
    /// </remarks>
    private void RestoreGuideRates()
    {
        RunSync(async () =>
        {
            await Channel.SendAsync(
                "RA" + _guideRateRightAscension.ToString("0.0000", CultureInfo.InvariantCulture),
                CancellationToken.None).ConfigureAwait(false);

            await Channel.SendAsync(
                "RE" + _guideRateDeclination.ToString("0.0000", CultureInfo.InvariantCulture),
                CancellationToken.None).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Clears the MoveAxis flags. A goto, a park or a homing run replaces any axis
    /// move, so leaving the flags set would keep Slewing true forever.
    /// </summary>
    private void ClearAxisMotion()
    {
        _primaryAxisMoving = false;
        _secondaryAxisMoving = false;
    }

    private void SetAxisMoving(TelescopeAxis axis, bool moving)
    {
        if (axis == TelescopeAxis.Primary)
        {
            _primaryAxisMoving = moving;
        }
        else
        {
            _secondaryAxisMoving = moving;
        }
    }

    /// <summary>
    /// Pulse guides for a number of milliseconds.
    /// </summary>
    /// <remarks>
    /// Asynchronous: it returns straight away and <see cref="IsPulseGuiding"/> reports
    /// progress. OnStep takes the duration inline in <c>:Mg#</c>, so there is nothing
    /// for the driver to time itself.
    /// </remarks>
    public void PulseGuide(GuideDirection direction, int duration)
    {
        RequireUnparked();

        if (duration < 0)
        {
            throw new InvalidValueException("A pulse guide duration cannot be negative.");
        }

        // The firmware takes the duration as four digits, so anything longer than
        // this would be truncated into a shorter pulse without any warning.
        if (duration > 9999)
        {
            throw new InvalidValueException(
                $"A pulse guide of {duration} ms is longer than the 9999 ms the firmware accepts.");
        }

        if (duration == 0)
        {
            return;
        }

        char code = direction switch
        {
            GuideDirection.North => 'n',
            GuideDirection.South => 's',
            GuideDirection.East => 'e',
            GuideDirection.West => 'w',
            _ => throw new InvalidValueException($"{direction} is not a guide direction."),
        };

        RunCommandAndRefresh(() => Channel.SendAsync(
            $"Mg{code}{duration.ToString("0000", CultureInfo.InvariantCulture)}",
            CancellationToken.None));
    }

    /// <summary>
    /// Blocks until the mount stops moving, then waits out
    /// <see cref="SlewSettleTime"/>.
    /// </summary>
    private void WaitForSlewToComplete()
    {
        // Generous ceiling: a slew across the sky on a slow mount can take minutes,
        // but hanging a client forever on a mount that never stops is worse.
        TimeSpan limit = TimeSpan.FromMinutes(10);
        DateTime deadline = DateTime.UtcNow + limit;

        while (Slewing)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new DriverException(
                    $"The mount was still slewing after {limit.TotalMinutes} minutes.");
            }

            Thread.Sleep(200);
        }

        if (_slewSettleTime > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(_slewSettleTime));
        }
    }

    private void RequireUnparked()
    {
        if (AtPark)
        {
            throw new ParkedException("The mount is parked. Unpark it before moving it.");
        }
    }

    /// <summary>
    /// OnStep refuses equatorial gotos while it is in standby, meaning tracking is
    /// off. Reporting that as a plain failure sends clients hunting for a hardware
    /// fault, so the driver explains it.
    /// </summary>
    private void RequireTrackingForEquatorialSlew()
    {
        if (Snapshot.IsValid
            && !Snapshot.Status.IsTracking
            && Snapshot.Status.MountKind != MountKind.AltAzm)
        {
            throw new ASCOM.InvalidOperationException(
                "OnStep will not accept an equatorial goto while tracking is off. " +
                "Set Tracking to true first.");
        }
    }

    private static void ThrowIfGotoRejected(GotoResult result)
    {
        if (result.IsAccepted())
        {
            return;
        }

        throw result switch
        {
            GotoResult.MountParked => new ParkedException(result.Describe()),

            GotoResult.BelowHorizonLimit
                or GotoResult.AboveOverheadLimit
                or GotoResult.OutsideLimits => new InvalidValueException(result.Describe()),

            GotoResult.GotoInProgress
                or GotoResult.AlreadyInMotion
                or GotoResult.ControllerInStandby =>
                new ASCOM.InvalidOperationException(result.Describe()),

            _ => new DriverException(result.Describe()),
        };
    }
}
