using System.Text.Json;
using NcmForge.Core.Tagging;

namespace NcmForge.Core.Covers;

/// <summary>Cover art from the iTunes Search API. No key required, good coverage of Western releases.</summary>
public sealed class ITunesCoverSource : ICoverSource
{
    public string Name => "itunes";

    public async Task<CoverArt?> FindAsync(HttpClient http, CoverQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.SearchText))
        {
            return null;
        }

        string term = Uri.EscapeDataString(query.SearchText);
        string url = $"https://itunes.apple.com/search?term={term}&entity=song&limit=5";

        using JsonDocument? document = await CoverArtFetcher.GetJsonAsync(http, url, null, Name, cancellationToken).ConfigureAwait(false);
        if (document is null
            || !document.RootElement.TryGetProperty("results", out JsonElement results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return null;
        }

        foreach (JsonElement result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("artworkUrl100", out JsonElement artwork))
            {
                continue;
            }

            string? thumbnail = artwork.GetString();
            if (string.IsNullOrWhiteSpace(thumbnail))
            {
                continue;
            }

            // The CDN serves any size by rewriting the dimension segment.
            string full = thumbnail
                .Replace("100x100bb", "1400x1400bb", StringComparison.Ordinal)
                .Replace("100x100", "1400x1400", StringComparison.Ordinal);

            CoverArt? cover = await CoverArtFetcher.DownloadAsync(http, full, Name, null, cancellationToken).ConfigureAwait(false);
            if (cover is not null)
            {
                return cover;
            }
        }

        return null;
    }
}

/// <summary>Cover art from the Deezer public API. No key required, strong non-Western coverage.</summary>
public sealed class DeezerCoverSource : ICoverSource
{
    public string Name => "deezer";

    public async Task<CoverArt?> FindAsync(HttpClient http, CoverQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.SearchText))
        {
            return null;
        }

        string term = Uri.EscapeDataString(query.SearchText);
        string url = $"https://api.deezer.com/search?q={term}&limit=5";

        using JsonDocument? document = await CoverArtFetcher.GetJsonAsync(http, url, null, Name, cancellationToken).ConfigureAwait(false);
        if (document is null
            || !document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement track in data.EnumerateArray())
        {
            if (!track.TryGetProperty("album", out JsonElement album))
            {
                continue;
            }

            foreach (string property in (string[])["cover_xl", "cover_big", "cover_medium"])
            {
                if (album.TryGetProperty(property, out JsonElement cover)
                    && cover.GetString() is { Length: > 0 } coverUrl)
                {
                    CoverArt? art = await CoverArtFetcher.DownloadAsync(http, coverUrl, Name, null, cancellationToken).ConfigureAwait(false);
                    if (art is not null)
                    {
                        return art;
                    }
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Cover art from MusicBrainz matched releases via the Cover Art Archive. Last resort: it is rate
/// limited and depends on a community-maintained database.
/// </summary>
public sealed class CoverArtArchiveSource : ICoverSource
{
    public string Name => "coverartarchive";

    public async Task<CoverArt?> FindAsync(HttpClient http, CoverQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.SearchText))
        {
            return null;
        }

        string search = Uri.EscapeDataString(query.SearchText);
        string url = $"https://musicbrainz.org/ws/2/release?query={search}&fmt=json&limit=5";

        using JsonDocument? document = await CoverArtFetcher.GetJsonAsync(http, url, null, Name, cancellationToken).ConfigureAwait(false);
        if (document is null
            || !document.RootElement.TryGetProperty("releases", out JsonElement releases)
            || releases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement release in releases.EnumerateArray())
        {
            if (!release.TryGetProperty("id", out JsonElement id)
                || id.GetString() is not { Length: > 0 } mbid)
            {
                continue;
            }

            CoverArt? cover = await CoverArtFetcher.DownloadAsync(
                http, $"https://coverartarchive.org/release/{mbid}/front-1200", Name, null, cancellationToken).ConfigureAwait(false);

            if (cover is not null)
            {
                return cover;
            }
        }

        return null;
    }
}
