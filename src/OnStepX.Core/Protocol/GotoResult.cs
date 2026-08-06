namespace OnStepX.Core.Protocol;

/// <summary>
/// Code returned by <c>:MS#</c>, <c>:MA#</c>, <c>:MN#</c> and <c>:MP#</c>.
/// </summary>
/// <remarks>
/// It is a distinct set, independent of <see cref="CommandError"/>, with its
/// own numbering from 0 to 9. It arrives as a single digit with no
/// terminator, so it must be read with <see cref="ReplyKind.SingleDigit"/>.
/// Watch out for the sense being reversed compared to the boolean responses
/// of the rest of the protocol: here <b>0 means success</b>.
/// </remarks>
public enum GotoResult
{
    /// <summary>Goto accepted. It is the only success value.</summary>
    Accepted = 0,

    /// <summary>Below the horizon limit.</summary>
    BelowHorizonLimit = 1,

    /// <summary>Above the zenith limit.</summary>
    AboveOverheadLimit = 2,

    /// <summary>The controller is in standby.</summary>
    ControllerInStandby = 3,

    /// <summary>The mount is parked.</summary>
    MountParked = 4,

    /// <summary>A goto is already in progress.</summary>
    GotoInProgress = 5,

    /// <summary>Outside the configured limits.</summary>
    OutsideLimits = 6,

    /// <summary>Hardware fault.</summary>
    HardwareFault = 7,

    /// <summary>The mount is already in motion.</summary>
    AlreadyInMotion = 8,

    /// <summary>Unspecified error.</summary>
    Unspecified = 9,
}

/// <summary>
/// Utilities over <see cref="GotoResult"/>.
/// </summary>
public static class GotoResults
{
    /// <summary>
    /// Interprets the digit returned by a goto.
    /// </summary>
    public static bool TryParse(string? payload, out GotoResult result)
    {
        result = GotoResult.Unspecified;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        string trimmed = payload.Trim();
        if (trimmed.Length != 1 || trimmed[0] < '0' || trimmed[0] > '9')
        {
            return false;
        }

        result = (GotoResult)(trimmed[0] - '0');
        return true;
    }

    /// <summary>
    /// Unlike the rest of the protocol, here success is zero.
    /// </summary>
    public static bool IsAccepted(this GotoResult result) => result == GotoResult.Accepted;

    /// <summary>
    /// Readable message in English.
    /// </summary>
    public static string Describe(this GotoResult result) => result switch
    {
        GotoResult.Accepted => "Goto accepted",
        GotoResult.BelowHorizonLimit => "The target is below the horizon limit",
        GotoResult.AboveOverheadLimit => "The target is above the zenith limit",
        GotoResult.ControllerInStandby => "The controller is in standby, tracking must be started",
        GotoResult.MountParked => "The mount is parked, it must be unparked first",
        GotoResult.GotoInProgress => "A goto is already in progress",
        GotoResult.OutsideLimits => "The target is outside the configured limits",
        GotoResult.HardwareFault => "Hardware fault",
        GotoResult.AlreadyInMotion => "The mount is already in motion",
        GotoResult.Unspecified => "The goto failed for an unspecified reason",
        _ => $"Unknown goto code ({(int)result})",
    };
}
