using System.Runtime.InteropServices;

namespace NcmForge.Core;

/// <summary>
/// Where NcmForge keeps everything it owns.
/// </summary>
/// <remarks>
/// The application is <b>portable by default</b>: it reads and writes only inside its own folder and
/// the directory you point it at, so it can be unzipped anywhere and removed by deleting the folder.
/// <para>
/// The one exception is settings. Those live beside the executable when that folder is writable, and
/// otherwise fall back to the per-user configuration directory — so a copy running from a read-only
/// location (a network share, Program Files) still remembers its options. Dropping a file named
/// <c>NcmForge.portable</c> next to the executable forces the beside-the-exe location even when the
/// folder is writable.
/// </para>
/// </remarks>
public static class AppPaths
{
    /// <summary>Name of the marker file that forces settings to sit beside the executable.</summary>
    public const string PortableMarker = "NcmForge.portable";

    /// <summary>Folder the running executable was loaded from.</summary>
    public static string ApplicationDirectory { get; } = ResolveApplicationDirectory();

    /// <summary>Folder holding the bundled ffmpeg and its licence, if one was shipped.</summary>
    public static string BundledToolsDirectory => Path.Combine(ApplicationDirectory, "tools");

    /// <summary>True when settings are kept beside the executable rather than in the user profile.</summary>
    public static bool IsPortable { get; } = ResolvePortability();

    /// <summary>Folder the settings file lives in.</summary>
    public static string SettingsDirectory { get; } = ResolveSettingsDirectory();

    /// <summary>The settings file itself.</summary>
    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Where conversions go when the caller does not choose a directory.</summary>
    public static string DefaultOutputDirectory { get; } = ResolveDefaultOutputDirectory();

    /// <summary>
    /// Folder the pipeline creates its scratch files in, relative to the output directory.
    /// </summary>
    /// <remarks>
    /// Deliberately next to the output rather than in the system temp folder: the intermediate
    /// payload can be hundreds of megabytes, and keeping it on the same volume means the final move
    /// is cheap and cannot fail for lack of space elsewhere.
    /// </remarks>
    public const string WorkingDirectoryName = ".ncmforge-tmp";

    /// <summary>Human-readable summary of every location, for diagnostics.</summary>
    public static string Describe()
    {
        var lines = new List<string>
        {
            $"  application  : {ApplicationDirectory}",
            $"  mode         : {(IsPortable ? "portable (settings beside the executable)" : "settings in the user profile")}",
            $"  settings     : {SettingsFile}",
            $"  default out  : {DefaultOutputDirectory}",
            $"  scratch      : <output folder>\\{WorkingDirectoryName} (removed after each conversion)",
        };

        if (Directory.Exists(BundledToolsDirectory))
        {
            lines.Add($"  bundled tools: {BundledToolsDirectory}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ResolveApplicationDirectory()
    {
        try
        {
            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (NotSupportedException)
        {
            return Directory.GetCurrentDirectory();
        }
    }

    private static bool ResolvePortability()
    {
        try
        {
            if (File.Exists(Path.Combine(ApplicationDirectory, PortableMarker)))
            {
                return true;
            }

            // No marker: use the application folder only when a file can actually be written there.
            return IsWritable(ApplicationDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsWritable(string directory)
    {
        string probe = Path.Combine(directory, $".ncmforge-write-probe-{Guid.NewGuid():N}");

        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ResolveSettingsDirectory()
    {
        if (IsPortable)
        {
            return ApplicationDirectory;
        }

        if (OperatingSystem.IsWindows())
        {
            string? appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                return Path.Combine(appData, "NcmForge");
            }
        }
        else
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg))
            {
                return Path.Combine(xdg!, "NcmForge");
            }

            string? home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                return Path.Combine(home, ".config", "NcmForge");
            }
        }

        return ApplicationDirectory;
    }

    private static string ResolveDefaultOutputDirectory()
    {
        string folder = "NcmForge";

        try
        {
            string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (!string.IsNullOrEmpty(music))
            {
                return Path.Combine(music, folder);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException)
        {
            // Fall through to the application folder.
        }

        return Path.Combine(ApplicationDirectory, "output");
    }
}
