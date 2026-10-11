using System.Diagnostics;
using System.Text.RegularExpressions;
using NcmForge.Core.Pipeline;

namespace NcmForge.Core.Tools;

/// <summary>Outcome of running an external process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardError, string StandardOutput)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>One hardware/system encoder family this ffmpeg build supports.</summary>
/// <param name="Family">The family identifier.</param>
/// <param name="Name">The suffix that identifies it in encoder names (<c>nvenc</c>, <c>qsv</c>, <c>mf</c>, ...).</param>
/// <param name="Encoders">The encoder names found for this family.</param>
/// <param name="HasAudioEncoder">Whether any of them encode audio rather than video.</param>
public sealed record HardwareFamilyInfo(
    Pipeline.HardwareAcceleration Family,
    string Name,
    IReadOnlyList<string> Encoders,
    bool HasAudioEncoder)
{
    public bool IsAvailable => Encoders.Count > 0;
}

/// <summary>
/// Locates and runs ffmpeg.
/// </summary>
/// <remarks>
/// ffmpeg is used for decoding and re-encoding rather than a managed codec library: it covers nearly
/// every format an .ncm can carry, it is fast, and it keeps the application's own dependency surface
/// small enough to publish self-contained per platform.
/// <para>
/// What the binary can actually do is <em>probed</em> rather than assumed. Builds differ — a custom
/// build can carry codecs the official ones lack (AVS3-P3, for instance) and a different set of
/// hardware encoders — so hard-coding either a list of "unsupported" codecs or a list of "available"
/// accelerators would be wrong for somebody.
/// </para>
/// </remarks>
public sealed class FfmpegTool
{
    /// <summary>Decoder names that satisfy a given MP4 coding name.</summary>
    /// <remarks>
    /// Matched as <em>substrings</em> of a decoder name, not as exact names. A patched build is
    /// free to call its decoder something nobody can predict: NetEase's own bundled ffmpeg
    /// registers the Audio Vivid decoder as <c>avs3p3_3d_audio</c>, which an exact-match check
    /// would miss even though the build plainly supports the codec.
    /// </remarks>
    private static readonly Dictionary<string, string[]> DecoderCandidates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["av3a"] = ["avs3p3", "av3a", "avs3a", "avs3-p3", "avs3_p3", "vivid"],
        ["ec-3"] = ["eac3"],
        ["dec3"] = ["eac3"],
        ["ac-3"] = ["ac3"],
        ["dac3"] = ["ac3"],
        ["mp4a"] = ["aac"],
        ["alac"] = ["alac"],
        ["Opus"] = ["opus"],
        ["fLaC"] = ["flac"],
    };

    /// <summary>Both <c>-decoders</c> and <c>-encoders</c> list one name per line in this shape.</summary>
    private static readonly Regex ListEntry = new(@"^\s*[A-Z.]{6}\s+(\S+)", RegexOptions.Compiled);

    private readonly object _probeLock = new();
    private HashSet<string>? _decoders;
    private HashSet<string>? _encoders;
    private HashSet<string>? _hwAccels;

    private FfmpegTool(string ffmpegPath, string? ffprobePath)
    {
        FfmpegPath = ffmpegPath;
        FfprobePath = ffprobePath;
    }

    public string FfmpegPath { get; }

    public string? FfprobePath { get; }

    public bool HasFfprobe => FfprobePath is not null;

    /// <summary>
    /// Finds ffmpeg from an explicit path, the <c>NCMFORGE_FFMPEG</c> environment variable, the
    /// executable path, or a set of well-known install locations. Returns <c>null</c> when absent.
    /// </summary>
    public static FfmpegTool? Locate(string? explicitPath = null)
    {
        string executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string probeExecutable = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

        foreach (string candidate in CandidateDirectories(explicitPath))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string ffmpeg = Path.Combine(candidate, executable);
            if (File.Exists(ffmpeg))
            {
                string ffprobe = Path.Combine(candidate, probeExecutable);
                return new FfmpegTool(ffmpeg, File.Exists(ffprobe) ? ffprobe : null);
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            // Accept either the binary itself or its directory.
            yield return File.Exists(explicitPath) && !Directory.Exists(explicitPath)
                ? Path.GetDirectoryName(Path.GetFullPath(explicitPath))!
                : explicitPath!;
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable("NCMFORGE_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            yield return fromEnvironment!;
        }

        // A build shipped with the application wins over whatever is on PATH: if a distributor
        // bundled one, it is bundled precisely because the system copy cannot do the job.
        string baseDirectory;
        try
        {
            baseDirectory = AppContext.BaseDirectory;
        }
        catch (NotSupportedException)
        {
            baseDirectory = string.Empty;
        }

        if (!string.IsNullOrEmpty(baseDirectory))
        {
            yield return Path.Combine(baseDirectory, "tools", "ffmpeg");
            yield return Path.Combine(baseDirectory, "ffmpeg");
            yield return Path.Combine(baseDirectory, "tools");
            yield return baseDirectory;

            // A distribution that keeps one copy of ffmpeg shared between the GUI and the CLI.
            DirectoryInfo? parent = Directory.GetParent(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is not null)
            {
                yield return Path.Combine(parent.FullName, "tools", "ffmpeg");
            }
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (string part in pathVariable!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return part.Trim();
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (string root in (string[])["C:\\", "D:\\", "E:\\"])
            {
                foreach (string folder in (string[])["ffmpeg\\bin", "Program Files\\ffmpeg\\bin", "Program Files (x86)\\ffmpeg\\bin"])
                {
                    yield return Path.Combine(root, folder);
                }
            }
        }
        else
        {
            yield return "/usr/bin";
            yield return "/usr/local/bin";
            yield return "/opt/homebrew/bin";
        }
    }

    /// <summary>
    /// True when the located ffmpeg sits inside the application's own directory, i.e. it was
    /// shipped with the app rather than installed on the machine.
    /// </summary>
    public bool IsBundled
    {
        get
        {
            try
            {
                string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
                string binary = Path.GetFullPath(FfmpegPath);
                return binary.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or PathTooLongException)
            {
                return false;
            }
        }
    }

    /// <summary>Runs ffmpeg with the supplied arguments, capturing its diagnostics.</summary>
    public Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        => RunProcessAsync(FfmpegPath, arguments, cancellationToken);

    /// <summary>Runs ffprobe and returns its JSON output.</summary>
    public Task<ProcessResult> ProbeAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (FfprobePath is null)
        {
            throw new InvalidOperationException("ffprobe was not found next to ffmpeg.");
        }

        return RunProcessAsync(FfprobePath, arguments, cancellationToken);
    }

    // ---------- capability probes ----------

    /// <summary>
    /// Reports whether this ffmpeg build has a decoder able to handle the given MP4 coding name.
    /// </summary>
    /// <remarks>
    /// When the probe itself fails the answer is optimistic (<c>true</c>): refusing work the binary
    /// might be able to do is worse than letting the transcode attempt fail with ffmpeg's own
    /// message.
    /// </remarks>
    public bool HasDecoderFor(string? codecFourCc)
    {
        if (string.IsNullOrWhiteSpace(codecFourCc))
        {
            return true;
        }

        HashSet<string> decoders = GetDecoders();
        return decoders.Count == 0 || MatchesAnyCandidate(decoders, codecFourCc!);
    }

    /// <summary>
    /// True when any advertised decoder name contains one of the candidates for this coding name.
    /// </summary>
    internal static bool MatchesAnyCandidate(IEnumerable<string> decoders, string codecFourCc)
    {
        if (!DecoderCandidates.TryGetValue(codecFourCc, out string[]? candidates))
        {
            candidates = [codecFourCc];
        }

        foreach (string decoder in decoders)
        {
            foreach (string candidate in candidates)
            {
                if (decoder.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The decoder names this build advertises, or an empty set when it cannot be queried.</summary>
    public IReadOnlyCollection<string> GetDecoderNames() => GetDecoders();

    /// <summary>The encoder names this build advertises.</summary>
    public IReadOnlyCollection<string> GetEncoderNames() => GetEncoders();

    /// <summary>The hardware acceleration methods this build accepted (<c>-hwaccels</c>).</summary>
    public IReadOnlyCollection<string> GetHardwareAccelerators() => GetHwAccels();

    /// <summary>
    /// Reports which hardware/system encoder families this build actually provides.
    /// </summary>
    /// <remarks>
    /// Detection is by encoder-name suffix rather than by asking for a fixed list, so a family this
    /// table does not name still surfaces through <see cref="HardwareFamilyInfo.Encoders"/>.
    /// </remarks>
    public IReadOnlyList<HardwareFamilyInfo> GetHardwareFamilies()
    {
        HashSet<string> encoders = GetEncoders();
        HashSet<string> hwAccels = GetHwAccels();

        return
        [
            Build(HardwareAcceleration.MediaFoundation, "_mf", encoders),
            Build(HardwareAcceleration.Nvenc, "_nvenc", encoders),

            // CUDA has no encoder suffix of its own; it appears as a hardware acceleration method.
            new HardwareFamilyInfo(
                HardwareAcceleration.NvCuda,
                "cuda",
                hwAccels.Contains("cuda") ? ["cuda"] : [],
                HasAudioEncoder: false),

            Build(HardwareAcceleration.Qsv, "_qsv", encoders),
            Build(HardwareAcceleration.Amf, "_amf", encoders),
            Build(HardwareAcceleration.VaApi, "_vaapi", encoders),
            Build(HardwareAcceleration.VideoToolbox, "_videotoolbox", encoders),
            Build(HardwareAcceleration.MediaCodec, "_mediacodec", encoders),
            Build(HardwareAcceleration.V4L2, "_v4l2m2m", encoders),
        ];
    }

    private static HardwareFamilyInfo Build(HardwareAcceleration family, string suffix, HashSet<string> encoders)
    {
        string[] matching = encoders
            .Where(e => e.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // The *_mf family is the only one that wraps audio codecs; the rest are video-only.
        bool hasAudio = matching.Any(e => e is "aac_mf" or "mp3_mf" or "ac3_mf" or "eac3_mf");
        return new HardwareFamilyInfo(family, suffix.TrimStart('_'), matching, hasAudio);
    }

    private HashSet<string> GetDecoders() => Cached(ref _decoders, "-decoders", ListEntry);

    private HashSet<string> GetEncoders() => Cached(ref _encoders, "-encoders", ListEntry);

    private HashSet<string> Cached(ref HashSet<string>? cache, string argument, Regex pattern)
    {
        lock (_probeLock)
        {
            if (cache is not null)
            {
                return cache;
            }

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in RunListCommand(argument))
            {
                Match match = pattern.Match(line);
                if (match.Success)
                {
                    found.Add(match.Groups[1].Value);
                }
            }

            cache = found;
            return found;
        }
    }

    private HashSet<string> GetHwAccels()
    {
        lock (_probeLock)
        {
            if (_hwAccels is not null)
            {
                return _hwAccels;
            }

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // -hwaccels prints bare method names, one per line, after a header line.
            foreach (string line in RunListCommand("-hwaccels"))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.Contains(':') && !trimmed.Contains(' '))
                {
                    found.Add(trimmed);
                }
            }

            _hwAccels = found;
            return found;
        }
    }

    private string[] RunListCommand(string argument)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            // Read before waiting: a full pipe would otherwise deadlock the child.
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);
            return output.Split('\n');
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    // ---------- process running ----------

    private static async Task<ProcessResult> RunProcessAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            return new ProcessResult(-1, $"Failed to start {executable}.", string.Empty);
        }

        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        string error = await errorTask.ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, error, output);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the check and the kill.
        }
        catch (NotSupportedException)
        {
        }
    }
}
