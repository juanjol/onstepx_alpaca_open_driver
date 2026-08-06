using System.Globalization;
using OnStepX.Core.Protocol;

namespace OnStepX.Core.Configuration;

/// <summary>
/// Read only telemetry, for the diagnostics page.
/// </summary>
public sealed partial class ControllerConfiguration
{
    /// <summary>
    /// Reads the controller telemetry.
    /// </summary>
    /// <param name="axes">
    /// Axes to ask about for driver status and StallGuard. Defaults to the two mount
    /// axes. Absent axes cost one expired deadline each, which is why the caller chooses
    /// rather than the driver probing all nine.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    public async Task<ControllerDiagnostics> ReadDiagnosticsAsync(
        IReadOnlyList<int>? axes = null,
        CancellationToken cancellationToken = default)
    {
        axes ??= [1, 2];

        var driverStatus = new List<AxisDriverStatus>();
        var stallGuard = new List<StallGuardStatus>();

        foreach (int axis in axes)
        {
            AxisDriverStatus? driver = await ReadDriverStatusAsync(axis, cancellationToken)
                .ConfigureAwait(false);

            if (driver is not null)
            {
                driverStatus.Add(driver);
            }

            StallGuardStatus? stall = await ReadStallGuardAsync(axis, cancellationToken)
                .ConfigureAwait(false);

            if (stall is not null)
            {
                stallGuard.Add(stall);
            }
        }

        return new ControllerDiagnostics
        {
            MountType = await ReadMountTypeAsync(cancellationToken).ConfigureAwait(false),
            CoordinateMode = await ReadInt32Async("GXEE", cancellationToken).ConfigureAwait(false),
            Axis1InstrumentDegrees = await ReadDoubleAsync("GX42", cancellationToken)
                .ConfigureAwait(false),
            Axis2InstrumentDegrees = await ReadDoubleAsync("GX43", cancellationToken)
                .ConfigureAwait(false),
            Axis1EncoderCount = await ReadInt64Async("GX44", cancellationToken)
                .ConfigureAwait(false),
            Axis2EncoderCount = await ReadInt64Async("GX45", cancellationToken)
                .ConfigureAwait(false),
            Axis1StepsPerDegree = await ReadDoubleAsync("GXE4", cancellationToken)
                .ConfigureAwait(false),
            Axis2StepsPerDegree = await ReadDoubleAsync("GXE5", cancellationToken)
                .ConfigureAwait(false),
            Axis1StepFrequency = await ReadDoubleAsync("GXF3", cancellationToken)
                .ConfigureAwait(false),
            Axis2StepFrequency = await ReadDoubleAsync("GXF4", cancellationToken)
                .ConfigureAwait(false),
            McuTemperature = await ReadMcuTemperatureAsync(cancellationToken).ConfigureAwait(false),
            LastError = await Channel.GetLastErrorAsync(cancellationToken).ConfigureAwait(false),
            DriverStatus = driverStatus,
            StallGuard = stallGuard,
        };
    }

    /// <summary>
    /// Reads the stepper driver flags of one axis.
    /// </summary>
    /// <remarks>
    /// The reply is a comma separated list of mnemonics. Only a servo or stepper driver
    /// that reports status has this at all, so an absent reply is the normal case on
    /// simpler hardware and not an error.
    /// </remarks>
    private async Task<AxisDriverStatus?> ReadDriverStatusAsync(
        int axis,
        CancellationToken cancellationToken)
    {
        FirmwareValue<string> reply = await ReadTextAsync(
                "GXU" + axis.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);

        if (!reply.IsSupported)
        {
            return null;
        }

        string[] flags = reply.Value!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new AxisDriverStatus
        {
            Axis = axis,
            Flags = flags,
            Raw = reply.Value!,
        };
    }

    /// <summary>Reads the StallGuard telemetry of one axis.</summary>
    private async Task<StallGuardStatus?> ReadStallGuardAsync(
        int axis,
        CancellationToken cancellationToken)
    {
        FirmwareValue<string> reply = await ReadTextAsync(
                "GXSG" + axis.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);

        if (!reply.IsSupported)
        {
            return null;
        }

        string[] fields = reply.Value!.Split(',', StringSplitOptions.TrimEntries);

        // sg,trip,badMs,armed,latched. A short reply means something other than
        // StallGuard answered, so it is reported as absent rather than half parsed.
        if (fields.Length < 5
            || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out int value)
            || !int.TryParse(fields[1], CultureInfo.InvariantCulture, out int trip)
            || !int.TryParse(fields[2], CultureInfo.InvariantCulture, out int badMilliseconds)
            || !int.TryParse(fields[3], CultureInfo.InvariantCulture, out int armed)
            || !int.TryParse(fields[4], CultureInfo.InvariantCulture, out int latched))
        {
            return null;
        }

        return new StallGuardStatus
        {
            Axis = axis,
            Value = value,
            TripLevel = trip,
            BadMilliseconds = badMilliseconds,
            Armed = armed != 0,
            Latched = latched != 0,
            Raw = reply.Value!,
        };
    }

    /// <summary>
    /// Reads the full firmware identity, for the diagnostics page.
    /// </summary>
    /// <remarks>
    /// The connection already reads part of this when it opens, but the identity of a
    /// connection that failed is exactly what the user needs to see, so the page can ask
    /// again on demand.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> ReadFirmwareIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        (string Label, string Command)[] items =
        [
            ("Product", "GVP"),
            ("Version", "GVN"),
            ("Name and version", "GVM"),
            ("Build date", "GVD"),
            ("Build time", "GVT"),
            ("Configuration", "GVC"),
            ("Hardware", "GVH"),
        ];

        var identity = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string label, string command) in items)
        {
            FirmwareValue<string> value = await ReadTextAsync(command, cancellationToken)
                .ConfigureAwait(false);

            if (value.IsSupported)
            {
                identity[label] = value.Value!;
            }
        }

        return identity;
    }
}
