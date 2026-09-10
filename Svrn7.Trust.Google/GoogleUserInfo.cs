using System.Net.Http.Headers;
using System.Text.Json;

namespace Svrn7.Trust.Google;

internal static class GoogleUserInfo
{
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";

    internal static async Task<GoogleUser> GetAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var response = await GoogleOAuthHttp.SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                return request;
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Could not load Google profile. Try --reauth.");
        }

        using var doc = await GoogleOAuthHttp.ReadJsonDocumentAsync(response, cancellationToken);
        var root = doc.RootElement;
        bool? emailVerified = null;
        if (root.TryGetProperty("email_verified", out var verified))
        {
            emailVerified = verified.ValueKind == JsonValueKind.True;
        }

        return new GoogleUser(
            Subject: GoogleOAuthUtil.ReadString(root, "sub") ?? "",
            Email: GoogleOAuthUtil.ReadString(root, "email"),
            Name: GoogleOAuthUtil.ReadString(root, "name"),
            EmailVerified: emailVerified);
    }
}
