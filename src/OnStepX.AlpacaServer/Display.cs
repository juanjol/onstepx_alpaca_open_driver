using System.Globalization;
using OnStepX.Core.Configuration;
using OnStepX.Core.Protocol;

namespace OnStepX.AlpacaServer;

/// <summary>
/// Formatting for the setup pages.
/// </summary>
/// <remarks>
/// The reason this exists rather than being inlined in the markup is a single rule that has
/// to hold everywhere: a value the firmware does not provide reads <b>not supported</b> and
/// never as a number. A zero shown by mistake is worse than a gap, because a zero looks
/// like an answer and somebody acts on it.
/// </remarks>
public static class Display
{
    /// <summary>Text shown where the firmware provides nothing.</summary>
    public const string NotSupported = "not supported";

    /// <summary>Formats an optional number, with an optional unit.</summary>
    public static string Number(
        FirmwareValue<double> value,
        string format = "0.###",
        string? unit = null) =>
        value.IsSupported
            ? Append(value.Value.ToString(format, CultureInfo.InvariantCulture), unit)
            : NotSupported;

    /// <summary>Formats an optional whole number, with an optional unit.</summary>
    public static string Integer(FirmwareValue<long> value, string? unit = null) =>
        value.IsSupported
            ? Append(value.Value.ToString(CultureInfo.InvariantCulture), unit)
            : NotSupported;

    /// <summary>Formats an optional whole number, with an optional unit.</summary>
    public static string Integer(FirmwareValue<int> value, string? unit = null) =>
        value.IsSupported
            ? Append(value.Value.ToString(CultureInfo.InvariantCulture), unit)
            : NotSupported;

    /// <summary>Formats an optional flag as yes or no.</summary>
    public static string YesNo(FirmwareValue<bool> value) =>
        value.IsSupported ? (value.Value ? "yes" : "no") : NotSupported;

    /// <summary>Formats a flag as yes or no.</summary>
    public static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>Formats an optional enumeration value.</summary>
    public static string Text<T>(FirmwareValue<T> value) =>
        value.IsSupported ? Words(value.Value?.ToString()) : NotSupported;

    /// <summary>Formats an optional date and time.</summary>
    public static string Moment(FirmwareValue<DateTime> value) =>
        value.IsSupported
            ? value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : NotSupported;

    /// <summary>
    /// Formats an optional value in hours as <c>HH:MM:SS</c>, for sidereal time.
    /// </summary>
    public static string Hours(FirmwareValue<double> value) =>
        value.IsSupported ? Lx200Format.FormatHoursHigh(value.Value) : NotSupported;

    /// <summary>
    /// Formats a signed angle in degrees, minutes and seconds, which is how anybody with a
    /// star atlas reads a latitude.
    /// </summary>
    public static string Angle(FirmwareValue<double> value)
    {
        if (!value.IsSupported)
        {
            return NotSupported;
        }

        // The protocol form is sDD*MM:SS. Substituting the separators gives the
        // conventional notation rather than something half protocol and half astronomy.
        string[] parts = Lx200Format.FormatDegreesHigh(value.Value).Split('*', ':');

        return parts.Length == 3
            ? $"{parts[0]}° {parts[1]}' {parts[2]}\""
            : Lx200Format.FormatDegreesHigh(value.Value);
    }

    /// <summary>
    /// Turns a name written in the code's casing into something readable, so
    /// <c>RefractionSingleAxis</c> shows up as <c>Refraction single axis</c>.
    /// </summary>
    public static string Words(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder(name.Length + 6);

        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                text.Append(' ');
                text.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                text.Append(i == 0 ? name[i] : char.ToLowerInvariant(name[i]));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Turns a step period in microseconds into degrees per second, for the axis given.
    /// </summary>
    /// <remarks>
    /// Shown alongside the period because that is the number a user thinks in, but never
    /// used as an input. The conversion needs the steps per degree of the axis, and letting
    /// two fields recompute each other would put that arithmetic in the middle of a form
    /// for no benefit.
    /// </remarks>
    public static string DegreesPerSecond(
        FirmwareValue<double> periodMicroseconds,
        FirmwareValue<double> stepsPerDegree)
    {
        if (!periodMicroseconds.IsSupported
            || !stepsPerDegree.IsSupported
            || periodMicroseconds.Value <= 0
            || stepsPerDegree.Value <= 0)
        {
            return NotSupported;
        }

        double stepsPerSecond = 1_000_000.0 / periodMicroseconds.Value;

        return (stepsPerSecond / stepsPerDegree.Value)
            .ToString("0.0", CultureInfo.InvariantCulture) + " deg/s";
    }

    private static string Append(string text, string? unit) =>
        unit is null ? text : text + " " + unit;
}
