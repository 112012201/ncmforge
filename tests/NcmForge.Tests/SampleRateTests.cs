using NcmForge.Core.Pipeline;
using Xunit;

namespace NcmForge.Tests;

/// <summary>
/// Coverage for the output sample rate every re-encode is pinned to.
/// </summary>
/// <remarks>
/// 48 kHz is the default because it is the rate the surround formats are built around: E-AC-3 and
/// DTS are only legal at 32/44.1/48 kHz, TrueHD is authored at 48/96/192, and libopus resamples to
/// it internally whatever it is handed.
/// </remarks>
public sealed class SampleRateTests
{
    [Fact]
    public void FortyEightThousandIsTheDefault()
        => Assert.Equal(48_000, new ConversionOptions().SampleRate);

    [Fact]
    public void EveryRungIsPinnedToTheRequestedRate()
    {
        // Lossless codecs included: unlike the bitrate, which only applies to lossy ones, a sample
        // rate is meaningful for all of them.
        foreach (EncodeTarget rung in CodecLadder.For(2, 320, ffmpeg: null, sampleRate: 48_000))
        {
            Assert.Contains("-ar", rung.ExtraArguments);
            Assert.Contains("48000", rung.ExtraArguments);
        }
    }

    [Fact]
    public void TheRateIsCarriedOnExplicitlyChosenCodecsToo()
    {
        EncodeTarget wav = Assert.Single(CodecLadder.For(12, 320, ffmpeg: null, onlyKey: "wav", sampleRate: 96_000));

        int index = wav.ExtraArguments.ToList().IndexOf("-ar");
        Assert.True(index >= 0, "no -ar argument");
        Assert.Equal("96000", wav.ExtraArguments[index + 1]);
    }

    [Fact]
    public void ZeroMeansKeepThePayloadsOwnRate()
    {
        // The escape hatch: no -ar at all, so ffmpeg passes the source rate through.
        foreach (EncodeTarget rung in CodecLadder.For(2, 320, ffmpeg: null, sampleRate: 0))
        {
            Assert.DoesNotContain("-ar", rung.ExtraArguments);
        }
    }

    [Fact]
    public void TheRateAndTheBitrateBothReachALossyRung()
    {
        EncodeTarget mp3 = Assert.Single(CodecLadder.For(2, 192, ffmpeg: null, onlyKey: "mp3", sampleRate: 48_000));

        Assert.Contains("-b:a", mp3.ExtraArguments);
        Assert.Contains("192k", mp3.ExtraArguments);
        Assert.Contains("-ar", mp3.ExtraArguments);
    }

    [Fact]
    public void ALosslessRungGetsNoBitrateButStillGetsTheRate()
    {
        EncodeTarget flac = Assert.Single(CodecLadder.For(2, 320, ffmpeg: null, onlyKey: "flac", sampleRate: 48_000));

        Assert.DoesNotContain("-b:a", flac.ExtraArguments);
        Assert.Contains("-ar", flac.ExtraArguments);
    }

    [Fact]
    public void TheRateIsAPlainIntegerWithNoSeparators()
    {
        // ffmpeg parses -ar itself and rejects "48,000"/"48 000"; the value must be digits only.
        EncodeTarget rung = Assert.Single(CodecLadder.For(2, 320, ffmpeg: null, onlyKey: "flac", sampleRate: 48_000));

        int index = rung.ExtraArguments.ToList().IndexOf("-ar");
        Assert.True(index >= 0, "no -ar argument");

        string value = rung.ExtraArguments[index + 1];
        Assert.Matches("^[0-9]+$", value);
        Assert.Equal(48_000, int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NoArgumentIsEmittedAsABareStringWithoutItsFlag()
    {
        // Every value in the list is either a flag or follows one; a stray value would be handed to
        // ffmpeg as a positional argument and break the command line.
        foreach (EncodeTarget rung in CodecLadder.For(6, 320, ffmpeg: null, sampleRate: 48_000))
        {
            for (int i = 0; i < rung.ExtraArguments.Count; i++)
            {
                bool isFlag = rung.ExtraArguments[i].StartsWith('-');
                bool followsFlag = i > 0 && rung.ExtraArguments[i - 1].StartsWith('-');

                Assert.True(
                    isFlag || followsFlag,
                    $"{rung.Key}: '{rung.ExtraArguments[i]}' is neither a flag nor a value for one");
            }
        }
    }
}
