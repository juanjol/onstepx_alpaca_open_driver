using ASCOM;
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using OnStepX.Core.Config;
using OnStepX.Core.Devices;
using OnStepX.Core.Hardware;
using OnStepX.Core.Protocol;

namespace OnStepX.Devices;

/// <summary>One reading of the rotator's state.</summary>
public sealed record RotatorSnapshot
{
    /// <summary>Mechanical angle in degrees, exactly as the firmware reports it.</summary>
    public required double MechanicalAngle { get; init; }

    /// <summary>Lowest mechanical angle the firmware allows.</summary>
    public required double MinAngle { get; init; }

    /// <summary>Highest mechanical angle the firmware allows.</summary>
    public required double MaxAngle { get; init; }

    /// <summary>Degrees per step.</summary>
    public required double DegreesPerStep { get; init; }

    /// <summary>The rotator is moving.</summary>
    public required bool IsMoving { get; init; }

    /// <summary>Field derotation is switched on.</summary>
    public required bool DerotationEnabled { get; init; }
}

/// <summary>
/// ASCOM rotator device backed by an OnStepX controller.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mechanical angle and sky angle are not the same thing</b>, and OnStep only knows
/// the first. <c>:rG#</c> and <c>:rS#</c> are purely mechanical, so
/// <see cref="MechanicalPosition"/> passes straight through while
/// <see cref="Position"/> is the sky angle obtained by adding the offset that
/// <see cref="Sync"/> established. Treating them as interchangeable is what makes a
/// plate solve and a rotator disagree by a fixed amount that nobody can account for.
/// </para>
/// <para>
/// The sync offset is part of the saved configuration, so a rotator stays calibrated
/// across restarts instead of needing a fresh plate solve every session.
/// </para>
/// </remarks>
public sealed class OnStepRotator : OnStepDeviceBase, IRotatorV4
{
    private readonly SnapshotPoller<RotatorSnapshot> _poller;

    private float _targetPosition;

    /// <summary>Creates the rotator device.</summary>
    public OnStepRotator(
        OnStepXConnection connection,
        Func<OnStepXSettings> settingsProvider,
        ILoggerFactory loggerFactory)
        : base(connection, settingsProvider, Require(loggerFactory).CreateLogger<OnStepRotator>())
    {
        _poller = new SnapshotPoller<RotatorSnapshot>(
            "Rotator",
            ReadSnapshotAsync,
            TimeSpan.FromMilliseconds(settingsProvider().Connection.PollIntervalMilliseconds),
            loggerFactory.CreateLogger<OnStepRotator>());
    }

    private static ILoggerFactory Require(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory;
    }

    /// <inheritdoc />
    protected override string DeviceKey => "Rotator";

    /// <inheritdoc />
    public override void InvalidateSnapshot() => _poller.Invalidate();

    /// <inheritdoc />
    public override string Name => "OnStepX Rotator";

    /// <inheritdoc />
    public override string Description =>
        Connection.Identity is { } identity
            ? $"OnStepX rotator, firmware {identity.FirmwareVersion}"
            : "OnStepX rotator";

    /// <inheritdoc />
    public override short InterfaceVersion => 4;

    private RotatorSnapshot Snapshot
    {
        get
        {
            RequireConnected();

            return _poller.GetFresh()
                ?? throw new NotConnectedException("No rotator status has been read yet.");
        }
    }

    /// <inheritdoc />
    protected override async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        OnStepXSettings settings = Settings;

        _poller.PollInterval = TimeSpan.FromMilliseconds(
            Math.Clamp(settings.Connection.PollIntervalMilliseconds, 100, 10_000));

        // Refuse to pretend: if this firmware has no rotator, say so at connect time
        // rather than answering every property with a fabricated value.
        string capability = await Channel.GetStringAsync("GX98", cancellationToken)
            .ConfigureAwait(false);

        if (capability.StartsWith('N'))
        {
            throw new NotConnectedException(
                "This OnStepX build reports no rotator. Enable AXIS3 in the firmware " +
                "configuration, or do not connect this device.");
        }

        await _poller.StartAsync(cancellationToken).ConfigureAwait(false);

        if (settings.Rotator.MoveToPositionOnConnect)
        {
            await MoveOnConnectAsync(settings.Rotator.PositionOnConnect, cancellationToken)
                .ConfigureAwait(false);
        }

