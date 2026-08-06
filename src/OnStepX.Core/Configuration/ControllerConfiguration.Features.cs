using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace OnStepX.Core.Configuration;

/// <summary>
/// Auxiliary features: the eight slots a controller can drive as switches, analog outputs,
/// dew heaters or camera shutter releases.
/// </summary>
/// <remarks>
/// <para>
/// These are read differently from the rest of the configuration, and deliberately so. The
/// typed helpers in the main part of this class decide presence from the <b>format</b> of a
/// single reply, which works because no other text field legitimately answers a bare
/// <c>0</c>. A feature slot breaks that premise twice over: a switch that is off answers
/// exactly <c>0</c>, and so does a slot that does not exist. So presence is established
/// once from the <c>:GXY0#</c> bitmap, and the state commands are read through
/// <see cref="Protocol.OnStepChannel.TryGetStringAsync"/> and parsed here.
/// </para>
/// <para>
/// The other reason this is not modelled like the accessories is that a slot is
/// heterogeneous: the payload of <c>:GXXn#</c> depends on what the slot was configured to
/// be, so the purpose has to be known before the state can be parsed at all.
/// </para>
/// </remarks>
public sealed partial class ControllerConfiguration
{
    /// <summary>Auxiliary feature slots the firmware can hold.</summary>
    public const int FeatureSlotCount = 8;

    /// <summary>Lowest value a feature output accepts.</summary>
    public const int FeatureValueMinimum = 0;

    /// <summary>Highest value a feature output accepts.</summary>
    public const int FeatureValueMaximum = 255;

    /// <summary>Lowest dew heater ramp temperature the firmware accepts, in degrees.</summary>
    public const double DewHeaterRampMinimum = -5.0;

    /// <summary>Highest dew heater ramp temperature the firmware accepts, in degrees.</summary>
    public const double DewHeaterRampMaximum = 20.0;

    /// <summary>
    /// Lists the configured auxiliary feature slots, with their names and purposes.
    /// </summary>
    /// <remarks>
    /// Two commands per configured slot plus one, so this is meant to be called once when a
    /// device connects and then kept. Nothing here changes until the controller is
    /// reconfigured, and the reply of <c>:GXXn#</c> cannot even be parsed without it.
    /// </remarks>
    /// <returns>
    /// The configured slots in slot order, or an empty list when this build has no
    /// auxiliary features at all.
    /// </returns>
    public async Task<IReadOnlyList<FeatureSlot>> ReadFeatureSlotsAsync(
        CancellationToken cancellationToken = default)
    {
        string? bitmap = await Channel.TryGetStringAsync("GXY0", cancellationToken)
            .ConfigureAwait(false);

        if (!IsFeatureBitmap(bitmap))
        {
            _logger.LogDebug(
                "This build reports no auxiliary features, :GXY0# answered {Reply}",
                bitmap ?? "nothing");

            return [];
        }

        List<FeatureSlot> slots = [];

        for (int slot = 1; slot <= FeatureSlotCount; slot++)
        {
            if (bitmap[slot - 1] != '1')
            {
                continue;
            }

            FeatureSlot? described = await ReadFeatureSlotAsync(slot, cancellationToken)
                .ConfigureAwait(false);

            if (described is not null)
            {
                slots.Add(described);
            }
        }

        return slots;
    }

