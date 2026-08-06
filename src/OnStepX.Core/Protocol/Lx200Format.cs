using System.Globalization;

namespace OnStepX.Core.Protocol;

/// <summary>
/// Reading and writing of the sexagesimal formats of OnStepX's extended
/// LX200 protocol.
/// </summary>
/// <remarks>
/// <para>
/// The protocol uses different separators depending on the command, and on
/// top of that low, high and maximum precision all coexist:
/// </para>
/// <list type="table">
///   <listheader><term>Format</term><description>Commands</description></listheader>
///   <item><term><c>sDD*MM</c></term><description><c>:GD#</c> low precision</description></item>
///   <item><term><c>sDD*MM:SS</c></term><description><c>:GD#</c> high precision</description></item>
///   <item><term><c>sDD*MM:SS.SSS</c></term><description><c>:GDH#</c></description></item>
///   <item><term><c>sDD*MM'SS</c></term><description><c>:GA#</c> high precision</description></item>
///   <item><term><c>sDD*MM'SS.SSS</c></term><description><c>:GAH#</c></description></item>
///   <item><term><c>HH:MM.T</c></term><description><c>:GR#</c> low precision</description></item>
///   <item><term><c>HH:MM:SS</c></term><description><c>:GR#</c> high precision</description></item>
///   <item><term><c>HH:MM:SS.SSSS</c></term><description><c>:GRH#</c></description></item>
///   <item><term><c>DDD*MM</c></term><description><c>:GZ#</c> low precision</description></item>
///   <item><term><c>DDD*MM'SS</c></term><description><c>:GZ#</c> high precision</description></item>
///   <item><term><c>DDD*MM'SS.SSS</c></term><description><c>:GZH#</c></description></item>
///   <item><term><c>sDDD*MM</c></term><description><c>:rG#</c> rotator</description></item>
/// </list>
/// <para>
/// Reading is permissive on purpose, it accepts any of the separators,
/// because the active precision depends on the firmware configuration and
/// is not always known beforehand. Writing, on the other hand, is strict:
/// each command demands its exact format.
/// </para>
/// <para>
/// Every decomposition works on integers in the smallest unit of the
/// format. This is how it is guaranteed that rounding never produces a
/// <c>60</c> in minutes or seconds, which is the classic error when
/// formatting sexagesimal values with floating point arithmetic.
/// </para>
/// </remarks>
public static class Lx200Format
{
    private const string DegreeSeparators = "*:'°′″’ ";

    /// <summary>
    /// Interprets any of the protocol's sexagesimal formats.
    /// </summary>
    /// <param name="text">
    /// Payload received, with or without the trailing <c>#</c>.
    /// </param>
    /// <param name="value">
    /// Decimal value. It is degrees or hours depending on the command,
    /// this function does not distinguish between them.
    /// </param>
    public static bool TryParse(string? text, out double value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim().TrimEnd('#').Trim();
        if (s.Length == 0)
        {
            return false;
        }

        int sign = 1;
        if (s[0] is '+' or '-')
        {
            sign = s[0] == '-' ? -1 : 1;
            s = s[1..];
        }

        // Split on any of the protocol's separators. Empty fields are
        // discarded to tolerate repeated or two character separators.
        string[] parts = s.Split(
            DegreeSeparators.ToCharArray(),
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length is 0 or > 3)
        {
            return false;
        }

        double total = 0;
        double scale = 1;

        foreach (string part in parts)
        {
            if (!double.TryParse(
                    part,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out double field))
            {
                return false;
            }

            // A negative field only makes sense in the overall sign,
            // which has already been consumed.
            if (field < 0)
            {
                return false;
            }

            // Minute and second fields cannot reach 60. This is checked
            // so a corrupted response does not pass as valid.
            if (scale > 1 && field >= 60)
            {
                return false;
            }

            total += field / scale;
            scale *= 60;
        }

        value = sign * total;
        return true;
    }

