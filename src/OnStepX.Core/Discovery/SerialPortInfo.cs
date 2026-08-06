namespace OnStepX.Core.Discovery;

/// <summary>
/// Candidate serial port, with what is known about it before probing it.
/// </summary>
public sealed record SerialPortInfo
{
    /// <summary>Name it is opened with: <c>COM7</c>, <c>/dev/ttyUSB0</c>.</summary>
    public required string PortName { get; init; }

    /// <summary>Device's friendly name, if known.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>USB vendor identifier, if known.</summary>
    public int? VendorId { get; init; }

    /// <summary>USB product identifier, if known.</summary>
    public int? ProductId { get; init; }

    /// <summary>
    /// Probing priority. Higher gets probed first. Calculated by
    /// <see cref="PortRanking"/>.
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// The port has been excluded without probing it, and why.
    /// </summary>
    public string? ExcludedReason { get; init; }

    /// <summary>Whether it is going to be probed.</summary>
    public bool IsCandidate => ExcludedReason is null;

    /// <inheritdoc />
    public override string ToString() =>
        FriendlyName is null ? PortName : $"{PortName} ({FriendlyName})";
}

/// <summary>
/// Result of successfully probing a port.
/// </summary>
public sealed record DiscoveredController
{
    /// <summary>Port where it answered.</summary>
    public required string PortName { get; init; }

    /// <summary>Baud rate at which it answered.</summary>
    public required int BaudRate { get; init; }

    /// <summary>Product name, from <c>:GVP#</c>.</summary>
    public required string ProductName { get; init; }

    /// <summary>Firmware version, from <c>:GVN#</c>.</summary>
    public string? FirmwareVersion { get; init; }

    /// <summary>Port's friendly name, if known.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>How long it took to answer.</summary>
    public TimeSpan ProbeDuration { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{ProductName} {FirmwareVersion} on {PortName} at {BaudRate} baud";
}
