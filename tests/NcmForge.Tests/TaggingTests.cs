using System.Buffers.Binary;
using System.Text;
using NcmForge.Core.Audio;
using NcmForge.Core.Audio.Flac;
using NcmForge.Core.Ncm;
using NcmForge.Core.Tagging;
using Xunit;

namespace NcmForge.Tests;

/// <summary>Verifies the tag writers produce structures other tools can read.</summary>
public sealed class TaggingTests
{
    private static TrackTags SampleTags() => new()
    {
        Title = "环绕测试 Surround Test",
        Artist = "LumynX / DJ XRAY",
        Album = "MONTAGEM 486",
        TrackNumber = 3,
        TrackTotal = 12,
        Year = 2024,
        MusicId = 3316995512,
        Cover = new CoverArt(TestImages.Jpeg(600, 600), ImageFormat.Jpeg, "test"),
        Lyrics = "[0:00.000]作词: Test\n[0:17.640]Do you know\n",
        PlainLyrics = "作词: Test\nDo you know\n",
    };

    // ---------- ID3v2 ----------

    [Fact]
    public void Id3TagHasExpectedHeaderAndFrames()
    {
        byte[] tag = Id3v2Writer.BuildTag(SampleTags());

        Assert.Equal("ID3", Encoding.ASCII.GetString(tag, 0, 3));
        Assert.Equal(3, tag[3]); // ID3v2.3
        Assert.Equal(0, tag[4]);
        Assert.Equal(0, tag[5]); // no unsynchronisation, no extended header

        // Size is syncsafe: every byte below 0x80, and it must cover the rest of the tag.
        int size = ((tag[6] & 0x7F) << 21) | ((tag[7] & 0x7F) << 14) | ((tag[8] & 0x7F) << 7) | (tag[9] & 0x7F);
        Assert.Equal(tag.Length - 10, size);

        Dictionary<string, byte[]> frames = ParseFrames(tag);
        Assert.Contains("TIT2", frames.Keys);
        Assert.Contains("TPE1", frames.Keys);
        Assert.Contains("TALB", frames.Keys);
        Assert.Contains("TRCK", frames.Keys);
        Assert.Contains("TYER", frames.Keys);
        Assert.Contains("APIC", frames.Keys);

        Assert.Equal("环绕测试 Surround Test", ReadUtf16(frames["TIT2"]));
        Assert.Equal("3/12", ReadUtf16(frames["TRCK"]));
    }

    [Fact]
    public void ApicFrameCarriesMimeTypeAndImageBytes()
    {
        TrackTags tags = SampleTags();
        byte[] tag = Id3v2Writer.BuildTag(tags);
        byte[] apic = ParseFrames(tag)["APIC"];

        Assert.Equal(0x00, apic[0]); // Latin-1 encoding

        int mimeEnd = Array.IndexOf(apic, (byte)0, 1);
        Assert.Equal("image/jpeg", Encoding.ASCII.GetString(apic, 1, mimeEnd - 1));
        Assert.Equal(0x03, apic[mimeEnd + 1]); // front cover

        // encoding, mime, terminator, picture type, empty description terminator, then data.
        int dataStart = mimeEnd + 3;
        Assert.Equal(tags.Cover!.Data, apic[dataStart..]);
    }

    [Fact]
    public void TxxxFrameSeparatesDescriptionFromValue()
    {
        byte[] tag = Id3v2Writer.BuildTag(SampleTags());
        byte[] txxx = ParseFrames(tag)["TXXX"];

        Assert.Equal(0x01, txxx[0]); // UTF-16

        // BOM, description, double-NUL terminator, BOM, value.
        Assert.Equal(0xFF, txxx[1]);
        Assert.Equal(0xFE, txxx[2]);

        int cursor = 3;
        while (cursor + 1 < txxx.Length && !(txxx[cursor] == 0 && txxx[cursor + 1] == 0))
        {
            cursor += 2;
        }

        string description = Encoding.Unicode.GetString(txxx, 3, cursor - 3);
        Assert.Equal("NCMFORGE_MUSICID", description);

        int valueStart = cursor + 2;
        Assert.Equal(0xFF, txxx[valueStart]);
        Assert.Equal(0xFE, txxx[valueStart + 1]);
        Assert.Equal("3316995512", Encoding.Unicode.GetString(txxx, valueStart + 2, txxx.Length - valueStart - 2));
    }

    [Fact]
    public void CommentFrameDescriptionIsBomPrefixed()
    {
        var tags = new TrackTags { Title = "T", Comment = "hello" };
        byte[] comm = ParseFrames(Id3v2Writer.BuildTag(tags))["COMM"];

        Assert.Equal(0x01, comm[0]);
        Assert.Equal("eng", Encoding.ASCII.GetString(comm, 1, 3));

        // A UTF-16 description must open with a BOM even when empty, or readers skip the frame.
        Assert.Equal(0xFF, comm[4]);
        Assert.Equal(0xFE, comm[5]);
        Assert.Equal(0x00, comm[6]);
        Assert.Equal(0x00, comm[7]);
    }

