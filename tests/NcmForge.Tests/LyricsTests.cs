using System.Text;
using NcmForge.Core.Lyrics;
using Xunit;

namespace NcmForge.Tests;

/// <summary>Parsing of NetEase's hybrid JSON + LRC sidecar files.</summary>
public sealed class LyricsTests
{
    /// <summary>The shape NetEase actually writes: JSON credits, then ordinary LRC lines.</summary>
    private const string HybridSample = """
        {"t":0,"c":[{"tx":"作词: "},{"tx":"Billie Joe Armstrong"}]}
        {"t":1000,"c":[{"tx":"作曲: "},{"tx":"Green Day"}]}
        [00:17.640]Do you know what's worth fighting for'
        [00:23.640]When it's not worth dying for?
        """;

    private const string PlainSample = """
        [00:05.00]纯音乐，请欣赏
        """;

    [Fact]
    public void JsonCreditLinesBecomeTimedLyricLines()
    {
        LyricDocument document = LyricDocument.Parse(HybridSample);

        Assert.Equal(4, document.Lines.Count);
        Assert.Equal("作词: Billie Joe Armstrong", document.Lines[0].Text);
        Assert.Equal(TimeSpan.Zero, document.Lines[0].Timestamp);
        Assert.Equal("作曲: Green Day", document.Lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), document.Lines[1].Timestamp);
    }

    [Fact]
    public void RenderedLrcIsStandard()
    {
        string lrc = LyricDocument.Parse(HybridSample).ToLrc();
        string[] lines = lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("[0:00.000]作词: Billie Joe Armstrong", lines[0]);
        Assert.Equal("[0:01.000]作曲: Green Day", lines[1]);
        Assert.Equal("[0:17.640]Do you know what's worth fighting for'", lines[2]);
    }

    [Fact]
    public void PlainRenderingDropsTimestamps()
    {
        string plain = LyricDocument.Parse(HybridSample).ToPlainText();

        Assert.DoesNotContain("[0:00.000]", plain);
        Assert.Contains("作词: Billie Joe Armstrong", plain);
    }

    [Fact]
    public void PlainLrcWithoutCreditsStillParses()
    {
        LyricDocument document = LyricDocument.Parse(PlainSample);

        Assert.Single(document.Lines);
        Assert.Equal("纯音乐，请欣赏", document.Lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(5), document.Lines[0].Timestamp);
    }

    [Fact]
    public void MultipleTimestampsOnOneLineProduceOneEntryEach()
    {
        LyricDocument document = LyricDocument.Parse("[00:10.00][01:20.00]chorus");

        Assert.Equal(2, document.Lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), document.Lines[0].Timestamp);
        Assert.Equal(TimeSpan.FromSeconds(80), document.Lines[1].Timestamp);
        Assert.All(document.Lines, l => Assert.Equal("chorus", l.Text));
    }

    [Fact]
    public void EnhancedWordTimingsAreStripped()
    {
        LyricDocument document = LyricDocument.Parse("[00:10.00]<00:10.00>Hello <00:11.00>world");

        Assert.Single(document.Lines);
        Assert.Equal("Hello world", document.Lines[0].Text);
    }

    [Fact]
    public void MetadataTagsAndBlankLinesAreIgnored()
    {
        LyricDocument document = LyricDocument.Parse("""
            [ti:Song]
            [ar:Artist]
            [offset:500]

            [00:01.00]first
            """);

        Assert.Single(document.Lines);
        Assert.Equal("first", document.Lines[0].Text);
    }

    [Fact]
    public void LinesAreSortedByTimestamp()
    {
        LyricDocument document = LyricDocument.Parse("[00:30.00]late\n[00:10.00]early");

        Assert.Equal("early", document.Lines[0].Text);
        Assert.Equal("late", document.Lines[1].Text);
    }

    [Fact]
    public void MalformedJsonLineIsSkippedNotThrown()
    {
        LyricDocument document = LyricDocument.Parse("{\"t\": not json\n[00:01.00]ok");

        Assert.Single(document.Lines);
        Assert.Equal("ok", document.Lines[0].Text);
    }

    [Fact]
    public void EmptyInputYieldsEmptyDocument()
    {
        Assert.True(LyricDocument.Parse(string.Empty).IsEmpty);
        Assert.True(LyricDocument.Parse("   \n\n  ").IsEmpty);
    }

    // ---------- encoding ----------

    [Fact]
    public void Utf8WithAndWithoutBomDecode()
    {
        byte[] plain = Encoding.UTF8.GetBytes("纯音乐");
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. plain];

        Assert.Equal("纯音乐", LyricLoader.Decode(plain));
        Assert.Equal("纯音乐", LyricLoader.Decode(withBom));
    }

    [Fact]
    public void Utf16Decodes()
    {
        byte[] littleEndian = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("歌词")];
        byte[] bigEndian = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("歌词")];

        Assert.Equal("歌词", LyricLoader.Decode(littleEndian));
        Assert.Equal("歌词", LyricLoader.Decode(bigEndian));
    }

    [Fact]
    public void GbkDecodedWhenNotValidUtf8()
    {
        // GB18030 bytes for 歌词; invalid as UTF-8, so the loader must fall through to the code page.
        byte[] gbk = [0xB8, 0xE8, 0xB4, 0xCA];

        string decoded = LyricLoader.Decode(gbk);

        Assert.False(string.IsNullOrWhiteSpace(decoded));
        Assert.DoesNotContain('\uFFFD', decoded);
    }

    // ---------- sidecar discovery ----------

    [Fact]
    public void SidecarIsFoundByExactName()
    {
        string directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "Song.ncm"), "x");
            File.WriteAllText(Path.Combine(directory, "Song.lrc"), "[00:01.00]hi");

            Assert.Equal(
                Path.Combine(directory, "Song.lrc"),
                LyricLoader.FindSidecar(Path.Combine(directory, "Song.ncm"), "Song"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SidecarIsFoundByTitleWhenNamesDiffer()
    {
        string directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "Artist - 21 Guns.ncm"), "x");
            File.WriteAllText(Path.Combine(directory, "Green Day - 21 Guns.lrc"), "[00:01.00]hi");

            string? found = LyricLoader.FindSidecar(Path.Combine(directory, "Artist - 21 Guns.ncm"), "21 Guns");

            Assert.NotNull(found);
            Assert.EndsWith("Green Day - 21 Guns.lrc", found);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void NoSidecarReturnsNull()
    {
        string directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "Song.ncm"), "x");
            Assert.Null(LyricLoader.FindSidecar(Path.Combine(directory, "Song.ncm"), "Song"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTempDirectory()
    {
        // Created beside the test binaries rather than under the system temp path, which is not
        // writable in every sandboxed environment.
        string path = Path.Combine(AppContext.BaseDirectory, "lyrics-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
