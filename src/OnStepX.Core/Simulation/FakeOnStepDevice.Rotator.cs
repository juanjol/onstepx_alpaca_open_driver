using System.Globalization;
using OnStepX.Core.Protocol;

namespace OnStepX.Core.Simulation;

/// <summary>
/// Rotator commands of the simulated device.
/// </summary>
/// <remarks>
/// The angle of <c>:rG#</c> and <c>:rS#</c> is <b>mechanical</b>, not sky
/// angle. The distinction between <c>MechanicalPosition</c> and
/// <c>Position</c> is resolved by the device layer applying the sync
/// offset, not the firmware.
/// </remarks>
public sealed partial class FakeOnStepDevice
{
    private SimReply? DispatchRotator(string cmd)
    {
        if (!RotatorPresent)
        {
            return null;
        }

        DateTimeOffset now = Now;
        SimulatedRotator r = Rotator;

        switch (cmd)
        {
            case "rA":
                return SimReply.Bool(true);

            // State plus rate digit.
            case "rT":
                return SimReply.Text(
                    (r.Angle.IsMovingAt(now) ? "M" : "S")
                    + (r.DerotationEnabled ? "D" : string.Empty)
                    + r.RatePreset.ToString(CultureInfo.InvariantCulture));

            case "rI": return SimReply.Int(r.MinAngle);
            case "rM": return SimReply.Int(r.MaxAngle);
            case "rD": return SimReply.Number(r.DegreesPerStep, "0.00");
            case "rb": return SimReply.Int(r.Backlash);

            case "rG":
                return SimReply.Text(Lx200Format.FormatRotatorAngle(r.Angle.PositionAt(now)));

            case "rW":
                return SimReply.Number(r.Angle.DefaultRatePerSecond, "0.0");

            // Stop. Answers nothing.
            case "rQ":
                r.Angle.Stop(now);
                return SimReply.None();

            // Continuous motion. Answer nothing.
            case "r>":
                r.Angle.MoveTo(r.MaxAngle, now);
                return SimReply.None();
            case "r<":
                r.Angle.MoveTo(r.MinAngle, now);
                return SimReply.None();

            // Accepted for compatibility, no effect.
            case "rc":
                return SimReply.None();

            // Zero, half travel and home. Answer nothing.
            case "rZ":
                r.Angle.SetPosition(0);
                return SimReply.None();
            case "rF":
                r.Angle.SetPosition((r.MinAngle + r.MaxAngle) / 2.0);
                return SimReply.None();
            case "rC":
                r.Angle.MoveTo((r.MinAngle + r.MaxAngle) / 2.0, now);
                return SimReply.None();

            // Derotation. Answer nothing.
            case "r+":
                r.DerotationEnabled = true;
                return SimReply.None();
            case "r-":
                r.DerotationEnabled = false;
                return SimReply.None();
            case "rR":
                r.DerotationReversed = !r.DerotationReversed;
                return SimReply.None();

            case "rP":
                // Goes to the parallactic angle. Approximated with the hour angle.
                r.Angle.MoveTo(ParallacticAngle(now), now);
                return SimReply.None();

            default:
                return DispatchRotatorWithParameters(cmd, r, now);
        }
    }

    private SimReply? DispatchRotatorWithParameters(
        string cmd,
        SimulatedRotator r,
        DateTimeOffset now)
    {
        // Rate preset :r1# through :r9#. Answer nothing.
        if (cmd.Length == 2 && cmd[0] == 'r' && cmd[1] is >= '1' and <= '9')
        {
            r.RatePreset = cmd[1] - '0';
            r.Angle.DefaultRatePerSecond = r.RatePreset switch
            {
                1 => 0.01,
                2 => 0.1,
                3 => 1.0,
                4 or 5 => 1.5,
                6 => 2.0,
                7 => 3.0,
                8 => 4.5,
                _ => 6.0,
            };
            return SimReply.None();
        }

        // Absolute goto :rSsDDD*MM#
        if (cmd.StartsWith("rS", StringComparison.Ordinal))
        {
            if (!Lx200Format.TryParse(cmd[2..], out double target))
            {
                return SimReply.Bool(false);
            }

            if (target < r.MinAngle || target > r.MaxAngle)
            {
                Mount.LastError = CommandError.ParameterRange;
                return SimReply.Bool(false);
            }

            r.Angle.MoveTo(target, now);
            return SimReply.Bool(true);
        }

        // Relative goto :rrsDDD*MM#. Answers nothing.
        if (cmd.StartsWith("rr", StringComparison.Ordinal))
        {
            if (!Lx200Format.TryParse(cmd[2..], out double delta))
            {
                return SimReply.None();
            }

            double target = Math.Clamp(
                r.Angle.PositionAt(now) + delta, r.MinAngle, r.MaxAngle);

            r.Angle.MoveTo(target, now);
            return SimReply.None();
        }

        // Backlash.
        if (cmd.StartsWith("rb", StringComparison.Ordinal) && TryInt(cmd[2..], out int backlash))
        {
            r.Backlash = backlash;
            return SimReply.Bool(true);
        }

        return null;
    }

    private double ParallacticAngle(DateTimeOffset now)
    {
        double ha = Astronomy.Coordinates.NormalizeHours(
            LocalSiderealTimeHours - Mount.RightAscension.PositionAt(now));

        if (ha >= 12)
        {
            ha -= 24;
        }

        // Good enough approximation for the simulator.
        double haDegrees = ha * 15.0;

        return Math.Clamp(haDegrees, Rotator.MinAngle, Rotator.MaxAngle);
    }
}