    [Fact]
    public void EmptyValuesProduceNoFrame()
    {
        byte[] tag = Id3v2Writer.BuildTag(new TrackTags { Title = "Only title" });
        Dictionary<string, byte[]> frames = ParseFrames(tag);

        Assert.Contains("TIT2", frames.Keys);
        Assert.DoesNotContain("TPE1", frames.Keys);
        Assert.DoesNotContain("APIC", frames.Keys);
    }

    [Fact]
    public void SkipExistingTagFindsTheAudioStart()
    {
        byte[] existing = Id3v2Writer.BuildTag(new TrackTags { Title = "Old" });
        var file = new byte[existing.Length + 100];
        existing.CopyTo(file, 0);

        using var stream = new MemoryStream(file);
        Assert.Equal(existing.Length, Id3v2Writer.SkipExistingTag(stream));
    }

    [Fact]
    public void SkipExistingTagIgnoresFilesWithoutATag()
    {
        var file = new byte[100];
        file[0] = 0xFF;
        file[1] = 0xFB;

        using var stream = new MemoryStream(file);
        Assert.Equal(0, Id3v2Writer.SkipExistingTag(stream));
    }

    [Fact]
    public void WriteTaggedReplacesAPreviousTag()
    {
        byte[] oldTag = Id3v2Writer.BuildTag(new TrackTags { Title = "Old" });
        byte[] audio = new byte[256];
        for (var i = 0; i < audio.Length; i++)
        {
            audio[i] = 0xFF;
        }

        var source = new byte[oldTag.Length + audio.Length];
        oldTag.CopyTo(source, 0);
        audio.CopyTo(source, oldTag.Length);

        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        Id3v2Writer.WriteTagged(input, output, new TrackTags { Title = "New" });

        // Exactly one tag, preceded by nothing, followed by the original audio bytes.
        Assert.Equal("ID3", Encoding.ASCII.GetString(output.ToArray(), 0, 3));
        Assert.Equal("New", ReadUtf16(ParseFrames(output.ToArray())["TIT2"]));
        Assert.Equal(audio, output.ToArray()[^audio.Length..]);
    }

    // ---------- FLAC ----------

    [Fact]
    public void FlacRewriteAddsVorbisCommentAndPicture()
    {
        byte[] frames = new byte[512];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = (byte)(i * 7);
        }

        byte[] flac = TestImages.Flac(48000, 6, 24, 96000, frames);

        using var input = new MemoryStream(flac);
        using var output = new MemoryStream();
        FlacTagWriter.WriteTagged(input, output, SampleTags());

        byte[] result = output.ToArray();
        Assert.Equal("fLaC", Encoding.ASCII.GetString(result, 0, 4));

        using var stream = new MemoryStream(result);
        IReadOnlyList<(byte Type, int Length, bool IsLast)> blocks = FlacTagWriter.ListBlocks(stream);

        Assert.Equal(0, blocks[0].Type); // STREAMINFO must stay first
        Assert.Contains(blocks, b => b.Type == 4); // VORBIS_COMMENT
        Assert.Contains(blocks, b => b.Type == 6); // PICTURE
        Assert.True(blocks[^1].IsLast, "the final metadata block must carry the last-block flag");
        Assert.Single(blocks, b => b.IsLast);

        // The coded frames must come through untouched.
        Assert.Equal(frames, result[^frames.Length..]);

