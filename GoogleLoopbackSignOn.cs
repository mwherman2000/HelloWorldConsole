using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;

internal sealed class GoogleLoopbackSignOn
{
    private readonly ClientSecrets _secrets;
    private readonly DpapiJsonDataStore _store;
    private readonly ILogger _logger;

    public GoogleLoopbackSignOn(string clientId, string clientSecret, string tokenPath, ILogger logger)
    {
        _secrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
        _store = new DpapiJsonDataStore(tokenPath, logger);
        _logger = logger;
    }

    public static GoogleLoopbackSignOn Create(string appDirectory, ILogger logger)
    {
        var (clientId, clientSecret) = GoogleOAuthUtil.LoadClientSecrets(appDirectory);
        var tokenPath = Path.Combine(GoogleOAuthUtil.TokenDirectory(), "google-loopback-token.bin");
        return new GoogleLoopbackSignOn(clientId, clientSecret, tokenPath, logger);
    }

    public Task SignOutAsync() => _store.ClearAsync();

    public async Task<GoogleUser> SignInAsync(bool forceInteractive, CancellationToken cancellationToken)
    {
        if (forceInteractive)
        {
            _logger.LogInformation("Ignoring saved loopback tokens (--reauth).");
            await _store.ClearAsync();
        }

        _logger.LogInformation("Starting Google installed-app (loopback) sign-on.");
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            _secrets,
            GoogleOAuthUtil.Scopes,
            "user",
            cancellationToken,
            _store);

        var token = credential.Token
            ?? throw new InvalidOperationException("Google did not return an access token.");

        if (credential.Token.IsStale)
        {
            _logger.LogDebug("Access token is stale; refreshing.");
            if (!await credential.RefreshTokenAsync(cancellationToken))
            {
                throw new InvalidOperationException("Could not refresh the Google access token. Try --reauth.");
            }

            token = credential.Token;
        }

        return await GoogleIdentity.ResolveUserAsync(
            _secrets.ClientId,
            token.AccessToken,
            token.IdToken,
            GoogleUserInfo.GetAsync,
            _logger,
            cancellationToken);
    }
}
