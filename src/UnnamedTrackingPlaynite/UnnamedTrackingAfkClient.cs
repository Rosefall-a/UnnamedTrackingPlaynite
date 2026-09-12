using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Playnite.SDK;

namespace UnnamedTrackingPlaynite;

[DataContract]
internal sealed class UnnamedTrackingAfkGameResponse
{
    [DataMember(Name = "game_id")] public Guid GameId { get; set; }
}

internal sealed class UnnamedTrackingAfkClient
{
    private readonly ILogger logger;

    public UnnamedTrackingAfkClient(ILogger logger)
    {
        this.logger = logger;
    }

    public async Task RecordAsync(string apiUrl, string authValue, Guid playniteGuid, long afkSeconds)
    {
        if (afkSeconds <= 0 || string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(authValue)) return;

        try
        {
            var lookup = await SendAsync(
                apiUrl.TrimEnd('/') + "/api/game/playnite/" + playniteGuid,
                authValue,
                "GET",
                null).ConfigureAwait(false);
            var game = Deserialize<UnnamedTrackingAfkGameResponse>(lookup);
            if (game == null || game.GameId == Guid.Empty)
            {
                logger.Info($"Unnamed Tracking could not resolve Playnite game {playniteGuid} for AFK reporting.");
                return;
            }

            var payload = $"{{\"seconds\":{afkSeconds}}}";
            await SendAsync(
                apiUrl.TrimEnd('/') + "/api/game/" + game.GameId + "/afk",
                authValue,
                "POST",
                payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error($"Unnamed Tracking AFK reporting failed for Playnite game {playniteGuid}: {ex}");
        }
    }

    private static async Task<string> SendAsync(string endpoint, string authValue, string method, string? body)
    {
        var request = (HttpWebRequest)WebRequest.Create(endpoint);
        request.Method = method;
        request.Accept = "application/json";
        request.KeepAlive = false;
        request.Expect = null;
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + authValue;

        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = bytes.Length;
            using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
        }

        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var stream = response.GetResponseStream())
        using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
        {
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            return serializer.ReadObject(stream) as T;
        }
    }
}