        // STREAMINFO must survive the rewrite, or the file would be unplayable.
        FlacStreamInfo? info = FlacProbe.Read(new MemoryStream(result));
        Assert.NotNull(info);
        Assert.Equal(48000, info!.SampleRate);
        Assert.Equal(6, info.Channels);
        Assert.Equal(24, info.BitsPerSample);
        Assert.Equal(96000, info.TotalSamples);
    }

    [Fact]
    public void FlacRewriteReplacesAnExistingCommentBlock()
    {
        byte[] flac = TestImages.Flac(44100, 2, 16, 1000, new byte[64]);

        using var first = new MemoryStream();
        FlacTagWriter.WriteTagged(new MemoryStream(flac), first, new TrackTags { Title = "First" });

        using var second = new MemoryStream();
        FlacTagWriter.WriteTagged(new MemoryStream(first.ToArray()), second, new TrackTags { Title = "Second" });

        IReadOnlyList<(byte Type, int Length, bool IsLast)> blocks = FlacTagWriter.ListBlocks(new MemoryStream(second.ToArray()));
        Assert.Single(blocks, b => b.Type == 4);
    }

    [Fact]
    public void FlacDetectionRejectsNonFlacStreams()
    {
        Assert.True(FlacTagWriter.IsFlac(new MemoryStream(TestImages.Flac(44100, 2, 16, 100, []))));
        Assert.False(FlacTagWriter.IsFlac(new MemoryStream("ID3\u0003\u0000"u8.ToArray())));
        Assert.Null(FlacProbe.Read(new MemoryStream(new byte[100])));
    }

    [Fact]
    public void FlacRewriteRejectsAStreamWithoutStreamInfo()
    {
        // A chain whose first block is not STREAMINFO cannot be safely rewritten.
        byte[] bogus = [.. "fLaC"u8, 0x84, 0x00, 0x00, 0x04, 1, 2, 3, 4];

        Assert.Throws<InvalidDataException>(() =>
        {
            using var output = new MemoryStream();
            FlacTagWriter.WriteTagged(new MemoryStream(bogus), output, new TrackTags { Title = "x" });
        });
    }

    [Fact]
    public void UsltFrameIsWellFormed()
    {
        TrackTags tags = SampleTags();
        byte[] uslt = ParseFrames(Id3v2Writer.BuildTag(tags))["USLT"];

        Assert.Equal(0x01, uslt[0]);

        // The language is reported by readers as part of the tag name, so CJK text must mark it chi.
        Assert.Equal("chi", Encoding.ASCII.GetString(uslt, 1, 3));

        // Empty descriptor: BOM, terminator, then the value's own BOM.
        Assert.Equal(0xFF, uslt[4]);
        Assert.Equal(0xFE, uslt[5]);
        Assert.Equal(0x00, uslt[6]);
        Assert.Equal(0x00, uslt[7]);
        Assert.Equal(0xFF, uslt[8]);
        Assert.Equal(0xFE, uslt[9]);

        Assert.Equal(tags.Lyrics, Encoding.Unicode.GetString(uslt, 10, uslt.Length - 10));
    }

    [Fact]
    public void LyricsLanguageFallsBackToEnglishForLatinText()
    {
        byte[] uslt = ParseFrames(Id3v2Writer.BuildTag(new TrackTags { Lyrics = "[0:01.000]hello" }))["USLT"];
        Assert.Equal("eng", Encoding.ASCII.GetString(uslt, 1, 3));
    }

    [Fact]
    public void FlacVorbisCommentCarriesLyrics()
    {
        TrackTags tags = SampleTags();
        byte[] flac = TestImages.Flac(44100, 2, 16, 1000, new byte[64]);

        using var output = new MemoryStream();
        FlacTagWriter.WriteTagged(new MemoryStream(flac), output, tags);

        Dictionary<string, string> comments = ReadVorbisComments(output.ToArray());

        Assert.Equal(tags.Lyrics, comments["LYRICS"]);
        Assert.Equal(tags.PlainLyrics, comments["UNSYNCEDLYRICS"]);
        Assert.Equal(tags.Title, comments["TITLE"]);
    }

    // ---------- helpers ----------

    /// <summary>Reads the VORBIS_COMMENT block back out of a FLAC stream.</summary>
    private static Dictionary<string, string> ReadVorbisComments(byte[] flac)
    {
        var comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int cursor = 4;

        while (cursor + 4 <= flac.Length)
        {
            bool isLast = (flac[cursor] & 0x80) != 0;
            byte type = (byte)(flac[cursor] & 0x7F);
            int length = (flac[cursor + 1] << 16) | (flac[cursor + 2] << 8) | flac[cursor + 3];
            int payload = cursor + 4;

            if (type == 4)
            {
                int p = payload;
                int vendorLength = BinaryPrimitives.ReadInt32LittleEndian(flac.AsSpan(p, 4));
                p += 4 + vendorLength;

                int count = BinaryPrimitives.ReadInt32LittleEndian(flac.AsSpan(p, 4));
                p += 4;

                for (var i = 0; i < count; i++)
                {
                    int entryLength = BinaryPrimitives.ReadInt32LittleEndian(flac.AsSpan(p, 4));
                    p += 4;

                    string entry = Encoding.UTF8.GetString(flac, p, entryLength);
                    p += entryLength;

                    int equals = entry.IndexOf('=');
                    if (equals > 0)
                    {
                        comments[entry[..equals]] = entry[(equals + 1)..];
                    }
                }

                return comments;
            }

            cursor = payload + length;
            if (isLast)
            {
                break;
            }
        }

        return comments;
    }

    private static Dictionary<string, byte[]> ParseFrames(byte[] tag)
    {
        var frames = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int cursor = 10;

        while (cursor + 10 <= tag.Length)
        {
            string id = Encoding.ASCII.GetString(tag, cursor, 4);
            if (id[0] == '\0')
            {
                break;
            }

            int size = BinaryPrimitives.ReadInt32BigEndian(tag.AsSpan(cursor + 4, 4));
            if (size <= 0 || cursor + 10 + size > tag.Length)
            {
                break;
            }

            frames[id] = tag[(cursor + 10)..(cursor + 10 + size)];
            cursor += 10 + size;
        }

        return frames;
    }

    private static string ReadUtf16(byte[] payload)
    {
        Assert.Equal(0x01, payload[0]);
        return Encoding.Unicode.GetString(payload, 3, payload.Length - 3);
    }
}
