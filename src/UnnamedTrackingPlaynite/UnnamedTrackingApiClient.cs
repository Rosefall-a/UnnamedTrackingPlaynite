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

public sealed class UnnamedTrackingUploadFailure
{
    public string GameName { get; set; } = string.Empty;
    public Guid? GameId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
}

public sealed class UnnamedTrackingUploadResult
{
    public int TotalGames { get; set; }
    public int SucceededGames { get; set; }
    public List<UnnamedTrackingUploadFailure> Failures { get; set; } = new List<UnnamedTrackingUploadFailure>();

    public int FailedGames => Failures.Count;
}

[DataContract]
internal sealed class UnnamedTrackingGamePayload
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
    [DataMember(Name = "links")] public List<UnnamedTrackingLinkPayload> Links { get; set; } = new List<UnnamedTrackingLinkPayload>();
    [DataMember(Name = "source")] public string Source { get; set; } = string.Empty;
    [DataMember(Name = "age_rating")] public string? AgeRating { get; set; }
    [DataMember(Name = "favorite")] public bool Favorite { get; set; }
    [DataMember(Name = "notes")] public string Notes { get; set; } = string.Empty;
    [DataMember(Name = "playtime_seconds")] public long PlaytimeSeconds { get; set; }
    [DataMember(Name = "rating_overall")] public decimal? RatingOverall { get; set; }
    [DataMember(Name = "status")] public string Status { get; set; } = string.Empty;
    [DataMember(Name = "folder_location")] public string FolderLocation { get; set; } = string.Empty;
}

[DataContract]
internal sealed class UnnamedTrackingGameUpdatePayload
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
    [DataMember(Name = "links")] public List<UnnamedTrackingLinkPayload> Links { get; set; } = new List<UnnamedTrackingLinkPayload>();
    [DataMember(Name = "source")] public string Source { get; set; } = string.Empty;
    [DataMember(Name = "age_rating")] public string? AgeRating { get; set; }
    [DataMember(Name = "favorite")] public bool Favorite { get; set; }
    [DataMember(Name = "notes")] public string Notes { get; set; } = string.Empty;
    [DataMember(Name = "playtime_seconds")] public long PlaytimeSeconds { get; set; }
    [DataMember(Name = "rating_overall")] public decimal? RatingOverall { get; set; }
    [DataMember(Name = "status")] public string Status { get; set; } = string.Empty;
    [DataMember(Name = "folder_location")] public string FolderLocation { get; set; } = string.Empty;
}

[DataContract]
internal sealed class UnnamedTrackingExistingGame
{
    [DataMember(Name = "id")] public Guid Id { get; set; }
    [DataMember(Name = "folder_location")] public string FolderLocation { get; set; } = string.Empty;
}

[DataContract]
internal sealed class UnnamedTrackingLinkPayload
{
    [DataMember(Name = "label")] public string Label { get; set; } = string.Empty;
    [DataMember(Name = "url")] public string Url { get; set; } = string.Empty;
}

public sealed class UnnamedTrackingApiClient
{
    private const string GameCreatePath = "/api/game/create";
    private const string GameListPath = "/api/game/list";
    private const string GameUpdatePath = "/api/game/";
    private const int GameListPageSize = 200;

    private readonly ILogger logger;

    public UnnamedTrackingApiClient(ILogger logger)
    {
        this.logger = logger;
    }

