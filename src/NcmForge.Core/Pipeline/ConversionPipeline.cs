using NcmForge.Core.Audio;
using NcmForge.Core.Audio.Flac;
using NcmForge.Core.Audio.Mp4;
using NcmForge.Core.Covers;
using NcmForge.Core.Ncm;
using NcmForge.Core.Tagging;
using NcmForge.Core.Tools;

namespace NcmForge.Core.Pipeline;

/// <summary>
/// Turns an .ncm container into a tagged, artwork-bearing audio file.
/// </summary>
/// <remarks>
/// The pipeline deliberately distinguishes <em>remux</em> from <em>transcode</em>. When the payload
/// already is the requested codec, the audio bytes are copied verbatim and only metadata is
/// rewritten; that is both far faster and lossless. Re-encoding happens only when the request
/// genuinely requires a different codec or a different channel count.
/// </remarks>
public sealed class ConversionPipeline
{
    private readonly CoverArtFetcher _covers;

    public ConversionPipeline(HttpClient http)
    {
        _covers = new CoverArtFetcher(http);
    }

    /// <summary>Decides the route without touching the filesystem.</summary>
    public static ConversionPlan Plan(NcmFile ncm, ConversionOptions options)
    {
        AudioFormat format = ncm.Format;
        int channelCount = ResolveChannelCount(ncm) ?? 0;
        bool decodable = CanDecodeLocally(ncm, options);

        if (options.Format == OutputFormat.LosslessCopy)
        {
            return new ConversionPlan("lossless-copy", ExtensionFor(ncm, options), "lossless copy requested");
        }

        if (!decodable && string.IsNullOrWhiteSpace(options.ExternalDecoderCommand))
        {
            return new ConversionPlan(
                "lossless-copy",
                ExtensionFor(ncm, options),
                $"the configured ffmpeg has no decoder for {ncm.CodecDisplayName}; the payload is copied " +
                "bit-for-bit. Point at an ffmpeg build that supports it, or configure an external decoder.");
        }

        if (options.Format == OutputFormat.Mp3)
        {
            if (!options.ForceReencode && format == AudioFormat.Mp3 && channelCount is 0 or <= 2)
            {
                return new ConversionPlan("remux", ".mp3", "payload is already MP3; audio copied verbatim");
            }

            return new ConversionPlan("transcode", ".mp3", $"encode {ncm.CodecDisplayName} to MP3");
        }

        // FLAC.
        if (!options.ForceReencode && format == AudioFormat.Flac && channelCount is > 0 and <= 8)
        {
            return new ConversionPlan("remux", ".flac", "payload is already FLAC; audio copied verbatim");
        }

        if (options.Format == OutputFormat.Compatible && options.ForceReencode)
        {
            IReadOnlyList<EncodeTarget> ladder = CodecLadder.For(channelCount, options.BitrateKbps, FfmpegTool.Locate(options.FfmpegPath));
            if (ladder.Count > 0)
            {
                return new ConversionPlan(
                    "ladder",
                    ladder[0].Extension,
                    "按优先级逐档尝试：" + string.Join(" → ", ladder.Select(r => r.Label)));
            }
        }

        // "Compatible" leaves the codec choice to the selector, so the plan is built from it.
        FfmpegTool? tool = FfmpegTool.Locate(options.FfmpegPath);
        EncodeChoice choice = EncoderSelector.Select(
            options.Format,
            EncoderSelector.TierOf(ncm),
            channelCount,
            options.ForceReencode ? options.Hardware : HardwareAcceleration.Disabled,
            tool,
            out string? selectorNote);

        if (options.Format == OutputFormat.Compatible && !options.ForceReencode && IsAlreadyCompatible(ncm, channelCount))
        {
            return new ConversionPlan("remux", PayloadExtension(ncm), "payload is already broadly playable; audio copied verbatim");
        }

        if (choice.TargetChannels is > 8)
        {
            throw new ConversionException(
                $"FLAC stores at most 8 channels but the source has {channelCount}. " +
                "Choose a 7.1 or 5.1 downmix, or use lossless copy to keep every channel.");
        }

        if (channelCount > 8 && options.Surround == SurroundMapping.Preserve && !choice.Accelerated && choice.Extension == ".flac")
        {
            throw new ConversionException(
                $"FLAC stores at most 8 channels but the source has {channelCount}. " +
                "Choose a 7.1 or 5.1 downmix, or use lossless copy to keep every channel.");
        }

        string reason = $"{choice.Reason} → {choice.CodecLabel}";
        if (selectorNote is not null)
        {
            reason += $"（{selectorNote}）";
        }

        return new ConversionPlan("transcode", choice.Extension, reason);
    }

