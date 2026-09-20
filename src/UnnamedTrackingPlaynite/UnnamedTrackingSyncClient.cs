using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
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
internal sealed class UnnamedTrackingGameUpdatePayload
{
    [DataMember(Name = "playtime_seconds")] public long PlaytimeSeconds { get; set; }
    [DataMember(Name = "favorite")] public bool Favorite { get; set; }
    [DataMember(Name = "status")] public string Status { get; set; } = string.Empty;
}

[DataContract]
internal sealed class UnnamedTrackingSyncExistingGame
{
    [DataMember(Name = "id")] public Guid Id { get; set; }
    [DataMember(Name = "folder_location")] public string FolderLocation { get; set; } = string.Empty;
    [DataMember(Name = "playnite_guid")] public Guid? PlayniteGuid { get; set; }
}

[DataContract]
internal sealed class UnnamedTrackingSyncCreatedGame
{
    [DataMember(Name = "id")] public Guid Id { get; set; }
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

internal sealed class UnnamedTrackingSyncClient
{
    private const string CreatePath = "/api/game/create";
    private const string ListPath = "/api/game/list";
    private const int PageSize = 200;

    private readonly ILogger logger;
    private readonly IPlayniteAPI playniteApi;

    public UnnamedTrackingSyncClient(ILogger logger, IPlayniteAPI playniteApi)
    {
        this.logger = logger;
        this.playniteApi = playniteApi;
    }

    public async Task<UnnamedTrackingUploadResult> UploadLibraryAsync(string apiUrl, string authValue, IEnumerable<Game> games)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) throw new InvalidOperationException("API URL is not configured.");
        if (string.IsNullOrWhiteSpace(authValue)) throw new InvalidOperationException("Authentication token is not configured.");

        var sourceGames = (games ?? Enumerable.Empty<Game>()).ToList();
        var result = new UnnamedTrackingUploadResult { TotalGames = sourceGames.Count };
        Dictionary<string, UnnamedTrackingSyncExistingGame> existing;
        try
        {
            existing = await GetExistingGamesAsync(apiUrl, authValue).ConfigureAwait(false);
        }
        catch (UnnamedTrackingSyncApiException ex)
        {
            foreach (var game in sourceGames) result.Failures.Add(Failure(game, "Lookup", ex));
            return result;
        }

