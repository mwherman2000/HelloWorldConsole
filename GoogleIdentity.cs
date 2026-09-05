using Google.Apis.Auth;
using Microsoft.Extensions.Logging;

internal static class GoogleIdentity
{
    internal static async Task<GoogleUser> ResolveUserAsync(
        string clientId,
        string accessToken,
        string? idToken,
        Func<string, CancellationToken, Task<GoogleUser>> userInfo,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(idToken))
        {
            try
            {
                var payload = await GoogleJsonWebSignature.ValidateAsync(
                    idToken,
                    new GoogleJsonWebSignature.ValidationSettings
                    {
                        Audience = [clientId],
                    }).WaitAsync(cancellationToken);

                logger.LogDebug("Validated Google ID token for subject {Subject}.", payload.Subject);
                return new GoogleUser(
                    payload.Subject,
                    payload.Email,
                    payload.Name,
                    payload.EmailVerified);
            }
            catch (Exception ex) when (ex is InvalidJwtException or ArgumentException or FormatException)
            {
                logger.LogError(ex, "ID token validation failed.");
                throw new InvalidOperationException(
                    "Google ID token could not be verified. Aborting sign-on. Try --reauth.",
                    ex);
            }
        }
        else
        {
            logger.LogDebug("No ID token present; loading profile from userinfo.");
        }

        return await userInfo(accessToken, cancellationToken);
    }
}
