using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace UnnamedTrackingPlaynite;

public sealed class UnnamedTrackingUploadResult
{
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
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
        var payload = games.Select(ToGamePayload).ToList();
        var json = new JavaScriptSerializer().Serialize(payload);

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

                logger.Error($"Unnamed Tracking library upload failed with HTTP {statusCode}.");
                throw new InvalidOperationException(
                    statusCode > 0
                        ? $"The server returned HTTP {statusCode}."
                        : "The Unnamed Tracking server could not be reached.",
                    ex);
            }
        }
    }

    private static Dictionary<string, object> ToGamePayload(Game game)
    {
        var tags = game.Tags
            .Select(tag => tag.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        tags.AddRange(game.Genres
            .Select(genre => genre.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => $"Genre: {name}"));

        tags.AddRange(game.Platforms
            .Select(platform => platform.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => $"Platform: {name}"));

        return new Dictionary<string, object>
        {
            ["title"] = game.Name ?? string.Empty,
            ["sort_title"] = string.IsNullOrWhiteSpace(game.SortingName) ? game.Name ?? string.Empty : game.SortingName ?? string.Empty,
            ["description"] = game.Description ?? string.Empty,
            ["release_date"] = GetReleaseDate(game.ReleaseDate),
            ["developer"] = JoinNames(game.Developers),
            ["publisher"] = JoinNames(game.Publishers),
            ["series"] = JoinNames(game.Series),
            ["tags"] = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ["features"] = game.Features
                .Select(feature => feature.Name ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ["collections"] = game.Series
                .Select(series => series.Name ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ["links"] = game.Links
                .Where(link => !string.IsNullOrWhiteSpace(link.Url))
                .Select(link => new Dictionary<string, object>
                {
                    ["label"] = string.IsNullOrWhiteSpace(link.Name) ? "Playnite link" : link.Name ?? "Playnite link",
                    ["url"] = link.Url ?? string.Empty
                })
                .ToList(),
            ["source"] = game.Source?.Name ?? string.Empty,
            ["age_rating"] = JoinNames(game.AgeRatings),
            ["favorite"] = game.Favorite,
            ["notes"] = game.Notes ?? string.Empty,
            ["playtime_seconds"] = game.Playtime,
            ["rating_overall"] = game.UserScore.HasValue ? game.UserScore.Value / 10m : (decimal?)null,
            ["status"] = MapStatus(game),
            ["folder_location"] = $"playnite-{game.Id:N}",
            ["profiles_enabled"] = false,
            ["osrs_stats_enabled"] = false
        };
    }

    private static string? JoinNames<T>(IEnumerable<T> values) where T : DatabaseObject
    {
        var names = values
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
