# HelloWorldConsole

A .NET 8 console app that prints `Hello, World!` and then signs you in with Google. The default path is Google’s **installed-app loopback** flow (recommended for a Windows console with a browser). If that client type is not configured, the app falls back to the **device** (TV / limited-input) flow. After approval it verifies the ID token when present, then prints your Google name and email.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) (token encryption uses Windows DPAPI at runtime)
- A Google Cloud project
  - **Desktop app** OAuth client for default loopback sign-on
  - **TVs and Limited Input devices** client if you use `--device` (or as fallback)

## Run

From the repo root:

```bash
dotnet run
dotnet test
```

Or in Cursor / VS Code: **Run Without Debugging** (`Ctrl+F5`) or **Start Debugging** (`F5`).

### Where output goes

Launch is configured in `.vscode/launch.json` with `"console": "integratedTerminal"`. `Console.WriteLine` output appears in the **Terminal** panel, not the Debug Console. Structured logs (timestamps) also go to the console; pass `--verbose` for debug-level logs.

The build task and the running app can use different terminal tabs. Switch to the tab that launched the app if you do not see `Hello, World!` or the Google code.

### Debugging

The launch configuration starts `bin/Debug/net8.0/HelloWorldConsole.dll` after a `preLaunchTask` build. Working directory is the workspace folder. `client_secrets.json` is resolved from the current directory first, then the directory that contains the DLL.

If a debug or “run without debugging” session pauses on the first line of `Program.cs`, check `stopAtEntry` in `.vscode/launch.json`. When that is `true`, the .NET debugger still stops at the entry point even for Run Without Debugging, because that command uses the same launch config. Set `"stopAtEntry": false` to run straight through, then continue (`F5`) if you are already paused.

A run with no debugger at all is `dotnet run` in the terminal.

## Google sign-on

**Loopback (default):** the app opens a browser and listens on `http://127.0.0.1` for the OAuth redirect ([OAuth 2.0 for mobile and desktop apps](https://developers.google.com/identity/protocols/oauth2/native-app)).

**Device (`--device`, or automatic fallback):** [OAuth 2.0 for TVs and limited-input devices](https://developers.google.com/identity/protocols/oauth2/limited-input-device). The app prints a user code, allowlists `https` Google verification URLs before opening a browser, and polls until you approve.

Both paths request `openid email profile`. The ID token is validated with Google’s certificates when Google returns one; otherwise the app loads [userinfo](https://www.googleapis.com/oauth2/v3/userinfo).

### Create OAuth credentials

1. Open [Google Cloud Console → Credentials](https://console.cloud.google.com/apis/credentials).
2. Create an **OAuth client ID**.
3. For default sign-on, application type **Desktop app**. For `--device`, type **TVs and Limited Input devices**.
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

`client_secrets.json` is gitignored. Do not commit real secrets.

### Saved tokens

Encrypted token files live under `%LocalAppData%\HelloWorldConsole\`:

- `google-loopback-token.bin` — loopback session (DPAPI, current-user ACL)
- `google-device-token.bin` — device-flow session (same protections)

Writes are atomic. An older plaintext `google-device-token.json` is migrated on the next successful device sign-in, then deleted. Ctrl+C cancels a waiting sign-on.

### Command-line flags

| Flag | Effect |
| --- | --- |
| *(none)* | Loopback sign-on; on client-config errors, fall back to device flow |
| `--loopback` | Loopback only (no device fallback) |
| `--device` | Device flow only |
| `--reauth` | Ignore saved tokens and sign in again |
| `--sign-out` | Delete local Google token files and exit |
| `--verbose` | Debug-level structured logs |

Examples:

```bash
dotnet run
dotnet run -- --device
dotnet run -- --loopback --verbose
dotnet run -- --reauth
dotnet run -- --sign-out
```

To pass flags from the debugger, put them in `"args"` in `.vscode/launch.json`.