        foreach (var game in sourceGames)
        {
            if (game == null)
            {
                result.Failures.Add(new UnnamedTrackingUploadFailure { GameName = "<null Playnite game>", Operation = "Prepare", ResponseBody = "Playnite returned a null game entry." });
                continue;
            }

            string operation = "Prepare";
            try
            {
                var payload = ToGamePayload(game);
                UnnamedTrackingSyncExistingGame? remote = null;
                var found = existing.TryGetValue(payload.FolderLocation, out remote);
                if (!found)
                {
                    remote = existing.Values.FirstOrDefault(item => item.PlayniteGuid == game.Id);
                    found = remote != null;
                }

                Guid remoteId;
                if (found && remote != null)
                {
                    operation = "Update";
                    await SendJsonAsync(apiUrl.TrimEnd('/') + "/api/game/" + remote.Id, authValue, "PATCH", Serialize(payload)).ConfigureAwait(false);
                    remoteId = remote.Id;
                }
                else
                {
                    operation = "Create";
                    remoteId = await CreateGameAsync(apiUrl, authValue, payload).ConfigureAwait(false);
                }

                operation = "Artwork (key art)";
                try
                {
                    await UploadCoverIfAvailableAsync(apiUrl, authValue, remoteId, game).ConfigureAwait(false);
                }
                catch (UnnamedTrackingSyncApiException ex)
                {
                    result.Warnings.Add(Failure(game, operation, ex));
                    logger.Error($"Unnamed Tracking {operation.ToLowerInvariant()} warning for '{game.Name}' ({game.Id}): HTTP {ex.StatusCode}: {ex.ResponseBody}");
                }

                operation = "Artwork (banner)";
                try
                {
                    await UploadBannerIfAvailableAsync(apiUrl, authValue, remoteId, game).ConfigureAwait(false);
                }
                catch (UnnamedTrackingSyncApiException ex)
                {
                    result.Warnings.Add(Failure(game, operation, ex));
                    logger.Error($"Unnamed Tracking {operation.ToLowerInvariant()} warning for '{game.Name}' ({game.Id}): HTTP {ex.StatusCode}: {ex.ResponseBody}");
                }

                result.SucceededGames++;
            }
            catch (UnnamedTrackingSyncApiException ex)
            {
                result.Failures.Add(Failure(game, operation, ex));
                logger.Error($"Unnamed Tracking {operation.ToLowerInvariant()} failed for '{game.Name}' ({game.Id}): HTTP {ex.StatusCode}: {ex.ResponseBody}");
            }
            catch (Exception ex)
            {
                result.Failures.Add(new UnnamedTrackingUploadFailure { GameName = game.Name ?? "<unnamed game>", GameId = game.Id, Operation = operation, ResponseBody = ex.Message });
                logger.Error($"Unnamed Tracking {operation.ToLowerInvariant()} failed for '{game.Name}' ({game.Id}): {ex}");
            }
        }
        return result;
    }

    public async Task UpdateGameAsync(string apiUrl, string authValue, Game game)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) throw new InvalidOperationException("API URL is not configured.");
        if (string.IsNullOrWhiteSpace(authValue)) throw new InvalidOperationException("Authentication token is not configured.");
        if (game == null) throw new ArgumentNullException(nameof(game));

        var payload = ToGamePayload(game);
        var remoteGames = await GetExistingGamesAsync(apiUrl, authValue).ConfigureAwait(false);
        UnnamedTrackingSyncExistingGame? remote = null;
        var found = remoteGames.TryGetValue(payload.FolderLocation, out remote);
        if (!found) remote = remoteGames.Values.FirstOrDefault(item => item.PlayniteGuid == game.Id);
        if (remote == null) return;

        var update = new UnnamedTrackingGameUpdatePayload
        {
            PlaytimeSeconds = payload.PlaytimeSeconds,
            Favorite = payload.Favorite,
            Status = payload.Status
        };
        await SendJsonAsync(
            apiUrl.TrimEnd('/') + "/api/game/update/" + remote.Id,
            authValue,
            "PATCH",
            Serialize(update)).ConfigureAwait(false);
    }

    public async Task<bool> TestConnectionAsync(string apiUrl, string authValue)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) throw new InvalidOperationException("API URL is not configured.");
        if (string.IsNullOrWhiteSpace(authValue)) throw new InvalidOperationException("Authentication token is not configured.");

        await SendJsonAsync(
            apiUrl.TrimEnd('/') + ListPath + "?skip=0&limit=1",
            authValue,
            "GET",
            null).ConfigureAwait(false);
        return true;
    }

    private async Task<Dictionary<string, UnnamedTrackingSyncExistingGame>> GetExistingGamesAsync(string apiUrl, string authValue)
    {
        var result = new Dictionary<string, UnnamedTrackingSyncExistingGame>(StringComparer.OrdinalIgnoreCase);
        var skip = 0;
        while (true)
        {
            var response = await SendJsonAsync(apiUrl.TrimEnd('/') + ListPath + $"?skip={skip}&limit={PageSize}", authValue, "GET", null).ConfigureAwait(false);
            var games = Deserialize<List<UnnamedTrackingSyncExistingGame>>(response) ?? new List<UnnamedTrackingSyncExistingGame>();
            foreach (var game in games.Where(item => item != null && !string.IsNullOrWhiteSpace(item.FolderLocation))) result[game.FolderLocation] = game;
            if (games.Count < PageSize) break;
            skip += PageSize;
        }
        return result;
    }

    private async Task<Guid> CreateGameAsync(string apiUrl, string authValue, UnnamedTrackingSyncGamePayload payload)
    {
        var response = await SendJsonAsync(apiUrl.TrimEnd('/') + CreatePath, authValue, "POST", Serialize(payload)).ConfigureAwait(false);
        var created = Deserialize<UnnamedTrackingSyncCreatedGame>(response);
        if (created == null || created.Id == Guid.Empty) throw new InvalidOperationException("Game Create succeeded but the API did not return a game ID.");
        return created.Id;
    }

    private Task UploadCoverIfAvailableAsync(string apiUrl, string authValue, Guid remoteGameId, Game game)
    {
        return UploadImageIfAvailableAsync(apiUrl, authValue, remoteGameId, game.CoverImage, "cover.png", "key_art", "cover", game);
    }

    private Task UploadBannerIfAvailableAsync(string apiUrl, string authValue, Guid remoteGameId, Game game)
    {
        return UploadImageIfAvailableAsync(apiUrl, authValue, remoteGameId, game.BackgroundImage, "banner.png", "banner", "banner", game);
    }

    private async Task UploadImageIfAvailableAsync(string apiUrl, string authValue, Guid remoteGameId, string imageReference, string defaultFileName, string assetKind, string imageKind, Game game)
    {
        byte[] imageData;
        string fileName;
        if (!TryReadPlayniteImage(imageReference, defaultFileName, imageKind, game, out imageData, out fileName)) return;

        var endpoint = apiUrl.TrimEnd('/') + "/api/game/" + remoteGameId + "/assets/" + assetKind;
        var requestId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        logger.Info($"Unnamed Tracking HTTP request {requestId}: POST {endpoint} [multipart upload, file='{fileName}', bytes={imageData.Length}]");

        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + GetSafeExtension(fileName));
        File.WriteAllBytes(tempPath, imageData);
        try
        {
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.Authorization] = "Bearer " + authValue;
                client.Headers[HttpRequestHeader.Accept] = "application/json";
                try
                {
                    var response = await client.UploadFileTaskAsync(endpoint, "POST", tempPath).ConfigureAwait(false);
                    stopwatch.Stop();
                    var responseBody = response == null ? string.Empty : Encoding.UTF8.GetString(response);
                    logger.Info($"Unnamed Tracking HTTP response {requestId}: POST {endpoint} -> success ({stopwatch.ElapsedMilliseconds} ms), body={responseBody}");
                }
                catch (WebException ex)
                {
                    stopwatch.Stop();
                    var apiException = ToApiException(ex);
                    logger.Error($"Unnamed Tracking HTTP response {requestId}: POST {endpoint} -> HTTP {apiException.StatusCode} ({stopwatch.ElapsedMilliseconds} ms), body={apiException.ResponseBody}");
                    throw apiException;
                }
            }
        }
        finally { try { File.Delete(tempPath); } catch { } }
    }

    private bool TryReadPlayniteImage(string imageReference, string defaultFileName, string imageKind, Game game, out byte[] data, out string fileName)
    {
        data = Array.Empty<byte>();
        fileName = defaultFileName;
        if (string.IsNullOrWhiteSpace(imageReference)) return false;

        try
        {
            var path = playniteApi.Database.GetFullFilePath(imageReference);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                data = File.ReadAllBytes(path);
                fileName = Path.GetFileName(path);
                return data.Length > 0;
            }
        }
        catch (Exception ex)
        {
            logger.Info($"Could not read Playnite {imageKind} for '{game.Name}' ({game.Id}): {ex.Message}");
        }

        if (Uri.TryCreate(imageReference, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                using (var client = new WebClient()) data = client.DownloadData(uri);
                fileName = Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains(".")) fileName = defaultFileName;
                return data.Length > 0;
            }
            catch (Exception ex)
            {
                logger.Info($"Could not download Playnite {imageKind} for '{game.Name}' ({game.Id}): {ex.Message}");
            }
        }

        return false;
    }

    private async Task<string> SendJsonAsync(string endpoint, string authValue, string method, string? body)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        var bodyBytes = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);

        logger.Info($"Unnamed Tracking HTTP request {requestId}: {method} {endpoint} [content-type=application/json; charset=utf-8, bytes={bodyBytes.Length}, keep-alive=false, expect=false]");
        if (body != null)
        {
            logger.Info($"Unnamed Tracking HTTP request {requestId} body: {body}");
        }

        var request = (HttpWebRequest)WebRequest.Create(endpoint);
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
                await requestStream.WriteAsync(bodyBytes, 0, bodyBytes.Length).ConfigureAwait(false);
            }
        }

        try
        {
            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
            {
                var responseBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                stopwatch.Stop();
                logger.Info($"Unnamed Tracking HTTP response {requestId}: {method} {endpoint} -> HTTP {(int)response.StatusCode} {response.StatusDescription} ({stopwatch.ElapsedMilliseconds} ms), content-type={response.ContentType ?? string.Empty}, bytes={response.ContentLength}, body={responseBody}");
                return responseBody;
            }
        }
        catch (WebException ex)
        {
            stopwatch.Stop();
            var apiException = ToApiException(ex);
            logger.Error($"Unnamed Tracking HTTP response {requestId}: {method} {endpoint} -> HTTP {apiException.StatusCode} ({stopwatch.ElapsedMilliseconds} ms), body={apiException.ResponseBody}");
            throw apiException;
        }
    }

    private static UnnamedTrackingSyncApiException ToApiException(WebException ex)
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
        }
        return new UnnamedTrackingSyncApiException(status, body, ex);
    }

    private static UnnamedTrackingUploadFailure Failure(Game game, string operation, UnnamedTrackingSyncApiException ex) => new()
    {
        GameName = game?.Name ?? "<unknown game>", GameId = game?.Id, Operation = operation, StatusCode = ex.StatusCode, ResponseBody = ex.ResponseBody
    };

    private static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream()) { serializer.WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); }
    }

    private static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default!;
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) return (T)serializer.ReadObject(stream)!;
    }

    private static UnnamedTrackingSyncGamePayload ToGamePayload(Game game)
    {
        var tags = OrEmpty(game.Tags).Where(x => x != null).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        tags.AddRange(OrEmpty(game.Genres).Where(x => x != null).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => "Genre: " + x));
        tags.AddRange(OrEmpty(game.Platforms).Where(x => x != null).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => "Platform: " + x));
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
            Collections = OrEmpty(game.Series).Where(x => x != null).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
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

    private static string GetLegacyFolderLocation(Guid gameId) => "playnite-" + gameId.ToString("N");

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
        var status = game.CompletionStatus?.Name?.Trim().ToLowerInvariant() ?? string.Empty;
        if (status.Contains("wishlist") || status.Contains("wish list")) return "WISHLIST";
        if (status.Contains("dropped")) return "DROPPED";
        if (status.Contains("hold")) return "ON_HOLD";
        if (status.Contains("playing") || status.Contains("progress")) return "PLAYING";
        if (status.Contains("mastered")) return "MASTERED";
        if (status.Contains("beaten") || status.Contains("completed") || status == "complete") return "BEATEN";
        if (status.Contains("played")) return "PLAYED";
        return game.Playtime > 0 ? "PLAYED" : "BACKLOG";
    }
}