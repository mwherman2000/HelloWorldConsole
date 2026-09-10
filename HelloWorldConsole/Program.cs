using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Svrn7.Trust.Google;

namespace HelloWorldConsole;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        Console.WriteLine("Hello, World!");

        var forceInteractive = args.Contains("--reauth", StringComparer.OrdinalIgnoreCase);
        var signOut = args.Contains("--sign-out", StringComparer.OrdinalIgnoreCase);
        var deviceOnly = args.Contains("--device", StringComparer.OrdinalIgnoreCase);
        var loopbackOnly = args.Contains("--loopback", StringComparer.OrdinalIgnoreCase);
        var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        var otelConsole = args.Contains("--otel-console", StringComparer.OrdinalIgnoreCase);
        var jaeger = args.Contains("--jaeger", StringComparer.OrdinalIgnoreCase);

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

        using var tracerProvider = BuildTracerProvider(otelConsole, jaeger);
        using var meterProvider = BuildMeterProvider(otelConsole);
        if (tracerProvider is not null || meterProvider is not null)
        {
            logger.LogInformation(
                "OpenTelemetry export is on (console: {Console}, jaeger/OTLP: {Jaeger}).",
                otelConsole,
                jaeger);
        }

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
            if (loopbackOnly)
            {
                logger.LogWarning(
                    "Loopback is blocked by Google for many installed clients (including Drive API Quickstart). "
                    + "If the browser shows Error 400 invalid_request / loopback blocked, close it and run without --loopback.");
                user = await loopback.SignInAsync(forceInteractive, cts.Token);
            }
            else
            {
                logger.LogInformation("Starting Google device sign-on (default; loopback is opt-in via --loopback).");
                user = await device.SignInAsync(forceInteractive, cts.Token);
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
        catch (Exception ex) when (IsExpected(ex))
        {
            // Deliberately-raised, user-actionable errors: one clean line, no stack trace.
            // (Run with --verbose to see the exception detail.)
            logger.LogDebug(ex, "Sign-on stopped with an expected error.");
            Console.Error.WriteLine(ex.Message);
            if (ResolveHint(ex) is { } hint)
            {
                Console.Error.WriteLine(hint);
            }

            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            // Unexpected: keep the full stack trace so the bug can be diagnosed.
            logger.LogError(ex, "Sign-on failed with an unexpected error.");
            Console.Error.WriteLine($"Unexpected error: {ex.Message}");
            Console.Error.WriteLine("This looks like a bug. Re-run with --verbose and report it if it persists.");
            Environment.ExitCode = 70; // EX_SOFTWARE
        }

        Console.WriteLine("Press Enter to exit...");
        Console.ReadLine();
    }

    /// <summary>
    /// Builds a tracer for the library's <see cref="GoogleTelemetry.ActivitySourceName"/> source,
    /// or null when no trace exporter was requested. <c>--otel-console</c> writes spans to the
    /// console; <c>--jaeger</c> exports them over OTLP (gRPC <c>localhost:4317</c> by default;
    /// override with <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> / <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>).
    /// </summary>
    private static TracerProvider? BuildTracerProvider(bool console, bool jaeger)
    {
        if (!console && !jaeger)
        {
            return null;
        }

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("HelloWorldConsole"))
            .AddSource(GoogleTelemetry.ActivitySourceName);

        if (console)
        {
            builder.AddConsoleExporter();
        }

        if (jaeger)
        {
            builder.AddOtlpExporter();
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds a meter for the library's <see cref="GoogleTelemetry.MeterName"/> meter that writes
    /// to the console, or null unless <c>--otel-console</c> was passed. (Jaeger is traces only.)
    /// </summary>
    private static MeterProvider? BuildMeterProvider(bool console)
    {
        if (!console)
        {
            return null;
        }

        return Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("HelloWorldConsole"))
            .AddMeter(GoogleTelemetry.MeterName)
            .AddConsoleExporter()
            .Build();
    }

    /// <summary>
    /// True for exceptions the app raises on purpose for conditions the user can fix
    /// (missing/invalid credentials, timeouts, denied or blocked sign-on, no network).
    /// These are reported as a single line without a stack trace.
    /// </summary>
    private static bool IsExpected(Exception ex) => ex is
        InvalidOperationException or
        TimeoutException or
        PlatformNotSupportedException or
        HttpRequestException or
        System.Net.Sockets.SocketException or
        Google.Apis.Auth.OAuth2.Responses.TokenResponseException;

    /// <summary>Returns a one-line next step for an expected error, or null if the message already says enough.</summary>
    private static string? ResolveHint(Exception ex)
    {
        if (GoogleOAuthUtil.LooksLikeLoopbackBlocked(ex.ToString()))
        {
            return GoogleOAuthUtil.LoopbackBlockedGuidance;
        }

        var message = ex.Message;

        if (message.Contains("credentials were not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase)
            || message.Contains("client_id or client_secret", StringComparison.OrdinalIgnoreCase)
            || message.Contains("GOOGLE_CLIENT_ID", StringComparison.OrdinalIgnoreCase))
        {
            return "Set up credentials: copy client_secrets.json.example to client_secrets.json, "
                + "or set GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET.";
        }

        if (ex is TimeoutException or HttpRequestException or System.Net.Sockets.SocketException)
        {
            return "Check your network connection and try again.";
        }

        if (message.Contains("reauth", StringComparison.OrdinalIgnoreCase))
        {
            return null; // the message already points at --reauth
        }

        return "If a saved session is stale, try: dotnet run -- --reauth";
    }
}
