using System.Text;
using NcmForge.Core.Audio;
using NcmForge.Core.Audio.Mp4;
using NcmForge.Core.Ncm;
using NcmForge.Core.Tagging;
using Xunit;

namespace NcmForge.Tests;

/// <summary>
/// Covers the MP4 tag rewrite.
/// </summary>
/// <remarks>
/// The risky part is not writing atoms but keeping the file addressable: growing the movie box
/// shifts every byte after it, so the chunk offset tables must move by the same amount. A mistake
/// there yields a file that still looks well-formed while every sample points at the wrong bytes,
/// so these tests decode the audio back out and compare it.
/// </remarks>
public sealed class Mp4TaggingTests
{
    private static byte[][] Samples()
    {
        var random = new Random(7);
        var samples = new byte[5][];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = new byte[97 + i * 13];
            random.NextBytes(samples[i]);
        }

        return samples;
    }

    private static TrackTags Tags(bool withCoverAndLyrics = true) => new()
    {
        Title = "测试标题 Title",
        Artist = "Artist",
        Album = "Album",
        TrackNumber = 27,
        Year = 2024,
        Comment = "written by NcmForge",
        Lyrics = "[0:01.000]第一行\n[0:05.000]second line\n",
        Cover = withCoverAndLyrics ? new CoverArt(TestImages.Jpeg(600, 600), ImageFormat.Jpeg, "test") : null,
    };

    [Fact]
    public void AudioSamplesSurviveTheRewrite()
    {
        byte[][] original = Samples();
        byte[] mp4 = TestImages.Mp4("av3a", 12, 44100, 16, original);

        using var source = new MemoryStream(mp4);
        using var tagged = new MemoryStream();
        Mp4TagResult result = Mp4TagWriter.WriteTagged(source, tagged, Tags());

        Assert.True(result.Tagged);
        Assert.Contains("©nam", result.Atoms);
        Assert.Contains("covr", result.Atoms);
        Assert.Contains("©lyr", result.Atoms);

        // The movie box grew, so offsets must have been shifted.
        Assert.True(result.OffsetDelta > 0, "the moov box should have grown");

        using var output = new MemoryStream(tagged.ToArray());
        using var extracted = new MemoryStream();
        Mp4ExtractionResult? extraction = Mp4SampleExtractor.Extract(output, extracted);

        Assert.NotNull(extraction);
        Assert.Equal(original.Sum(s => s.Length), extraction!.ByteCount);

        // Byte-for-byte equality proves the rewritten chunk offsets still address the right data.
        byte[] expected = [.. original[0], .. original[1], .. original[2], .. original[3], .. original[4]];
        Assert.Equal(expected, extracted.ToArray());
    }

    [Fact]
    public void TrackMetadataIsUnchanged()
    {
        byte[] mp4 = TestImages.Mp4("av3a", 12, 44100, 16, Samples());

        using var source = new MemoryStream(mp4);
        using var tagged = new MemoryStream();
        Mp4TagWriter.WriteTagged(source, tagged, Tags());

        Mp4AudioTrack? track = Mp4Probe.Probe(new MemoryStream(tagged.ToArray()));

        Assert.NotNull(track);
        Assert.Equal("av3a", track!.CodecFourCc);
        Assert.Equal(12, track.Channels);
        Assert.Equal(44100, track.SampleRate);
    }

    [Fact]
    public void WrittenAtomsCarryTheValues()
    {
        byte[] mp4 = TestImages.Mp4("av3a", 2, 44100, 16, Samples());

        using var source = new MemoryStream(mp4);
        using var tagged = new MemoryStream();
        Mp4TagWriter.WriteTagged(source, tagged, Tags());

        byte[] result = tagged.ToArray();

        Assert.Contains("udta"u8.ToArray(), result);
        Assert.Contains("ilst"u8.ToArray(), result);
        Assert.Contains("mdir"u8.ToArray(), result);

        // Atom names use the Macintosh copyright sign, so they are matched as raw bytes.
        Assert.Contains(new byte[] { 0xA9, (byte)'n', (byte)'a', (byte)'m' }, result);
        Assert.Contains(new byte[] { 0xA9, (byte)'l', (byte)'y', (byte)'r' }, result);
        Assert.Contains(new byte[] { (byte)'c', (byte)'o', (byte)'v', (byte)'r' }, result);

        Assert.Contains(Encoding.UTF8.GetBytes("测试标题 Title"), result);
        Assert.Contains(Encoding.UTF8.GetBytes("[0:01.000]第一行"), result);
    }

    [Fact]
    public void RewritingTwiceReplacesRatherThanDuplicates()
    {
        byte[] mp4 = TestImages.Mp4("av3a", 2, 44100, 16, Samples());

        using var first = new MemoryStream();
        Mp4TagWriter.WriteTagged(new MemoryStream(mp4), first, Tags());

        using var second = new MemoryStream();
        Mp4TagWriter.WriteTagged(new MemoryStream(first.ToArray()), second, Tags());

        byte[] result = second.ToArray();
        Assert.Equal(1, CountOccurrences(result, "udta"u8.ToArray()));

        // The audio must still be intact after a second pass over an already-tagged file.
        using var extracted = new MemoryStream();
        Mp4ExtractionResult? extraction = Mp4SampleExtractor.Extract(new MemoryStream(result), extracted);
        Assert.NotNull(extraction);
        Assert.Equal(Samples().Sum(s => s.Length), extraction!.ByteCount);
    }

    [Fact]
    public void AlreadyTaggedFileKeepsItsAudio()
    {
        byte[][] original = Samples();
        byte[] mp4 = TestImages.Mp4("av3a", 2, 44100, 16, original);

        using var first = new MemoryStream();
        Mp4TagWriter.WriteTagged(new MemoryStream(mp4), first, Tags());

        using var extracted = new MemoryStream();
        Mp4SampleExtractor.Extract(new MemoryStream(first.ToArray()), extracted);

        Assert.Equal([.. original[0], .. original[1], .. original[2], .. original[3], .. original[4]], extracted.ToArray());
    }

    [Fact]
    public void FileWithoutMovieBoxIsRejected()
    {
        var notMp4 = new byte[64];
        Encoding.ASCII.GetBytes("ftypisom").CopyTo(notMp4, 4);

        Assert.Throws<InvalidDataException>(() =>
        {
            using var output = new MemoryStream();
            Mp4TagWriter.WriteTagged(new MemoryStream(notMp4), output, Tags());
        });
    }

    [Fact]
    public void EmptyTagsLeaveTheFileUntouched()
    {
        byte[] mp4 = TestImages.Mp4("av3a", 2, 44100, 16, Samples());

        using var source = new MemoryStream(mp4);
        using var tagged = new MemoryStream();
        Mp4TagResult result = Mp4TagWriter.WriteTagged(source, tagged, new TrackTags());

        Assert.False(result.Tagged);
        Assert.Empty(result.Atoms);
        Assert.Equal(0, result.OffsetDelta);
        Assert.Equal(mp4, tagged.ToArray());
    }

    [Fact]
    public void ExistingUserDataIsPreservedWhenNoTagsAreWritten()
    {
        byte[] plain = TestImages.Mp4("av3a", 2, 44100, 16, Samples());

        // Give the source its own user data, as a real download would have.
        using var first = new MemoryStream();
        Mp4TagWriter.WriteTagged(new MemoryStream(plain), first, new TrackTags { Title = "Original" });
        byte[] withUserData = first.ToArray();
        Assert.Contains("Original"u8.ToArray(), withUserData);

        // Asking for no tags must not strip metadata the file already carried.
        using var second = new MemoryStream();
        Mp4TagResult result = Mp4TagWriter.WriteTagged(new MemoryStream(withUserData), second, new TrackTags());

        Assert.False(result.Tagged);
        Assert.Equal(withUserData.Length, second.Length);
        Assert.Equal(withUserData, second.ToArray());
        Assert.Contains("Original"u8.ToArray(), second.ToArray());
    }

    [Fact]
    public void AtomsTheWriterDoesNotSetArePreserved()
    {
        byte[] mp4 = TestImages.Mp4("av3a", 2, 44100, 16, Samples());

        // A source that already carries a track number, as NetEase's own downloads do.
        using var first = new MemoryStream();
        Mp4TagWriter.WriteTagged(new MemoryStream(mp4), first, new TrackTags { TrackNumber = 27 });
        Assert.Contains(new byte[] { (byte)'t', (byte)'r', (byte)'k', (byte)'n' }, first.ToArray());

        // Writing only a title must not remove it.
        using var second = new MemoryStream();
        Mp4TagResult result = Mp4TagWriter.WriteTagged(new MemoryStream(first.ToArray()), second, new TrackTags { Title = "New" });

        byte[] output = second.ToArray();
        Assert.Contains("trkn"u8.ToArray(), output);
        Assert.Contains(Encoding.UTF8.GetBytes("New"), output);

        byte[] extractedAudio = ExtractAudio(output);
        Assert.Equal(Samples().Sum(s => s.Length), extractedAudio.Length);
    }

    [Fact]
    public void SettingAnAtomReplacesRatherThanDuplicatesIt()
    {
        byte[] mp4 = TestImages.Mp4("av3a", 2, 44100, 16, Samples());

        using var first = new MemoryStream();
        Mp4TagWriter.WriteTagged(new MemoryStream(mp4), first, new TrackTags { Album = "First Album" });

        using var second = new MemoryStream();
        Mp4TagWriter.WriteTagged(new MemoryStream(first.ToArray()), second, new TrackTags { Album = "Second Album" });

        byte[] output = second.ToArray();
        Assert.DoesNotContain(Encoding.UTF8.GetBytes("First Album"), output);
        Assert.Contains(Encoding.UTF8.GetBytes("Second Album"), output);
    }

    private static byte[] ExtractAudio(byte[] mp4)
    {
        using var extracted = new MemoryStream();
        Mp4SampleExtractor.Extract(new MemoryStream(mp4), extracted);
        return extracted.ToArray();
    }

    private static int CountOccurrences(byte[] haystack, byte[] needle)
    {
        var count = 0;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                count++;
            }
        }

        return count;
    }
}
