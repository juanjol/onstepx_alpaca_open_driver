using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OnStepX.Core.Protocol;

namespace OnStepX.Core.Configuration;

/// <summary>
/// Reads and writes the settings the OnStepX controller keeps in its own non volatile
/// storage.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of the setup UI. The driver settings file holds what belongs to
/// the driver, such as which port to open and whether to push the clock on connect, while
/// everything here belongs to the mount and survives reinstalling the driver. Keeping the
/// two apart is why a setup page can say honestly whether saving changed the mount or only
/// the driver, which the old WinForms dialogs could not.
/// </para>
/// <para>
/// It knows nothing about ASCOM on purpose, so it stays testable against
/// <c>FakeOnStepDevice</c> with no server and no client, exactly like the rest of Core.
/// </para>
/// <para>
/// <b>Every read is individually optional.</b> Almost the whole configuration command set
/// is compile time gated in the firmware, so a build without PEC, without encoders or
/// without a rotator simply has no handler for those frames. See
/// <see cref="FirmwareValue{T}"/> for why that absence has to reach the UI instead of being
/// flattened into a zero.
/// </para>
/// </remarks>
public sealed partial class ControllerConfiguration
{
    private readonly Func<OnStepChannel> _channelProvider;
    private readonly Action? _invalidateCaches;
    private readonly ILogger _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="channelProvider">
    /// Supplies the channel of the shared connection. A function rather than a value
    /// because the channel is replaced on every reconnection.
    /// </param>
    /// <param name="invalidateCaches">
    /// Called after any write that changes state a device snapshot serves. Without it a
    /// client would keep reading a cached tracking mode or park state for up to a poll
    /// interval after the UI changed it, and the polling loop has no way of knowing a
    /// second writer exists.
    /// </param>
    /// <param name="logger">Logger.</param>
    public ControllerConfiguration(
        Func<OnStepChannel> channelProvider,
        Action? invalidateCaches = null,
        ILogger<ControllerConfiguration>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(channelProvider);

        _channelProvider = channelProvider;
        _invalidateCaches = invalidateCaches;
        _logger = logger ?? NullLogger<ControllerConfiguration>.Instance;
    }

    private OnStepChannel Channel => _channelProvider();

    // Reading
    //
    // Telling "this build does not have that command" apart from "the value really is
    // zero" is the whole difficulty of reading configuration. With error correction on,
    // the firmware answers an unknown frame with the body 0 through the very same path a
    // real reply uses, so the two are only distinguishable by <b>format</b>: every field
    // the firmware prints with a decimal point, a degree mark or a sign is unambiguous,
    // because a real zero comes back as 0.0 or +00* and never as a bare 0. That is the
    // rule the environmental sensor probe already relies on, and it is what keeps an
    // absent sensor from reporting zero degrees.
    //
    // Plain integer fields have no such marker, so a bare 0 from :GXE9# or :%BR# is
    // genuinely indistinguishable from zero minutes of meridian limit and zero arcseconds
    // of backlash, and those are ordinary values. Those fields report what the firmware
    // said. Asking :GE# afterwards looks like a way out, but the reference does not
    // document the error code being cleared per command, so a stale failure from earlier
    // would turn a legitimate zero into "not supported", which is the worse mistake.
    // Where a subsystem's presence genuinely matters, it is established once at the
    // section level instead: PEC from the status string, the rotator from :GX98#, the
    // focuser from :Fa#.

    /// <summary>
    /// A reply that is exactly <c>0</c> or <c>1</c>, which is what the numeric failure
    /// path produces and therefore what an unimplemented command looks like.
    /// </summary>
    private static bool IsBareBoolean(string reply) =>
        reply.Trim() is "0" or "1" or "";

