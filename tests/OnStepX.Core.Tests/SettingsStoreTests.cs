using OnStepX.Core.Config;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// The settings file doubles as the export format, so a config exported from one
/// machine is a valid settings file on another. These tests pin that property down
/// along with the failure modes that would otherwise lose a user's configuration.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public SettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "onstepx-settings-" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void MissingFileYieldsDefaultsWithoutAnError()
    {
        var store = new SettingsStore(_path);

        OnStepXSettings settings = store.Load(out string? error);

        Assert.Null(error);
        Assert.Equal(TransportKind.Serial, settings.Connection.Kind);
        Assert.Equal(9600, settings.Connection.BaudRate);
        Assert.Equal(11111, settings.Server.Port);
    }

    [Fact]
    public void SaveThenLoadRoundTripsEveryDeviceSection()
    {
        var store = new SettingsStore(_path);

        var original = new OnStepXSettings
        {
            Connection = new ConnectionSettings
            {
                Kind = TransportKind.Tcp,
                Host = "10.0.0.5",
                TcpPort = 9998,
                PortName = "COM7",
                BaudRate = 115200,
                TimeoutMilliseconds = 2500,
                UseErrorCorrection = false,
                AutoDiscoverPort = false,
            },
            Telescope = new TelescopeSettings
            {
                SetDateTimeOnConnect = true,
                ApertureDiameter = 0.2,
                FocalLength = 1.0,
            },
            Focuser = new FocuserSettings
            {
                FocuserNumber = 3,
                MoveToPositionOnConnect = true,
                PositionOnConnect = 12500,
            },
            Rotator = new RotatorSettings
            {
                MoveToPositionOnConnect = true,
                PositionOnConnect = -45.5,
                Reverse = true,
            },
            TraceEnabled = true,
        };

        store.Save(original);
        OnStepXSettings loaded = store.Load(out string? error);

        Assert.Null(error);
        Assert.Equal(TransportKind.Tcp, loaded.Connection.Kind);
        Assert.Equal("10.0.0.5", loaded.Connection.Host);
        Assert.Equal(9998, loaded.Connection.TcpPort);
        Assert.Equal("COM7", loaded.Connection.PortName);
        Assert.Equal(115200, loaded.Connection.BaudRate);
        Assert.False(loaded.Connection.UseErrorCorrection);
        Assert.True(loaded.Telescope.SetDateTimeOnConnect);
        Assert.Equal(0.2, loaded.Telescope.ApertureDiameter);
        Assert.Equal(3, loaded.Focuser.FocuserNumber);
        Assert.Equal(12500, loaded.Focuser.PositionOnConnect);
        Assert.Equal(-45.5, loaded.Rotator.PositionOnConnect);
        Assert.True(loaded.Rotator.Reverse);
        Assert.True(loaded.TraceEnabled);
    }

    [Fact]
    public void EnumsArePersistedByNameNotByNumber()
    {
        // Storing the ordinal would silently repoint every saved config the day a
        // value is inserted into the enum.
        var store = new SettingsStore(_path);
        store.Save(new OnStepXSettings
        {
            Connection = new ConnectionSettings { Kind = TransportKind.Simulated },
        });

        string json = File.ReadAllText(_path);

        Assert.Contains("Simulated", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaultsInsteadOfThrowing()
    {
        // An unreadable config must not stop the server: the user would be locked
        // out of the very UI they need to fix it.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, "{ this is not json");

        var store = new SettingsStore(_path);
        OnStepXSettings settings = store.Load(out string? error);

        Assert.NotNull(error);
        Assert.Contains("Could not read", error, StringComparison.Ordinal);
        Assert.Equal(11111, settings.Server.Port);
    }

    [Fact]
    public void SaveCreatesTheDirectoryWhenItDoesNotExist()
    {
        var store = new SettingsStore(Path.Combine(_directory, "nested", "deeper", "settings.json"));

        store.Save(new OnStepXSettings());

        Assert.True(File.Exists(store.Path));
    }

    [Fact]
    public void SavingOverAnExistingFileKeepsItValid()
    {
        // The write goes to a temporary file and then replaces the original, so a
        // crash midway leaves the previous config intact rather than a truncated
        // file and a user who has lost all their settings.
        var store = new SettingsStore(_path);

        store.Save(new OnStepXSettings { Server = new ServerSettings { Port = 12345 } });
        store.Save(new OnStepXSettings { Server = new ServerSettings { Port = 23456 } });

        Assert.Equal(23456, store.Load().Server.Port);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void ExportedJsonImportsBackIdentically()
    {
        var original = new OnStepXSettings
        {
            Connection = new ConnectionSettings { PortName = "COM12", BaudRate = 19200 },
            Focuser = new FocuserSettings { FocuserNumber = 5 },
        };

        string json = SettingsStore.Export(original);
        OnStepXSettings? imported = SettingsStore.TryImport(json, out string? error);

        Assert.Null(error);
        Assert.NotNull(imported);
        Assert.Equal("COM12", imported.Connection.PortName);
        Assert.Equal(19200, imported.Connection.BaudRate);
        Assert.Equal(5, imported.Focuser.FocuserNumber);
    }

    [Fact]
    public void AnExportedFileIsAlsoAValidSettingsFile()
    {
        // This is what makes a config portable between installations: no separate
        // export format to keep in sync.
        var store = new SettingsStore(_path);
        string exported = SettingsStore.Export(new OnStepXSettings
        {
            Connection = new ConnectionSettings { PortName = "COM9" },
        });

        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, exported);

        Assert.Equal("COM9", store.Load(out string? error).Connection.PortName);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"connection\": ")]
    public void InvalidImportIsRejectedWithAMessageRatherThanAnException(string json)
    {
        OnStepXSettings? imported = SettingsStore.TryImport(json, out string? error);

        Assert.Null(imported);
        Assert.NotNull(error);
    }

    [Fact]
    public void ConfigFromANewerSchemaIsRefusedWithAClearReason()
    {
        // Importing a newer schema would silently drop settings this build does not
        // know about, so it is better to refuse and say why.
        string json = SettingsStore.Export(new OnStepXSettings { SchemaVersion = 99 });

        OnStepXSettings? imported = SettingsStore.TryImport(json, out string? error);

        Assert.Null(imported);
        Assert.NotNull(error);
        Assert.Contains("99", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPropertiesInAnOlderExportAreIgnored()
    {
        // Forward compatibility in the other direction: a config written by a newer
        // build with the same schema version must still load.
        string json = """
            {
              "schemaVersion": 1,
              "connection": { "portName": "COM4", "somethingNew": 42 },
              "brandNewSection": { "x": 1 }
            }
            """;

        OnStepXSettings? imported = SettingsStore.TryImport(json, out string? error);

        Assert.Null(error);
        Assert.NotNull(imported);
        Assert.Equal("COM4", imported.Connection.PortName);
    }

    [Fact]
    public void DefaultPathIsPlatformAppropriate()
    {
        string path = SettingsStore.DefaultPath();

        Assert.EndsWith("settings.json", path, StringComparison.Ordinal);

        if (OperatingSystem.IsWindows())
        {
            // ProgramData, so the tray and the service share one configuration
            // despite running under different accounts.
            Assert.Contains("OnStepX ASCOM", path, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("onstepx-ascom", path, StringComparison.Ordinal);
        }
    }
}
