using System.Globalization;
using OnStepX.Core.Protocol;

namespace OnStepX.Core.Simulation;

/// <summary>
/// Site, date and time commands of the simulated device.
/// </summary>
/// <remarks>
/// Two OnStep conventions that go against the usual and are replicated
/// here as is:
/// <list type="bullet">
///   <item>The longitude of <c>:Gg#</c> is <b>positive west</b>.</item>
///   <item>
///     The offset of <c>:GG#</c> is the one that must be <b>added</b> to
///     local time to get UT1, that is the opposite of the time zone value.
///   </item>
/// </list>
/// And OnStep <b>never</b> applies daylight saving: everything is standard time.
/// </remarks>
public sealed partial class FakeOnStepDevice
{
    /// <summary>Names of the four sites.</summary>
    public string[] SiteNames { get; } = ["Home", "Site 2", "Site 3", "Site 4"];

    /// <summary>Active site, from 0 to 3.</summary>
    public int ActiveSite { get; set; }

    /// <summary>DUT1 correction, in seconds.</summary>
    public double Dut1 { get; set; }

    private SimReply? DispatchSite(string cmd)
    {
        switch (cmd)
        {
            case "Gt":
                return SimReply.Text(Lx200Format.FormatDegreesLow(Mount.Latitude));
            case "GtH":
                return SimReply.Text(Lx200Format.FormatDegreesHighest(Mount.Latitude));

            case "Gg":
                return SimReply.Text(Lx200Format.FormatRotatorAngle(Mount.LongitudeWestPositive));
            case "GgH":
                return SimReply.Text(FormatLongitudeHighest(Mount.LongitudeWestPositive));

            case "Gv":
                return SimReply.Number(Mount.Elevation, "+0.0;-0.0");

            case "GG":
                return SimReply.Text(FormatUtcOffset(Mount.UtcOffset));

            case "Gc":
                return SimReply.Text("24");

            case "GL":
                return SimReply.Text(Mount.LocalStandardTimeAt(Now).ToString(
                    "HH:mm:ss", CultureInfo.InvariantCulture));
            case "GLH":
                return SimReply.Text(Mount.LocalStandardTimeAt(Now).ToString(
                    "HH:mm:ss.ffff", CultureInfo.InvariantCulture));
            case "Ga":
                return SimReply.Text(Mount.LocalStandardTimeAt(Now).ToString(
                    "hh:mm:ss", CultureInfo.InvariantCulture));

            case "GC":
                return SimReply.Text(Mount.LocalStandardTimeAt(Now).ToString(
                    "MM/dd/yy", CultureInfo.InvariantCulture));

            case "GS":
                return SimReply.Text(Lx200Format.FormatHoursHigh(LocalSiderealTimeHours));
            case "GSH":
                return SimReply.Text(FormatSiderealHighest(LocalSiderealTimeHours));

            case "GX80":
                return SimReply.Text(Ut1Time().ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture));
            case "GX81":
                return SimReply.Text(Ut1Time().ToString("MM/dd/yy", CultureInfo.InvariantCulture));

            // Watch the sense: 0 means ready, 1 means not ready.
            case "GX89":
                return SimReply.Text("0");

            case "GM": return SimReply.Text(SiteNames[0]);
            case "GN": return SimReply.Text(SiteNames[1]);
            case "GO": return SimReply.Text(SiteNames[2]);
            case "GP": return SimReply.Text(SiteNames[3]);

            case "W?": return SimReply.Int(ActiveSite);
            case "W0": ActiveSite = 0; return SimReply.None();
            case "W1": ActiveSite = 1; return SimReply.None();
            case "W2": ActiveSite = 2; return SimReply.None();
            case "W3": ActiveSite = 3; return SimReply.None();

            default:
                return DispatchSiteWithParameters(cmd);
        }
    }

    private SimReply? DispatchSiteWithParameters(string cmd)
    {
        if (cmd.StartsWith("St", StringComparison.Ordinal))
        {
            if (!Lx200Format.TryParse(cmd[2..], out double lat) || Math.Abs(lat) > 90)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Mount.Latitude = lat;
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("Sg", StringComparison.Ordinal))
        {
            if (!Lx200Format.TryParse(cmd[2..], out double lon) || Math.Abs(lon) > 360)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Mount.LongitudeWestPositive = lon;
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("Sv", StringComparison.Ordinal) && TryDouble(cmd[2..], out double elev))
        {
            Mount.Elevation = elev;
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("SG", StringComparison.Ordinal))
        {
            if (!TryParseUtcOffset(cmd[2..], out double offset))
            {
                Mount.LastError = CommandError.ParameterForm;
                return SimReply.Bool(false);
            }

            Mount.UtcOffset = offset;
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("SL", StringComparison.Ordinal))
        {
            if (!TimeSpan.TryParse(cmd[2..], CultureInfo.InvariantCulture, out TimeSpan tod)
                || tod < TimeSpan.Zero
                || tod >= TimeSpan.FromDays(1))
            {
                Mount.LastError = CommandError.ParameterForm;
                return SimReply.Bool(false);
            }

            Mount.SetLocalStandardTime(Mount.LocalStandardTimeAt(Now).Date + tod, Now);
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("SC", StringComparison.Ordinal))
        {
            string[] formats = ["MM/dd/yy", "MM/dd/yyyy"];

            if (!DateTime.TryParseExact(
                    cmd[2..], formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime date))
            {
                Mount.LastError = CommandError.ParameterForm;
                return SimReply.Bool(false);
            }

            Mount.SetLocalStandardTime(
                date + Mount.LocalStandardTimeAt(Now).TimeOfDay, Now);
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("SU", StringComparison.Ordinal) && TryDouble(cmd[2..], out double dut1))
        {
            if (Math.Abs(dut1) > 0.9)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            Dut1 = dut1;
            return SimReply.Bool(true);
        }

        // Site names, maximum 15 characters.
        int slot = cmd.Length > 1 ? "MNOP".IndexOf(cmd[1]) : -1;
        if (cmd.Length > 1 && cmd[0] == 'S' && slot >= 0)
        {
            string name = cmd[2..];
            if (name.Length > 15)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            SiteNames[slot] = name;
            return SimReply.Bool(true);
        }

        return null;
    }

    private DateTime Ut1Time() => Mount.LocalStandardTimeAt(Now).AddHours(Mount.UtcOffset);

    private static string FormatUtcOffset(double hours)
    {
        int totalMinutes = (int)Math.Round(Math.Abs(hours) * 60.0);
        char sign = hours < 0 ? '-' : '+';

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{totalMinutes / 60:00}:{totalMinutes % 60:00}");
    }

    private static bool TryParseUtcOffset(string text, out double hours)
    {
        hours = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int sign = text[0] == '-' ? -1 : 1;
        string body = text[0] is '+' or '-' ? text[1..] : text;

        string[] parts = body.Split(':');

        if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out int h) || h > 14)
        {
            return false;
        }

        int m = 0;
        if (parts.Length > 1
            && (!int.TryParse(parts[1], CultureInfo.InvariantCulture, out m) || m >= 60))
        {
            return false;
        }

        hours = sign * (h + (m / 60.0));
        return true;
    }

    private static string FormatLongitudeHighest(double degrees)
    {
        // sDDD*MM:SS.SSS
        char sign = degrees < 0 ? '-' : '+';
        double abs = Math.Abs(degrees);

        long totalMs = (long)Math.Round(abs * 3600.0 * 1000.0, MidpointRounding.AwayFromZero);
        long d = totalMs / 3_600_000;
        long m = totalMs % 3_600_000 / 60_000;
        long s = totalMs % 60_000 / 1000;
        long frac = totalMs % 1000;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{d:000}*{m:00}:{s:00}.{frac:000}");
    }

    private static string FormatSiderealHighest(double hours)
    {
        // HH:MM:SS.ss
        long totalCs = (long)Math.Round(
            Lx200Format.NormalizeHours(hours) * 3600.0 * 100.0,
            MidpointRounding.AwayFromZero);

        totalCs %= 24L * 3600 * 100;

        long h = totalCs / 360_000;
        long m = totalCs % 360_000 / 6000;
        long s = totalCs % 6000 / 100;
        long cs = totalCs % 100;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{h:00}:{m:00}:{s:00}.{cs:00}");
    }
}
