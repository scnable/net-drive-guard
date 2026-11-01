param(
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [switch]$SkipClippy,
    [switch]$Release
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../tools/windows-workspace.ps1')
$WorkDirectory = Initialize-NdgWorkspace $WorkDirectory
$TargetDir = Join-Path $WorkDirectory 'target'
$desktop = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $desktop 'dist'
if (!(Test-Path (Join-Path $dist 'index.html'))) { throw 'Run npm run build in desktop first.' }
# Tauri walks assets with follow_links=true. The SMB server can reject the
# directory identity query (Windows error 87), although normal reads succeed.
# Stage generated assets alongside local build outputs; source stays shared.
$assets = Join-Path $WorkDirectory ('frontend-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $assets | Out-Null
# SMB directory enumeration can retain filenames from the preceding Vite build.
# Read an explicit inventory and verify every byte before compiling the host.
$inventory = Get-Content (Join-Path $dist 'asset-manifest.json') -Raw | ConvertFrom-Json
if (!$inventory -or !($inventory | Where-Object { $_.path -eq 'index.html' })) { throw 'Frontend asset inventory is missing index.html.' }
foreach ($entry in $inventory) {
    if ($entry.path -match '(^[/\\]|:|(^|[/\\])\.\.([/\\]|$))') { throw 'Invalid frontend asset path.' }
    $source = Join-Path $dist $entry.path
    $destination = Join-Path $assets $entry.path
    New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
    [IO.File]::Copy($source, $destination, $true)
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Frontend asset hash mismatch: $($entry.path). Rebuild the frontend and retry."
    }
}
$oldConfig = $env:TAURI_CONFIG
$oldTarget = $env:CARGO_TARGET_DIR
try {
    $config = if ($oldConfig) { $oldConfig | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (!$config.PSObject.Properties['build']) { $config | Add-Member build ([pscustomobject]@{}) }
    # Extended Windows paths deserialize as a directory, not a URL (C: scheme).
    $config.build | Add-Member frontendDist ("\\?\" + [IO.Path]::GetFullPath($assets)) -Force
    $env:TAURI_CONFIG = $config | ConvertTo-Json -Depth 32 -Compress
    $env:CARGO_TARGET_DIR = $TargetDir
    $manifest = Join-Path $desktop 'src-tauri/Cargo.toml'
    $buildArgs = @('build', '--locked', '--offline', '--manifest-path', $manifest, '--features', 'custom-protocol')
    if ($Release) { $buildArgs += '--release' }
    Invoke-NdgCargo (Join-Path $WorkDirectory 'desktop-build.log') $buildArgs
    if (!$SkipClippy) {
        $clippyArgs = @('clippy', '--locked', '--offline', '--manifest-path', $manifest, '--features', 'custom-protocol')
        if ($Release) { $clippyArgs += '--release' }
        Invoke-NdgCargo (Join-Path $WorkDirectory 'desktop-clippy.log') ($clippyArgs + @('--', '-D', 'warnings'))
    }
} finally {
    $env:TAURI_CONFIG = $oldConfig
    $env:CARGO_TARGET_DIR = $oldTarget
}
