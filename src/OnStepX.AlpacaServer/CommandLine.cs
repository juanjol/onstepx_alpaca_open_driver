using OnStepX.Core.Config;

namespace OnStepX.AlpacaServer;

/// <summary>
/// Command line options for the server.
/// </summary>
public sealed record CommandLineOptions
{
    /// <summary>Requested hosting mode.</summary>
    public HostMode Mode { get; init; } = HostMode.Console;

    /// <summary>
    /// Forces the simulated transport, whatever the saved configuration says.
    /// </summary>
    /// <remarks>
    /// This is the switch that makes the project's verification loop possible: start the
    /// server with no hardware attached and point a conformance checker at it. Without
    /// it, conformance could only ever be checked against a real mount.
    /// </remarks>
    public bool Simulate { get; init; }

    /// <summary>HTTP port, when given on the command line.</summary>
    public int? Port { get; init; }

    /// <summary>Alternative settings file path.</summary>
    public string? SettingsPath { get; init; }

    /// <summary>Show the help text and exit.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Argument that was not recognised, if there was one.</summary>
    public string? UnknownArgument { get; init; }
}

/// <summary>How the process runs.</summary>
public enum HostMode
{
    /// <summary>In the foreground, logging to the console.</summary>
    Console,

    /// <summary>System tray icon. Windows only.</summary>
    Tray,

    /// <summary>Windows service, or a systemd unit on Linux.</summary>
    Service,
}

/// <summary>Parses the command line.</summary>
public static class CommandLine
{
    /// <summary>Help text.</summary>
    public const string HelpText = """
        OnStepX ASCOM Alpaca server

        Usage: OnStepX.AlpacaServer [options]

          --console            Run in the foreground. This is the default.
          --tray               System tray icon. Windows only.
          --service            Run as a Windows service or a systemd unit.
          --simulate           Use a simulated OnStepX controller, with no hardware.
                               Intended for conformance checking and for testing.
          --port <n>           HTTP port. Defaults to 11111, the Alpaca standard.
          --settings <path>    Use an alternative settings file.
          --help               Show this help.
        """;

    /// <summary>Parses the arguments.</summary>
    public static CommandLineOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var mode = HostMode.Console;
        bool simulate = false;
        bool help = false;
        int? port = null;
        string? settingsPath = null;
        string? unknown = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            // Both conventions are accepted, one dash or two or a slash, and case is
            // ignored, because people type all of them.
            string normalised = arg.TrimStart('-', '/').ToLowerInvariant();

            switch (normalised)
            {
                case "console":
                    mode = HostMode.Console;
                    break;

                case "tray":
                    mode = HostMode.Tray;
                    break;

                case "service":
                    mode = HostMode.Service;
                    break;

                case "simulate" or "simulated" or "sim":
                    simulate = true;
                    break;

                case "help" or "h" or "?":
                    help = true;
                    break;

                case "port":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedPort))
                    {
                        port = parsedPort;
                        i++;
                    }
                    else
                    {
                        unknown ??= $"{arg} needs a port number";
                    }

                    break;

                case "settings":
                    if (i + 1 < args.Length)
                    {
                        settingsPath = args[i + 1];
                        i++;
                    }
                    else
                    {
                        unknown ??= $"{arg} needs a path";
                    }

                    break;

                default:
                    // ASP.NET Core takes arguments of its own, such as --urls and
                    // --environment. Those must not be reported as errors.
                    if (!normalised.StartsWith("urls", StringComparison.Ordinal)
                        && !normalised.StartsWith("environment", StringComparison.Ordinal)
                        && !normalised.StartsWith("contentroot", StringComparison.Ordinal)
                        && !arg.StartsWith('-'))
                    {
                        unknown ??= arg;
                    }

                    break;
            }
        }

        return new CommandLineOptions
        {
            Mode = mode,
            Simulate = simulate,
            Port = port,
            SettingsPath = settingsPath,
            ShowHelp = help,
            UnknownArgument = unknown,
        };
    }

    /// <summary>
    /// Applies whatever the command line asked for to the settings model.
    /// </summary>
    /// <remarks>
    /// The command line wins over the saved file, but is deliberately <b>not
    /// persisted</b>: starting once with <c>--simulate</c> must not leave the driver in
    /// simulated mode for good.
    /// </remarks>
    public static OnStepXSettings Apply(OnStepXSettings settings, CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Simulate)
        {
            settings.Connection.Kind = TransportKind.Simulated;
        }

        if (options.Port is int port)
        {
            settings.Server.Port = port;
        }

        return settings;
    }
}
