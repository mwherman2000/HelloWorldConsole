using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Svrn7.Trust.Google;

public sealed class GoogleDeviceSignOn
{
    private const string DeviceCodeEndpoint = "https://oauth2.googleapis.com/device/code";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _tokenPath;
    private readonly string _legacyTokenPath;
    private readonly ILogger _logger;

    public GoogleDeviceSignOn(string clientId, string clientSecret, string tokenPath, ILogger logger)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _tokenPath = tokenPath;
        _legacyTokenPath = Path.ChangeExtension(tokenPath, ".json");
        _logger = logger;
    }

    public static GoogleDeviceSignOn Create(string appDirectory, ILogger logger)
    {
        var (clientId, clientSecret) = GoogleOAuthUtil.LoadClientSecrets(appDirectory);
        var tokenPath = Path.Combine(GoogleOAuthUtil.TokenDirectory(), "google-device-token.bin");
        return new GoogleDeviceSignOn(clientId, clientSecret, tokenPath, logger);
    }

    public void SignOut()
    {
        GoogleOAuthUtil.TryDelete(_tokenPath);
        GoogleOAuthUtil.TryDelete(_legacyTokenPath);
    }

    public async Task<GoogleUser> SignInAsync(bool forceInteractive, CancellationToken cancellationToken)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("GoogleSignOn.Device");
        activity?.SetTag(Instrumentation.Tags.Flow, "device");
        activity?.SetTag(Instrumentation.Tags.ForceInteractive, forceInteractive);
        Instrumentation.SignInAttempts.Add(1, new KeyValuePair<string, object?>(Instrumentation.Tags.Flow, "device"));

        var startTimestamp = Stopwatch.GetTimestamp();
        var outcome = "error";
        try
        {
            if (!forceInteractive)
            {
                var stored = LoadTokens();
                if (stored?.RefreshToken is { Length: > 0 })
                {
                    var refreshed = await RefreshAsync(stored.RefreshToken, cancellationToken);
                    if (refreshed is not null)
                    {
                        var user = await ResolveUserAsync(refreshed, cancellationToken);
                        SaveTokens(refreshed);
                        activity?.SetTag(Instrumentation.Tags.FromCache, true);
                        outcome = "success";
                        return user;
                    }

                    _logger.LogInformation("Stored refresh token was rejected; starting device sign-on.");
                }
            }
            else
            {
                _logger.LogInformation("Ignoring saved device tokens (--reauth).");
            }

            var device = await RequestDeviceCodeAsync(cancellationToken);
            PrintSignInInstructions(device);
            TryOpenBrowser(device.VerificationUrl);

            var tokens = await PollForTokensAsync(device, cancellationToken);
            var signedIn = await ResolveUserAsync(tokens, cancellationToken);
            SaveTokens(tokens);
            activity?.SetTag(Instrumentation.Tags.FromCache, false);
            outcome = "success";
            return signedIn;
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
                new KeyValuePair<string, object?>(Instrumentation.Tags.Flow, "device"),
                new KeyValuePair<string, object?>(Instrumentation.Tags.Outcome, outcome));
        }
    }

    private Task<GoogleUser> ResolveUserAsync(TokenSet tokens, CancellationToken cancellationToken) =>
        GoogleIdentity.ResolveUserAsync(
            _clientId,
            tokens.AccessToken,
            tokens.IdToken,
            GoogleUserInfo.GetAsync,
            _logger,
            cancellationToken);

    private async Task<DeviceAuthorization> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using var response = await GoogleOAuthHttp.PostFormAsync(
            DeviceCodeEndpoint,
            new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scope"] = string.Join(' ', GoogleOAuthUtil.Scopes),
            },
            cancellationToken);

        using var doc = await GoogleOAuthHttp.ReadJsonDocumentAsync(response, cancellationToken);
        var root = doc.RootElement;

        if (!response.IsSuccessStatusCode)
        {
            var message = GoogleOAuthUtil.ReadString(root, "error_description")
                ?? GoogleOAuthUtil.ReadString(root, "error")
                ?? GoogleOAuthUtil.ReadString(root, "error_code")
                ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException(
                GoogleOAuthUtil.FormatDeviceCodeFailure(message, GoogleOAuthUtil.ReadString(root, "error")));
        }

        var verificationUrl = GoogleOAuthUtil.ReadString(root, "verification_url")
            ?? GoogleOAuthUtil.ReadString(root, "verification_uri")
            ?? "https://www.google.com/device";
        if (!GoogleOAuthUtil.IsAllowedVerificationUrl(verificationUrl))
        {
            throw new InvalidOperationException("Google returned an unexpected verification URL.");
        }

        return new DeviceAuthorization(
            DeviceCode: Required(root, "device_code"),
            UserCode: Required(root, "user_code"),
            VerificationUrl: verificationUrl,
            ExpiresIn: GoogleOAuthUtil.ReadInt(root, "expires_in") ?? 1800,
            Interval: Math.Max(1, GoogleOAuthUtil.ReadInt(root, "interval") ?? 5));
    }

    private async Task<TokenSet> PollForTokensAsync(DeviceAuthorization device, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(device.Interval);
        var deadline = DateTime.UtcNow.AddSeconds(device.ExpiresIn);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(interval, cancellationToken);

            using var response = await GoogleOAuthHttp.PostFormAsync(
                TokenEndpoint,
                new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret,
                    ["device_code"] = device.DeviceCode,
                    ["grant_type"] = DeviceGrantType,
                },
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var step = GoogleOAuthUtil.InterpretDeviceTokenPoll((int)response.StatusCode, body);
            switch (step.Disposition)
            {
                case DevicePollDisposition.Succeeded:
                    using (var doc = JsonDocument.Parse(body))
                    {
                        return ParseTokens(doc.RootElement);
                    }
                case DevicePollDisposition.Pending:
                    continue;
                case DevicePollDisposition.SlowDown:
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                default:
                    throw new InvalidOperationException(step.FatalMessage ?? "Token poll failed.");
            }
        }

        throw new TimeoutException("Timed out waiting for Google sign-on.");
    }

    private async Task<TokenSet?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("GoogleSignOn.TokenRefresh");
        activity?.SetTag(Instrumentation.Tags.Flow, "device");
        var result = "error";
        try
        {
            using var response = await GoogleOAuthHttp.PostFormAsync(
                TokenEndpoint,
                new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token",
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                result = "rejected";
                return null;
            }

            using var doc = await GoogleOAuthHttp.ReadJsonDocumentAsync(response, cancellationToken);
            var tokens = ParseTokens(doc.RootElement);
            result = "ok";
            return tokens with { RefreshToken = tokens.RefreshToken ?? refreshToken };
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            activity?.SetTag(Instrumentation.Tags.RefreshResult, result);
            Instrumentation.TokenRefreshes.Add(
                1,
                new KeyValuePair<string, object?>(Instrumentation.Tags.Flow, "device"),
                new KeyValuePair<string, object?>(Instrumentation.Tags.RefreshResult, result));
        }
    }

    private TokenSet? LoadTokens()
    {
        try
        {
            if (File.Exists(_tokenPath))
            {
                var plain = GoogleOAuthUtil.UnprotectTokenBytes(File.ReadAllBytes(_tokenPath));
                return ParseTokenJson(Encoding.UTF8.GetString(plain));
            }

            if (File.Exists(_legacyTokenPath))
            {
                var legacy = ParseTokenJson(File.ReadAllText(_legacyTokenPath));
                if (legacy is not null)
                {
                    SaveTokens(legacy);
                    GoogleOAuthUtil.TryDelete(_legacyTokenPath);
                }

                return legacy;
            }
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or IOException
            or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable device token cache.");
            return null;
        }

        return null;
    }

    private void SaveTokens(TokenSet tokens)
    {
        var payload = JsonSerializer.Serialize(new
        {
            access_token = tokens.AccessToken,
            refresh_token = tokens.RefreshToken,
            id_token = tokens.IdToken,
            expires_in = tokens.ExpiresIn,
        });
        GoogleOAuthUtil.WriteAtomicProtected(_tokenPath, Encoding.UTF8.GetBytes(payload));
    }

    private static TokenSet? ParseTokenJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseTokens(doc.RootElement);
    }

    private static TokenSet ParseTokens(JsonElement root) =>
        new(
            AccessToken: Required(root, "access_token"),
            RefreshToken: GoogleOAuthUtil.ReadString(root, "refresh_token"),
            IdToken: GoogleOAuthUtil.ReadString(root, "id_token"),
            ExpiresIn: GoogleOAuthUtil.ReadInt(root, "expires_in"));

    private static void PrintSignInInstructions(DeviceAuthorization device)
    {
        Console.WriteLine();
        Console.WriteLine("Google sign-on (device flow)");
        Console.WriteLine($"  1. Open {device.VerificationUrl}");
        Console.WriteLine($"  2. Enter this code: {device.UserCode}");
        Console.WriteLine("Waiting for you to finish sign-on in the browser...");
        Console.WriteLine("Press Ctrl+C to cancel.");
    }

    private static void TryOpenBrowser(string url)
    {
        if (!GoogleOAuthUtil.IsAllowedVerificationUrl(url))
        {
            Console.WriteLine("Refusing to open a verification URL that is not an https Google host.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not open a browser automatically: {ex.Message}");
        }
    }

    private static string Required(JsonElement root, string name) =>
        GoogleOAuthUtil.ReadString(root, name) ?? throw new InvalidOperationException($"Google response was missing '{name}'.");

    private sealed record DeviceAuthorization(
        string DeviceCode,
        string UserCode,
        string VerificationUrl,
        int ExpiresIn,
        int Interval);

    private sealed record TokenSet(
        string AccessToken,
        string? RefreshToken,
        string? IdToken,
        int? ExpiresIn);
}
