using System.Globalization;
using NcmForge.Core.Audio;
using NcmForge.Core.Ncm;
using NcmForge.Core.Tools;

namespace NcmForge.Core.Pipeline;

/// <summary>
/// One codec NcmForge can encode to, and everything a chooser needs to know about it.
/// </summary>
/// <param name="Key">Stable identifier used by the CLI and the settings file.</param>
/// <param name="Label">Human-readable format name.</param>
/// <param name="Encoder">The ffmpeg encoder name, or <c>null</c> for a rung no build provides.</param>
/// <param name="Extension">Output extension, including the dot.</param>
/// <param name="ExtraArguments">Encoder-specific ffmpeg arguments (bitrate, <c>-strict</c>, ...).</param>
/// <param name="MaxChannels">
/// Most channels this encoder was <em>measured</em> to produce. See <see cref="CodecLadder"/> for why
/// these are measurements rather than the capability the format claims.
/// </param>
/// <param name="Lossless">Whether the codec is lossless.</param>
/// <param name="SelfTagged">True when NcmForge's own writers handle this container's tags.</param>
/// <param name="LastResort">True for the final MP4/MP3 rungs, which warrant a warning when reached.</param>
/// <param name="Compatibility">Which players actually open the result.</param>
/// <param name="Note">Caveats worth surfacing, especially where a name over-promises.</param>
/// <param name="InLadder">
/// Whether the automatic fallback may land here. False for formats that are a deliberate choice
/// rather than a fallback — an uncompressed WAV is a perfectly good output and a terrible surprise
/// to receive because a compression codec failed.
/// </param>
public sealed record EncodeTarget(
    string Key,
    string Label,
    string? Encoder,
    string Extension,
    IReadOnlyList<string> ExtraArguments,
    int MaxChannels,
    bool Lossless,
    bool SelfTagged,
    bool LastResort,
    string Compatibility,
    string Note,
    bool InLadder = true);

/// <summary>
/// The ordered set of codecs a re-encode may fall back through, and the catalogue a user picks from.
/// </summary>
/// <remarks>
/// <para>
/// The order favours the formats that can carry a full immersive mix without discarding channels,
/// then degrades toward the ones every player understands. Two entries deliberately over-promise in
/// their conventional names, and <see cref="EncodeTarget.Note"/> corrects them: ffmpeg has no
/// <em>Dolby Atmos</em> renderer (E-AC-3 is the carrier, but the object metadata cannot be authored
/// here) and no <c>DTS-HD MA</c> encoder (<c>dca</c> produces a lossy DTS core).
/// </para>
/// <para>
/// The <see cref="EncodeTarget.MaxChannels"/> values are what each encoder was measured to produce
/// against a real source, not what its container theoretically allows — and the two differ sharply.
/// WavPack's format permits 256 channels while its ffmpeg encoder stops at 64; TrueHD's format
/// permits 16 while its encoder stops at 6. Someone choosing an output format needs the second.
/// </para>
/// <para>
/// Rungs whose encoder this build does not provide are removed by <see cref="For"/> rather than
/// failing at encode time.
/// </para>
/// </remarks>
public static class CodecLadder
{
    /// <summary>
    /// Highest channel count any label will print.
    /// </summary>
    /// <remarks>
    /// Counts are shown as a friendly ceiling rather than an exact figure, and nothing here reaches
    /// it, but the cap keeps a future high-channel encoder from advertising a number no player would
    /// accept.
    /// </remarks>
    public const int MaxDisplayedChannels = 255;

