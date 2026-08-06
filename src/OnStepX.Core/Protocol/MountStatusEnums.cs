namespace OnStepX.Core.Protocol;

/// <summary>Parked state. The four values are mutually exclusive.</summary>
public enum ParkState
{
    /// <summary>The firmware did not report any park state.</summary>
    Unknown,

    /// <summary>Not parked. Character <c>p</c>.</summary>
    Unparked,

    /// <summary>Parking. Character <c>I</c>.</summary>
    Parking,

    /// <summary>Parked. Character <c>P</c>.</summary>
    Parked,

    /// <summary>Parking failed. Character <c>F</c>.</summary>
    ParkFailed,
}

/// <summary>
/// Mount type as reported by <c>:GU#</c>.
/// </summary>
/// <remarks>
/// This is a small set of four values. It must not be confused with
/// <c>:GXEM#</c>, which returns a number from 1 to 9 and includes the
/// tangent arm and sector variants, and therefore has its own enumeration.
/// </remarks>
public enum MountKind
{
    /// <summary>Not reported.</summary>
    Unknown,

    /// <summary>German equatorial. Character <c>E</c>.</summary>
    Gem,

    /// <summary>Fork. Character <c>K</c>.</summary>
    Fork,

    /// <summary>Alt azimuth. Character <c>A</c>.</summary>
    AltAzm,

    /// <summary>Alt alt. Character <c>L</c>.</summary>
    AltAlt,
}

/// <summary>Pier side.</summary>
public enum PierSide
{
    /// <summary>Not reported.</summary>
    Unknown,

    /// <summary>
    /// None. Character <c>o</c> in <c>:GU#</c> and <c>N</c> in <c>:Gm#</c>.
    /// </summary>
    None,

    /// <summary>
    /// East. Character <c>T</c> in <c>:GU#</c> and <c>E</c> in <c>:Gm#</c>.
    /// </summary>
    East,

    /// <summary>West. Character <c>W</c> in both commands.</summary>
    West,
}

/// <summary>
/// Tracking compensation mode.
/// </summary>
/// <remarks>
/// The firmware encodes it with two paired characters, not a single one:
/// <c>r</c> plus <c>s</c> is single axis refraction, <c>r</c> alone is dual
/// axis refraction, <c>t</c> plus <c>s</c> is single axis full model, and
/// <c>t</c> alone is dual axis full model. Interpreting <c>s</c> on its own
/// leads to wrong conclusions.
/// </remarks>
public enum TrackingCompensation
{
    /// <summary>No compensation. <c>RC_NONE</c>.</summary>
    None,

    /// <summary>Refraction, single axis. <c>r</c> plus <c>s</c>.</summary>
    RefractionSingleAxis,

    /// <summary>Refraction, dual axis. Only <c>r</c>.</summary>
    RefractionDualAxis,

    /// <summary>Full model, single axis. <c>t</c> plus <c>s</c>.</summary>
    ModelSingleAxis,

    /// <summary>Full model, dual axis. Only <c>t</c>.</summary>
    ModelDualAxis,
}

/// <summary>
/// Selected tracking rate.
/// </summary>
/// <remarks>
/// <b>Only observable if <see cref="TrackingCompensation.None"/> is
/// active.</b> The firmware only emits the characters <c>(</c>, <c>O</c>
/// and <c>k</c> inside the <c>rc == RC_NONE</c> branch, so with
/// compensation active this data does not come in <c>:GU#</c> and must be
/// read with <c>:GT#</c>.
/// </remarks>
public enum MountTrackingRate
{
    /// <summary>
    /// Not determinable from <c>:GU#</c>, because compensation is active.
    /// </summary>
    Unknown,

    /// <summary>Sidereal. This is the one with no character of its own.</summary>
    Sidereal,

    /// <summary>Lunar, 57.900 Hz. Character <c>(</c>.</summary>
    Lunar,

    /// <summary>Solar, 60.000 Hz. Character <c>O</c>.</summary>
    Solar,

    /// <summary>King, 60.136 Hz. Character <c>k</c>.</summary>
    King,
}

/// <summary>
/// What the mount does upon reaching the meridian.
/// </summary>
public enum MeridianFlipHomeMode
{
    /// <summary>
    /// Direct slew. This is the active mode when neither <c>v</c> nor
    /// <c>u</c> appears.
    /// </summary>
    DirectSlew,

    /// <summary>Visits home. Character <c>v</c>.</summary>
    VisitHome,

    /// <summary>Pauses at home. Character <c>u</c>.</summary>
    PauseAtHome,
}

/// <summary>
/// PEC state, from the firmware's <c>"/,~;^"</c> literal.
/// </summary>
public enum PecState
{
    /// <summary>
    /// Not reported. Happens if PEC is not compiled in or the mount is not
    /// in equatorial mode.
    /// </summary>
    Unknown,

    /// <summary>Ignore. Character <c>/</c>.</summary>
    Ignore,

    /// <summary>Ready to play. Character <c>,</c>.</summary>
    ReadyPlaying,

    /// <summary>Playing. Character <c>~</c>.</summary>
    Playing,

    /// <summary>Ready to record. Character <c>;</c>.</summary>
    ReadyRecording,

    /// <summary>Recording. Character <c>^</c>.</summary>
    Recording,
}