    /// <summary>
    /// True when the payload is already a codec every common player handles, so "compatible" needs
    /// no work.
    /// </summary>
    private static bool IsAlreadyCompatible(NcmFile ncm, int channelCount) => ncm.Format switch
    {
        AudioFormat.Mp3 => channelCount is 0 or <= 2,
        AudioFormat.Aac => true,
        AudioFormat.Flac => channelCount is > 0 and <= 8,
        _ => false,
    };

    /// <summary>Runs a conversion end to end.</summary>
    public async Task<ConversionResult> ConvertAsync(
        string ncmPath,
        ConversionOptions options,
        Action<string>? log = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using NcmFile ncm = NcmFile.Open(ncmPath);

        int? channels = ResolveChannelCount(ncm);
        log?.Invoke($"container : {Path.GetFileName(ncmPath)} — {DescribeLayout(ncm)}, {ncm.AudioLength:N0} bytes of payload");
        log?.Invoke($"payload   : {ncm.CodecDisplayName}{(channels is > 0 ? $", {channels} channels" : string.Empty)}");

        ConversionPlan plan = Plan(ncm, options);
        log?.Invoke($"plan      : {plan.Action} -> {plan.OutputExtension} ({plan.Reason})");

        string outputDirectory = options.OutputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(ncmPath)) ?? ".";
        Directory.CreateDirectory(outputDirectory);
        string baseName = Path.GetFileNameWithoutExtension(ncmPath);
        string outputPath = Path.Combine(outputDirectory, baseName + plan.OutputExtension);

        if (File.Exists(outputPath) && !options.Overwrite)
        {
            throw new ConversionException($"Output already exists: {outputPath}");
        }

        string workDirectory = CreateWorkDirectory(outputDirectory);