    public async Task<UnnamedTrackingUploadResult> UploadLibraryAsync(
        string apiUrl,
        string authValue,
        IEnumerable<Game> games)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            throw new InvalidOperationException("API URL is not configured.");
        }

        if (string.IsNullOrWhiteSpace(authValue))
        {
            throw new InvalidOperationException("Authentication token is not configured.");
        }

        var sourceGames = (games ?? Enumerable.Empty<Game>()).ToList();
        var result = new UnnamedTrackingUploadResult
        {
            TotalGames = sourceGames.Count
        };

        logger.Info($"Starting Unnamed Tracking library sync for {sourceGames.Count} Playnite games.");

        Dictionary<string, UnnamedTrackingExistingGame> existingGames;
        try
        {
            existingGames = await GetExistingGamesAsync(apiUrl, authValue).ConfigureAwait(false);
        }
        catch (UnnamedTrackingApiException ex)
        {
            logger.Error($"Unable to read existing Unnamed Tracking games before library sync: {ex}");

            foreach (var game in sourceGames)
            {
                result.Failures.Add(new UnnamedTrackingUploadFailure
                {
                    GameName = game?.Name ?? "<unknown game>",
                    GameId = game?.Id,
                    Operation = "Lookup",
                    StatusCode = ex.StatusCode,
                    ResponseBody = ex.ResponseBody
                });
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
                LogNullCollections(game);
                var payload = ToGamePayload(game);
                var folderLocation = payload.FolderLocation;

                if (existingGames.TryGetValue(folderLocation, out var existingGame))
                {
                    operation = "Update";
                    await UpdateGameAsync(apiUrl, authValue, existingGame.Id, ToUpdatePayload(payload)).ConfigureAwait(false);
                    logger.Info($"Updated Unnamed Tracking game for Playnite game '{game.Name}' ({game.Id}).");
                }
                else
                {
                    operation = "Create";
                    await CreateGameAsync(apiUrl, authValue, payload).ConfigureAwait(false);
                    logger.Info($"Created Unnamed Tracking game for Playnite game '{game.Name}' ({game.Id}).");
                }

                result.SucceededGames++;
            }
            catch (UnnamedTrackingApiException ex)
            {
                result.Failures.Add(new UnnamedTrackingUploadFailure
                {
                    GameName = game.Name ?? "<unnamed game>",
                    GameId = game.Id,
                    Operation = operation,
                    StatusCode = ex.StatusCode,
                    ResponseBody = ex.ResponseBody
                });

                logger.Error($"Failed to {operation.ToLowerInvariant()} Unnamed Tracking game '{game.Name}' ({game.Id}) with HTTP {ex.StatusCode}: {ex.ResponseBody}");
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

                logger.Error($"Failed to {operation.ToLowerInvariant()} Unnamed Tracking game '{game.Name}' ({game.Id}): {ex}");
            }
        }

        logger.Info($"Unnamed Tracking library sync finished. Total: {result.TotalGames}, succeeded: {result.SucceededGames}, failed: {result.FailedGames}.");
        return result;
    }

    private async Task<Dictionary<string, UnnamedTrackingExistingGame>> GetExistingGamesAsync(
        string apiUrl,
        string authValue)
    {
        var result = new Dictionary<string, UnnamedTrackingExistingGame>(StringComparer.OrdinalIgnoreCase);
        var skip = 0;

        while (true)
        {
            var endpoint = apiUrl.TrimEnd('/') + GameListPath + $"?skip={skip}&limit={GameListPageSize}";
            var response = await SendAsync(endpoint, authValue, "GET", null).ConfigureAwait(false);
            var games = Deserialize<List<UnnamedTrackingExistingGame>>(response.Body) ?? new List<UnnamedTrackingExistingGame>();

            foreach (var game in games)
            {
                if (game == null || string.IsNullOrWhiteSpace(game.FolderLocation))
                {
                    continue;
                }

                result[game.FolderLocation] = game;
            }

            if (games.Count < GameListPageSize)
            {
                break;
            }

            skip += GameListPageSize;
        }

        logger.Info($"Found {result.Count} existing Unnamed Tracking games for library sync.");
        return result;
    }

    private async Task CreateGameAsync(string apiUrl, string authValue, UnnamedTrackingGamePayload payload)
    {
        var endpoint = apiUrl.TrimEnd('/') + GameCreatePath;
        await SendAsync(endpoint, authValue, "POST", Serialize(payload)).ConfigureAwait(false);
    }

    private async Task UpdateGameAsync(
        string apiUrl,
        string authValue,
        Guid gameId,
        UnnamedTrackingGameUpdatePayload payload)
    {
        var endpoint = apiUrl.TrimEnd('/') + GameUpdatePath + gameId;
        await SendAsync(endpoint, authValue, "PATCH", Serialize(payload)).ConfigureAwait(false);
    }

    private async Task<UnnamedTrackingHttpResponse> SendAsync(
        string endpoint,
        string authValue,
        string method,
        string? body)
    {
        using (var client = new WebClient())
        {
            client.Headers[HttpRequestHeader.Authorization] = "Bearer " + authValue;
            client.Headers[HttpRequestHeader.Accept] = "application/json";
            if (body != null)
            {
                client.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";
            }

            try
            {
                string responseBody;
                if (method == "GET")
                {
                    responseBody = await client.DownloadStringTaskAsync(endpoint).ConfigureAwait(false);
                }
                else
                {
                    responseBody = await client.UploadStringTaskAsync(endpoint, method, body ?? string.Empty).ConfigureAwait(false);
                }

                return new UnnamedTrackingHttpResponse
                {
                    StatusCode = 200,
                    Body = responseBody ?? string.Empty
                };
            }
            catch (WebException ex)
            {
                var statusCode = 0;
                var responseBody = string.Empty;
                var response = ex.Response as HttpWebResponse;

                if (response != null)
                {
                    statusCode = (int)response.StatusCode;
                    try
                    {
                        using (var stream = response.GetResponseStream())
                        using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                        {
                            responseBody = reader.ReadToEnd();
                        }
                    }
                    catch (Exception readException)
                    {
                        logger.Error($"Unable to read Unnamed Tracking HTTP error response: {readException}");
                    }
                }

                throw new UnnamedTrackingApiException(statusCode, responseBody, ex);
            }
        }
    }

    private static string Serialize<T>(T payload)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, payload);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            return (T?)serializer.ReadObject(stream);
        }
    }

    private static UnnamedTrackingGamePayload ToGamePayload(Game game)
    {
        var tags = OrEmpty(game.Tags)
            .Where(tag => tag != null)
            .Select(tag => tag.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        tags.AddRange(OrEmpty(game.Genres)
            .Where(genre => genre != null)
            .Select(genre => genre.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => $"Genre: {name}"));

        tags.AddRange(OrEmpty(game.Platforms)
            .Where(platform => platform != null)
            .Select(platform => platform.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => $"Platform: {name}"));

        return new UnnamedTrackingGamePayload
        {
            Title = game.Name ?? string.Empty,
            SortTitle = string.IsNullOrWhiteSpace(game.SortingName) ? game.Name ?? string.Empty : game.SortingName ?? string.Empty,
            Description = game.Description ?? string.Empty,
            ReleaseDate = GetReleaseDate(game.ReleaseDate),
            Developer = JoinNames(game.Developers),
            Publisher = JoinNames(game.Publishers),
            Series = JoinNames(game.Series),
            Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Features = OrEmpty(game.Features)
                .Where(feature => feature != null)
                .Select(feature => feature.Name ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Collections = OrEmpty(game.Series)
                .Where(series => series != null)
                .Select(series => series.Name ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Links = OrEmpty(game.Links)
                .Where(link => link != null && !string.IsNullOrWhiteSpace(link.Url))
                .Select(link => new UnnamedTrackingLinkPayload
                {
                    Label = string.IsNullOrWhiteSpace(link.Name) ? "Playnite link" : link.Name ?? "Playnite link",
                    Url = link.Url ?? string.Empty
                })
                .ToList(),
            Source = game.Source?.Name ?? string.Empty,
            AgeRating = JoinNames(game.AgeRatings),
            Favorite = game.Favorite,
            Notes = game.Notes ?? string.Empty,
            PlaytimeSeconds = ToPlaytimeSeconds(game.Playtime),
            RatingOverall = game.UserScore.HasValue ? game.UserScore.Value / 10m : (decimal?)null,
            Status = MapStatus(game),
            FolderLocation = $"playnite-{game.Id:N}"
        };
    }

    private static UnnamedTrackingGameUpdatePayload ToUpdatePayload(UnnamedTrackingGamePayload payload)
    {
        return new UnnamedTrackingGameUpdatePayload
        {
            Title = payload.Title,
            SortTitle = payload.SortTitle,
            Description = payload.Description,
            ReleaseDate = payload.ReleaseDate,
            Developer = payload.Developer,
            Publisher = payload.Publisher,
            Series = payload.Series,
            Tags = payload.Tags,
            Features = payload.Features,
            Collections = payload.Collections,
            Links = payload.Links,
            Source = payload.Source,
            AgeRating = payload.AgeRating,
            Favorite = payload.Favorite,
            Notes = payload.Notes,
            PlaytimeSeconds = payload.PlaytimeSeconds,
            RatingOverall = payload.RatingOverall,
            Status = payload.Status,
            FolderLocation = payload.FolderLocation
        };
    }

    private void LogNullCollections(Game game)
    {
        var nullCollections = new List<string>();

        if (game.Tags == null) nullCollections.Add(nameof(game.Tags));
        if (game.Genres == null) nullCollections.Add(nameof(game.Genres));
        if (game.Platforms == null) nullCollections.Add(nameof(game.Platforms));
        if (game.Features == null) nullCollections.Add(nameof(game.Features));
        if (game.Developers == null) nullCollections.Add(nameof(game.Developers));
        if (game.Publishers == null) nullCollections.Add(nameof(game.Publishers));
        if (game.Series == null) nullCollections.Add(nameof(game.Series));
        if (game.Links == null) nullCollections.Add(nameof(game.Links));
        if (game.AgeRatings == null) nullCollections.Add(nameof(game.AgeRatings));

        if (nullCollections.Count > 0)
        {
            logger.Info($"Playnite game '{game.Name}' ({game.Id}) has null metadata collections: {string.Join(", ", nullCollections)}. Treating them as empty.");
        }
    }

    private static IEnumerable<T> OrEmpty<T>(IEnumerable<T>? source)
    {
        return source ?? Enumerable.Empty<T>();
    }

    private static string? JoinNames<T>(IEnumerable<T>? values) where T : DatabaseObject
    {
        var names = OrEmpty(values)
            .Where(value => value != null && !string.IsNullOrWhiteSpace(value.Name))
            .Select(value => value.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static string? GetReleaseDate(ReleaseDate? releaseDate)
    {
        if (!releaseDate.HasValue || releaseDate.Value.Year <= 0)
        {
            return null;
        }

        var date = releaseDate.Value.Date;
        return date.ToString("yyyy-MM-dd");
    }

    private static long ToPlaytimeSeconds(ulong playtime)
    {
        return playtime > long.MaxValue ? long.MaxValue : (long)playtime;
    }

    private static string MapStatus(Game game)
    {
        var status = game.CompletionStatus?.Name?.Trim().ToLowerInvariant() ?? string.Empty;

        if (status.Contains("wishlist") || status.Contains("wish list"))
        {
            return "WISHLIST";
        }

        if (status.Contains("dropped"))
        {
            return "DROPPED";
        }

        if (status.Contains("hold"))
        {
            return "ON_HOLD";
        }

        if (status.Contains("playing") || status.Contains("progress"))
        {
            return "PLAYING";
        }

        if (status.Contains("mastered"))
        {
            return "MASTERED";
        }

        if (status.Contains("beaten") || status.Contains("completed") || status == "complete")
        {
            return "BEATEN";
        }

        if (status.Contains("played"))
        {
            return "PLAYED";
        }

        return game.Playtime > 0 ? "PLAYED" : "BACKLOG";
    }

    private sealed class UnnamedTrackingHttpResponse
    {
        public int StatusCode { get; set; }
        public string Body { get; set; } = string.Empty;
    }

    private sealed class UnnamedTrackingApiException : Exception
    {
        public int StatusCode { get; }
        public string ResponseBody { get; }

        public UnnamedTrackingApiException(int statusCode, string responseBody, Exception innerException)
            : base(statusCode > 0 ? $"The server returned HTTP {statusCode}." : "The Unnamed Tracking server could not be reached.", innerException)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody ?? string.Empty;
        }
    }
}
