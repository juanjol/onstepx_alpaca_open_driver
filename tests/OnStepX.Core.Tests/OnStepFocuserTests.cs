using Microsoft.Extensions.Logging.Abstractions;
using OnStepX.Core.Config;
using OnStepX.Core.Hardware;
using OnStepX.Core.Simulation;
using OnStepX.Devices;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// Base for device tests: a simulated controller behind a real shared connection, so
/// every test exercises the whole stack from the ASCOM member down to the framed
/// command.
/// </summary>
public abstract class DeviceTestBase : IAsyncDisposable
{
    protected FakeOnStepDevice Device { get; }

    protected OnStepXConnection Connection { get; }

    protected OnStepXSettings Settings { get; }

    protected DeviceTestBase()
    {
        Device = new FakeOnStepDevice();

        Settings = new OnStepXSettings
        {
            Connection = new ConnectionSettings
            {
                Kind = TransportKind.Simulated,
                TimeoutMilliseconds = 5000,
                UseErrorCorrection = true,
                PollIntervalMilliseconds = 100,
            },
        };

        Connection = new OnStepXConnection(() => Settings, () => Device);

        // Run the simulated mechanics fast. At the realistic default rate a full travel
        // move takes half a minute, which makes the suite slow and, worse, makes tests
        // interfere with each other through sheer wall clock pressure.
        foreach (SimulatedFocuser focuser in Device.Focusers)
        {
            focuser.Position.DefaultRatePerSecond = 200_000;
        }

        Device.Rotator.Angle.DefaultRatePerSecond = 720;
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Waits for a condition, polling, so device motion can complete.</summary>
    protected static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            Thread.Sleep(25);
        }
    }
}

public sealed class OnStepFocuserTests : DeviceTestBase
{
    private OnStepFocuser Create(int number = 1) =>
        new(Connection, () => Settings, NullLoggerFactory.Instance, number);

    [Fact]
    public void ConnectingSelectsTheConfiguredFocuser()
    {
        Device.FocuserCount = 3;

        using OnStepFocuser focuser = Create(3);
        focuser.Connected = true;

        // Every later command without an explicit number goes to the active focuser, so
        // selecting it has to happen before anything else is read.
        Assert.Contains("FA3", Device.ReceivedCommands);
        Assert.True(focuser.Connected);
    }

    /// <summary>
    /// The single most important focuser test.
    /// </summary>
    [Fact]
    public void PositionAndTravelAreReportedInStepsNotMicrons()
    {
        // The simulator uses 1.13507 microns per step, deliberately not one, so any
        // confusion between the two scales shows up as a clearly different number.
        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        focuser.Move(10_000);
        WaitUntil(() => !focuser.IsMoving, "focuser to stop");

        Assert.Equal(10_000, focuser.Position);
        Assert.Equal(77_500, focuser.MaxStep);

        // StepSize is the conversion factor, reported for display only.
        Assert.Equal(1.13507, focuser.StepSize, precision: 5);

        // If the driver had used the micron commands, the position would come back as
        // roughly 11350 and the travel as 87979.
        Assert.NotEqual(11_350, focuser.Position);
        Assert.NotEqual(87_979, focuser.MaxStep);
    }

    [Fact]
    public void TheDriverUsesLowerCaseStepCommandsAndNeverTheMicronForms()
    {
        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        focuser.Move(5_000);
        WaitUntil(() => !focuser.IsMoving, "focuser to stop");

        // Lower case is steps, upper case is microns. Only the step forms may appear.
        Assert.Contains(Device.ReceivedCommands, c => c == "Fg");
        Assert.Contains(Device.ReceivedCommands, c => c == "Fm");
        Assert.Contains(Device.ReceivedCommands, c => c == "Fi");
        Assert.Contains(Device.ReceivedCommands, c => c.StartsWith("Fs", StringComparison.Ordinal));

        Assert.DoesNotContain(Device.ReceivedCommands, c => c == "FG");
        Assert.DoesNotContain(Device.ReceivedCommands, c => c == "FM");
        Assert.DoesNotContain(Device.ReceivedCommands, c => c == "FI");
        Assert.DoesNotContain(Device.ReceivedCommands, c => c.StartsWith("FS", StringComparison.Ordinal));
    }

    [Fact]
    public void MoveIsAsynchronousAndIsMovingTracksIt()
    {
        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        // Slow this one focuser down so the move is unambiguously still running when the
        // call returns.
        Device.Focusers[0].Position.DefaultRatePerSecond = 20_000;

        focuser.Move(60_000);

        // Returns immediately with the move under way.
        Assert.True(focuser.IsMoving);

        WaitUntil(() => !focuser.IsMoving, "focuser to arrive");

        Assert.Equal(60_000, focuser.Position);
    }

