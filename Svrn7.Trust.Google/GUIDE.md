# Svrn7.Trust.Google — developer guide

A small library that signs a user in with Google from a desktop / console
application and returns their verified identity. It wraps two OAuth flows:

- **Device flow** ("TVs and Limited Input devices") — the default. Prints a
  short code and a URL, opens a browser, polls until the user approves.
- **Loopback flow** ("Desktop app", `http://127.0.0.1`) — still available, but
  Google now **blocks it for many client types** (anything derived from the
  Drive API Quickstart / older Desktop clients). Use device flow unless you
  have a Desktop client that Google still permits.

Both flows request the scopes `openid email profile`, validate Google's ID
token against Google's certificates when one is returned, and otherwise fall
back to the [userinfo endpoint](https://www.googleapis.com/oauth2/v3/userinfo).
Refresh tokens are cached, encrypted at rest.

---

## Requirements & platform support

| | |
|---|---|
| Target framework | `net8.0` |
| Runtime in practice | **Windows only** |
| Reason | The token cache is encrypted with **Windows DPAPI** (`System.Security.Cryptography.ProtectedData`) and locked with a current-user ACL. On non-Windows, any call that writes or reads the cache throws `PlatformNotSupportedException`. |

The assembly *targets* the portable `net8.0` TFM and will restore/compile on
Linux or macOS, but sign-on will fail at runtime there until token-at-rest
protection is abstracted. If you need cross-platform support, construct the
sign-on types with your own token path and be aware the DPAPI calls in
`GoogleOAuthUtil` still need replacing.

## Adding the library

It is not published to NuGet. Reference the project:

```xml
<ItemGroup>
  <ProjectReference Include="..\Svrn7.Trust.Google\Svrn7.Trust.Google.csproj" />
</ItemGroup>
```

Transitive package dependencies you inherit:

| Package | Version | Used for |
|---|---|---|
| `Google.Apis.Auth` | 1.73.0 | ID token validation, loopback broker |
| `Microsoft.Extensions.Logging.Abstractions` | 8.0.2 | `ILogger` |
| `System.Diagnostics.DiagnosticSource` | 8.0.1 | `ActivitySource` / `Meter` instrumentation |
| `Newtonsoft.Json` | 13.0.4 | token cache serialization |
| `System.Security.Cryptography.ProtectedData` | 10.0.11 | DPAPI |
| `System.IO.FileSystem.AccessControl` | 5.0.0 | current-user ACL on the cache file |
| `System.Security.Principal.Windows` | 5.0.0 | `WindowsIdentity` |

## Public API

Namespace: `Svrn7.Trust.Google`

### `GoogleUser`

```csharp
public sealed record GoogleUser(string Subject, string? Email, string? Name, bool? EmailVerified);
```

`Subject` is the stable Google account id (`sub`). `EmailVerified` is `null`
when it could not be determined; treat only `true` as verified.

### `GoogleDeviceSignOn`

```csharp
// Convenience: reads credentials, uses the default token cache path.
public static GoogleDeviceSignOn Create(string appDirectory, ILogger logger);

// Full control: you supply the client id/secret and the token cache path.
public GoogleDeviceSignOn(string clientId, string clientSecret, string tokenPath, ILogger logger);

public Task<GoogleUser> SignInAsync(bool forceInteractive, CancellationToken cancellationToken);
public void SignOut();   // deletes the cached device token (and legacy .json)
```

`SignInAsync` returns immediately from the cache when a stored refresh token
still works. Pass `forceInteractive: true` to ignore the cache and prompt
again. With no cached token it prints instructions to `Console.Out`, tries to
open a browser, and polls until approval, `cancellationToken`, or the device
code expires.

### `GoogleLoopbackSignOn`

```csharp
public static GoogleLoopbackSignOn Create(string appDirectory, ILogger logger);
public GoogleLoopbackSignOn(string clientId, string clientSecret, string tokenPath, ILogger logger);

public Task<GoogleUser> SignInAsync(bool forceInteractive, CancellationToken cancellationToken);
public Task SignOutAsync();   // clears the cached loopback token
```

Delegates to `GoogleWebAuthorizationBroker` and stores tokens through the same
encrypted cache. Expect this to fail with a `redirect_uri_mismatch` /
`invalid_request` style error for blocked client types — see below.

### `GoogleOAuthUtil` (helpers)

```csharp
public static string ResolveSecretsDirectory();          // where Create() looks for client_secrets.json
public static bool   LooksLikeLoopbackBlocked(string text);
public static string LoopbackBlockedGuidance { get; }     // user-facing remediation text
```

### `GoogleTelemetry`

```csharp
public const string ActivitySourceName = "Svrn7.Trust.Google";
public const string MeterName = "Svrn7.Trust.Google";
```

Names to register with your OpenTelemetry provider — see
[Telemetry](#telemetry-opentelemetry).

---

Everything else on `GoogleOAuthUtil` is `internal`. The test project sees
internals via `InternalsVisibleTo("HelloWorldConsole.Tests")`; add your own
assembly there if you need to unit-test against internal helpers (for example
`GoogleOAuthHttp.UseClient(HttpClient)` to inject a fake transport).

## Credentials

`Create(appDirectory, logger)` resolves the OAuth client id/secret in this
order:

1. Environment variables `GOOGLE_CLIENT_ID` **and** `GOOGLE_CLIENT_SECRET`
   (both or neither — setting only one is an error).
2. `client_secrets.json` in `appDirectory`. Accepts the standard
   `{ "installed": { ... } }` or `{ "web": { ... } }` shape, or `client_id` /
   `client_secret` at the root.

`ResolveSecretsDirectory()` returns the current working directory if it
contains `client_secrets.json`, else `AppContext.BaseDirectory` if that does,
else the current working directory. Pass the result as `appDirectory`, or pass
any directory you manage yourself.

To avoid file discovery entirely, call the constructor with the id/secret you
already hold.

## Token cache

| | |
|---|---|
| Location (via `Create`) | `%LOCALAPPDATA%\HelloWorldConsole\` |
| Device file | `google-device-token.bin` |
| Loopback file | `google-loopback-token.bin` |
| Protection | DPAPI (`CurrentUser`) + ACL restricted to the current user |
| Writes | atomic (temp file + move) |

> The default directory name is `HelloWorldConsole` — it is not derived from
> your application name. To use your own location, pass an explicit `tokenPath`
> to the constructor instead of `Create`.

A legacy plaintext `google-device-token.json` is migrated to the encrypted
`.bin` on the next successful device sign-in, then deleted.

## Minimal example (device flow)

```csharp
using Microsoft.Extensions.Logging;
using Svrn7.Trust.Google;

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var logger = loggerFactory.CreateLogger("sign-on");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var device = GoogleDeviceSignOn.Create(GoogleOAuthUtil.ResolveSecretsDirectory(), logger);

try
{
    GoogleUser user = await device.SignInAsync(forceInteractive: false, cts.Token);
    Console.WriteLine($"Signed in as {user.Name} <{user.Email}> (sub {user.Subject})");
    if (user.EmailVerified != true)
        Console.WriteLine("Warning: email not verified by Google.");
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Sign-on cancelled.");
}
```

## Loopback example and the "blocked" case

```csharp
var loopback = GoogleLoopbackSignOn.Create(GoogleOAuthUtil.ResolveSecretsDirectory(), logger);

try
{
    GoogleUser user = await loopback.SignInAsync(forceInteractive: false, cts.Token);
}
catch (Exception ex) when (GoogleOAuthUtil.LooksLikeLoopbackBlocked(ex.ToString()))
{
    Console.Error.WriteLine(GoogleOAuthUtil.LoopbackBlockedGuidance);
    // fall back to GoogleDeviceSignOn
}
```

## Error handling

| Exception | When |
|---|---|
| `OperationCanceledException` | `cancellationToken` fired (e.g. Ctrl+C) |
| `TimeoutException` | device code expired before approval |
| `InvalidOperationException` | missing/invalid credentials, denied sign-on, unverifiable ID token, malformed Google response, HTTP failure after retries |
| `PlatformNotSupportedException` | token cache accessed on a non-Windows OS |

The ID token path **fails closed**: if Google returns an ID token that cannot
be validated, sign-on aborts with `InvalidOperationException` rather than
falling back to userinfo.

`InvalidOperationException` messages are written to be shown to the user
directly. For device-code setup failures the message already includes
remediation (create a "TVs and Limited Input devices" client, etc.).

## Telemetry (OpenTelemetry)

The library is instrumented with the in-box `System.Diagnostics` primitives
(`ActivitySource` + `Meter`) and takes **no dependency on the OpenTelemetry
SDK**. With nothing listening it is effectively free. Wire it into your app's
OpenTelemetry pipeline by name:

```csharp
using Svrn7.Trust.Google;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(GoogleTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(GoogleTelemetry.MeterName));
```

Both names are the string `"Svrn7.Trust.Google"`.

The sample `HelloWorldConsole` app wires this up: the console exporter (spans
and metrics) is on by default and `--no-otel-console` disables it; `--jaeger`
additionally exports spans over OTLP (`http://localhost:4317` by default). The
library emits regardless of whether anything is listening.

### Spans

| Name | Emitted by | Key tags |
|---|---|---|
| `GoogleSignOn.Device` | `GoogleDeviceSignOn.SignInAsync` | `signon.flow`, `signon.force_interactive`, `signon.from_cache`, `signon.outcome`, `error.type` |
| `GoogleSignOn.Loopback` | `GoogleLoopbackSignOn.SignInAsync` | same as above |
| `GoogleSignOn.TokenRefresh` | either flow, on an access-token refresh | `signon.flow`, `signon.refresh_result` (`ok` / `rejected` / `error`) |
| `GoogleSignOn.ResolveIdentity` | ID-token validation or userinfo lookup | `signon.identity_source` (`id_token` / `userinfo`), `error.type` |

`signon.outcome` is `success`, `cancelled`, or `error`. Spans are set to
`ActivityStatusCode.Error` on cancellation and on any thrown exception.

### Metrics

| Instrument | Kind | Unit | Tags |
|---|---|---|---|
| `svrn7.google.sign_in.attempts` | Counter&lt;long&gt; | `{attempt}` | `signon.flow` |
| `svrn7.google.sign_in.duration` | Histogram&lt;double&gt; | `s` | `signon.flow`, `signon.outcome` |
| `svrn7.google.token.refreshes` | Counter&lt;long&gt; | `{refresh}` | `signon.flow`, `signon.refresh_result` |
| `svrn7.google.http.retries` | Counter&lt;long&gt; | `{retry}` | `http.retry_reason` (`status_429` / `status_5xx` / `exception` / `timeout`) |

The histogram's count is the number of completed sign-on attempts; break it
down by `signon.outcome` for a success/failure/cancel split.

No secrets, tokens, user codes, email addresses, or subject ids are placed on
spans or metrics.

## Behavioural notes

- `SignInAsync` writes progress to `Console.Out` / `Console.Error` and may
  launch a browser (`ProcessStartInfo { UseShellExecute = true }`). Verification
  URLs are allow-listed to `https` Google hosts before a browser is opened.
- HTTP calls retry transient failures (429, 5xx, `HttpRequestException`) up to
  3 times with linear backoff. The shared `HttpClient` is process-wide.
- The instances are cheap; create one per sign-on. They hold no unmanaged
  resources and do not need disposal.
