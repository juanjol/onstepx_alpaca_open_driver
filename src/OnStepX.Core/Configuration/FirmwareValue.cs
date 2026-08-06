namespace OnStepX.Core.Configuration;

/// <summary>
/// A value read from the controller that may not exist in this firmware build.
/// </summary>
/// <remarks>
/// <para>
/// Almost the whole configuration command set is compile time gated in OnStepX. PEC,
/// encoders, StallGuard, the rotator, the focusers and every environmental sensor can all
/// be absent, and the same driver has to face any of those builds.
/// </para>
/// <para>
/// So a configuration reader that collapses "not present in this build" into a default
/// value produces a setup page that looks perfect against the simulator and lies against
/// real hardware, which is the exact failure this project's design exists to avoid. It is
/// the same rule the environmental sensors already follow: an absent sensor throws rather
/// than returning zero, because a zero dew point is believable and a client acts on it. A
/// meridian limit of zero minutes or a backlash of zero is just as believable.
/// </para>
/// <para>
/// The default value of the struct is the unsupported state, which is the safe default: a
/// field nobody managed to read reports itself as unread rather than as zero.
/// </para>
/// </remarks>
/// <typeparam name="T">Type of the parsed value.</typeparam>
public readonly record struct FirmwareValue<T>
{
    private FirmwareValue(bool isSupported, T? value, string? raw)
    {
        IsSupported = isSupported;
        Value = value;
        Raw = raw;
    }

    /// <summary>The firmware answered and the answer could be parsed.</summary>
    public bool IsSupported { get; }

    /// <summary>Parsed value, meaningful only when <see cref="IsSupported"/> is set.</summary>
    public T? Value { get; }

    /// <summary>
    /// Raw reply, kept even when parsing failed. It is what makes an unexpected
    /// firmware reply diagnosable instead of just missing.
    /// </summary>
    public string? Raw { get; }

    /// <summary>A value the firmware reported.</summary>
    public static FirmwareValue<T> Present(T value, string? raw = null) =>
        new(true, value, raw);

    /// <summary>A value this build does not provide.</summary>
    public static FirmwareValue<T> Absent(string? raw = null) =>
        new(false, default, raw);

    /// <summary>The value, or a fallback if the firmware does not provide it.</summary>
    public T Or(T fallback) => IsSupported && Value is not null ? Value : fallback;

    /// <inheritdoc />
    public override string ToString() =>
        IsSupported ? Value?.ToString() ?? string.Empty : "not supported";
}
