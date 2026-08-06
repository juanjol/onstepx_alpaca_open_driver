using System.Globalization;

namespace OnStepX.Core.Simulation;

/// <summary>
/// Focuser commands of the simulated device.
/// </summary>
/// <remarks>
/// <para>
/// <b>The critical point is the units.</b> The protocol exposes the same
/// operations at two scales: the uppercase <c>B D G I M R S</c> work in
/// <b>microns</b> and the lowercase <c>b d g i m r s</c> in <b>raw steps</b>.
/// </para>
/// <para>
/// Internal state is kept in steps, and the responses in microns are
/// calculated by multiplying by <c>MicronsPerStep</c>, which in the
/// simulator is deliberately set to 1.13507: by not being 1, a driver that
/// confuses the two scales produces clearly different numbers and the
/// test fails. With a factor of 1 the bug would go unnoticed.
/// </para>
/// </remarks>
public sealed partial class FakeOnStepDevice
{
    private SimulatedFocuser Focuser => Focusers[Math.Clamp(ActiveFocuser, 1, 6) - 1];

    private SimReply? DispatchFocuser(string cmd)
    {
        DateTimeOffset now = Now;

        // Active focuser selection: :FA# queries, :FA1# through :FA6# set it.
        if (cmd == "FA")
        {
            return SimReply.Text(ActiveFocuser.ToString(CultureInfo.InvariantCulture));
        }

        if (cmd.Length == 3 && cmd.StartsWith("FA", StringComparison.Ordinal)
            && cmd[2] is >= '1' and <= '6')
        {
            int index = cmd[2] - '0';
            if (index > FocuserCount)
            {
                return SimReply.Bool(false);
            }

            ActiveFocuser = index;
            return SimReply.Bool(true);
        }

        SimulatedFocuser f = Focuser;

        switch (cmd)
        {
            case "Fa":
                return SimReply.Text(FocuserCount > 0 ? "1" : "0");

            // State plus rate digit: 'M' moving, 'S' stopped.
            case "FT":
                return SimReply.Text(
                    (f.Position.IsMovingAt(now) ? "M" : "S")
                    + f.RatePreset.ToString(CultureInfo.InvariantCulture));

            case "Fp":
                return SimReply.Text(f.IsDcMotor ? "1" : "0");

            // Limits. Uppercase in microns, lowercase in steps.
            case "FI": return SimReply.Int(ToMicrons(f, f.MinPosition));
            case "Fi": return SimReply.Int(f.MinPosition);
            case "FM": return SimReply.Int(ToMicrons(f, f.MaxPosition));
            case "Fm": return SimReply.Int(f.MaxPosition);

            // Current position.
            case "FG": return SimReply.Int(ToMicrons(f, CurrentSteps(f, now)));
            case "Fg": return SimReply.Int(CurrentSteps(f, now));

            case "Fu": return SimReply.Number(f.MicronsPerStep, "0.00000");
            case "Ft": return SimReply.Number(f.Temperature, "+0.0;-0.0");
            case "Fe": return SimReply.Number(f.TemperatureDelta, "+0.0;-0.0");

            // Backlash.
            case "FB": return SimReply.Int(ToMicrons(f, f.Backlash));
            case "Fb": return SimReply.Int(f.Backlash);

            // Temperature compensation.
            case "FC": return SimReply.Number(f.TempCompCoefficient, "0.00000");
            case "Fc": return SimReply.Text(f.TempCompEnabled ? "1" : "0");
            case "Fc0": f.TempCompEnabled = false; return SimReply.Bool(true);
            case "Fc1": f.TempCompEnabled = true; return SimReply.Bool(true);
            case "FD": return SimReply.Int(ToMicrons(f, f.TempCompDeadband));
            case "Fd": return SimReply.Int(f.TempCompDeadband);

            case "FP": return SimReply.Int(f.DcPower);

            // Working speed, in microns per second.
            case "FW":
                return SimReply.Int((long)Math.Round(
                    f.Position.DefaultRatePerSecond * f.MicronsPerStep));

            // Stop. Answers nothing.
            case "FQ":
                f.Position.Stop(now);
                return SimReply.None();

            // Continuous motion. Answer nothing.
            case "F+":
                f.Position.MoveTo(f.MaxPosition, now);
                return SimReply.None();
            case "F-":
                f.Position.MoveTo(f.MinPosition, now);
                return SimReply.None();

            // Zero and home. Answer nothing.
            case "FZ":
                f.Position.SetPosition(0);
                return SimReply.None();
            case "FH":
                f.Position.SetPosition(f.Position.PositionAt(now));
                return SimReply.None();
            case "Fh":
                f.Position.MoveTo(0, now);
                return SimReply.None();

            default:
                return DispatchFocuserWithParameters(cmd, f, now);
        }
    }

