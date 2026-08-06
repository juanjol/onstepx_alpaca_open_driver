using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using OnStepX.Core.Configuration;
using OnStepX.Core.Protocol;

namespace OnStepX.Core.Simulation;

/// <summary>
/// Auxiliary feature commands of the simulated device.
/// </summary>
/// <remarks>
/// <para>
/// Three firmware behaviours are reproduced here deliberately, because they are the ones a
/// consumer gets wrong. <c>:GXYn#</c> reports a momentary switch and a cover switch as a plain
/// switch, so a driver cannot tell them apart and must not try. A hidden switch reports itself
/// present and then answers the unknown command error to <c>:GXXn#</c> while accepting writes
/// that go nowhere. And a dew heater without a temperature sensor answers the literal
/// <c>NAN</c> for its delta rather than a number.
/// </para>
/// <para>
/// The default configuration is a mixed one on purpose: a switch, an analog output, a dew
/// heater with a sensor, a dew heater without one, an intervalometer and a hidden switch. That
/// way a single conformance run exercises the slots that are exposed, the slots that must be
/// skipped and the reading that is unavailable, rather than only the happy path.
/// </para>
/// </remarks>
public sealed partial class FakeOnStepDevice
{
    /// <summary>
    /// The eight auxiliary feature slots, index 0 being slot 1.
    /// </summary>
    public SimulatedFeature[] Features { get; } =
    [
        new() { Purpose = FeaturePurpose.Switch, Name = "FANS" },
        new() { Purpose = FeaturePurpose.AnalogOutput, Name = "FLATPANEL", Value = 96 },
        new()
        {
            Purpose = FeaturePurpose.DewHeater,
            Name = "MAINDEW",
            DewHeaterEnabled = true,
            Zero = 1.5,
            Span = 8.0,
            DeltaT = 4.5,
        },

        // No temperature sensor on this one, so its delta is unavailable.
        new()
        {
            Purpose = FeaturePurpose.DewHeater,
            Name = "GUIDEDEW",
            Zero = -5.0,
            Span = 15.0,
            DeltaT = null,
        },
        new() { Purpose = FeaturePurpose.Intervalometer, Name = "SHUTTER" },
        new() { Purpose = FeaturePurpose.HiddenSwitch, Name = "BOOTPIN" },
        new(),
        new(),
    ];

    /// <summary>
    /// Whether this build has auxiliary features compiled in at all. When false every feature
    /// command answers the numeric failure, exactly as a build without them does.
    /// </summary>
    public bool FeaturesPresent { get; set; } = true;

    /// <summary>
    /// Whether power monitoring is compiled in, which makes every slot state carry a
    /// <c>;volts,amps,flags</c> suffix.
    /// </summary>
    /// <remarks>
    /// Off by default, because most builds do not have it, but a consumer has to survive it
    /// being on: a parser that splits the whole reply on commas would read a dew heater as
    /// having seven fields and would report the supply voltage as the delta above the dew point.
    /// </remarks>
    public bool PowerMonitoringPresent { get; set; }

    private SimReply? DispatchFeatures(string cmd)
    {
        if (!FeaturesPresent)
        {
            return null;
        }

        // Bitmap of configured slots. This is the only command that separates "the slot is
        // not there" from "the switch is off", because an off switch answers a bare 0.
        if (cmd == "GXY0")
        {
            return SimReply.Text(string.Concat(
                Features.Select(static f => f.Purpose == FeaturePurpose.None ? '0' : '1')));
        }

        if (TrySlot(cmd, "GXY", out SimulatedFeature? slot))
        {
            return slot.Purpose == FeaturePurpose.None
                ? SimReply.Bool(false)
                : SimReply.Text(
                    Truncate(slot.Name, 10)
                    + ","
                    + ((int)ReportedPurpose(slot.Purpose)).ToString(CultureInfo.InvariantCulture));
        }

        if (TrySlot(cmd, "GXX", out slot))
        {
            return DescribeFeature(slot);
        }

        return DispatchFeaturesWithParameters(cmd);
    }

