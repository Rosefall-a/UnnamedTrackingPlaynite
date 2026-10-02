using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace UnnamedTrackingPlaynite;

[DataContract]
internal sealed class UnnamedTrackingSyncGamePayload
{
    [DataMember(Name = "title")] public string Title { get; set; } = string.Empty;
    [DataMember(Name = "sort_title")] public string SortTitle { get; set; } = string.Empty;
    [DataMember(Name = "description")] public string Description { get; set; } = string.Empty;
    [DataMember(Name = "release_date")] public string? ReleaseDate { get; set; }
    [DataMember(Name = "developer")] public string? Developer { get; set; }
    [DataMember(Name = "publisher")] public string? Publisher { get; set; }
    [DataMember(Name = "series")] public string? Series { get; set; }
    [DataMember(Name = "tags")] public List<string> Tags { get; set; } = new List<string>();
    [DataMember(Name = "features")] public List<string> Features { get; set; } = new List<string>();
    [DataMember(Name = "collections")] public List<string> Collections { get; set; } = new List<string>();
    [DataMember(Name = "links")] public List<UnnamedTrackingSyncLink> Links { get; set; } = new List<UnnamedTrackingSyncLink>();
    [DataMember(Name = "source")] public string Source { get; set; } = string.Empty;
    [DataMember(Name = "age_rating")] public string? AgeRating { get; set; }
    [DataMember(Name = "favorite")] public bool Favorite { get; set; }
    [DataMember(Name = "notes")] public string Notes { get; set; } = string.Empty;
    [DataMember(Name = "playtime_seconds")] public long PlaytimeSeconds { get; set; }
    [DataMember(Name = "rating_overall")] public decimal? RatingOverall { get; set; }
    [DataMember(Name = "status")] public string Status { get; set; } = string.Empty;
    [DataMember(Name = "folder_location")] public string FolderLocation { get; set; } = string.Empty;
    [DataMember(Name = "playnite_guid")] public Guid PlayniteGuid { get; set; }
}

[DataContract]
internal sealed class UnnamedTrackingSyncExistingGame
{
    [DataMember(Name = "id")] public Guid Id { get; set; }
    [DataMember(Name = "folder_location")] public string FolderLocation { get; set; } = string.Empty;
    [DataMember(Name = "playnite_guid")] public Guid? PlayniteGuid { get; set; }
    [DataMember(Name = "title")] public string Title { get; set; } = string.Empty;
    [DataMember(Name = "source")] public string Source { get; set; } = string.Empty;
    [DataMember(Name = "parent_game_id")] public Guid? ParentGameId { get; set; }
}

[DataContract]
internal sealed class UnnamedTrackingSyncCreatedGame
{
    [DataMember(Name = "id")] public Guid Id { get; set; }
}

[DataContract]
internal sealed class UnnamedTrackingGameRelationshipPayload
{
    [DataMember(Name = "parent_game_id")] public Guid ParentGameId { get; set; }
    [DataMember(Name = "relationship_type")] public string RelationshipType { get; set; } = string.Empty;
}

[DataContract]
internal sealed class UnnamedTrackingSyncLink
{
    [DataMember(Name = "label")] public string Label { get; set; } = string.Empty;
    [DataMember(Name = "url")] public string Url { get; set; } = string.Empty;
}

internal sealed class UnnamedTrackingSyncApiException : Exception
{
    public int StatusCode { get; }
    public string ResponseBody { get; }

    public UnnamedTrackingSyncApiException(int statusCode, string responseBody, Exception inner)
        : base(statusCode > 0 ? $"The server returned HTTP {statusCode}." : "The Unnamed Tracking server could not be reached.", inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody ?? string.Empty;
    }
}

