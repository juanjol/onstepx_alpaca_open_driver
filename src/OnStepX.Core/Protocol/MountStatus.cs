namespace OnStepX.Core.Protocol;

/// <summary>
/// Mount status, the result of interpreting the response to <c>:GU#</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the driver's central query: a single <c>:GU#</c> per cycle
/// feeds <c>Slewing</c>, <c>AtPark</c>, <c>AtHome</c>, <c>Tracking</c>,
/// <c>SideOfPier</c> and the rest of the ASCOM state, instead of one
/// command per property.
/// </para>
/// <para>
/// Response structure, according to
/// <c>src/telescope/mount/status/Status.command.cpp</c>: a set of flag
/// characters in indeterminate order, and at the end <b>always three
/// digits</b> which are, in order, the pulse guide rate index, the guide
/// rate index, and the general error code.
/// </para>
/// <para>
/// <b>Watch out for the inverted logic.</b> Three flags assert the
/// opposite of what they look like: <c>n</c> means "NOT tracking",
/// <c>N</c> means "NO goto" and <c>p</c> means "NOT parked". The
/// <b>absence</b> of <c>n</c> is what means the mount is tracking.
/// </para>
/// </remarks>
public sealed record MountStatus
{
    /// <summary>Original response, useful for traces and diagnostics.</summary>
    public string Raw { get; init; } = string.Empty;

    /// <summary>
    /// The mount is tracking. Deduced from the <b>absence</b> of <c>n</c>.
    /// </summary>
    public bool IsTracking { get; init; }

    /// <summary>
    /// A goto is in progress. Deduced from the <b>absence</b> of <c>N</c>.
    /// </summary>
    public bool IsGotoActive { get; init; }

    /// <summary>Parked state.</summary>
    public ParkState ParkState { get; init; }

    /// <summary>Syncs only to the encoders. Character <c>e</c>.</summary>
    public bool SyncToEncodersOnly { get; init; }

    /// <summary>Is at the home position. Character <c>H</c>.</summary>
    public bool IsAtHome { get; init; }

    /// <summary>Is going to home. Character <c>h</c>.</summary>
    public bool IsHoming { get; init; }

    /// <summary>Auto home on boot. Character <c>B</c>.</summary>
    public bool AutoHomeAtBoot { get; init; }

    /// <summary>Synced by PPS. Character <c>S</c>.</summary>
    public bool PpsSynced { get; init; }

    /// <summary>Pulse guide in progress. Character <c>G</c>.</summary>
    public bool PulseGuideActive { get; init; }

    /// <summary>Guiding in progress. Character <c>g</c>.</summary>
    public bool GuideActive { get; init; }

    /// <summary>Tracking compensation mode.</summary>
    public TrackingCompensation Compensation { get; init; }

    /// <summary>
    /// Tracking rate. Only determinable if <see cref="Compensation"/> is
    /// <see cref="TrackingCompensation.None"/>.
    /// </summary>
    public MountTrackingRate TrackingRate { get; init; }

    /// <summary>Waiting at home after a pause. Character <c>w</c>.</summary>
    public bool WaitingAtHome { get; init; }

    /// <summary>What it does upon reaching the meridian.</summary>
    public MeridianFlipHomeMode MeridianFlipHomeMode { get; init; }

    /// <summary>Buzzer enabled. Character <c>z</c>.</summary>
    public bool BuzzerEnabled { get; init; }

    /// <summary>Automatic meridian flip enabled. Character <c>a</c>.</summary>
    public bool AutoMeridianFlip { get; init; }

    /// <summary>There is recorded PEC data. Character <c>R</c>.</summary>
    public bool PecRecorded { get; init; }

    /// <summary>PEC state.</summary>
    public PecState PecState { get; init; }

    /// <summary>Mount type.</summary>
    public MountKind MountKind { get; init; }

    /// <summary>Pier side.</summary>
    public PierSide PierSide { get; init; }

    /// <summary>Pulse guide rate index. First trailing digit.</summary>
    public int PulseGuideRateSelect { get; init; }

    /// <summary>Guide rate index. Second trailing digit.</summary>
    public int GuideRateSelect { get; init; }

    /// <summary>General error code. Third trailing digit.</summary>
    public int GeneralErrorCode { get; init; }

    /// <summary>
    /// Whether the three trailing digits were well formed. If <c>false</c>
    /// the flags are still valid but the indices are not.
    /// </summary>
    public bool TrailingDigitsValid { get; init; }

    /// <summary>The mount is in motion due to a goto or a homing operation.</summary>
    public bool IsSlewing => IsGotoActive || IsHoming;

    /// <summary>Shortcut: genuinely parked.</summary>
    public bool IsParked => ParkState == ParkState.Parked;