    /// <summary>
    /// Reads the live state of one slot. The purpose must be the one
    /// <see cref="ReadFeatureSlotsAsync"/> reported, because the reply's shape depends on it.
    /// </summary>
    /// <returns>The state, or null when the controller did not answer.</returns>
    public async Task<FeatureState?> ReadFeatureStateAsync(
        int slot,
        FeaturePurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ValidateSlot(slot);

        string? raw = await Channel
            .TryGetStringAsync("GXX" + Integer(slot), cancellationToken)
            .ConfigureAwait(false);

        if (raw is null)
        {
            return null;
        }

        // Power monitoring, where it is compiled in, appends ";volts,amps,flags". That has
        // to come off before the fields are counted, or a switch would appear to carry
        // three of them.
        string body = raw;
        string? power = null;
        int suffix = raw.IndexOf(';', StringComparison.Ordinal);

        if (suffix >= 0)
        {
            body = raw[..suffix];
            power = raw[(suffix + 1)..];
        }

        string[] fields = body.Split(',');

        var state = new FeatureState
        {
            Slot = slot,
            Purpose = purpose,
            Raw = raw,
            PowerTelemetry = power,
        };

        return purpose switch
        {
            FeaturePurpose.Switch
                or FeaturePurpose.MomentarySwitch
                or FeaturePurpose.CoverSwitch
                or FeaturePurpose.AnalogOutput => state with
                {
                    Value = ToInt(Field(fields, 0)),
                },

            FeaturePurpose.DewHeater => state with
            {
                DewHeaterEnabled = ToBool(Field(fields, 0)),
                Zero = ToDouble(Field(fields, 1)),
                Span = ToDouble(Field(fields, 2)),
                DeltaT = ToDouble(Field(fields, 3)),
            },

            FeaturePurpose.Intervalometer => state with
            {
                CurrentCount = ToInt(Field(fields, 0)),
                Exposure = ToDouble(Field(fields, 1)),
                Delay = ToDouble(Field(fields, 2)),
                Count = ToInt(Field(fields, 3)),
            },

            // A hidden switch reaches no branch in the firmware and answers the unknown
            // command error, so there is nothing to parse. See FeaturePurpose.HiddenSwitch.
            _ => state,
        };
    }

    /// <summary>Reads the live state of several slots, in slot order.</summary>
    public async Task<IReadOnlyDictionary<int, FeatureState>> ReadFeatureStatesAsync(
        IEnumerable<FeatureSlot> slots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slots);

        Dictionary<int, FeatureState> states = [];

        foreach (FeatureSlot slot in slots.OrderBy(static s => s.Slot))
        {
            FeatureState? state = await ReadFeatureStateAsync(
                    slot.Slot, slot.Purpose, cancellationToken)
                .ConfigureAwait(false);

            if (state is not null)
            {
                states[slot.Slot] = state;
            }
        }

