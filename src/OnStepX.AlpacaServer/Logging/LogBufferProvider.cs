namespace OnStepX.AlpacaServer.Logging;

/// <summary>
/// Logging provider that writes into <see cref="LogBuffer"/>.
/// </summary>
/// <remarks>
/// Registered on both logger factories the server builds: the host's own, and
/// the boot factory the devices and the controller connection use. They are
/// separate factories but share the one static buffer, so the page shows the
/// protocol traffic and the web traffic interleaved in real order.
/// </remarks>
public sealed class LogBufferProvider : ILoggerProvider
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new LogBufferLogger(categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>
    /// Level filter for this provider, kept apart from whatever the console does.
    /// </summary>
    /// <remarks>
    /// The driver's own categories follow <see cref="LogBuffer.MinimumLevel"/>, so
    /// turning on debug detail shows which ports were probed and what each
    /// answered. Everything else, meaning the ASP.NET internals, stays at
    /// information or the page would be nothing but request noise.
    /// </remarks>
    public static bool ShouldLog(string? category, LogLevel level)
    {
        LogLevel floor = category is not null
            && (category.StartsWith("OnStepX", StringComparison.Ordinal)
                || category.StartsWith("Alpaca", StringComparison.Ordinal))
            ? LogBuffer.MinimumLevel
            : LogLevel.Information;

        return level >= floor;
    }
}

/// <summary>Logger for one category, writing into the shared buffer.</summary>
internal sealed class LogBufferLogger(string category) : ILogger
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.None)
        {
            return;
        }

        LogBuffer.Add(new LogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = logLevel,
            Category = category,
            Message = formatter(state, exception),
            Exception = exception?.ToString(),
        });
    }
}
