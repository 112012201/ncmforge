using System.Security.Cryptography;

namespace NcmForge.Core.Ncm;

/// <summary>
/// Cryptographic primitives used by the NetEase Cloud Music (.ncm) container.
/// </summary>
/// <remarks>
/// Two independent layers protect an .ncm file:
/// <list type="number">
///   <item>An AES-128-ECB layer that wraps the per-file RC4 key and the JSON metadata.</item>
///   <item>A byte-wise RC4-derived keystream that is XORed over the compressed audio frames.</item>
/// </list>
/// Both fixed AES keys are constants of the container format itself, not user secrets.
/// </remarks>
internal static class NcmCrypto
{
    /// <summary>AES-128 key protecting the per-file RC4 key. ASCII "hzHRAmso5kInbaxW".</summary>
    private static readonly byte[] KeyProtectionKey =
        Convert.FromHexString("687A4852416D736F356B496E62617857");

    /// <summary>AES-128 key protecting the JSON metadata block. ASCII "#14ljk_!\]&amp;0U&lt;'(".</summary>
    private static readonly byte[] MetadataProtectionKey =
        Convert.FromHexString("2331346C6A6B5F215C5D2630553C2728");

    /// <summary>XOR mask applied to the raw key block before AES decryption.</summary>
    private const byte KeyBlockXor = 0x64;

    /// <summary>XOR mask applied to the raw metadata block before base64/AES decoding.</summary>
    private const byte MetadataBlockXor = 0x63;

    private const int MetadataHeaderLength = 22; // "163 key(Don't modify):"
    private const int MetadataPayloadPrefixLength = 6; // "music:"
    private const int KeyPayloadPrefixLength = 17; // "neteasecloudmusic"

    /// <summary>The plaintext marker that prefixes the recovered key material.</summary>
    internal const string KeyPayloadPrefix = "neteasecloudmusic";

    internal static byte[] DecodeKeyBlock(ReadOnlySpan<byte> rawKeyBlock)
        => DecodeKeyCandidates(rawKeyBlock).Padded;

    /// <summary>
    /// Recovers the per-file RC4 key. Two readings of the AES plaintext are returned because the
    /// container's PKCS#7 padding is not applied uniformly by every writer: normally the final
    /// block carries padding that must be stripped, but some payloads are an exact block multiple
    /// with no padding at all. The caller verifies which reading yields coherent audio.
    /// </summary>
    internal static (byte[] Padded, byte[] Verbatim, bool PrefixValid, int PaddedLength) DecodeKeyCandidates(
        ReadOnlySpan<byte> rawKeyBlock)
    {
        if (rawKeyBlock.IsEmpty)
        {
            throw new NcmFormatException("The .ncm key block is empty.");
        }

        var scrambled = new byte[rawKeyBlock.Length];
        for (var i = 0; i < rawKeyBlock.Length; i++)
        {
            scrambled[i] = (byte)(rawKeyBlock[i] ^ KeyBlockXor);
        }

        byte[] full = AesEcbDecrypt(scrambled, KeyProtectionKey);
        byte[] unpadded = RemovePkcs7Padding(full);

        bool prefixValid = full.AsSpan(0, Math.Min(KeyPayloadPrefixLength, full.Length))
            .SequenceEqual(System.Text.Encoding.ASCII.GetBytes(KeyPayloadPrefix)[..Math.Min(KeyPayloadPrefixLength, full.Length)]);

        byte[] padded = unpadded.Length > KeyPayloadPrefixLength ? unpadded[KeyPayloadPrefixLength..] : [];
        byte[] verbatim = full.Length > KeyPayloadPrefixLength ? full[KeyPayloadPrefixLength..] : [];

        return (padded, verbatim, prefixValid, unpadded.Length);
    }

    /// <summary>Decodes the AES/base64 protected JSON metadata block into UTF-8 JSON text.</summary>
    internal static string DecodeMetadataBlock(ReadOnlySpan<byte> rawMetadataBlock)
    {
        if (rawMetadataBlock.Length <= MetadataHeaderLength)
        {
            throw new NcmFormatException("The .ncm metadata block is shorter than its 22-byte header.");
        }

        var unmasked = new byte[rawMetadataBlock.Length];
        for (var i = 0; i < rawMetadataBlock.Length; i++)
        {
            unmasked[i] = (byte)(rawMetadataBlock[i] ^ MetadataBlockXor);
        }

        // Skip "163 key(Don't modify):" and base64-decode the remainder.
        string base64 = System.Text.Encoding.ASCII.GetString(unmasked, MetadataHeaderLength, unmasked.Length - MetadataHeaderLength);

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new NcmFormatException("The .ncm metadata block is not valid base64.", ex);
        }

