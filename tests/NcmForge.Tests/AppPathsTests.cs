using NcmForge.Core;
using Xunit;

namespace NcmForge.Tests;

/// <summary>Coverage for the locations the application defines for itself.</summary>
public sealed class AppPathsTests
{
    [Fact]
    public void TheApplicationDirectoryIsAResolvedAbsolutePath()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppPaths.ApplicationDirectory));
        Assert.True(Path.IsPathRooted(AppPaths.ApplicationDirectory));
        Assert.Equal(AppPaths.ApplicationDirectory, Path.GetFullPath(AppPaths.ApplicationDirectory));
    }

    [Fact]
    public void SettingsLiveInAFileCalledSettingsJson()
    {
        Assert.True(Path.IsPathRooted(AppPaths.SettingsFile));
        Assert.Equal("settings.json", Path.GetFileName(AppPaths.SettingsFile));
        Assert.Equal(AppPaths.SettingsDirectory, Path.GetDirectoryName(AppPaths.SettingsFile));
    }

    [Fact]
    public void PortableModeKeepsSettingsBesideTheExecutable()
    {
        // The marker is the documented way to force portability, so when it is present the two
        // locations must coincide. On this test host the folder is writable, which also selects
        // portable mode — either way the invariant holds.
        if (AppPaths.IsPortable)
        {
            Assert.Equal(AppPaths.ApplicationDirectory, AppPaths.SettingsDirectory);
        }
        else
        {
            Assert.NotEqual(AppPaths.ApplicationDirectory, AppPaths.SettingsDirectory);
        }
    }

    [Fact]
    public void TheScratchFolderIsASinglePathSegment()
    {
        // It is appended to an output directory, so a separator here would escape it.
        Assert.DoesNotContain(Path.DirectorySeparatorChar, AppPaths.WorkingDirectoryName);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, AppPaths.WorkingDirectoryName);
        Assert.StartsWith(".", AppPaths.WorkingDirectoryName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultOutputDirectoryIsAbsolute()
    {
        Assert.True(Path.IsPathRooted(AppPaths.DefaultOutputDirectory));
        Assert.Equal(AppPaths.DefaultOutputDirectory, Path.GetFullPath(AppPaths.DefaultOutputDirectory));
    }

    [Fact]
    public void TheReportNamesEveryLocationTheUserMightNeedToFind()
    {
        string report = AppPaths.Describe();

        Assert.Contains(AppPaths.ApplicationDirectory, report, StringComparison.Ordinal);
        Assert.Contains(AppPaths.SettingsFile, report, StringComparison.Ordinal);
        Assert.Contains(AppPaths.DefaultOutputDirectory, report, StringComparison.Ordinal);
        Assert.Contains(AppPaths.WorkingDirectoryName, report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBundledToolsDirectorySitsInsideTheApplicationDirectory()
    {
        // This is the folder FfmpegTool probes, so the two must agree.
        Assert.Equal(Path.Combine(AppPaths.ApplicationDirectory, "tools"), AppPaths.BundledToolsDirectory);
    }
}
