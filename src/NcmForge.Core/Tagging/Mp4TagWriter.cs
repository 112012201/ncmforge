using System.Buffers.Binary;
using System.Text;
using NcmForge.Core.Ncm;

namespace NcmForge.Core.Tagging;

/// <summary>What changed during an MP4 tag rewrite.</summary>
/// <param name="Tagged">True when metadata atoms were written.</param>
/// <param name="OffsetDelta">How much the media data shifted, in bytes.</param>
/// <param name="Atoms">The metadata atoms that were written.</param>
public sealed record Mp4TagResult(bool Tagged, long OffsetDelta, IReadOnlyList<string> Atoms);

/// <summary>
/// Writes iTunes-style metadata into an ISO base media file.
/// </summary>
/// <remarks>
/// This exists for the one case the other writers cannot serve: an immersive download whose audio
/// no local decoder understands. The payload is still worth keeping, and keeping it untagged would
/// make the result useless in a library, so the movie box is rebuilt with a <c>udta/meta/ilst</c>
/// while the media data is copied through untouched.
/// <para>
/// Because growing the movie box shifts everything after it, the chunk offset tables
/// (<c>stco</c>/<c>co64</c>) are rewritten by the same delta. Skipping that step would produce a
/// file whose samples point at the wrong bytes.
/// </para>
/// </remarks>
public static class Mp4TagWriter
{
    private static readonly byte[] NameTitle = [(byte)0xA9, (byte)'n', (byte)'a', (byte)'m'];
    private static readonly byte[] NameArtist = [(byte)0xA9, (byte)'A', (byte)'R', (byte)'T'];
    private static readonly byte[] NameAlbum = [(byte)0xA9, (byte)'a', (byte)'l', (byte)'b'];
    private static readonly byte[] NameAlbumArtist = [(byte)'a', (byte)'A', (byte)'R', (byte)'T'];
    private static readonly byte[] NameGenre = [(byte)0xA9, (byte)'g', (byte)'e', (byte)'n'];
    private static readonly byte[] NameYear = [(byte)0xA9, (byte)'d', (byte)'a', (byte)'y'];
    private static readonly byte[] NameComment = [(byte)0xA9, (byte)'c', (byte)'m', (byte)'t'];
    private static readonly byte[] NameLyrics = [(byte)0xA9, (byte)'l', (byte)'y', (byte)'r'];
    private static readonly byte[] NameTrack = [(byte)'t', (byte)'r', (byte)'k', (byte)'n'];
    private static readonly byte[] NameDisc = [(byte)'d', (byte)'i', (byte)'s', (byte)'k'];
    private static readonly byte[] NameCover = [(byte)'c', (byte)'o', (byte)'v', (byte)'r'];

    private const int TextType = 1;
    private const int JpegType = 13;
    private const int PngType = 14;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> with metadata written
    /// into a rebuilt movie box. Audio samples are copied byte for byte.
    /// </summary>
    public static Mp4TagResult WriteTagged(Stream source, Stream destination, TrackTags tags)
    {
        if (!source.CanSeek)
        {
            throw new ArgumentException("The MP4 source must be seekable.", nameof(source));
        }

        List<TopLevelBox> boxes = ReadTopLevelBoxes(source);
        TopLevelBox? moov = boxes.FirstOrDefault(b => b.Type == "moov");
        if (moov is null)
        {
            throw new InvalidDataException("The stream has no moov box, so it is not an ISO base media file.");
        }

        byte[] oldMoov = ReadBytes(source, moov.Offset, moov.Size);
        byte[] newMoov = RebuildMovie(oldMoov, tags, out IReadOnlyList<string> atoms);

        long delta = newMoov.Length - oldMoov.Length;

        // Only media data positioned after the movie box moves, so only then do offsets shift.
        bool mediaFollowsMovie = boxes.Any(b => b.Offset > moov.Offset && b.Type is "mdat" or "moof");
        if (mediaFollowsMovie && delta != 0)
        {
            ShiftChunkOffsets(newMoov, delta);
        }

        foreach (TopLevelBox box in boxes.OrderBy(b => b.Offset))
        {
            if (box.Offset == moov.Offset)
            {
                destination.Write(newMoov, 0, newMoov.Length);
                continue;
            }

            CopyRange(source, destination, box.Offset, box.Size);
        }

        return new Mp4TagResult(atoms.Count > 0, mediaFollowsMovie ? delta : 0, atoms);
    }

