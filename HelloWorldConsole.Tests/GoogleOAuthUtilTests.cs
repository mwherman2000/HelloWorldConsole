using System.Text;
using System.Text.Json;
using Svrn7.Trust.Google;
using Xunit;

namespace HelloWorldConsole.Tests;

public class GoogleOAuthUtilTests
{
    [Theory]
    [InlineData("https://www.google.com/device", true)]
    [InlineData("https://google.com/device", true)]
    [InlineData("https://accounts.google.com/o/oauth2/device", true)]
    [InlineData("http://www.google.com/device", false)]
    [InlineData("https://evil.example/device", false)]
    [InlineData("https://google.com.evil.com/device", false)]
    [InlineData("not-a-url", false)]
    public void IsAllowedVerificationUrl(string url, bool expected)
    {
        Assert.Equal(expected, GoogleOAuthUtil.IsAllowedVerificationUrl(url));
    }

    [Fact]
    public void TryGetEnvCredentials_NeitherSet_ReturnsFalse()
    {
        Assert.False(GoogleOAuthUtil.TryGetEnvCredentials(null, null, out _, out _, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryGetEnvCredentials_Partial_ReturnsError()
    {
        Assert.False(GoogleOAuthUtil.TryGetEnvCredentials("id-only", null, out _, out _, out var error));
        Assert.Contains("both", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetEnvCredentials_BothSet()
    {
        Assert.True(GoogleOAuthUtil.TryGetEnvCredentials("id", "secret", out var id, out var secret, out var error));
        Assert.Null(error);
        Assert.Equal("id", id);
        Assert.Equal("secret", secret);
    }

    [Theory]
    [InlineData("authorization_pending", "Pending")]
    [InlineData("slow_down", "SlowDown")]
    [InlineData("access_denied", "Denied")]
    [InlineData("expired_token", "Expired")]
    [InlineData("invalid_grant", "Unknown")]
    [InlineData(null, "Unknown")]
    public void MapDevicePollError(string? error, string expected)
    {
        Assert.Equal(expected, GoogleOAuthUtil.MapDevicePollError(error).ToString());
    }

    [Fact]
    public void TryParseJson_RejectsEmptyAndHtml()
    {
        Assert.False(GoogleOAuthUtil.TryParseJson(" ", out _));
        Assert.False(GoogleOAuthUtil.TryParseJson("<html></html>", out _));
        Assert.True(GoogleOAuthUtil.TryParseJson("""{"error":"authorization_pending"}""", out var doc));
        using (doc)
        {
            Assert.Equal("authorization_pending", doc!.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public void ProtectRoundTrip_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var plain = Encoding.UTF8.GetBytes("""{"access_token":"x"}""");
        var roundTrip = GoogleOAuthUtil.UnprotectTokenBytes(GoogleOAuthUtil.ProtectTokenBytes(plain));
        Assert.Equal(plain, roundTrip);
    }

    [Fact]
    public void WriteAtomicProtected_ThenCorruptFile_UnprotectFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"hwn-{Guid.NewGuid():N}.bin");
        try
        {
            GoogleOAuthUtil.WriteAtomicProtected(path, Encoding.UTF8.GetBytes("{}"));
            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
            Assert.ThrowsAny<Exception>(() => GoogleOAuthUtil.UnprotectTokenBytes(File.ReadAllBytes(path)));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void LooksLikeLoopbackBlocked_MatchesGoogleBrowserCopy()
    {
        Assert.True(GoogleOAuthUtil.LooksLikeLoopbackBlocked(
            "The loopback flow has been blocked in order to keep users secure."));
        Assert.True(GoogleOAuthUtil.LooksLikeLoopbackBlocked(
            "invalid_request: loopback IP redirects are blocked"));
        Assert.False(GoogleOAuthUtil.LooksLikeLoopbackBlocked("invalid_request only"));
    }

    [Fact]
    public void FormatDeviceCodeFailure_GuidesTvsClientOnUnauthorized()
    {
        var text = GoogleOAuthUtil.FormatDeviceCodeFailure("Client is not authorized", "unauthorized_client");
        Assert.Contains("TVs and Limited Input devices", text, StringComparison.Ordinal);
        Assert.Contains("Device code request failed", text, StringComparison.Ordinal);
        Assert.Equal(
            "Device code request failed: rate limited",
            GoogleOAuthUtil.FormatDeviceCodeFailure("rate limited", "slow_down"));
    }
}