    /// <summary>
    /// Interprets the payload of <c>:GU#</c>.
    /// </summary>
    /// <param name="payload">
    /// Response without the trailing <c>#</c>. Having it is tolerated.
    /// </param>
    public static MountStatus Parse(string? payload)
    {
        string s = (payload ?? string.Empty).Trim();
        if (s.EndsWith('#'))
        {
            s = s[..^1];
        }

        // The last three characters are positional, not flags. They must
        // be separated before treating the rest as a set, because no flag
        // is a digit but the error code can coincide with characters that
        // appear in other positions.
        bool trailingValid = s.Length >= 3
            && char.IsAsciiDigit(s[^1])
            && char.IsAsciiDigit(s[^2])
            && char.IsAsciiDigit(s[^3]);

        string flags = trailingValid ? s[..^3] : s;

        bool Has(char c) => flags.Contains(c, StringComparison.Ordinal);

        // Compensation: 's' never appears alone, it always accompanies
        // 'r' or 't'.
        bool single = Has('s');
        TrackingCompensation compensation =
            Has('r') ? (single ? TrackingCompensation.RefractionSingleAxis
                              : TrackingCompensation.RefractionDualAxis)
          : Has('t') ? (single ? TrackingCompensation.ModelSingleAxis
                              : TrackingCompensation.ModelDualAxis)
          : TrackingCompensation.None;

        // The rate is only emitted on the branch with no compensation.
        // With compensation active it cannot be known from here and
        // :GT# must be queried instead.
        MountTrackingRate rate;
        if (compensation != TrackingCompensation.None)
        {
            rate = MountTrackingRate.Unknown;
        }
        else if (Has('('))
        {
            rate = MountTrackingRate.Lunar;
        }
        else if (Has('O'))
        {
            rate = MountTrackingRate.Solar;
        }
        else if (Has('k'))
        {
            rate = MountTrackingRate.King;
        }
        else
        {
            // Sidereal is the one with no character of its own.
            rate = MountTrackingRate.Sidereal;
        }

        return new MountStatus
        {
            Raw = payload ?? string.Empty,

            // Inverted logic: the flag asserts the negation.
            IsTracking = !Has('n'),
            IsGotoActive = !Has('N'),

            ParkState = Has('p') ? ParkState.Unparked
                      : Has('I') ? ParkState.Parking
                      : Has('P') ? ParkState.Parked
                      : Has('F') ? ParkState.ParkFailed
                      : ParkState.Unknown,

            SyncToEncodersOnly = Has('e'),
            IsAtHome = Has('H'),
            IsHoming = Has('h'),
            AutoHomeAtBoot = Has('B'),
            PpsSynced = Has('S'),
            PulseGuideActive = Has('G'),
            GuideActive = Has('g'),

            Compensation = compensation,
            TrackingRate = rate,

            WaitingAtHome = Has('w'),

            MeridianFlipHomeMode = Has('u') ? MeridianFlipHomeMode.PauseAtHome
                                 : Has('v') ? MeridianFlipHomeMode.VisitHome
                                 : MeridianFlipHomeMode.DirectSlew,

            BuzzerEnabled = Has('z'),
            AutoMeridianFlip = Has('a'),
            PecRecorded = Has('R'),

            PecState = Has('/') ? PecState.Ignore
                     : Has(',') ? PecState.ReadyPlaying
                     : Has('~') ? PecState.Playing
                     : Has(';') ? PecState.ReadyRecording
                     : Has('^') ? PecState.Recording
                     : PecState.Unknown,

            MountKind = Has('E') ? MountKind.Gem
                      : Has('K') ? MountKind.Fork
                      : Has('A') ? MountKind.AltAzm
                      : Has('L') ? MountKind.AltAlt
                      : MountKind.Unknown,

            PierSide = Has('o') ? PierSide.None
                     : Has('T') ? PierSide.East
                     : Has('W') ? PierSide.West
                     : PierSide.Unknown,

            TrailingDigitsValid = trailingValid,
            PulseGuideRateSelect = trailingValid ? s[^3] - '0' : -1,
            GuideRateSelect = trailingValid ? s[^2] - '0' : -1,
            GeneralErrorCode = trailingValid ? s[^1] - '0' : -1,
        };
    }

    /// <summary>
    /// Interprets the response to <c>:Gm#</c>, which uses different
    /// letters from <c>:GU#</c> for the same concept: <c>N</c> none,
    /// <c>E</c> east, <c>W</c> west.
    /// </summary>
    public static PierSide ParseMeridianPierSide(string? payload)
    {
        string s = (payload ?? string.Empty).Trim().TrimEnd('#').Trim();

        return s.Length == 0 ? PierSide.Unknown : s[0] switch
        {
            'N' => PierSide.None,
            'E' => PierSide.East,
            'W' => PierSide.West,
            _ => PierSide.Unknown,
        };
    }
}
