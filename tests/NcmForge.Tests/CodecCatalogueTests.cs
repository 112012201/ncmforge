using NcmForge.Core.Pipeline;
using Xunit;

namespace NcmForge.Tests;

/// <summary>
/// Coverage for the codec catalogue a user chooses an output format from.
/// </summary>
/// <remarks>
/// The channel ceilings asserted here are measurements against a real source, not the capability the
/// containers claim. The two differ sharply — WavPack's format allows 256 channels and its ffmpeg
/// encoder stops at 64; TrueHD's format allows 16 and its encoder stops at 6 — and a chooser that
/// printed the first number would promise what the tool cannot deliver.
/// </remarks>
public sealed class CodecCatalogueTests
{
    [Theory]
    [InlineData("wavpack", "WavPack")]
    [InlineData("truehd", "Dolby TrueHD")]
    [InlineData("eac3", "E-AC-3 (DD+)")]
    [InlineData("dts", "DTS")]
    [InlineData("flac", "FLAC")]
    [InlineData("alac", "ALAC")]
    [InlineData("opus", "Opus")]
    [InlineData("aac", "AAC-LC (MP4)")]
    [InlineData("mp3", "MP3")]
    public void EveryAdvertisedCodecCanBeFoundByItsKey(string key, string label)
    {
        EncodeTarget? rung = CodecLadder.Find(key);

        Assert.NotNull(rung);
        Assert.Equal(label, rung!.Label);
        Assert.True(CodecLadder.IsCodecKey(key));
    }

    [Theory]
    [InlineData("WavPack")]
    [InlineData("wavpack ")]
    [InlineData(" WAVPACK")]
    public void LookupIgnoresCaseAndSurroundingSpace(string key)
        => Assert.Equal("wavpack", CodecLadder.Find(key)!.Key);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("flac2")]
    [InlineData("dolby")]
    [InlineData(null)]
    public void AnUnknownKeyIsNotACodec(string? key)
    {
        Assert.Null(CodecLadder.Find(key));
        Assert.False(CodecLadder.IsCodecKey(key));
    }

    [Fact]
    public void EveryKeyIsDistinct()
    {
        IReadOnlyList<string> keys = [.. CodecLadder.Rungs.Select(r => r.Key)];

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    // Measured against a real source. WavPack's own format allows 256 channels and its ffmpeg
    // encoder stops here; the label must say what the encoder does, not what the format permits.
    [InlineData("wavpack", 64)]
    [InlineData("truehd", 6)]
    [InlineData("eac3", 6)]
    [InlineData("dts", 6)]
    [InlineData("flac", 8)]
    [InlineData("alac", 8)]
    [InlineData("opus", 8)]
    [InlineData("aac", 6)]
    [InlineData("mp3", 2)]
    public void ChannelCeilingsAreTheMeasuredOnes(string key, int expected)
        => Assert.Equal(expected, CodecLadder.Find(key)!.MaxChannels);

    [Fact]
    public void ADescriptionCarriesEverythingAChooserNeeds()
    {
        string described = CodecLadder.Describe(CodecLadder.Find("wavpack")!);

        Assert.Contains("WavPack", described, StringComparison.Ordinal);
        Assert.Contains("无损", described, StringComparison.Ordinal);
        Assert.Contains("64 声道", described, StringComparison.Ordinal);
        // Compatibility is the part that tells someone whether their player will open it.
        Assert.Contains("VLC", described, StringComparison.Ordinal);
    }

    [Fact]
    public void ALossyFormatSaysSo()
        => Assert.Contains("有损", CodecLadder.Describe(CodecLadder.Find("mp3")!), StringComparison.Ordinal);

    [Fact]
    public void TheDisplayedChannelCountIsCappedAt255()
    {
        // Nothing in the catalogue reaches the cap today, so this is asserted on a synthetic rung:
        // the point is that a future high-channel encoder cannot advertise an unusable figure.
        var wide = new EncodeTarget("wide", "Imaginary", "imaginary", ".x", [], 4096, true, false, false, "nowhere", "test");

        Assert.Equal(255, CodecLadder.MaxDisplayedChannels);
        Assert.Contains("最多 255 声道", CodecLadder.Describe(wide), StringComparison.Ordinal);
    }

    [Fact]
    public void AskingForOneCodecYieldsExactlyThatCodec()
    {
        IReadOnlyList<EncodeTarget> ladder = CodecLadder.For(2, 320, ffmpeg: null, onlyKey: "opus");

        EncodeTarget only = Assert.Single(ladder);
        Assert.Equal("opus", only.Key);
    }

    [Fact]
    public void AskingForAnUnknownCodecYieldsNothingRatherThanTheWholeLadder()
    {
        // Falling back to every codec here would silently ignore the user's choice.
        IReadOnlyList<EncodeTarget> ladder = CodecLadder.For(2, 320, ffmpeg: null, onlyKey: "not-a-codec");

        Assert.Empty(ladder);
    }

    [Fact]
    public void AnExplicitCodecStillReceivesTheCallersBitrate()
    {
        IReadOnlyList<EncodeTarget> ladder = CodecLadder.For(2, 256, ffmpeg: null, onlyKey: "aac");

        EncodeTarget aac = Assert.Single(ladder);
        Assert.Contains("512k", aac.ExtraArguments);
    }

    [Fact]
    public void EveryCodecInTheCatalogueHasACompatibilityNote()
    {
        // The chooser shows this text; an empty one would leave the user guessing.
        foreach (EncodeTarget rung in CodecLadder.Rungs)
        {
            Assert.False(string.IsNullOrWhiteSpace(rung.Compatibility), $"{rung.Key} has no compatibility note");
            Assert.False(string.IsNullOrWhiteSpace(rung.Note), $"{rung.Key} has no note");
            Assert.False(string.IsNullOrWhiteSpace(rung.Label), $"{rung.Key} has no label");
        }
    }
}