        return states;
    }

    /// <summary>
    /// Sets a slot's output value: 0 or 1 for a switch, 0 to 255 for an analog output.
    /// </summary>
    /// <remarks>
    /// The firmware validates against the purpose as well as the range, so a value of 2 sent
    /// to a plain switch is refused even though it is inside the range this method accepts.
    /// </remarks>
    public Task WriteFeatureValueAsync(
        int slot,
        int value,
        CancellationToken cancellationToken = default)
    {
        ValidateSlot(slot);

        if (value is < FeatureValueMinimum or > FeatureValueMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"An auxiliary feature value is {FeatureValueMinimum} to {FeatureValueMaximum}.");
        }

        return WriteAsync($"SXX{Integer(slot)},V{Integer(value)}", cancellationToken);
    }

    /// <summary>Starts or stops a dew heater's temperature ramp.</summary>
    public Task WriteDewHeaterEnabledAsync(
        int slot,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        WriteFeatureValueAsync(slot, enabled ? 1 : 0, cancellationToken);

    /// <summary>
    /// Sets the delta above the dew point at which the heater runs at full power.
    /// </summary>
    /// <remarks>
    /// <b>Read the slot back afterwards.</b> The firmware keeps zero strictly below span and
    /// silently moves whichever of the two it has to, so the value it kept is often not the
    /// value that was sent. It also writes to non volatile storage on every call, which is
    /// why this is a setup operation rather than something a client hammers.
    /// </remarks>
    public Task WriteDewHeaterZeroAsync(
        int slot,
        double celsius,
        CancellationToken cancellationToken = default)
    {
        ValidateSlot(slot);
        ValidateDewHeaterRamp(celsius, nameof(celsius));

        return WriteAsync($"SXX{Integer(slot)},Z{Decimal(celsius, "0.0")}", cancellationToken);
    }

    /// <summary>
    /// Sets the delta above the dew point at which the heater switches off.
    /// </summary>
    /// <remarks>See <see cref="WriteDewHeaterZeroAsync"/> for why the value has to be read back.</remarks>
    public Task WriteDewHeaterSpanAsync(
        int slot,
        double celsius,
        CancellationToken cancellationToken = default)
    {
        ValidateSlot(slot);
        ValidateDewHeaterRamp(celsius, nameof(celsius));

        return WriteAsync($"SXX{Integer(slot)},S{Decimal(celsius, "0.0")}", cancellationToken);
    }

    private async Task<FeatureSlot?> ReadFeatureSlotAsync(
        int slot,
        CancellationToken cancellationToken)
    {
        string? raw = await Channel
            .TryGetStringAsync("GXY" + Integer(slot), cancellationToken)
            .ConfigureAwait(false);

        // An unconfigured slot answers the numeric failure, and so does a build that does
        // not know the command at all.
        if (raw is null || IsBareBoolean(raw))
        {
            return null;
        }

        // Split on the last comma rather than the first: the purpose is always the final
        // field, and the firmware copies the configured name verbatim, so a name containing
        // a comma would otherwise be read as the purpose.
        int separator = raw.LastIndexOf(',');

        if (separator < 0 || ToInt(raw[(separator + 1)..]) is not { } code)
        {
            _logger.LogDebug(
                "Auxiliary feature slot {Slot} answered {Reply}, which is not a name and a purpose",
                slot,
                raw);

            return null;
        }

        return new FeatureSlot
        {
            Slot = slot,
            Name = raw[..separator].Trim(),
            Purpose = ToPurpose(code),
            Raw = raw,
        };
    }

    /// <summary>
    /// An eight character string of <c>0</c> and <c>1</c>. Anything else means there is
    /// nothing here, including the bare <c>0</c> a build without auxiliary features answers.
    /// </summary>
    private static bool IsFeatureBitmap([NotNullWhen(true)] string? reply) =>
        reply is { Length: FeatureSlotCount } && reply.All(static c => c is '0' or '1');

    private static FeaturePurpose ToPurpose(int code) =>
        code is >= (int)FeaturePurpose.Switch and <= (int)FeaturePurpose.CoverSwitch
            ? (FeaturePurpose)code
            : FeaturePurpose.None;

    private static void ValidateSlot(int slot)
    {
        if (slot is < 1 or > FeatureSlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slot),
                slot,
                $"An auxiliary feature slot is 1 to {FeatureSlotCount}.");
        }
    }

    private static void ValidateDewHeaterRamp(double celsius, string parameterName)
    {
        if (celsius is < DewHeaterRampMinimum or > DewHeaterRampMaximum
            || double.IsNaN(celsius))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                celsius,
                "A dew heater ramp temperature is "
                + $"{Decimal(DewHeaterRampMinimum, "0.0")} to "
                + $"{Decimal(DewHeaterRampMaximum, "0.0")} degrees.");
        }
    }

    private static string Field(string[] fields, int index) =>
        index < fields.Length ? fields[index] : string.Empty;

    /// <summary>
    /// Parses a numeric field, treating anything unreadable as absent rather than as zero.
    /// </summary>
    /// <remarks>
    /// The firmware prints an unavailable float as the literal <c>NAN</c>, which turns up in
    /// a dew heater's delta whenever the slot has no temperature sensor or the dew point is
    /// unknown, and in the power telemetry. The explicit check is here because whether
    /// <c>double.TryParse</c> accepts that spelling is not something to depend on.
    /// </remarks>
    private static double? ToDouble(string field)
    {
        string trimmed = field.Trim();

        if (trimmed.Equals("NAN", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(
                trimmed,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value)
            && !double.IsNaN(value)
            ? value
            : null;
    }

    private static int? ToInt(string field) =>
        int.TryParse(
            field.Trim(),
            NumberStyles.Integer | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : null;

    private static bool? ToBool(string field) => ToInt(field) is { } value ? value != 0 : null;
}
