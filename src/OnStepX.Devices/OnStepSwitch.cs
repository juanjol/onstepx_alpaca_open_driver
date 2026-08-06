using System.Globalization;
using ASCOM;
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using OnStepX.Core.Config;
using OnStepX.Core.Configuration;
using OnStepX.Core.Devices;
using OnStepX.Core.Hardware;

namespace OnStepX.Devices;

/// <summary>One reading of every exposed auxiliary feature slot.</summary>
public sealed record SwitchSnapshot
{
    /// <summary>The state of each polled slot, keyed by slot number.</summary>
    public required IReadOnlyDictionary<int, FeatureState> Slots { get; init; }
}

/// <summary>
/// ASCOM switch device backed by an OnStepX controller's auxiliary features.
/// </summary>
/// <remarks>
/// <para>
/// <b>A firmware slot and an ASCOM switch are not the same thing.</b> ASCOM presents a flat
/// numbered list where every entry carries exactly one value, while an OnStepX slot is a small
/// device with a purpose: a plain switch carries one value, but a dew heater carries a running
/// flag, two ramp temperatures and a measured delta. So one slot becomes one or two channels,
/// and the mapping is built once when the device connects and never changes afterwards,
/// because clients read <see cref="MaxSwitch"/> once and then iterate it.
/// </para>
/// <para>
/// <b>A dew heater's ramp temperatures are deliberately not channels.</b> ASCOM defines
/// <see cref="SetSwitch"/> with false as "write the minimum value", so the ordinary client
/// habit of walking the list switching everything off would set the ramp start to its lowest
/// possible value and destroy the heater's calibration, and would burn a non volatile storage
/// cell doing it. They belong to the setup page, with the rest of the settings that live in
/// the controller.
/// </para>
/// <para>
/// Two purposes are skipped rather than exposed. An intervalometer reports no running flag in
/// <c>:GXXn#</c>, so a switch for it could be written but never read, and inventing a reading
/// from the frame counter would lie as soon as a sequence finished. A hidden switch is worse:
/// the firmware reports it present, answers the unknown command error when asked for its
/// state, and reports success for writes it never carries out. Both are logged at connect so
/// the reason is visible rather than looking like a driver that lost a slot.
/// </para>
/// </remarks>
public sealed class OnStepSwitch : OnStepDeviceBase, ISwitchV3
{
    private readonly SnapshotPoller<SwitchSnapshot> _poller;
    private readonly ControllerConfiguration _configuration;

    private IReadOnlyList<SwitchChannel> _channels = [];
    private IReadOnlyList<FeatureSlot> _polled = [];

    /// <summary>Creates the switch device.</summary>
    public OnStepSwitch(
        OnStepXConnection connection,
        Func<OnStepXSettings> settingsProvider,
        ILoggerFactory loggerFactory)
        : base(connection, settingsProvider, Require(loggerFactory).CreateLogger<OnStepSwitch>())
    {
        _poller = new SnapshotPoller<SwitchSnapshot>(
            "Switch",
            ReadSnapshotAsync,
            TimeSpan.FromMilliseconds(settingsProvider().Switch.PollIntervalMilliseconds),
            loggerFactory.CreateLogger<OnStepSwitch>());

        // The feature commands are the one place where the controller's own configuration and a
        // device's operational surface are the same thing, so this device reads them through
        // the same class the setup pages use rather than duplicating the parser.
        //
        // No invalidation callback: this device owns the only cache those commands feed and it
        // marks it stale itself after a write. The setup UI has its own instance, whose callback
        // reaches every registered device including this one.
        _configuration = new ControllerConfiguration(
            () => Channel,
            invalidateCaches: null,
            loggerFactory.CreateLogger<ControllerConfiguration>());
    }

    private static ILoggerFactory Require(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory;
    }

    /// <inheritdoc />
    protected override string DeviceKey => "Switch";

    /// <inheritdoc />
    public override void InvalidateSnapshot() => _poller.Invalidate();

    /// <inheritdoc />
    public override string Name => "OnStepX Switch";

    /// <inheritdoc />
    public override string Description =>
        Connection.Identity is { } identity
            ? $"OnStepX auxiliary features, firmware {identity.FirmwareVersion}"
            : "OnStepX auxiliary features";