    /// <summary>
    /// Every codec NcmForge knows how to ask ffmpeg for, in preference order.
    /// </summary>
    public static IReadOnlyList<EncodeTarget> Rungs { get; } =
    [
        new("wavpack", "WavPack", "wavpack", ".wv", [], 64, true, false, false,
            "foobar2000 / VLC / mpv 可放；系统自带播放器大多不行",
            "实测可无损承载至 64 声道，是本表中唯一不会为降混而丢声道的无损选择"),

        new("truehd", "Dolby TrueHD", "truehd", ".mka", ["-strict", "-2"], 6, true, false, false,
            "VLC / PotPlayer / mpv 可放；Windows Media Player 不支持",
            "ffmpeg 的 TrueHD 编码器实测上限 6 声道（5.1）；需 Matroska 容器"),

        new("eac3", "E-AC-3 (DD+)", "eac3", ".mka", [], 6, false, false, false,
            "电视、功放与主流播放器广泛支持",
            "ffmpeg 无法写入 Atmos 对象元数据，因此这是 DD+ 而非真正的 Atmos；实测上限 5.1"),

        new("dts", "DTS", "dca", ".mka", ["-strict", "-2"], 6, false, false, false,
            "家庭影院设备与 VLC / PotPlayer 支持",
            "ffmpeg 没有 DTS-HD MA 编码器，dca 产出的是有损 DTS 核心；实测上限 5.1"),

        new("flac", "FLAC", "flac", ".flac", [], 8, true, true, false,
            "全平台通用，无损首选",
            "上限 8 声道，超过需降混"),

        // The two PCM entries sit beside FLAC because that is where a chooser looks for lossless,
        // but neither takes part in the automatic ladder: an uncompressed file is a fine thing to
        // ask for and an awful thing to receive because a compression codec happened to fail.
        //
        // 16-bit is the one that goes furthest. From a 16-bit source ffmpeg hands samples straight
        // to the muxer and PCM itself has no channel limit; asking for 24-bit instead routes through
        // the resampler, which was measured to stop at 64. Since NCM payloads are 16-bit, this is
        // also the variant that is bit-exact rather than merely lossless in principle.
        new("wav", "WAV (16-bit PCM)", "pcm_s16le", ".wav", [], 256, true, false, false,
            "所有播放器与音频编辑软件都支持",
            "未压缩 PCM，文件极大。16 位与 NCM 负载的位深一致，因此逐比特无损；负载若为 24 位请改用 WAV (24-bit PCM)",
            InLadder: false),

        new("wav24", "WAV (24-bit PCM)", "pcm_s24le", ".wav", [], 64, true, false, false,
            "所有播放器与音频编辑软件都支持",
            "未压缩 24 位 PCM，永远不会截断位深；但从 16 位源转 24 位要经过 ffmpeg 重采样器，实测上限 64 声道",
            InLadder: false),

        new("alac", "ALAC", "alac", ".m4a", [], 8, true, true, false,
            "Apple 生态原生支持；Windows Media Player 不支持",
            "实测上限 8 声道"),

        new("opus", "Opus", "libopus", ".opus", [], 8, false, false, false,
            "现代浏览器与移动端；较老的硬件播放器不支持",
            "标准声道映射上限 8 声道"),

        new("aac", "AAC-LC (MP4)", "aac", ".m4a", [], 6, false, true, true,
            "兼容性最好：Windows、移动端、各播放器通吃",
            "最后手段：会降混并有损，但几乎没有放不了的环境"),

        new("mp3", "MP3", "libmp3lame", ".mp3", [], 2, false, true, true,
            "所有播放器都支持",
            "最后手段：仅立体声"),
    ];

