using System.Diagnostics;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;

namespace Svrn7.Trust.Google;

public sealed class GoogleLoopbackSignOn
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
        using var activity = Instrumentation.ActivitySource.StartActivity("GoogleSignOn.Loopback");
        activity?.SetTag(Instrumentation.Tags.Flow, "loopback");
        activity?.SetTag(Instrumentation.Tags.ForceInteractive, forceInteractive);
        Instrumentation.SignInAttempts.Add(1, new KeyValuePair<string, object?>(Instrumentation.Tags.Flow, "loopback"));

        var startTimestamp = Stopwatch.GetTimestamp();
        var outcome = "error";
        try
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
                using var refreshActivity = Instrumentation.ActivitySource.StartActivity("GoogleSignOn.TokenRefresh");
                refreshActivity?.SetTag(Instrumentation.Tags.Flow, "loopback");

                var refreshed = await credential.RefreshTokenAsync(cancellationToken);
                var refreshResult = refreshed ? "ok" : "rejected";
                refreshActivity?.SetTag(Instrumentation.Tags.RefreshResult, refreshResult);
                Instrumentation.TokenRefreshes.Add(
                    1,
                    new KeyValuePair<string, object?>(Instrumentation.Tags.Flow, "loopback"),
                    new KeyValuePair<string, object?>(Instrumentation.Tags.RefreshResult, refreshResult));

                if (!refreshed)
                {
                    throw new InvalidOperationException("Could not refresh the Google access token. Try --reauth.");
                }

                token = credential.Token;
            }

            var user = await GoogleIdentity.ResolveUserAsync(
                _secrets.ClientId,
                token.AccessToken,
                token.IdToken,
                GoogleUserInfo.GetAsync,
                _logger,
                cancellationToken);
            outcome = "success";
            return user;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            outcome = "error";
            activity?.SetTag(Instrumentation.Tags.ErrorType, ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            activity?.SetTag(Instrumentation.Tags.Outcome, outcome);
            Instrumentation.SignInDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
                new KeyValuePair<string, object?>(Instrumentation.Tags.Flow, "loopback"),
                new KeyValuePair<string, object?>(Instrumentation.Tags.Outcome, outcome));
        }
    }
}
