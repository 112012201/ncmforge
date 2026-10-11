using System.Text.RegularExpressions;
using NcmForge.Core.Audio;
using NcmForge.Core.Audio.Flac;
using NcmForge.Core.Audio.Mp4;
using NcmForge.Core.Tools;

namespace NcmForge.Core.Pipeline;

/// <summary>
/// Checks what an encoder actually produced, rather than trusting that it exited successfully.
/// </summary>
/// <remarks>
/// This exists because ffmpeg will happily <em>succeed</em> while silently changing the channel
/// layout: asked for 12 channels, its TrueHD encoder errors, its E-AC-3 and DTS encoders quietly
/// emit 5.0, and its ALAC encoder emits 6.1. Without a check, the fallback ladder would stop at the
/// first rung that "worked" and hand back a mix with half the channels missing.
/// <para>
/// Formats NcmForge can parse itself are read directly; anything else falls back to reading the
/// layout out of ffmpeg's own stream banner, which avoids depending on a bundled ffprobe.
/// </para>
/// </remarks>
public static class EncodeVerifier
{
    /// <summary>Matches the layout token ffmpeg prints between the sample rate and the bitrate.</summary>
    private static readonly Regex BannerLayout = new(
        @"Hz,\s*(?<layout>[0-9a-z]+(?:\.[0-9]+)*(?:\([a-z]+\))?|mono|stereo|quad|octagonal|hexadecagonal)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Reports how many channels an encoded file actually contains, or 0 when it cannot be told.
    /// </summary>
    public static async Task<int> CountChannelsAsync(FfmpegTool ffmpeg, string path, CancellationToken cancellationToken = default)
    {
        // Prefer reading the container directly where a parser exists.
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".flac":
                    using (var stream = File.OpenRead(path))
                    {
                        if (FlacProbe.Read(stream) is { } info)
                        {
                            return info.Channels;
                        }
                    }

                    break;

                case ".m4a":
                case ".mp4":
                    using (var stream = File.OpenRead(path))
                    {
                        if (Mp4Probe.Probe(stream) is { } track)
                        {
                            return track.Channels;
                        }
                    }

                    break;

                case ".mp3":
                    return 2;
            }
        }
        catch (IOException)
        {
            // Fall through to the banner probe.
        }
        catch (InvalidDataException)
        {
        }

        return await CountChannelsFromBannerAsync(ffmpeg, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the channel layout out of ffmpeg's own description of the file.</summary>
    private static async Task<int> CountChannelsFromBannerAsync(FfmpegTool ffmpeg, string path, CancellationToken cancellationToken)
    {
        // No output file is given, so ffmpeg only reports what it found and exits.
        ProcessResult result = await ffmpeg.RunAsync(["-hide_banner", "-i", path], cancellationToken).ConfigureAwait(false);

        string text = result.StandardError + "\n" + result.StandardOutput;
        Match match = BannerLayout.Match(text);

        return match.Success ? CountChannels(match.Groups["layout"].Value) : 0;
    }

    /// <summary>
    /// Turns a layout name into a channel count.
    /// </summary>
    /// <remarks>
    /// Dotted layouts are summed ("7.1.4" is 7 + 1 + 4 = 12, "5.0(side)" is 5). Named layouts are
    /// mapped explicitly. An unrecognised value yields 0, which callers treat as "cannot verify"
    /// rather than as a failure.
    /// </remarks>
    public static int CountChannels(string layout)
    {
        if (string.IsNullOrWhiteSpace(layout))
        {
            return 0;
        }

        string cleaned = layout.Trim().ToLowerInvariant();

        // Drop the positional suffix, e.g. "5.1(side)" -> "5.1".
        int paren = cleaned.IndexOf('(');
        if (paren >= 0)
        {
            cleaned = cleaned[..paren];
        }

        switch (cleaned)
        {
            case "mono": return 1;
            case "stereo": return 2;
            case "quad": return 4;
            case "octagonal": return 8;
            case "hexadecagonal": return 16;
            case "unknown": return 0;
        }

        var total = 0;
        var sawDigit = false;

        foreach (string part in cleaned.Split('.'))
        {
            if (int.TryParse(part, out int value))
            {
                total += value;
                sawDigit = true;
            }
        }

        return sawDigit ? total : 0;
    }

    /// <summary>Whether an encoded file carries the channel count that was asked for.</summary>
    public static bool Matches(int actual, int expected) => actual == 0 || expected == 0 || actual == expected;
}