    private SimReply? DispatchFocuserWithParameters(
        string cmd,
        SimulatedFocuser f,
        DateTimeOffset now)
    {
        // Rate preset :F1# through :F9#. Answer nothing.
        if (cmd.Length == 2 && cmd[0] == 'F' && cmd[1] is >= '1' and <= '9')
        {
            f.RatePreset = cmd[1] - '0';
            f.Position.DefaultRatePerSecond = f.RatePreset switch
            {
                1 => 1.0 / f.MicronsPerStep,
                2 => 10.0 / f.MicronsPerStep,
                3 => 100.0 / f.MicronsPerStep,
                4 or 5 => 1000.0,
                6 => 1320.0,
                7 => 2000.0,
                8 => 3000.0,
                _ => 4000.0,
            };
            return SimReply.None();
        }

        // Absolute goto. Uppercase in microns, lowercase in steps.
        if (cmd.StartsWith("FS", StringComparison.Ordinal)
            && TryInt(cmd[2..], out int absMicrons))
        {
            return MoveFocuserTo(f, FromMicrons(f, absMicrons), now);
        }

        if (cmd.StartsWith("Fs", StringComparison.Ordinal)
            && TryInt(cmd[2..], out int absSteps))
        {
            return MoveFocuserTo(f, absSteps, now);
        }

        // Relative goto. Answer nothing.
        if (cmd.StartsWith("FR", StringComparison.Ordinal)
            && TryInt(cmd[2..], out int relMicrons))
        {
            f.Position.MoveTo(
                Math.Clamp(CurrentSteps(f, now) + FromMicrons(f, relMicrons),
                    f.MinPosition, f.MaxPosition),
                now);
            return SimReply.None();
        }

        if (cmd.StartsWith("Fr", StringComparison.Ordinal)
            && TryInt(cmd[2..], out int relSteps))
        {
            f.Position.MoveTo(
                Math.Clamp(CurrentSteps(f, now) + relSteps, f.MinPosition, f.MaxPosition),
                now);
            return SimReply.None();
        }

        // Backlash.
        if (cmd.StartsWith("FB", StringComparison.Ordinal) && TryInt(cmd[2..], out int blMicrons))
        {
            f.Backlash = (int)FromMicrons(f, blMicrons);
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("Fb", StringComparison.Ordinal) && TryInt(cmd[2..], out int blSteps))
        {
            f.Backlash = blSteps;
            return SimReply.Bool(true);
        }

        // Compensation coefficient.
        if (cmd.StartsWith("FC", StringComparison.Ordinal) && TryDouble(cmd[2..], out double coeff))
        {
            f.TempCompCoefficient = coeff;
            return SimReply.Bool(true);
        }

        // Dead band.
        if (cmd.StartsWith("FD", StringComparison.Ordinal) && TryInt(cmd[2..], out int dbMicrons))
        {
            f.TempCompDeadband = (int)FromMicrons(f, dbMicrons);
            return SimReply.Bool(true);
        }

        if (cmd.StartsWith("Fd", StringComparison.Ordinal) && TryInt(cmd[2..], out int dbSteps))
        {
            f.TempCompDeadband = dbSteps;
            return SimReply.Bool(true);
        }

        // DC motor power.
        if (cmd.StartsWith("FP", StringComparison.Ordinal) && TryInt(cmd[2..], out int power))
        {
            if (power is < 0 or > 100)
            {
                return SimReply.Bool(false);
            }

            f.DcPower = power;
            return SimReply.Bool(true);
        }

        return null;
    }

    private SimReply MoveFocuserTo(SimulatedFocuser f, long steps, DateTimeOffset now)
    {
        if (steps < f.MinPosition || steps > f.MaxPosition)
        {
            return SimReply.Bool(false);
        }

        f.Position.MoveTo(steps, now);
        return SimReply.Bool(true);
    }

    private static long CurrentSteps(SimulatedFocuser f, DateTimeOffset now) =>
        (long)Math.Round(f.Position.PositionAt(now));

    private static long ToMicrons(SimulatedFocuser f, long steps) =>
        (long)Math.Round(steps * f.MicronsPerStep);

    private static long FromMicrons(SimulatedFocuser f, long microns) =>
        (long)Math.Round(microns / f.MicronsPerStep);
}
