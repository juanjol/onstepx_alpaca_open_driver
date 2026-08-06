using System.Globalization;
using System.Reflection;
using ASCOM.Alpaca;
using ASCOM.Alpaca.Razor;
using OnStepX.AlpacaServer;
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

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

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
using ILoggerFactory bootLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
ServerRuntime.Initialise(options, bootLoggerFactory);

ILogger bootLogger = bootLoggerFactory.CreateLogger("OnStepX");

if (ServerRuntime.SettingsWarning is not null)
{
    bootLogger.LogWarning("{Warning}", ServerRuntime.SettingsWarning);
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

await app.RunAsync();

return 0;
