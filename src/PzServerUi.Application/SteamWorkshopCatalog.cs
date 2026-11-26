using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PzServerUi.Application;

public sealed record WorkshopCard(string Id, string Title, string? PreviewPath, string? PreviewRevision);

/// <summary>Enriches configured Workshop IDs without modifying or downloading the configured mods.</summary>
public sealed partial class SteamWorkshopCatalog(HttpClient http, string cacheDirectory)
{
    private const string DetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private const int MetadataLimit = 2 * 1024 * 1024;
    private const int PreviewLimit = 4 * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);

    public static bool IsWorkshopId(string id) =>
        ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;

    public async Task EnrichAsync(IEnumerable<string> configuredIds, IProgress<WorkshopCard> progress,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in configuredIds.Where(id => IsWorkshopId(id) && seen.Add(id)).Chunk(50))
        {
            var entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            var stale = new List<string>();
            var now = DateTimeOffset.UtcNow;
            foreach (var id in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = await TryReadCacheAsync(id, cancellationToken).ConfigureAwait(false);
                if (entry is not null)
                {
                    entries[id] = entry;
                    Report(id, entry, progress);
                }
                if (entry is null || now - entry.UpdatedAt >= CacheLifetime)
                    stale.Add(id);
            }

            // Failures affect just their own item: a single removed entry does not invalidate the batch.
            if (stale.Count != 0)
            {
                var details = await TryGetDetailsAsync(stale, cancellationToken).ConfigureAwait(false);
                foreach (var item in details)
                {
                    var title = item?.Title?.Trim();
                    if (item is null || item.Result != 1 || item.AppId != 108600 || item.Id is not { } id
                        || !stale.Contains(id) || string.IsNullOrEmpty(title))
                        continue;
                    var entry = new CacheEntry(title, ValidPreview(item.Preview)?.AbsoluteUri,
                        entries.GetValueOrDefault(id)?.CachedPreview, now);
                    entries[id] = entry;
                    await TryWriteAsync(MetadataPath(id), JsonSerializer.SerializeToUtf8Bytes(entry, WorkshopJson.Default.CacheEntry),
                        cancellationToken).ConfigureAwait(false);
                    Report(id, entry, progress);
                }
            }

            await Parallel.ForEachAsync(entries, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken
            }, async (pair, token) =>
            {
                var (id, entry) = pair;
                if (ValidPreview(entry.Preview) is not { } uri
                    || (entry.Preview == entry.CachedPreview && File.Exists(PreviewPath(id))))
                    return;
                var bytes = await TryDownloadPreviewAsync(uri, token).ConfigureAwait(false);
                if (bytes is not null && await TryWriteAsync(PreviewPath(id), bytes, token).ConfigureAwait(false))
                {
                    var saved = entry with { CachedPreview = entry.Preview };
                    await TryWriteAsync(MetadataPath(id), JsonSerializer.SerializeToUtf8Bytes(saved, WorkshopJson.Default.CacheEntry), token)
                        .ConfigureAwait(false);
                    Report(id, saved, progress);
                }
            }).ConfigureAwait(false);
        }
    }

    private void Report(string id, CacheEntry entry, IProgress<WorkshopCard> progress)
    {
        var path = PreviewPath(id);
        progress.Report(new WorkshopCard(id, entry.Title, File.Exists(path) ? path : null, entry.CachedPreview));
    }

    private string MetadataPath(string id) => Path.Combine(cacheDirectory, id + ".json");
    private string PreviewPath(string id) => Path.Combine(cacheDirectory, id + ".cover");

    private async Task<CacheEntry?> TryReadCacheAsync(string id, CancellationToken token)
    {
        try
        {
            if (!File.Exists(MetadataPath(id)))
                return null;
            await using var stream = File.OpenRead(MetadataPath(id));
            if (stream.Length > MetadataLimit)
                return null;
            var entry = await JsonSerializer.DeserializeAsync(stream, WorkshopJson.Default.CacheEntry, token)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry?.Title) ? null : entry;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task<Details?[]> TryGetDetailsAsync(List<string> ids, CancellationToken token)
    {
        using var form = new FormUrlEncodedContent(ids.Select((id, i) =>
            new KeyValuePair<string, string>($"publishedfileids[{i}]", id))
            .Prepend(new("itemcount", ids.Count.ToString(CultureInfo.InvariantCulture))));
        using var request = new HttpRequestMessage(HttpMethod.Post, DetailsUrl) { Content = form };
        var bytes = await TryDownloadAsync(request, MetadataLimit, null, token).ConfigureAwait(false);
        try
        {
            return bytes is null
                ? []
                : JsonSerializer.Deserialize(bytes, WorkshopJson.Default.DetailsEnvelope)?.Response?.Items ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<byte[]?> TryDownloadPreviewAsync(Uri uri, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await TryDownloadAsync(request, PreviewLimit,
            ["image/png", "image/jpeg", "image/webp", "image/gif"], token).ConfigureAwait(false);
    }

    private async Task<byte[]?> TryDownloadAsync(HttpRequestMessage request, int limit, string[]? mediaTypes,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit
                || (mediaTypes is not null && !mediaTypes.Contains(response.Content.Headers.ContentType?.MediaType)))
                return null;

            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int length;
            while ((length = await input.ReadAsync(chunk.AsMemory(), deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + length > limit)
                    return null;
                buffer.Write(chunk, 0, length);
            }
            return buffer.Length == 0 ? null : buffer.ToArray();
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            || (error is OperationCanceledException && !token.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task<bool> TryWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        // Use a unique sibling so overlapping refreshes or shutdown never expose a partial entry.
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllBytesAsync(staged, bytes, token).ConfigureAwait(false);
            File.Move(staged, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(staged); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Uri? ValidPreview(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (uri.IdnHost.EndsWith(".steamusercontent.com", StringComparison.Ordinal)
            || uri.IdnHost == "steamuserimages-a.akamaihd.net")
            ? uri : null;

    private sealed record CacheEntry(string Title, string? Preview, string? CachedPreview, DateTimeOffset UpdatedAt);
    private sealed record DetailsEnvelope([property: JsonPropertyName("response")] DetailsResponse? Response);
    private sealed record DetailsResponse([property: JsonPropertyName("publishedfiledetails")] Details?[]? Items);
    private sealed record Details(
        [property: JsonPropertyName("publishedfileid")] string? Id,
        [property: JsonPropertyName("result")] int Result,
        [property: JsonPropertyName("consumer_app_id")] int AppId,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("preview_url")] string? Preview);

    [JsonSerializable(typeof(CacheEntry))]
    [JsonSerializable(typeof(DetailsEnvelope))]
    private sealed partial class WorkshopJson : JsonSerializerContext;
}
