using NcmForge.Core.Audio;
using NcmForge.Core.Ncm;
using Xunit;

namespace NcmForge.Tests;

/// <summary>
/// Round-trip coverage for the container reader, driven through the writer.
/// </summary>
/// <remarks>
/// The .ncm layout is undocumented, so a fixture copied from the wild proves only that the reader
/// handles that one file. Writing a container and reading it back pins down the invariants the
/// reader relies on: the AES key/Metadata wrapping, the keystream phase, the trailer length, and
/// both payload layouts.
/// </remarks>
public sealed class NcmContainerTests
{
    private static byte[] SampleAudio(int length)
    {
        var audio = new byte[length];
        for (var i = 0; i < length; i++)
        {
            audio[i] = (byte)((i * 31 + (i >> 8) * 17) & 0xFF);
        }

        // The reader identifies the payload by its signature, so the fixture needs a real header.
        "fLaC"u8.CopyTo(audio);
        return audio;
    }

    private static MemoryStream BuildContainer(byte[] audio, byte[]? cover, out NcmWriteOptions options)
    {
        options = new NcmWriteOptions
        {
            Title = "Round Trip",
            Artist = "NcmForge",
            Album = "Tests",
            MusicId = 424242,
            Format = "mp3",
            EmbeddedCover = cover,
        };

        var container = new MemoryStream();
        using var source = new MemoryStream(audio);
        NcmWriter.Write(container, source, options);
        container.Position = 0;
        return container;
    }

    [Fact]
    public void StandardLayout_RoundTripsMetadataAndPayload()
    {
        byte[] audio = SampleAudio(4096);
        using MemoryStream container = BuildContainer(audio, cover: null, out NcmWriteOptions options);

        using var ncm = NcmFile.Open(container);

        Assert.Equal(NcmPayloadLayout.Standard, ncm.Layout);
        Assert.Null(ncm.EmbeddedImage);
        Assert.Equal(options.Title, ncm.Metadata.MusicName);
        Assert.Equal("NcmForge", ncm.Metadata.ArtistDisplay);
        Assert.Equal(options.Album, ncm.Metadata.Album);
        Assert.Equal(424242, ncm.Metadata.MusicId);

        using Stream payload = ncm.OpenDecryptedAudioStream();
        using var buffer = new MemoryStream();
        payload.CopyTo(buffer);

        Assert.Equal(audio, buffer.ToArray());
    }

    [Fact]
    public void ImmersiveLayout_RoundTripsCoverAndPayload()
    {
        byte[] audio = SampleAudio(5000);
        byte[] cover = TestImages.Jpeg(240, 180);
        using MemoryStream container = BuildContainer(audio, cover, out _);

        using var ncm = NcmFile.Open(container);

        Assert.Equal(NcmPayloadLayout.AfterEmbeddedImage, ncm.Layout);

        NcmEmbeddedImage? found = ncm.EmbeddedImage;
        Assert.NotNull(found);
        NcmEmbeddedImage image = found!;
        Assert.Equal(ImageFormat.Jpeg, image.Format);
        Assert.Equal(cover.Length, image.ImageLength);

        byte[]? extractedCover = ncm.ReadEmbeddedImage();
        Assert.NotNull(extractedCover);
        Assert.Equal(cover, extractedCover!);

        using Stream payload = ncm.OpenDecryptedAudioStream();
        using var buffer = new MemoryStream();
        payload.CopyTo(buffer);

        Assert.Equal(audio, buffer.ToArray());
    }

    [Fact]
    public void PayloadLengthMatchesContainerRemainder()
    {
        byte[] audio = SampleAudio(1234);
        using MemoryStream container = BuildContainer(audio, cover: null, out _);
        using var ncm = NcmFile.Open(container);

        Assert.Equal(audio.Length, ncm.AudioLength);
        Assert.Equal(container.Length - ncm.AudioOffset, ncm.AudioLength);
    }

    [Fact]
    public void TruncatedAndForeignFilesAreRejected()
    {
        Assert.Throws<NcmFormatException>(() => NcmFile.Open(new MemoryStream(new byte[4])));

        var notNcm = new byte[64];
        "NOTANNCM"u8.CopyTo(notNcm);
        Assert.Throws<NcmFormatException>(() => NcmFile.Open(new MemoryStream(notNcm)));
    }

    [Fact]
    public void RandomKeyProducesDifferentCiphertextForSameAudio()
    {
        byte[] audio = SampleAudio(512);
        using MemoryStream first = BuildContainer(audio, cover: null, out _);
        using MemoryStream second = BuildContainer(audio, cover: null, out _);

        // Keys are random per container, so the on-disk payload must differ even for identical audio.
        Assert.NotEqual(first.ToArray(), second.ToArray());
    }

    [Fact]
    public void EmbeddedCoverDetectionRequiresARealImage()
    {
        // A length field followed by non-image bytes must not be mistaken for a cover block.
        var options = new NcmWriteOptions { Title = "No cover", EmbeddedCover = null };
        var container = new MemoryStream();
        using var source = new MemoryStream(SampleAudio(256));
        NcmWriter.Write(container, source, options);
        container.Position = 0;

        using var ncm = NcmFile.Open(container);
        Assert.Null(ncm.EmbeddedImage);
    }

    [Fact]
    public void NonSeekableStreamIsBuffered()
    {
        byte[] audio = SampleAudio(2048);
        using MemoryStream container = BuildContainer(audio, cover: null, out _);
        using var nonSeekable = new NonSeekableStream(container.ToArray());

        using var ncm = NcmFile.Open(nonSeekable);
        using Stream payload = ncm.OpenDecryptedAudioStream();
        using var buffer = new MemoryStream();
        payload.CopyTo(buffer);

        Assert.Equal(audio, buffer.ToArray());
    }

    /// <summary>Read-only forward stream, used to prove the reader buffers non-seekable sources.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }
}
