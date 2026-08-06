using System.Collections;
using ASCOM.Common.DeviceInterfaces;

namespace OnStepX.Devices;

/// <summary>
/// A single closed range of axis rates, in degrees per second.
/// </summary>
public sealed class TelescopeRate(double minimum, double maximum) : IRate
{
    /// <inheritdoc />
    public double Minimum { get; set; } = minimum;

    /// <inheritdoc />
    public double Maximum { get; set; } = maximum;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release. ASCOM requires the member on the collection types.
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Rate collection returned by <c>AxisRates</c>.
/// </summary>
/// <remarks>
/// ASCOM's collections are one based and expose the legacy
/// <see cref="IEnumerable"/> shape, which is why this is a hand written type rather
/// than a plain list.
/// </remarks>
public sealed class TelescopeAxisRates(IReadOnlyList<IRate> rates) : IAxisRates
{
    /// <inheritdoc />
    public int Count => rates.Count;

    /// <inheritdoc />
    public IRate this[int index] =>
        index >= 1 && index <= rates.Count
            ? rates[index - 1]
            : throw new ASCOM.InvalidValueException(
                $"AxisRates index {index} is out of range. Valid values are 1 to {rates.Count}.");

    /// <inheritdoc />
    public IEnumerator GetEnumerator() => rates.GetEnumerator();

    /// <inheritdoc />
    public void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>
/// Drive rate collection returned by <c>TrackingRates</c>.
/// </summary>
public sealed class TelescopeTrackingRates(IReadOnlyList<DriveRate> rates) : ITrackingRates
{
    /// <inheritdoc />
    public int Count => rates.Count;

    /// <inheritdoc />
    public DriveRate this[int index] =>
        index >= 1 && index <= rates.Count
            ? rates[index - 1]
            : throw new ASCOM.InvalidValueException(
                $"TrackingRates index {index} is out of range. Valid values are 1 to {rates.Count}.");

    /// <inheritdoc />
    public IEnumerator GetEnumerator() => rates.GetEnumerator();

    /// <inheritdoc />
    public void Dispose() => GC.SuppressFinalize(this);
}
