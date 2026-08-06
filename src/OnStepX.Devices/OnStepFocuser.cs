using ASCOM;
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using OnStepX.Core.Config;
using OnStepX.Core.Devices;
using OnStepX.Core.Hardware;
using OnStepX.Core.Protocol;

namespace OnStepX.Devices;

/// <summary>One reading of the focuser's state.</summary>
public sealed record FocuserSnapshot
{
    /// <summary>Position in <b>steps</b>, as the firmware reports it.</summary>
    public required long RawPosition { get; init; }

    /// <summary>Lowest position the firmware allows, in steps.</summary>
    public required long MinPosition { get; init; }

    /// <summary>Highest position the firmware allows, in steps.</summary>
    public required long MaxPosition { get; init; }

    /// <summary>Microns per step.</summary>
    public required double MicronsPerStep { get; init; }

    /// <summary>The focuser is moving.</summary>
    public required bool IsMoving { get; init; }

    /// <summary>Temperature compensation is switched on.</summary>
    public required bool TempCompEnabled { get; init; }

    /// <summary>Focuser temperature in degrees Celsius, if a sensor is fitted.</summary>
    public double? Temperature { get; init; }
}

/// <summary>
/// ASCOM focuser device backed by an OnStepX controller.
/// </summary>
/// <remarks>
/// <para>
/// <b>Units are the thing to get right here.</b> The protocol exposes the same
/// operations at two scales: upper case <c>B D G I M R S</c> work in <b>microns</b> and
/// lower case <c>b d g i m r s</c> in <b>raw steps</b>. ASCOM's
/// <see cref="Position"/>, <see cref="MaxStep"/> and <see cref="MaxIncrement"/> are step
/// counts, so this driver drives everything from the lower case commands and uses
/// <c>:Fu#</c> only to report <see cref="StepSize"/>.
/// </para>
/// <para>
/// Getting that backwards does not fail loudly. It reports positions that look
/// perfectly plausible and are wrong by the microns per step factor, so autofocus runs
/// converge on the wrong place and nobody knows why.
/// </para>
/// <para>
/// A second, quieter trap: ASCOM requires positions from zero to
/// <see cref="MaxStep"/>, while OnStep's travel can start at a non zero minimum. The
/// driver therefore reports positions <b>relative to that minimum</b> and converts on
/// the way in and out.
/// </para>
/// </remarks>
public sealed class OnStepFocuser : OnStepDeviceBase, IFocuserV4
{
    private readonly SnapshotPoller<FocuserSnapshot> _poller;
    private readonly int _focuserNumber;

    /// <summary>Creates the focuser device.</summary>
    /// <param name="focuserNumber">
    /// Which of OnStep's six focusers this device drives, from 1 to 6.
    /// </param>
    public OnStepFocuser(
        OnStepXConnection connection,
        Func<OnStepXSettings> settingsProvider,
        ILoggerFactory loggerFactory,
        int focuserNumber = 1)
        : base(connection, settingsProvider, Require(loggerFactory).CreateLogger<OnStepFocuser>())
    {
        if (focuserNumber is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(
                nameof(focuserNumber), focuserNumber, "OnStep supports focusers 1 to 6.");
        }

        _focuserNumber = focuserNumber;

        _poller = new SnapshotPoller<FocuserSnapshot>(
            "Focuser",
            ReadSnapshotAsync,
            TimeSpan.FromMilliseconds(settingsProvider().Connection.PollIntervalMilliseconds),
            loggerFactory.CreateLogger<OnStepFocuser>());
    }

    private static ILoggerFactory Require(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory;
    }

    /// <inheritdoc />
    protected override string DeviceKey => "Focuser";

    /// <inheritdoc />
    public override void InvalidateSnapshot() => _poller.Invalidate();

    /// <inheritdoc />
    public override string Name => $"OnStepX Focuser {_focuserNumber}";

    /// <inheritdoc />
    public override string Description =>
        Connection.Identity is { } identity
            ? $"OnStepX focuser {_focuserNumber}, firmware {identity.FirmwareVersion}"
            : $"OnStepX focuser {_focuserNumber}";

    /// <inheritdoc />
    public override short InterfaceVersion => 4;

    private FocuserSnapshot Snapshot
    {
        get
        {
            RequireConnected();

            FocuserSnapshot? snapshot = _poller.GetFresh();

            return snapshot
                ?? throw new NotConnectedException("No focuser status has been read yet.");
        }
    }

