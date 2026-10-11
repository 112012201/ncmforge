using NcmForge.Core.Audio;
using NcmForge.Core.Ncm;
using NcmForge.Core.Tools;

namespace NcmForge.Core.Pipeline;

/// <summary>Whether the source carries a lossless or a lossy encode.</summary>
public enum AudioQualityTier
{
    Lossy,
    Lossless,
}

/// <summary>The encoder, container and channel count a re-encode should target.</summary>
/// <param name="Encoder">The ffmpeg encoder to use first.</param>
/// <param name="FallbackEncoder">The CPU encoder to retry with when the first attempt fails.</param>
/// <param name="CodecLabel">Human-readable codec name for reports.</param>
/// <param name="Extension">Output file extension, including the dot.</param>
/// <param name="TargetChannels">Channel count to encode, or <c>null</c> to keep the source's.</param>
/// <param name="Accelerated">True when <paramref name="Encoder"/> is a hardware/system encoder.</param>
/// <param name="Reason">Why this target was chosen.</param>
public sealed record EncodeChoice(
    string Encoder,
    string FallbackEncoder,
    string CodecLabel,
    string Extension,
    int? TargetChannels,
    bool Accelerated,
    string Reason);

/// <summary>
/// Chooses an encoder that widespread players can actually decode, at the source's quality tier.
/// </summary>
/// <remarks>
/// The point of a re-encode here is compatibility, not compression. A lossless source is re-encoded
/// losslessly and a lossy one stays lossy, so "re-encode" never silently discards quality that was
/// paid for. Targets are picked for playback reach: FLAC for lossless (VLC, PotPlayer, Windows 10+,
/// macOS, Linux) and AAC-LC for lossy (universally decodable, including Windows Media Player).
/// <para>
/// Hardware acceleration is offered as a separate axis. Exactly one family — Windows Media
/// Foundation — ships <em>audio</em> encoders (<c>aac_mf</c>, <c>mp3_mf</c>, <c>ac3_mf</c>); every
/// other family ffmpeg exposes (NVENC, QSV, AMF, VA-API, VideoToolbox, MediaCodec, V4L2) is
/// video-only. Rather than pretend otherwise, an accelerated encoder is used only where one exists
/// for the chosen codec, and every accelerated attempt keeps a CPU encoder to fall back to.
/// </remarks>
public static class EncoderSelector
{
    /// <summary>Media Foundation encoder names, keyed by the codec they encode.</summary>
    private static readonly Dictionary<string, string> MediaFoundationByCodec = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aac"] = "aac_mf",
        ["mp3"] = "mp3_mf",
        ["ac3"] = "ac3_mf",
        ["eac3"] = "eac3_mf",
    };

    /// <summary>Determines whether a payload is lossless or lossy.</summary>
    public static AudioQualityTier TierOf(NcmFile ncm)
    {
        string? fourCc = ncm.Mp4Track?.CodecFourCc;

        return ncm.Format switch
        {
            AudioFormat.Flac or AudioFormat.Wav => AudioQualityTier.Lossless,

            // PCM and ALAC in an MP4 are lossless; everything else the container holds is not.
            AudioFormat.Mp4 when fourCc is "alac" or "lpcm" or "twos" or "sowt" or "in24" or "in32" => AudioQualityTier.Lossless,

            _ => AudioQualityTier.Lossy,
        };
    }

    /// <summary>
    /// Picks the encoder for a re-encode.
    /// </summary>
    /// <param name="requested">What the caller asked for; <see cref="OutputFormat.Compatible"/> delegates the choice here.</param>
    /// <param name="tier">The source's quality tier.</param>
    /// <param name="sourceChannels">Source channel count, or 0 when unknown.</param>
    /// <param name="hardware">Which encoder family to prefer.</param>
    /// <param name="ffmpeg">The tool to probe for accelerator support; may be <c>null</c>.</param>
    /// <param name="note">An explanation of anything the caller should know, e.g. why an accelerator was not used.</param>
    public static EncodeChoice Select(
        OutputFormat requested,
        AudioQualityTier tier,
        int sourceChannels,
        HardwareAcceleration hardware,
        FfmpegTool? ffmpeg,
        out string? note)
    {
        note = null;

        // A local with an explicit tuple type: a switch expression cannot infer a common type
        // across arms that carry a bare `null` for the channel count.
        (string Codec, string Fallback, string Label, string Extension, int? Channels, string Reason) target;

        if (requested == OutputFormat.Mp3)
        {
            target = ("mp3", "libmp3lame", "MP3", ".mp3", 2,
                "MP3 请求：立体声，兼容所有播放器");
        }
        else if (requested == OutputFormat.Flac)
        {
            target = ("flac", "flac", "FLAC", ".flac", sourceChannels > 8 ? 8 : null,
                sourceChannels > 8 ? "FLAC 上限 8 声道，超出部分降混" : "FLAC 请求：无损保留");
        }
        else if (tier == AudioQualityTier.Lossless)
        {
            target = sourceChannels > 8
                ? ("aac", "aac", "AAC-LC 5.1", ".m4a", 6,
                    "无损源但超过 8 声道，FLAC 放不下，改用通用性最好的多声道 AAC")
                : ("flac", "flac", "FLAC", ".flac", null,
                    "无损源 → 无损目标，FLAC 是兼容性最好的无损格式");
        }
        else
        {
            target = sourceChannels > 2
                ? ("aac", "aac", "AAC-LC 5.1", ".m4a", 6,
                    "有损多声道源 → AAC-LC 5.1，Windows/移动端/播放器通用")
                : ("aac", "aac", "AAC-LC", ".m4a", 2,
                    "有损源 → AAC-LC，兼容性优于 MP3 且同码率质量更好");
        }

        string encoder = target.Fallback;
        bool accelerated = false;

        if (hardware != HardwareAcceleration.Disabled && ffmpeg is not null)
        {
            string? found = FindAcceleratedEncoder(target.Codec, hardware, ffmpeg, out string? acceleratorNote);

            if (found is not null)
            {
                encoder = found;
                accelerated = true;
            }
            else
            {
                note = acceleratorNote ?? BuildNoAcceleratorNote(hardware, target.Codec);
            }
        }

        return new EncodeChoice(
            encoder,
            target.Fallback,
            target.Label + (accelerated ? "（硬件/系统编码）" : string.Empty),
            target.Extension,
            target.Channels,
            accelerated,
            target.Reason);
    }

    /// <summary>Finds an accelerated encoder for a codec, or explains why there is none.</summary>
    private static string? FindAcceleratedEncoder(
        string codec,
        HardwareAcceleration hardware,
        FfmpegTool ffmpeg,
        out string? note)
    {
        note = null;
        IReadOnlyList<HardwareFamilyInfo> families = ffmpeg.GetHardwareFamilies();

        HardwareFamilyInfo? selected = families.FirstOrDefault(f => f.Family == hardware);

        if (hardware == HardwareAcceleration.Auto)
        {
            // Only audio-capable families can accelerate an audio encode; in practice that is
            // Media Foundation, which is why the search is restricted rather than "first available".
            HardwareFamilyInfo? audio = families.FirstOrDefault(f => f.IsAvailable && f.HasAudioEncoder);
            if (audio is null)
            {
                note = "自动：本机 ffmpeg 没有音频硬件编码器（NVENC/QSV/AMF/VA-API 等均为视频专用），改用 CPU 编码";
                return null;
            }

            selected = audio;
        }
        else if (selected is null || !selected.IsAvailable)
        {
            note = $"所选硬件加速（{hardware}）在本机 ffmpeg 中不可用，已回退 CPU 编码";
            return null;
        }

        if (selected is null)
        {
            return null;
        }

        if (!selected.HasAudioEncoder)
        {
            note = $"{selected.Name} 只有视频编码器（{string.Join(", ", selected.Encoders.Take(3))}…），音频仍需 CPU 编码";
            return null;
        }

        if (!MediaFoundationByCodec.TryGetValue(codec, out string? encoderName))
        {
            note = $"{selected.Name} 没有 {codec} 编码器，改用 CPU";
            return null;
        }

        if (!selected.Encoders.Contains(encoderName, StringComparer.OrdinalIgnoreCase))
        {
            note = $"{selected.Name} 未提供 {encoderName}，改用 CPU";
            return null;
        }

        return encoderName;
    }

    private static string BuildNoAcceleratorNote(HardwareAcceleration hardware, string codec)
        => $"{hardware} 没有可用的 {codec} 编码器，改用 CPU 编码";
}
