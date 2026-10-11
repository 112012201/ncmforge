using System.Text;
using NcmForge.Core;
using NcmForge.Core.Tools;

namespace NcmForge.Cli;

/// <summary>
/// Reports what the local toolchain can actually do, so a surprising conversion result can be
/// diagnosed without guesswork.
/// </summary>
internal static class DoctorCommand
{
    /// <summary>Decoders worth checking, with the codec each one covers.</summary>
    private static readonly (string Decoder, string Covers)[] Interesting =
    [
        ("mp3", "MP3 payloads"),
        ("flac", "FLAC payloads"),
        ("aac", "AAC payloads"),
        ("ac3", "Dolby Digital (AC-3)"),
        ("eac3", "Dolby Digital Plus (E-AC-3)"),
        ("avs3p3", "AVS3-P3 / Audio Vivid (name used by NetEase's own build)"),
        ("av3a", "AVS3-P3 / Audio Vivid"),
    ];

    public static int Run(string[] args)
    {
        string? explicitPath = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--ffmpeg")
            {
                explicitPath = args[i + 1];
            }
        }

        var text = new StringBuilder();

        FfmpegTool? ffmpeg = FfmpegTool.Locate(explicitPath);
        text.AppendLine("=== ffmpeg ===");
        if (ffmpeg is null)
        {
            text.AppendLine("  not found");
            text.AppendLine("  set NCMFORGE_FFMPEG, pass --ffmpeg <dir>, or put it on PATH");
        }
        else
        {
            text.AppendLine($"  ffmpeg  : {ffmpeg.FfmpegPath}");
            text.AppendLine($"  ffprobe : {ffmpeg.FfprobePath ?? "(not found, not required)"}");

            IReadOnlyCollection<string> decoders = ffmpeg.GetDecoderNames();
            text.AppendLine($"  decoders advertised : {decoders.Count}");

            text.AppendLine();
            text.AppendLine("=== decoder coverage ===");
            foreach ((string decoder, string covers) in Interesting)
            {
                // Substring match, to agree with the decision the pipeline actually makes: a build
                // may call its decoder something unpredictable (NetEase ships "avs3p3_3d_audio",
                // this one ships "libarcdav3a"), and reporting NO for a codec the tool will happily
                // decode would be worse than useless.
                bool present = decoders.Any(d => d.Contains(decoder, StringComparison.OrdinalIgnoreCase));
                text.AppendLine($"  {(present ? "yes" : " NO")}  {decoder,-14} {covers}");
            }

            bool anyVivid = decoders.Any(d =>
                d.Contains("av3a", StringComparison.OrdinalIgnoreCase)
                || d.Contains("avs3a", StringComparison.OrdinalIgnoreCase)
                || d.Contains("vivid", StringComparison.OrdinalIgnoreCase));

            text.AppendLine();
            if (anyVivid)
            {
                text.AppendLine("  AVS3-P3: this build can decode immersive Audio Vivid tracks directly.");
            }
            else
            {
                text.AppendLine("  AVS3-P3: this build CANNOT decode Audio Vivid tracks.");
                text.AppendLine("           Immersive .ncm files will be copied losslessly instead of converted.");
                text.AppendLine("           To convert them, either point --ffmpeg at a build that has the decoder,");
                text.AppendLine("           or supply an external decoder with --decoder.");
            }
        }

        text.AppendLine();
        text.AppendLine("=== locations ===");
        text.AppendLine(AppPaths.Describe());

        text.AppendLine();
        text.AppendLine("=== notes ===");
        text.AppendLine("  Decoder names are read from ffmpeg's own -decoders output, so a custom build is");
        text.AppendLine("  detected correctly even when its decoder is named differently than expected.");

        if (ffmpeg is not null)
        {
            AppendHardwareReport(text, ffmpeg);
        }

        Console.WriteLine(text.ToString());
        return ffmpeg is null ? 1 : 0;
    }

    /// <summary>Lists the encoder families this build offers and whether any of them encode audio.</summary>
    private static void AppendHardwareReport(StringBuilder text, FfmpegTool ffmpeg)
    {
        IReadOnlyList<HardwareFamilyInfo> families = ffmpeg.GetHardwareFamilies();
        List<HardwareFamilyInfo> available = families.Where(f => f.IsAvailable).ToList();

        text.AppendLine();
        text.AppendLine("=== hardware encoders ===");

        if (available.Count == 0)
        {
            text.AppendLine("  none detected — the re-encode will use CPU encoders");
        }
        else
        {
            foreach (HardwareFamilyInfo family in available)
            {
                string audio = family.HasAudioEncoder ? "audio + video" : "video only";
                string sample = string.Join(", ", family.Encoders.Take(4));
                if (family.Encoders.Count > 4)
                {
                    sample += ", …";
                }

                text.AppendLine($"  {family.Name,-14} {audio,-14} {sample}");
            }
        }

        text.AppendLine();
        text.AppendLine("=== hardware audio encoding ===");
        if (available.Any(f => f.HasAudioEncoder))
        {
            text.AppendLine("  available: Media Foundation aac_mf / mp3_mf / ac3_mf");
            text.AppendLine("  use --reencode --hw mf (or --hw auto) to prefer them; a CPU encoder is retried if one fails");
        }
        else
        {
            text.AppendLine("  NOT available: this build has no hardware audio encoder.");
            text.AppendLine("  NVENC / QSV / AMF / VA-API / VideoToolbox / MediaCodec / V4L2 are video-only,");
            text.AppendLine("  so audio re-encoding always runs on the CPU.");
        }

        IReadOnlyCollection<string> hwAccels = ffmpeg.GetHardwareAccelerators();
        if (hwAccels.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"  -hwaccels: {string.Join(", ", hwAccels.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))}");
        }
    }
}
