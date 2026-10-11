using System.Text.Json;
using System.Text.Json.Nodes;

namespace NcmForge.Core.Ncm;

/// <summary>
/// The JSON metadata block embedded in every .ncm container.
/// </summary>
/// <remarks>
/// The schema is not documented and varies by product line (standard, lossless, and the
/// immersive/surround downloads carry different field sets). Rather than bind a rigid DTO that
/// would throw on an unexpected shape, the raw JSON is kept and typed accessors read through it
/// defensively. Unknown fields remain reachable via <see cref="Root"/>.
/// </remarks>
public sealed class NcmMetadata
{
    private static readonly string[] ArtistNameFallbacks = ["artistName", "artists", "singer"];

    private NcmMetadata(string rawJson, JsonObject? root)
    {
        RawJson = rawJson;
        Root = root;
    }

    /// <summary>The verbatim UTF-8 JSON text, with the "music:" prefix already removed.</summary>
    public string RawJson { get; }

    /// <summary>Parsed root object, or <c>null</c> when the payload was not a JSON object.</summary>
    public JsonObject? Root { get; }

    public long? MusicId => GetInt64("musicId");

    public string? MusicName => GetString("musicName");

    public string? Album => GetString("album");

    public long? AlbumId => GetInt64("albumId");

    /// <summary>Cover art URL carried inside the container itself, when present.</summary>
    public string? AlbumPicUrl => GetString("albumPic");

    /// <summary>Declared container/codec label, e.g. "mp3", "flac", "eac3".</summary>
    public string? Format => GetString("format");

    public int? Bitrate => GetInt32("bitrate");

    public int? SampleRate => GetInt32("sr");

    public int? Channels => GetInt32("channels");

    public int? BitsPerSample => GetInt32("bitsPerSample");

    public int? EncodedSampleRate => GetInt32("encodedSR");

    public double? VolumeDelta => GetDouble("volumeDelta");

    /// <summary>Track duration in milliseconds, as declared by the container.</summary>
    public long? DurationMilliseconds => GetInt64("duration");

    public TimeSpan? Duration => DurationMilliseconds is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    /// <summary>Performer names, flattened from the <c>[["name", id], ...]</c> shape.</summary>
    public IReadOnlyList<string> Artists
    {
        get
        {
            var names = new List<string>();

            if (Root is null)
            {
                return names;
            }

            if (Root["artist"] is JsonArray artistArray)
            {
                foreach (JsonNode? entry in artistArray)
                {
                    // Documented shape is ["name", id]; some payloads use a bare string or object.
                    switch (entry)
                    {
                        case JsonArray pair when pair.Count > 0 && pair[0] is JsonValue nameValue
                                                  && nameValue.TryGetValue(out string? name) && !string.IsNullOrWhiteSpace(name):
                            names.Add(name.Trim());
                            break;
                        case JsonValue scalar when scalar.TryGetValue(out string? scalarName) && !string.IsNullOrWhiteSpace(scalarName):
                            names.Add(scalarName.Trim());
                            break;
                        case JsonObject obj when obj["name"] is JsonValue objName
                                                 && objName.TryGetValue(out string? nested) && !string.IsNullOrWhiteSpace(nested):
                            names.Add(nested.Trim());
                            break;
                    }
                }
            }

            if (names.Count == 0)
            {
                foreach (string key in ArtistNameFallbacks)
                {
                    string? fallback = GetString(key);
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        names.Add(fallback.Trim());
                        break;
                    }
                }
            }

            return names;
        }
    }

    /// <summary>"Artist A / Artist B", or <c>null</c> when nothing was recorded.</summary>
    public string? ArtistDisplay => Artists.Count == 0 ? null : string.Join(" / ", Artists);

    public static NcmMetadata Parse(string rawJson)
    {
        JsonObject? root = null;
        try
        {
            root = JsonNode.Parse(rawJson) as JsonObject;
        }
        catch (JsonException)
        {
            // A malformed metadata block must not sink the conversion: the audio is still valid
            // and cover art can be recovered by search once a title is known from the filename.
        }

        return new NcmMetadata(rawJson, root);
    }

    private JsonNode? Field(string name) => Root?[name];

    private string? GetString(string name)
    {
        JsonNode? node = Field(name);
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out string? text))
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        // Some builds emit numeric identifiers in string-typed slots and vice versa.
        if (value.TryGetValue(out long number))
        {
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return null;
    }

    private long? GetInt64(string name)
    {
        JsonNode? node = Field(name);
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out long number))
        {
            return number;
        }

        if (value.TryGetValue(out string? text)
            && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        if (value.TryGetValue(out double dbl))
        {
            return (long)dbl;
        }

        return null;
    }

    private int? GetInt32(string name)
    {
        long? value = GetInt64(name);
        return value is null or < int.MinValue or > int.MaxValue ? null : (int)value.Value;
    }

    private double? GetDouble(string name)
    {
        JsonNode? node = Field(name);
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out double number))
        {
            return number;
        }

        if (value.TryGetValue(out string? text)
            && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return null;
    }
}
