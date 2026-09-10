<#
.SYNOPSIS
    Build and run HelloWorldConsole with debugging aids, from the solution root.

.DESCRIPTION
    Wraps the console app so the common local-debug pitfalls are handled:

      * Runs with the working directory set to the solution root, so
        `client_secrets.json` in the repo root is found by
        GoogleOAuthUtil.ResolveSecretsDirectory().
      * Prints an environment report (SDK, installed runtimes, credential
        source, token cache) before launching.
      * If no .NET 8 runtime is installed, sets DOTNET_ROLL_FORWARD=Major so a
        net8.0 build runs on 9.x / 10.x instead of failing to start.
      * Forwards the app flags (--reauth, --sign-out, --device, --loopback,
        --verbose).

.PARAMETER Reauth
    Pass --reauth: ignore any cached tokens and sign in again.

.PARAMETER SignOut
    Pass --sign-out: delete the local token cache and exit (no network).

.PARAMETER Device
    Pass --device: force the device (TV / limited-input) flow.

.PARAMETER Loopback
    Pass --loopback: force the loopback flow (Google blocks this for many
    clients; expect Error 400 invalid_request).

.PARAMETER Trace
    Pass --verbose: debug-level structured logs from the app.

.PARAMETER NoBuild
    Skip the build step and run the existing binary.

.PARAMETER CheckOnly
    Print the environment report and exit without building or running.

.PARAMETER Otel
    Pass --otel-console: export spans and metrics from the Svrn7.Trust.Google
    library to the console via the OpenTelemetry SDK.

.PARAMETER Jaeger
    Pass --jaeger: export spans over OTLP to a local collector / Jaeger
    all-in-one (gRPC http://localhost:4317 by default; override with
    OTEL_EXPORTER_OTLP_ENDPOINT). Start one with:
      docker run --rm -p 16686:16686 -p 4317:4317 jaegertracing/all-in-one
    then open http://localhost:16686.

.PARAMETER NewWindow
    Launch the app in a separate console window via Start-Process and return
    immediately (the window stays open for the device-flow wait or for a
    debugger to attach). Without it, the app runs in this console and the
    script blocks until it exits, then reports the exit code.

.EXAMPLE
    ./docs/DEBUG.ps1
    Build and run the default (device) flow. Run from the repo root; the script
    resolves paths relative to its own location (docs/), not the caller's.

.EXAMPLE
    ./docs/DEBUG.ps1 -Reauth -Trace

.EXAMPLE
    ./docs/DEBUG.ps1 -Otel
    Run with OpenTelemetry spans/metrics printed to the console.

.EXAMPLE
    ./docs/DEBUG.ps1 -Jaeger
    Run with traces exported to a local Jaeger.

.EXAMPLE
    ./docs/DEBUG.ps1 -CheckOnly
    Just show the environment report.

.EXAMPLE
    ./docs/DEBUG.ps1 -SignOut
    Clear the local token cache.
