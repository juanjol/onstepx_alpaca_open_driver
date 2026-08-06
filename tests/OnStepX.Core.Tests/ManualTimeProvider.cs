namespace OnStepX.Core.Tests;

/// <summary>
/// Manually controlled clock, to verify simulated motion with no real waits.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 8, 5, 22, 30, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Advances the clock.</summary>
    public void Advance(TimeSpan delta) => _now += delta;

    /// <summary>Advances the clock by a number of seconds.</summary>
    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
