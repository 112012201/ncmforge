using System.Buffers.Binary;
using System.Text;
using NcmForge.Core.Audio;
using NcmForge.Core.Audio.Flac;
using NcmForge.Core.Audio.Mp4;
using NcmForge.Core.Ncm;
using NcmForge.Core.Tools;
using Xunit;

namespace NcmForge.Tests;

/// <summary>Signature detection: the only trustworthy statement about what a payload contains.</summary>
public sealed class DetectionTests
{
    [Theory]
    [InlineData("fLaC", AudioFormat.Flac)]
    [InlineData("OggS", AudioFormat.Ogg)]
    [InlineData("RIFF", AudioFormat.Wav)]
    [InlineData("ID3\u0004", AudioFormat.Mp3)]
    public void ContainerSignaturesAreRecognised(string prefix, AudioFormat expected)
    {
        var data = new byte[64];
        Encoding.ASCII.GetBytes(prefix).CopyTo(data, 0);

        Assert.Equal(expected, AudioFormatDetector.Detect(data, out _));
    }

    [Fact]
    public void Mp4IsRecognisedByTheFtypBox()
    {
        var data = new byte[64];
        data[3] = 0x1C;
        Encoding.ASCII.GetBytes("ftyp").CopyTo(data, 4);
        Encoding.ASCII.GetBytes("isom").CopyTo(data, 8);

        Assert.Equal(AudioFormat.Mp4, AudioFormatDetector.Detect(data, out _));
    }

    [Theory]
    [InlineData(16, AudioFormat.Eac3)]
    [InlineData(14, AudioFormat.Eac3)]
    [InlineData(11, AudioFormat.Eac3)]
    [InlineData(8, AudioFormat.Ac3)]
    [InlineData(6, AudioFormat.Ac3)]
    public void DolbySyncWordIsSplitByBsid(int bsid, AudioFormat expected)
    {
        // syncword(16), crc1(16), fscod(2), frmsizecod(6), bsid(5) -> bsid occupies bits 40..44.
        var data = new byte[16];
        data[0] = 0x0B;
        data[1] = 0x77;
        data[5] = (byte)(bsid << 3);

        AudioFormat format = AudioFormatDetector.Detect(data, out string? detail);

        Assert.Equal(expected, format);
        Assert.Contains($"bsid={bsid}", detail);
    }

    [Fact]
    public void AdtsIsNotMistakenForMpegAudio()
    {
        var data = new byte[16];
        data[0] = 0xFF;
        data[1] = 0xF1; // ADTS, MPEG-4, no CRC

        Assert.Equal(AudioFormat.Aac, AudioFormatDetector.Detect(data, out _));
    }