    /// <inheritdoc />
    public override short InterfaceVersion => 3;

    private SwitchSnapshot Snapshot
    {
        get
        {
            RequireConnected();

            return _poller.GetFresh()
                ?? throw new NotConnectedException("No auxiliary feature state has been read yet.");
        }
    }

    /// <inheritdoc />
    protected override async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        _poller.PollInterval = TimeSpan.FromMilliseconds(Math.Clamp(
            Settings.Switch.PollIntervalMilliseconds,
            SwitchSettings.MinimumPollIntervalMilliseconds,
            SwitchSettings.MaximumPollIntervalMilliseconds));

        // The slot list is the only thing that separates an absent slot from a switch that is
        // off, and the state reply cannot even be parsed without knowing the purpose, so this
        // has to happen before anything else and only once.
        IReadOnlyList<FeatureSlot> slots = await _configuration
            .ReadFeatureSlotsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (slots.Count == 0)
        {
            throw new NotConnectedException(
                "This OnStepX build reports no auxiliary features. Set FEATURE1_PURPOSE or "
                + "another of the eight in the firmware configuration, or do not connect this "
                + "device.");
        }

        IReadOnlyDictionary<int, FeatureState> states = await _configuration
            .ReadFeatureStatesAsync(slots, cancellationToken)
            .ConfigureAwait(false);

        _channels = [.. BuildChannels(slots, states)];
        _polled = [.. slots.Where(slot => slot.Purpose.IsControllable()).OrderBy(slot => slot.Slot)];

        if (_channels.Count == 0)
        {
            throw new NotConnectedException(
                $"This OnStepX build has {slots.Count} auxiliary feature slots but none of them "
                + "can be exposed as a switch. Configure a slot as SWITCH, ANALOG_OUT or "
                + "DEW_HEATER, or do not connect this device.");
        }

        await _poller.StartAsync(cancellationToken).ConfigureAwait(false);

