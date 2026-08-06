namespace OnStepX.Core.Configuration;

/// <summary>Rotator capability declared by <c>:GX98#</c>.</summary>
public enum RotatorCapability
{
    /// <summary>Not reported.</summary>
    Unknown,

    /// <summary>No rotator in this build.</summary>
    None,

    /// <summary>Rotation only, no field derotation.</summary>
    RotateOnly,

    /// <summary>Rotation plus field derotation.</summary>
    Derotate,
}

/// <summary>Focuser configuration and live travel, as the controller holds them.</summary>
/// <remarks>
/// <b>Units are the whole game here.</b> The protocol exposes the same operations at two
/// scales, uppercase in microns and lowercase in raw steps, and the ASCOM position,
/// maximum and increment are all step counts. So everything below is in <b>steps</b>,
/// read through the lowercase commands, and <see cref="MicronsPerStep"/> exists only to
/// let the page show a physical distance next to a step count. Using it to convert a
/// position would produce plausible numbers that are wrong by that factor, which is the
/// worst available failure mode: autofocus would converge on the wrong place while every
/// reading looked reasonable.
/// </remarks>
public sealed record FocuserConfiguration
{
    /// <summary>Active focuser, 1 to 6, from <c>:FA#</c>.</summary>
    public FirmwareValue<int> ActiveFocuser { get; init; }

    /// <summary>A focuser is present, from <c>:Fa#</c>.</summary>
    public FirmwareValue<bool> IsPresent { get; init; }

    /// <summary>Minimum position in steps, from <c>:Fi#</c>.</summary>
    public FirmwareValue<long> MinimumSteps { get; init; }

    /// <summary>Maximum position in steps, from <c>:Fm#</c>.</summary>
    public FirmwareValue<long> MaximumSteps { get; init; }

    /// <summary>Current position in steps, from <c>:Fg#</c>.</summary>
    public FirmwareValue<long> PositionSteps { get; init; }

    /// <summary>Microns per step, from <c>:Fu#</c>. For display only.</summary>
    public FirmwareValue<double> MicronsPerStep { get; init; }

    /// <summary>Backlash in steps, from <c>:Fb#</c>.</summary>
    public FirmwareValue<long> BacklashSteps { get; init; }

    /// <summary>Temperature compensation deadband in steps, from <c>:Fd#</c>.</summary>
    public FirmwareValue<long> DeadbandSteps { get; init; }

    /// <summary>DC motor power in percent, from <c>:FP#</c>.</summary>
    public FirmwareValue<int> DcMotorPowerPercent { get; init; }

    /// <summary>The focuser is driven as a DC motor, from <c>:Fp#</c>.</summary>
    public FirmwareValue<bool> IsDcMotor { get; init; }

    /// <summary>Temperature compensation enabled, from <c>:Fc#</c>.</summary>
    public FirmwareValue<bool> TemperatureCompensation { get; init; }

    /// <summary>Compensation coefficient in microns per degree, from <c>:FC#</c>.</summary>
    public FirmwareValue<double> Coefficient { get; init; }

    /// <summary>Focuser temperature in degrees, from <c>:Ft#</c>.</summary>
    public FirmwareValue<double> Temperature { get; init; }

    /// <summary>Temperature change from the compensation baseline, from <c>:Fe#</c>.</summary>
    public FirmwareValue<double> TemperatureDelta { get; init; }

    /// <summary>Working move rate in microns per second, from <c>:FW#</c>.</summary>
    public FirmwareValue<long> WorkingRateMicronsPerSecond { get; init; }

    /// <summary>
    /// Compensation the firmware would apply for the current temperature delta, in
    /// microns. Derived rather than read, because no command reports it.
    /// </summary>
    public double? CompensationMicrons =>
        Coefficient.IsSupported && TemperatureDelta.IsSupported
            ? Coefficient.Value * TemperatureDelta.Value
            : null;
}

/// <summary>Rotator configuration and live travel.</summary>
public sealed record RotatorConfiguration
{
    /// <summary>What this build's rotator can do.</summary>
    public FirmwareValue<RotatorCapability> Capability { get; init; }

    /// <summary>The rotator is active, from <c>:rA#</c>.</summary>
    public FirmwareValue<bool> IsPresent { get; init; }

    /// <summary>Minimum mechanical angle in degrees, from <c>:rI#</c>.</summary>
    public FirmwareValue<double> MinimumDegrees { get; init; }

    /// <summary>Maximum mechanical angle in degrees, from <c>:rM#</c>.</summary>
    public FirmwareValue<double> MaximumDegrees { get; init; }

    /// <summary>Degrees per step, from <c>:rD#</c>.</summary>
    public FirmwareValue<double> DegreesPerStep { get; init; }

    /// <summary>Backlash in steps, from <c>:rb#</c>.</summary>
    public FirmwareValue<long> BacklashSteps { get; init; }

    /// <summary>
    /// Current <b>mechanical</b> angle in degrees, from <c>:rG#</c>. The sky angle a
    /// client sees is this plus the driver's sync offset, and that offset is a driver
    /// setting rather than a firmware one.
    /// </summary>
    public FirmwareValue<double> MechanicalDegrees { get; init; }

    /// <summary>Working slew rate in degrees per second, from <c>:rW#</c>.</summary>
    public FirmwareValue<double> WorkingRateDegreesPerSecond { get; init; }

    /// <summary>A move is under way, from <c>:rT#</c>.</summary>
    public FirmwareValue<bool> IsMoving { get; init; }

    /// <summary>Field derotation is running, from <c>:rT#</c>.</summary>
    public FirmwareValue<bool> IsDerotating { get; init; }
}

/// <summary>One environmental sensor as the controller reports it.</summary>
/// <remarks>
/// The whole point of reporting presence separately is that an absent sensor must never
/// be shown as zero. Zero degrees, zero humidity and a zero dew point are all
/// believable, so a client acts on them, and a false dew point closes an observatory
/// roof for no reason.
/// </remarks>
public sealed record WeatherConfiguration
{
    /// <summary>Ambient temperature in degrees, from <c>:GX9A#</c>.</summary>
    public FirmwareValue<double> Temperature { get; init; }

    /// <summary>Barometric pressure in millibars, from <c>:GX9B#</c>.</summary>
    public FirmwareValue<double> Pressure { get; init; }

    /// <summary>Relative humidity in percent, from <c>:GX9C#</c>.</summary>
    public FirmwareValue<double> Humidity { get; init; }

    /// <summary>Dew point in degrees, from <c>:GX9E#</c>.</summary>
    public FirmwareValue<double> DewPoint { get; init; }

    /// <summary>Controller temperature in degrees, from <c>:GX9F#</c>.</summary>
    public FirmwareValue<double> McuTemperature { get; init; }
}
