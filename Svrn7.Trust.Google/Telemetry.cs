using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Svrn7.Trust.Google;

/// <summary>
/// Names of the <see cref="ActivitySource"/> and <see cref="Meter"/> that this
/// library emits on. Register them with your OpenTelemetry provider:
/// <code>
/// tracerProviderBuilder.AddSource(GoogleTelemetry.ActivitySourceName);
/// meterProviderBuilder.AddMeter(GoogleTelemetry.MeterName);
/// </code>
/// The library takes no dependency on the OpenTelemetry SDK; it only uses the
/// in-box <c>System.Diagnostics</c> primitives. When nothing is listening the
/// instrumentation is effectively free.
/// </summary>
public static class GoogleTelemetry
{
    /// <summary>Name of the <see cref="ActivitySource"/> for spans this library starts.</summary>
    public const string ActivitySourceName = "Svrn7.Trust.Google";

    /// <summary>Name of the <see cref="Meter"/> for metrics this library records.</summary>
    public const string MeterName = "Svrn7.Trust.Google";
}

/// <summary>
/// The shared <see cref="ActivitySource"/>, <see cref="Meter"/> and instruments.
/// Spans:
/// <list type="bullet">
///   <item><c>GoogleSignOn.Device</c> / <c>GoogleSignOn.Loopback</c> — one per <c>SignInAsync</c> call.</item>
///   <item><c>GoogleSignOn.TokenRefresh</c> — an access-token refresh.</item>
///   <item><c>GoogleSignOn.ResolveIdentity</c> — ID-token validation or userinfo lookup.</item>
/// </list>
/// Metrics: <c>svrn7.google.sign_in.attempts</c>, <c>svrn7.google.sign_in.duration</c>,
/// <c>svrn7.google.token.refreshes</c>, <c>svrn7.google.http.retries</c>.
/// </summary>
internal static class Instrumentation
{
    private static readonly string Version =
        typeof(Instrumentation).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    internal static readonly ActivitySource ActivitySource = new(GoogleTelemetry.ActivitySourceName, Version);

    internal static readonly Meter Meter = new(GoogleTelemetry.MeterName, Version);

    internal static readonly Counter<long> SignInAttempts = Meter.CreateCounter<long>(
        "svrn7.google.sign_in.attempts",
        unit: "{attempt}",
        description: "Google sign-on attempts started.");

    internal static readonly Histogram<double> SignInDuration = Meter.CreateHistogram<double>(
        "svrn7.google.sign_in.duration",
        unit: "s",
        description: "Wall-clock duration of a completed Google sign-on attempt, tagged with flow and outcome.");

    internal static readonly Counter<long> TokenRefreshes = Meter.CreateCounter<long>(
        "svrn7.google.token.refreshes",
        unit: "{refresh}",
        description: "Access-token refresh attempts, tagged with flow and result.");

    internal static readonly Counter<long> HttpRetries = Meter.CreateCounter<long>(
        "svrn7.google.http.retries",
        unit: "{retry}",
        description: "Google HTTP requests retried, tagged with reason.");

    /// <summary>Tag keys, kept identical across spans and metrics.</summary>
    internal static class Tags
    {
        internal const string Flow = "signon.flow";                          // "device" | "loopback"
        internal const string Outcome = "signon.outcome";                    // "success" | "cancelled" | "error"
        internal const string FromCache = "signon.from_cache";               // bool
        internal const string ForceInteractive = "signon.force_interactive"; // bool
        internal const string IdentitySource = "signon.identity_source";     // "id_token" | "userinfo"
        internal const string RefreshResult = "signon.refresh_result";       // "ok" | "rejected" | "error"
        internal const string RetryReason = "http.retry_reason";             // "status_429" | "status_5xx" | "exception" | "timeout"
        internal const string ErrorType = "error.type";
    }
}
