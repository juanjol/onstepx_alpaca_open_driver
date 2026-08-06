namespace OnStepX.Core.Configuration;

/// <summary>
/// Focuser, rotator and environmental sensor side of the configuration.
/// </summary>
public sealed partial class ControllerConfiguration
{
    // Focuser

    /// <summary>
    /// Reads the configuration of the <b>currently active</b> focuser.
    /// </summary>
    /// <remarks>
    /// Everything is read through the lowercase commands, so every position, limit,
    /// backlash and deadband below is a count of <b>steps</b>. The uppercase forms of the
    /// same commands answer in microns, and mixing the two produces positions that look
    /// entirely plausible while being wrong by the microns per step factor, which is how
    /// an autofocus run converges confidently on the wrong place.
    /// <para>
    /// Which focuser is active is not changed here. The driver selects it when it
    /// connects, from the settings file, because switching it while a client is connected
    /// would silently redirect that client's focuser to different hardware.
    /// </para>
    /// </remarks>
    public async Task<FocuserConfiguration> ReadFocuserAsync(
        CancellationToken cancellationToken = default) =>
        new()
        {
            ActiveFocuser = await ReadInt32Async("FA", cancellationToken).ConfigureAwait(false),
            IsPresent = await ReadFlagAsync("Fa", '1', cancellationToken).ConfigureAwait(false),
            MinimumSteps = await ReadInt64Async("Fi", cancellationToken).ConfigureAwait(false),
            MaximumSteps = await ReadInt64Async("Fm", cancellationToken).ConfigureAwait(false),
            PositionSteps = await ReadInt64Async("Fg", cancellationToken).ConfigureAwait(false),
            MicronsPerStep = await ReadDoubleAsync("Fu", cancellationToken).ConfigureAwait(false),
            BacklashSteps = await ReadInt64Async("Fb", cancellationToken).ConfigureAwait(false),
            DeadbandSteps = await ReadInt64Async("Fd", cancellationToken).ConfigureAwait(false),
            DcMotorPowerPercent = await ReadInt32Async("FP", cancellationToken)
                .ConfigureAwait(false),
            IsDcMotor = await ReadFlagAsync("Fp", '1', cancellationToken).ConfigureAwait(false),
            TemperatureCompensation = await ReadFlagAsync("Fc", '1', cancellationToken)
                .ConfigureAwait(false),
            Coefficient = await ReadDoubleAsync("FC", cancellationToken).ConfigureAwait(false),
            Temperature = await ReadDoubleAsync("Ft", cancellationToken).ConfigureAwait(false),
            TemperatureDelta = await ReadDoubleAsync("Fe", cancellationToken).ConfigureAwait(false),
            WorkingRateMicronsPerSecond = await ReadInt64Async("FW", cancellationToken)
                .ConfigureAwait(false),
        };

