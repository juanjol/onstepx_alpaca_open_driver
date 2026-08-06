namespace OnStepX.Core.Configuration;

/// <summary>
/// What an auxiliary feature slot controls, as <c>:GXYn#</c> reports it.
/// </summary>
/// <remarks>
/// <para>
/// The numbers are the firmware's own, from <c>Constants.h</c>. They are not an ASCOM
/// concept and they are not contiguous in practice, because <c>:GXYn#</c> reports
/// <see cref="MomentarySwitch"/> and <see cref="CoverSwitch"/> as <see cref="Switch"/>
/// before the reply leaves the controller. So a driver reading a live mount only ever sees
/// <see cref="Switch"/>, <see cref="AnalogOutput"/>, <see cref="DewHeater"/>,
/// <see cref="Intervalometer"/> and <see cref="HiddenSwitch"/>. The two collapsed values
/// are kept here because the simulator configures slots the way a user configures the
/// firmware, and reproducing the collapse is part of behaving like the firmware.
/// </para>
/// <para>
/// One consequence worth knowing: a cover switch's semantics, where a value of 1 means
/// closed rather than powered, are invisible to the driver. There is no command that
/// distinguishes it.
/// </para>
/// </remarks>
public enum FeaturePurpose
{
    /// <summary>The slot is not configured, or its purpose could not be read.</summary>
    None = 0,

    /// <summary>A plain on and off switch.</summary>
    Switch = 1,

    /// <summary>A pulse width modulated output, 0 to 255 for 0 to 100 percent power.</summary>
    AnalogOutput = 2,

    /// <summary>A dew heater with its own temperature driven power ramp.</summary>
    DewHeater = 3,

    /// <summary>A camera shutter release.</summary>
    Intervalometer = 4,

    /// <summary>
    /// A switch that releases itself after a short pulse. Reported as
    /// <see cref="Switch"/> by <c>:GXYn#</c>.
    /// </summary>
    MomentarySwitch = 5,

    /// <summary>
    /// A slot that exists only to hold a pin state from boot.
    /// </summary>
    /// <remarks>
    /// <b>Not usable through the command set, and not a bug in this driver.</b> The
    /// firmware marks the slot present in <c>:GXY0#</c> and reports the purpose in
    /// <c>:GXYn#</c>, but <c>:GXXn#</c> matches no branch and answers the unknown command
    /// error, while <c>:SXXn,Vv#</c> stores the value and returns success without ever
    /// writing the pin. Any consumer must skip these slots: exposing one would report a
    /// read error to a client and would accept writes that do nothing.
    /// </remarks>
    HiddenSwitch = 6,

    /// <summary>
    /// A servo driven telescope cover, where 1 is closed. Reported as
    /// <see cref="Switch"/> by <c>:GXYn#</c>.
    /// </summary>
    CoverSwitch = 7,
}

/// <summary>
/// Which auxiliary feature purposes can actually be driven through the command set.
/// </summary>
/// <remarks>
/// This is a protocol fact rather than a driver preference, which is why it lives here and not
/// in the device or the setup page. Two of the seven purposes cannot be operated honestly no
/// matter what a consumer does, and both the device that hides them and the page that explains
/// why have to agree about which and about the reason. Saying it once is what keeps them from
/// drifting into telling a user two different stories.
/// </remarks>
public static class FeaturePurposes
{
    /// <summary>
    /// Whether a slot of this purpose can be both read and written through the command set.
    /// </summary>
    public static bool IsControllable(this FeaturePurpose purpose) =>
        purpose is FeaturePurpose.Switch
            or FeaturePurpose.MomentarySwitch
            or FeaturePurpose.CoverSwitch
            or FeaturePurpose.AnalogOutput
            or FeaturePurpose.DewHeater;

