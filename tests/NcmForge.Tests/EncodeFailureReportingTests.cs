using NcmForge.Core.Pipeline;
using Xunit;

namespace NcmForge.Tests;

/// <summary>
/// Coverage for how an ffmpeg failure is read and classified.
/// </summary>
/// <remarks>
/// These exist because the fallback ladder reported the wrong line for every rung: ffmpeg opens its
/// output with an "Input #0 ..." banner, and taking the first line surfaced that banner instead of
/// the error, so nine distinct failures all looked identical and the real cause — a folder refusing
/// ffmpeg's writes — was never shown to the user.
/// </remarks>
public sealed class EncodeFailureReportingTests
{
    /// <summary>
    /// Verbatim from a real report: the GUI could write its payload into the working folder, but
    /// ffmpeg could not create its output beside it.
    /// </summary>
    private const string PermissionDeniedStderr = """
        ffmpeg version 5c4c25e Copyright (c) 2000-2023 the FFmpeg developers
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'E:\Music\NcmForge\.ncmforge-tmp\48969be8\LumynX,DJ XRAY - MONTAGEM 486.m4a':
          Metadata:
            artist          : LumynX/DJ XRAY
            album           : MONTAGEM 486
          Duration: 00:02:18.86, start: 0.000000, bitrate: 832 kb/s
          Stream #0:0[0x1](eng): Audio: av3a (av3a / 0x61337661), 44100 Hz, 7.1.4, s16, 832 kb/s (default)
        [out#0/flac @ 0000028da48db540] Error opening output E:\Music\NcmForge\.ncmforge-tmp\48969be8\LumynX,DJ XRAY - MONTAGEM 486.encoded.flac: Permission denied
        Error opening output file E:\Music\NcmForge\.ncmforge-tmp\48969be8\LumynX,DJ XRAY - MONTAGEM 486.encoded.flac.
        Error opening output files: Permission denied
        """;

    [Fact]
    public void TheBannerIsNotMistakenForTheError()
    {
        string reported = ConversionPipeline.FirstLine(PermissionDeniedStderr);

        Assert.DoesNotContain("Input #0", reported, StringComparison.Ordinal);
        Assert.Contains("Permission denied", reported, StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteFailureIsRecognised()
    {
        Assert.True(ConversionPipeline.IsOutputWriteFailure(PermissionDeniedStderr));
        Assert.True(ConversionPipeline.IsOutputWriteFailure("Error opening output file out.flac."));
        Assert.True(ConversionPipeline.IsOutputWriteFailure("Error opening output x.mka: Permission denied"));
    }

    [Theory]
    // Ordinary encoder problems must not trigger the scratch-folder retry: it would waste a second
    // attempt and, worse, blame the folder for a codec that simply cannot do the job.
    [InlineData("Unknown encoder 'libfdk_aac'")]
    [InlineData("[flac @ 0x1] 12 channels not supported (max 8)")]
    [InlineData("[aac @ 0x2] Unsupported channel layout \"7.1.4\"")]
    public void OtherFailuresDoNotLookLikeWriteFailures(string stderr)
        => Assert.False(ConversionPipeline.IsOutputWriteFailure(stderr));

    [Fact]
    public void AnOrdinaryErrorStillSurfacesWhenNoMarkerMatches()
    {
        // No recognised keyword: the reason is the last line ffmpeg printed, not its banner.
        const string stderr = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'in.m4a':
              Duration: 00:02:18.86, start: 0.000000, bitrate: 832 kb/s
            [out#0/mp3 @ 0x1] something went sideways at the very end
            """;

        string reported = ConversionPipeline.FirstLine(stderr);

        Assert.Contains("sideways", reported, StringComparison.Ordinal);
        Assert.DoesNotContain("Input #0", reported, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDiagnosticsAreHandledRatherThanThrowing()
    {
        Assert.Equal("(no diagnostics)", ConversionPipeline.FirstLine(string.Empty));
        Assert.Equal("(no diagnostics)", ConversionPipeline.FirstLine("   \n  \n"));
    }

    [Fact]
    public void AVeryLongLineIsTruncated()
    {
        string reported = ConversionPipeline.FirstLine("Error opening output " + new string('x', 400) + ": Permission denied");

        Assert.True(reported.Length <= 201, $"expected truncation, got {reported.Length} characters");
    }
}
