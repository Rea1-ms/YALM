#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot
$taskScript = Join-Path $taskRoot 'app/Mechrevo/Updates/ApplyUpdate.ps1'
$taskStage = Join-Path $taskRoot ('.cache/update-handoff/' + [guid]::NewGuid().ToString('N') + " space [bracket] 'quote' `$literal")
$taskShell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
$taskHarmless = Join-Path $env:WINDIR 'System32/where.exe'

foreach ($taskCase in @('success', 'checksum', 'rollback', 'parent-identity', 'parent-wait')) {
    $taskDirectory = Join-Path $taskStage $taskCase
    New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
    $taskSource = Join-Path $taskDirectory 'staged.exe'
    $taskTarget = Join-Path $taskDirectory 'target.exe'
    Copy-Item -LiteralPath $taskHarmless -Destination $taskTarget
    [IO.File]::AppendAllText($taskTarget, 'previous version')
    if ($taskCase -eq 'rollback') { [IO.File]::WriteAllText($taskSource, 'invalid executable') }
    else { Copy-Item -LiteralPath $taskHarmless -Destination $taskSource }
    $taskBefore = (Get-FileHash -LiteralPath $taskTarget).Hash
    $taskSourceHash = (Get-FileHash -LiteralPath $taskSource).Hash
    $taskHash = (Get-FileHash -LiteralPath $taskSource).Hash
    if ($taskCase -eq 'checksum') { $taskHash = '0' * 64 }
    $taskStart = [Diagnostics.ProcessStartInfo]::new($taskShell)
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskParent = if ($taskCase -eq 'parent-identity') { $PID } else { [int]::MaxValue }
    $taskParentStarted = 0L
    $taskWaitingParent = $null
    if ($taskCase -eq 'parent-wait') {
        $taskWaitingParent = Start-Process -FilePath $taskShell -ArgumentList '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 3' -WindowStyle Hidden -PassThru
        $taskParent = $taskWaitingParent.Id
        $taskParentStarted = $taskWaitingParent.StartTime.ToUniversalTime().Ticks
    }
    foreach ($taskArgument in @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $taskScript,
        '-ParentId', "$taskParent", '-ParentStarted', "$taskParentStarted", '-Source', $taskSource, '-Target', $taskTarget, '-Sha256', $taskHash)) {
        $taskStart.ArgumentList.Add($taskArgument)
    }
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    if ($null -ne $taskWaitingParent) {
        Start-Sleep -Milliseconds 700
        if ((Get-FileHash -LiteralPath $taskTarget).Hash -ne $taskBefore) { throw 'Helper replaced an executable before its parent exited.' }
    }
    if (-not $taskProcess.WaitForExit(20000)) { throw "Helper timed out: $taskCase" }
    $taskExit = $taskProcess.ExitCode
    $taskProcess.Dispose()
    $taskExpected = if ($taskCase -in @('success', 'parent-wait')) { 0 } else { 1 }
    if ($taskExit -ne $taskExpected) { throw "Unexpected helper exit: $taskCase ($taskExit)" }
    $taskExpectedHash = if ($taskCase -in @('success', 'parent-wait')) { $taskSourceHash } else { $taskBefore }
    if ((Get-FileHash -LiteralPath $taskTarget).Hash -ne $taskExpectedHash) { throw "Unexpected installed file: $taskCase" }
    if ($null -ne $taskWaitingParent) { $taskWaitingParent.Dispose() }
    if (-not (Test-Path -LiteralPath $taskSource)) { throw 'Staged source was removed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $taskDirectory 'update.log'))) { throw 'Missing outcome log.' }
    $taskBackups = @(Get-ChildItem -LiteralPath $taskDirectory -Filter '*.bak')
    if ($taskCase -in @('success', 'rollback', 'parent-wait') -and $taskBackups.Count -ne 1) { throw 'Missing original backup.' }
    if ($taskBackups.Count -eq 1 -and (Get-FileHash -LiteralPath $taskBackups[0].FullName).Hash -ne $taskBefore) { throw 'Backup does not match the original.' }
    if ($taskCase -in @('checksum', 'parent-identity') -and $taskBackups.Count -ne 0) { throw 'Failed validation touched the target.' }
    Write-Output "PASS: $taskCase"
}
Write-Output "PASS: 5 updater handoff checks; retained cache: $taskStage"
