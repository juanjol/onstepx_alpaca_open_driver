using OnStepX.Core.Astronomy;
using Xunit;
using Xunit.Abstractions;

namespace OnStepX.Core.Tests;

/// <summary>
/// The two coordinate conversions must be exact inverses of each other. If they are
/// not, an alt az slew lands somewhere else entirely: the simulator converts the
/// request to equatorial, slews there, and then reports altitude by converting back.
/// </summary>
public class CoordinatesTests(ITestOutputHelper output)
{
    [Theory]
    // Northern site.
    [InlineData(40.4168, 12.0, 45.0, 180.0)]
    [InlineData(40.4168, 0.0, 50.0, 90.0)]
    [InlineData(40.4168, 6.0, 30.0, 270.0)]
    [InlineData(40.4168, 18.0, 70.0, 0.0)]
    // Southern site.
    [InlineData(-33.9, 12.0, 45.0, 180.0)]
    [InlineData(-33.9, 3.0, 20.0, 45.0)]
    // Equator.
    [InlineData(0.0, 8.0, 60.0, 135.0)]
    // High latitude.
    [InlineData(68.0, 20.0, 15.0, 300.0)]
    public void HorizontalToEquatorialAndBackIsAnExactRoundTrip(
        double latitude,
        double localSiderealTime,
        double altitude,
        double azimuth)
    {
        (double ra, double dec) = Coordinates.HorizontalToEquatorial(
            altitude, azimuth, latitude, localSiderealTime);

        (double backAlt, double backAz) = Coordinates.EquatorialToHorizontal(
            ra, dec, latitude, localSiderealTime);

        output.WriteLine(
            $"alt {altitude} az {azimuth} -> ra {ra:F6} dec {dec:F6} -> alt {backAlt:F6} az {backAz:F6}");

        Assert.Equal(altitude, backAlt, precision: 6);
        Assert.Equal(azimuth, backAz, precision: 6);
    }

    [Theory]
    [InlineData(40.4168, 12.0, 6.0, 30.0)]
    [InlineData(40.4168, 0.0, 18.0, -20.0)]
    [InlineData(-33.9, 5.0, 10.0, -60.0)]
    [InlineData(0.0, 8.0, 2.0, 0.0)]
    public void EquatorialToHorizontalAndBackIsAnExactRoundTrip(
        double latitude,
        double localSiderealTime,
        double rightAscension,
        double declination)
    {
        (double alt, double az) = Coordinates.EquatorialToHorizontal(
            rightAscension, declination, latitude, localSiderealTime);

        (double backRa, double backDec) = Coordinates.HorizontalToEquatorial(
            alt, az, latitude, localSiderealTime);

        output.WriteLine(
            $"ra {rightAscension} dec {declination} -> alt {alt:F6} az {az:F6} -> ra {backRa:F6} dec {backDec:F6}");

        Assert.Equal(declination, backDec, precision: 6);
        Assert.Equal(rightAscension, backRa, precision: 6);
    }

    [Fact]
    public void AnObjectOnTheMeridianIsDueSouthFromANorthernSite()
    {
        // Hour angle zero puts the object on the meridian. From a northern site, an
        // object south of the zenith is due south, azimuth 180.
        const double Latitude = 40.0;
        const double Lst = 10.0;

        (double alt, double az) = Coordinates.EquatorialToHorizontal(
            rightAscensionHours: Lst, declinationDegrees: 20.0,
            latitudeDegrees: Latitude, localSiderealTimeHours: Lst);

        Assert.Equal(180.0, az, precision: 6);

        // Altitude on the meridian is 90 minus latitude plus declination.
        Assert.Equal(90.0 - Latitude + 20.0, alt, precision: 6);
    }

    [Fact]
    public void AnObjectNorthOfTheZenithIsDueNorth()
    {
        const double Latitude = 40.0;
        const double Lst = 10.0;

        (double alt, double az) = Coordinates.EquatorialToHorizontal(
            rightAscensionHours: Lst, declinationDegrees: 70.0,
            latitudeDegrees: Latitude, localSiderealTimeHours: Lst);

        Assert.Equal(0.0, az, precision: 6);
        Assert.Equal(90.0 - 70.0 + Latitude, alt, precision: 6);
    }

    [Fact]
    public void TheCelestialPoleSitsAtTheLatitudeAltitudeDueNorth()
    {
        (double alt, double az) = Coordinates.EquatorialToHorizontal(
            rightAscensionHours: 0.0, declinationDegrees: 90.0,
            latitudeDegrees: 40.4168, localSiderealTimeHours: 7.5);

        Assert.Equal(40.4168, alt, precision: 6);
        Assert.Equal(0.0, az, precision: 6);
    }

    [Fact]
    public void SiderealTimeAdvancesFasterThanSolarTime()
    {
        var start = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

        double t0 = Coordinates.LocalSiderealTime(start, 0.0);
        double t1 = Coordinates.LocalSiderealTime(start.AddHours(1), 0.0);

        double advance = t1 - t0;
        if (advance < 0)
        {
            advance += 24;
        }

        // A sidereal hour is about 1.0027 solar hours.
        Assert.InRange(advance, 1.002, 1.003);
    }

    [Fact]
    public void SiderealTimeShiftsWithLongitudeByOneHourPerFifteenDegrees()
    {
        var when = new DateTimeOffset(2026, 8, 5, 22, 0, 0, TimeSpan.Zero);

        double atGreenwich = Coordinates.LocalSiderealTime(when, 0.0);
        double fifteenEast = Coordinates.LocalSiderealTime(when, 15.0);

        double difference = fifteenEast - atGreenwich;
        if (difference < 0)
        {
            difference += 24;
        }

        Assert.Equal(1.0, difference, precision: 6);
    }
}
