# HelloWorldConsole
Google direct-device authentication sample (with resuable library)

A .NET 8 console app that prints `Hello, World!` and then signs you in with Google using the **device** (TV / limited-input) OAuth flow by default. Google has **blocked the loopback IP flow** for many installed clients (including **Drive API Quickstart**), which shows Error 400 `invalid_request` in the browser. Device flow avoids that. `--loopback` remains available only for a Desktop client that still allows `http://127.0.0.1` redirects.

All of the Google sign-on logic lives in the reusable **`Svrn7.Trust.Google`** class library; `HelloWorldConsole` is just a thin driver over it. To use the library in your own project, see [`Svrn7.Trust.Google/GUIDE.md`](Svrn7.Trust.Google/GUIDE.md).

## Repository layout

```
HelloWorldConsole.slnx              solution
Svrn7.Trust.Google/                 reusable library (OAuth device + loopback sign-on)
  GUIDE.md                          how to consume the library
HelloWorldConsole/                  console app (driver)
  Program.cs
  Properties/launchSettings.json
HelloWorldConsole.Tests/            xUnit tests for the library
client_secrets.json[.example]       OAuth client credentials (repo root; gitignored)
```

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) (token encryption uses Windows DPAPI at runtime)
- A Google Cloud project
  - **TVs and Limited Input devices** OAuth client for default device sign-on (required for Drive Quickstart–era credentials; those clients cannot use loopback)
  - **Desktop app** only if you pass `--loopback` and Google still allows loopback for that client

## Run

From the repo root:

```bash
dotnet run --project HelloWorldConsole
dotnet test
```

Or in Cursor / VS Code: **Run Without Debugging** (`Ctrl+F5`) or **Start Debugging** (`F5`).

### Where output goes

Launch is configured in `.vscode/launch.json` with `"console": "integratedTerminal"`. `Console.WriteLine` output appears in the **Terminal** panel, not the Debug Console. Structured logs (timestamps) also go to the console; pass `--verbose` for debug-level logs.

The build task and the running app can use different terminal tabs. Switch to the tab that launched the app if you do not see `Hello, World!` or the Google code.

### Debugging

The launch configuration starts `HelloWorldConsole/bin/Debug/net8.0/HelloWorldConsole.dll` after a `preLaunchTask` build. Working directory is the repo root (the `launchSettings.json` profile sets `workingDirectory` to `$(ProjectDir)..`, and `.vscode/launch.json` sets `cwd` to `${workspaceFolder}`). `client_secrets.json` is resolved from the current directory first, then the directory that contains the DLL.

If a debug or “run without debugging” session pauses on the first line of `Program.cs`, check `stopAtEntry` in `.vscode/launch.json`. When that is `true`, the .NET debugger still stops at the entry point even for Run Without Debugging, because that command uses the same launch config. Set `"stopAtEntry": false` to run straight through, then continue (`F5`) if you are already paused.

A run with no debugger at all is `dotnet run --project HelloWorldConsole` in the terminal.

## Google sign-on

**Device (default):** [OAuth 2.0 for TVs and limited-input devices](https://developers.google.com/identity/protocols/oauth2/limited-input-device). The app prints a user code, allowlists `https` Google verification URLs before opening a browser, and polls until you approve.

**Loopback (`--loopback` only):** [OAuth 2.0 for desktop apps](https://developers.google.com/identity/protocols/oauth2/native-app) via `http://127.0.0.1`. Google currently **blocks this flow** for many clients. If the browser says *“The loopback flow has been blocked”* / Error 400 `invalid_request` / *Drive API Quickstart sent an invalid request*, close the tab and run **without** `--loopback`. Create a **TVs and Limited Input devices** client if device sign-on also rejects the existing client.

Both paths request `openid email profile`. The ID token is validated with Google’s certificates when Google returns one; otherwise the app loads [userinfo](https://www.googleapis.com/oauth2/v3/userinfo).

### Create OAuth credentials

1. Open [Google Cloud Console → Credentials](https://console.cloud.google.com/apis/credentials).
2. Create an **OAuth client ID**.
3. Application type **TVs and Limited Input devices** (do not reuse a Drive API Quickstart / Desktop client for the default flow).
4. Configure the **OAuth consent screen**. If the app is in Testing, add your Google account as a test user.

### Give the app the client ID and secret

Pick one:

**File (recommended for local use)**

1. Copy `client_secrets.json.example` to `client_secrets.json` in the repo root (or next to the built DLL).
2. Fill in `client_id` and `client_secret`.

```json
{
  "installed": {
    "client_id": "YOUR_CLIENT_ID.apps.googleusercontent.com",
    "client_secret": "YOUR_CLIENT_SECRET"
  }
}
```

Root-level `client_id` / `client_secret`, or a nested `web` object, are also accepted.

**Environment variables**

Set **both** `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`. If either is set, both must be set (they are not mixed with `client_secrets.json`).

`client_secrets.json` is gitignored. Do not commit real secrets. Do not ship this console exe to other machines with the client secret baked in or copied beside it — Google desktop/TV client secrets are not confidential once the binary is distributed. This app is for local use on your Windows account.

### Saved tokens

Encrypted token files live under `%LocalAppData%\HelloWorldConsole\`:

- `google-loopback-token.bin` — loopback session (DPAPI, current-user ACL)
- `google-device-token.bin` — device-flow session (same protections)

Writes are atomic. An older plaintext `google-device-token.json` is migrated on the next successful device sign-in, then deleted. Ctrl+C cancels a waiting sign-on.

### Command-line flags

| Flag | Effect |
| --- | --- |
| *(none)* or `--device` | Device flow (avoids Google’s loopback block) |
| `--loopback` | Loopback only (often blocked; Drive Quickstart clients fail with Error 400) |
| `--reauth` | Ignore saved tokens and sign in again |
| `--sign-out` | Delete local Google token files and exit |
| `--verbose` | Debug-level structured logs |
| `--no-otel-console` | Turn **off** the console OpenTelemetry exporter (on by default: `Svrn7.Trust.Google` spans and metrics print to the console) |
| `--jaeger` | Also export spans over OTLP (gRPC `http://localhost:4317` by default; set `OTEL_EXPORTER_OTLP_ENDPOINT` to change) |

Examples:

```bash
dotnet run --project HelloWorldConsole
dotnet run --project HelloWorldConsole -- --device
dotnet run --project HelloWorldConsole -- --loopback --verbose
dotnet run --project HelloWorldConsole -- --reauth
dotnet run --project HelloWorldConsole -- --sign-out
dotnet run --project HelloWorldConsole -- --no-otel-console
dotnet run --project HelloWorldConsole -- --jaeger
```

The console OpenTelemetry exporter is **on by default** — `Svrn7.Trust.Google`
spans and metrics print to the console after each run; `--no-otel-console`
silences it. `--jaeger` additionally exports spans over OTLP. These flags only
wire the sample app to an exporter; the library emits regardless. See
[`Svrn7.Trust.Google/GUIDE.md`](Svrn7.Trust.Google/GUIDE.md#telemetry-opentelemetry).
`docs/DEBUG.ps1 -NoOtel` / `-Jaeger` do the same from the debug helper.

To pass flags from the debugger, put them in `"args"` in `.vscode/launch.json`.
