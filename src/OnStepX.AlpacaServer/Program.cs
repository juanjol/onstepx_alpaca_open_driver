using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using ASCOM.Alpaca;
using ASCOM.Alpaca.Razor;
using H.NotifyIcon.Core;
using OnStepX.AlpacaServer;
using OnStepX.AlpacaServer.Logging;
using OnStepX.Core.Config;

CommandLineOptions options = CommandLine.Parse(args);

if (options.ShowHelp)
{
    Console.WriteLine(CommandLine.HelpText);
    return 0;
}

if (options.UnknownArgument is not null)
{
    Console.Error.WriteLine($"Unrecognised argument: {options.UnknownArgument}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.HelpText);
    return 2;
}

// The protocol always uses a full stop as the decimal separator. Without this, on a
// machine with Spanish or German regional settings the driver would format coordinates
// with a comma and the firmware would reject every one of them.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// Tray mode is Windows only (help text says so); elsewhere it just runs as console. The
// check happens once, up front, because it also decides whether console logging can be
// wired up at all below.
bool isWindowsTray = options.Mode == HostMode.Tray && OperatingSystem.IsWindows();

if (isWindowsTray)
{
    // This exe's OutputType is Exe, not WinExe, because the same binary is also the
    // console/service entry point, so Windows still allocates a console window at
    // process start. FreeConsole closes it before anything writes to it, which has to
    // happen before the console logging provider is wired up below.
    NativeMethods.FreeConsole();
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();

if (!isWindowsTray)
{
    builder.Logging.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
}

// Unconditionally, unlike the console above. Tray and service are the two
// modes the installer offers and neither has anywhere to write, so without
// this the installed driver produces no diagnostics at all and a problem can
// only be reproduced by stopping it and rerunning the exe from a terminal.
builder.Logging.AddProvider(new LogBufferProvider());
builder.Logging.AddFilter<LogBufferProvider>(LogBufferProvider.ShouldLog);

if (options.Mode == HostMode.Service)
{
    // A service has no console, so the host has to own the lifetime. Windows and systemd
    // are detected separately because they signal readiness and shutdown differently.
    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddWindowsService(o => o.ServiceName = "OnStepX ASCOM");
    }
    else
    {
        builder.Services.AddSystemd();
    }
}

// Global state is initialised before the host is built, because the official REST layer
// resolves devices through DeviceManager, which is static and has to be populated before
// any request arrives.
using ILoggerFactory bootLoggerFactory = LoggerFactory.Create(b =>
{
    if (!isWindowsTray)
    {
        b.AddSimpleConsole();
    }

    // Same buffer as the host's factory above. The devices and the controller
    // connection log through this one, so leaving it out would mean the log
    // page showed web traffic and none of the protocol traffic that matters.
    b.AddProvider(new LogBufferProvider());
    b.AddFilter<LogBufferProvider>(LogBufferProvider.ShouldLog);
});
ServerRuntime.Initialise(options, bootLoggerFactory);

// The dead "Trace" checkbox on the connection page finally does something: the
// reasons autodiscovery gives up on a port are all logged at debug level, so
// at information level a busy port and an absent board look identical.
LogBuffer.MinimumLevel = ServerRuntime.Settings.TraceEnabled
    ? LogLevel.Debug
    : LogLevel.Information;

ILogger bootLogger = bootLoggerFactory.CreateLogger("OnStepX");

if (ServerRuntime.SettingsWarning is not null)
{
    bootLogger.LogWarning("{Warning}", ServerRuntime.SettingsWarning);
}

if (options.Mode == HostMode.Tray && !OperatingSystem.IsWindows())
{
    bootLogger.LogWarning("Tray mode is Windows only. Running in console mode instead.");
}

if (ServerRuntime.IsSimulated)
{
    bootLogger.LogWarning(
        "SIMULATED transport. There is no real mount behind this. " +
        "This is the mode meant for conformance checking and for testing.");
}

// The official REST layer logs through its own interface, so it gets an adapter. That
// way protocol traces and web traces come out of one sink, interleaved in real order,
// which is what you need when diagnosing a connection problem.
Logging.AttachLogger(new AscomLoggerAdapter(bootLoggerFactory.CreateLogger("Alpaca")));

DeviceManager.LoadConfiguration(new AlpacaConfiguration(() => ServerRuntime.Settings));

// Devices are registered here, before the host starts serving.
DeviceRegistration.RegisterAll(bootLoggerFactory);

// Bind to the configured port unless the command line already said where to listen.
if (!args.Any(a => a.Contains("--urls", StringComparison.OrdinalIgnoreCase)))
{
    ServerSettings server = ServerRuntime.Settings.Server;
    string host = server.AllowRemoteAccess ? "*" : "localhost";

    builder.WebHost.UseUrls($"http://{host}:{server.Port}");
}

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Works out where the controller is before a client asks, because a first
// blind search takes longer than some clients wait for a connect. Pointless
// against the simulator, which has no port to find.
if (!ServerRuntime.IsSimulated)
{
    builder.Services.AddHostedService<PortWatcher>();
}

string xmlPath = Path.Combine(
    AppContext.BaseDirectory,
    $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");

StartupHelpers.ConfigureSwagger(builder.Services, xmlPath);
StartupHelpers.ConfigureAlpacaAPIBehavoir(builder.Services);
StartupHelpers.ConfigureAuthentication(builder.Services);

builder.Services.AddScoped<IUserService, OnStepXUserService>();

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

StartupHelpers.ConfigureSwagger(app);

// Answers Alpaca discovery over UDP, which is how NINA and conformance tools find the
// server without the user typing an address anywhere.
StartupHelpers.ConfigureDiscovery(app);

app.UseStaticFiles();
app.UseRouting();

StartupHelpers.ConfigureAuthentication(app);

app.MapControllers();

// Settings download, so exporting a configuration is a file rather than a block of text to
// select by hand. The password is left out, because an export is meant to be copied about.
app.MapGet("/settings/export", () =>
{
    string json = SettingsStore.Export(ServerRuntime.Settings);

    return Results.File(
        System.Text.Encoding.UTF8.GetBytes(json),
        "application/json",
        "onstepx-settings.json");
});

// Alpaca's standard per device setup URL, which is what a client's "configure"
// button opens: /setup/v1/<devicetype>/<n>/setup. Without these it is a 404 and
// the client looks broken, which is exactly what NINA showed. Mapped explicitly
// rather than left to the Blazor fallback, because something in the routing
// table already claims /setup and answers 404 instead of falling through.
app.MapGet("/setup/v1/{deviceType}/{deviceNumber:int}/setup", (string deviceType) =>
    Results.Redirect(SetupPages.For(deviceType), permanent: false));

// The server's own setup page, the other half of the same convention.
app.MapGet("/setup", () => Results.Redirect("/", permanent: false));

// Log download, so a problem can be sent on as a file rather than selected by
// hand out of a scrolling page.
app.MapGet("/logs/download", () => Results.File(
    System.Text.Encoding.UTF8.GetBytes(LogBuffer.ToText()),
    "text/plain",
    $"onstepx-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Lifetime.ApplicationStopping.Register(() =>
{
    bootLogger.LogInformation("Closing the connection to the controller");
    ServerRuntime.ShutdownAsync().GetAwaiter().GetResult();
});

ILogger startLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("OnStepX");

startLogger.LogInformation(
    "OnStepX ASCOM listening on port {Port}, mode {Mode}{Simulated}",
    ServerRuntime.Settings.Server.Port,
    options.Mode,
    ServerRuntime.IsSimulated ? " (SIMULATED)" : string.Empty);

if (isWindowsTray)
{
    // Always localhost here, regardless of AllowRemoteAccess: the bind host can be "*",
    // which is not something a browser can navigate to.
    string setupUrl = $"http://localhost:{ServerRuntime.Settings.Server.Port}/";

    var trayIcon = new TrayIconWithContextMenu
    {
        ToolTip = "OnStepX ASCOM",
        // Reads the icon back from this same exe's own ApplicationIcon resource rather
        // than shipping the .ico a second time as a loose file. Under "dotnet run" the
        // running process is dotnet.exe, so this falls back to its icon, harmless since
        // installed use is always the published exe.
        Icon = NativeMethods.ExtractIcon(0, Environment.ProcessPath ?? "OnStepX.AlpacaServer.exe", 0),
        ContextMenu = new PopupMenu
        {
            Items =
            {
                new PopupMenuItem(
                    "Open setup page",
                    (_, _) => Process.Start(new ProcessStartInfo(setupUrl) { UseShellExecute = true })),
                new PopupMenuItem("Exit", (_, _) => app.Lifetime.StopApplication()),
            },
        },
    };
    trayIcon.Create();

    // The tray runs its own message loop on a foreground thread, so without disposing it
    // here that thread (and the process) would never exit after "Exit" is clicked.
    app.Lifetime.ApplicationStopping.Register(trayIcon.Dispose);
}

await app.RunAsync();

return 0;

/// <summary>
/// Maps an Alpaca device type onto the setup page that configures it.
/// </summary>
/// <remarks>
/// The names are Alpaca's own, from the URL a client opens, so they are matched
/// as they arrive rather than translated through the driver's device keys.
/// </remarks>
internal static class SetupPages
{
    /// <summary>The page for a device type, or the dashboard if unrecognised.</summary>
    public static string For(string deviceType) =>
        deviceType.ToLowerInvariant() switch
        {
            "telescope" => "/mount",
            "focuser" => "/focuser",
            "rotator" => "/rotator",
            "observingconditions" => "/weather",
            "switch" => "/features",
            _ => "/",
        };
}

/// <summary>
/// The few Win32 calls tray mode needs directly, kept out of H.NotifyIcon because they
/// are about this exe's own window and resources, not the tray icon itself.
/// </summary>
internal static class NativeMethods
{
    /// <summary>
    /// Detaches the process from its console, closing the window if this process was its
    /// only owner. Needed because this exe's OutputType is Exe, shared with console and
    /// service modes, so nothing else hides the console Windows allocates at startup.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FreeConsole();

    /// <summary>
    /// Pulls the first icon out of an executable's own resources, which is how a running
    /// exe can reuse its own &lt;ApplicationIcon&gt; for the tray without shipping a
    /// second copy of the .ico file.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint ExtractIcon(nint hInst, string lpszExeFileName, int nIconIndex);
}