    /// <summary>Reads an optional command as a plain decimal number.</summary>
    /// <remarks>
    /// A bare <c>0</c> is absence and not a reading. The firmware prints these fields with
    /// a decimal point, so a sensor at freezing answers <c>0.0</c> and one that is not
    /// fitted answers <c>0</c>. Getting this backwards is how an absent dew point sensor
    /// ends up reporting zero degrees and a client closes an observatory roof over it.
    /// </remarks>
    private async Task<FirmwareValue<double>> ReadDoubleAsync(
        string command,
        CancellationToken cancellationToken)
    {
        string? raw = await Channel.TryGetStringAsync(command, cancellationToken)
            .ConfigureAwait(false);

        if (raw is null || IsBareBoolean(raw))
        {
            return FirmwareValue<double>.Absent(raw);
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? FirmwareValue<double>.Present(v, raw)
            : FirmwareValue<double>.Absent(raw);
    }

    /// <summary>Reads an optional command as a whole number.</summary>
    /// <remarks>
    /// A reply of <c>0</c> is kept as the value zero, because zero backlash, a zero
    /// meridian limit and a zero encoder count are all ordinary values and the firmware
    /// prints them exactly as it prints the failure reply. Nothing on the wire separates
    /// the two, so the field reports what the firmware said and presence is established at
    /// the section level instead.
    /// </remarks>
    private async Task<FirmwareValue<long>> ReadInt64Async(
        string command,
        CancellationToken cancellationToken)
    {
        string? raw = await Channel.TryGetStringAsync(command, cancellationToken)
            .ConfigureAwait(false);

        if (raw is null)
        {
            return FirmwareValue<long>.Absent();
        }

        return long.TryParse(
                raw,
                NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long v)
            ? FirmwareValue<long>.Present(v, raw)
            : FirmwareValue<long>.Absent(raw);
    }

    private async Task<FirmwareValue<int>> ReadInt32Async(
        string command,
        CancellationToken cancellationToken)
    {
        FirmwareValue<long> value = await ReadInt64Async(command, cancellationToken)
            .ConfigureAwait(false);

        return value.IsSupported
            ? FirmwareValue<int>.Present((int)value.Value, value.Raw)
            : FirmwareValue<int>.Absent(value.Raw);
    }

    /// <summary>Reads an optional command in any of the sexagesimal formats.</summary>
    /// <remarks>
    /// These always carry a sign, a degree mark or a separator, so a bare <c>0</c> is the
    /// failure reply rather than an angle of zero.
    /// </remarks>
    private async Task<FirmwareValue<double>> ReadAngleAsync(
        string command,
        CancellationToken cancellationToken)
    {
        string? raw = await Channel.TryGetStringAsync(command, cancellationToken)
            .ConfigureAwait(false);

        if (raw is null || IsBareBoolean(raw))
        {
            return FirmwareValue<double>.Absent(raw);
        }

        return Lx200Format.TryParse(raw, out double v)
            ? FirmwareValue<double>.Present(v, raw)
            : FirmwareValue<double>.Absent(raw);
    }

    /// <summary>Reads an optional command whose reply is text.</summary>
    /// <remarks>
    /// Here a bare <c>0</c> does mean absence. With error correction on, the firmware
    /// answers every frame, and an unimplemented one comes back as the numeric failure
    /// <c>0</c> through the same path a text reply would use. No text field of the
    /// command set legitimately answers a single zero.
    /// </remarks>
    private async Task<FirmwareValue<string>> ReadTextAsync(
        string command,
        CancellationToken cancellationToken)
    {
        string? raw = await Channel.TryGetStringAsync(command, cancellationToken)
            .ConfigureAwait(false);

        return raw is null or "0"
            ? FirmwareValue<string>.Absent(raw)
            : FirmwareValue<string>.Present(raw, raw);
    }

    /// <summary>Reads an optional command whose reply is a single flag character.</summary>
    /// <remarks>
    /// Inherently ambiguous, and knowingly so: a flag legitimately answers <c>0</c>, which
    /// is also exactly what an unimplemented command answers, and <c>:GX89#</c> even uses
    /// <c>0</c> to mean yes. So the reply is taken at face value, and the caller must not
    /// use a flag as evidence that a subsystem exists.
    /// </remarks>
    private async Task<FirmwareValue<bool>> ReadFlagAsync(
        string command,
        char trueValue,
        CancellationToken cancellationToken)
    {
        string? raw = await Channel.TryGetStringAsync(command, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(raw))
        {
            return FirmwareValue<bool>.Absent(raw);
        }

        return FirmwareValue<bool>.Present(raw[0] == trueValue, raw);
    }

    // Writing

    /// <summary>
    /// Issues a command that answers <c>0</c> or <c>1</c> and throws with the firmware's
    /// own error code if it refuses.
    /// </summary>
    private async Task WriteAsync(string command, CancellationToken cancellationToken)
    {
        await Channel.RequireTrueAsync(command, cancellationToken).ConfigureAwait(false);
        InvalidateCaches();
    }

    /// <summary>
    /// Issues a command that answers nothing at all.
    /// </summary>
    /// <remarks>
    /// A good part of the configuration set is like this: the home, PEC and slew preset
    /// commands report no result, so the only way to confirm the change is to read the
    /// value back, which is what the setup pages do after applying.
    /// </remarks>
    private async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        await Channel.SendAsync(command, cancellationToken).ConfigureAwait(false);
        InvalidateCaches();
    }

    private void InvalidateCaches()
    {
        try
        {
            _invalidateCaches?.Invoke();
        }
        catch (Exception ex)
        {
            // Failing to invalidate a cache must not turn a successful write into an
            // error the user sees, because the write did happen.
            _logger.LogWarning(ex, "Could not invalidate the device snapshots after a write");
        }
    }

    private static string Decimal(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string Integer(long value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
