namespace OnStepX.Core.Simulation;

/// <summary>
/// A simulated focuser. OnStepX supports up to six.
/// </summary>
public sealed class SimulatedFocuser
{
    /// <summary>Axis, in <b>steps</b>.</summary>
    /// <remarks>
    /// The internal unit is steps on purpose. The protocol exposes both
    /// units, microns in uppercase and steps in lowercase, and keeping the
    /// state in steps is what lets the simulator catch a driver that
    /// confuses one for the other.
    /// </remarks>
    public SimulatedMotion Position { get; } = new(0, 2000);

    /// <summary>Minimum position, in steps.</summary>
    public long MinPosition { get; set; }

    /// <summary>Maximum position, in steps.</summary>
    public long MaxPosition { get; set; } = 77500;

    /// <summary>Microns per step.</summary>
    public double MicronsPerStep { get; set; } = 1.13507;

    /// <summary>Backlash, in steps.</summary>
    public int Backlash { get; set; }

    /// <summary>Temperature compensation enabled.</summary>
    public bool TempCompEnabled { get; set; }

    /// <summary>Compensation coefficient, in microns per degree.</summary>
    public double TempCompCoefficient { get; set; }

    /// <summary>Compensation dead band, in steps.</summary>
    public int TempCompDeadband { get; set; } = 1;

    /// <summary>Focuser temperature, in degrees.</summary>
    public double Temperature { get; set; } = 12.5;

    /// <summary>Delta relative to the compensation reference.</summary>
    public double TemperatureDelta { get; set; }

    /// <summary>DC motor power, in percent.</summary>
    public int DcPower { get; set; } = 75;

    /// <summary>Rate preset, from 1 to 9.</summary>
    public int RatePreset { get; set; } = 7;

    /// <summary>Whether it is a DC or pseudo absolute focuser.</summary>
    public bool IsDcMotor { get; set; }
}

/// <summary>
/// Simulated rotator.
/// </summary>
public sealed class SimulatedRotator
{
    /// <summary>Mechanical angle, in degrees.</summary>
    /// <summary>
    /// Mechanical angle in degrees, and how fast it moves.
    /// </summary>
    /// <remarks>
    /// 12 degrees per second, which is what a fast field rotator manages. The rate has to
    /// be realistic rather than conservative because of a case that looks pathological but
    /// is not: on a rotator whose travel is -180 to 180, the sky angles 180 and 225 map to
    /// mechanical 180 and -135, and the rotator cannot pass through its own limit, so
    /// moving 45 degrees in ASCOM terms means sweeping 315 degrees mechanically. At 3
    /// degrees per second that took nearly two minutes and exceeded the timeout a
    /// conformance check allows for a move.
    /// </remarks>
    public SimulatedMotion Angle { get; } = new(0, 12.0);

    /// <summary>Minimum angle, in degrees.</summary>
    public int MinAngle { get; set; } = -180;

    /// <summary>Maximum angle, in degrees.</summary>
    public int MaxAngle { get; set; } = 180;

    /// <summary>Degrees per step.</summary>
    public double DegreesPerStep { get; set; } = 0.01;

    /// <summary>Backlash, in steps.</summary>
    public int Backlash { get; set; }

    /// <summary>Field derotation enabled.</summary>
    public bool DerotationEnabled { get; set; }

    /// <summary>Derotation direction reversed.</summary>
    public bool DerotationReversed { get; set; }

    /// <summary>Rate preset, from 1 to 9.</summary>
    public int RatePreset { get; set; } = 7;

    /// <summary>
    /// Capability declared in <c>:GX98#</c>: <c>D</c> derotator, <c>R</c>
    /// rotation only, <c>N</c> none.
    /// </summary>
    public char Capability { get; set; } = 'D';
}

/// <summary>
/// Simulated stepper driver telemetry for one axis.
/// </summary>
/// <remarks>
/// Both halves are switchable because both are optional on real hardware. A plain
/// step and direction driver reports no status at all, and only some drivers implement
/// StallGuard, so the diagnostics page has to cope with either being missing rather than
/// showing a reassuring row of zeroes.
/// </remarks>
public sealed class SimulatedDriverStatus
{
    /// <summary>The driver reports status flags through <c>:GXUa#</c>.</summary>
    public bool ReportsStatus { get; set; } = true;

    /// <summary>
    /// Flag mnemonics. <c>ST</c> alone means standstill and nothing wrong; anything else
    /// is a fault worth showing.
    /// </summary>
    public string Flags { get; set; } = "ST";

    /// <summary>The driver reports StallGuard telemetry through <c>:GXSGn#</c>.</summary>
    public bool ReportsStallGuard { get; set; } = true;

    /// <summary>Current StallGuard reading.</summary>
    public int StallGuardValue { get; set; } = 240;

    /// <summary>Threshold at which a stall is declared.</summary>
    public int StallGuardTrip { get; set; } = 60;

    /// <summary>Milliseconds spent below the threshold.</summary>
    public int StallGuardBadMilliseconds { get; set; }

    /// <summary>Stall detection is armed.</summary>
    public bool StallGuardArmed { get; set; } = true;

    /// <summary>A stall has been latched.</summary>
    public bool StallGuardLatched { get; set; }
}

/// <summary>
/// Simulated environmental sensors.
/// </summary>
/// <remarks>
/// Each sensor can be disabled independently. This is essential to verify
/// that the driver throws <c>PropertyNotImplementedException</c> instead
/// of returning zero when the firmware does not have that sensor compiled
/// in, which is what ConformU checks and what poisons clients' safety
/// logic if done wrong.
/// </remarks>
public sealed class SimulatedWeather
{
    /// <summary>Whether there is an ambient temperature sensor.</summary>
    public bool HasTemperature { get; set; } = true;

    /// <summary>Whether there is a pressure sensor.</summary>
    public bool HasPressure { get; set; } = true;

    /// <summary>Whether there is a humidity sensor.</summary>
    public bool HasHumidity { get; set; } = true;

    /// <summary>Ambient temperature, in degrees.</summary>
    public double Temperature { get; set; } = 14.2;

    /// <summary>Pressure, in millibars.</summary>
    public double Pressure { get; set; } = 942.5;

    /// <summary>Relative humidity, in percent.</summary>
    public double Humidity { get; set; } = 61.0;

    /// <summary>Microcontroller temperature, in degrees.</summary>
    public double McuTemperature { get; set; } = 38.0;

    /// <summary>
    /// Dew point calculated with the Magnus approximation, the same way
    /// the firmware does, instead of storing it as an independent value.
    /// </summary>
    public double DewPoint
    {
        get
        {
            const double B = 17.62;
            const double C = 243.12;

            double h = Math.Max(1e-3, Humidity);
            double gamma = Math.Log(h / 100.0) + (B * Temperature / (C + Temperature));

            return C * gamma / (B - gamma);
        }
    }
}