        try
        {
            CoverArt? cover = await ResolveCoverAsync(ncm, options, log, cancellationToken).ConfigureAwait(false);

            TrackTags tags = TrackTags.FromMetadata(ncm.Metadata);
            tags.Cover = options.Cover == CoverMode.None ? null : cover;
            tags.Comment ??= $"Decoded from NetEase .ncm by NcmForge ({ncm.CodecDisplayName})";
            ApplyLyrics(ncmPath, ncm, options, tags, log);

            bool extractElementaryStream = options.ExtractElementaryStream && ncm.Format == AudioFormat.Mp4;

            // A lossless copy still carries tags when its container supports them: the point is to
            // leave the *audio* untouched, not to strip the metadata the user asked for.
            bool canTagCopy = !extractElementaryStream
                && ncm.Format is AudioFormat.Mp3 or AudioFormat.Flac or AudioFormat.Mp4;

            var warnings = new List<string>();
            string finalOutputPath = outputPath;

            if (plan.Action == "ladder")
            {
                string ladderInput = Path.Combine(workDirectory, baseName + PayloadExtension(ncm));
                progress?.Report(0.05);
                ncm.ExtractAudioTo(ladderInput, null, cancellationToken);
                progress?.Report(0.35);

                LadderOutcome outcome = await RunLadderAsync(ncm, ladderInput, options, tags, workDirectory, baseName, log, cancellationToken)
                    .ConfigureAwait(false);

                // The rung that succeeded decides the container, so the file name is settled here
                // rather than from the plan — a fallback changes the extension.
                finalOutputPath = Path.Combine(outputDirectory, baseName + outcome.Target.Extension);
                if (File.Exists(finalOutputPath) && !options.Overwrite)
                {
                    throw new ConversionException($"Output already exists: {finalOutputPath}");
                }

                TagLadderOutput(outcome, finalOutputPath, tags, log);
                warnings.AddRange(outcome.Warnings);
                progress?.Report(1.0);
            }
            else if (plan.Action == "lossless-copy" && !canTagCopy)
            {
                WriteLosslessCopy(ncm, outputPath, options, log, cancellationToken);
                progress?.Report(1.0);
            }
            else
            {
                string payloadPath = Path.Combine(workDirectory, baseName + PayloadExtension(ncm));
                progress?.Report(0.05);
                ncm.ExtractAudioTo(payloadPath, null, cancellationToken);
                log?.Invoke($"extracted : {new FileInfo(payloadPath).Length:N0} bytes -> {Path.GetFileName(payloadPath)}");
                progress?.Report(0.35);

                if (plan.Action == "remux" || (plan.Action == "lossless-copy" && canTagCopy))
                {
                    WriteTaggedRemux(payloadPath, outputPath, ncm.Format, tags, log);
                }
                else
                {
                    await TranscodeAsync(ncm, payloadPath, outputPath, options, tags, workDirectory, baseName, log, progress, cancellationToken)
                        .ConfigureAwait(false);
                }

                progress?.Report(1.0);
            }

            long outputBytes = new FileInfo(finalOutputPath).Length;
            log?.Invoke($"done      : {finalOutputPath} ({outputBytes:N0} bytes)");

            foreach (string warning in warnings)
            {
                log?.Invoke($"warning   : {warning}");
            }

            return new ConversionResult(ncmPath, finalOutputPath, ncm.CodecDisplayName, channels ?? 0, plan, cover, outputBytes, warnings);
        }
        finally
        {
            if (options.KeepTemporaryFiles)
            {
                log?.Invoke($"temp kept : {workDirectory}");
            }
            else
            {
                TryDeleteDirectory(workDirectory);
            }
        }
    }

    /// <summary>
    /// Attaches the sidecar lyrics, if the caller asked for them.
    /// </summary>
    private static void ApplyLyrics(string ncmPath, NcmFile ncm, ConversionOptions options, TrackTags tags, Action<string>? log)
    {
        if (options.Lyrics == LyricsMode.None)
        {
            return;
        }

        Lyrics.LyricDocument? document = Lyrics.LyricLoader.FindAndLoad(ncmPath, ncm.Metadata.MusicName, log);
        if (document is null)
        {
            return;
        }

        tags.Lyrics = document.ToLrc();
        tags.PlainLyrics = document.ToPlainText();
    }

    private static void WriteLosslessCopy(NcmFile ncm, string outputPath, ConversionOptions options,
        Action<string>? log, CancellationToken cancellationToken)
    {
        if (options.ExtractElementaryStream && ncm.Format == AudioFormat.Mp4)
        {
            using Stream source = ncm.OpenDecryptedAudioStream();
            using var destination = File.Create(outputPath);
            Mp4ExtractionResult? result = Mp4SampleExtractor.Extract(source, destination);
            if (result is null)
            {
                throw new ConversionException("The MP4 payload has no readable audio sample table.");
            }

            log?.Invoke($"extracted : {result.SampleCount:N0} {result.CodecFourCc} samples, {result.ByteCount:N0} bytes of elementary stream");
            return;
        }

        ncm.ExtractAudioTo(outputPath, null, cancellationToken);
    }

    private static void WriteTaggedRemux(string payloadPath, string outputPath, AudioFormat format,
        TrackTags tags, Action<string>? log)
    {
        using var source = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        switch (format)
        {
            case AudioFormat.Mp3:
                Id3v2Writer.WriteTagged(source, destination, tags);
                log?.Invoke("tagged    : ID3v2.3 written (remux, audio untouched)");
                break;

            case AudioFormat.Flac:
                FlacTagWriter.WriteTagged(source, destination, tags);
                log?.Invoke("tagged    : Vorbis comments + PICTURE written (remux, frames untouched)");
                break;

            case AudioFormat.Mp4:
                Mp4TagResult result = Mp4TagWriter.WriteTagged(source, destination, tags);
                log?.Invoke(result.Atoms.Count == 0
                    ? "tagged    : MP4 copied unchanged (nothing to write)"
                    : $"tagged    : MP4 metadata written ({string.Join(", ", result.Atoms)}), samples untouched");
                break;

            default:
                throw new ConversionException($"Cannot remux {format} with tags; this path should have been a transcode.");
        }
    }

    private async Task TranscodeAsync(NcmFile ncm, string payloadPath, string outputPath, ConversionOptions options,
        TrackTags tags, string workDirectory, string baseName, Action<string>? log, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        FfmpegTool ffmpeg = FfmpegTool.Locate(options.FfmpegPath)
            ?? throw new ConversionException(
                "ffmpeg was not found. Install it, put it on PATH, or set NCMFORGE_FFMPEG / the ffmpeg path in settings.");

        int channelCount = ResolveChannelCount(ncm) ?? 0;
        string ffmpegInput = payloadPath;

        if (!CanDecodeLocally(ncm, options))
        {
            ffmpegInput = await DecodeWithExternalToolAsync(ncm, payloadPath, options, channelCount, workDirectory, baseName, log, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(0.55);
        }

        EncodeChoice choice = EncoderSelector.Select(
            options.Format,
            EncoderSelector.TierOf(ncm),
            channelCount,
            options.ForceReencode ? options.Hardware : HardwareAcceleration.Disabled,
            ffmpeg,
            out string? note);

        if (note is not null)
        {
            log?.Invoke($"encoder   : {note}");
        }

        log?.Invoke($"encoder   : {choice.Encoder} → {choice.CodecLabel}（{choice.Reason}）");

        int? target = choice.TargetChannels ?? TargetChannelCount(options, channelCount);
        string encodedPath = Path.Combine(workDirectory, baseName + ".encoded" + choice.Extension);

        ProcessResult result = await RunEncodeAsync(ffmpeg, ffmpegInput, encodedPath, choice, target, options, log, cancellationToken)
            .ConfigureAwait(false);

        // An accelerated encoder can fail for reasons outside our control — a driver, a codec
        // profile, a channel layout it will not take. Retrying on the CPU encoder keeps the
        // conversion working instead of failing on an optimisation the user only asked to prefer.
        if (!result.Succeeded
            && choice.Accelerated
            && !string.Equals(choice.Encoder, choice.FallbackEncoder, StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"encoder   : {choice.Encoder} 失败（exit {result.ExitCode}），回退 {choice.FallbackEncoder} 重试");
            result = await RunEncodeAsync(ffmpeg, ffmpegInput, encodedPath,
                choice with { Encoder = choice.FallbackEncoder, Accelerated = false }, target, options, log, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!result.Succeeded)
        {
            throw new ConversionException(
                $"ffmpeg failed with exit code {result.ExitCode}.{Environment.NewLine}{Tail(result.StandardError)}");
        }

        progress?.Report(0.85);

        TagEncodedOutput(encodedPath, choice.Extension, outputPath, tags, log);
    }

    /// <summary>Runs one ffmpeg encode attempt with the chosen encoder.</summary>
    private static async Task<ProcessResult> RunEncodeAsync(FfmpegTool ffmpeg, string input, string output,
        EncodeChoice choice, int? channels, ConversionOptions options, Action<string>? log, CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn",
        };

        if (channels is > 0)
        {
            arguments.Add("-ac");
            arguments.Add(channels.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.AddRange(["-c:a", choice.Encoder]);

        if (choice.Extension == ".flac")
        {
            arguments.AddRange(["-compression_level", options.FlacCompressionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        else
        {
            arguments.AddRange(["-b:a", $"{options.BitrateKbps}k"]);
        }

        // Tags are written by this tool, so ffmpeg must not carry any across.
        arguments.AddRange(["-map_metadata", "-1"]);
        arguments.Add(output);

        log?.Invoke($"ffmpeg    : {string.Join(' ', arguments)}");
        return await ffmpeg.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes tags into the freshly encoded file, using the writer its container needs.</summary>
    private static void TagEncodedOutput(string encodedPath, string extension, string outputPath, TrackTags tags, Action<string>? log)
    {
        using var encoded = new FileStream(encodedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        switch (extension)
        {
            case ".mp3":
                Id3v2Writer.WriteTagged(encoded, destination, tags);
                log?.Invoke("tagged    : ID3v2.3 + cover written");
                break;

            case ".flac":
                FlacTagWriter.WriteTagged(encoded, destination, tags);
                log?.Invoke("tagged    : Vorbis comments + PICTURE written");
                break;

            default:
                // AAC lands in an MP4 container, which takes iTunes-style atoms.
                Mp4TagResult mp4 = Mp4TagWriter.WriteTagged(encoded, destination, tags);
                log?.Invoke(mp4.Atoms.Count == 0
                    ? "tagged    : MP4 copied unchanged (nothing to write)"
                    : $"tagged    : MP4 metadata written ({string.Join(", ", mp4.Atoms)})");
                break;
        }
    }

    /// <summary>The rung a ladder conversion settled on.</summary>
    private sealed record LadderOutcome(
        string EncodedPath,
        EncodeTarget Target,
        int AttemptIndex,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Walks the codec ladder until one rung encodes, degrading only as far as necessary.
    /// </summary>
    /// <remarks>
    /// Each rung is attempted and, if it fails, the next is tried — so a build without TrueHD, a
    /// driver that rejects a channel layout, or a codec that cannot hold 12 channels costs one
    /// attempt rather than the whole conversion. Cover art is treated the same way: a rung is first
    /// tried with the picture attached and, if that is what failed, retried without it before
    /// giving up the rung.
    /// </remarks>
    private async Task<LadderOutcome> RunLadderAsync(NcmFile ncm, string payloadPath, ConversionOptions options,
        TrackTags tags, string workDirectory, string baseName, Action<string>? log, CancellationToken cancellationToken)
    {
        FfmpegTool ffmpeg = FfmpegTool.Locate(options.FfmpegPath)
            ?? throw new ConversionException(
                "ffmpeg was not found. Install it, put it on PATH, or set NCMFORGE_FFMPEG / the ffmpeg path in settings.");

        int channelCount = ResolveChannelCount(ncm) ?? 0;
        string input = payloadPath;

        if (!CanDecodeLocally(ncm, options))
        {
            input = await DecodeWithExternalToolAsync(ncm, payloadPath, options, channelCount, workDirectory, baseName, log, cancellationToken)
                .ConfigureAwait(false);
        }

        IReadOnlyList<EncodeTarget> ladder = CodecLadder.For(channelCount, options.BitrateKbps, ffmpeg);
        if (ladder.Count == 0)
        {
            throw new ConversionException("This ffmpeg build provides none of the encoders the fallback ladder needs.");
        }

        log?.Invoke($"ladder    : {string.Join(" → ", ladder.Select(r => r.Label))}");

        string? coverPath = null;
        if (options.Cover != CoverMode.None && tags.Cover is { } cover)
        {
            coverPath = Path.Combine(workDirectory, baseName + ".cover" + Audio.ImageSignature.Extension(cover.Format));
            await File.WriteAllBytesAsync(coverPath, cover.Data, cancellationToken).ConfigureAwait(false);
        }

        var failures = new List<string>();

        for (var i = 0; i < ladder.Count; i++)
        {
            EncodeTarget rung = ladder[i];
            int? targetChannels = channelCount > rung.MaxChannels ? rung.MaxChannels : null;
            string encodedPath = Path.Combine(workDirectory, $"{baseName}.rung{i}{rung.Extension}");

            string downmix = targetChannels is null ? string.Empty : $"（降混到 {targetChannels} 声道）";
            log?.Invoke($"attempt   : [{i + 1}/{ladder.Count}] {rung.Label}{downmix}");

            ProcessResult result = await RunEncodeAsync(ffmpeg, input, encodedPath, rung, targetChannels, options, tags, coverPath, log, cancellationToken)
                .ConfigureAwait(false);

            // A container that cannot take the picture should not cost us the rung.
            if (!result.Succeeded && coverPath is not null && !rung.SelfTagged)
            {
                log?.Invoke($"retry     : {rung.Label} 去掉封面重试");
                result = await RunEncodeAsync(ffmpeg, input, encodedPath, rung, targetChannels, options, tags, null, log, cancellationToken)
                    .ConfigureAwait(false);
            }

            int expectedChannels = targetChannels ?? channelCount;

            // ffmpeg can exit 0 having quietly emitted fewer channels than asked for, so the file
            // itself is checked. Without this the ladder would stop on a half-empty mix.
            if (result.Succeeded && expectedChannels > 0)
            {
                int actual = await EncodeVerifier.CountChannelsAsync(ffmpeg, encodedPath, cancellationToken).ConfigureAwait(false);

                if (!EncodeVerifier.Matches(actual, expectedChannels))
                {
                    log?.Invoke($"retry     : {rung.Label} 实际输出 {actual} 声道（期望 {expectedChannels}），显式指定后重试");

                    result = await RunEncodeAsync(ffmpeg, input, encodedPath, rung, expectedChannels, options, tags,
                        rung.SelfTagged ? null : coverPath, log, cancellationToken).ConfigureAwait(false);

                    actual = result.Succeeded
                        ? await EncodeVerifier.CountChannelsAsync(ffmpeg, encodedPath, cancellationToken).ConfigureAwait(false)
                        : 0;

                    if (result.Succeeded && !EncodeVerifier.Matches(actual, expectedChannels))
                    {
                        failures.Add($"{rung.Label}: 静默降为 {actual} 声道（期望 {expectedChannels}）");
                        log?.Invoke($"failed    : {rung.Label} — 声道被静默降低，尝试下一档");
                        continue;
                    }
                }
            }

            if (result.Succeeded)
            {
                var warnings = new List<string>();
                if (CodecLadder.NeedsWarning(rung, channelCount, i > 0))
                {
                    string detail = CodecLadder.DescribeDegradation(rung, channelCount, i);
                    warnings.Add($"{rung.Label}：{detail}。{rung.Note}");
                }

                if (rung.Encoder is "truehd" or "dca")
                {
                    log?.Invoke($"note      : {rung.Label} 使用 ffmpeg 的实验性编码器（-strict -2）");
                }

                return new LadderOutcome(encodedPath, rung, i, warnings);
            }

            string reason = FirstLine(result.StandardError);
            failures.Add($"{rung.Label}: {reason}");
            log?.Invoke($"failed    : {rung.Label} — {reason}");
        }

        throw new ConversionException(
            "所有候选编码都失败了：" + Environment.NewLine +
            string.Join(Environment.NewLine, failures.Select(f => "  • " + f)));
    }

    /// <summary>Moves the successful rung's output into place, writing tags where we can.</summary>
    private static void TagLadderOutput(LadderOutcome outcome, string outputPath, TrackTags tags, Action<string>? log)
    {
        if (outcome.Target.SelfTagged)
        {
            TagEncodedOutput(outcome.EncodedPath, outcome.Target.Extension, outputPath, tags, log);
            return;
        }

        // Containers with no writer here already carry the metadata ffmpeg was given, so the file
        // is moved rather than rebuilt.
        File.Move(outcome.EncodedPath, outputPath, overwrite: true);
        log?.Invoke($"tagged    : {outcome.Target.Label} 容器由 ffmpeg 写入元数据");
    }

    /// <summary>Runs one ladder rung.</summary>
    private static async Task<ProcessResult> RunEncodeAsync(FfmpegTool ffmpeg, string input, string output,
        EncodeTarget rung, int? channels, ConversionOptions options, TrackTags tags, string? coverPath,
        Action<string>? log, CancellationToken cancellationToken)
    {
        // Every input is declared before any output option: ffmpeg binds options that precede an
        // -i to that input, so putting -map between two inputs makes it an (invalid) input option.
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };

        bool attachCover = coverPath is not null && !rung.SelfTagged;
        if (attachCover)
        {
            arguments.AddRange(["-i", coverPath!]);
        }

        arguments.AddRange(["-map", "0:a:0"]);

        // -vn would discard the very stream the cover is mapped from, so the audio-only guards only
        // apply when no picture is attached. -map 0:a:0 already excludes the source's own video.
        arguments.AddRange(attachCover ? ["-sn", "-dn"] : ["-vn", "-sn", "-dn"]);

        if (channels is > 0)
        {
            arguments.Add("-ac");
            arguments.Add(channels.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.AddRange(["-c:a", rung.Encoder!]);

        if (rung.Extension == ".flac")
        {
            arguments.AddRange(["-compression_level", options.FlacCompressionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(rung.ExtraArguments);

        if (rung.SelfTagged)
        {
            // Tags are written by this tool, so ffmpeg must not carry any across.
            arguments.AddRange(["-map_metadata", "-1"]);
        }
        else
        {
            // Drop the source's own global metadata first — otherwise a Matroska output inherits
            // MP4 brand tags like major_brand and track from the .m4a payload — then write ours.
            arguments.AddRange(["-map_metadata", "-1"]);
            AddMetadataArguments(arguments, tags);

            if (attachCover)
            {
                arguments.AddRange(["-map", "1:v", "-c:v", "copy", "-disposition:v", "attached_pic"]);
            }
        }

        arguments.Add(output);

        log?.Invoke($"ffmpeg    : {string.Join(' ', arguments)}");
        return await ffmpeg.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Passes text metadata to ffmpeg for containers this tool cannot tag itself.</summary>
    private static void AddMetadataArguments(List<string> arguments, TrackTags tags)
    {
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                arguments.AddRange(["-metadata", $"{key}={value}"]);
            }
        }

        Add("title", tags.Title);
        Add("artist", tags.Artist);
        Add("album", tags.Album);
        Add("album_artist", tags.AlbumArtist ?? tags.Artist);
        Add("genre", tags.Genre);
        Add("comment", tags.Comment);
        Add("lyrics", tags.PlainLyrics);
        Add("date", tags.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(no diagnostics)";
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("In fftools/", StringComparison.Ordinal))
            {
                return trimmed.Length > 160 ? trimmed[..160] + "…" : trimmed;
            }
        }

        return "(no diagnostics)";
    }

    private static async Task<string> DecodeWithExternalToolAsync(NcmFile ncm, string payloadPath, ConversionOptions options,
        int channelCount, string workDirectory, string baseName, Action<string>? log, CancellationToken cancellationToken)
    {
        var decoder = new ExternalDecoder(options.ExternalDecoderCommand!);
        if (!decoder.IsUsable(out string? problem))
        {
            throw new ConversionException($"The external decoder command is invalid: {problem}.");
        }

        string decoderInput = payloadPath;

        // Codecs carried in MP4 usually have to be pulled out of the sample tables first.
        if (options.ExternalDecoderInput == ExternalDecoderInput.ElementaryStream && ncm.Format == AudioFormat.Mp4)
        {
            decoderInput = Path.Combine(workDirectory, baseName + ".elementary");
            using Stream source = ncm.OpenDecryptedAudioStream();
            using var destination = File.Create(decoderInput);
            Mp4ExtractionResult? extracted = Mp4SampleExtractor.Extract(source, destination);

            if (extracted is null || extracted.ByteCount == 0)
            {
                throw new ConversionException("Could not extract an elementary stream from the MP4 payload for the external decoder.");
            }

            log?.Invoke($"extracted : elementary stream {extracted.CodecFourCc}, {extracted.SampleCount:N0} samples, {extracted.ByteCount:N0} bytes");
        }

        string wavPath = Path.Combine(workDirectory, baseName + ".decoded.wav");
        int sampleRate = ncm.Mp4Track?.SampleRate ?? ncm.Metadata.SampleRate ?? 0;

        await decoder.DecodeAsync(decoderInput, wavPath, channelCount, sampleRate, log, cancellationToken).ConfigureAwait(false);
        return wavPath;
    }

    private async Task<CoverArt?> ResolveCoverAsync(NcmFile ncm, ConversionOptions options, Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (options.Cover == CoverMode.None)
        {
            return null;
        }

        CoverArt? embedded = null;
        if (ncm.ReadEmbeddedImage() is { Length: > 0 } bytes && ncm.EmbeddedImage is { } image)
        {
            embedded = new CoverArt(bytes, image.Format, "embedded");
        }

        if (options.Cover == CoverMode.Embedded)
        {
            log?.Invoke(embedded is null ? "cover     : none embedded in the container" : "cover     : using the container's own artwork");
            return embedded;
        }

        // Online modes never hand the embedded image to the fetcher, so the network is genuinely tried.
        CoverQuery query = CoverQuery.FromMetadata(ncm.Metadata, embedded: null);
        CoverArt? online = await _covers.FetchAsync(query, m => log?.Invoke(m), cancellationToken).ConfigureAwait(false);

        if (online is not null)
        {
            return online;
        }

        if (options.Cover == CoverMode.OnlineThenEmbedded && embedded is not null)
        {
            log?.Invoke("cover     : falling back to the container's own artwork");
            return embedded;
        }

        return null;
    }

    /// <summary>Channel count of the decoded audio, or <c>null</c> when it cannot be determined cheaply.</summary>
    public static int? ResolveChannelCount(NcmFile ncm)
    {
        switch (ncm.Format)
        {
            case AudioFormat.Flac:
                using (Stream stream = ncm.OpenDecryptedAudioStream())
                {
                    return FlacProbe.Read(stream)?.Channels;
                }

            case AudioFormat.Mp3:
            case AudioFormat.Aac:
                return 2;

            case AudioFormat.Mp4:
                return ncm.Mp4Track?.Channels;

            default:
                return null;
        }
    }

    /// <summary>Sample rate of the decoded audio, or <c>null</c> when unknown.</summary>
    public static int? ResolveSampleRate(NcmFile ncm)
    {
        switch (ncm.Format)
        {
            case AudioFormat.Flac:
                using (Stream stream = ncm.OpenDecryptedAudioStream())
                {
                    return FlacProbe.Read(stream)?.SampleRate;
                }

            case AudioFormat.Mp4:
                return ncm.Mp4Track?.SampleRate;

            default:
                return null;
        }
    }

    /// <summary>
    /// True when the configured ffmpeg has a decoder for this payload.
    /// </summary>
    /// <remarks>
    /// Probed against the actual binary rather than a hard-coded list of "unsupported" codecs: a
    /// custom ffmpeg build can carry decoders the official ones lack — AVS3-P3 among them — and
    /// refusing work the installed binary can do would be wrong.
    /// </remarks>
    private static bool CanDecodeLocally(NcmFile ncm, ConversionOptions options)
    {
        if (ncm.Format != AudioFormat.Mp4)
        {
            return true;
        }

        string? fourCc = ncm.Mp4Track?.CodecFourCc;
        if (string.IsNullOrEmpty(fourCc) || options.ForceFfmpegDecode)
        {
            return true;
        }

        FfmpegTool? ffmpeg = FfmpegTool.Locate(options.FfmpegPath);

        // No ffmpeg at all is reported later as its own, clearer error.
        return ffmpeg is null || ffmpeg.HasDecoderFor(fourCc);
    }

    private static int? TargetChannelCount(ConversionOptions options, int sourceChannels)
    {
        if (options.Format == OutputFormat.Mp3)
        {
            // MP3 is stereo at most, so this is not a preference.
            return 2;
        }

        return options.Surround switch
        {
            SurroundMapping.DownmixToStereo => 2,
            SurroundMapping.DownmixTo51 => 6,
            SurroundMapping.DownmixTo71 => 8,
            _ => null,
        };
    }

    /// <summary>File extension for the raw decrypted payload, chosen so ffmpeg picks the right demuxer.</summary>
    private static string PayloadExtension(NcmFile ncm) => ncm.Format switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Flac => ".flac",
        AudioFormat.Mp4 => ".m4a",
        AudioFormat.Eac3 => ".eac3",
        AudioFormat.Ac3 => ".ac3",
        AudioFormat.Aac => ".aac",
        AudioFormat.Ogg => ".ogg",
        AudioFormat.Wav => ".wav",
        _ => ".bin",
    };

    private static string ExtensionFor(NcmFile ncm, ConversionOptions options)
    {
        if (options.ExtractElementaryStream && ncm.Format == AudioFormat.Mp4)
        {
            string? fourCc = ncm.Mp4Track?.CodecFourCc;
            return fourCc switch
            {
                "av3a" => ".av3a",
                "ec-3" or "dec3" => ".eac3",
                "ac-3" or "dac3" => ".ac3",
                _ => ".raw",
            };
        }

        return PayloadExtension(ncm);
    }

    private static string DescribeLayout(NcmFile ncm) => ncm.Layout switch
    {
        NcmPayloadLayout.AfterEmbeddedImage => "immersive layout with an embedded cover",
        NcmPayloadLayout.Scanned => "layout recovered by signature scan",
        _ => "standard layout",
    };

    private static string Tail(string text, int lines = 12)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(no diagnostics)";
        }

        string[] all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, all.TakeLast(lines)).Trim();
    }

    /// <summary>
    /// Chooses a scratch directory for intermediates.
    /// </summary>
    /// <remarks>
    /// Intermediates are staged beside the output rather than in the system temp directory: that
    /// keeps them on the same volume as the result, and it keeps a conversion working in
    /// environments where the temp path is not writable.
    /// </remarks>
    private static string CreateWorkDirectory(string outputDirectory)
    {
        string besideOutput = Path.Combine(outputDirectory, ".ncmforge-tmp", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(besideOutput);
            return besideOutput;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        string systemTemp = Path.Combine(Path.GetTempPath(), "ncmforge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(systemTemp);
        return systemTemp;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            // Remove the shared staging parent once it is empty.
            string? parent = Path.GetDirectoryName(path);
            if (parent is not null && Path.GetFileName(parent) == ".ncmforge-tmp" && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
