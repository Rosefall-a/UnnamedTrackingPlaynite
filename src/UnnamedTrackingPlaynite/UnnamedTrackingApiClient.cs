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

public sealed class UnnamedTrackingUploadResult
{
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
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
    [DataMember(Name = "playtime_seconds")] public ulong PlaytimeSeconds { get; set; }
    [DataMember(Name = "rating_overall")] public decimal? RatingOverall { get; set; }
    [DataMember(Name = "status")] public string Status { get; set; } = string.Empty;
    [DataMember(Name = "folder_location")] public string FolderLocation { get; set; } = string.Empty;
    [DataMember(Name = "profiles_enabled")] public bool ProfilesEnabled { get; set; }
    [DataMember(Name = "osrs_stats_enabled")] public bool OsrsStatsEnabled { get; set; }
}

[DataContract]
internal sealed class UnnamedTrackingLinkPayload
{
    [DataMember(Name = "label")] public string Label { get; set; } = string.Empty;
    [DataMember(Name = "url")] public string Url { get; set; } = string.Empty;
}

public sealed class UnnamedTrackingApiClient
{
    private const string ImportLibraryPath = "/api/import/library";

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

        var endpoint = apiUrl.TrimEnd('/') + ImportLibraryPath;
        var payload = new List<UnnamedTrackingGamePayload>();
        var sourceGames = games ?? Enumerable.Empty<Game>();

        foreach (var game in sourceGames)
        {
            if (game == null)
            {
                logger.Error("Unnamed Tracking encountered a null Playnite game while preparing the library upload.");
                continue;
            }

            try
            {
                LogNullCollections(game);
                payload.Add(ToGamePayload(game));
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to prepare Playnite game '{game.Name}' ({game.Id}) for Unnamed Tracking upload: {ex}");
                throw;
            }
        }

        var json = Serialize(payload);

        using (var client = new WebClient())
        {
            client.Headers[HttpRequestHeader.Authorization] = "Bearer " + authValue;
            client.Headers[HttpRequestHeader.Accept] = "application/json";
            client.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";

            logger.Info($"Uploading {payload.Count} Playnite games to Unnamed Tracking.");

            try
            {
                var responseBody = await client.UploadStringTaskAsync(endpoint, "POST", json).ConfigureAwait(false);

                logger.Info("Unnamed Tracking library upload completed successfully.");

                return new UnnamedTrackingUploadResult
                {
                    StatusCode = 200,
                    ResponseBody = responseBody ?? string.Empty
                };
            }
            catch (WebException ex)
            {
                var statusCode = 0;
                var response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    statusCode = (int)response.StatusCode;
                }

                logger.Error($"Unnamed Tracking library upload failed with HTTP {statusCode}: {ex}");
                throw new InvalidOperationException(
                    statusCode > 0
                        ? $"The server returned HTTP {statusCode}."
                        : "The Unnamed Tracking server could not be reached.",
                    ex);
            }
        }
    }

    private static string Serialize(List<UnnamedTrackingGamePayload> payload)
    {
        var serializer = new DataContractJsonSerializer(typeof(List<UnnamedTrackingGamePayload>));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, payload);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static UnnamedTrackingGamePayload ToGamePayload(Game game)
    {
        var tags = OrEmpty(game.Tags)
            .Select(tag => tag.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        tags.AddRange(OrEmpty(game.Genres)
            .Select(genre => genre.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => $"Genre: {name}"));

        tags.AddRange(OrEmpty(game.Platforms)
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
                .Select(feature => feature.Name ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Collections = OrEmpty(game.Series)
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
            PlaytimeSeconds = game.Playtime,
            RatingOverall = game.UserScore.HasValue ? game.UserScore.Value / 10m : (decimal?)null,
            Status = MapStatus(game),
            FolderLocation = $"playnite-{game.Id:N}",
            ProfilesEnabled = false,
            OsrsStatsEnabled = false
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
}
