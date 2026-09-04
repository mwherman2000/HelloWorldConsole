# HelloWorldConsole

A .NET 8 console app that prints `Hello, World!` and then signs you in with Google using the **device** (TV / limited-input) OAuth flow. After you approve in the browser, it prints your Google name and email.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- A Google Cloud project with an OAuth client of type **TVs and Limited Input devices**

## Run

From the repo root:

```bash
dotnet run
```

Or in Cursor / VS Code: **Run Without Debugging** (`Ctrl+F5`) or **Start Debugging** (`F5`).

### Where output goes

Launch is configured in `.vscode/launch.json` with `"console": "integratedTerminal"`. `Console.WriteLine` output appears in the **Terminal** panel, not the Debug Console.

The build task and the running app can use different terminal tabs. Switch to the tab that launched the app if you do not see `Hello, World!` or the Google code.

### Debugging

The launch configuration starts `bin/Debug/net8.0/HelloWorldConsole.dll` after a `preLaunchTask` build. Working directory is the workspace folder, so `client_secrets.json` in the repo root is found when you debug.

If a debug or “run without debugging” session pauses on the first line of `Program.cs`, check `stopAtEntry` in `.vscode/launch.json`. When that is `true`, the .NET debugger still stops at the entry point even for Run Without Debugging, because that command uses the same launch config. Set `"stopAtEntry": false` to run straight through, then continue (`F5`) if you are already paused.

A run with no debugger at all is `dotnet run` in the terminal.

## Google device sign-on

This is Google’s [OAuth 2.0 device flow](https://developers.google.com/identity/protocols/oauth2/limited-input-device) / [Sign-In on TVs and Limited Input Devices](https://developers.google.com/identity/gsi/web/guides/devices):

1. The app requests a device code and user code from `https://oauth2.googleapis.com/device/code` with scopes `openid email profile`.
2. It prints the verification URL (`https://www.google.com/device`) and the user code, and tries to open the URL in your default browser on this PC.
3. You enter the code and approve access in the browser (on this machine or another device).
4. The app polls `https://oauth2.googleapis.com/token` until you finish, then loads your profile from `https://www.googleapis.com/oauth2/v3/userinfo`.

Later runs reuse a stored refresh token so you are not prompted every time.

### Create OAuth credentials

1. Open [Google Cloud Console → Credentials](https://console.cloud.google.com/apis/credentials).
2. Create an **OAuth client ID**.
3. Application type must be **TVs and Limited Input devices** (desktop / web clients will not work with this flow).
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

Set `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`. These override missing values from the file.

`client_secrets.json` is gitignored. Do not commit real secrets.

### Saved tokens

Refresh tokens are stored at:

`%LocalAppData%\HelloWorldConsole\google-device-token.json`

That path is outside the repo.

### Command-line flags

| Flag | Effect |
| --- | --- |
| *(none)* | Sign in; reuse saved tokens if they still refresh |
| `--reauth` | Ignore saved tokens and run the device flow again |
| `--sign-out` | Delete the local token file and exit |

Examples:

```bash
dotnet run
dotnet run -- --reauth
dotnet run -- --sign-out
```

To pass flags from the debugger, put them in `"args"` in `.vscode/launch.json`.