    /// <summary>
    /// Decomposes a value into units, minutes, and the remaining fraction,
    /// working on integers so that rounding carry is exact.
    /// </summary>
    /// <param name="value">Signed value.</param>
    /// <param name="ticksPerUnit">
    /// How many ticks a unit has. For example 3600 for whole seconds, or
    /// 3600000 for thousandths of a second.
    /// </param>
    /// <param name="wrapUnits">
    /// If greater than zero, the total is reduced modulo that number of
    /// units <b>after</b> rounding. This is needed for hours and azimuth:
    /// without it, 23.99999 hours would round to <c>24:00:00</c> and
    /// 359.9999 degrees to <c>360*00</c>, and neither is a valid protocol
    /// value.
    /// </param>
    private static (bool Negative, long Units, long Ticks) Decompose(
        double value,
        long ticksPerUnit,
        long wrapUnits = 0)
    {
        bool negative = value < 0;
        double magnitude = Math.Abs(value);

        // MidpointRounding.AwayFromZero replicates what snprintf does in C,
        // which is the reference this must match.
        long totalTicks = (long)Math.Round(magnitude * ticksPerUnit, MidpointRounding.AwayFromZero);

        if (wrapUnits > 0)
        {
            totalTicks %= wrapUnits * ticksPerUnit;
        }

        return (negative, totalTicks / ticksPerUnit, totalTicks % ticksPerUnit);
    }

    /// <summary>Units of a full turn in hours.</summary>
    private const long HoursPerTurn = 24;

    /// <summary>Units of a full turn in degrees.</summary>
    private const long DegreesPerTurn = 360;

    private static char Sign(bool negative) => negative ? '-' : '+';

