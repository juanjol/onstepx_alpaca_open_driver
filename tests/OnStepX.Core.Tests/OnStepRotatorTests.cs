using Microsoft.Extensions.Logging.Abstractions;
using OnStepX.Devices;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// The rotator's whole difficulty is that OnStep only knows the mechanical angle while
/// ASCOM asks for both that and the sky angle.
/// </summary>
public sealed class OnStepRotatorTests : DeviceTestBase
{
    private OnStepRotator Create() =>
        new(Connection, () => Settings, NullLoggerFactory.Instance);

    [Fact]
    public void ConnectingRefusesWhenTheFirmwareHasNoRotator()
    {
        // Better to fail loudly at connect than to answer every property with a
        // fabricated value for hardware that is not there.
        Device.RotatorPresent = false;

        using OnStepRotator rotator = Create();

        Assert.Throws<ASCOM.NotConnectedException>(() => rotator.Connected = true);
        Assert.False(rotator.Connected);
    }

    [Fact]
    public void MechanicalAngleIsReportedInTheRangeAscomRequires()
    {
        // OnStep's travel is normally -180 to 180 while ASCOM wants 0 to 360.
        Device.Rotator.Angle.SetPosition(-90);

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        Assert.Equal(270f, rotator.MechanicalPosition, precision: 2);
    }

    [Fact]
    public void WithoutASyncTheSkyAngleEqualsTheMechanicalAngle()
    {
        Device.Rotator.Angle.SetPosition(45);

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        Assert.Equal(45f, rotator.MechanicalPosition, precision: 2);
        Assert.Equal(45f, rotator.Position, precision: 2);
    }

    /// <summary>
    /// The distinction that matters.
    /// </summary>
    [Fact]
    public void SyncOffsetsTheSkyAngleWithoutMovingTheRotator()
    {
        Device.Rotator.Angle.SetPosition(10);

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        // A plate solve says the sky angle here is really 100 degrees.
        rotator.Sync(100f);

        // The sky angle now reads what the solve said, and the mechanical angle has not
        // moved at all. Conflating the two is what makes a rotator and a plate solve
        // disagree by a fixed amount nobody can account for.
        Assert.Equal(100f, rotator.Position, precision: 2);
        Assert.Equal(10f, rotator.MechanicalPosition, precision: 2);
        Assert.Equal(90.0, Settings.Rotator.SyncOffset, precision: 2);
    }

    [Fact]
    public void TheSyncOffsetIsPersistedInTheConfiguration()
    {
        // A rotator calibrated by a plate solve must stay calibrated across restarts,
        // otherwise every session starts with another solve.
        Device.Rotator.Angle.SetPosition(0);

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        rotator.Sync(30f);

        Assert.Equal(30.0, Settings.Rotator.SyncOffset, precision: 2);
    }

    [Fact]
    public void MoveAbsoluteTargetsASkyAngleAndTheRotatorEndsUpThere()
    {
        Device.Rotator.Angle.SetPosition(0);

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        rotator.Sync(90f);
        rotator.MoveAbsolute(120f);

        WaitUntil(() => !rotator.IsMoving, "rotator to arrive");

        Assert.Equal(120f, rotator.Position, precision: 1);

        // Sky 120 with a 90 degree offset is mechanical 30.
        Assert.Equal(30f, rotator.MechanicalPosition, precision: 1);
    }

    [Fact]
    public void MoveMechanicalTargetsTheMechanicalAngleDirectly()
    {
        Device.Rotator.Angle.SetPosition(0);

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        rotator.Sync(90f);
        rotator.MoveMechanical(45f);

        WaitUntil(() => !rotator.IsMoving, "rotator to arrive");

        Assert.Equal(45f, rotator.MechanicalPosition, precision: 1);
        Assert.Equal(135f, rotator.Position, precision: 1);
    }

    [Fact]
    public void MoveIsRelativeToTheCurrentSkyAngle()
    {
        Device.Rotator.Angle.SetPosition(20);

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        rotator.Move(25f);

        WaitUntil(() => !rotator.IsMoving, "rotator to arrive");

        Assert.Equal(45f, rotator.Position, precision: 1);
    }