// Captured on Playnite's UI/event context. Workers only use these detached values.
internal sealed class SyncGameSnapshot
{
    public UnnamedTrackingSyncGamePayload Payload { get; set; } = new UnnamedTrackingSyncGamePayload();
    public string Name => Payload.Title;
    public Guid Id => Payload.PlayniteGuid;
    public bool Ignored { get; set; }
    public string CoverImage { get; set; } = string.Empty;
    public string BackgroundImage { get; set; } = string.Empty;
}

internal sealed class UnnamedTrackingSyncClient
{
    private const string CreatePath = "/api/game/create";
    private const string ListPath = "/api/game/list";
    private const int PageSize = 200;

    private readonly ILogger logger;
    private readonly IPlayniteAPI playniteApi;
    private readonly SemaphoreSlim mutationGate = new SemaphoreSlim(1, 1);

    public UnnamedTrackingSyncClient(ILogger logger, IPlayniteAPI playniteApi)
    {
        this.logger = logger;
        this.playniteApi = playniteApi;
    }

    internal List<SyncGameSnapshot> Capture(IEnumerable<Game> games, string ignoreTag)
    {
        return (games ?? Enumerable.Empty<Game>()).Where(game => game != null)
            .GroupBy(game => game.Id).Select(group => group.First())
            .Select(game => new SyncGameSnapshot
            {
                Payload = ToGamePayload(game),
                Ignored = HasIgnoreTag(game, ignoreTag),
                CoverImage = ResolveImage(game.CoverImage),
                BackgroundImage = ResolveImage(game.BackgroundImage)
            }).ToList();
    }

