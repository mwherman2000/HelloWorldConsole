using System.Net;
using System.Net.Http;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.Logging.Abstractions;
using Svrn7.Trust.Google;
using Xunit;

namespace HelloWorldConsole.Tests;

public class ProductionHardeningTests
{
    [Fact]
    public void ParseClientSecretsJson_Corrupt_ThrowsClearError()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GoogleOAuthUtil.ParseClientSecretsJson("{not json", "client_secrets.json"));
        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadClientSecretsDocument_Installed()
    {
        using var doc = GoogleOAuthUtil.ParseClientSecretsJson("""
            {"installed":{"client_id":"abc","client_secret":"xyz"}}
            """);
        var (id, secret) = GoogleOAuthUtil.ReadClientSecretsDocument(doc);
        Assert.Equal("abc", id);
        Assert.Equal("xyz", secret);
    }

    [Fact]
    public void IsLoopbackClientMismatch_OnlySpecificTokenErrors()
    {
        Assert.True(GoogleOAuthUtil.IsLoopbackClientMismatch(
            new TokenResponseException(new TokenErrorResponse { Error = "redirect_uri_mismatch" })));
        Assert.True(GoogleOAuthUtil.IsLoopbackClientMismatch(
            new TokenResponseException(new TokenErrorResponse { Error = "unauthorized_client" })));
        Assert.True(GoogleOAuthUtil.IsLoopbackClientMismatch(
            new TokenResponseException(new TokenErrorResponse { Error = "invalid_client" })));
        Assert.False(GoogleOAuthUtil.IsLoopbackClientMismatch(
            new TokenResponseException(new TokenErrorResponse { Error = "invalid_request" })));
        Assert.False(GoogleOAuthUtil.IsLoopbackClientMismatch(
            new InvalidOperationException("invalid_request happened")));
    }

    [Theory]
    [InlineData(200, """{"access_token":"a"}""", "Succeeded")]
    [InlineData(400, """{"error":"authorization_pending"}""", "Pending")]
    [InlineData(400, """{"error":"slow_down"}""", "SlowDown")]
    [InlineData(400, """{"error":"access_denied"}""", "Denied")]
    [InlineData(400, """{"error":"expired_token"}""", "Expired")]
    public void InterpretDeviceTokenPoll_HttpStatusAndBody(int status, string body, string expected)
    {
        Assert.Equal(expected, GoogleOAuthUtil.InterpretDeviceTokenPoll(status, body).Disposition.ToString());
    }

    [Fact]
    public void InterpretDeviceTokenPoll_NonJson_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GoogleOAuthUtil.InterpretDeviceTokenPoll(500, "<html>nope</html>"));
    }

    [Fact]
    public async Task ResolveUserAsync_InvalidIdToken_FailsClosed()
    {
        var userInfoCalled = false;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GoogleIdentity.ResolveUserAsync(
                "client-id.apps.googleusercontent.com",
                "access-token",
                "not-a-valid.jwt",
                (_, _) =>
                {
                    userInfoCalled = true;
                    return Task.FromResult(new GoogleUser("sub", "a@b.c", "n", true));
                },
                NullLogger.Instance,
                CancellationToken.None));
        Assert.Contains("could not be verified", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(userInfoCalled);
    }

    [Fact]
    public async Task ResolveUserAsync_NoIdToken_UsesUserInfo()
    {
        var user = await GoogleIdentity.ResolveUserAsync(
            "client-id",
            "access-token",
            idToken: null,
            (_, _) => Task.FromResult(new GoogleUser("sub-1", "a@b.c", "Ada", true)),
            NullLogger.Instance,
            CancellationToken.None);
        Assert.Equal("sub-1", user.Subject);
    }

    [Fact]
    public async Task PostFormAsync_RetriesThenSucceeds()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}"""),
            });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var previousDelay = GoogleOAuthHttp.RetryDelayStep;
        GoogleOAuthHttp.RetryDelayStep = TimeSpan.FromMilliseconds(5);
        try
        {
            using (GoogleOAuthHttp.UseClient(client))
            {
                using var response = await GoogleOAuthHttp.PostFormAsync(
                    "https://oauth2.googleapis.com/token",
                    new Dictionary<string, string> { ["grant_type"] = "device_code" },
                    CancellationToken.None);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
        finally
        {
            GoogleOAuthHttp.RetryDelayStep = previousDelay;
        }

        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task DpapiJsonDataStore_CorruptCache_LogsAndStartsEmpty()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"loopback-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [9, 9, 9]);
        try
        {
            var store = new DpapiJsonDataStore(path, NullLogger.Instance);
            var value = await store.GetAsync<string>("missing");
            Assert.Null(value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
