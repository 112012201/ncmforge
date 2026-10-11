using System.Text.Json;
using System.Text.Json.Serialization;
using NcmForge.Core;

namespace NcmForge.App.Settings;

/// <summary>
/// The options the app remembers between runs.
/// </summary>
/// <remarks>
/// Enums are stored as their names, and an unrecognised name falls back to the default rather than
/// throwing: a settings file written by an older or newer build should never stop the app starting.
/// </remarks>
public sealed record AppSettings
{
    public string? OutputDirectory { get; init; }

    public string? FfmpegPath { get; init; }

    public string OutputFormat { get; init; } = "Mp3";

    public bool Reencode { get; init; }

    public string Hardware { get; init; } = "Disabled";

    public string Surround { get; init; } = "Preserve";

    public string Cover { get; init; } = "OnlineFirst";

    public string Lyrics { get; init; } = "Sidecar";

    public int Bitrate { get; init; } = 320;

    public bool KeepTemporaryFiles { get; init; }
}

/// <summary>Reads and writes <see cref="AppSettings"/> at <see cref="AppPaths.SettingsFile"/>.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Loads the saved settings, or defaults when there are none or the file cannot be read.
    /// </summary>
    /// <remarks>
    /// Deliberately never throws. A corrupt or unreadable settings file must degrade to "fresh
    /// install", not to an app that will not start.
    /// </remarks>
    public static AppSettings Load(Action<string>? log = null)
    {
        string path = AppPaths.SettingsFile;

        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(path);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

            if (settings is null)
            {
                return new AppSettings();
            }

            log?.Invoke($"settings  : loaded from {path}");
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log?.Invoke($"settings  : could not read {path} ({ex.GetType().Name}); using defaults");
            return new AppSettings();
        }
    }

    /// <summary>Saves the settings, reporting rather than throwing when the location is not writable.</summary>
    public static bool Save(AppSettings settings, Action<string>? log = null)
    {
        string path = AppPaths.SettingsFile;

        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.Invoke($"settings  : could not save to {path} ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>Parses a stored enum name, falling back to <paramref name="fallback"/>.</summary>
    public static T Parse<T>(string? value, T fallback)
        where T : struct, Enum
        => Enum.TryParse(value, ignoreCase: true, out T parsed) ? parsed : fallback;
}