#>
[CmdletBinding()]
param(
    [switch]$Reauth,
    [switch]$SignOut,
    [switch]$Device,
    [switch]$Loopback,
    [switch]$Trace,
    [switch]$Otel,
    [switch]$Jaeger,
    [switch]$NoBuild,
    [switch]$CheckOnly,
    [switch]$NewWindow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This script lives in docs/; the repo root is its parent.
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$Project     = Join-Path $RepoRoot 'HelloWorldConsole'
$Csproj      = Join-Path $Project  'HelloWorldConsole.csproj'
$Solution    = Join-Path $RepoRoot 'HelloWorldConsole.slnx'
$SecretsFile = Join-Path $RepoRoot 'client_secrets.json'
$TokenDir    = Join-Path $env:LOCALAPPDATA 'HelloWorldConsole'

function Write-Section($text) {
    Write-Host ''
    Write-Host "== $text ==" -ForegroundColor Cyan
}

function Test-Net8Runtime {
    $runtimes = & dotnet --list-runtimes 2>$null
    return [bool]($runtimes | Select-String -SimpleMatch 'Microsoft.NETCore.App 8.')
}

function Write-EnvironmentReport {
    Write-Section 'Toolchain'
    Write-Host ("SDK           : {0}" -f (& dotnet --version))
    $netcore = & dotnet --list-runtimes 2>$null | Select-String 'Microsoft.NETCore.App'
    Write-Host 'NETCore.App   :'
    $netcore | ForEach-Object { Write-Host ("  {0}" -f $_.Line) }
    $has8 = Test-Net8Runtime
    $color = if ($has8) { 'Green' } else { 'Yellow' }
    Write-Host ("net8.0 runtime: {0}" -f $(if ($has8) { 'installed' } else { 'MISSING -> will use DOTNET_ROLL_FORWARD=Major' })) -ForegroundColor $color

    Write-Section 'Credentials'
    $idSet = -not [string]::IsNullOrWhiteSpace($env:GOOGLE_CLIENT_ID)
    $secretSet = -not [string]::IsNullOrWhiteSpace($env:GOOGLE_CLIENT_SECRET)
    Write-Host ("GOOGLE_CLIENT_ID     : {0}" -f $(if ($idSet) { 'set' } else { 'not set' }))
    Write-Host ("GOOGLE_CLIENT_SECRET : {0}" -f $(if ($secretSet) { 'set' } else { 'not set' }))
    if ($idSet -xor $secretSet) {
        Write-Host 'Only one of the two env vars is set - the app requires both or neither.' -ForegroundColor Yellow
    }
    Write-Host ("client_secrets.json  : {0}" -f $(if (Test-Path $SecretsFile) { $SecretsFile } else { 'not found in repo root' }))
    if (-not $idSet -and -not $secretSet -and -not (Test-Path $SecretsFile)) {
        Write-Host 'No credentials available: set the env vars or add client_secrets.json (see README).' -ForegroundColor Yellow
    }

    Write-Section 'Token cache'
    Write-Host ("Directory: {0}" -f $TokenDir)
    if (Test-Path $TokenDir) {
        $files = Get-ChildItem -File $TokenDir -ErrorAction SilentlyContinue
        if ($files) {
            $files | ForEach-Object { Write-Host ("  {0,-28} {1,8} bytes  {2}" -f $_.Name, $_.Length, $_.LastWriteTime) }
        }
        else {
            Write-Host '  (empty - no saved sessions)'
        }
    }
    else {
        Write-Host '  (does not exist yet - no saved sessions)'
    }

    Write-Section 'Run context'
    Write-Host ("Working directory: {0}" -f $RepoRoot)
    Write-Host ("Project         : {0}" -f $Csproj)
}

# --- main ------------------------------------------------------------------

if (-not (Test-Path $Csproj)) {
    throw "Cannot find $Csproj - this script must stay in the repo's docs/ folder."
}

Write-EnvironmentReport

if ($CheckOnly) {
    return
}

if (-not (Test-Net8Runtime)) {
    $env:DOTNET_ROLL_FORWARD = 'Major'
    Write-Host ''
    Write-Host 'net8.0 runtime not found - set DOTNET_ROLL_FORWARD=Major for this run.' -ForegroundColor Yellow
}

if (-not $NoBuild) {
    Write-Section 'Build'
    & dotnet build $Solution --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

$appArgs = @()
if ($SignOut)  { $appArgs += '--sign-out' }
if ($Device)   { $appArgs += '--device' }
if ($Loopback) { $appArgs += '--loopback' }
if ($Reauth)   { $appArgs += '--reauth' }
if ($Trace)    { $appArgs += '--verbose' }
if ($Otel)     { $appArgs += '--otel-console' }
if ($Jaeger)   { $appArgs += '--jaeger' }

$runArgs = @('run', '--project', $Csproj, '--no-launch-profile')
if ($NoBuild) { $runArgs += '--no-build' }
if ($appArgs.Count -gt 0) { $runArgs += '--'; $runArgs += $appArgs }

Write-Section 'Run'
Write-Host ("dotnet {0}" -f ($runArgs -join ' '))
Write-Host ("Working directory: {0}" -f $RepoRoot)
Write-Host ''

# Launch as a child process. Start-Process -WorkingDirectory pins the app's
# current directory to the repo root (so client_secrets.json resolves) and
# -PassThru gives us the PID to attach a debugger to.
$startParams = @{
    FilePath         = 'dotnet'
    ArgumentList     = $runArgs
    WorkingDirectory = $RepoRoot
    PassThru         = $true
}
if (-not $NewWindow) { $startParams['NoNewWindow'] = $true }

$proc = Start-Process @startParams
Write-Host ("Started dotnet (PID {0}) - use 'Attach to Process' to debug it." -f $proc.Id) -ForegroundColor Cyan

if ($NewWindow) {
    Write-Host 'Running in a separate window; this script is done.'
    return
}

$proc.WaitForExit()
$exit = $proc.ExitCode

Write-Host ''
Write-Host ("Exit code: {0}" -f $exit) -ForegroundColor $(if ($exit -eq 0) { 'Green' } else { 'Yellow' })
exit $exit
