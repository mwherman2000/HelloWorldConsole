using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class GoogleDeviceSignOn
{
    private const string DeviceCodeEndpoint = "https://oauth2.googleapis.com/device/code";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
    private const string Scopes = "openid email profile";
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("HelloWorldConsole.GoogleDeviceSignOn.v1");
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _tokenPath;
    private readonly string _legacyTokenPath;

    public GoogleDeviceSignOn(string clientId, string clientSecret, string tokenPath)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _tokenPath = tokenPath;
        _legacyTokenPath = Path.ChangeExtension(tokenPath, ".json");
    }

    public static GoogleDeviceSignOn FromEnvironmentOrSecretsFile(string appDirectory)
    {
        var envId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
        var envSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");
        var envIdSet = !string.IsNullOrWhiteSpace(envId);
        var envSecretSet = !string.IsNullOrWhiteSpace(envSecret);

        string? clientId = null;
        string? clientSecret = null;

        if (envIdSet || envSecretSet)
        {
            if (!envIdSet || !envSecretSet)
            {
                throw new InvalidOperationException(
                    "Set both GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET, or neither (use client_secrets.json).");
            }

            clientId = envId;
            clientSecret = envSecret;
        }
        else
        {
            var secretsPath = Path.Combine(appDirectory, "client_secrets.json");
            if (!File.Exists(secretsPath))
            {
                throw new InvalidOperationException(
                    "Google OAuth credentials were not found. Create an OAuth client of type " +
                    "\"TVs and Limited Input devices\" in Google Cloud Console, then either copy " +
                    "client_secrets.json.example to client_secrets.json and fill it in, or set " +
                    "GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET.");
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(secretsPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("installed", out var installed))
            {
                root = installed;
            }
            else if (root.TryGetProperty("web", out var web))
            {
                root = web;
            }

            clientId = ReadString(root, "client_id");
            clientSecret = ReadString(root, "client_secret");
        }

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Google OAuth client_id or client_secret is missing.");
        }

        var tokenDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HelloWorldConsole");
        Directory.CreateDirectory(tokenDir);
        var tokenPath = Path.Combine(tokenDir, "google-device-token.bin");
        return new GoogleDeviceSignOn(clientId, clientSecret, tokenPath);
    }

    public void SignOut()
    {
        TryDelete(_tokenPath);
        TryDelete(_legacyTokenPath);
    }

    public async Task<GoogleUser> SignInAsync(bool forceInteractive, CancellationToken cancellationToken)
    {
        if (!forceInteractive)
        {
            var stored = LoadTokens();
            if (stored?.RefreshToken is { Length: > 0 })
            {
                var refreshed = await RefreshAsync(stored.RefreshToken, cancellationToken);
                if (refreshed is not null)
                {
                    var user = await GetUserAsync(refreshed.AccessToken, cancellationToken);
                    SaveTokens(refreshed);
                    return user;
                }
            }
        }

        var device = await RequestDeviceCodeAsync(cancellationToken);
        PrintSignInInstructions(device);
        TryOpenBrowser(device.VerificationUrl);

        var tokens = await PollForTokensAsync(device, cancellationToken);
        var signedIn = await GetUserAsync(tokens.AccessToken, cancellationToken);
        SaveTokens(tokens);
        return signedIn;
    }

    private async Task<DeviceAuthorization> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using var response = await PostFormAsync(
            DeviceCodeEndpoint,
            new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scope"] = Scopes,
            },
            cancellationToken);

        using var doc = await ReadJsonDocumentAsync(response, cancellationToken);
        var root = doc.RootElement;

        if (!response.IsSuccessStatusCode)
        {
            var message = ReadString(root, "error_description")
                ?? ReadString(root, "error")
                ?? ReadString(root, "error_code")
                ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Device code request failed: {message}");
        }

        var verificationUrl = ReadString(root, "verification_url")
            ?? ReadString(root, "verification_uri")
            ?? "https://www.google.com/device";
        if (!IsAllowedVerificationUrl(verificationUrl))
        {
            throw new InvalidOperationException("Google returned an unexpected verification URL.");
        }

        return new DeviceAuthorization(
            DeviceCode: Required(root, "device_code"),
            UserCode: Required(root, "user_code"),
            VerificationUrl: verificationUrl,
            ExpiresIn: ReadInt(root, "expires_in") ?? 1800,
            Interval: Math.Max(1, ReadInt(root, "interval") ?? 5));
    }

    private async Task<TokenSet> PollForTokensAsync(DeviceAuthorization device, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(device.Interval);
        var deadline = DateTime.UtcNow.AddSeconds(device.ExpiresIn);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(interval, cancellationToken);

            using var response = await PostFormAsync(
                TokenEndpoint,
                new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret,
                    ["device_code"] = device.DeviceCode,
                    ["grant_type"] = DeviceGrantType,
                },
                cancellationToken);

            using var doc = await ReadJsonDocumentAsync(response, cancellationToken);
            var root = doc.RootElement;

            if (response.IsSuccessStatusCode)
            {
                return ParseTokens(root);
            }

            var error = ReadString(root, "error");
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "access_denied":
                    throw new InvalidOperationException("Google sign-on was denied.");
                case "expired_token":
                    throw new InvalidOperationException("The device code expired. Run the app again to start a new sign-on.");
                default:
                    var description = ReadString(root, "error_description") ?? error ?? $"HTTP {(int)response.StatusCode}";
                    throw new InvalidOperationException($"Token poll failed: {description}");
            }
        }

        throw new TimeoutException("Timed out waiting for Google sign-on.");
    }

    private async Task<TokenSet?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostFormAsync(
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
                return null;
            }

            using var doc = await ReadJsonDocumentAsync(response, cancellationToken);
            var tokens = ParseTokens(doc.RootElement);
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
    }

    private async Task<GoogleUser> GetUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
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

        using var doc = await ReadJsonDocumentAsync(response, cancellationToken);
        var root = doc.RootElement;
        bool? emailVerified = null;
        if (root.TryGetProperty("email_verified", out var verified))
        {
            emailVerified = verified.ValueKind == JsonValueKind.True;
        }

        return new GoogleUser(
            Subject: ReadString(root, "sub") ?? "",
            Email: ReadString(root, "email"),
            Name: ReadString(root, "name"),
            EmailVerified: emailVerified);
    }

    private TokenSet? LoadTokens()
    {
        try
        {
            if (File.Exists(_tokenPath))
            {
                var protectedBytes = File.ReadAllBytes(_tokenPath);
                var plain = UnprotectTokenBytes(protectedBytes);
                return ParseTokenJson(Encoding.UTF8.GetString(plain));
            }

            if (File.Exists(_legacyTokenPath))
            {
                var legacy = ParseTokenJson(File.ReadAllText(_legacyTokenPath));
                if (legacy is not null)
                {
                    SaveTokens(legacy);
                    TryDelete(_legacyTokenPath);
                }

                return legacy;
            }
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or IOException
            or UnauthorizedAccessException or PlatformNotSupportedException)
        {
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
        var plain = Encoding.UTF8.GetBytes(payload);
        var protectedBytes = ProtectTokenBytes(plain);

        var directory = Path.GetDirectoryName(_tokenPath)
            ?? throw new InvalidOperationException("Token path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(tempPath, protectedBytes);
            File.Move(tempPath, _tokenPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static TokenSet? ParseTokenJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseTokens(doc.RootElement);
    }

    private static TokenSet ParseTokens(JsonElement root) =>
        new(
            AccessToken: Required(root, "access_token"),
            RefreshToken: ReadString(root, "refresh_token"),
            IdToken: ReadString(root, "id_token"),
            ExpiresIn: ReadInt(root, "expires_in"));

    private static void PrintSignInInstructions(DeviceAuthorization device)
    {
        Console.WriteLine();
        Console.WriteLine("Google sign-on (direct device flow)");
        Console.WriteLine($"  1. Open {device.VerificationUrl}");
        Console.WriteLine($"  2. Enter this code: {device.UserCode}");
        Console.WriteLine("Waiting for you to finish sign-on in the browser...");
        Console.WriteLine("Press Ctrl+C to cancel.");
    }

    private static void TryOpenBrowser(string url)
    {
        if (!IsAllowedVerificationUrl(url))
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

    private static bool IsAllowedVerificationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return host is "google.com" || host.EndsWith(".google.com", StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        string url,
        Dictionary<string, string> form,
        CancellationToken cancellationToken) =>
        await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(form),
            },
            cancellationToken);

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
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
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("Google request failed after retries.", lastException);
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Google returned an empty body (HTTP {(int)response.StatusCode}).");
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Google returned a non-JSON body (HTTP {(int)response.StatusCode}).");
        }
    }

    private static byte[] ProtectTokenBytes(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Encrypted token storage uses Windows DPAPI. Run this app on Windows.");
        }

        return ProtectedData.Protect(plain, TokenEntropy, DataProtectionScope.CurrentUser);
    }

    private static byte[] UnprotectTokenBytes(byte[] protectedBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Encrypted token storage uses Windows DPAPI. Run this app on Windows.");
        }

        return ProtectedData.Unprotect(protectedBytes, TokenEntropy, DataProtectionScope.CurrentUser);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not delete '{path}': {ex.Message}");
        }
    }

    private static string Required(JsonElement root, string name) =>
        ReadString(root, name) ?? throw new InvalidOperationException($"Google response was missing '{name}'.");

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

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

internal sealed record GoogleUser(string Subject, string? Email, string? Name, bool? EmailVerified);
