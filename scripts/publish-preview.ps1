param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$requestedOutput = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
}
$allowedPrefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $requestedOutput.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw '发布目录必须位于项目 artifacts 目录内。'
}

if (Test-Path -LiteralPath $requestedOutput) {
    throw "发布目录已存在，拒绝覆盖：$requestedOutput"
}

$localDotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
}
else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-cli-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

& $dotnet publish (Join-Path $projectRoot 'src\NetDriveGuard.App\NetDriveGuard.App.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $requestedOutput `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "发布失败，dotnet 退出码：$LASTEXITCODE"
}

$executable = Join-Path $requestedOutput 'NetDriveGuard.App.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw '发布结束但未找到 NetDriveGuard.App.exe。'
}

Get-Item -LiteralPath $executable
