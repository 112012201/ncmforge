using NcmForge.Core.Pipeline;
using Xunit;

namespace NcmForge.Tests;

/// <summary>
/// Coverage for the list of formats a user is offered.
/// </summary>
/// <remarks>
/// The window, the command line and these tests all read <see cref="FormatCatalog"/>, so an entry
/// asserted here is the entry the chooser shows.
/// </remarks>
public sealed class FormatCatalogTests
{
    [Fact]
    public void TheListOpensWithAutomaticAndClosesWithACopy()
    {
        Assert.Null(FormatCatalog.All[0].CodecKey);
        Assert.Equal(OutputFormat.Compatible, FormatCatalog.All[0].Format);
        Assert.Contains("自动", FormatCatalog.All[0].Label, StringComparison.Ordinal);

        FormatOption last = FormatCatalog.All[^1];
        Assert.Equal(OutputFormat.LosslessCopy, last.Format);
        Assert.Null(last.CodecKey);
    }

    [Fact]
    public void EveryEncodableCodecExceptMp3AndFlacIsSelectable()
    {
        // MP3 and FLAC are deliberately absent as codec entries: the two remuxing modes above cover
        // them, and a plain codec entry would re-encode an MP3 into an MP3 for no gain.
        string[] expected = [.. CodecLadder.Rungs
            .Where(r => r.Key is not ("mp3" or "flac"))
            .Select(r => r.Key)];

        string[] offered = [.. FormatCatalog.All.Where(o => o.CodecKey is not null).Select(o => o.CodecKey!)];

        Assert.Equal(expected, offered);
        Assert.DoesNotContain("mp3", offered);
        Assert.DoesNotContain("flac", offered);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    public void TheRemuxingModesAreOfferedAsModes(string key)
    {
        // Their codec key is absent, but the mode is present — that is the point of the design.
        Assert.Null(FormatCatalog.ForCodec(key));
        Assert.Contains(FormatCatalog.All, o => o.Label.StartsWith(key.ToUpperInvariant(), StringComparison.Ordinal));
    }

    [Fact]
    public void EveryCodecEntryStatesWhatItPreservesAndWhatOpensIt()
    {
        foreach (FormatOption option in FormatCatalog.All.Where(o => o.CodecKey is not null))
        {
            Assert.True(
                option.Label.Contains("无损", StringComparison.Ordinal) || option.Label.Contains("有损", StringComparison.Ordinal),
                $"{option.CodecKey} does not say whether it is lossless");

            Assert.Contains("最多", option.Label, StringComparison.Ordinal);
            Assert.Contains("声道", option.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoEntryEverAdvertisesMoreThanTheDisplayCap()
    {
        foreach (FormatOption option in FormatCatalog.All.Where(o => o.CodecKey is not null))
        {
            // "最多 64 声道" is fine; "最多 256 声道" would not be.
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(option.Label, @"最多 (\d+) 声道"))
            {
                int shown = int.Parse(m.Groups[1].Value);
                Assert.True(shown <= CodecLadder.MaxDisplayedChannels, $"{option.CodecKey} advertises {shown} channels");
            }
        }
    }

    [Fact]
    public void TheAutomaticEntryNamesNoCodecSoTheLadderDecides()
        => Assert.Null(FormatCatalog.All[0].CodecKey);
}
