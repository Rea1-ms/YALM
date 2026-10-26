#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskStamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
$taskStage = Join-Path $taskRoot ".cache/publish/$taskStamp-$([guid]::NewGuid().ToString('N'))"
$taskPublish = Join-Path $taskStage 'publish'
$taskStandalone = Join-Path $taskStage 'standalone'
$taskOutput = Join-Path $taskRoot 'artifacts/portable'

Push-Location -LiteralPath $taskRoot
try {
    New-Item -ItemType Directory -Path $taskPublish, $taskStandalone -Force | Out-Null
    # 与上游相同：单文件、依赖桌面运行时，发布配置集中保存在现有配置文件。
    & dotnet publish app/YALM.csproj --configuration Release `
        -p:PublishProfile=FolderProfile --output $taskPublish --nologo
    if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码：$LASTEXITCODE" }

    # 仅复制可执行文件，验证运行时不依赖发布目录的其他文件。
    $taskExe = Join-Path $taskStandalone 'YALM.exe'
    Copy-Item -LiteralPath (Join-Path $taskPublish 'YALM.exe') -Destination $taskExe
    $taskSmoke = Start-Process -FilePath $taskExe -ArgumentList '--smoke-test' `
        -WorkingDirectory $taskStandalone -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $taskStage 'smoke.stdout.log') `
        -RedirectStandardError (Join-Path $taskStage 'smoke.stderr.log')
    if (-not $taskSmoke.WaitForExit(20000)) {
        throw "演示自检未按时退出，检查进程和缓存已保留：$taskStage"
    }
    $taskSmoke.Refresh()
    if ($taskSmoke.ExitCode -ne 0) {
        throw "演示自检失败，退出码：$($taskSmoke.ExitCode)；记录：$taskStage"
    }

    # 分发内容使用明确清单，不将缓存、配置或凭据打入压缩包。
    $taskFiles = @($taskExe)
    foreach ($taskName in @('LICENSE', 'NOTICE.md', 'README.md')) {
        $taskCopy = Join-Path $taskStandalone $taskName
        Copy-Item -LiteralPath (Join-Path $taskRoot $taskName) -Destination $taskCopy
        $taskFiles += $taskCopy
    }
    $taskZip = Join-Path $taskStage 'YALM-win-x64.zip'
    Compress-Archive -LiteralPath $taskFiles -DestinationPath $taskZip

    New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
    Copy-Item -LiteralPath $taskFiles -Destination $taskOutput -Force
    Copy-Item -LiteralPath $taskZip -Destination $taskOutput -Force
    $taskResult = Get-Item -LiteralPath (Join-Path $taskOutput 'YALM.exe')
    Write-Output "单文件发布完成：$($taskResult.FullName)（$([math]::Round($taskResult.Length / 1MB, 2)) MiB，兆字节）"
    Write-Output "分发压缩包：$(Join-Path $taskOutput 'YALM-win-x64.zip')"
    Write-Output "仅含可执行文件的目录中，演示自检退出码为 0；仍需 .NET 10 桌面运行时。"
    Write-Output "缓存保留，可自行清理：$taskStage"
}
finally {
    Pop-Location
}