    /// <summary>Writes the focuser backlash, in <b>steps</b>.</summary>
    public Task WriteFocuserBacklashAsync(
        long steps,
        CancellationToken cancellationToken = default)
    {
        if (steps is < 0 or > 32767)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steps), steps, "Focuser backlash is 0 to 32767 steps.");
        }

        // Lowercase, so the parameter is steps and not microns.
        return WriteAsync("Fb" + Integer(steps), cancellationToken);
    }

    /// <summary>Writes the temperature compensation deadband, in <b>steps</b>.</summary>
    public Task WriteFocuserDeadbandAsync(
        long steps,
        CancellationToken cancellationToken = default)
    {
        if (steps is < 0 or > 32767)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steps), steps, "The deadband is 0 to 32767 steps.");
        }

        return WriteAsync("Fd" + Integer(steps), cancellationToken);
    }

    /// <summary>
    /// Writes the temperature compensation coefficient, in microns per degree.
    /// </summary>
    /// <remarks>
    /// This one really is in microns, and it is not a conversion of anything: it is the
    /// physical drift of the focal plane per degree, which is what the user measures.
    /// </remarks>
    public Task WriteFocuserCoefficientAsync(
        double micronsPerDegree,
        CancellationToken cancellationToken = default)
    {
        if (double.IsNaN(micronsPerDegree) || Math.Abs(micronsPerDegree) > 999)
        {
            throw new ArgumentOutOfRangeException(
                nameof(micronsPerDegree),
                micronsPerDegree,
                "The coefficient must be within 999 microns per degree.");
        }

        return WriteAsync("FC" + Decimal(micronsPerDegree, "+0.0####;-0.0####"), cancellationToken);
    }

    /// <summary>Enables or disables temperature compensation.</summary>
    /// <remarks>
    /// Leaving it on does not block a move. From interface version 3 onwards ASCOM
    /// explicitly allows moving with compensation active, which is the opposite of what
    /// reasoning from first principles suggests.
    /// </remarks>
    public Task WriteFocuserTemperatureCompensationAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        WriteAsync(enabled ? "Fc1" : "Fc0", cancellationToken);

    /// <summary>Writes the DC motor power, in percent.</summary>
    public Task WriteFocuserDcPowerAsync(
        int percent,
        CancellationToken cancellationToken = default)
    {
        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percent), percent, "DC motor power is 0 to 100 percent.");
        }

        return WriteAsync("FP" + Integer(percent), cancellationToken);
    }

    /// <summary>Moves the focuser to its home position.</summary>
    public Task FocuserGoHomeAsync(CancellationToken cancellationToken = default) =>
        SendAsync("Fh", cancellationToken);

    /// <summary>Declares the current focuser position to be zero.</summary>
    public Task FocuserSetZeroAsync(CancellationToken cancellationToken = default) =>
        SendAsync("FZ", cancellationToken);

    /// <summary>Declares the current focuser position to be home.</summary>
    public Task FocuserSetHomeAsync(CancellationToken cancellationToken = default) =>
        SendAsync("FH", cancellationToken);

    /// <summary>Stops the focuser.</summary>
    public Task FocuserHaltAsync(CancellationToken cancellationToken = default) =>
        SendAsync("FQ", cancellationToken);

    // Rotator

    /// <summary>Reads the rotator configuration.</summary>
    public async Task<RotatorConfiguration> ReadRotatorAsync(
        CancellationToken cancellationToken = default)
    {
        FirmwareValue<string> capability = await ReadTextAsync("GX98", cancellationToken)
            .ConfigureAwait(false);

        FirmwareValue<string> status = await ReadTextAsync("rT", cancellationToken)
            .ConfigureAwait(false);

        return new RotatorConfiguration
        {
            Capability = capability.IsSupported
                ? FirmwareValue<RotatorCapability>.Present(
                    ParseCapability(capability.Value!), capability.Raw)
                : FirmwareValue<RotatorCapability>.Absent(capability.Raw),
            IsPresent = await ReadFlagAsync("rA", '1', cancellationToken).ConfigureAwait(false),
            MinimumDegrees = await ReadDoubleAsync("rI", cancellationToken).ConfigureAwait(false),
            MaximumDegrees = await ReadDoubleAsync("rM", cancellationToken).ConfigureAwait(false),
            DegreesPerStep = await ReadDoubleAsync("rD", cancellationToken).ConfigureAwait(false),
            BacklashSteps = await ReadInt64Async("rb", cancellationToken).ConfigureAwait(false),
            MechanicalDegrees = await ReadAngleAsync("rG", cancellationToken).ConfigureAwait(false),
            WorkingRateDegreesPerSecond = await ReadDoubleAsync("rW", cancellationToken)
                .ConfigureAwait(false),

            // :rT# answers a state letter, then optionally D for derotating, then a rate
            // digit.
            IsMoving = status.IsSupported
                ? FirmwareValue<bool>.Present(status.Value!.StartsWith('M'), status.Raw)
                : FirmwareValue<bool>.Absent(status.Raw),
            IsDerotating = status.IsSupported
                ? FirmwareValue<bool>.Present(
                    status.Value!.Contains('D', StringComparison.Ordinal), status.Raw)
                : FirmwareValue<bool>.Absent(status.Raw),
        };
    }

    private static RotatorCapability ParseCapability(string reply) =>
        reply.Trim() switch
        {
            "D" => RotatorCapability.Derotate,
            "R" => RotatorCapability.RotateOnly,
            "N" => RotatorCapability.None,
            _ => RotatorCapability.Unknown,
        };

    /// <summary>Writes the rotator backlash, in steps.</summary>
    public Task WriteRotatorBacklashAsync(
        long steps,
        CancellationToken cancellationToken = default)
    {
        if (steps is < 0 or > 32767)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steps), steps, "Rotator backlash is 0 to 32767 steps.");
        }

        return WriteAsync("rb" + Integer(steps), cancellationToken);
    }

    /// <summary>Enables or disables field derotation.</summary>
    public Task WriteDerotationAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(enabled ? "r+" : "r-", cancellationToken);

    /// <summary>
    /// Flips the derotation direction. The command is a toggle and reports nothing, so the
    /// resulting direction has to be read back from the rotator's own behaviour.
    /// </summary>
    public Task ToggleDerotationReverseAsync(CancellationToken cancellationToken = default) =>
        SendAsync("rR", cancellationToken);

    /// <summary>Moves the rotator to the parallactic angle.</summary>
    public Task RotatorGoToParallacticAsync(CancellationToken cancellationToken = default) =>
        SendAsync("rP", cancellationToken);

    /// <summary>Moves the rotator to its half travel position.</summary>
    public Task RotatorGoToHalfTravelAsync(CancellationToken cancellationToken = default) =>
        SendAsync("rC", cancellationToken);

    /// <summary>Declares the current rotator position to be half travel.</summary>
    public Task RotatorSetHalfTravelAsync(CancellationToken cancellationToken = default) =>
        SendAsync("rF", cancellationToken);

    /// <summary>Declares the current rotator position to be zero.</summary>
    public Task RotatorSetZeroAsync(CancellationToken cancellationToken = default) =>
        SendAsync("rZ", cancellationToken);

    /// <summary>Stops the rotator.</summary>
    public Task RotatorHaltAsync(CancellationToken cancellationToken = default) =>
        SendAsync("rQ", cancellationToken);

    // Environmental sensors

    /// <summary>
    /// Reads the environmental sensors, reporting each one's presence separately.
    /// </summary>
    /// <remarks>
    /// A sensor that is not compiled into the firmware answers the numeric failure rather
    /// than a reading, and that difference has to survive all the way to the page. Showing
    /// zero instead would be actively harmful: a dew point of zero is believable, and
    /// safety logic acts on it by closing a roof for no reason.
    /// </remarks>
    public async Task<WeatherConfiguration> ReadWeatherAsync(
        CancellationToken cancellationToken = default) =>
        new()
        {
            Temperature = await ReadDoubleAsync("GX9A", cancellationToken).ConfigureAwait(false),
            Pressure = await ReadDoubleAsync("GX9B", cancellationToken).ConfigureAwait(false),
            Humidity = await ReadDoubleAsync("GX9C", cancellationToken).ConfigureAwait(false),
            DewPoint = await ReadDoubleAsync("GX9E", cancellationToken).ConfigureAwait(false),
            McuTemperature = await ReadMcuTemperatureAsync(cancellationToken).ConfigureAwait(false),
        };

    /// <summary>
    /// Pushes an external weather station's readings into the controller, so that its own
    /// refraction compensation works on a site without the sensors fitted.
    /// </summary>
    public async Task PushWeatherAsync(
        double? temperature,
        double? pressure,
        double? humidity,
        CancellationToken cancellationToken = default)
    {
        if (temperature is { } t)
        {
            await WriteAsync("SX9A," + Decimal(t, "+0.0;-0.0"), cancellationToken)
                .ConfigureAwait(false);
        }

        if (pressure is { } p)
        {
            await WriteAsync("SX9B," + Decimal(p, "0.0"), cancellationToken).ConfigureAwait(false);
        }

        if (humidity is { } h)
        {
            await WriteAsync("SX9C," + Decimal(h, "0.0"), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the controller temperature.
    /// </summary>
    /// <remarks>
    /// This is the one numeric field where a bare zero really does mean absence: the
    /// command reference documents <c>0</c> as the reply on a board with no internal
    /// sensor, and no MCU sits at exactly zero degrees while running.
    /// </remarks>
    private async Task<FirmwareValue<double>> ReadMcuTemperatureAsync(
        CancellationToken cancellationToken)
    {
        FirmwareValue<double> value = await ReadDoubleAsync("GX9F", cancellationToken)
            .ConfigureAwait(false);

        return value.IsSupported && value.Value == 0
            ? FirmwareValue<double>.Absent(value.Raw)
            : value;
    }
}
