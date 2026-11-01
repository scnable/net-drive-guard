function Initialize-NdgWorkspace([string]$WorkDirectory) {
    $full = [IO.Path]::GetFullPath($WorkDirectory).TrimEnd('\')
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if (!$full.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $full -Leaf) -notmatch '^netDriveGuard-[a-zA-Z0-9-]+$') {
        throw '工作目录必须位于 TEMP 下，名称为 netDriveGuard-加本次唯一后缀。'
    }
    New-Item -ItemType Directory -Force $full | Out-Null
    return $full
}

function Invoke-NdgCargo([string]$LogPath, [string[]]$CargoArguments) {
    # 调用参数由项目脚本提供；拒绝 cmd.exe 的控制字符。
    $values = @($CargoArguments) + @($LogPath)
    foreach ($value in $values) {
        if ($value -match '["%&|<>^!\r\n]') { throw '构建参数包含不支持的命令字符。' }
    }
    $quoted = ($CargoArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    & cmd.exe /d /s /c ('cargo ' + $quoted + ' > "' + $LogPath + '" 2>&1')
    $code = $LASTEXITCODE
    Get-Content -LiteralPath $LogPath -Encoding UTF8
    if ($code -ne 0) { throw "Cargo 执行失败（$code），日志：$LogPath" }
}
