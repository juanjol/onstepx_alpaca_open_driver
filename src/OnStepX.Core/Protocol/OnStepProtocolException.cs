namespace OnStepX.Core.Protocol;

/// <summary>
/// Protocol failure while talking to OnStepX: corrupted response, deadline
/// expired after exhausting retries, or channel desynchronization.
/// </summary>
public class OnStepProtocolException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OnStepProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception, chaining the cause.</summary>
    public OnStepProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Command that caused the failure, unframed.</summary>
    public string? Payload { get; init; }
}

/// <summary>
/// The command was sent and answered correctly, but the firmware reported
/// an application error.
/// </summary>
public sealed class OnStepCommandException : OnStepProtocolException
{
    /// <summary>Creates the exception from an error code.</summary>
    public OnStepCommandException(CommandError error, string? payload = null)
        : base($"The command {payload ?? "(unknown)"} failed: {error.Describe()}")
    {
        Error = error;
        Payload = payload;
    }

    /// <summary>Code returned by <c>:GE#</c>.</summary>
    public CommandError Error { get; }
}