    [Fact]
    public void NoiseIsUnknown()
    {
        var data = new byte[64];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 3 + 1);
        }

        Assert.Equal(AudioFormat.Unknown, AudioFormatDetector.Detect(data, out _));
    }

    // ---------- images ----------

    [Fact]
    public void JpegDimensionsAreRead()
    {
        ImageInfo? info = ImageProbe.Read(TestImages.Jpeg(1920, 1080), ImageFormat.Jpeg);

        Assert.NotNull(info);
        Assert.Equal(1920, info!.Value.Width);
        Assert.Equal(1080, info.Value.Height);
        Assert.Equal(8, info.Value.BitDepth);
        Assert.Equal(3, info.Value.ColorComponents);
        Assert.Equal(24, info.Value.DepthBits);
    }

    [Fact]
    public void PngDimensionsAreRead()
    {
        ImageInfo? info = ImageProbe.Read(TestImages.Png(800, 600), ImageFormat.Png);

        Assert.NotNull(info);
        Assert.Equal(800, info!.Value.Width);
        Assert.Equal(600, info.Value.Height);
    }

    [Fact]
    public void JpegEndMarkerBoundsTheImage()
    {
        byte[] jpeg = TestImages.Jpeg(64, 64);
        var padded = new byte[jpeg.Length + 37];
        jpeg.CopyTo(padded, 0);

        // Trailing container padding must not be tagged onto the image.
        Assert.Equal(jpeg.Length, ImageSignature.FindImageLength(padded, ImageFormat.Jpeg));
    }

    [Fact]
    public void ImageMimeTypesAndExtensionsAreConsistent()
    {
        Assert.Equal("image/jpeg", ImageSignature.MimeType(ImageFormat.Jpeg));
        Assert.Equal(".png", ImageSignature.Extension(ImageFormat.Png));
        Assert.Equal(ImageFormat.Unknown, ImageSignature.Detect(new byte[8]));
    }

    // ---------- FLAC ----------

    [Fact]
    public void FlacStreamInfoIsDecodedFromPackedFields()
    {
        byte[] flac = TestImages.Flac(96000, 8, 24, 500_000, []);

        FlacStreamInfo? info = FlacProbe.Read(new MemoryStream(flac));

        Assert.NotNull(info);
        Assert.Equal(96000, info!.SampleRate);
        Assert.Equal(8, info.Channels);
        Assert.Equal(24, info.BitsPerSample);
        Assert.Equal(500_000, info.TotalSamples);
        Assert.Equal(TimeSpan.FromSeconds(500_000.0 / 96000), info.Duration);
    }

    // ---------- MP4 ----------

    [Fact]
    public void Mp4ProbeReadsTheSampleEntry()
    {
        byte[] mp4 = TestImages.Mp4("av3a", channels: 12, sampleRate: 44100, sampleSize: 16, samples: [new byte[32], new byte[64]]);

        Mp4AudioTrack? track = Mp4Probe.Probe(new MemoryStream(mp4));

        Assert.NotNull(track);
        Assert.Equal("av3a", track!.CodecFourCc);
        Assert.Equal(12, track.Channels);
        Assert.Equal(44100, track.SampleRate);
        Assert.Equal(16, track.SampleSize);
        Assert.Contains("AVS3-P3", track.CodecName);
    }

    [Fact]
    public void Mp4ProbeReportsUnknownCodecsByName()
    {
        byte[] mp4 = TestImages.Mp4("zzzz", channels: 2, sampleRate: 48000, sampleSize: 16, samples: [new byte[16]]);

        Mp4AudioTrack? track = Mp4Probe.Probe(new MemoryStream(mp4));

        Assert.NotNull(track);
        Assert.Equal("unknown (zzzz)", track!.CodecName);
    }

    [Fact]
    public void Mp4SampleExtractorConcatenatesSamplesInOrder()
    {
        byte[] first = [1, 2, 3, 4, 5];
        byte[] second = [6, 7, 8];
        byte[] third = [9];
        byte[] mp4 = TestImages.Mp4("av3a", 2, 44100, 16, [first, second, third]);

        using var output = new MemoryStream();
        Mp4ExtractionResult? result = Mp4SampleExtractor.Extract(new MemoryStream(mp4), output);

        Assert.NotNull(result);
        Assert.Equal(3, result!.SampleCount);
        Assert.Equal(9, result.ByteCount);
        Assert.Equal([.. first, .. second, .. third], output.ToArray());
    }

    [Fact]
    public void NonMp4StreamsYieldNoTrack()
    {
        Assert.Null(Mp4Probe.Probe(new MemoryStream(Encoding.ASCII.GetBytes("this is not an mp4 file at all"))));
        Assert.Null(Mp4Probe.Probe(new MemoryStream(new byte[8])));
    }

    // ---------- ffmpeg capability matching ----------

    [Theory]
    // The name NetEase's own patched ffmpeg registers its Audio Vivid decoder under.
    [InlineData("av3a", "avs3p3_3d_audio", true)]
    [InlineData("av3a", "av3a", true)]
    [InlineData("av3a", "avs3a", true)]
    [InlineData("av3a", "aac", false)]
    [InlineData("ec-3", "eac3", true)]
    [InlineData("ec-3", "ac3", false)]
    [InlineData("ac-3", "ac3", true)]
    [InlineData("mp4a", "aac_latm", true)]
    [InlineData("fLaC", "flac", true)]
    [InlineData("zzzz", "zzzz_custom", true)]
    public void DecoderMatchingAcceptsUnpredictableNames(string fourCc, string decoderName, bool expected)
    {
        // Exact-name matching would reject avs3p3_3d_audio and refuse work the build can do.
        Assert.Equal(expected, FfmpegTool.MatchesAnyCandidate([decoderName], fourCc));
    }

    [Fact]
    public void DecoderMatchingIgnoresUnrelatedDecoders()
    {
        string[] decoders = ["mp3", "flac", "eac3", "ac3", "aac"];

        Assert.False(FfmpegTool.MatchesAnyCandidate(decoders, "av3a"));
        Assert.True(FfmpegTool.MatchesAnyCandidate(decoders, "ec-3"));
    }
}