        byte[] plain = AesEcbDecrypt(cipher, MetadataProtectionKey);
        plain = RemovePkcs7Padding(plain);

        if (plain.Length <= MetadataPayloadPrefixLength)
        {
            throw new NcmFormatException("The .ncm metadata payload is shorter than its 6-byte prefix.");
        }

        return System.Text.Encoding.UTF8.GetString(plain, MetadataPayloadPrefixLength, plain.Length - MetadataPayloadPrefixLength);
    }

    /// <summary>Decrypts an AES-128-ECB block sequence with no padding handling.</summary>
    private static byte[] AesEcbDecrypt(byte[] cipher, byte[] key)
    {
        if (cipher.Length == 0 || cipher.Length % 16 != 0)
        {
            throw new NcmFormatException($"AES block length {cipher.Length} is not a positive multiple of 16.");
        }

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    /// <summary>
    /// Wraps a raw per-file key into the container's key block: prefix, PKCS#7 pad, AES-128-ECB,
    /// then the <c>0x64</c> XOR mask. The inverse of <see cref="DecodeKeyCandidates"/>.
    /// </summary>
    internal static byte[] EncodeKeyBlock(ReadOnlySpan<byte> audioKey)
    {
        byte[] plain = new byte[KeyPayloadPrefixLength + audioKey.Length];
        System.Text.Encoding.ASCII.GetBytes(KeyPayloadPrefix).CopyTo(plain, 0);
        audioKey.CopyTo(plain.AsSpan(KeyPayloadPrefixLength));

        byte[] cipher = AesEcbEncrypt(AddPkcs7Padding(plain), KeyProtectionKey);
        for (var i = 0; i < cipher.Length; i++)
        {
            cipher[i] ^= KeyBlockXor;
        }

        return cipher;
    }

    /// <summary>
    /// Wraps JSON metadata into the container's metadata block: the <c>music:</c> prefix, PKCS#7
    /// pad, AES-128-ECB, the ASCII header, base64, then the <c>0x63</c> XOR mask.
    /// </summary>
    internal static byte[] EncodeMetadataBlock(string json)
    {
        byte[] prefix = System.Text.Encoding.UTF8.GetBytes("music:");
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(json);

        byte[] plain = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(plain, 0);
        payload.CopyTo(plain, prefix.Length);

        byte[] cipher = AesEcbEncrypt(AddPkcs7Padding(plain), MetadataProtectionKey);
        string base64 = Convert.ToBase64String(cipher);

        byte[] header = System.Text.Encoding.ASCII.GetBytes("163 key(Don't modify):");
        byte[] body = System.Text.Encoding.ASCII.GetBytes(base64);

        byte[] block = new byte[header.Length + body.Length];
        header.CopyTo(block, 0);
        body.CopyTo(block, header.Length);

        for (var i = 0; i < block.Length; i++)
        {
            block[i] ^= MetadataBlockXor;
        }

        return block;
    }

    private static byte[] AesEcbEncrypt(byte[] plain, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plain, 0, plain.Length);
    }

    private static byte[] AddPkcs7Padding(byte[] data)
    {
        int padding = 16 - (data.Length % 16);
        var padded = new byte[data.Length + padding];
        data.CopyTo(padded, 0);

        for (var i = data.Length; i < padded.Length; i++)
        {
            padded[i] = (byte)padding;
        }

        return padded;
    }

    private static byte[] RemovePkcs7Padding(byte[] data)
    {
        if (data.Length == 0)
        {
            return data;
        }

        int pad = data[^1];
        if (pad is 0 or > 16 || pad > data.Length)
        {
            return data;
        }

        for (var i = data.Length - pad; i < data.Length; i++)
        {
            if (data[i] != pad)
            {
                return data;
            }
        }

        return data[..^pad];
    }
}
