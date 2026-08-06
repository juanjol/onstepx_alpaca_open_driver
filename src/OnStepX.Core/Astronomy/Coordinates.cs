namespace OnStepX.Core.Astronomy;

/// <summary>
/// Coordinate and sidereal time conversions.
/// </summary>
/// <remarks>
/// Precision that is enough for the simulator and for showing values in
/// the UI. The real driver does not need this math because OnStepX already
/// exposes altitude and azimuth with <c>:GA#</c> and <c>:GZ#</c>, but the
/// simulator does, so that the altitude and azimuth it returns are
/// consistent with its right ascension and declination. ConformU checks
/// the ranges, so made up values would fail.
/// </remarks>
public static class Coordinates
{
    /// <summary>Degrees to radians.</summary>
    public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>Radians to degrees.</summary>
    public static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

    /// <summary>
    /// Julian date from a UTC instant.
    /// </summary>
    public static double JulianDate(DateTimeOffset utc)
    {
        // Relies on OADate's epoch, which already handles the calendar.
        return utc.UtcDateTime.ToOADate() + 2415018.5;
    }

    /// <summary>
    /// Greenwich mean sidereal time, in hours.
    /// </summary>
    public static double GreenwichMeanSiderealTime(DateTimeOffset utc)
    {
        double jd = JulianDate(utc);
        double t = (jd - 2451545.0) / 36525.0;

        // Standard formula, in degrees.
        double gmst = 280.46061837
            + (360.98564736629 * (jd - 2451545.0))
            + (0.000387933 * t * t)
            - (t * t * t / 38710000.0);

        return NormalizeHours(gmst / 15.0);
    }

    /// <summary>
    /// Local sidereal time, in hours.
    /// </summary>
    /// <param name="longitudeEastPositive">
    /// Longitude with the <b>usual</b> sign, positive east. Watch out:
    /// OnStep uses the opposite convention.
    /// </param>
    public static double LocalSiderealTime(DateTimeOffset utc, double longitudeEastPositive) =>
        NormalizeHours(GreenwichMeanSiderealTime(utc) + (longitudeEastPositive / 15.0));

    /// <summary>
    /// Converts from equatorial to horizontal coordinates.
    /// </summary>
    /// <param name="rightAscensionHours">Right ascension, in hours.</param>
    /// <param name="declinationDegrees">Declination, in degrees.</param>
    /// <param name="latitudeDegrees">Latitude, positive north.</param>
    /// <param name="localSiderealTimeHours">Local sidereal time, in hours.</param>
    public static (double AltitudeDegrees, double AzimuthDegrees) EquatorialToHorizontal(
        double rightAscensionHours,
        double declinationDegrees,
        double latitudeDegrees,
        double localSiderealTimeHours)
    {
        // Hour angle, in degrees.
        double ha = ToRadians(NormalizeHours(localSiderealTimeHours - rightAscensionHours) * 15.0);
        double dec = ToRadians(declinationDegrees);
        double lat = ToRadians(latitudeDegrees);

        double sinAlt = (Math.Sin(dec) * Math.Sin(lat))
            + (Math.Cos(dec) * Math.Cos(lat) * Math.Cos(ha));
        sinAlt = Math.Clamp(sinAlt, -1.0, 1.0);
        double alt = Math.Asin(sinAlt);

        // Azimuth measured from north towards east.
        //
        // Both atan2 arguments come straight from the spherical triangle, with no
        // algebraic shortcuts. Shortcuts are what broke the inverse of this function:
        // it is easy to simplify one argument by a factor of cos(latitude) and forget
        // that the other needs the same factor, and atan2 only tolerates a shared
        // positive scale. The result then only came out right on the equator or due
        // north and south.
        double y = -Math.Cos(dec) * Math.Sin(ha);
        double x = (Math.Sin(dec) * Math.Cos(lat))
            - (Math.Cos(dec) * Math.Sin(lat) * Math.Cos(ha));
        double az = Math.Atan2(y, x);

        return (ToDegrees(alt), NormalizeDegrees(ToDegrees(az)));
    }

    /// <summary>
    /// Converts from horizontal to equatorial coordinates.
    /// </summary>
    public static (double RightAscensionHours, double DeclinationDegrees) HorizontalToEquatorial(
        double altitudeDegrees,
        double azimuthDegrees,
        double latitudeDegrees,
        double localSiderealTimeHours)
    {
        double alt = ToRadians(altitudeDegrees);
        double az = ToRadians(azimuthDegrees);
        double lat = ToRadians(latitudeDegrees);

        double sinDec = (Math.Sin(alt) * Math.Sin(lat))
            + (Math.Cos(alt) * Math.Cos(lat) * Math.Cos(az));
        sinDec = Math.Clamp(sinDec, -1.0, 1.0);
        double dec = Math.Asin(sinDec);

        // Same textbook form as the forward conversion. The earlier version used the
        // simplified numerator sin(alt) - sin(lat) sin(dec) for x while leaving y
        // unscaled, and those two differ by a factor of cos(latitude). The hour angle
        // then came out wrong everywhere except on the equator or due north and south,
        // which sent every alt az slew degrees off target.
        double y = -Math.Cos(alt) * Math.Sin(az);
        double x = (Math.Sin(alt) * Math.Cos(lat))
            - (Math.Cos(alt) * Math.Sin(lat) * Math.Cos(az));
        double ha = Math.Atan2(y, x);

        double ra = NormalizeHours(localSiderealTimeHours - (ToDegrees(ha) / 15.0));

        return (ra, ToDegrees(dec));
    }

    /// <summary>Brings hours into the range [0, 24).</summary>
    public static double NormalizeHours(double hours)
    {
        double h = hours % 24.0;
        return h < 0 ? h + 24.0 : h;
    }

    /// <summary>Brings degrees into the range [0, 360).</summary>
    /// <remarks>
    /// The final guard matters. A due north direction comes out of <c>atan2</c> as a
    /// vanishingly small negative angle, and adding 360 to it rounds straight back up
    /// to exactly 360 in double precision. Without the guard, an object due north
    /// reports azimuth 360 instead of 0, which is outside the range ASCOM allows.
    /// </remarks>
    public static double NormalizeDegrees(double degrees)
    {
        double d = degrees % 360.0;

        if (d < 0)
        {
            d += 360.0;
        }

        return d >= 360.0 ? 0.0 : d;
    }
}
