using Microsoft.Extensions.Logging;

Console.WriteLine("Hello, World!");

var forceInteractive = args.Contains("--reauth", StringComparer.OrdinalIgnoreCase);
var signOut = args.Contains("--sign-out", StringComparer.OrdinalIgnoreCase);
var deviceOnly = args.Contains("--device", StringComparer.OrdinalIgnoreCase);
var loopbackOnly = args.Contains("--loopback", StringComparer.OrdinalIgnoreCase);
var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);

if (deviceOnly && loopbackOnly)
{
    Console.Error.WriteLine("Use only one of --device or --loopback.");
    Environment.ExitCode = 1;
    return;
}

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
});
var logger = loggerFactory.CreateLogger("HelloWorldConsole");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var secretsDirectory = GoogleOAuthUtil.ResolveSecretsDirectory();
    var loopback = GoogleLoopbackSignOn.Create(secretsDirectory, logger);
    var device = GoogleDeviceSignOn.Create(secretsDirectory, logger);

    if (signOut)
    {
        await loopback.SignOutAsync();
        device.SignOut();
        Console.WriteLine("Signed out. Local Google tokens were removed.");
        return;
    }

    GoogleUser user;
    if (deviceOnly)
    {
        user = await device.SignInAsync(forceInteractive, cts.Token);
    }
    else
    {
        try
        {
            user = await loopback.SignInAsync(forceInteractive, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!loopbackOnly && IsLoopbackConfigurationError(ex))
        {
            logger.LogWarning(
                ex,
                "Loopback sign-on failed (Desktop OAuth client required). Falling back to device flow. Pass --device to skip loopback, or --loopback to disable fallback.");
            user = await device.SignInAsync(forceInteractive, cts.Token);
        }
    }

    var displayName = string.IsNullOrWhiteSpace(user.Name) ? "Google user" : user.Name;
    var email = string.IsNullOrWhiteSpace(user.Email) ? user.Subject : user.Email;
    Console.WriteLine($"Signed in as {displayName} <{email}>.");
    if (user.EmailVerified == false)
    {
        Console.WriteLine("Warning: Google has not verified this email.");
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Sign-on cancelled.");
    Environment.ExitCode = 130;
}
catch (Exception ex)
{
    logger.LogError(ex, "Sign-on failed.");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("If a saved session is stale, try: dotnet run -- --reauth");
    Environment.ExitCode = 1;
}

static bool IsLoopbackConfigurationError(Exception ex)
{
    var text = ex.ToString();
    return text.Contains("redirect_uri_mismatch", StringComparison.OrdinalIgnoreCase)
        || text.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
        || text.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase)
        || text.Contains("invalid_request", StringComparison.OrdinalIgnoreCase);
}
