namespace OnStepX.Core.Protocol;

/// <summary>
/// Error codes returned by <c>:GE#</c>.
/// </summary>
/// <remarks>
/// They correspond to <c>src/lib/commands/CommandErrors.h</c> of the
/// firmware. Watch out for 24, which does not exist: the enumeration jumps
/// from <see cref="SlewErrorUnspecified"/> (23) to <see cref="True"/> (25).
/// </remarks>
public enum CommandError
{
    /// <summary>No error. <c>CE_NONE</c>.</summary>
    None = 0,

    /// <summary>False or failure, no protocol error. <c>CE_0</c>.</summary>
    False = 1,

    /// <summary>Unknown command. <c>CE_CMD_UNKNOWN</c>.</summary>
    CommandUnknown = 2,

    /// <summary>Invalid response. <c>CE_REPLY_UNKNOWN</c>.</summary>
    ReplyUnknown = 3,

    /// <summary>Parameter out of range. <c>CE_PARAM_RANGE</c>.</summary>
    ParameterRange = 4,

    /// <summary>Incorrect parameter format. <c>CE_PARAM_FORM</c>.</summary>
    ParameterForm = 5,

    /// <summary>Alignment failed. <c>CE_ALIGN_FAIL</c>.</summary>
    AlignFail = 6,

    /// <summary>Alignment is not active. <c>CE_ALIGN_NOT_ACTIVE</c>.</summary>
    AlignNotActive = 7,

    /// <summary>Not parked nor at home. <c>CE_NOT_PARKED_OR_AT_HOME</c>.</summary>
    NotParkedOrAtHome = 8,

    /// <summary>Already parked. <c>CE_PARKED</c>.</summary>
    Parked = 9,

    /// <summary>Parking failed. <c>CE_PARK_FAILED</c>.</summary>
    ParkFailed = 10,

    /// <summary>Not parked. <c>CE_NOT_PARKED</c>.</summary>
    NotParked = 11,

    /// <summary>No park position set. <c>CE_NO_PARK_POSITION_SET</c>.</summary>
    NoParkPositionSet = 12,

    /// <summary>Goto failed. <c>CE_GOTO_FAIL</c>.</summary>
    GotoFail = 13,

    /// <summary>Library full. <c>CE_LIBRARY_FULL</c>.</summary>
    LibraryFull = 14,

    /// <summary>Target below the horizon limit. <c>CE_SLEW_ERR_BELOW_HORIZON</c>.</summary>
    SlewErrorBelowHorizon = 15,

    /// <summary>Target above the zenith limit. <c>CE_SLEW_ERR_ABOVE_OVERHEAD</c>.</summary>
    SlewErrorAboveOverhead = 16,

    /// <summary>Controller in standby. <c>CE_SLEW_ERR_IN_STANDBY</c>.</summary>
    SlewErrorInStandby = 17,

    /// <summary>Mount parked. <c>CE_SLEW_ERR_IN_PARK</c>.</summary>
    SlewErrorInPark = 18,

    /// <summary>A goto is already in progress. <c>CE_SLEW_IN_SLEW</c>.</summary>
    SlewInSlew = 19,

    /// <summary>Outside the configured limits. <c>CE_SLEW_ERR_OUTSIDE_LIMITS</c>.</summary>
    SlewErrorOutsideLimits = 20,

    /// <summary>Hardware fault. <c>CE_SLEW_ERR_HARDWARE_FAULT</c>.</summary>
    SlewErrorHardwareFault = 21,

    /// <summary>The mount is already in motion. <c>CE_SLEW_IN_MOTION</c>.</summary>
    SlewInMotion = 22,

    /// <summary>Other slew error. <c>CE_SLEW_ERR_UNSPECIFIED</c>.</summary>
    SlewErrorUnspecified = 23,

    /// <summary>True, or explicit success. <c>CE_1</c>.</summary>
    True = 25,
}

/// <summary>
/// Utilities over <see cref="CommandError"/>.
/// </summary>
public static class CommandErrors
{
    /// <summary>
    /// Interprets the payload of <c>:GE#</c>, which is two digits.
    /// </summary>
    public static bool TryParse(string? payload, out CommandError error)
    {
        error = CommandError.None;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        if (!int.TryParse(payload.Trim(), out int code))
        {
            return false;
        }

        // 24 is not assigned, so an unrecognized value is rejected instead
        // of turning into an invalid enum.
        if (!Enum.IsDefined(typeof(CommandError), code))
        {
            return false;
        }

        error = (CommandError)code;
        return true;
    }

    /// <summary>
    /// Indicates whether the code represents success. The firmware treats
    /// <see cref="CommandError.None"/> and <see cref="CommandError.True"/>
    /// as the two forms of success, and responds <c>1</c> in both cases.
    /// </summary>
    public static bool IsSuccess(this CommandError error) =>
        error is CommandError.None or CommandError.True;

    /// <summary>
    /// Readable message in English.
    /// </summary>
    public static string Describe(this CommandError error) => error switch
    {
        CommandError.None => "No error",
        CommandError.False => "The command returned false",
        CommandError.CommandUnknown => "Command unknown to this firmware",
        CommandError.ReplyUnknown => "Invalid response",
        CommandError.ParameterRange => "Parameter out of range",
        CommandError.ParameterForm => "Incorrect parameter format",
        CommandError.AlignFail => "Alignment failed",
        CommandError.AlignNotActive => "Alignment is not active",
        CommandError.NotParkedOrAtHome => "The mount is not parked nor at home",
        CommandError.Parked => "The mount is already parked",
        CommandError.ParkFailed => "Parking failed",
        CommandError.NotParked => "The mount is not parked",
        CommandError.NoParkPositionSet => "No park position is set",
        CommandError.GotoFail => "The goto failed",
        CommandError.LibraryFull => "The object library is full",
        CommandError.SlewErrorBelowHorizon => "The target is below the horizon limit",
        CommandError.SlewErrorAboveOverhead => "The target is above the zenith limit",
        CommandError.SlewErrorInStandby => "The controller is in standby",
        CommandError.SlewErrorInPark => "The mount is parked",
        CommandError.SlewInSlew => "A goto is already in progress",
        CommandError.SlewErrorOutsideLimits => "The target is outside the configured limits",
        CommandError.SlewErrorHardwareFault => "Hardware fault",
        CommandError.SlewInMotion => "The mount is already in motion",
        CommandError.SlewErrorUnspecified => "Unspecified slew error",
        CommandError.True => "Success",
        _ => $"Unknown error code ({(int)error})",
    };
}
