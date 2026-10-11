using System.Text.Json;
using NcmForge.Core.Tagging;

namespace NcmForge.Core.Covers;

/// <summary>
/// Cover art from NetEase Cloud Music itself.
/// </summary>
/// <remarks>
/// Three routes are tried in order of reliability: the URL the container already recorded, the
/// public song-detail endpoint keyed by song id, and finally a search by title and artist. The
/// public endpoints do not require authentication, which keeps the tool usable without the user
/// handing over an account.
/// </remarks>
public sealed class NetEaseCoverSource : ICoverSource
{
    private const string SongDetailEndpoint = "https://music.163.com/api/song/detail/?id={0}&ids=%5B{0}%5D";
    private const string SearchEndpoint = "https://music.163.com/api/search/get/web?s={0}&type=1&offset=0&total=true&limit=5";

    /// <summary>Requested pixel size for the CDN-rendered image.</summary>
    private const string PreferredSize = "1024y1024";

    public string Name => "netease";

    public async Task<CoverArt?> FindAsync(HttpClient http, CoverQuery query, CancellationToken cancellationToken)
    {
        // 1. The container's own metadata already named the artwork.
        if (!string.IsNullOrWhiteSpace(query.NetEaseCoverUrl))
        {
            CoverArt? direct = await CoverArtFetcher.DownloadAsync(
                http, WithSize(query.NetEaseCoverUrl!), Name, null, cancellationToken).ConfigureAwait(false);
            if (direct is not null)
            {
                return direct;
            }
        }

        long? songId = query.MusicId;

        // 2. Look the song up by id to obtain the canonical, current artwork URL.
        if (songId is { } id)
        {
            string? url = await GetArtworkUrlAsync(http, string.Format(SongDetailEndpoint, id), cancellationToken).ConfigureAwait(false);
            CoverArt? cover = await CoverArtFetcher.DownloadAsync(http, WithSize(url), Name, null, cancellationToken).ConfigureAwait(false);
            if (cover is not null)
            {
                return cover;
            }
        }

        // 3. Fall back to a text search, which also covers containers with no identifiers.
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            songId = await SearchAsync(http, query, cancellationToken).ConfigureAwait(false);
            if (songId is { } found)
            {
                string? url = await GetArtworkUrlAsync(http, string.Format(SongDetailEndpoint, found), cancellationToken).ConfigureAwait(false);
                CoverArt? cover = await CoverArtFetcher.DownloadAsync(http, WithSize(url), Name, null, cancellationToken).ConfigureAwait(false);
                if (cover is not null)
                {
                    return cover;
                }
            }
        }

        return null;
    }

    /// <summary>Appends the CDN's sizing parameter so a thumbnail URL yields a usable cover.</summary>
    private static string? WithSize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // NetEase image URLs accept ?param=WxH; an existing parameter is replaced.
        int queryIndex = url.IndexOf('?', StringComparison.Ordinal);
        string bare = queryIndex >= 0 ? url[..queryIndex] : url;
        return $"{bare}?param={PreferredSize}";
    }

    private static async Task<string?> GetArtworkUrlAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using JsonDocument? document = await CoverArtFetcher.GetJsonAsync(http, url, null, "netease", cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        if (document.RootElement.TryGetProperty("songs", out JsonElement songs)
            && songs.ValueKind == JsonValueKind.Array
            && songs.GetArrayLength() > 0
            && songs[0].TryGetProperty("album", out JsonElement album)
            && album.TryGetProperty("picUrl", out JsonElement picUrl))
        {
            return picUrl.GetString();
        }

        return null;
    }

    private static async Task<long?> SearchAsync(HttpClient http, CoverQuery query, CancellationToken cancellationToken)
    {
        string term = Uri.EscapeDataString(query.SearchText);
        string url = string.Format(SearchEndpoint, term);

        using JsonDocument? document = await CoverArtFetcher.GetJsonAsync(http, url, null, "netease", cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        if (document.RootElement.TryGetProperty("result", out JsonElement result)
            && result.TryGetProperty("songs", out JsonElement songs)
            && songs.ValueKind == JsonValueKind.Array
            && songs.GetArrayLength() > 0
            && songs[0].TryGetProperty("id", out JsonElement id)
            && id.TryGetInt64(out long value))
        {
            return value;
        }

        return null;
    }
}
