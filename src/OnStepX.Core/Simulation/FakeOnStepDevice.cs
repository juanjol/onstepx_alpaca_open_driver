using System.Globalization;
using System.Text;
using OnStepX.Core.Astronomy;
using OnStepX.Core.Protocol;
using OnStepX.Core.Transport;

namespace OnStepX.Core.Simulation;

/// <summary>
/// Simulated OnStepX controller, exposed as <see cref="ITransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that allows running the tests and <b>ConformU on
/// Linux, with no hardware</b>. By implementing the transport and not the
/// channel, it genuinely exercises framing, checksum, retries, and response
/// interpretation.
/// </para>
/// <para>
/// It simulates a <b>mount integrated build</b>. That matters for the
/// firmware's dispatch order: in an integrated build <c>:hP#</c> and
/// <c>:hR#</c> park and unpark <b>the mount</b>, and the focuser and
/// rotator handlers never get to see them. Only in standalone or remote
/// node builds do those commands go to the accessory.
/// </para>
/// </remarks>
public sealed partial class FakeOnStepDevice : ITransport
{
    private readonly StringBuilder _input = new();
    private readonly Queue<byte> _output = new();
    private readonly TimeProvider _time;

    private bool _open;

    /// <summary>Creates the simulated device.</summary>
    /// <param name="timeProvider">
    /// Clock. Injectable so tests can control the progress of motion
    /// instead of depending on real waits.
    /// </param>
    public FakeOnStepDevice(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Mount state.</summary>
    public SimulatedMount Mount { get; } = new();

    /// <summary>The six possible focusers.</summary>
    public SimulatedFocuser[] Focusers { get; } =
        [new(), new(), new(), new(), new(), new()];

    /// <summary>Active focuser, from 1 to 6.</summary>
    public int ActiveFocuser { get; set; } = 1;

    /// <summary>Number of focusers present.</summary>
    public int FocuserCount { get; set; } = 1;

    /// <summary>Rotator state.</summary>
    public SimulatedRotator Rotator { get; } = new();

    /// <summary>Environmental sensors.</summary>
    public SimulatedWeather Weather { get; } = new();

    /// <summary>Product name returned by <c>:GVP#</c>.</summary>
    public string ProductName { get; set; } = "On-Step";

    /// <summary>Version returned by <c>:GVN#</c>.</summary>
    public string FirmwareVersion { get; set; } = "10.21b";

    /// <summary>Whether a rotator is present.</summary>
    public bool RotatorPresent { get; set; } = true;

    /// <summary>Commands received, in order. For assertions in tests.</summary>
    public List<string> ReceivedCommands { get; } = [];

    /// <summary>
    /// Commands the device must declare as unknown, to allow testing the
    /// firmware path without that function compiled in.
    /// </summary>
    public HashSet<string> UnsupportedCommands { get; } = [];

    /// <inheritdoc />
    public string Description => "Simulated OnStepX";

    /// <inheritdoc />
    public bool IsOpen => _open;

    /// <inheritdoc />
    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        _open = true;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask CloseAsync()
    {
        _open = false;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        foreach (byte b in data.Span)
        {
            char c = (char)b;

            // The firmware discards space, LF and CR while parsing.
            if (c is ' ' or '\n' or '\r')
            {
                continue;
            }

            _input.Append(c);

            if (c == '#')
            {
                ProcessFrame(_input.ToString());
                _input.Clear();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (_output.Count == 0)
        {
            // With no response pending it waits, so that the channel's own
            // deadline is what cuts it off. This is what allows the timeout
            // path to be tested.
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }

        int count = Math.Min(_output.Count, buffer.Length);
        for (int i = 0; i < count; i++)
        {
            buffer.Span[i] = _output.Dequeue();
        }

        return count;
    }

    /// <inheritdoc />
    public void DiscardInputBuffer() => _output.Clear();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _open = false;
        return ValueTask.CompletedTask;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private void ProcessFrame(string frame)
    {
        // frame includes the leading delimiter and the trailing '#'.
        if (frame.Length < 3)
        {
            return;
        }

        bool checksum = frame[0] == ';';
        string inner = frame[1..^1];
        char sequence = '\0';

        if (checksum)
        {
            if (inner.Length < 3)
            {
                return;
            }

            sequence = inner[^1];
            string expected = inner.Substring(inner.Length - 3, 2);
            string payload = inner[..^3];

            if (!string.Equals(OnStepFraming.Checksum(payload), expected, StringComparison.Ordinal))
            {
                // The firmware asks for retransmission, and on the real
                // code path that response goes out without a checksum.
                Emit("CK_FAIL#");
                return;
            }

            inner = payload;
        }

        ReceivedCommands.Add(inner);

        Mount.Advance(Now);

        SimReply reply = UnsupportedCommands.Contains(inner)
            ? SimReply.Bool(false)
            : Dispatch(inner);

        EmitReply(reply, checksum, sequence);
    }

    private void EmitReply(SimReply reply, bool checksum, char sequence)
    {
        if (checksum)
        {
            // With checksum the firmware responds to everything and always
            // frames: the condition is strlen(reply) > 0 || buffer.checksum,
            // and suppressFrame = false is forced.
            string body = reply.Kind == ReplyKind.None ? string.Empty : reply.Body;
            Emit(body + OnStepFraming.Checksum(body) + sequence + "#");
            return;
        }

        switch (reply.Kind)
        {
            case ReplyKind.None:
                // Without checksum, absolutely nothing is sent.
                break;

            case ReplyKind.Boolean:
            case ReplyKind.SingleDigit:
                // A single character, with no terminator.
                Emit(reply.Body);
                break;

            default:
                Emit(reply.Body + "#");
                break;
        }
    }

    private void Emit(string text)
    {
        foreach (byte b in Encoding.ASCII.GetBytes(text))
        {
            _output.Enqueue(b);
        }
    }

    private readonly record struct SimReply(ReplyKind Kind, string Body)
    {
        public static SimReply None() => new(ReplyKind.None, string.Empty);

        public static SimReply Bool(bool value) => new(ReplyKind.Boolean, value ? "1" : "0");

        public static SimReply Digit(int value) =>
            new(ReplyKind.SingleDigit, value.ToString(CultureInfo.InvariantCulture));

        public static SimReply Text(string value) => new(ReplyKind.Terminated, value);

        public static SimReply Number(double value, string format) =>
            new(ReplyKind.Terminated, value.ToString(format, CultureInfo.InvariantCulture));

        public static SimReply Int(long value) =>
            new(ReplyKind.Terminated, value.ToString(CultureInfo.InvariantCulture));
    }

    private SimReply Dispatch(string cmd) =>
        DispatchFirmware(cmd)
        ?? DispatchMount(cmd)
        ?? DispatchSite(cmd)
        ?? DispatchFocuser(cmd)
        ?? DispatchRotator(cmd)
        ?? SimReply.Bool(false);

    private SimReply? DispatchFirmware(string cmd) => cmd switch
    {
        "GVP" => SimReply.Text(ProductName),
        "GVN" => SimReply.Text(FirmwareVersion),
        "GVM" => SimReply.Text($"OnStepX {FirmwareVersion}"),
        "GVD" => SimReply.Text("Aug 05 2026"),
        "GVT" => SimReply.Text("22:30:00"),
        "GVC" => SimReply.Text("Simulated OnStepX"),
        "GVH" => SimReply.Text("Simulation"),
        "GE" => SimReply.Text(((int)Mount.LastError).ToString("00", CultureInfo.InvariantCulture)),

        // Environmental sensors. An absent sensor returns false, not a
        // zero, just like the firmware does without that sensor compiled in.
        "GX9A" => Weather.HasTemperature
            ? SimReply.Number(Weather.Temperature, "+0.0;-0.0")
            : SimReply.Bool(false),
        "GX9B" => Weather.HasPressure
            ? SimReply.Number(Weather.Pressure, "0.0")
            : SimReply.Bool(false),
        "GX9C" => Weather.HasHumidity
            ? SimReply.Number(Weather.Humidity, "0.0")
            : SimReply.Bool(false),
        "GX9E" => Weather.HasTemperature && Weather.HasHumidity
            ? SimReply.Number(Weather.DewPoint, "+0.0;-0.0")
            : SimReply.Bool(false),
        "GX9F" => SimReply.Number(Weather.McuTemperature, "0"),

        _ => DispatchFirmwareWithParameters(cmd),
    };

    private SimReply? DispatchFirmwareWithParameters(string cmd)
    {
        if (TryParam(cmd, "SX9A,", out string v) && TryDouble(v, out double t))
        {
            Weather.Temperature = t;
            return SimReply.Bool(true);
        }

        if (TryParam(cmd, "SX9B,", out v) && TryDouble(v, out double p))
        {
            Weather.Pressure = p;
            return SimReply.Bool(true);
        }

        if (TryParam(cmd, "SX9C,", out v) && TryDouble(v, out double h))
        {
            Weather.Humidity = h;
            return SimReply.Bool(true);
        }

        return null;
    }

    private static bool TryParam(string cmd, string prefix, out string value)
    {
        if (cmd.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = cmd[prefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryDouble(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value);
}
