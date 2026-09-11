using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
        var json = Serialization.ToJson(payload);

        using (var client = new HttpClient())
        using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authValue);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            logger.Info($"Uploading {payload.Count} Playnite games to Unnamed Tracking.");

            using (var response = await client.SendAsync(request).ConfigureAwait(false))
            {
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    logger.Error($"Unnamed Tracking library upload failed with HTTP {(int)response.StatusCode}.");
                    throw new HttpRequestException(
                        $"The server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                }

                logger.Info("Unnamed Tracking library upload completed successfully.");

                return new UnnamedTrackingUploadResult
                {
                    StatusCode = (int)response.StatusCode,
                    ResponseBody = responseBody
                };
            }
        }
    }

    private static Dictionary<string, object> ToGamePayload(Game game)
    {
        var tags = game.Tags.Select(tag => tag.Name).ToList();
        tags.AddRange(game.Genres.Select(genre => $"Genre: {genre.Name}"));
        tags.AddRange(game.Platforms.Select(platform => $"Platform: {platform.Name}"));

        return new Dictionary<string, object>
        {
            ["title"] = game.Name ?? string.Empty,
            ["sort_title"] = string.IsNullOrWhiteSpace(game.SortingName) ? game.Name ?? string.Empty : game.SortingName,
            ["description"] = game.Description,
            ["release_date"] = GetReleaseDate(game.ReleaseDate),
            ["developer"] = JoinNames(game.Developers),
            ["publisher"] = JoinNames(game.Publishers),
            ["series"] = JoinNames(game.Series),
            ["tags"] = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ["features"] = game.Features.Select(feature => feature.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ["collections"] = game.Series.Select(series => series.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ["links"] = game.Links
                .Where(link => !string.IsNullOrWhiteSpace(link.Url))
                .Select(link => new Dictionary<string, object>
                {
                    ["label"] = string.IsNullOrWhiteSpace(link.Name) ? "Playnite link" : link.Name,
                    ["url"] = link.Url
                })
                .ToList(),
            ["source"] = game.Source?.Name,
            ["age_rating"] = JoinNames(game.AgeRatings),
            ["favorite"] = game.Favorite,
            ["notes"] = game.Notes,
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
            .Select(value => value.Name)
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
