param(
    [Parameter(Mandatory = $true)][int] $ParentId,
    [Parameter(Mandatory = $true)][long] $ParentStarted,
    [Parameter(Mandatory = $true)][string] $Source,
    [Parameter(Mandatory = $true)][string] $Target,
    [Parameter(Mandatory = $true)][string] $Sha256
)

$ErrorActionPreference = 'Stop'
function Get-UpdateHash([string] $Path) {
    $taskStream = [IO.File]::OpenRead($Path)
    $taskHasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($taskHasher.ComputeHash($taskStream)).Replace('-', '') }
    finally { $taskHasher.Dispose(); $taskStream.Dispose() }
}
function Start-UpdatedApplication([string] $Path) {
    $taskStart = New-Object Diagnostics.ProcessStartInfo
    $taskStart.FileName = $Path
    $taskStart.WorkingDirectory = [IO.Path]::GetDirectoryName($Path)
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    [Diagnostics.Process]::Start($taskStart).Dispose()
}
$taskDirectory = Split-Path -LiteralPath $Source
$taskLog = Join-Path $taskDirectory 'update.log'
$taskBackup = $Target + '.before-update-' + [guid]::NewGuid().ToString('N') + '.bak'
$taskCopied = $false
$taskParentExited = $false
try {
    # Signal readiness before the application releases its service connection and instance lock.
    Set-Content -LiteralPath (Join-Path $taskDirectory 'ready') -Value 'ready'
    $taskParent = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
    if ($null -ne $taskParent) {
        if ($taskParent.StartTime.ToUniversalTime().Ticks -ne $ParentStarted) { throw 'Parent identity changed.' }
        $taskParent.WaitForExit(120000) | Out-Null
        if (-not $taskParent.HasExited) { throw 'Application did not exit in time.' }
    }
    $taskParentExited = $true
    if ((Get-UpdateHash $Source) -ne $Sha256) { throw 'Checksum mismatch.' }
    Copy-Item -LiteralPath $Target -Destination $taskBackup -ErrorAction Stop
    $taskCopied = $true
    # A just-exited executable can remain locked briefly by Windows or a scanner.
    for ($taskAttempt = 0; $taskAttempt -lt 30; $taskAttempt++) {
        try { Copy-Item -LiteralPath $Source -Destination $Target -Force -ErrorAction Stop; break }
        catch { if ($taskAttempt -eq 29) { throw }; Start-Sleep -Milliseconds 200 }
    }
    if ((Get-UpdateHash $Target) -ne $Sha256) { throw 'Installed checksum mismatch.' }
    Start-UpdatedApplication $Target
    Set-Content -LiteralPath $taskLog -Value 'Update installed and restart requested.'
}
catch {
    $taskFailure = $_.Exception.GetType().Name + ' at line ' + $_.InvocationInfo.ScriptLineNumber
    if ($taskParentExited) {
        try {
            if ($taskCopied) { Copy-Item -LiteralPath $taskBackup -Destination $Target -Force -ErrorAction Stop }
            Start-UpdatedApplication $Target
        }
        catch { $taskFailure += '; recovery: ' + $_.Exception.GetType().Name }
    }
    Set-Content -LiteralPath $taskLog -Value ('Update failed: ' + $taskFailure)
    # Preserve the package, script, log and backup for recovery. Never remove user files.
    exit 1
}
