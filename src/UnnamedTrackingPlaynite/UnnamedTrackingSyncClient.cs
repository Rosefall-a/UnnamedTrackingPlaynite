using System;
using System.Collections.Generic;
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

    public async Task<UnnamedTrackingUploadResult> UploadLibraryAsync(
        string apiUrl,
        string authValue,
        IEnumerable<Game> games)
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
            foreach (var game in sourceGames)
            {
                result.Failures.Add(Failure(game, "Lookup", ex));
            }
            return result;
        }

        foreach (var game in sourceGames)
        {
            if (game == null)
            {
                result.Failures.Add(new UnnamedTrackingUploadFailure
                {
                    GameName = "<null Playnite game>",
                    Operation = "Prepare",
                    ResponseBody = "Playnite returned a null game entry."
                });
                continue;
            }

            string operation = "Prepare";
            try
            {
                var payload = ToGamePayload(game);
                UnnamedTrackingSyncExistingGame remote;
                var found = existing.TryGetValue(payload.FolderLocation, out remote);
                if (!found)
                {
                    found = existing.Values.FirstOrDefault(item => item.PlayniteGuid == game.Id) is UnnamedTrackingSyncExistingGame guidMatch;
                    remote = guidMatch;
                }

                Guid remoteId;
                if (found)
                {
                    operation = "Update";
                    await SendJsonAsync(
                        apiUrl.TrimEnd('/') + "/api/game/" + remote.Id,
                        authValue,
                        "PATCH",
                        Serialize(payload)).ConfigureAwait(false);
                    remoteId = remote.Id;
                }
                else
                {
                    operation = "Create";
                    remoteId = await CreateGameAsync(apiUrl, authValue, payload).ConfigureAwait(false);
                }

                operation = "Artwork";
                await UploadCoverIfAvailableAsync(apiUrl, authValue, remoteId, game).ConfigureAwait(false);
                result.SucceededGames++;
            }
            catch (UnnamedTrackingSyncApiException ex)
            {
                result.Failures.Add(Failure(game, operation, ex));
                logger.Error($"Unnamed Tracking {operation.ToLowerInvariant()} failed for '{game.Name}' ({game.Id}): HTTP {ex.StatusCode}: {ex.ResponseBody}");
            }
            catch (Exception ex)
            {
                result.Failures.Add(new UnnamedTrackingUploadFailure
                {
                    GameName = game.Name ?? "<unnamed game>",
                    GameId = game.Id,
                    Operation = operation,
                    ResponseBody = ex.Message
                });
                logger.Error($"Unnamed Tracking {operation.ToLowerInvariant()} failed for '{game.Name}' ({game.Id}): {ex}");
            }
        }

        return result;
    }

    private async Task<Dictionary<string, UnnamedTrackingSyncExistingGame>> GetExistingGamesAsync(string apiUrl, string authValue)
    {
        var result = new Dictionary<string, UnnamedTrackingSyncExistingGame>(StringComparer.OrdinalIgnoreCase);
        var skip = 0;

        while (true)
        {
            var response = await SendJsonAsync(
                apiUrl.TrimEnd('/') + ListPath + $"?skip={skip}&limit={PageSize}",
                authValue,
                "GET",
                null).ConfigureAwait(false);
            var games = Deserialize<List<UnnamedTrackingSyncExistingGame>>(response) ?? new List<UnnamedTrackingSyncExistingGame>();

            foreach (var game in games.Where(item => item != null && !string.IsNullOrWhiteSpace(item.FolderLocation)))
            {
                result[game.FolderLocation] = game;
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
        {
            throw new InvalidOperationException("Game Create succeeded but the API did not return a game ID.");
        }

        return created.Id;
    }

    private async Task UploadCoverIfAvailableAsync(string apiUrl, string authValue, Guid remoteGameId, Game game)
    {
        byte[] imageData;
        string fileName;
        if (!TryReadPlayniteCover(game, out imageData, out fileName))
        {
            return;
        }

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
                    await client.UploadFileTaskAsync(
                        apiUrl.TrimEnd('/') + "/api/game/" + remoteGameId + "/assets/key_art",
                        "POST",
                        tempPath).ConfigureAwait(false);
                }
                catch (WebException ex)
                {
                    throw ToApiException(ex);
                }
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private bool TryReadPlayniteCover(Game game, out byte[] data, out string fileName)
    {
        data = null;
        fileName = "cover.png";
        if (string.IsNullOrWhiteSpace(game.CoverImage)) return false;

        try
        {
            var path = playniteApi.Database.GetFullFilePath(game.CoverImage);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                data = File.ReadAllBytes(path);
                fileName = Path.GetFileName(path);
                return data.Length > 0;
            }
        }
        catch (Exception ex)
        {
            logger.Info($"Could not read Playnite cover for '{game.Name}' ({game.Id}): {ex.Message}");
        }

        if (Uri.TryCreate(game.CoverImage, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                using (var client = new WebClient())
                {
                    data = client.DownloadData(uri);
                }
                fileName = Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains(".")) fileName = "cover.png";
                return data.Length > 0;
            }
            catch (Exception ex)
            {
                logger.Info($"Could not download Playnite cover for '{game.Name}' ({game.Id}): {ex.Message}");
            }
        }

        return false;
    }

    private async Task<string> SendJsonAsync(string endpoint, string authValue, string method, string body)
    {
        using (var client = new WebClient())
        {
            client.Headers[HttpRequestHeader.Authorization] = "Bearer " + authValue;
            client.Headers[HttpRequestHeader.Accept] = "application/json";
            if (body != null) client.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";

            try
            {
                if (method == "GET") return await client.DownloadStringTaskAsync(endpoint).ConfigureAwait(false);
                return await client.UploadStringTaskAsync(endpoint, method, body ?? string.Empty).ConfigureAwait(false);
            }
            catch (WebException ex)
            {
                throw ToApiException(ex);
            }
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
                using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }
            }
            catch { }
        }
        return new UnnamedTrackingSyncApiException(status, body, ex);
    }

    private static UnnamedTrackingUploadFailure Failure(Game game, string operation, UnnamedTrackingSyncApiException ex)
    {
        return new UnnamedTrackingUploadFailure
        {
            GameName = game?.Name ?? "<unknown game>",
            GameId = game?.Id,
            Operation = operation,
            StatusCode = ex.StatusCode,
            ResponseBody = ex.ResponseBody
        };
    }

    private static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, value);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default(T);
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            return (T)serializer.ReadObject(stream);
        }
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
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (var character in game.Name ?? "Unnamed Game")
        {
            builder.Append(invalid.Contains(character) || character == '/' || character == '\\' ? '_' : character);
        }

        var safeName = builder.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "Unnamed Game";
        if (safeName.Length > 100) safeName = safeName.Substring(0, 100).TrimEnd(' ', '.');
        return $"playnite-{safeName}-{game.Id:N}";
    }

    private static string GetLegacyFolderLocation(Guid gameId) => "playnite-" + gameId.ToString("N");

    private static string GetSafeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension) || extension.Length > 8 ? ".png" : extension;
    }

    private static string JoinNames<T>(IEnumerable<T> values) where T : DatabaseObject
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
