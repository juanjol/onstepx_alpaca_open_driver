namespace OnStepX.Core.Simulation;

/// <summary>
/// Motion of a simulated axis, interpolated over time.
/// </summary>
/// <remarks>
/// The simulator has to genuinely move, not teleport. ConformU checks
/// that <c>Slewing</c> turns true and then false, that <c>AbortSlew</c>
/// has an effect, and that the position progresses, and none of that can
/// be verified against a device that reaches its destination instantly.
/// </remarks>
public sealed class SimulatedMotion
{
    private double _from;
    private double _to;
    private double _ratePerSecond;
    private DateTimeOffset _startedAt;
    private bool _moving;

    /// <summary>Creates an axis stopped at the given position.</summary>
    public SimulatedMotion(double position, double defaultRatePerSecond)
    {
        _from = position;
        _to = position;
        _ratePerSecond = defaultRatePerSecond;
        DefaultRatePerSecond = defaultRatePerSecond;
    }

    /// <summary>Nominal rate, in units per second.</summary>
    public double DefaultRatePerSecond { get; set; }

    /// <summary>Starts a move toward <paramref name="target"/>.</summary>
    public void MoveTo(double target, DateTimeOffset now, double? ratePerSecond = null)
    {
        // Takes the total position, drift included, as the new origin and restarts
        // the drift accumulation from there. Otherwise the drift already applied
        // would be counted twice.
        _from = PositionAt(now);
        RebaseDrift(now);
        _to = target;
        _ratePerSecond = Math.Abs(ratePerSecond ?? DefaultRatePerSecond);
        _startedAt = now;
        _moving = !AreClose(_from, _to) && _ratePerSecond > 0;
    }

    /// <summary>Stops the axis wherever it is at that instant.</summary>
    public void Stop(DateTimeOffset now)
    {
        _from = PositionAt(now);
        RebaseDrift(now);
        _to = _from;
        _moving = false;
    }

    /// <summary>Sets the position with no motion, for sync and for zero.</summary>
    public void SetPosition(double position)
    {
        _from = position;
        _to = position;
        _moving = false;

        // A sync defines the position exactly, so any accumulated drift is gone.
        _driftAccumulated = 0;
        _driftStarted = false;
    }

    /// <summary>Position at a given instant, drift included.</summary>
    public double PositionAt(DateTimeOffset now) => CorePositionAt(now) + DriftAt(now);

    /// <summary>
    /// Position from commanded motion alone, with no drift applied.
    /// </summary>
    /// <remarks>
    /// Drift is kept strictly separate from commanded motion. Folding it into
    /// <c>_from</c> would double count it the next time a move started, because a move
    /// takes its origin from the current position.
    /// </remarks>
    private double CorePositionAt(DateTimeOffset now)
    {
        if (!_moving)
        {
            return _from;
        }

        double elapsed = (now - _startedAt).TotalSeconds;
        double travelled = elapsed * _ratePerSecond;
        double distance = Math.Abs(_to - _from);

        if (travelled >= distance)
        {
            _from = _to;
            _moving = false;
            return _to;
        }

        return _from + Math.Sign(_to - _from) * travelled;
    }

    /// <summary>
    /// Continuous drift applied on top of commanded motion, in axis units per second.
    /// </summary>
    /// <remarks>
    /// This models a tracking rate offset. Without it the simulator would accept a
    /// rate offset, report it back correctly, and never move, which is exactly the
    /// kind of gap that lets a driver bug through: Conform sets a rate and then
    /// measures whether the reported position actually changes.
    /// </remarks>
    public double DriftRatePerSecond { get; private set; }

    private DateTimeOffset _driftAnchor;
    private double _driftAccumulated;
    private bool _driftStarted;

    /// <summary>Sets the drift rate, banking whatever drift has happened so far.</summary>
    public void SetDriftRate(double ratePerSecond, DateTimeOffset now)
    {
        _driftAccumulated = DriftAt(now);
        _driftAnchor = now;
        _driftStarted = true;
        DriftRatePerSecond = ratePerSecond;
    }

    /// <summary>
    /// Keeps the drift rate but restarts its accumulation from zero at this instant.
    /// </summary>
    private void RebaseDrift(DateTimeOffset now)
    {
        _driftAccumulated = 0;
        _driftAnchor = now;
    }

    private double DriftAt(DateTimeOffset now)
    {
        if (!_driftStarted)
        {
            return 0;
        }

        return _driftAccumulated + ((now - _driftAnchor).TotalSeconds * DriftRatePerSecond);
    }

    /// <summary>Whether the axis is still moving at that instant.</summary>
    public bool IsMovingAt(DateTimeOffset now)
    {
        // Querying the position is what closes out the motion on arrival,
        // so it must be done before answering.
        PositionAt(now);
        return _moving;
    }

    /// <summary>Current destination.</summary>
    public double Target => _to;

    private static bool AreClose(double a, double b) => Math.Abs(a - b) < 1e-9;
}
