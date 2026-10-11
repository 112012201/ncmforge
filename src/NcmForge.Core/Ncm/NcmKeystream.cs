namespace NcmForge.Core.Ncm;

/// <summary>
/// The RC4-derived keystream that masks an .ncm container's audio frames.
/// </summary>
/// <remarks>
/// The reference algorithm derives one mask byte per position using a 256-byte state box:
/// <code>
/// j = (i + 1) &amp; 0xFF
/// mask(i) = box[(box[j] + box[(box[j] + j) &amp; 0xFF]) &amp; 0xFF]
/// </code>
/// <c>j</c> depends only on <c>i mod 256</c>, so the whole keystream is a 256-byte repeating
/// pattern. Precomputing that pattern turns decryption into a single table-indexed XOR per byte
/// instead of four dependent memory lookups, and keeps the operation trivially vectorizable.
/// </remarks>
internal sealed class NcmKeystream
{
    private const int Period = 256;

    private readonly byte[] _mask = new byte[Period];

    internal NcmKeystream(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new NcmFormatException("The .ncm audio key is empty.");
        }

        // Standard RC4-style key schedule producing the 256-byte state box.
        Span<byte> box = stackalloc byte[Period];
        for (var i = 0; i < Period; i++)
        {
            box[i] = (byte)i;
        }

        int lastByte = 0;
        int keyOffset = 0;
        for (var i = 0; i < Period; i++)
        {
            byte swap = box[i];
            int c = (swap + lastByte + key[keyOffset]) & 0xFF;

            keyOffset++;
            if (keyOffset >= key.Length)
            {
                keyOffset = 0;
            }

            box[i] = box[c];
            box[c] = swap;
            lastByte = c;
        }

        // Flatten the dependent lookups into the repeating mask.
        for (var j = 0; j < Period; j++)
        {
            int index = (box[j] + box[(box[j] + j) & 0xFF]) & 0xFF;
            _mask[j] = box[index];
        }
    }

    /// <summary>
    /// XORs <paramref name="buffer"/> in place. <paramref name="absoluteOffset"/> is the position of
    /// <c>buffer[0]</c> within the audio stream, which is required because the mask is periodic but
    /// phase-dependent.
    /// </summary>
    internal void Apply(Span<byte> buffer, long absoluteOffset)
    {
        // Phase of the pattern at buffer[0]: position i consumes mask index (i + 1) & 0xFF.
        int phase = (int)((absoluteOffset + 1) & (Period - 1));

        byte[] mask = _mask;
        int length = buffer.Length;

        int i = 0;

        // Peel the head so the body can be processed in aligned 256-byte blocks where phase is 0.
        if (phase != 0)
        {
            int head = Math.Min(length, Period - phase);
            for (; i < head; i++)
            {
                buffer[i] ^= mask[phase + i];
            }
        }

        for (; i + Period <= length; i += Period)
        {
            for (var k = 0; k < Period; k++)
            {
                buffer[i + k] ^= mask[k];
            }
        }

        for (int tail = 0; i < length; i++, tail++)
        {
            buffer[i] ^= mask[tail];
        }
    }
}
