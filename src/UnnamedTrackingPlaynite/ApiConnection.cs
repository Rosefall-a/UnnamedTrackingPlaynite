using System;
using System.Net;
using System.Threading;

namespace UnnamedTrackingPlaynite;

// Transport policy shared by the existing library client and save manager.
internal static class ApiConnection
{
    private static readonly AsyncLocal<CancellationToken> cancellation = new AsyncLocal<CancellationToken>();
    internal static CancellationToken Token => cancellation.Value;

    internal static string ValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Server URL must be HTTP or HTTPS, without credentials, query parameters, or a fragment.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    internal static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("utk_", StringComparison.Ordinal) || key.Length <= 4 ||
            System.Linq.Enumerable.Any(key, char.IsWhiteSpace))
            throw new InvalidOperationException("Use an Unnamed Tracking user API key beginning with utk_. Plugin-management tokens cannot synchronize games.");
    }

    internal static string DownloadUrl(string baseUrl, string path, Guid gameId, Guid archiveId, Guid versionId)
    {
        var expected = $"/api/game/{gameId}/archives/{archiveId}/versions/{versionId}/download";
        if (!string.Equals(path, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("The server returned an unexpected save download URL.");
        return ValidateBaseUrl(baseUrl) + expected;
    }

    internal static HttpWebRequest CreateRequest(string url)
    {
        Token.ThrowIfCancellationRequested();
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.AllowAutoRedirect = false;
        request.Timeout = 60000;
        request.ReadWriteTimeout = 60000;
        return request;
    }

    internal static HttpWebResponse EnsureSuccess(HttpWebResponse response)
    {
        if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300) return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new WebException("The server returned HTTP " + status + ". Redirects are not followed; configure the final server URL.", null, WebExceptionStatus.ProtocolError, response);
    }

    internal static IDisposable RegisterRequest(HttpWebRequest request) => new RequestCancellation(request);

    private sealed class RequestCancellation : IDisposable
    {
        private readonly CancellationTokenSource timeout;
        private readonly CancellationTokenRegistration registration;
        internal RequestCancellation(HttpWebRequest request)
        {
            timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            registration = timeout.Token.Register(request.Abort);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
        }
        public void Dispose() { registration.Dispose(); timeout.Dispose(); }
    }

    internal static IDisposable UseCancellation(CancellationToken token) => new CancellationScope(token);

    private sealed class CancellationScope : IDisposable
    {
        private readonly CancellationToken previous;
        internal CancellationScope(CancellationToken token) { previous = cancellation.Value; cancellation.Value = token; }
        public void Dispose() { cancellation.Value = previous; }
    }

    internal sealed class UploadClient : WebClient
    {
        protected override WebResponse GetWebResponse(WebRequest request, IAsyncResult result)
            => EnsureSuccess((HttpWebResponse)base.GetWebResponse(request, result));

        protected override WebRequest GetWebRequest(Uri address)
        {
            var request = base.GetWebRequest(address);
            if (request is HttpWebRequest http)
            {
                http.AllowAutoRedirect = false;
                http.Timeout = 60000;
                http.ReadWriteTimeout = 60000;
            }
            return request;
        }
    }
}
