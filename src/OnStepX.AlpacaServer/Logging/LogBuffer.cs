namespace OnStepX.AlpacaServer.Logging;

/// <summary>One line of log, as the log page shows it.</summary>
public sealed record LogEntry
{
    /// <summary>When it was written, local time.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Severity.</summary>
    public required LogLevel Level { get; init; }

    /// <summary>Logger category, normally the type that wrote it.</summary>
    public required string Category { get; init; }

    /// <summary>The formatted message.</summary>
    public required string Message { get; init; }

    /// <summary>Exception detail, when there was one.</summary>
    public string? Exception { get; init; }

    /// <summary>Category without its namespace, which is what fits on screen.</summary>
    public string ShortCategory
    {
        get
        {
            int dot = Category.LastIndexOf('.');

            return dot >= 0 && dot < Category.Length - 1 ? Category[(dot + 1)..] : Category;
        }
    }

    /// <summary>One line of plain text, for the downloaded copy.</summary>
    public string ToText() =>
        $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level,-11} {Category} {Message}"
        + (Exception is null ? string.Empty : Environment.NewLine + Exception);
}

/// <summary>
/// The last few thousand log lines, kept in memory so the setup UI can show them.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the two modes the installer offers, tray and Windows
/// service, have nowhere to write a console log, so until now they produced no
/// diagnostic output at all. Reproducing a connection problem meant stopping
/// the installed instance and rerunning the executable by hand from a terminal.
/// </para>
/// <para>
/// Static on purpose. The devices and the connection log through the boot
/// logger factory, which is built before the host and its dependency injection
/// exist, so a shared instance is the only thing both sides can reach.
/// </para>
/// </remarks>
public static class LogBuffer
{
    /// <summary>How many lines are kept. Older ones fall off the front.</summary>
    public const int Capacity = 3000;

    private static readonly object Gate = new();
    private static readonly Queue<LogEntry> Entries = new(Capacity);

    /// <summary>
    /// Lowest level kept for the driver's own categories, changeable while running.
    /// </summary>
    /// <remarks>
    /// The interesting detail during a failed connection is at
    /// <see cref="LogLevel.Debug"/>: which ports were tried, and what each one
    /// answered. Raising this at runtime avoids a restart in the middle of
    /// reproducing a problem.
    /// </remarks>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>Raised when a line is added, so the page can refresh.</summary>
    public static event Action? Changed;

    /// <summary>Adds a line, dropping the oldest one if the buffer is full.</summary>
    public static void Add(LogEntry entry)
    {
        lock (Gate)
        {
            if (Entries.Count >= Capacity)
            {
                Entries.Dequeue();
            }

            Entries.Enqueue(entry);
        }

        // Outside the lock: a subscriber that logs would deadlock otherwise.
        Changed?.Invoke();
    }

    /// <summary>A copy of what is held, oldest first.</summary>
    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Gate)
        {
            return [.. Entries];
        }
    }

    /// <summary>Empties the buffer.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }

        Changed?.Invoke();
    }

    /// <summary>Everything held, as plain text.</summary>
    public static string ToText() =>
        string.Join(Environment.NewLine, Snapshot().Select(e => e.ToText()));
}
