param(
    [Parameter(Mandatory=$true)][string]$TargetDir,
    [string]$OutputDirectory = 'D:\成果物\netDriveGuard',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $TargetDir 'release/net-drive-guard-desktop.exe'
if (!(Test-Path $exe)) { throw 'Run build-windows.ps1 -Release first.' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$destination = Join-Path $OutputDirectory 'NetDriveGuard.exe'
if ((Test-Path $destination) -and !$Force) { throw 'Executable already exists. Use -Force to replace it.' }
try {
    [IO.File]::Copy($exe, $destination, [bool]$Force)
} catch {
    throw "无法更新程序文件；请指定独立版本目录交付，不要结束用户进程。目标：$destination。$($_.Exception.Message)"
}
$sourceHash = (Get-FileHash $exe -Algorithm SHA256).Hash
$destinationHash = (Get-FileHash $destination -Algorithm SHA256).Hash
if ($sourceHash -ne $destinationHash) { throw 'Executable copy verification failed.' }
Get-Item $destination | Select-Object FullName, Length
"SHA256=$($destinationHash.ToLowerInvariant())"

[pscustomobject]@{
    file='NetDriveGuard.exe'; sha256=$destinationHash.ToLowerInvariant()
    deliveredAt=[DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'NetDriveGuard.sha256.json') -Encoding UTF8
