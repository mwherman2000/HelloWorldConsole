using System.Net;

internal static class GoogleOAuthHttp
{
    private static readonly object Gate = new();
    private static HttpClient _http = CreateDefaultClient();

    internal static TimeSpan RetryDelayStep { get; set; } = TimeSpan.FromSeconds(2);

    internal static HttpClient Http
    {
        get
        {
            lock (Gate)
            {
                return _http;
            }
        }
    }

    internal static IDisposable UseClient(HttpClient client)
    {
        lock (Gate)
        {
            var previous = _http;
            _http = client;
            return new RestoreClient(previous);
        }
    }

    private static HttpClient CreateDefaultClient() =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

    internal static async Task<HttpResponseMessage> PostFormAsync(
        string url,
        Dictionary<string, string> form,
        CancellationToken cancellationToken) =>
        await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(form),
            },
            cancellationToken);

    internal static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = requestFactory();
                var response = await Http.SendAsync(request, cancellationToken);
                var code = (int)response.StatusCode;
                var retryable = code is (int)HttpStatusCode.TooManyRequests
                    or (>= 500 and <= 599);
                if (retryable && attempt < maxAttempts)
                {
                    response.Dispose();
                    await Task.Delay(RetryDelayStep * attempt, cancellationToken);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                await Task.Delay(RetryDelayStep * attempt, cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                lastException = ex;
                await Task.Delay(RetryDelayStep * attempt, cancellationToken);
            }
        }

        throw new InvalidOperationException("Google request failed after retries.", lastException);
    }

    internal static async Task<System.Text.Json.JsonDocument> ReadJsonDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!GoogleOAuthUtil.TryParseJson(text, out var document) || document is null)
        {
            var reason = string.IsNullOrWhiteSpace(text) ? "empty" : "non-JSON";
            throw new InvalidOperationException($"Google returned a {reason} body (HTTP {(int)response.StatusCode}).");
        }

        return document;
    }

    private sealed class RestoreClient(HttpClient previous) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
            {
                _http = previous;
            }
        }
    }
}