    /// <summary>
    /// <c>sDD*MM</c>. Declination in low precision.
    /// </summary>
    public static string FormatDegreesLow(double degrees)
    {
        var (neg, deg, ticks) = Decompose(degrees, 60);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1:00}*{2:00}",
            Sign(neg), deg, ticks);
    }

    /// <summary>
    /// <c>sDD*MM:SS</c>. Declination in high precision.
    /// </summary>
    public static string FormatDegreesHigh(double degrees) =>
        FormatDegreesWithSeconds(degrees, ':', 0);

    /// <summary>
    /// <c>sDD*MM:SS.SSS</c>. Declination in maximum precision, <c>:GDH#</c>.
    /// </summary>
    public static string FormatDegreesHighest(double degrees) =>
        FormatDegreesWithSeconds(degrees, ':', 3);

    /// <summary>
    /// <c>sDD*MM'SS</c>. Altitude in high precision. Uses an apostrophe
    /// instead of a colon, unlike declination.
    /// </summary>
    public static string FormatAltitudeHigh(double degrees) =>
        FormatDegreesWithSeconds(degrees, '\'', 0);

    /// <summary>
    /// <c>sDD*MM'SS.SSS</c>. Altitude in maximum precision, <c>:GAH#</c>.
    /// </summary>
    public static string FormatAltitudeHighest(double degrees) =>
        FormatDegreesWithSeconds(degrees, '\'', 3);

    private static string FormatDegreesWithSeconds(
        double degrees,
        char minuteSeparator,
        int secondDecimals)
    {
        long ticksPerSecond = (long)Math.Pow(10, secondDecimals);
        var (neg, deg, ticks) = Decompose(degrees, 3600 * ticksPerSecond);

        long minutes = ticks / (60 * ticksPerSecond);
        long secondTicks = ticks % (60 * ticksPerSecond);
        long seconds = secondTicks / ticksPerSecond;
        long fraction = secondTicks % ticksPerSecond;

        string body = string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1:00}*{2:00}{3}{4:00}",
            Sign(neg), deg, minutes, minuteSeparator, seconds);

        return secondDecimals == 0
            ? body
            : body + "." + fraction.ToString(new string('0', secondDecimals), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>HH:MM.T</c>. Right ascension in low precision, where <c>T</c> is
    /// tenths of a minute.
    /// </summary>
    public static string FormatHoursLow(double hours)
    {
        // 600 ticks per hour, that is tenths of a minute.
        var (_, h, ticks) = Decompose(NormalizeHours(hours), 600, HoursPerTurn);

        long minutes = ticks / 10;
        long tenths = ticks % 10;

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}.{2:0}",
            h, minutes, tenths);
    }

    /// <summary>
    /// <c>HH:MM:SS</c>. Right ascension in high precision.
    /// </summary>
    public static string FormatHoursHigh(double hours) => FormatHours(hours, 0);

    /// <summary>
    /// <c>HH:MM:SS.SSSS</c>. Right ascension in maximum precision,
    /// <c>:GRH#</c>. This is four decimals, one more than declination.
    /// </summary>
    public static string FormatHoursHighest(double hours) => FormatHours(hours, 4);

    private static string FormatHours(double hours, int secondDecimals)
    {
        long ticksPerSecond = (long)Math.Pow(10, secondDecimals);
        var (_, h, ticks) = Decompose(NormalizeHours(hours), 3600 * ticksPerSecond, HoursPerTurn);

        long minutes = ticks / (60 * ticksPerSecond);
        long secondTicks = ticks % (60 * ticksPerSecond);
        long seconds = secondTicks / ticksPerSecond;
        long fraction = secondTicks % ticksPerSecond;

        string body = string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}",
            h, minutes, seconds);

        return secondDecimals == 0
            ? body
            : body + "." + fraction.ToString(new string('0', secondDecimals), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>DDD*MM</c>. Azimuth in low precision, unsigned and with three digits.
    /// </summary>
    public static string FormatAzimuthLow(double degrees)
    {
        var (_, deg, ticks) = Decompose(NormalizeDegrees(degrees), 60, DegreesPerTurn);
        return string.Format(CultureInfo.InvariantCulture, "{0:000}*{1:00}", deg, ticks);
    }

    /// <summary>
    /// <c>DDD*MM'SS</c>. Azimuth in high precision.
    /// </summary>
    public static string FormatAzimuthHigh(double degrees) => FormatAzimuth(degrees, 0);

    /// <summary>
    /// <c>DDD*MM'SS.SSS</c>. Azimuth in maximum precision, <c>:GZH#</c>.
    /// </summary>
    public static string FormatAzimuthHighest(double degrees) => FormatAzimuth(degrees, 3);

    private static string FormatAzimuth(double degrees, int secondDecimals)
    {
        long ticksPerSecond = (long)Math.Pow(10, secondDecimals);
        var (_, deg, ticks) = Decompose(NormalizeDegrees(degrees), 3600 * ticksPerSecond, DegreesPerTurn);

        long minutes = ticks / (60 * ticksPerSecond);
        long secondTicks = ticks % (60 * ticksPerSecond);
        long seconds = secondTicks / ticksPerSecond;
        long fraction = secondTicks % ticksPerSecond;

        string body = string.Format(
            CultureInfo.InvariantCulture,
            "{0:000}*{1:00}'{2:00}",
            deg, minutes, seconds);

        return secondDecimals == 0
            ? body
            : body + "." + fraction.ToString(new string('0', secondDecimals), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>sDDD*MM</c>. Rotator angle, signed and with three digits of degrees.
    /// </summary>
    public static string FormatRotatorAngle(double degrees)
    {
        var (neg, deg, ticks) = Decompose(degrees, 60);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1:000}*{2:00}",
            Sign(neg), deg, ticks);
    }

    /// <summary>
    /// Brings hours into the range [0, 24). Applied before formatting
    /// because the protocol does not allow negative hours or hours greater
    /// than 24.
    /// </summary>
    public static double NormalizeHours(double hours)
    {
        double h = hours % 24.0;
        return h < 0 ? h + 24.0 : h;
    }

    /// <summary>
    /// Brings degrees into the range [0, 360). Used for azimuth.
    /// </summary>
    public static double NormalizeDegrees(double degrees)
    {
        double d = degrees % 360.0;
        return d < 0 ? d + 360.0 : d;
    }
}