        Logger.LogInformation(
            "Rotator connected, travel {Min} to {Max} degrees, capability {Capability}",
            _poller.Current!.MinAngle, _poller.Current.MaxAngle, capability);
    }

    /// <summary>
    /// Sends the rotator to its configured start angle.
    /// </summary>
    /// <remarks>
    /// The configured value is a <b>mechanical</b> angle, because that is what survives
    /// a restart: the sky angle for a given mechanical position depends on where the
    /// telescope is pointing.
    /// </remarks>
    private async Task MoveOnConnectAsync(double mechanicalAngle, CancellationToken cancellationToken)
    {
        try
        {
            RotatorSnapshot snapshot = _poller.Current!;
            double clamped = Math.Clamp(mechanicalAngle, snapshot.MinAngle, snapshot.MaxAngle);

            if (Math.Abs(clamped - mechanicalAngle) > 1e-6)
            {
                Logger.LogWarning(
                    "Configured start angle {Requested} is outside the travel, using {Used}",
                    mechanicalAngle, clamped);
            }

            Logger.LogInformation("Moving rotator to its configured start angle {Angle}", clamped);

            await MoveMechanicalRawAsync(clamped, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not move the rotator to its configured start angle");
        }
    }

    /// <inheritdoc />
    protected override async Task OnDisconnectingAsync() =>
        await _poller.StopAsync().ConfigureAwait(false);

    private async Task<RotatorSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        string angle = await Channel.GetStringAsync("rG", cancellationToken).ConfigureAwait(false);

        if (!Lx200Format.TryParse(angle, out double mechanical))
        {
            throw new OnStepProtocolException($"Could not parse the rotator angle: {angle}");
        }

        double min = await Channel.GetDoubleAsync("rI", cancellationToken).ConfigureAwait(false);
        double max = await Channel.GetDoubleAsync("rM", cancellationToken).ConfigureAwait(false);
        double stepSize = await Channel.GetDoubleAsync("rD", cancellationToken).ConfigureAwait(false);
        string status = await Channel.GetStringAsync("rT", cancellationToken).ConfigureAwait(false);

        return new RotatorSnapshot
        {
            MechanicalAngle = mechanical,
            MinAngle = min,
            MaxAngle = max,
            DegreesPerStep = stepSize,

            // :rT# answers with a state letter, then optionally D for derotating, then a
            // rate digit.
            IsMoving = status.StartsWith('M'),
            DerotationEnabled = status.Contains('D', StringComparison.Ordinal),
        };
    }

    /// <summary>Sync offset between mechanical and sky angle, in degrees.</summary>
    private double SyncOffset => Settings.Rotator.SyncOffset;

    /// <summary>
    /// Sky angle in degrees, in the range 0 to 360.
    /// </summary>
    /// <remarks>
    /// The mechanical angle plus the offset established by <see cref="Sync"/>.
    /// </remarks>
    public float Position => (float)Normalise(Snapshot.MechanicalAngle + SyncOffset);

    /// <summary>
    /// Mechanical angle in degrees, in the range 0 to 360.
    /// </summary>
    /// <remarks>
    /// ASCOM requires 0 to 360 while OnStep's travel is typically -180 to 180, so the
    /// value is normalised on the way out and denormalised on the way in.
    /// </remarks>
    public float MechanicalPosition => (float)Normalise(Snapshot.MechanicalAngle);

    /// <summary>Target sky angle of the move in progress.</summary>
    public float TargetPosition => _targetPosition;

    /// <summary>Degrees per step.</summary>
    public float StepSize => (float)Snapshot.DegreesPerStep;

    /// <summary>The rotator is moving.</summary>
    public bool IsMoving => Snapshot.IsMoving;

    /// <summary>OnStep can reverse the rotator.</summary>
    public bool CanReverse => true;

    /// <summary>
    /// Whether the reported direction of rotation is reversed.
    /// </summary>
    /// <remarks>
    /// Kept in the driver's configuration rather than sent to the firmware. OnStep's
    /// <c>:rR#</c> toggles the <b>derotator</b> direction, which is a different thing:
    /// it affects tracking of field rotation, not the sense in which angles are
    /// reported to a client.
    /// </remarks>
    public bool Reverse
    {
        get => Settings.Rotator.Reverse;

        set
        {
            OnStepXSettings settings = Settings;
            settings.Rotator.Reverse = value;

            Logger.LogInformation("Rotator direction reversed: {Reverse}", value);
        }
    }

    /// <summary>Moves by a relative sky angle, in degrees.</summary>
    public void Move(float relativeAngle)
    {
        if (float.IsNaN(relativeAngle) || float.IsInfinity(relativeAngle))
        {
            throw new InvalidValueException($"{relativeAngle} is not a valid angle.");
        }

        MoveAbsolute((float)Normalise(Position + relativeAngle));
    }

    /// <summary>Moves to an absolute sky angle, in degrees.</summary>
    public void MoveAbsolute(float skyAngle)
    {
        ValidateAngle(skyAngle);

        // Sky angle to mechanical angle, then into the firmware's own range.
        double mechanical = Denormalise(Normalise(skyAngle - SyncOffset));

        _targetPosition = (float)Normalise(skyAngle);

        RunSync(() => MoveMechanicalRawAsync(mechanical, CancellationToken.None));
    }

    /// <summary>Moves to an absolute mechanical angle, in degrees.</summary>
    public void MoveMechanical(float mechanicalAngle)
    {
        ValidateAngle(mechanicalAngle);

        double mechanical = Denormalise(mechanicalAngle);

        _targetPosition = (float)Normalise(mechanicalAngle + SyncOffset);

        RunSync(() => MoveMechanicalRawAsync(mechanical, CancellationToken.None));
    }

    /// <summary>
    /// Declares that the current mechanical angle corresponds to a given sky angle.
    /// </summary>
    /// <remarks>
    /// Stored as an offset in the driver's configuration and persisted, so a rotator
    /// calibrated by a plate solve stays calibrated after a restart.
    /// </remarks>
    public void Sync(float skyAngle)
    {
        ValidateAngle(skyAngle);

        double mechanical = Snapshot.MechanicalAngle;
        double offset = Normalise(skyAngle - mechanical);

        OnStepXSettings settings = Settings;
        settings.Rotator.SyncOffset = offset;

        Logger.LogInformation(
            "Rotator synced: mechanical {Mechanical} is sky angle {Sky}, offset {Offset}",
            mechanical, skyAngle, offset);
    }

    /// <summary>Stops the rotator where it is.</summary>
    public void Halt()
    {
        RunSync(() => Channel.SendAsync("rQ", CancellationToken.None));

        _poller.Invalidate();
    }

    /// <inheritdoc />
    public override List<StateValue> DeviceState
    {
        get
        {
            RotatorSnapshot? snapshot = _poller.Current;

            if (snapshot is null)
            {
                return [];
            }

            return
            [
                new(nameof(IRotatorV4.IsMoving), snapshot.IsMoving),
                new(nameof(IRotatorV4.MechanicalPosition), (float)Normalise(snapshot.MechanicalAngle)),
                new(nameof(IRotatorV4.Position), (float)Normalise(snapshot.MechanicalAngle + SyncOffset)),
                new("TimeStamp", DateTime.UtcNow),
            ];
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _poller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }

    private async Task MoveMechanicalRawAsync(double mechanicalAngle, CancellationToken cancellationToken)
    {
        RotatorSnapshot snapshot = _poller.Current
            ?? await _poller.RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (mechanicalAngle < snapshot.MinAngle || mechanicalAngle > snapshot.MaxAngle)
        {
            throw new InvalidValueException(
                $"Mechanical angle {mechanicalAngle:0.##} is outside the rotator's travel of " +
                $"{snapshot.MinAngle:0.##} to {snapshot.MaxAngle:0.##} degrees.");
        }

        await Channel.RequireTrueAsync(
            "rS" + Lx200Format.FormatRotatorAngle(mechanicalAngle), cancellationToken)
            .ConfigureAwait(false);

        await _poller.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Brings an angle into the 0 to 360 range that ASCOM requires.</summary>
    private static double Normalise(double degrees)
    {
        double value = degrees % 360.0;

        if (value < 0)
        {
            value += 360.0;
        }

        return value >= 360.0 ? 0.0 : value;
    }

    /// <summary>
    /// Brings an angle into the firmware's own range, which is normally -180 to 180.
    /// </summary>
    private double Denormalise(double degrees)
    {
        RotatorSnapshot? snapshot = _poller.Current;

        // Without a snapshot yet, assume the usual symmetric travel.
        double min = snapshot?.MinAngle ?? -180.0;

        double value = Normalise(degrees);

        // An angle above the top of travel is the same direction expressed negatively.
        if (min < 0 && value > 180.0)
        {
            value -= 360.0;
        }

        return value;
    }

    private static void ValidateAngle(float degrees)
    {
        if (float.IsNaN(degrees) || float.IsInfinity(degrees))
        {
            throw new InvalidValueException($"{degrees} is not a valid angle.");
        }

        if (degrees is < 0 or > 360)
        {
            throw new InvalidValueException(
                $"Angle {degrees} is outside the range 0 to 360 degrees that ASCOM requires.");
        }
    }
}