    [Fact]
    public void HaltStopsTheFocuserWhereItIs()
    {
        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        Device.Focusers[0].Position.DefaultRatePerSecond = 20_000;

        focuser.Move(70_000);
        Thread.Sleep(300);

        focuser.Halt();

        Assert.False(focuser.IsMoving);

        int stopped = focuser.Position;
        Assert.InRange(stopped, 1, 69_999);

        // And it stays put.
        Thread.Sleep(500);
        Assert.Equal(stopped, focuser.Position);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-10_000, 0)]
    [InlineData(77_501, 77_500)]
    [InlineData(int.MaxValue, 77_500)]
    public void MovingOutsideTheTravelIsClampedRatherThanRejected(int requested, int expected)
    {
        // The specification asks for a graceful move to the nearest end of travel, not an
        // exception. An earlier version of this test asserted the opposite and Conform
        // was the thing that caught it: reasoning from first principles said reject, the
        // specification says clamp, and a client stepping past the end of travel should
        // keep working rather than fail mid sequence.
        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        focuser.Move(requested);
        WaitUntil(() => !focuser.IsMoving, "focuser to arrive");

        Assert.Equal(expected, focuser.Position);
    }

    [Fact]
    public void MovingWhileTemperatureCompensationIsOnIsAllowedFromVersionThree()
    {
        // Interface versions 1 and 2 forbade this. Version 3 onwards allows it, and
        // refusing would break any client that leaves compensation switched on.
        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        focuser.TempComp = true;

        focuser.Move(1_000);
        WaitUntil(() => !focuser.IsMoving, "focuser to arrive");

        Assert.Equal(1_000, focuser.Position);
        Assert.True(focuser.TempComp);
    }

    [Fact]
    public void TemperatureCompensationRoundTrips()
    {
        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        Assert.False(focuser.TempComp);
        Assert.True(focuser.TempCompAvailable);

        focuser.TempComp = true;
        Assert.True(focuser.TempComp);

        focuser.TempComp = false;
        Assert.False(focuser.TempComp);
    }

    [Fact]
    public void AbsoluteIsTrueAndMaxIncrementCoversTheWholeTravel()
    {
        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        Assert.True(focuser.Absolute);
        Assert.Equal(focuser.MaxStep, focuser.MaxIncrement);
    }

    [Fact]
    public void TemperatureIsReportedWhenAProbeIsFitted()
    {
        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        Assert.Equal(12.5, focuser.Temperature, precision: 1);
    }

    [Fact]
    public void PositionIsReportedRelativeToTheStartOfTravel()
    {
        // ASCOM requires 0 to MaxStep, but OnStep travel can begin at a non zero step
        // number. Reporting the raw firmware number would push positions outside the
        // range ASCOM allows.
        Device.Focusers[0].MinPosition = 500;
        Device.Focusers[0].MaxPosition = 10_500;
        Device.Focusers[0].Position.SetPosition(500);

        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        Assert.Equal(0, focuser.Position);
        Assert.Equal(10_000, focuser.MaxStep);

        focuser.Move(2_500);
        WaitUntil(() => !focuser.IsMoving, "focuser to arrive");

        Assert.Equal(2_500, focuser.Position);

        // The firmware really was told the offset position.
        Assert.Contains("Fs3000", Device.ReceivedCommands);
    }

    [Fact]
    public void MoveToPositionOnConnectIsHonouredWithoutBlockingTheConnection()
    {
        Settings.Focuser.MoveToPositionOnConnect = true;
        Settings.Focuser.PositionOnConnect = 25_000;

        using OnStepFocuser focuser = Create();

        focuser.Connected = true;

        // Connect returned while the focuser is still travelling, which is the point:
        // the client is not held up for the length of the move.
        Assert.True(focuser.Connected);

        WaitUntil(() => !focuser.IsMoving, "start position move to finish", 20_000);

        Assert.Equal(25_000, focuser.Position);
    }

    [Fact]
    public void AnOutOfRangeStartPositionIsClampedRatherThanFailingTheConnection()
    {
        Settings.Focuser.MoveToPositionOnConnect = true;
        Settings.Focuser.PositionOnConnect = 999_999;

        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        Assert.True(focuser.Connected);

        WaitUntil(() => !focuser.IsMoving, "clamped start position move", 30_000);

        Assert.Equal(focuser.MaxStep, focuser.Position);
    }

    [Fact]
    public void DeviceStateReportsTheOperationalValues()
    {
        using OnStepFocuser focuser = Create();
        focuser.Connected = true;

        var names = focuser.DeviceState.Select(v => v.Name).ToList();

        Assert.Contains("IsMoving", names);
        Assert.Contains("Position", names);
        Assert.Contains("TimeStamp", names);
    }

    [Fact]
    public void ReadingAPropertyWhileDisconnectedThrowsNotConnected()
    {
        using OnStepFocuser focuser = Create();

        Assert.Throws<ASCOM.NotConnectedException>(() => _ = focuser.Position);
    }

    [Fact]
    public void FocuserNumberOutsideOneToSixIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(7));
    }
}