    private string ResolveImage(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return string.Empty;
        if (Uri.TryCreate(reference, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return reference;
        try { return playniteApi.Database.GetFullFilePath(reference) ?? string.Empty; }
        catch (Exception ex) { logger.Warn("Could not resolve a Playnite image: " + ex.Message); return string.Empty; }
    }

    public Task<UnnamedTrackingUploadResult> UploadLibraryAsync(string apiUrl, string authValue, IEnumerable<Game> games, string ignoreTag = "trackingapp_ignore", CancellationToken cancellationToken = default(CancellationToken), Action<int, int, string>? progress = null)
        => UploadSnapshotsAsync(apiUrl, authValue, Capture(games, ignoreTag), cancellationToken, progress);

    internal async Task<UnnamedTrackingUploadResult> UploadSnapshotsAsync(string apiUrl, string authValue, IEnumerable<SyncGameSnapshot> games, CancellationToken cancellationToken = default(CancellationToken), Action<int, int, string>? progress = null)
    {
        apiUrl = ApiConnection.ValidateBaseUrl(apiUrl);
        ApiConnection.ValidateKey(authValue);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var cancellation = ApiConnection.UseCancellation(cancellationToken);
        try
        {
            var sourceGames = games.Where(game => !game.Ignored).GroupBy(game => game.Id).Select(group => group.First()).ToList();
            var result = new UnnamedTrackingUploadResult { TotalGames = sourceGames.Count };
            if (sourceGames.Count == 0) return result;
            Dictionary<string, UnnamedTrackingSyncExistingGame> existing;
            try
            {
                existing = await GetExistingGamesAsync(apiUrl, authValue, cancellationToken).ConfigureAwait(false);
            }
            catch (UnnamedTrackingSyncApiException ex)
            {
                foreach (var game in sourceGames) result.Failures.Add(Failure(game, "Lookup", ex));
                return result;
            }

            for (var index = 0; index < sourceGames.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var game = sourceGames[index];
                progress?.Invoke(index, sourceGames.Count, game?.Name ?? "<null game>");
                if (game == null)
                {
                    result.Failures.Add(new UnnamedTrackingUploadFailure { GameName = "<null Playnite game>", Operation = "Prepare", ResponseBody = "Playnite returned a null game entry." });
                    continue;
                }

                string operation = "Prepare";
                try
                {
                    var payload = game.Payload;
                    var remote = MatchGame(existing.Values, payload);
                    var found = remote != null;
                    // Keep an existing remote folder stable when a Playnite title changes.
                    if (remote != null) payload.FolderLocation = remote.FolderLocation;

                    Guid remoteId;
                    if (found && remote != null)
                    {
                        operation = "Update";
                        await SendJsonAsync(apiUrl.TrimEnd('/') + "/api/game/update/" + remote.Id, authValue, "PATCH", Serialize(payload)).ConfigureAwait(false);
                        remoteId = remote.Id;
                        await ApplyAtLauncherParentAsync(apiUrl, authValue, remoteId, game, existing).ConfigureAwait(false);
                    }
                    else
                    {
                        operation = "Create";
                        remoteId = await CreateGameAsync(apiUrl, authValue, payload).ConfigureAwait(false);
                        await ApplyAtLauncherParentAsync(apiUrl, authValue, remoteId, game, existing).ConfigureAwait(false);
                    }

                    existing[remoteId.ToString()] = new UnnamedTrackingSyncExistingGame
                    {
                        Id = remoteId,
                        PlayniteGuid = game.Id,
                        FolderLocation = payload.FolderLocation,
                        Title = payload.Title,
                        Source = payload.Source
                    };
                    operation = "Artwork (key art)";
                    try
                    {
                        await UploadCoverIfAvailableAsync(apiUrl, authValue, remoteId, game).ConfigureAwait(false);
                    }
                    catch (UnnamedTrackingSyncApiException ex)
                    {
                        result.Warnings.Add(Failure(game, operation, ex));
                        logger.Error($"Unnamed Tracking {operation.ToLowerInvariant()} warning for '{game.Name}' ({game.Id}): HTTP {ex.StatusCode}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result.Warnings.Add(new UnnamedTrackingUploadFailure { GameName = game.Name, GameId = game.Id, Operation = operation, ResponseBody = ex.Message });
                        logger.Warn("Artwork warning for '" + game.Name + "': " + ex.Message);
                    }

                    operation = "Artwork (banner)";
                    try
                    {
                        await UploadBannerIfAvailableAsync(apiUrl, authValue, remoteId, game).ConfigureAwait(false);
                    }
                    catch (UnnamedTrackingSyncApiException ex)
                    {
                        result.Warnings.Add(Failure(game, operation, ex));
                        logger.Error($"Unnamed Tracking {operation.ToLowerInvariant()} warning for '{game.Name}' ({game.Id}): HTTP {ex.StatusCode}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result.Warnings.Add(new UnnamedTrackingUploadFailure { GameName = game.Name, GameId = game.Id, Operation = operation, ResponseBody = ex.Message });
                        logger.Warn("Artwork warning for '" + game.Name + "': " + ex.Message);
                    }

                    result.SucceededGames++;
                    progress?.Invoke(index + 1, sourceGames.Count, game.Name ?? "<unnamed game>");
                }
                catch (UnnamedTrackingSyncApiException ex)
                {
                    result.Failures.Add(Failure(game, operation, ex));
                    logger.Error($"Unnamed Tracking {operation.ToLowerInvariant()} failed for '{game.Name}' ({game.Id}): HTTP {ex.StatusCode}");
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Failures.Add(new UnnamedTrackingUploadFailure { GameName = game.Name ?? "<unnamed game>", GameId = game.Id, Operation = operation, ResponseBody = ex.Message });
                    logger.Error($"Unnamed Tracking {operation.ToLowerInvariant()} failed for '{game.Name}' ({game.Id}): {ex}");
                }
            }
            return result;
        }
        finally { mutationGate.Release(); }
    }

    public Task<UnnamedTrackingSyncPreviewResult> PreviewLibraryAsync(string apiUrl, string authValue, IEnumerable<Game> games, string ignoreTag = "trackingapp_ignore", CancellationToken cancellationToken = default(CancellationToken))
        => PreviewSnapshotsAsync(apiUrl, authValue, Capture(games, ignoreTag), cancellationToken);

    internal async Task<UnnamedTrackingSyncPreviewResult> PreviewSnapshotsAsync(string apiUrl, string authValue, IEnumerable<SyncGameSnapshot> games, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) throw new InvalidOperationException("API URL is not configured.");
        ApiConnection.ValidateKey(authValue);

        var sourceGames = games.GroupBy(game => game.Id).Select(group => group.First()).ToList();
        var result = new UnnamedTrackingSyncPreviewResult { TotalGames = sourceGames.Count };
        var existing = await GetExistingGamesAsync(apiUrl, authValue, cancellationToken).ConfigureAwait(false);

        foreach (var game in sourceGames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (game == null) continue;
            if (game.Ignored)
            {
                result.Ignored++;
                result.IgnoredGames.Add(game.Name ?? "<unnamed game>");
                continue;
            }

            var payload = game.Payload;
            var found = MatchGame(existing.Values, payload) != null;
            if (found)
            {
                result.WouldUpdate++;
                result.Updates.Add(game.Name ?? "<unnamed game>");
            }
            else
            {
                result.WouldCreate++;
                result.Creates.Add(game.Name ?? "<unnamed game>");
            }
        }
        return result;
    }

    public Task<bool> UpdateGameAsync(string apiUrl, string authValue, Game game, string ignoreTag = "trackingapp_ignore", CancellationToken cancellationToken = default(CancellationToken))
        => UpdateSnapshotAsync(apiUrl, authValue, Capture(new[] { game }, ignoreTag).Single(), cancellationToken);

    internal async Task<bool> UpdateSnapshotAsync(string apiUrl, string authValue, SyncGameSnapshot snapshot, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) throw new InvalidOperationException("API URL is not configured.");
        ApiConnection.ValidateKey(authValue);
        if (snapshot.Ignored) return false;
        var payload = snapshot.Payload;
        apiUrl = ApiConnection.ValidateBaseUrl(apiUrl);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var cancellation = ApiConnection.UseCancellation(cancellationToken);
        try
        {
            var remoteGames = await GetExistingGamesAsync(apiUrl, authValue, cancellationToken).ConfigureAwait(false);
            var remote = MatchGame(remoteGames.Values, payload);
            if (remote == null) return false;
            payload.FolderLocation = remote.FolderLocation;

            // The upstream GameUpdate API supports the full game metadata model.
            // Keep Playnite edits in sync rather than only sending playtime/favorite/status.
            // In particular, tags, features, collections, links and Playnite identity are
            // all supported by the upstream API and should not be silently dropped.
            await SendJsonAsync(
                apiUrl.TrimEnd('/') + "/api/game/update/" + remote.Id,
                authValue,
                "PATCH",
                Serialize(payload)).ConfigureAwait(false);

            await ApplyAtLauncherParentAsync(apiUrl, authValue, remote.Id, snapshot, remoteGames).ConfigureAwait(false);
            return true;
        }
        finally { mutationGate.Release(); }
    }

    public async Task<bool> IsGameLinkedAsync(string apiUrl, string authValue, Guid playniteGuid, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) throw new InvalidOperationException("API URL is not configured.");
        ApiConnection.ValidateKey(authValue);
        if (playniteGuid == Guid.Empty) return false;

        var existing = await GetExistingGamesAsync(apiUrl, authValue, cancellationToken).ConfigureAwait(false);
        return existing.Values.Any(game => game.PlayniteGuid == playniteGuid);
    }

    public async Task<bool> TestConnectionAsync(string apiUrl, string authValue, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) throw new InvalidOperationException("API URL is not configured.");
        ApiConnection.ValidateKey(authValue);

        apiUrl = ApiConnection.ValidateBaseUrl(apiUrl);
        using var cancellation = ApiConnection.UseCancellation(cancellationToken);
        var response = await SendJsonAsync(
            apiUrl.TrimEnd('/') + ListPath + "?skip=0&limit=1",
            authValue,
            "GET",
            null).ConfigureAwait(false);
        var games = Deserialize<List<UnnamedTrackingSyncExistingGame>>(response);
        if (games == null || games.Any(game => game == null || game.Id == Guid.Empty))
            throw new InvalidDataException("The server returned an invalid game list.");
        return true;
    }