    /// <summary>Finds a codec by its key, ignoring case and surrounding spaces.</summary>
    public static EncodeTarget? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        string wanted = key.Trim();
        return Rungs.FirstOrDefault(r => string.Equals(r.Key, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="key"/> names a codec this tool can encode to.</summary>
    public static bool IsCodecKey(string? key) => Find(key) is not null;

    /// <summary>
    /// The one-line description shown beside a format in the chooser.
    /// </summary>
    /// <remarks>
    /// Reads as "WavPack · 无损 · 最多 64 声道 · foobar2000 / VLC / mpv 可放；…", so someone picking a
    /// format sees what it preserves and what will open it without consulting the documentation.
    /// </remarks>
    public static string Describe(EncodeTarget rung)
        => $"{rung.Label} · {(rung.Lossless ? "无损" : "有损")} · 最多 {Math.Min(rung.MaxChannels, MaxDisplayedChannels)} 声道 · {rung.Compatibility}";

    /// <summary>
    /// Builds the ladder for one conversion: rungs this build cannot encode are dropped, and each
    /// remaining rung is told how many channels it should target.
    /// </summary>
    /// <param name="onlyKey">
    /// When set, the result holds just that codec. This is how an explicit format choice reuses the
    /// ladder's retry, verification and tagging rather than needing a second code path.
    /// </param>
    /// <param name="sampleRate">
    /// Output sample rate in Hz, or 0 to keep whatever the payload already uses.
    /// </param>
    public static IReadOnlyList<EncodeTarget> For(
        int sourceChannels,
        int bitrateKbps,
        FfmpegTool? ffmpeg,
        string? onlyKey = null,
        int sampleRate = 0)
    {
        HashSet<string>? available = ffmpeg?.GetEncoderNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ladder = new List<EncodeTarget>();

        foreach (EncodeTarget rung in Rungs)
        {
            // A null encoder marks a rung that no build provides; skip it explicitly so the
            // ladder's intent stays readable.
            if (rung.Encoder is null)
            {
                continue;
            }

            if (onlyKey is not null && !string.Equals(rung.Key, onlyKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // An explicit choice may name anything; the automatic ladder only walks the rungs that
            // are meant to be fallen back to.
            if (onlyKey is null && !rung.InLadder)
            {
                continue;
            }

            // Without a probe, assume everything is available and let ffmpeg report otherwise.
            if (available is { Count: > 0 } && !available.Contains(rung.Encoder))
            {
                continue;
            }

            ladder.Add(rung with { ExtraArguments = WithEncodingParameters(rung, bitrateKbps, sampleRate) });
        }

        return ladder;
    }

    /// <summary>Applies the caller's bitrate and sample rate to the rungs.</summary>
    private static IReadOnlyList<string> WithEncodingParameters(EncodeTarget rung, int bitrateKbps, int sampleRate)
    {
        var arguments = new List<string>(rung.ExtraArguments);

        if (!rung.Lossless && bitrateKbps > 0)
        {
            // Multichannel needs proportionally more, but the codecs involved cap out; scaling by
            // the channel count keeps stereo from being handed a 5.1 bitrate and vice versa.
            int scaled = rung.MaxChannels >= 6 ? bitrateKbps * 2 : bitrateKbps;
            arguments.AddRange(["-b:a", $"{scaled}k"]);
        }

        // Resampling is always a real conversion, so it is requested explicitly rather than left to
        // the encoder's default. 48 kHz is the rate the surround formats are built around — E-AC-3
        // and DTS are only legal at 32/44.1/48 kHz, TrueHD is authored at 48/96/192, and libopus
        // resamples to 48 kHz internally whatever it is handed.
        if (sampleRate > 0)
        {
            arguments.AddRange(["-ar", sampleRate.ToString(CultureInfo.InvariantCulture)]);
        }

        return arguments;
    }

    /// <summary>
    /// Reports whether the caller should be warned about the rung a conversion settled on.
    /// </summary>
    public static bool NeedsWarning(EncodeTarget target, int sourceChannels, bool fellBack)
        => target.LastResort || fellBack || target.MaxChannels < sourceChannels;

    /// <summary>Explains, in one line, what the fallback cost.</summary>
    public static string DescribeDegradation(EncodeTarget target, int sourceChannels, int attemptIndex)
    {
        var reasons = new List<string>();

        if (attemptIndex > 0)
        {
            reasons.Add($"前 {attemptIndex} 种编码不可用");
        }

        if (target.MaxChannels < sourceChannels && sourceChannels > 0)
        {
            reasons.Add($"{sourceChannels} 声道降混为 {target.MaxChannels}");
        }

        if (target.LastResort)
        {
            reasons.Add("已降到兼容性兜底编码");
        }

        return reasons.Count == 0 ? string.Empty : string.Join("；", reasons);
    }

    /// <summary>True when the source tier means a lossless rung would not recover any quality.</summary>
    public static bool IsLosslessWasteful(AudioQualityTier tier, EncodeTarget target)
        => tier == AudioQualityTier.Lossy && target.Lossless;
}