    private SimReply? DispatchFeaturesWithParameters(string cmd)
    {
        // :SXXn,Lv# where L selects which of the slot's values is being written.
        if (!cmd.StartsWith("SXX", StringComparison.Ordinal)
            || cmd.Length < 7
            || cmd[3] is < '1' or > '8'
            || cmd[4] != ',')
        {
            return null;
        }

        SimulatedFeature slot = Features[cmd[3] - '1'];
        char selector = cmd[5];

        if (!TryDouble(cmd[6..], out double value))
        {
            Mount.LastError = CommandError.ParameterForm;
            return SimReply.Bool(false);
        }

        if (slot.Purpose == FeaturePurpose.None)
        {
            Mount.LastError = CommandError.CommandUnknown;
            return SimReply.Bool(false);
        }

        long rounded = (long)Math.Round(value, MidpointRounding.AwayFromZero);

        // The firmware stores the raw value before it ever looks at the purpose, so this
        // happens even for a slot that then refuses the command.
        if (selector == 'V' && rounded is >= 0 and <= 255)
        {
            slot.Value = (int)rounded;
        }

        return slot.Purpose switch
        {
            FeaturePurpose.Switch
                or FeaturePurpose.MomentarySwitch
                or FeaturePurpose.CoverSwitch => WriteBoundedValue(slot, selector, rounded, 1),

            FeaturePurpose.AnalogOutput => WriteBoundedValue(slot, selector, rounded, 255),

            FeaturePurpose.DewHeater => WriteDewHeater(slot, selector, value, rounded),

            FeaturePurpose.Intervalometer => WriteIntervalometer(slot, selector, value, rounded),

            // A hidden switch matches no branch in the firmware, so the value is stored, no
            // error is raised and success is reported, while the pin is never touched. This
            // is why a hidden switch must not be exposed as a writable channel.
            _ => SimReply.Bool(true),
        };
    }

    private SimReply WriteBoundedValue(
        SimulatedFeature slot,
        char selector,
        long value,
        int maximum)
    {
        if (selector != 'V')
        {
            Mount.LastError = CommandError.ParameterForm;
            return SimReply.Bool(false);
        }

        if (value < 0 || value > maximum)
        {
            Mount.LastError = CommandError.ParameterRange;
            return SimReply.Bool(false);
        }

        slot.Value = (int)value;
        return SimReply.Bool(true);
    }

    private SimReply WriteDewHeater(
        SimulatedFeature slot,
        char selector,
        double value,
        long rounded)
    {
        switch (selector)
        {
            case 'V':
                if (rounded is < 0 or > 1)
                {
                    Mount.LastError = CommandError.ParameterRange;
                    return SimReply.Bool(false);
                }

                slot.DewHeaterEnabled = rounded == 1;
                return SimReply.Bool(true);

            case 'Z':
            case 'S':
                if (value is < ControllerConfiguration.DewHeaterRampMinimum
                    or > ControllerConfiguration.DewHeaterRampMaximum)
                {
                    Mount.LastError = CommandError.ParameterRange;
                    return SimReply.Bool(false);
                }

                if (selector == 'Z')
                {
                    slot.Zero = value;
                }
                else
                {
                    slot.Span = value;
                }

                slot.EnforceRampOrder(movedZero: selector == 'Z');
                return SimReply.Bool(true);

            default:
                Mount.LastError = CommandError.ParameterForm;
                return SimReply.Bool(false);
        }
    }

    private SimReply WriteIntervalometer(
        SimulatedFeature slot,
        char selector,
        double value,
        long rounded)
    {
        // Note there is nothing to store for 'V'. The firmware enables the sequence but
        // reports no enabled flag in :GXXn#, so the change is genuinely unobservable, which
        // is why an intervalometer gets no on and off channel.
        (double low, double high) = selector switch
        {
            'V' => (0.0, 1.0),
            'E' => (0.0, 3600.0),
            'D' => (1.0, 3600.0),
            'C' => (0.0, 255.0),
            _ => (double.NaN, double.NaN),
        };

        if (double.IsNaN(low))
        {
            Mount.LastError = CommandError.ParameterForm;
            return SimReply.Bool(false);
        }

        if (value < low || value > high)
        {
            Mount.LastError = CommandError.ParameterRange;
            return SimReply.Bool(false);
        }

        switch (selector)
        {
            case 'E': slot.Exposure = value; break;
            case 'D': slot.Delay = value; break;
            case 'C': slot.Count = (int)rounded; break;
            default: break;
        }

        return SimReply.Bool(true);
    }