    private async Task<Dictionary<string, UnnamedTrackingSyncExistingGame>> GetExistingGamesAsync(string apiUrl, string authValue, CancellationToken cancellationToken = default(CancellationToken))
    {
        apiUrl = ApiConnection.ValidateBaseUrl(apiUrl);
        using var cancellation = ApiConnection.UseCancellation(cancellationToken);
        var result = new Dictionary<string, UnnamedTrackingSyncExistingGame>(StringComparer.OrdinalIgnoreCase);
        var skip = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await SendJsonAsync(apiUrl.TrimEnd('/') + ListPath + $"?skip={skip}&limit={PageSize}", authValue, "GET", null).ConfigureAwait(false);
            var games = Deserialize<List<UnnamedTrackingSyncExistingGame>>(response);
            if (games == null || games.Any(game => game == null || game.Id == Guid.Empty || string.IsNullOrWhiteSpace(game.FolderLocation)))
                throw new InvalidDataException("The server returned an invalid game list. No games were created.");
            foreach (var game in games)
            {
                if (result.ContainsKey(game.Id.ToString())) throw new InvalidDataException("The server repeated a game while paging the library.");
                result[game.Id.ToString()] = game;
            }
            if (games.Count < PageSize) break;
            skip += PageSize;
        }
        return result;
    }

    private async Task<Guid> CreateGameAsync(string apiUrl, string authValue, UnnamedTrackingSyncGamePayload payload)
    {
        var response = await SendJsonAsync(
            apiUrl.TrimEnd('/') + CreatePath,
            authValue,
            "POST",
            Serialize(payload)).ConfigureAwait(false);
        var created = Deserialize<UnnamedTrackingSyncCreatedGame>(response);
        if (created == null || created.Id == Guid.Empty)
            throw new InvalidOperationException("Game Create succeeded but the API did not return a game ID.");

        return created.Id;
    }

    private Task UploadCoverIfAvailableAsync(string apiUrl, string authValue, Guid remoteGameId, SyncGameSnapshot game)
    {
        return UploadImageIfAvailableAsync(apiUrl, authValue, remoteGameId, game.CoverImage, "cover.png", "key_art", "cover", game);
    }

    private Task UploadBannerIfAvailableAsync(string apiUrl, string authValue, Guid remoteGameId, SyncGameSnapshot game)
    {
        return UploadImageIfAvailableAsync(apiUrl, authValue, remoteGameId, game.BackgroundImage, "banner.png", "banner", "banner", game);
    }

    private async Task UploadImageIfAvailableAsync(string apiUrl, string authValue, Guid remoteGameId, string imageReference, string defaultFileName, string assetKind, string imageKind, SyncGameSnapshot game)
    {
        if (string.IsNullOrWhiteSpace(imageReference)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ApiConnection.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var fileName = Path.GetFileName(imageReference);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = defaultFileName;
        byte[] imageData;
        if (Uri.TryCreate(imageReference, UriKind.Absolute, out var imageUri) &&
            (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps))
        {
            using var download = new WebClient();
            using var registration = deadline.Token.Register(download.CancelAsync);
            try { imageData = await download.DownloadDataTaskAsync(imageUri).ConfigureAwait(false); }
            catch (WebException) when (ApiConnection.Token.IsCancellationRequested) { throw new OperationCanceledException(ApiConnection.Token); }
        }
        else
        {
            if (!File.Exists(imageReference)) return;
            imageData = await Task.Run(() => File.ReadAllBytes(imageReference), ApiConnection.Token).ConfigureAwait(false);
        }
        if (imageData.Length == 0) return;
        var endpoint = apiUrl.TrimEnd('/') + "/api/game/" + remoteGameId + "/assets/" + assetKind;
        var requestId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        logger.Info($"Unnamed Tracking HTTP request {requestId}: POST {endpoint} [multipart upload, file='{fileName}', bytes={imageData.Length}]");

        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + GetSafeExtension(fileName));
        await Task.Run(() => File.WriteAllBytes(tempPath, imageData), ApiConnection.Token).ConfigureAwait(false);
        try
        {
            using (var client = new ApiConnection.UploadClient())
            {
                client.Headers[HttpRequestHeader.Authorization] = "Bearer " + authValue;
                client.Headers[HttpRequestHeader.Accept] = "application/json";
                using var registration = deadline.Token.Register(client.CancelAsync);
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var response = await client.UploadFileTaskAsync(endpoint, "POST", tempPath).ConfigureAwait(false);
                    stopwatch.Stop();
                    var responseBody = response == null ? string.Empty : Encoding.UTF8.GetString(response);
                    logger.Info($"Unnamed Tracking HTTP response {requestId}: POST {endpoint} -> success ({stopwatch.ElapsedMilliseconds} ms)");
                }
                catch (WebException) when (ApiConnection.Token.IsCancellationRequested) { throw new OperationCanceledException(ApiConnection.Token); }
                catch (WebException ex)
                {
                    stopwatch.Stop();
                    var apiException = ToApiException(ex, authValue);
                    logger.Error($"Unnamed Tracking HTTP response {requestId}: POST {endpoint} -> HTTP {apiException.StatusCode} ({stopwatch.ElapsedMilliseconds} ms)");
                    throw apiException;
                }
            }
        }
        finally { try { File.Delete(tempPath); } catch { } }
    }

    private async Task<string> SendJsonAsync(string endpoint, string authValue, string method, string? body)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        var bodyBytes = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);

        logger.Info($"Unnamed Tracking HTTP request {requestId}: {method} {endpoint} [content-type=application/json; charset=utf-8, bytes={bodyBytes.Length}, keep-alive=false, expect=false]");

        var request = ApiConnection.CreateRequest(endpoint);
        using var registration = ApiConnection.RegisterRequest(request);
        request.Method = method;
        request.Accept = "application/json";
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        request.KeepAlive = false;
        request.Expect = null;
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + authValue;

        if (body != null)
        {
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = bodyBytes.Length;
            using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            {
                await requestStream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ApiConnection.Token).ConfigureAwait(false);
            }
        }

        try
        {
            using (var response = ApiConnection.EnsureSuccess((HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)))
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
            {
                var responseBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                stopwatch.Stop();
                logger.Info($"Unnamed Tracking HTTP response {requestId}: {method} {endpoint} -> HTTP {(int)response.StatusCode} {response.StatusDescription} ({stopwatch.ElapsedMilliseconds} ms), content-type={response.ContentType ?? string.Empty}, bytes={response.ContentLength}");
                return responseBody;
            }
        }
        catch (WebException) when (ApiConnection.Token.IsCancellationRequested) { throw new OperationCanceledException(ApiConnection.Token); }
        catch (WebException ex)
        {
            stopwatch.Stop();
            var apiException = ToApiException(ex, authValue);
            logger.Error($"Unnamed Tracking HTTP response {requestId}: {method} {endpoint} -> HTTP {apiException.StatusCode} ({stopwatch.ElapsedMilliseconds} ms)");
            throw apiException;
        }
    }

    private static UnnamedTrackingSyncApiException ToApiException(WebException ex, string authValue)
    {
        var response = ex.Response as HttpWebResponse;
        var status = response == null ? 0 : (int)response.StatusCode;
        var body = string.Empty;
        if (response != null)
        {
            try
            {
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8)) body = reader.ReadToEnd();
            }
            catch { }
            finally { response.Dispose(); }
        }
        return new UnnamedTrackingSyncApiException(status, string.IsNullOrEmpty(authValue) ? body : body.Replace(authValue, "[redacted]"), ex);
    }

    private static UnnamedTrackingUploadFailure Failure(SyncGameSnapshot game, string operation, UnnamedTrackingSyncApiException ex) => new()
    {
        GameName = game?.Name ?? "<unknown game>",
        GameId = game?.Id,
        Operation = operation,
        StatusCode = ex.StatusCode,
        ResponseBody = ex.ResponseBody
    };

    private static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream()) { serializer.WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); }
    }

    private static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("The server returned an empty JSON response.");
        if (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(List<>) && !json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            throw new InvalidDataException("The server returned an object where a JSON array was required.");
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) return (T)serializer.ReadObject(stream)!;
    }

    private static UnnamedTrackingSyncGamePayload ToGamePayload(Game game)
    {
        // Preserve Playnite's user-visible library classifications in the upstream
        // fields it supports. Native tags stay untouched; metadata fields without a
        // one-to-one upstream field are represented as namespaced tags.
        var tags = OrEmpty(game.Tags).Where(x => x != null).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        tags.AddRange(OrEmpty(game.Genres).Where(x => x != null).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => "Genre: " + x));
        tags.AddRange(OrEmpty(game.Platforms).Where(x => x != null).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => "Platform: " + x));
        tags.AddRange(OrEmpty(game.Regions).Where(x => x != null).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => "Region: " + x));
        return new UnnamedTrackingSyncGamePayload
        {
            Title = game.Name ?? string.Empty,
            SortTitle = string.IsNullOrWhiteSpace(game.SortingName) ? game.Name ?? string.Empty : game.SortingName,
            Description = game.Description ?? string.Empty,
            ReleaseDate = game.ReleaseDate.HasValue && game.ReleaseDate.Value.Year > 0 ? game.ReleaseDate.Value.Date.ToString("yyyy-MM-dd") : null,
            Developer = JoinNames(game.Developers),
            Publisher = JoinNames(game.Publishers),
            Series = JoinNames(game.Series),
            Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Features = OrEmpty(game.Features).Where(x => x != null).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Collections = OrEmpty(game.Categories).Where(x => x != null).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Links = OrEmpty(game.Links).Where(x => x != null && !string.IsNullOrWhiteSpace(x.Url)).Select(x => new UnnamedTrackingSyncLink { Label = string.IsNullOrWhiteSpace(x.Name) ? "Playnite link" : x.Name, Url = x.Url }).ToList(),
            Source = game.Source?.Name ?? string.Empty,
            AgeRating = JoinNames(game.AgeRatings),
            Favorite = game.Favorite,
            Notes = game.Notes ?? string.Empty,
            PlaytimeSeconds = game.Playtime > long.MaxValue ? long.MaxValue : (long)game.Playtime,
            RatingOverall = game.UserScore.HasValue ? game.UserScore.Value / 10m : (decimal?)null,
            Status = MapStatus(game),
            FolderLocation = GetFolderLocation(game),
            PlayniteGuid = game.Id
        };
    }

    private async Task ApplyAtLauncherParentAsync(string apiUrl, string authValue, Guid remoteId, SyncGameSnapshot game, Dictionary<string, UnnamedTrackingSyncExistingGame> existing)
    {
        if (game.Payload.Source.IndexOf("ATLauncher", StringComparison.OrdinalIgnoreCase) < 0) return;

        var minecraft = existing.Values.FirstOrDefault(item =>
            string.Equals(item.Title?.Trim(), "Minecraft", StringComparison.OrdinalIgnoreCase));
        if (minecraft == null || minecraft.Id == Guid.Empty || minecraft.Id == remoteId)
        {
            logger.Warn($"ATLauncher game '{game.Name}' could not be linked to the Minecraft parent because no existing Minecraft game was found.");
            return;
        }

        var relationship = new UnnamedTrackingGameRelationshipPayload
        {
            ParentGameId = minecraft.Id,
            RelationshipType = "modpack"
        };
        await SendJsonAsync(apiUrl.TrimEnd('/') + "/api/game/update/" + remoteId, authValue, "PATCH", Serialize(relationship)).ConfigureAwait(false);
        logger.Info($"Linked ATLauncher game '{game.Name}' ({game.Id}) as a modpack child of Minecraft ({minecraft.Id}).");
    }

    internal static bool HasIgnoreTag(Game game, string ignoreTag)
    {
        return MatchesIgnoreTag(OrEmpty(game.Tags).Select(tag => tag?.Name ?? string.Empty), ignoreTag);
    }

    internal static bool MatchesIgnoreTag(IEnumerable<string> tags, string ignoreTag)
    {
        if (string.IsNullOrWhiteSpace(ignoreTag)) return false;
        return tags.Any(tag => string.Equals(tag?.Trim(), ignoreTag.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string GetFolderLocation(Game game)
    {
        var builder = new StringBuilder();

        foreach (var character in game.Name ?? "Unnamed Game")
        {
            var allowed =
                (character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') ||
                character == '_' ||
                character == '-';

            builder.Append(allowed ? character : '_');
        }

        var safeName = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "Unnamed_Game";

        if (safeName.Length > 100)
        {
            safeName = safeName.Substring(0, 100).Trim('_');
        }

        return $"playnite-{safeName}-{game.Id:N}";
    }

    internal static UnnamedTrackingSyncExistingGame? MatchGame(IEnumerable<UnnamedTrackingSyncExistingGame> games, UnnamedTrackingSyncGamePayload payload)
    {
        var byGuid = games.Where(game => game.PlayniteGuid == payload.PlayniteGuid).ToList();
        if (byGuid.Count > 1) throw new InvalidDataException("More than one remote game has this Playnite GUID. Resolve the duplicate on the server first.");
        if (byGuid.Count == 1) return byGuid[0];
        var folders = games.Where(game =>
            string.Equals(game.FolderLocation, payload.FolderLocation, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(game.FolderLocation, "playnite-" + payload.PlayniteGuid.ToString("N"), StringComparison.OrdinalIgnoreCase)).ToList();
        if (folders.Count > 1 || folders.Any(game => game.PlayniteGuid.HasValue && game.PlayniteGuid != payload.PlayniteGuid))
            throw new InvalidDataException("The remote folder belongs to a different or ambiguous Playnite game.");
        return folders.SingleOrDefault();
    }

    private static string GetSafeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension) || extension.Length > 8 ? ".png" : extension;
    }

    private static string? JoinNames<T>(IEnumerable<T> values) where T : DatabaseObject
    {
        var names = OrEmpty(values).Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static IEnumerable<T> OrEmpty<T>(IEnumerable<T> values) => values ?? Enumerable.Empty<T>();

    private static string MapStatus(Game game)
    {
        return NormalizeStatus(game.CompletionStatus?.Name, game.Playtime);
    }

    internal static string NormalizeStatus(string? name, ulong playtime)
    {
        var status = name?.Trim().ToLowerInvariant() ?? string.Empty;
        if (status.Contains("wishlist") || status.Contains("wish list")) return "WISHLIST";
        if (status.Contains("dropped")) return "DROPPED";
        if (status.Contains("hold")) return "ON_HOLD";
        if (status.Contains("playing") || status.Contains("progress")) return "PLAYING";
        if (status.Contains("mastered")) return "MASTERED";
        if (status.Contains("beaten") || status.Contains("completed") || status == "complete") return "BEATEN";
        if (status.Contains("played")) return "PLAYED";

        // Playnite allows custom completion statuses. Never send an arbitrary
        // custom name to the API: the server intentionally validates status
        // against its finite GameStatus enum. An unknown custom status has no
        // safe one-to-one mapping, so treat it as unclassified backlog. Only
        // use playtime as a fallback when Playnite has no completion status at all.
        return string.IsNullOrWhiteSpace(status)
            ? (playtime > 0 ? "PLAYED" : "BACKLOG")
            : "BACKLOG";
    }
}