    /// <summary>Lists the metadata atoms that would be written for these tags.</summary>
    public static IReadOnlyList<string> DescribeAtoms(TrackTags tags)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(tags.Title)) names.Add("©nam");
        if (!string.IsNullOrWhiteSpace(tags.Artist)) names.Add("©ART");
        if (!string.IsNullOrWhiteSpace(tags.Album)) names.Add("©alb");
        if (tags.TrackNumber is not null) names.Add("trkn");
        if (!string.IsNullOrWhiteSpace(tags.Lyrics)) names.Add("©lyr");
        if (tags.Cover is not null) names.Add("covr");
        return names;
    }

    /// <summary>
    /// Rebuilds the movie box so its user data carries the tags, keeping atoms it does not set.
    /// </summary>
    /// <remarks>
    /// The original user data is merged, not replaced. NetEase's own immersive downloads arrive
    /// with a populated <c>ilst</c> (<c>©nam</c>, <c>©ART</c>, <c>©alb</c>, <c>trkn</c>,
    /// <c>©too</c>); discarding it wholesale would silently drop the track number and the encoder
    /// provenance that no option here supplies.
    /// </remarks>
    private static byte[] RebuildMovie(byte[] moov, TrackTags tags, out IReadOnlyList<string> atoms)
    {
        (List<ItemAtom> items, List<string> names) = BuildItems(tags);
        atoms = names;

        // With nothing to write, hand the movie box back untouched. Rebuilding it anyway would
        // discard whatever user data the original carried, quietly shrinking the file.
        if (names.Count == 0)
        {
            return moov;
        }

        var replaced = new HashSet<string>(items.Select(i => Encoding.Latin1.GetString(i.Type)), StringComparer.Ordinal);

        var merged = new List<byte[]>();
        foreach (ItemAtom existing in ReadExistingItems(moov))
        {
            if (!replaced.Contains(Encoding.Latin1.GetString(existing.Type)))
            {
                merged.Add(existing.Atom);
            }
        }

        merged.AddRange(items.Select(i => i.Atom));

        var children = new List<byte[]>();
        foreach (InMemoryBox box in ReadBoxes(moov, 8, moov.Length, recurse: false))
        {
            // An existing user-data box is replaced, never duplicated.
            if (box.Type == "udta")
            {
                continue;
            }

            children.Add(moov[box.Start..box.End]);
        }

        byte[] hdlr = FullBox("hdlr", 0, 0, Concat(
            new byte[4],                       // pre_defined
            "mdir"u8.ToArray(),                // handler_type: metadata
            new byte[12],                      // reserved
            [0x00]));                          // empty name

        byte[] ilst = Box("ilst", Concat(merged.ToArray()));
        byte[] meta = FullBox("meta", 0, 0, Concat(hdlr, ilst));
        children.Add(Box("udta", meta));

        return Box("moov", Concat(children.ToArray()));
    }

    /// <summary>Reads the metadata atoms already present in the movie box's user data.</summary>
    private static List<ItemAtom> ReadExistingItems(byte[] moov)
    {
        var found = new List<ItemAtom>();

        InMemoryBox? udta = FindChild(moov, moov.Length, 8, "udta");
        if (udta is null)
        {
            return found;
        }

        InMemoryBox? meta = FindChild(moov, udta.PayloadEnd, udta.PayloadStart, "meta");
        if (meta is null)
        {
            return found;
        }

        // meta is a full box, so its children begin after version/flags.
        InMemoryBox? ilst = FindChild(moov, meta.PayloadEnd, meta.PayloadStart + 4, "ilst");
        if (ilst is null)
        {
            return found;
        }

        foreach (InMemoryBox child in ReadBoxes(moov, ilst.PayloadStart, ilst.PayloadEnd, recurse: false))
        {
            found.Add(new ItemAtom(child.TypeBytes, moov[child.Start..child.End]));
        }

        return found;
    }

    /// <summary>Finds one direct child box by type within a range.</summary>
    private static InMemoryBox? FindChild(byte[] buffer, int end, int start, string type)
    {
        foreach (InMemoryBox box in ReadBoxes(buffer, start, end, recurse: false))
        {
            if (box.Type == type)
            {
                return box;
            }
        }

        return null;
    }

    /// <summary>Builds the metadata atoms for these tags.</summary>
    private static (List<ItemAtom> Items, List<string> Names) BuildItems(TrackTags tags)
    {
        var items = new List<ItemAtom>();
        var labels = new List<string>();

        void Text(byte[] type, string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            items.Add(new ItemAtom(type, Atom(type, DataBox(TextType, Encoding.UTF8.GetBytes(value!)))));
            labels.Add(label);
        }

        Text(NameTitle, "©nam", tags.Title);
        Text(NameArtist, "©ART", tags.Artist);
        Text(NameAlbum, "©alb", tags.Album);
        Text(NameAlbumArtist, "aART", tags.AlbumArtist ?? tags.Artist);
        Text(NameGenre, "©gen", tags.Genre);
        Text(NameComment, "©cmt", tags.Comment);

        if (tags.Year is { } year)
        {
            Text(NameYear, "©day", year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Text(NameLyrics, "©lyr", tags.Lyrics);

        if (tags.TrackNumber is { } track)
        {
            // trkn is binary: two reserved bytes, number, total, then two more reserved.
            var value = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2, 2), (ushort)track);
            BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(4, 2), (ushort)(tags.TrackTotal ?? 0));
            items.Add(new ItemAtom(NameTrack, Atom(NameTrack, DataBox(0, value))));
            labels.Add("trkn");
        }

        if (tags.DiscNumber is { } disc)
        {
            var value = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2, 2), (ushort)disc);
            items.Add(new ItemAtom(NameDisc, Atom(NameDisc, DataBox(0, value))));
            labels.Add("disk");
        }

        if (tags.Cover is { } cover)
        {
            int type = cover.Format == Ncm.ImageFormat.Png ? PngType : JpegType;
            items.Add(new ItemAtom(NameCover, Atom(NameCover, DataBox(type, cover.Data))));
            labels.Add("covr");
        }

        return (items, labels);
    }

    /// <summary>Builds a <c>data</c> atom: version, well-known type, locale, then the value.</summary>
    private static byte[] DataBox(int wellKnownType, byte[] value)
    {
        var body = new byte[8 + value.Length];
        body[0] = 0; // version
        body[1] = (byte)((wellKnownType >> 16) & 0xFF);
        body[2] = (byte)((wellKnownType >> 8) & 0xFF);
        body[3] = (byte)(wellKnownType & 0xFF);
        // body[4..7] stays zero: the locale field.
        value.CopyTo(body, 8);
        return Box("data", body);
    }

    /// <summary>Adds <paramref name="delta"/> to every entry of every chunk offset table.</summary>
    private static void ShiftChunkOffsets(byte[] moov, long delta)
    {
        foreach (InMemoryBox box in ReadBoxes(moov, 8, moov.Length, recurse: true))
        {
            if (box.Type == "stco")
            {
                // Payload: version/flags(4), entry_count(4), then 32-bit offsets.
                int count = BinaryPrimitives.ReadInt32BigEndian(moov.AsSpan(box.PayloadStart + 4, 4));
                for (var i = 0; i < count; i++)
                {
                    int at = box.PayloadStart + 8 + i * 4;
                    if (at + 4 > box.PayloadEnd)
                    {
                        break;
                    }

                    uint value = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at, 4));
                    BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(at, 4), (uint)(value + delta));
                }
            }
            else if (box.Type == "co64")
            {
                int count = BinaryPrimitives.ReadInt32BigEndian(moov.AsSpan(box.PayloadStart + 4, 4));
                for (var i = 0; i < count; i++)
                {
                    int at = box.PayloadStart + 8 + i * 8;
                    if (at + 8 > box.PayloadEnd)
                    {
                        break;
                    }

                    ulong value = BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(at, 8));
                    BinaryPrimitives.WriteUInt64BigEndian(moov.AsSpan(at, 8), value + (ulong)delta);
                }
            }
        }
    }

    private static List<TopLevelBox> ReadTopLevelBoxes(Stream source)
    {
        var boxes = new List<TopLevelBox>();
        long position = 0;
        Span<byte> header = stackalloc byte[16];

        while (position + 8 <= source.Length)
        {
            source.Position = position;
            if (ReadFully(source, header[..8]) != 8)
            {
                break;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string type = Encoding.ASCII.GetString(header[4..8]);
            long headerSize = 8;

            if (size == 1)
            {
                if (ReadFully(source, header[8..16]) != 8)
                {
                    break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = source.Length - position;
            }

            if (size < headerSize || position + size > source.Length)
            {
                break;
            }

            boxes.Add(new TopLevelBox(type, position, size, position + headerSize));
            position += size;
        }

        return boxes;
    }

    /// <summary>
    /// Walks the boxes in <c>buffer[start..end]</c>.
    /// </summary>
    /// <param name="recurse">
    /// When true, container boxes are descended into and their descendants are yielded as well.
    /// Rebuilding a box tree needs direct children only: yielding descendants too would flatten
    /// the tree and duplicate every nested box into its parent.
    /// </param>
    private static IEnumerable<InMemoryBox> ReadBoxes(byte[] buffer, int start, int end, bool recurse)
    {
        int cursor = start;
        var guard = 0;

        while (cursor + 8 <= end && guard++ < 4096)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(cursor, 4));

            // Keep the raw type bytes: metadata atom names use 0xA9 (©), which ASCII decoding
            // turns into '?' and would then fail to match against a freshly built atom.
            byte[] typeBytes = buffer[(cursor + 4)..(cursor + 8)];
            string type = Encoding.ASCII.GetString(typeBytes);
            int headerSize = 8;

            if (size == 1)
            {
                if (cursor + 16 > end)
                {
                    yield break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(cursor + 8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - cursor;
            }

            if (size < headerSize || cursor + size > end)
            {
                yield break;
            }

            int boxStart = cursor;
            int boxEnd = cursor + (int)size;
            int payloadStart = cursor + headerSize;
            int payloadEnd = boxEnd;

            // meta is a full box, so its children begin after version/flags.
            int childStart = type == "meta" ? payloadStart + 4 : payloadStart;
            if (recurse && type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "udta" or "meta")
            {
                foreach (InMemoryBox nested in ReadBoxes(buffer, childStart, payloadEnd, recurse: true))
                {
                    yield return nested;
                }
            }

            yield return new InMemoryBox(type, boxStart, boxEnd, payloadStart, payloadEnd) { TypeBytes = typeBytes };
            cursor = boxEnd;
        }
    }

    private static byte[] ReadBytes(Stream source, long offset, long length)
    {
        var buffer = new byte[length];
        source.Position = offset;
        int read = ReadFully(source, buffer);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static void CopyRange(Stream source, Stream destination, long offset, long length)
    {
        const int Chunk = 1 << 20;

        source.Position = offset;
        var buffer = new byte[Chunk];
        long remaining = length;

        while (remaining > 0)
        {
            int want = (int)Math.Min(Chunk, remaining);
            int read = source.Read(buffer, 0, want);
            if (read == 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static byte[] Atom(byte[] type, byte[] payload) => Box(type, payload);

    private static byte[] Box(string type, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), result.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] Box(byte[] type, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), result.Length);
        type.CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
    {
        var body = new byte[4 + payload.Length];
        body[0] = version;
        body[1] = (byte)((flags >> 16) & 0xFF);
        body[2] = (byte)((flags >> 8) & 0xFF);
        body[3] = (byte)(flags & 0xFF);
        payload.CopyTo(body, 4);
        return Box(type, body);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var cursor = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, cursor);
            cursor += part.Length;
        }

        return result;
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

    private sealed record TopLevelBox(string Type, long Offset, long Size, long PayloadOffset);

    /// <summary>One metadata atom, keeping its raw type so it can be compared with an existing one.</summary>
    private readonly record struct ItemAtom(byte[] Type, byte[] Atom);

    private sealed record InMemoryBox(string Type, int Start, int End, int PayloadStart, int PayloadEnd)
    {
        /// <summary>The raw four-byte box type, which for metadata atoms is not plain ASCII.</summary>
        public byte[] TypeBytes { get; init; } = Encoding.ASCII.GetBytes(Type);
    }
}