    private SimReply DescribeFeature(SimulatedFeature slot) =>
        slot.Purpose switch
        {
            FeaturePurpose.Switch
                or FeaturePurpose.MomentarySwitch
                or FeaturePurpose.CoverSwitch
                or FeaturePurpose.AnalogOutput => SimReply.Text(
                    slot.Value.ToString(CultureInfo.InvariantCulture) + Power(slot)),

            FeaturePurpose.DewHeater => SimReply.Text(string.Join(
                    ',',
                    slot.DewHeaterEnabled ? "1" : "0",
                    Ramp(slot.Zero),
                    Ramp(slot.Span),
                    slot.DeltaT is { } delta ? Ramp(delta) : "NAN")
                + Power(slot)),

            // No power suffix here. The firmware appends it to the other purposes and not to
            // this one.
            FeaturePurpose.Intervalometer => SimReply.Text(string.Join(
                ',',
                slot.CurrentCount.ToString(CultureInfo.InvariantCulture),
                Seconds(slot.Exposure, allowMilliseconds: true),
                Seconds(slot.Delay, allowMilliseconds: false),
                slot.Count.ToString(CultureInfo.InvariantCulture))),

            // An unconfigured slot and a hidden switch both fall past every branch in the
            // firmware and come back as the unknown command error.
            _ => SimReply.Bool(false),
        };

    /// <summary>
    /// The power monitoring suffix, or nothing on a build without it. An unavailable reading is
    /// the literal <c>NAN</c>, and the five flag characters are each either a fault letter or an
    /// exclamation mark.
    /// </summary>
    private string Power(SimulatedFeature slot)
    {
        if (!PowerMonitoringPresent)
        {
            return string.Empty;
        }

        return ";"
            + (slot.Volts is { } volts ? Ramp(volts) : "NAN")
            + ","
            + (slot.Amps is { } amps ? Ramp(amps) : "NAN")
            + ",!!!!!";
    }

    /// <summary>
    /// The purpose as <c>:GXYn#</c> reports it, which is not always the purpose the slot was
    /// configured with: a momentary switch and a cover switch are both flattened to a plain
    /// switch before the reply leaves the controller, while a hidden switch is not.
    /// </summary>
    private static FeaturePurpose ReportedPurpose(FeaturePurpose purpose) =>
        purpose is FeaturePurpose.MomentarySwitch or FeaturePurpose.CoverSwitch
            ? FeaturePurpose.Switch
            : purpose;

    private bool TrySlot(
        string cmd,
        string prefix,
        [NotNullWhen(true)] out SimulatedFeature? slot)
    {
        slot = null;

        if (cmd.Length != prefix.Length + 1
            || !cmd.StartsWith(prefix, StringComparison.Ordinal)
            || cmd[prefix.Length] is < '1' or > '8')
        {
            return false;
        }

        slot = Features[cmd[prefix.Length] - '1'];
        return true;
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private static string Ramp(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a duration the way the firmware does, with fewer decimals the larger the value
    /// gets. Reproduced rather than fixed at one decimal so a parser that assumes a shape is
    /// caught here rather than on hardware. Only an exposure goes as fine as milliseconds.
    /// </summary>
    private static string Seconds(double value, bool allowMilliseconds)
    {
        int decimals = allowMilliseconds && value < 1.0 ? 3
            : value < 10.0 ? 2
            : value < 30.0 ? 1
            : 0;

        return value.ToString(
            "F" + decimals.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
    }
}