    /// <inheritdoc />
    protected override async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        OnStepXSettings settings = Settings;

        _poller.PollInterval = TimeSpan.FromMilliseconds(
            Math.Clamp(settings.Connection.PollIntervalMilliseconds, 100, 10_000));

        // Select this focuser before anything else, because every later command without
        // an explicit number is aimed at whichever one is active.
        await Channel.RequireTrueAsync($"FA{_focuserNumber}", cancellationToken)
            .ConfigureAwait(false);

        await _poller.StartAsync(cancellationToken).ConfigureAwait(false);

        if (settings.Focuser.MoveToPositionOnConnect)
        {
            await MoveOnConnectAsync(settings.Focuser.PositionOnConnect, cancellationToken)
                .ConfigureAwait(false);
        }

        Logger.LogInformation(
            "Focuser {Number} connected, travel {Min} to {Max} steps, {Microns} um per step",
            _focuserNumber,
            _poller.Current!.MinPosition,
            _poller.Current.MaxPosition,
            _poller.Current.MicronsPerStep);
    }

    /// <summary>
    /// Sends the focuser to its configured start position.
    /// </summary>
    /// <remarks>
    /// Started but deliberately <b>not awaited</b>. ASCOM's connect must not block for
    /// the length of a focuser travel, and <see cref="IsMoving"/> already tells the
    /// client that something is happening. It is also why the feature ships switched
    /// off: a device that moves the moment it connects surprises people.
    /// </remarks>
    private async Task MoveOnConnectAsync(int position, CancellationToken cancellationToken)
    {
        try
        {
            int clamped = Math.Clamp(position, 0, MaxStep);

            if (clamped != position)
            {
                Logger.LogWarning(
                    "Configured start position {Requested} is outside the travel, using {Used}",
                    position, clamped);
            }

            Logger.LogInformation("Moving focuser to its configured start position {Position}", clamped);

            await MoveToRawAsync(ToRaw(clamped), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed start move must not stop the device connecting: the user can
            // still focus by hand.
            Logger.LogWarning(ex, "Could not move the focuser to its configured start position");
        }
    }

    /// <inheritdoc />
    protected override async Task OnDisconnectingAsync() =>
        await _poller.StopAsync().ConfigureAwait(false);

    private async Task<FocuserSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        // Lower case commands throughout: these are step counts, not microns.
        long position = await Channel.GetInt64Async("Fg", cancellationToken).ConfigureAwait(false);
        long min = await Channel.GetInt64Async("Fi", cancellationToken).ConfigureAwait(false);
        long max = await Channel.GetInt64Async("Fm", cancellationToken).ConfigureAwait(false);
        double stepSize = await Channel.GetDoubleAsync("Fu", cancellationToken).ConfigureAwait(false);

        string status = await Channel.GetStringAsync("FT", cancellationToken).ConfigureAwait(false);
        string tempComp = await Channel.GetStringAsync("Fc", cancellationToken).ConfigureAwait(false);

        double? temperature = null;
        try
        {
            temperature = await Channel.GetDoubleAsync("Ft", cancellationToken).ConfigureAwait(false);
        }
        catch (OnStepProtocolException)
        {
            // No temperature probe fitted. Reported as unavailable rather than as zero.
        }

        return new FocuserSnapshot
        {
            RawPosition = position,
            MinPosition = min,
            MaxPosition = max,
            MicronsPerStep = stepSize,

            // :FT# answers with a state letter then a rate digit, M for moving and S
            // for stopped.
            IsMoving = status.StartsWith('M'),
            TempCompEnabled = tempComp.StartsWith('1'),
            Temperature = temperature,
        };
    }

    /// <summary>OnStep focusers are absolute positioners.</summary>
    public bool Absolute => true;

    /// <summary>
    /// Current position in steps, counted from the start of travel.
    /// </summary>
    public int Position
    {
        get
        {
            FocuserSnapshot snapshot = Snapshot;

            return (int)(snapshot.RawPosition - snapshot.MinPosition);
        }
    }

    /// <summary>Total travel in steps.</summary>
    public int MaxStep
    {
        get
        {
            FocuserSnapshot snapshot = Snapshot;

            return (int)(snapshot.MaxPosition - snapshot.MinPosition);
        }
    }

    /// <summary>
    /// Largest single move, in steps.
    /// </summary>
    /// <remarks>
    /// Equal to <see cref="MaxStep"/>: an absolute focuser can be sent anywhere within
    /// its travel in one command.
    /// </remarks>
    public int MaxIncrement => MaxStep;

    /// <summary>
    /// Microns per step, from <c>:Fu#</c>.
    /// </summary>
    /// <remarks>
    /// Reported for display and for clients that want physical units. It is
    /// <b>never</b> used to convert <see cref="Position"/>, which is already in steps.
    /// </remarks>
    public double StepSize => Snapshot.MicronsPerStep;

    /// <summary>The focuser is moving.</summary>
    public bool IsMoving => Snapshot.IsMoving;

    /// <summary>Temperature compensation is available on OnStep focusers.</summary>
    public bool TempCompAvailable => true;

    /// <summary>Temperature compensation on or off.</summary>
    public bool TempComp
    {
        get => Snapshot.TempCompEnabled;

        set
        {
            RunSync(() => Channel.RequireTrueAsync(
                value ? "Fc1" : "Fc0", CancellationToken.None));

            _poller.Invalidate();
        }
    }

    /// <summary>
    /// Focuser temperature in degrees Celsius.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="PropertyNotImplementedException"/> when no probe is fitted,
    /// rather than returning zero. Zero degrees is a perfectly believable temperature,
    /// so a client would use it and apply a compensation that is entirely made up.
    /// </remarks>
    public double Temperature =>
        Snapshot.Temperature
        ?? throw new PropertyNotImplementedException(
            "This focuser has no temperature probe.");

    /// <summary>
    /// Moves to an absolute position, in steps from the start of travel.
    /// </summary>
    /// <remarks>
    /// Asynchronous, as ASCOM requires for a focuser: it returns as soon as the move is
    /// accepted and <see cref="IsMoving"/> reports progress.
    /// </remarks>
    public void Move(int position)
    {
        // Interface version 3 changed two rules here, and both are easy to get wrong by
        // reasoning from first principles instead of reading the specification.
        //
        // First, moving while temperature compensation is active is <b>allowed</b> from
        // version 3 onwards. Only versions 1 and 2 forbade it. Refusing looks defensive
        // but breaks any client that leaves compensation on, which is most of them.
        //
        // Second, a position outside the travel must be <b>clamped, not rejected</b>.
        // The specification asks the driver to move to the nearest end rather than throw,
        // so a client stepping past the end of travel keeps working instead of failing
        // mid sequence.
        int limit = MaxStep;
        int clamped = Math.Clamp(position, 0, limit);

        if (clamped != position)
        {
            Logger.LogDebug(
                "Move to {Requested} is outside the travel of 0 to {Limit}, clamped to {Clamped}",
                position, limit, clamped);
        }

        RunSync(() => MoveToRawAsync(ToRaw(clamped), CancellationToken.None));
    }

    /// <summary>Stops the focuser where it is.</summary>
    public void Halt()
    {
        RunSync(() => Channel.SendAsync("FQ", CancellationToken.None));

        _poller.Invalidate();
    }

    /// <inheritdoc />
    public override List<StateValue> DeviceState
    {
        get
        {
            FocuserSnapshot? snapshot = _poller.Current;

            if (snapshot is null)
            {
                return [];
            }

            var state = new List<StateValue>
            {
                new(nameof(IFocuserV4.IsMoving), snapshot.IsMoving),
                new(nameof(IFocuserV4.Position), (int)(snapshot.RawPosition - snapshot.MinPosition)),
                new("TimeStamp", DateTime.UtcNow),
            };

            if (snapshot.Temperature is double temperature)
            {
                state.Add(new StateValue(nameof(IFocuserV4.Temperature), temperature));
            }

            return state;
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _poller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }

    /// <summary>Converts an ASCOM position into the firmware's own step number.</summary>
    private long ToRaw(int ascomPosition) => ascomPosition + Snapshot.MinPosition;

    private async Task MoveToRawAsync(long rawPosition, CancellationToken cancellationToken)
    {
        // Lower case :Fs# is the step based absolute goto. The upper case form would be
        // interpreted as microns.
        await Channel.RequireTrueAsync(
            "Fs" + rawPosition.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);

        await _poller.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }
}
