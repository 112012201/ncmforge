using NcmForge.Core.Audio;
using NcmForge.Core.Ncm;
using NcmForge.Core.Pipeline;
using Xunit;

namespace NcmForge.Tests;

/// <summary>
/// Coverage for the re-encode target selection.
/// </summary>
/// <remarks>
/// The rule under test is that a re-encode must not change the quality tier: a lossless source is
/// re-encoded losslessly and a lossy one stays lossy, with the target chosen for playback reach.
/// </remarks>
public sealed class EncoderSelectorTests
{
    private static NcmFile Container(byte[] payload, string format)
    {
        var options = new NcmWriteOptions { Title = "t", Format = format };
        var container = new MemoryStream();
        NcmWriter.Write(container, new MemoryStream(payload), options);
        container.Position = 0;
        return NcmFile.Open(container);
    }

    private static byte[] Mp3Payload()
    {
        // ID3v2 header followed by a valid MPEG-1 Layer III frame sync.
        var payload = new byte[512];
        payload[0] = (byte)'I';
        payload[1] = (byte)'D';
        payload[2] = (byte)'3';
        payload[3] = 0x03;
        payload[10] = 0xFF;
        payload[11] = 0xFB;
        return payload;
    }

    [Fact]
    public void TierIsLosslessForFlacAndPcm()
    {
        using NcmFile flac = Container(TestImages.Flac(44100, 2, 16, 1000, new byte[64]), "flac");
        Assert.Equal(AudioQualityTier.Lossless, EncoderSelector.TierOf(flac));

        // A RIFF/WAVE payload is PCM, so also lossless.
        var wav = new byte[256];
        "RIFF"u8.CopyTo(wav);
        using NcmFile pcm = Container(wav, "wav");
        Assert.Equal(AudioQualityTier.Lossless, EncoderSelector.TierOf(pcm));
    }

    [Fact]
    public void TierIsLossyForMp3AndAudioVivid()
    {
        using NcmFile mp3 = Container(Mp3Payload(), "mp3");
        Assert.Equal(AudioQualityTier.Lossy, EncoderSelector.TierOf(mp3));

        // AVS3-P3 in MP4: lossy, and the case this whole path exists for.
        byte[] mp4 = TestImages.Mp4("av3a", 12, 44100, 16, [new byte[64]]);
        using NcmFile vivid = Container(mp4, "m4a");
        Assert.Equal(AudioQualityTier.Lossy, EncoderSelector.TierOf(vivid));
    }

    [Fact]
    public void LosslessStereoStaysLossless()
    {
        EncodeChoice choice = EncoderSelector.Select(
            OutputFormat.Compatible, AudioQualityTier.Lossless, 2, HardwareAcceleration.Disabled, ffmpeg: null, out _);

        Assert.Equal("flac", choice.Encoder);
        Assert.Equal(".flac", choice.Extension);
        Assert.Null(choice.TargetChannels);
        Assert.False(choice.Accelerated);
    }

    [Fact]
    public void LossyStereoBecomesAac()
    {
        EncodeChoice choice = EncoderSelector.Select(
            OutputFormat.Compatible, AudioQualityTier.Lossy, 2, HardwareAcceleration.Disabled, ffmpeg: null, out _);

        Assert.Equal("aac", choice.Encoder);
        Assert.Equal(".m4a", choice.Extension);
        Assert.Equal(2, choice.TargetChannels);
    }

    [Fact]
    public void LossyMultichannelBecomesFivePointOneAac()
    {
        // A 12-channel immersive track cannot go into FLAC at all, and AAC 5.1 is what players take.
        EncodeChoice choice = EncoderSelector.Select(
            OutputFormat.Compatible, AudioQualityTier.Lossy, 12, HardwareAcceleration.Disabled, ffmpeg: null, out _);

        Assert.Equal("aac", choice.Encoder);
        Assert.Equal(6, choice.TargetChannels);
    }

    [Fact]
    public void LosslessBeyondEightChannelsFallsBackToAac()
    {
        EncodeChoice choice = EncoderSelector.Select(
            OutputFormat.Compatible, AudioQualityTier.Lossless, 12, HardwareAcceleration.Disabled, ffmpeg: null, out _);

        Assert.Equal("aac", choice.Encoder);
        Assert.Equal(6, choice.TargetChannels);
    }

    [Fact]
    public void ExplicitMp3RequestIsAlwaysStereo()
    {
        EncodeChoice choice = EncoderSelector.Select(
            OutputFormat.Mp3, AudioQualityTier.Lossless, 12, HardwareAcceleration.Disabled, ffmpeg: null, out _);

        Assert.Equal("libmp3lame", choice.Encoder);
        Assert.Equal(".mp3", choice.Extension);
        Assert.Equal(2, choice.TargetChannels);
    }

    [Fact]
    public void ExplicitFlacRequestClampsToEightChannels()
    {
        EncodeChoice choice = EncoderSelector.Select(
            OutputFormat.Flac, AudioQualityTier.Lossy, 12, HardwareAcceleration.Disabled, ffmpeg: null, out _);

        Assert.Equal("flac", choice.Encoder);
        Assert.Equal(8, choice.TargetChannels);
    }

    [Fact]
    public void WithoutAnFfmpegNoAcceleratedEncoderIsChosen()
    {
        EncodeChoice choice = EncoderSelector.Select(
            OutputFormat.Mp3, AudioQualityTier.Lossy, 2, HardwareAcceleration.Auto, ffmpeg: null, out string? note);

        Assert.False(choice.Accelerated);
        Assert.Equal("libmp3lame", choice.Encoder);

        // With no tool to probe, the accelerator request is simply ignored rather than reported.
        Assert.Null(note);
    }

    [Fact]
    public void EveryChoiceCarriesACpuFallback()
    {
        foreach (OutputFormat format in Enum.GetValues<OutputFormat>())
        {
            if (format == OutputFormat.LosslessCopy)
            {
                continue;
            }

            EncodeChoice choice = EncoderSelector.Select(
                format, AudioQualityTier.Lossy, 2, HardwareAcceleration.Disabled, ffmpeg: null, out _);

            Assert.False(string.IsNullOrWhiteSpace(choice.FallbackEncoder));
            Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
            Assert.StartsWith(".", choice.Extension, StringComparison.Ordinal);
        }
    }
}
