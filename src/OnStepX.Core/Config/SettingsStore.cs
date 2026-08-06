using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnStepX.Core.Config;

/// <summary>
/// Persists <see cref="OnStepXSettings"/> to a JSON file.
/// </summary>
/// <remarks>
/// The same format serves as the live configuration and as the export and
/// import format, so an exported configuration is a valid settings file and
/// can simply be copied between installations.
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>Creates the store over a specific path.</summary>
    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
    }

    /// <summary>Path of the settings file.</summary>
    public string Path => _path;

    /// <summary>
    /// Default path depending on the platform.
    /// </summary>
    /// <remarks>
    /// On Windows it goes to <c>ProgramData</c> so that service mode and
    /// tray mode share the same configuration, since they run under
    /// different accounts and a per user directory would leave them out of
    /// sync. On Linux, <c>XDG_CONFIG_HOME</c> is respected.
    /// </remarks>
    public static string DefaultPath()
    {
        string directory;

        if (OperatingSystem.IsWindows())
        {
            directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "OnStepX ASCOM");
        }
        else
        {
            string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config");

            directory = System.IO.Path.Combine(configHome, "onstepx-ascom");
        }

        return System.IO.Path.Combine(directory, "settings.json");
    }

    /// <summary>
    /// Loads the settings. If the file does not exist or is corrupted,
    /// returns the default values instead of throwing.
    /// </summary>
    /// <param name="error">
    /// Description of the problem if it had to fall back to the default
    /// values.
    /// </param>
    public OnStepXSettings Load(out string? error)
    {
        error = null;

        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new OnStepXSettings();
                }

                string json = File.ReadAllText(_path);

                OnStepXSettings? settings =
                    JsonSerializer.Deserialize<OnStepXSettings>(json, JsonOptions);

                if (settings is null)
                {
                    error = "The settings file is empty, using the default values.";
                    return new OnStepXSettings();
                }

                return settings;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // An unreadable configuration must not prevent startup: the
                // user would be left unable to get into the interface to
                // fix it.
                error = $"Could not read {_path}: {ex.Message}. Using the default values.";
                return new OnStepXSettings();
            }
        }
    }

    /// <summary>Loads the settings, discarding the error message.</summary>
    public OnStepXSettings Load() => Load(out _);

    /// <summary>
    /// Saves the settings atomically.
    /// </summary>
    /// <remarks>
    /// Writes to a temporary file and then replaces. Without this, a power
    /// cut or a shutdown midway would leave a truncated JSON, and the user
    /// would lose their entire configuration instead of keeping the
    /// previous one.
    /// </remarks>
    public void Save(OnStepXSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string temporary = _path + ".tmp";

            File.WriteAllText(temporary, json);

            if (File.Exists(_path))
            {
                File.Replace(temporary, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, _path);
            }
        }
    }

    /// <summary>Serializes to JSON, for exporting.</summary>
    /// <param name="settings">Settings to export.</param>
    /// <param name="includePassword">
    /// Include the authentication password. Off by default: an export is meant to be
    /// copied to another machine, attached to a message or committed somewhere, and a
    /// plaintext password should not travel by accident. The importing side is asked to
    /// type it again.
    /// </param>
    public static string Export(OnStepXSettings settings, bool includePassword = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        OnStepXSettings payload = settings;

        if (!includePassword && settings.Server.Password.Length > 0)
        {
            payload = Clone(settings);
            payload.Server.Password = string.Empty;
        }

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// Deep copy of the settings.
    /// </summary>
    /// <remarks>
    /// A record's <c>with</c> expression is a shallow copy, so it would share every nested
    /// section by reference. That matters for editing: the setup pages bind to a copy and
    /// only commit it when the user saves, and with a shallow copy a half typed baud rate
    /// would already be in force for the next connection and cancelling would be
    /// impossible. Going through the serializer guarantees the copy is complete, and it
    /// uses the same options as the file so a value that would not survive a save does not
    /// survive a copy either.
    /// </remarks>
    public static OnStepXSettings Clone(OnStepXSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string json = JsonSerializer.Serialize(settings, JsonOptions);

        return JsonSerializer.Deserialize<OnStepXSettings>(json, JsonOptions)
            ?? new OnStepXSettings();
    }

    /// <summary>
    /// Interprets an exported JSON.
    /// </summary>
    /// <returns>
    /// The settings, or <c>null</c> if the JSON is not valid. Does not
    /// throw, because the source is a file chosen by the user.
    /// </returns>
    public static OnStepXSettings? TryImport(string json, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The content is empty.";
            return null;
        }

        try
        {
            OnStepXSettings? settings =
                JsonSerializer.Deserialize<OnStepXSettings>(json, JsonOptions);

            if (settings is null)
            {
                error = "The JSON does not contain any configuration.";
                return null;
            }

            if (settings.SchemaVersion > new OnStepXSettings().SchemaVersion)
            {
                error = $"The configuration is from a newer version of the driver " +
                    $"(schema {settings.SchemaVersion}). Update before importing it.";
                return null;
            }

            return settings;
        }
        catch (JsonException ex)
        {
            error = $"The JSON is not valid: {ex.Message}";
            return null;
        }
    }
}
