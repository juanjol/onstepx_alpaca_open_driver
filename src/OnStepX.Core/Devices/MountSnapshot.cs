using OnStepX.Core.Protocol;

namespace OnStepX.Core.Devices;

/// <summary>
/// One consistent reading of everything the mount reports about itself.
/// </summary>
/// <remarks>
/// ASCOM properties are synchronous and clients read them constantly: Conform and
/// NINA will ask for <c>Slewing</c>, <c>RightAscension</c> and <c>Declination</c>
/// several times a second. Serving each one with its own serial round trip would be
/// far too slow, and worse, would return values taken at slightly different
/// instants, so a client could see a right ascension and declination that never
/// existed together.
/// </remarks>
public sealed record MountSnapshot
{
    /// <summary>Empty snapshot, before the first poll completes.</summary>
    public static MountSnapshot Empty { get; } = new();

    /// <summary>When the reading was taken.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Parsed <c>:GU#</c> status.</summary>
    public MountStatus Status { get; init; } = MountStatus.Parse(string.Empty);

    /// <summary>Right ascension, in hours.</summary>
    public double RightAscension { get; init; }

    /// <summary>Declination, in degrees.</summary>
    public double Declination { get; init; }

    /// <summary>Altitude, in degrees.</summary>
    public double Altitude { get; init; }

    /// <summary>Azimuth, in degrees.</summary>
    public double Azimuth { get; init; }

    /// <summary>Local sidereal time, in hours.</summary>
    public double SiderealTime { get; init; }

    /// <summary>
    /// Tracking rate in Hz as reported by <c>:GT#</c>, or zero when not tracking.
    /// </summary>
    public double TrackingHz { get; init; }

    /// <summary>This snapshot holds real data rather than defaults.</summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// The mount is moving under a goto, a homing run or a park.
    /// </summary>
    public bool IsSlewing => Status.IsSlewing || Status.ParkState == ParkState.Parking;
}