    [Fact]
    public void NegativeMechanicalAnglesRoundTripThroughTheZeroToThreeSixtyRange()
    {
        Device.Rotator.Angle.SetPosition(0);

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        // 300 in ASCOM terms is -60 in the firmware's own range.
        rotator.MoveMechanical(300f);
        WaitUntil(() => !rotator.IsMoving, "rotator to arrive");

        Assert.Equal(300f, rotator.MechanicalPosition, precision: 1);

        // And the firmware really was told the negative angle.
        Assert.Contains(Device.ReceivedCommands, c => c.StartsWith("rS-060", StringComparison.Ordinal));
    }

    [Fact]
    public void MovingIsAsynchronousAndIsMovingTracksIt()
    {
        Device.Rotator.Angle.SetPosition(0);
        Device.Rotator.Angle.DefaultRatePerSecond = 20;

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        rotator.MoveAbsolute(170f);

        Assert.True(rotator.IsMoving);

        WaitUntil(() => !rotator.IsMoving, "rotator to arrive", 30_000);

        Assert.Equal(170f, rotator.Position, precision: 1);
    }

    [Fact]
    public void HaltStopsTheRotatorWhereItIs()
    {
        Device.Rotator.Angle.SetPosition(0);
        Device.Rotator.Angle.DefaultRatePerSecond = 20;

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        rotator.MoveAbsolute(170f);
        Thread.Sleep(300);

        rotator.Halt();

        Assert.False(rotator.IsMoving);

        float stopped = rotator.MechanicalPosition;
        Assert.InRange(stopped, 0.1f, 169f);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(361f)]
    [InlineData(float.NaN)]
    public void AnglesOutsideZeroToThreeSixtyAreRejected(float angle)
    {
        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        Assert.Throws<ASCOM.InvalidValueException>(() => rotator.MoveAbsolute(angle));
    }

    [Fact]
    public void AMechanicalAngleBeyondTheTravelIsRejected()
    {
        // The simulated travel is -180 to 180, so 180 to 360 maps to negative angles
        // that are inside it, but a narrower rotator would refuse.
        Device.Rotator.MinAngle = -45;
        Device.Rotator.MaxAngle = 45;
        Device.Rotator.Angle.SetPosition(0);

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        Assert.Throws<ASCOM.InvalidValueException>(() => rotator.MoveMechanical(90f));
    }

    [Fact]
    public void ReverseIsHeldInTheDriverAndNotSentToTheFirmware()
    {
        // OnStep's :rR# toggles the derotator direction, which is about tracking field
        // rotation, not about the sense in which angles are reported to a client.
        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        Assert.True(rotator.CanReverse);
        Assert.False(rotator.Reverse);

        rotator.Reverse = true;

        Assert.True(rotator.Reverse);
        Assert.True(Settings.Rotator.Reverse);
        Assert.DoesNotContain("rR", Device.ReceivedCommands);
    }

    [Fact]
    public void TargetPositionReportsWhereTheMoveIsHeaded()
    {
        Device.Rotator.Angle.SetPosition(0);
        Device.Rotator.Angle.DefaultRatePerSecond = 20;

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        rotator.MoveAbsolute(90f);

        Assert.Equal(90f, rotator.TargetPosition, precision: 2);
    }

    [Fact]
    public void MoveToPositionOnConnectIsHonouredWithoutBlockingTheConnection()
    {
        Settings.Rotator.MoveToPositionOnConnect = true;
        Settings.Rotator.PositionOnConnect = 60;
        Device.Rotator.Angle.SetPosition(0);

        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        Assert.True(rotator.Connected);

        WaitUntil(() => !rotator.IsMoving, "start angle move", 30_000);

        Assert.Equal(60f, rotator.MechanicalPosition, precision: 1);
    }

    [Fact]
    public void DeviceStateReportsBothAngles()
    {
        using OnStepRotator rotator = Create();
        rotator.Connected = true;

        var names = rotator.DeviceState.Select(v => v.Name).ToList();

        Assert.Contains("IsMoving", names);
        Assert.Contains("Position", names);
        Assert.Contains("MechanicalPosition", names);
    }

    [Fact]
    public void ReadingAPropertyWhileDisconnectedThrowsNotConnected()
    {
        using OnStepRotator rotator = Create();

        Assert.Throws<ASCOM.NotConnectedException>(() => _ = rotator.Position);
    }
}