    /// <summary>
    /// Why a slot of this purpose cannot be controlled, in a sentence fit to show a user.
    /// </summary>
    public static string UncontrollableReason(this FeaturePurpose purpose) => purpose switch
    {
        FeaturePurpose.Intervalometer =>
            "The controller reports the frame counters but never whether a sequence is running, "
            + "so a switch for it could be written and never read back honestly.",

        FeaturePurpose.HiddenSwitch =>
            "The controller marks the slot present, refuses to report its state, and reports "
            + "success for writes it never carries out. A client would see a switch that always "
            + "appeared to work and never did.",

        _ => "This driver does not recognise that purpose.",
    };
}

/// <summary>
/// An auxiliary feature slot the controller reports as configured, from <c>:GXYn#</c>.
/// </summary>
/// <remarks>
/// This is the static half of a slot, fixed by the firmware configuration and unchanged
/// until the controller is reflashed. It is read once and kept, which is what allows the
/// polling loop to issue only the state command.
/// </remarks>
public sealed record FeatureSlot
{
    /// <summary>Slot number, 1 to 8, as used in the command payload.</summary>
    public required int Slot { get; init; }

    /// <summary>The name the firmware was configured with, up to ten characters.</summary>
    public required string Name { get; init; }

    /// <summary>What the slot controls.</summary>
    public required FeaturePurpose Purpose { get; init; }

    /// <summary>The unparsed reply, kept so an unexpected format stays diagnosable.</summary>
    public string? Raw { get; init; }
}

/// <summary>
/// The live state of one auxiliary feature slot, from <c>:GXXn#</c>.
/// </summary>
/// <remarks>
/// <para>
/// Which fields are populated depends on the purpose, because the firmware prints a
/// different payload for each. A field the reply did not carry, or carried as the literal
/// <c>NAN</c>, is null. Null means the controller did not report it, never zero: a dew
/// heater delta of zero degrees above the dew point is the moment the heater is most needed,
/// so confusing the two would be actively harmful.
/// </para>
/// <para>
/// Plain nullables are used here rather than <see cref="FirmwareValue{T}"/>, which exists
/// to record whether an individual command is compiled into the build. Presence is not a
/// per command question for a feature slot: it is settled once, for the whole slot, by the
/// <c>:GXY0#</c> bitmap.
/// </para>
/// </remarks>
public sealed record FeatureState
{
    /// <summary>Slot number, 1 to 8.</summary>
    public required int Slot { get; init; }

    /// <summary>The purpose the reply was parsed as.</summary>
    public required FeaturePurpose Purpose { get; init; }

    /// <summary>
    /// Output value for a switch or an analog output, 0 to 1 or 0 to 255.
    /// </summary>
    public int? Value { get; init; }

    /// <summary>The dew heater's temperature ramp is running.</summary>
    public bool? DewHeaterEnabled { get; init; }

    /// <summary>
    /// Delta above the dew point at which the heater runs at full power, in degrees.
    /// </summary>
    public double? Zero { get; init; }

    /// <summary>
    /// Delta above the dew point at which the heater switches off, in degrees.
    /// </summary>
    public double? Span { get; init; }

    /// <summary>
    /// How far the slot's own temperature sensor currently is above the dew point, in
    /// degrees. Null when the slot has no sensor or the dew point is unavailable.
    /// </summary>
    public double? DeltaT { get; init; }

    /// <summary>Frames taken so far, for an intervalometer.</summary>
    public int? CurrentCount { get; init; }

    /// <summary>Exposure length in seconds, for an intervalometer.</summary>
    public double? Exposure { get; init; }

    /// <summary>Delay between frames in seconds, for an intervalometer.</summary>
    public double? Delay { get; init; }

    /// <summary>Frames to take, for an intervalometer.</summary>
    public int? Count { get; init; }

    /// <summary>The unparsed reply, including any power telemetry.</summary>
    public string? Raw { get; init; }

    /// <summary>
    /// The power monitoring suffix, without its leading semicolon, when the firmware was
    /// built with it. Kept for diagnostics and not otherwise interpreted.
    /// </summary>
    public string? PowerTelemetry { get; init; }
}
