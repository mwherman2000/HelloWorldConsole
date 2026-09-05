using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

internal static class GoogleOAuthUtil
{
    internal static readonly string[] Scopes = ["openid", "email", "profile"];
    internal static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("HelloWorldConsole.GoogleDeviceSignOn.v1");

    internal static string TokenDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HelloWorldConsole");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static string ResolveSecretsDirectory()
    {
        var cwd = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(cwd, "client_secrets.json")))
        {
            return cwd;
        }

        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "client_secrets.json")))
        {
            return baseDir;
        }

        return cwd;
    }

    internal static bool TryGetEnvCredentials(
        string? envId,
        string? envSecret,
        out string? clientId,
        out string? clientSecret,
        out string? error)
    {
        clientId = null;
        clientSecret = null;
        error = null;
        var idSet = !string.IsNullOrWhiteSpace(envId);
        var secretSet = !string.IsNullOrWhiteSpace(envSecret);
        if (!idSet && !secretSet)
        {
            return false;
        }

        if (!idSet || !secretSet)
        {
            error = "Set both GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET, or neither (use client_secrets.json).";
            return false;
        }

        clientId = envId;
        clientSecret = envSecret;
        return true;
    }

    internal static (string ClientId, string ClientSecret) LoadClientSecrets(string appDirectory)
    {
        if (TryGetEnvCredentials(
                Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID"),
                Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET"),
                out var envId,
                out var envSecret,
                out var envError))
        {
            return (envId!, envSecret!);
        }

        if (envError is not null)
        {
            throw new InvalidOperationException(envError);
        }

        var secretsPath = Path.Combine(appDirectory, "client_secrets.json");
        if (!File.Exists(secretsPath))
        {
            throw new InvalidOperationException(
                "Google OAuth credentials were not found. Create an OAuth client in Google Cloud Console " +
                "(Desktop app for default loopback sign-on, or TVs and Limited Input devices for --device), " +
                "then copy client_secrets.json.example to client_secrets.json or set GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET.");
        }

        using var doc = ParseClientSecretsJson(File.ReadAllText(secretsPath), secretsPath);
        return ReadClientSecretsDocument(doc);
    }

    internal static JsonDocument ParseClientSecretsJson(string json, string? sourcePath = null)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            var where = string.IsNullOrWhiteSpace(sourcePath) ? "OAuth client secrets" : sourcePath;
            throw new InvalidOperationException($"{where} is not valid JSON.", ex);
        }
    }

    internal static (string ClientId, string ClientSecret) ReadClientSecretsDocument(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.TryGetProperty("installed", out var installed))
        {
            root = installed;
        }
        else if (root.TryGetProperty("web", out var web))
        {
            root = web;
        }

        var clientId = ReadString(root, "client_id");
        var clientSecret = ReadString(root, "client_secret");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Google OAuth client_id or client_secret is missing.");
        }

        return (clientId, clientSecret);
    }

    internal static bool IsLoopbackClientMismatch(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Google.Apis.Auth.OAuth2.Responses.TokenResponseException tokenEx)
            {
                var code = tokenEx.Error?.Error;
                return code is "redirect_uri_mismatch" or "unauthorized_client" or "invalid_client";
            }
        }

        return false;
    }

    internal static bool LooksLikeLoopbackBlocked(string text) =>
        text.Contains("loopback flow has been blocked", StringComparison.OrdinalIgnoreCase)
        || (text.Contains("loopback", StringComparison.OrdinalIgnoreCase)
            && text.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            && text.Contains("invalid_request", StringComparison.OrdinalIgnoreCase));

    internal static string FormatDeviceCodeFailure(string message, string? errorCode)
    {
        var prefix = $"Device code request failed: {message}";
        if (errorCode is "unauthorized_client" or "invalid_client"
            || message.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
        {
            return prefix + " Create an OAuth client of type \"TVs and Limited Input devices\" "
                + "(not Drive API Quickstart / Desktop). Google has blocked the loopback IP flow for many "
                + "installed clients, including Drive Quickstart. Put that client in client_secrets.json "
                + "and run without --loopback.";
        }

        return prefix;
    }

    internal static string LoopbackBlockedGuidance =>
        "Google blocked the loopback IP OAuth flow for this client (common with Drive API Quickstart). "
        + "Create an OAuth client of type \"TVs and Limited Input devices\", update client_secrets.json, "
        + "and run: dotnet run   (device flow is the default). Use --loopback only with a Desktop client "
        + "that still allows http://127.0.0.1 redirects.";

    internal static DevicePollStep InterpretDeviceTokenPoll(int statusCode, string? body)
    {
        if (!TryParseJson(body, out var document) || document is null)
        {
            var reason = string.IsNullOrWhiteSpace(body) ? "empty" : "non-JSON";
            throw new InvalidOperationException($"Google returned a {reason} body (HTTP {statusCode}).");
        }

        using (document)
        {
            var root = document.RootElement;
            if (statusCode is >= 200 and < 300)
            {
                return new DevicePollStep(DevicePollDisposition.Succeeded, FatalMessage: null);
            }

            var error = ReadString(root, "error");
            return MapDevicePollError(error) switch
            {
                DevicePollDisposition.Pending => new DevicePollStep(DevicePollDisposition.Pending, null),
                DevicePollDisposition.SlowDown => new DevicePollStep(DevicePollDisposition.SlowDown, null),
                DevicePollDisposition.Denied => new DevicePollStep(
                    DevicePollDisposition.Denied,
                    "Google sign-on was denied."),
                DevicePollDisposition.Expired => new DevicePollStep(
                    DevicePollDisposition.Expired,
                    "The device code expired. Run the app again to start a new sign-on."),
                _ => new DevicePollStep(
                    DevicePollDisposition.Unknown,
                    $"Token poll failed: {ReadString(root, "error_description") ?? error ?? $"HTTP {statusCode}"}"),
            };
        }
    }

    internal static bool IsAllowedVerificationUrl(string url)
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

    internal static DevicePollDisposition MapDevicePollError(string? error)
    {
        return error switch
        {
            "authorization_pending" => DevicePollDisposition.Pending,
            "slow_down" => DevicePollDisposition.SlowDown,
            "access_denied" => DevicePollDisposition.Denied,
            "expired_token" => DevicePollDisposition.Expired,
            _ => DevicePollDisposition.Unknown,
        };
    }

    internal static bool TryParseJson(string? text, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static byte[] ProtectTokenBytes(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Encrypted token storage uses Windows DPAPI. Run this app on Windows.");
        }

        return ProtectedData.Protect(plain, TokenEntropy, DataProtectionScope.CurrentUser);
    }

    internal static byte[] UnprotectTokenBytes(byte[] protectedBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Encrypted token storage uses Windows DPAPI. Run this app on Windows.");
        }

        return ProtectedData.Unprotect(protectedBytes, TokenEntropy, DataProtectionScope.CurrentUser);
    }

    internal static void WriteAtomicProtected(string path, byte[] plain)
    {
        var protectedBytes = ProtectTokenBytes(plain);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Token path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(tempPath, protectedBytes);
            File.Move(tempPath, path, overwrite: true);
            RestrictToCurrentUser(path);
        }
        catch
        {
            TryDelete(path: tempPath);
            throw;
        }
    }

    internal static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null)
            {
                return;
            }

            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best-effort; DPAPI still binds the blob to this user.
        }
    }

    internal static void TryDelete(string path)
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

    internal static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}

internal enum DevicePollDisposition
{
    Pending,
    SlowDown,
    Denied,
    Expired,
    Succeeded,
    Unknown,
}

internal readonly record struct DevicePollStep(DevicePollDisposition Disposition, string? FatalMessage);

internal sealed record GoogleUser(string Subject, string? Email, string? Name, bool? EmailVerified);
