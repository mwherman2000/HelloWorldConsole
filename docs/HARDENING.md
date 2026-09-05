# Hardening

This note records what was hardened in HelloWorldConsole’s Google sign-on path: robustness, fail-safe behavior, and limits of “production ready” for a **local Windows console app**.

It is **not** a substitute for [README.md](../README.md) (how to run and configure OAuth). That file is the operator guide; this file is the security and failure-mode guide.

## Scope

The app signs **one interactive user** in to Google on **this Windows account**. Tokens are bound to that user via DPAPI. The binary is not a confidential OAuth server and must **not** be distributed with `client_secret` copied beside it.

Target framework stays **`net8.0`**. DPAPI is called only when `OperatingSystem.IsWindows()` is true.

## Token storage

| Item | Behavior |
| --- | --- |
| Location | `%LocalAppData%\HelloWorldConsole\` |
| Files | `google-loopback-token.bin` (desktop/loopback), `google-device-token.bin` (device flow) |
| Encryption | Windows DPAPI, `DataProtectionScope.CurrentUser`, app-specific entropy |
| Write | Temp file in the same directory, then atomic `File.Move` overwrite |
| ACL | Best-effort: ACL rewritten so only the current user has Full Control |
| Legacy | Plaintext `google-device-token.json` is migrated on successful device sign-in, then deleted |
| Corrupt loopback cache | Unreadable `.bin` is ignored, a **warning is logged**, and sign-on starts as if there were no cache |
| Sign-out | `--sign-out` deletes both stores |

`--reauth` clears the loopback store (and skips using a saved device refresh token) so Google is prompted again.

## OAuth flows

**Default / `--device`:** [device / limited-input flow](https://developers.google.com/identity/protocols/oauth2/limited-input-device). Requires a **TVs and Limited Input devices** client. Google has blocked the loopback IP flow for many installed clients (Drive API Quickstart shows Error 400 `invalid_request` in the browser *before* redirect; the C# process cannot recover from that). Device flow is the supported default.

**`--loopback`:** [installed-app loopback](https://developers.google.com/identity/protocols/oauth2/native-app) via `GoogleWebAuthorizationBroker`. Opt-in only. `invalid_request` / “loopback flow has been blocked” are **not** treated as recoverable client-mismatch errors.

If loopback is still used internally, `TokenResponseException` codes `redirect_uri_mismatch`, `unauthorized_client`, and `invalid_client` are the only codes treated as loopback client mismatch (`IsLoopbackClientMismatch`).

## Identity verification

Scopes: `openid email profile`.

When Google returns an **ID token**, it is validated with `GoogleJsonWebSignature` (signature, issuer, **audience = this client id**). Validation failure **aborts** sign-on; the app does **not** fall back to userinfo. That avoids treating a bad JWT as “still signed in.”

When there is **no** ID token, the app loads [userinfo](https://www.googleapis.com/oauth2/v3/userinfo) with the access token over HTTPS.

Tokens are persisted **after** profile resolution succeeds. Unverified emails still sign in, with a console warning.

## HTTP and device polling

- Shared `HttpClient` with connection pooling and a 30s timeout.
- Retries (up to 3) on **429** and **5xx**, and on transport timeouts / `HttpRequestException`.
- Empty or non-JSON bodies fail with an HTTP status in the message (no raw HTML dump).
- Device token poll maps `authorization_pending`, `slow_down` (+5s interval), `access_denied`, and `expired_token`.
- Device **verification URL** must be `https` and host `google.com` or a subdomain (`*.google.com`) before it is printed as trusted or opened with `Process.Start`.

## Credentials

- `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET` must **both** be set or **neither**. They are never mixed with `client_secrets.json`.
- Secrets file is resolved from the **current directory**, then the directory of the DLL.
- Invalid JSON in `client_secrets.json` fails with a path-specific “not valid JSON” error, not a raw parser dump.

## Process and logging

- Ctrl+C cancels via `CancellationToken` (exit code **130**).
- Structured console logging (`Microsoft.Extensions.Logging`); `--verbose` sets Debug.
- Unexpected failures log the exception, print the message, and hint `dotnet run -- --reauth`.

## Tests

`HelloWorldConsole.Tests` is a **separate** project (not compiled into the exe). Run `dotnet test`.

Covered policies include: URL allowlist, env credential pairing, poll HTTP status/body mapping, non-JSON bodies, HTTP 503-then-success retry, corrupt secrets JSON, loopback mismatch codes (and that `invalid_request` does not match), invalid JWT fail-closed, DPAPI round-trip and corrupt blob, unreadable loopback store.

Not covered: a live browser login against Google’s production endpoints.

## Remaining limits

- A **Desktop/TV client secret** on a workstation is acceptable for personal use; it is not secret if the app is copied to other PCs.
- JWKS/network errors during JWT validation may still surface as Google.Apis exceptions rather than the wrapped “could not be verified” message.
- `GoogleOAuthHttp.UseClient` / `RetryDelayStep` exist so tests can inject `HttpClient`; they are process-wide and are not a service-hosting pattern.
- Loopback and device sessions are **separate files**; a user can have both until `--sign-out`.
