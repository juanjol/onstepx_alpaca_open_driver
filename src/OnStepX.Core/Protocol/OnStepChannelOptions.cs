namespace OnStepX.Core.Protocol;

/// <summary>
/// Settings for <see cref="OnStepChannel"/>.
/// </summary>
public sealed record OnStepChannelOptions
{
    /// <summary>
    /// Deadline for completing a transaction. Equivalent to the "Timeout"
    /// slider of the old forms.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Enables framing with checksum, that is the "Use Error Correction"
    /// checkbox of the old forms.
    /// </summary>
    /// <remarks>
    /// Besides detecting corruption, it has a very useful side effect: with
    /// checksum the firmware responds to <b>all</b> commands and <b>always</b>
    /// ends in <c>#</c>, even the ones that in normal mode answer nothing or
    /// answer a stray character with no terminator. That removes the
    /// ambiguity when reading and allows desynchronizations to be detected.
    /// </remarks>
    public bool UseErrorCorrection { get; init; } = true;

    /// <summary>
    /// Retries on expired deadline, checksum corruption, or sequence
    /// desynchronization.
    /// </summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// Wait between retries, to give the firmware margin to finish flushing
    /// whatever it was sending.
    /// </summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);
}