        Logger.LogInformation(
            "Switch connected, {Channels} channels from {Slots} auxiliary feature slots",
            _channels.Count,
            _polled.Count);
    }

    /// <inheritdoc />
    protected override async Task OnDisconnectingAsync() =>
        await _poller.StopAsync().ConfigureAwait(false);

    private async Task<SwitchSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) =>
        new()
        {
            Slots = await _configuration
                .ReadFeatureStatesAsync(_polled, cancellationToken)
                .ConfigureAwait(false),
        };

    // The switch surface

    /// <summary>Number of switch channels, fixed for the life of the connection.</summary>
    public short MaxSwitch
    {
        get
        {
            RequireConnected();

            return (short)_channels.Count;
        }
    }

    /// <summary>The channel's name, which is the firmware's own name for the slot.</summary>
    public string GetSwitchName(short id) => ChannelFor(id).Name;

    /// <summary>What the channel is and how it behaves.</summary>
    public string GetSwitchDescription(short id) => ChannelFor(id).Description;

    /// <summary>Whether the channel can be written.</summary>
    public bool CanWrite(short id) => ChannelFor(id).CanWrite;

    /// <summary>Lowest value the channel reports or accepts.</summary>
    public double MinSwitchValue(short id) => ChannelFor(id).Minimum;

    /// <summary>Highest value the channel reports or accepts.</summary>
    public double MaxSwitchValue(short id) => ChannelFor(id).Maximum;

    /// <summary>Smallest change the channel resolves.</summary>
    public double SwitchStep(short id) => ChannelFor(id).Step;

    /// <summary>The channel's value.</summary>
    public double GetSwitchValue(short id)
    {
        SwitchChannel channel = ChannelFor(id);

        return Read(channel, Snapshot);
    }

    /// <summary>
    /// The channel's value as a boolean: false only when it sits at its minimum.
    /// </summary>
    /// <remarks>
    /// ASCOM leaves intermediate values of a multi state channel to the driver, and treating
    /// anything above the minimum as on is the reading that matches how the firmware thinks
    /// about an output: any power at all is on. For the read only dew heater delta the boolean
    /// carries no useful information, since the minimum is a temperature nothing reaches. There
    /// the value is the reading, and the channel's description says so.
    /// </remarks>
    public bool GetSwitch(short id)
    {
        SwitchChannel channel = ChannelFor(id);

        return Read(channel, Snapshot) > channel.Minimum;
    }

    /// <summary>Sets the channel's value.</summary>
    public void SetSwitchValue(short id, double value)
    {
        SwitchChannel channel = ChannelFor(id);

        if (!channel.CanWrite)
        {
            throw new MethodNotImplementedException(
                $"{channel.Name} is a reading rather than a control, so it cannot be set.");
        }

        if (double.IsNaN(value) || value < channel.Minimum || value > channel.Maximum)
        {
            throw new InvalidValueException(
                $"{Format(value)} is outside the range {Format(channel.Minimum)} to "
                + $"{Format(channel.Maximum)} that {channel.Name} accepts.");
        }

        int requested = (int)Math.Round(value, MidpointRounding.AwayFromZero);

        RunSync(() => WriteAsync(channel, requested, CancellationToken.None));
    }

    /// <summary>Switches the channel on or off.</summary>
    /// <remarks>
    /// On is the channel's maximum, as ASCOM requires, so switching an analog output on runs it
    /// at full power rather than at some remembered level. The firmware has no memory of a
    /// previous level to return to.
    /// </remarks>
    public void SetSwitch(short id, bool state)
    {
        SwitchChannel channel = ChannelFor(id);

        SetSwitchValue(id, state ? channel.Maximum : channel.Minimum);
    }

    /// <summary>Not supported: names come from the controller's own configuration.</summary>
    public void SetSwitchName(short id, string name)
    {
        _ = ChannelFor(id);

        throw new MethodNotImplementedException(
            "A switch name is the name the slot was given in the firmware configuration, so it "
            + "cannot be changed from a client. Change FEATUREn_NAME and reflash.");
    }

    /// <summary>
    /// False for every channel: a write is a single command that has already taken effect by
    /// the time it returns.
    /// </summary>
    public bool CanAsync(short id)
    {
        _ = ChannelFor(id);

        return false;
    }

    /// <summary>Not supported. See <see cref="CanAsync"/>.</summary>
    public void SetAsync(short id, bool state) => throw NoAsynchronousForm(id);

    /// <summary>Not supported. See <see cref="CanAsync"/>.</summary>
    public void SetAsyncValue(short id, double value) => throw NoAsynchronousForm(id);

    /// <summary>Not supported. See <see cref="CanAsync"/>.</summary>
    public bool StateChangeComplete(short id) => throw NoAsynchronousForm(id);

    /// <summary>Not supported. See <see cref="CanAsync"/>.</summary>
    public void CancelAsync(short id) => throw NoAsynchronousForm(id);

    /// <inheritdoc />
    public override List<StateValue> DeviceState
    {
        get
        {
            SwitchSnapshot? snapshot = _poller.Current;

            if (snapshot is null)
            {
                return [];
            }

            List<StateValue> state = [];

            for (int id = 0; id < _channels.Count; id++)
            {
                SwitchChannel channel = _channels[id];

                // A channel the controller stopped reporting is left out rather than filled in
                // with a guess, so the collection never disagrees with the property.
                if (!TryRead(channel, snapshot, out double value))
                {
                    continue;
                }

                string suffix = id.ToString(CultureInfo.InvariantCulture);

                state.Add(new("GetSwitch" + suffix, value > channel.Minimum));
                state.Add(new("GetSwitchValue" + suffix, value));
            }

            state.Add(new("TimeStamp", DateTime.UtcNow));

            return state;
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _poller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }

    // Channel map

    private async Task WriteAsync(
        SwitchChannel channel,
        int value,
        CancellationToken cancellationToken)
    {
        if (channel.Field == SwitchField.DewHeaterEnabled)
        {
            await _configuration
                .WriteDewHeaterEnabledAsync(channel.Slot, value != 0, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _configuration
                .WriteFeatureValueAsync(channel.Slot, value, cancellationToken)
                .ConfigureAwait(false);
        }

        _poller.Invalidate();
    }

    private SwitchChannel ChannelFor(short id)
    {
        RequireConnected();

        if (id < 0 || id >= _channels.Count)
        {
            throw new InvalidValueException(
                $"There is no switch {id}. This device has {_channels.Count}, numbered 0 to "
                + $"{_channels.Count - 1}.");
        }

        return _channels[id];
    }

    private double Read(SwitchChannel channel, SwitchSnapshot snapshot)
    {
        if (!TryRead(channel, snapshot, out double value))
        {
            string reply = snapshot.Slots.TryGetValue(channel.Slot, out FeatureState? state)
                ? state.Raw ?? "nothing"
                : "nothing";

            throw new DriverException(
                $"The controller did not report {channel.Name}. Auxiliary feature slot "
                + $"{channel.Slot} answered {reply}.");
        }

        return value;
    }

    /// <summary>
    /// Reads a channel out of a snapshot, clamped into its declared range.
    /// </summary>
    /// <remarks>
    /// The single place a channel value is produced, so that the property and
    /// <see cref="DeviceState"/> can never report different numbers for the same channel. The
    /// clamp matters for the dew heater delta: a failed temperature sensor reports something
    /// like 85 or -127 degrees, and a value outside the declared range breaks the ASCOM
    /// contract even though the underlying reading is real.
    /// </remarks>
    private bool TryRead(SwitchChannel channel, SwitchSnapshot snapshot, out double value)
    {
        value = 0;

        if (!snapshot.Slots.TryGetValue(channel.Slot, out FeatureState? state))
        {
            return false;
        }

        double? reading = channel.Field switch
        {
            SwitchField.Value => state.Value,
            SwitchField.DewHeaterEnabled => state.DewHeaterEnabled switch
            {
                true => 1.0,
                false => 0.0,
                null => null,
            },
            SwitchField.DewHeaterDeltaT => state.DeltaT,
            _ => null,
        };

        if (reading is not { } raw)
        {
            return false;
        }

        value = Math.Clamp(raw, channel.Minimum, channel.Maximum);

        if (Math.Abs(value - raw) > 1e-9)
        {
            Logger.LogDebug(
                "{Name} reported {Reading}, outside its range of {Minimum} to {Maximum}, so "
                + "{Clamped} is reported instead",
                channel.Name,
                raw,
                channel.Minimum,
                channel.Maximum,
                value);
        }

        return true;
    }

    private IEnumerable<SwitchChannel> BuildChannels(
        IReadOnlyList<FeatureSlot> slots,
        IReadOnlyDictionary<int, FeatureState> states)
    {
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);

        foreach (FeatureSlot slot in slots.OrderBy(slot => slot.Slot))
        {
            string reported = string.IsNullOrWhiteSpace(slot.Name)
                ? "Feature " + slot.Slot.ToString(CultureInfo.InvariantCulture)
                : slot.Name.Trim();

            // Decided before a name is claimed, so a skipped slot cannot make a real one look
            // like a duplicate of something the client never sees.
            if (!slot.Purpose.IsControllable())
            {
                Logger.LogInformation(
                    "Auxiliary feature slot {Slot} ({Name}) has purpose {Purpose} and is not "
                    + "exposed as a switch: {Reason}",
                    slot.Slot,
                    reported,
                    slot.Purpose,
                    slot.Purpose.UncontrollableReason());

                continue;
            }

            string name = Unique(reported, slot.Slot, used);

            switch (slot.Purpose)
            {
                // A momentary switch and a cover switch never reach here, because the firmware
                // reports both as a plain switch. They are handled anyway so that a firmware
                // which stopped flattening them would keep working.
                case FeaturePurpose.Switch:
                case FeaturePurpose.MomentarySwitch:
                case FeaturePurpose.CoverSwitch:
                    yield return new SwitchChannel
                    {
                        Slot = slot.Slot,
                        Field = SwitchField.Value,
                        Name = name,
                        Description =
                            $"Auxiliary feature {slot.Slot} of the OnStepX controller, an on and "
                            + "off switch.",
                        Minimum = 0,
                        Maximum = 1,
                        Step = 1,
                        CanWrite = true,
                    };
                    break;

                case FeaturePurpose.AnalogOutput:
                    yield return new SwitchChannel
                    {
                        Slot = slot.Slot,
                        Field = SwitchField.Value,
                        Name = name,
                        Description =
                            $"Auxiliary feature {slot.Slot} of the OnStepX controller, a pulse "
                            + "width modulated output where 0 is off and 255 is full power.",
                        Minimum = 0,
                        Maximum = FeatureValueMaximum,
                        Step = 1,
                        CanWrite = true,
                    };
                    break;

                case FeaturePurpose.DewHeater:
                    yield return new SwitchChannel
                    {
                        Slot = slot.Slot,
                        Field = SwitchField.DewHeaterEnabled,
                        Name = name,
                        Description =
                            $"Auxiliary feature {slot.Slot} of the OnStepX controller, a dew "
                            + "heater. On lets the controller run its own temperature ramp, "
                            + "which it calibrates from settings held in the controller and "
                            + "edited in the driver's setup pages.",
                        Minimum = 0,
                        Maximum = 1,
                        Step = 1,
                        CanWrite = true,
                    };

                    // Only worth a channel when this slot actually has a temperature sensor and
                    // the dew point is known. Otherwise the controller answers NAN for ever,
                    // and a channel that can never be read is worse than no channel.
                    if (states.TryGetValue(slot.Slot, out FeatureState? state)
                        && state.DeltaT is not null)
                    {
                        yield return new SwitchChannel
                        {
                            Slot = slot.Slot,
                            Field = SwitchField.DewHeaterDeltaT,
                            Name = name + " DeltaT",
                            Description =
                                $"How far auxiliary feature {slot.Slot}'s temperature sensor is "
                                + "above the dew point, in degrees Celsius. A reading, not a "
                                + "control. Read the value rather than the on and off state, "
                                + "which is only false at the bottom of the range.",
                            Minimum = DeltaMinimum,
                            Maximum = DeltaMaximum,
                            Step = 0.1,
                            CanWrite = false,
                        };
                    }

                    break;

                default:
                    // Unreachable: IsControllable already filtered everything else out. Present
                    // so that adding a purpose there without adding a channel here is a compile
                    // time reminder rather than a silently missing switch.
                    throw new DriverException(
                        $"Auxiliary feature slot {slot.Slot} reports purpose {slot.Purpose}, "
                        + "which this driver counts as controllable but does not know how to "
                        + "expose.");
            }
        }
    }

    /// <summary>
    /// Keeps channel names distinct, since nothing stops two slots being given the same name in
    /// the firmware and a client shows the user nothing but the name.
    /// </summary>
    private static string Unique(string name, int slot, HashSet<string> used)
    {
        string candidate = used.Add(name)
            ? name
            : $"{name} ({slot.ToString(CultureInfo.InvariantCulture)})";

        used.Add(candidate);

        return candidate;
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private MethodNotImplementedException NoAsynchronousForm(short id)
    {
        // Validate first, so an out of range identifier is still reported as one.
        _ = ChannelFor(id);

        return new MethodNotImplementedException(
            "Setting an OnStepX auxiliary feature is a single command that has taken effect "
            + "before it returns, so there is no asynchronous form. CanAsync reports false for "
            + "every switch on this device.");
    }

    private const int FeatureValueMaximum = ControllerConfiguration.FeatureValueMaximum;

    /// <summary>
    /// Range declared for a dew heater delta. Wide enough that a real reading is never clamped
    /// and narrow enough to stay meaningful on a client's slider.
    /// </summary>
    private const double DeltaMinimum = -50.0;

    private const double DeltaMaximum = 50.0;

    /// <summary>Which of a slot's values a channel carries.</summary>
    private enum SwitchField
    {
        /// <summary>The slot's output value, for a switch or an analog output.</summary>
        Value,

        /// <summary>Whether a dew heater's ramp is running.</summary>
        DewHeaterEnabled,

        /// <summary>A dew heater's measured delta above the dew point.</summary>
        DewHeaterDeltaT,
    }

    /// <summary>One entry of the ASCOM switch list.</summary>
    private sealed record SwitchChannel
    {
        public required int Slot { get; init; }

        public required SwitchField Field { get; init; }

        public required string Name { get; init; }

        public required string Description { get; init; }

        public required double Minimum { get; init; }

        public required double Maximum { get; init; }

        public required double Step { get; init; }

        public required bool CanWrite { get; init; }
    }
}
