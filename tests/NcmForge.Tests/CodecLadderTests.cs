using NcmForge.Core.Pipeline;
using Xunit;

namespace NcmForge.Tests;

/// <summary>
/// Coverage for the re-encode fallback ladder and the check that keeps it honest.
/// </summary>
/// <remarks>
/// These tests pin down behaviour that was established by measurement, not by reading ffmpeg's
/// documentation: several encoders exit successfully while emitting fewer channels than requested,
/// so the declared ceilings here are the ones actually observed.
/// </remarks>
public sealed class CodecLadderTests
{
    [Theory]
    // The layout strings ffmpeg prints for the layouts this project meets.
    [InlineData("7.1.4", 12)]
    [InlineData("5.1", 6)]
    [InlineData("5.1(side)", 6)]
    [InlineData("5.0(side)", 5)]
    [InlineData("7.1", 8)]
    [InlineData("stereo", 2)]
    [InlineData("mono", 1)]
    [InlineData("quad", 4)]
    [InlineData("2.1", 3)]
    public void ChannelLayoutsAreCountedCorrectly(string layout, int expected)
        => Assert.Equal(expected, EncodeVerifier.CountChannels(layout));

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("nonsense")]
    public void UnreadableLayoutsReportZeroSoTheyAreNotTreatedAsFailures(string layout)
        => Assert.Equal(0, EncodeVerifier.CountChannels(layout));

    [Fact]
    public void AVerifiableZeroChannelsNeverFailsTheCheck()
    {
        // 0 means "could not determine", which must not be mistaken for a mismatch.
        Assert.True(EncodeVerifier.Matches(0, 12));
        Assert.True(EncodeVerifier.Matches(12, 0));
        Assert.True(EncodeVerifier.Matches(6, 6));
        Assert.False(EncodeVerifier.Matches(5, 6));
    }

    [Fact]
    public void TheLadderFollowsTheRequestedPreferenceOrder()
    {
        IReadOnlyList<EncodeTarget> ladder = CodecLadder.For(sourceChannels: 12, bitrateKbps: 320, ffmpeg: null);

        Assert.Equal(
            ["Dolby TrueHD", "E-AC-3 (DD+)", "DTS", "FLAC", "ALAC", "Opus", "WavPack", "AAC-LC (MP4)", "MP3"],
            ladder.Select(r => r.Label));
    }

    [Fact]
    public void TheLastTwoRungsAreMarkedAsLastResort()
    {
        IReadOnlyList<EncodeTarget> ladder = CodecLadder.For(12, 320, ffmpeg: null);

        Assert.False(ladder[^3].LastResort);   // WavPack
        Assert.True(ladder[^2].LastResort);    // AAC-LC
        Assert.True(ladder[^1].LastResort);    // MP3
    }

    [Theory]
    // Measured ceilings. ffmpeg's TrueHD encoder errors above 6 channels, E-AC-3 and DTS quietly
    // emit 5.0 when asked for 12, and ALAC emits 6.1 — so none of them may claim more than this.
    [InlineData("Dolby TrueHD", 6)]
    [InlineData("E-AC-3 (DD+)", 6)]
    [InlineData("DTS", 6)]
    [InlineData("FLAC", 8)]
    [InlineData("ALAC", 8)]
    [InlineData("Opus", 8)]
    [InlineData("AAC-LC (MP4)", 6)]
    [InlineData("MP3", 2)]
    public void DeclaredChannelCeilingsMatchWhatTheEncodersActuallyProduce(string label, int expected)
    {
        EncodeTarget rung = Assert.Single(CodecLadder.Rungs, r => r.Label == label);
        Assert.Equal(expected, rung.MaxChannels);
    }

    [Fact]
    public void WavPackIsTheOnlyRungThatCanCarryAFullImmersiveMix()
    {
        // This is why the ladder does not simply stop at its first rung for a 12-channel source.
        var capable = CodecLadder.Rungs.Where(r => r.MaxChannels >= 12).Select(r => r.Label).ToList();

        Assert.Equal(["WavPack"], capable);
    }

    [Fact]
    public void ExperimentalEncodersArePassedTheStrictFlagTheyNeed()
    {
        // Without -strict -2 these encoders refuse to run at all.
        foreach (string label in (string[])["Dolby TrueHD", "DTS"])
        {
            EncodeTarget rung = Assert.Single(CodecLadder.Rungs, r => r.Label == label);
            Assert.Contains("-strict", rung.ExtraArguments);
            Assert.Contains("-2", rung.ExtraArguments);
        }
    }

    [Fact]
    public void LossyRungsReceiveTheScaledBitrate()
    {
        IReadOnlyList<EncodeTarget> ladder = CodecLadder.For(6, 320, ffmpeg: null);

        EncodeTarget eac3 = Assert.Single(ladder, r => r.Label == "E-AC-3 (DD+)");
        Assert.Contains("640k", eac3.ExtraArguments);   // 320 x2 for multichannel

        EncodeTarget mp3 = Assert.Single(ladder, r => r.Label == "MP3");
        Assert.Contains("320k", mp3.ExtraArguments);
    }

    [Fact]
    public void LosslessRungsAreNotGivenABitrate()
    {
        IReadOnlyList<EncodeTarget> ladder = CodecLadder.For(2, 320, ffmpeg: null);

        foreach (string label in (string[])["FLAC", "ALAC", "WavPack"])
        {
            EncodeTarget rung = Assert.Single(ladder, r => r.Label == label);
            Assert.DoesNotContain("-b:a", rung.ExtraArguments);
        }
    }

    [Fact]
    public void DownmixingIsWarnedAboutAndExplained()
    {
        EncodeTarget trueHd = Assert.Single(CodecLadder.Rungs, r => r.Label == "Dolby TrueHD");

        Assert.True(CodecLadder.NeedsWarning(trueHd, sourceChannels: 12, fellBack: false));

        string detail = CodecLadder.DescribeDegradation(trueHd, sourceChannels: 12, attemptIndex: 0);
        Assert.Contains("12", detail);
        Assert.Contains("6", detail);
    }

    [Fact]
    public void AFirstChoiceThatKeepsEveryChannelIsNotAWarning()
    {
        // WavPack at 12 channels: no fallback, no downmix, no last resort.
        EncodeTarget wavPack = Assert.Single(CodecLadder.Rungs, r => r.Label == "WavPack");

        Assert.False(CodecLadder.NeedsWarning(wavPack, sourceChannels: 12, fellBack: false));
        Assert.Empty(CodecLadder.DescribeDegradation(wavPack, sourceChannels: 12, attemptIndex: 0));
    }

    [Fact]
    public void FallingBackIsAlwaysWorthAWarning()
    {
        EncodeTarget flac = Assert.Single(CodecLadder.Rungs, r => r.Label == "FLAC");

        Assert.True(CodecLadder.NeedsWarning(flac, sourceChannels: 6, fellBack: true));
        Assert.NotEmpty(CodecLadder.DescribeDegradation(flac, sourceChannels: 6, attemptIndex: 3));
    }
}
