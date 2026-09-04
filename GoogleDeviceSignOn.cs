using System.Diagnostics;
using System.Text.Json;

internal sealed class GoogleDeviceSignOn
{
    private const string DeviceCodeEndpoint = "https://oauth2.googleapis.com/device/code";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
    private const string Scopes = "openid email profile";
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _tokenPath;

    public GoogleDeviceSignOn(string clientId, string clientSecret, string tokenPath)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _tokenPath = tokenPath;
    }

    public static GoogleDeviceSignOn FromEnvironmentOrSecretsFile(string appDirectory)
    {
        var clientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");

        var secretsPath = Path.Combine(appDirectory, "client_secrets.json");
        if ((string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            && File.Exists(secretsPath))
        {
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

            clientId ??= ReadString(root, "client_id");
            clientSecret ??= ReadString(root, "client_secret");
        }

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Google OAuth credentials were not found. Create an OAuth client of type " +
                "\"TVs and Limited Input devices\" in Google Cloud Console, then either copy " +
                "client_secrets.json.example to client_secrets.json and fill it in, or set " +
                "GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET.");
        }

        var tokenDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HelloWorldConsole");
        Directory.CreateDirectory(tokenDir);
        var tokenPath = Path.Combine(tokenDir, "google-device-token.json");
        return new GoogleDeviceSignOn(clientId, clientSecret, tokenPath);
    }

    public void SignOut()
    {
        if (File.Exists(_tokenPath))
        {
            File.Delete(_tokenPath);
        }
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
                    SaveTokens(refreshed);
                    return await GetUserAsync(refreshed.AccessToken, cancellationToken);
                }
            }
        }

        var device = await RequestDeviceCodeAsync(cancellationToken);
        PrintSignInInstructions(device);
        TryOpenBrowser(device.VerificationUrl);

        var tokens = await PollForTokensAsync(device, cancellationToken);
        SaveTokens(tokens);
        return await GetUserAsync(tokens.AccessToken, cancellationToken);
    }

    private async Task<DeviceAuthorization> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.PostAsync(
            DeviceCodeEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scope"] = Scopes,
            }),
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!response.IsSuccessStatusCode)
        {
            var message = ReadString(root, "error_description")
                ?? ReadString(root, "error")
                ?? ReadString(root, "error_code")
                ?? json;
            throw new InvalidOperationException($"Device code request failed: {message}");
        }

        return new DeviceAuthorization(
            DeviceCode: Required(root, "device_code"),
            UserCode: Required(root, "user_code"),
            VerificationUrl: ReadString(root, "verification_url")
                ?? ReadString(root, "verification_uri")
                ?? "https://www.google.com/device",
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

            using var response = await Http.PostAsync(
                TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret,
                    ["device_code"] = device.DeviceCode,
                    ["grant_type"] = DeviceGrantType,
                }),
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
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
                    var description = ReadString(root, "error_description") ?? error ?? json;
                    throw new InvalidOperationException($"Token poll failed: {description}");
            }
        }

        throw new TimeoutException("Timed out waiting for Google sign-on.");
    }

    private async Task<TokenSet?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var response = await Http.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            }),
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var tokens = ParseTokens(doc.RootElement);
        return tokens with { RefreshToken = tokens.RefreshToken ?? refreshToken };
    }

    private async Task<GoogleUser> GetUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Could not load Google profile: {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new GoogleUser(
            Subject: ReadString(root, "sub") ?? "",
            Email: ReadString(root, "email"),
            Name: ReadString(root, "name"),
            EmailVerified: root.TryGetProperty("email_verified", out var verified) && verified.ValueKind == JsonValueKind.True);
    }

    private TokenSet? LoadTokens()
    {
        if (!File.Exists(_tokenPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_tokenPath));
            return ParseTokens(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
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
        File.WriteAllText(_tokenPath, payload);
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
    }

    private static void TryOpenBrowser(string url)
    {
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

internal sealed record GoogleUser(string Subject, string? Email, string? Name, bool EmailVerified);
