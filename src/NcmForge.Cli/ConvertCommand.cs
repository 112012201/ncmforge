using System.Text;
using NcmForge.Core.Pipeline;

namespace NcmForge.Cli;

/// <summary>Batch conversion front end: the same pipeline the desktop UI drives.</summary>
internal static class ConvertCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        var options = new ConversionOptions();
        var inputs = new List<string>();
        bool recurse = false;

        for (var i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "--format":
                {
                    string format = (Next() ?? string.Empty).ToLowerInvariant();

                    // Accepts both the high-level modes and any codec from the catalogue, so
                    // "--format wavpack" names that encoder exactly instead of walking the ladder.
                    if (CodecLadder.IsCodecKey(format))
                    {
                        options.TargetCodec = CodecLadder.Find(format)!.Key;
                        options.Format = OutputFormat.Compatible;

                        // Naming a codec is itself the request to re-encode.
                        options.ForceReencode = true;
                        break;
                    }

                    options.TargetCodec = null;
                    options.Format = format switch
                    {
                        "mp3" => OutputFormat.Mp3,
                        "flac" => OutputFormat.Flac,
                        "auto" or "compatible" => OutputFormat.Compatible,
                        "copy" or "lossless" => OutputFormat.LosslessCopy,
                        var other => throw new ArgumentException(
                            $"unknown format '{other}'; use mp3, flac, auto, copy, or one of: {string.Join(", ", CodecLadder.Rungs.Select(r => r.Key))}"),
                    };
                    break;
                }

                case "--surround":
                    options.Surround = (Next() ?? string.Empty).ToLowerInvariant() switch
                    {
                        "preserve" or "keep" => SurroundMapping.Preserve,
                        "stereo" or "2" => SurroundMapping.DownmixToStereo,
                        "51" or "5.1" => SurroundMapping.DownmixTo51,
                        "71" or "7.1" => SurroundMapping.DownmixTo71,
                        var other => throw new ArgumentException($"unknown surround mode '{other}'"),
                    };
                    break;

                case "--cover":
                    options.Cover = (Next() ?? string.Empty).ToLowerInvariant() switch
                    {
                        "none" => CoverMode.None,
                        "embedded" or "local" => CoverMode.Embedded,
                        "online" => CoverMode.Online,
                        "online+embedded" or "auto" => CoverMode.OnlineThenEmbedded,
                        var other => throw new ArgumentException($"unknown cover mode '{other}'"),
                    };
                    break;

                case "--lyrics":
                    options.Lyrics = (Next() ?? string.Empty).ToLowerInvariant() switch
                    {
                        "none" or "off" => LyricsMode.None,
                        "sidecar" or "lrc" or "auto" => LyricsMode.Sidecar,
                        var other => throw new ArgumentException($"unknown lyrics mode '{other}'"),
                    };
                    break;

                case "--bitrate":
                    options.BitrateKbps = int.Parse(Next() ?? "320", System.Globalization.CultureInfo.InvariantCulture);
                    break;

                case "--reencode":
                    options.ForceReencode = true;
                    break;

                case "--hw":
                    options.Hardware = (Next() ?? string.Empty).ToLowerInvariant() switch
                    {
                        "auto" => HardwareAcceleration.Auto,
                        "off" or "none" or "cpu" => HardwareAcceleration.Disabled,
                        "mf" or "mediafoundation" => HardwareAcceleration.MediaFoundation,
                        "nvenc" or "nvidia" => HardwareAcceleration.Nvenc,
                        "nvcuda" or "cuda" => HardwareAcceleration.NvCuda,
                        "qsv" or "intel" => HardwareAcceleration.Qsv,
                        "amf" or "vce" or "uvd" or "amd" => HardwareAcceleration.Amf,
                        "vaapi" => HardwareAcceleration.VaApi,
                        "videotoolbox" or "vt" => HardwareAcceleration.VideoToolbox,
                        "mediacodec" => HardwareAcceleration.MediaCodec,
                        "v4l2" or "vpu" or "adreno" => HardwareAcceleration.V4L2,
                        var other => throw new ArgumentException($"unknown hardware acceleration '{other}'"),
                    };
                    break;

                case "--out":
                    options.OutputDirectory = Next();
                    break;

                case "--ffmpeg":
                    options.FfmpegPath = Next();
                    break;

                case "--assume-ffmpeg-decodes":
                    options.ForceFfmpegDecode = true;
                    break;

                case "--decoder":
                    options.ExternalDecoderCommand = Next();
                    break;

                case "--decoder-input":
                    options.ExternalDecoderInput = (Next() ?? string.Empty).ToLowerInvariant() switch
                    {
                        "payload" => ExternalDecoderInput.Payload,
                        _ => ExternalDecoderInput.ElementaryStream,
                    };
                    break;

                case "--extract-es":
                    options.ExtractElementaryStream = true;
                    break;

                case "--keep-temp":
                    options.KeepTemporaryFiles = true;
                    break;

                case "--no-overwrite":
                    options.Overwrite = false;
                    break;

                case "--recursive":
                    recurse = true;
                    break;

                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"unknown option '{arg}'");
                        Usage();
                        return 2;
                    }

                    inputs.Add(arg);
                    break;
            }
        }

        if (inputs.Count == 0)
        {
            Usage();
            return 2;
        }

        List<string> files = ExpandInputs(inputs, recurse);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("no .ncm files found");
            return 1;
        }

        using var http = CreateHttpClient();
        var pipeline = new ConversionPipeline(http);

        int failures = 0;
        foreach (string file in files)
        {
            Console.WriteLine($"=== {Path.GetFileName(file)} ===");
            try
            {
                ConversionResult result = await pipeline.ConvertAsync(
                    file, options, log: Console.WriteLine, progress: null, CancellationToken.None);

                Console.WriteLine($"  -> {result.OutputPath}");
                Console.WriteLine($"     codec={result.Codec} channels={result.SourceChannels} route={result.Plan.Action} size={result.OutputBytes:N0}");
                if (result.Cover is { } cover)
                {
                    Console.WriteLine($"     cover={cover.Source} {cover.Format} {cover.Length:N0} bytes");
                }
            }
            catch (ConversionException ex)
            {
                Console.Error.WriteLine($"  FAILED: {ex.Message}");
                failures++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{files.Count - failures}/{files.Count} converted");
        return failures == 0 ? 0 : 1;
    }

    internal static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        // Identifies the tool to the public APIs and satisfies MusicBrainz's user-agent policy.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NcmForge/1.0 (+https://github.com/ncmforge)");
        http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return http;
    }

    private static List<string> ExpandInputs(List<string> inputs, bool recurse)
    {
        var files = new List<string>();

        foreach (string input in inputs)
        {
            if (File.Exists(input))
            {
                files.Add(Path.GetFullPath(input));
                continue;
            }

            if (Directory.Exists(input))
            {
                var search = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                files.AddRange(Directory.EnumerateFiles(input, "*.ncm", search));
                continue;
            }

            Console.Error.WriteLine($"skipping '{input}': not a file or directory");
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Usage()
    {
        var text = new StringBuilder();
        text.AppendLine("usage: ncmforge convert <file.ncm | directory> [options]");
        text.AppendLine();
        text.AppendLine("  --format <name>     output format (default mp3). Codes:");
        text.AppendLine("                        auto | compatible   walk the fallback ladder and pick for you");
        text.AppendLine("                        copy | lossless     keep the original audio bit-exact");
        foreach (EncodeTarget rung in CodecLadder.Rungs)
        {
            text.AppendLine($"                        {rung.Key,-14} {CodecLadder.Describe(rung)}");
        }
        text.AppendLine("  --reencode                   re-encode even when the payload could be remuxed");
        text.AppendLine("  --hw <auto|off|mf|nvenc|nvcuda|qsv|amf|vaapi|videotoolbox|mediacodec|v4l2>");
        text.AppendLine("                               encoder family to prefer (only used with --reencode)");
        text.AppendLine("  --surround <preserve|stereo|51|71>");
        text.AppendLine("                               channel handling (default preserve)");
        text.AppendLine("  --cover <none|embedded|online|online+embedded>");
        text.AppendLine("  --lyrics <sidecar|none>      embed the sibling .lrc file (default sidecar)");
        text.AppendLine("  --bitrate <kbps>             MP3 bitrate (default 320)");
        text.AppendLine("  --out <directory>            output directory (default: alongside source)");
        text.AppendLine("  --ffmpeg <path>              explicit ffmpeg location");
        text.AppendLine("  --assume-ffmpeg-decodes      transcode even if the codec probe says the build cannot");
        text.AppendLine("  --decoder \"<command>\"        external decoder; {input} {output} {channels} {sampleRate}");
        text.AppendLine("  --decoder-input <es|payload> what the external decoder receives");
        text.AppendLine("  --extract-es                 write the raw elementary stream instead of the container");
        text.AppendLine("  --recursive                  descend into subdirectories");
        text.AppendLine("  --no-overwrite               skip files that already exist");
        Console.Error.WriteLine(text.ToString());
    }
}
