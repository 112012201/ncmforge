namespace NcmForge.Core.Ncm;

/// <summary>
/// A read-only, seekable view over an .ncm container's audio payload with the keystream removed.
/// </summary>
/// <remarks>
/// Decryption is applied lazily per read against the underlying seekable stream, so converting a
/// large immersive track never materialises the whole payload in memory and seeking is O(1).
/// </remarks>
internal sealed class NcmDecryptedStream : Stream
{
    private readonly Stream _baseStream;
    private readonly NcmKeystream _keystream;
    private readonly long _audioOffset;
    private readonly long _length;
    private readonly bool _ownsBase;

    private long _position;

    internal NcmDecryptedStream(Stream baseStream, NcmKeystream keystream, long audioOffset, long length, bool ownsBase)
    {
        _baseStream = baseStream;
        _keystream = keystream;
        _audioOffset = audioOffset;
        _length = length;
        _ownsBase = ownsBase;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length)
        {
            return 0;
        }

        int toRead = (int)Math.Min(buffer.Length, _length - _position);
        if (toRead <= 0)
        {
            return 0;
        }

        _baseStream.Position = _audioOffset + _position;
        int read = ReadFully(_baseStream, buffer[..toRead]);
        if (read == 0)
        {
            return 0;
        }

        _keystream.Apply(buffer[..read], _position);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
        {
            throw new IOException("Attempted to seek before the start of the audio stream.");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsBase)
        {
            _baseStream.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
