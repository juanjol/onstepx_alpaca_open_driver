using System.Globalization;

namespace OnStepX.Core.Protocol;

/// <summary>
/// Parsing and formatting of the controller's clock and site conventions.
/// </summary>
/// <remarks>
/// <para>
/// These live in one place because two of them are sign conventions that are the
/// opposite of what everybody expects, and a second implementation is how the two
/// drift apart:
/// </para>
/// <list type="bullet">
/// <item>
/// OnStep stores longitude <b>positive west</b>, the opposite of the geographic and
/// ASCOM convention. A flipped sign puts the mount on the other side of the planet
/// and every goto lands somewhere else.
/// </item>
/// <item>
/// The offset from <c>:GG#</c> is the value to <b>add</b> to local time to reach UT1,
/// which is the negative of the timezone offset people write down. So UTC is local
/// plus that offset, not minus.
/// </item>
/// </list>
/// <para>
/// And one behaviour that is not a sign at all: OnStep never applies daylight saving.
/// Its clock is always standard time, so a summer wall clock reading sent verbatim
/// leaves the mount an hour out and every goto off by fifteen degrees.
/// </para>
/// </remarks>
public static class OnStepClock
{
    private static readonly string[] DateFormats = ["MM/dd/yy", "MM/dd/yyyy"];

    /// <summary>
    /// Converts a longitude read from the controller to the ASCOM convention,
    /// positive east.
    /// </summary>
    public static double ToAscomLongitude(double onStepWestPositive) => -onStepWestPositive;

    /// <summary>
    /// Converts an ASCOM longitude, positive east, to the controller convention.
    /// </summary>
    public static double ToOnStepLongitude(double ascomEastPositive) => -ascomEastPositive;

    /// <summary>
    /// Parses the local standard date and time from <c>:GC#</c> and <c>:GL#</c>.
    /// </summary>
    public static bool TryParseLocalStandard(string? date, string? time, out DateTime local)
    {
        local = default;

        if (!DateTime.TryParseExact(
                (date ?? string.Empty).Trim(),
                DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime datePart))
        {
            return false;
        }

        if (!TimeSpan.TryParse(
                (time ?? string.Empty).Trim(), CultureInfo.InvariantCulture, out TimeSpan timePart))
        {
            return false;
        }

        local = datePart.Add(timePart);
        return true;
    }

    /// <summary>
    /// Parses <c>:GG#</c>, which OnStep formats as <c>sHH</c> or <c>sHH:MM</c>.
    /// </summary>
    /// <remarks>
    /// The result is the offset to <b>add</b> to local time to obtain UT1, so a site
    /// in central Europe in winter reports <c>-01</c> and not <c>+01</c>.
    /// </remarks>
    public static bool TryParseUtcOffsetHours(string? reply, out double hours)
    {
        hours = 0;

        string text = (reply ?? string.Empty).Trim().TrimEnd('#').Trim();
        if (text.Length == 0)
        {
            return false;
        }

        int sign = text[0] == '-' ? -1 : 1;
        string body = text[0] is '+' or '-' ? text[1..] : text;

        string[] parts = body.Split(':');

        if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out int wholeHours))
        {
            return false;
        }

        int minutes = 0;
        if (parts.Length > 1
            && !int.TryParse(parts[1], CultureInfo.InvariantCulture, out minutes))
        {
            return false;
        }

        hours = sign * (wholeHours + (minutes / 60.0));
        return true;
    }

    /// <summary>
    /// Formats the parameter of <c>:SGsHH:MM#</c>.
    /// </summary>
    /// <remarks>
    /// The firmware comments restrict the minutes to <c>00</c>, <c>30</c> or
    /// <c>45</c>, which covers every real timezone, so the value is snapped to the
    /// nearest of those rather than rejected.
    /// </remarks>
    public static string FormatUtcOffset(double hours)
    {
        int sign = hours < 0 ? -1 : 1;
        double magnitude = Math.Abs(hours);

        int wholeHours = (int)Math.Floor(magnitude);
        int minutes = (int)Math.Round((magnitude - wholeHours) * 60);

        minutes = minutes switch
        {
            < 15 => 0,
            < 38 => 30,
            < 53 => 45,
            _ => 60,
        };

        if (minutes == 60)
        {
            wholeHours++;
            minutes = 0;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1:00}:{2:00}",
            sign < 0 ? '-' : '+',
            wholeHours,
            minutes);
    }

    /// <summary>Formats the parameter of <c>:SCMM/DD/YY#</c>.</summary>
    public static string FormatDate(DateTime localStandard) =>
        localStandard.ToString("MM/dd/yy", CultureInfo.InvariantCulture);

    /// <summary>Formats the parameter of <c>:SLHH:MM:SS#</c>.</summary>
    public static string FormatTime(DateTime localStandard) =>
        localStandard.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
