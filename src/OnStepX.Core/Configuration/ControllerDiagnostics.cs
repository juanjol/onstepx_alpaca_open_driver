using OnStepX.Core.Protocol;

namespace OnStepX.Core.Configuration;

/// <summary>Stepper driver status for one axis, from <c>:GXUa#</c>.</summary>
public sealed record AxisDriverStatus
{
    /// <summary>Axis number, 1 to 9.</summary>
    public required int Axis { get; init; }

    /// <summary>Flag mnemonics exactly as the firmware sent them.</summary>
    public required IReadOnlyList<string> Flags { get; init; }

    /// <summary>Raw reply.</summary>
    public required string Raw { get; init; }

    /// <summary>The driver reports a fault, an open load, a short or over temperature.</summary>
    /// <remarks>
    /// <c>ST</c>, standstill, is the only flag that is not a problem, so anything else
    /// present at all is worth showing in red.
    /// </remarks>
    public bool HasFault => Flags.Any(f => !string.Equals(f, "ST", StringComparison.Ordinal));
}

/// <summary>StallGuard telemetry for one axis, from <c>:GXSGn#</c>.</summary>
public sealed record StallGuardStatus
{
    /// <summary>Axis number.</summary>
    public required int Axis { get; init; }

    /// <summary>Current StallGuard reading.</summary>
    public required int Value { get; init; }

    /// <summary>Threshold at which a stall is declared.</summary>
    public required int TripLevel { get; init; }

    /// <summary>Milliseconds spent below the threshold.</summary>
    public required int BadMilliseconds { get; init; }

    /// <summary>Detection is armed.</summary>
    public required bool Armed { get; init; }

    /// <summary>A stall has been latched.</summary>
    public required bool Latched { get; init; }

    /// <summary>Raw reply.</summary>
    public required string Raw { get; init; }
}

/// <summary>
/// Read only controller telemetry, for the diagnostics page.
/// </summary>
/// <remarks>
/// Read on demand and per section rather than on page load. A full pass is dozens of
/// serialized commands competing with the polling loop, which is instant against the
/// simulator and several seconds at 9600 baud on a real link.
/// </remarks>
public sealed record ControllerDiagnostics
{
    /// <summary>Mount type in force.</summary>
    public FirmwareValue<OnStepMountType> MountType { get; init; }

    /// <summary>Coordinate mode, from <c>:GXEE#</c>.</summary>
    public FirmwareValue<int> CoordinateMode { get; init; }

    /// <summary>Axis 1 instrument angle in degrees, from <c>:GX42#</c>.</summary>
    public FirmwareValue<double> Axis1InstrumentDegrees { get; init; }

    /// <summary>Axis 2 instrument angle in degrees, from <c>:GX43#</c>.</summary>
    public FirmwareValue<double> Axis2InstrumentDegrees { get; init; }

    /// <summary>Axis 1 encoder count, from <c>:GX44#</c>.</summary>
    public FirmwareValue<long> Axis1EncoderCount { get; init; }

    /// <summary>Axis 2 encoder count, from <c>:GX45#</c>.</summary>
    public FirmwareValue<long> Axis2EncoderCount { get; init; }

    /// <summary>Axis 1 steps per degree, from <c>:GXE4#</c>.</summary>
    public FirmwareValue<double> Axis1StepsPerDegree { get; init; }

    /// <summary>Axis 2 steps per degree, from <c>:GXE5#</c>.</summary>
    public FirmwareValue<double> Axis2StepsPerDegree { get; init; }

    /// <summary>Axis 1 step frequency, from <c>:GXF3#</c>.</summary>
    public FirmwareValue<double> Axis1StepFrequency { get; init; }

    /// <summary>Axis 2 step frequency, from <c>:GXF4#</c>.</summary>
    public FirmwareValue<double> Axis2StepFrequency { get; init; }

    /// <summary>Controller temperature in degrees.</summary>
    public FirmwareValue<double> McuTemperature { get; init; }

    /// <summary>Last command error, from <c>:GE#</c>.</summary>
    public CommandError LastError { get; init; }

    /// <summary>Stepper driver status per axis.</summary>
    public IReadOnlyList<AxisDriverStatus> DriverStatus { get; init; } = [];

    /// <summary>StallGuard telemetry per axis.</summary>
    public IReadOnlyList<StallGuardStatus> StallGuard { get; init; } = [];
}
