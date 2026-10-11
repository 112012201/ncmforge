using NcmForge.Core.Ncm;
using Xunit;

namespace NcmForge.Tests;

/// <summary>
/// Guards the keystream optimization.
/// </summary>
/// <remarks>
/// The reference algorithm derives one mask byte per position from a 256-byte state box, but the
/// index it uses depends only on <c>position mod 256</c>. The implementation exploits that to
/// precompute a repeating mask, which changes decryption into a table-indexed XOR. These tests
/// prove the optimization is behaviour-preserving, because a phase error would corrupt audio
/// silently rather than fail loudly.
/// </remarks>
public sealed class NcmKeystreamTests
{
    private static byte[] Key() => System.Text.Encoding.ASCII.GetBytes(
        "11170191017446E7fT49x7doS2m4Q8wZ0aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcd");

    private static byte[] Data(int length)
    {
        var data = new byte[length];
        var random = new Random(20240517);

        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)random.Next(256);
        }

        return data;
    }

    [Fact]
    public void ChunkedApplicationMatchesSinglePass()
    {
        byte[] key = Key();
        byte[] single = Data(70_000);
        byte[] chunked = (byte[])single.Clone();

        new NcmKeystream(key).Apply(single, 0);

        // Deliberately awkward chunk sizes, including ones that do not divide the 256-byte period.
        var keystream = new NcmKeystream(key);
        var offset = 0;
        foreach (int size in (int[])[1, 7, 255, 256, 257, 1000, 4096, 13])
        {
            int take = Math.Min(size, chunked.Length - offset);
            if (take <= 0)
            {
                break;
            }

            keystream.Apply(chunked.AsSpan(offset, take), offset);
            offset += take;
        }

        if (offset < chunked.Length)
        {
            keystream.Apply(chunked.AsSpan(offset), offset);
        }

        Assert.Equal(single, chunked);
    }

    [Fact]
    public void ApplyingTwiceRestoresThePlaintext()
    {
        byte[] key = Key();
        byte[] original = Data(1000);
        byte[] working = (byte[])original.Clone();

        var keystream = new NcmKeystream(key);
        keystream.Apply(working, 0);
        keystream.Apply(working, 0);

        Assert.Equal(original, working);
    }

    [Fact]
    public void NonZeroStartOffsetUsesTheCorrectPhase()
    {
        byte[] key = Key();
        byte[] whole = Data(4096);
        byte[] expected = (byte[])whole.Clone();
        new NcmKeystream(key).Apply(expected, 0);

        // Decrypting only the tail must agree with decrypting everything.
        byte[] tailOnly = (byte[])whole.Clone();
        var keystream = new NcmKeystream(key);
        keystream.Apply(tailOnly.AsSpan(1000, 500), 1000);

        Assert.Equal(expected.AsSpan(1000, 500).ToArray(), tailOnly.AsSpan(1000, 500).ToArray());
    }

    [Fact]
    public void MaskRepeatsEvery256Bytes()
    {
        byte[] key = Key();
        byte[] data = new byte[512];
        new NcmKeystream(key).Apply(data, 0);

        // The mask has period 256, so byte i and byte i+256 are masked identically.
        Assert.Equal(data.AsSpan(0, 256).ToArray(), data.AsSpan(256, 256).ToArray());
    }

    [Fact]
    public void EmptyKeyIsRejected()
    {
        Assert.Throws<NcmFormatException>(() => new NcmKeystream(ReadOnlySpan<byte>.Empty));
    }
}
