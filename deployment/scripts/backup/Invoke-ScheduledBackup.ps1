<#
.SYNOPSIS
  Runs Backup-Raavi.ps1 for a scheduler (Task Scheduler / cron / SQL Agent job step), records the outcome and raises an alert on failure. (Phase 12f, ADR-042)
.DESCRIPTION
  * Status file per database and mode: <BackupDirectory>/status/<db>-<mode>.json (last success, last failure, last error, consecutive failures).
    Test-BackupFreshness.ps1 reads these files, so a monitor can notice a backup that silently stopped running.
  * Log file: <BackupDirectory>/logs/<db>-<yyyyMMdd>.log (no connection strings, no secrets).
  * On failure (and on the first success after failures) the alert channels given are used; every channel is best effort and one failing never hides
    the others or the original error:
      -AlertScript   a script path called as:  script.ps1 -Subject <text> -Body <text>     (generic hook: pager, chat, ticket, ...)
      -AlertWebhookUrl  HTTP POST of {"subject","body","host","database","mode","utcTime"}
      -AlertEmailTo / -SmtpServer / -AlertEmailFrom   Send-MailMessage (SMTP without credentials; use -AlertScript for authenticated mail)
      Windows Event Log (source "Raavi Backup", Application log) when it can be written.
  * Exit code 0 = backup succeeded; 1 = failed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerInstance,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$BackupDirectory,
    [ValidateSet('Full', 'Differential', 'Log')][string]$Mode = 'Full',
    [string]$EncryptionCertificate,
    [switch]$Compression,
    [ValidateRange(1, 3650)][int]$RetentionDays,
    [string]$AlertScript,
    [string]$AlertWebhookUrl,
    [string]$AlertEmailTo,
    [string]$AlertEmailFrom,
    [string]$SmtpServer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$null = Format-RaaviIdentifier $Database

New-Item -ItemType Directory -Path (Join-Path $BackupDirectory 'status'), (Join-Path $BackupDirectory 'logs') -Force | Out-Null
$statusFile = Join-Path $BackupDirectory "status/$Database-$Mode.json"
$logFile = Join-Path $BackupDirectory ("logs/{0}-{1}.log" -f $Database, (Get-Date).ToUniversalTime().ToString('yyyyMMdd'))

function Write-Log([string]$Text) {
    $line = '{0} [{1}] {2}' -f (Get-Date).ToUniversalTime().ToString('o'), $Mode, $Text
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
    Write-Host $line
}

function Hide-Secrets([string]$Text) {
    # connection strings / passwords must never reach logs, status files or alerts
    $t = [regex]::Replace($Text, '(?i)(password|pwd|secret|token|key)\s*=\s*[^;\s]+', '$1=***')
    if ($t.Length -gt 1500) { $t = $t.Substring(0, 1500) + '...' }
    return $t
}

function Send-Alert([string]$Subject, [string]$Body) {
    $hostName = [System.Environment]::MachineName
    $full = "$Body`nHost: $hostName`nDatabase: $Database`nMode: $Mode"
    try { if ($AlertScript) { & $AlertScript -Subject $Subject -Body $full } } catch { Write-Log "alert script failed: $(Hide-Secrets $_.Exception.Message)" }
    try {
        if ($AlertWebhookUrl) {
            $payload = @{ subject = $Subject; body = $Body; host = $hostName; database = $Database; mode = $Mode; utcTime = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json
            Invoke-RestMethod -Uri $AlertWebhookUrl -Method Post -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 20 | Out-Null
        }
    } catch { Write-Log "alert webhook failed: $(Hide-Secrets $_.Exception.Message)" }
    try { if ($AlertEmailTo -and $SmtpServer -and $AlertEmailFrom) { Send-MailMessage -To $AlertEmailTo -From $AlertEmailFrom -Subject $Subject -Body $full -SmtpServer $SmtpServer -Encoding UTF8 } } catch { Write-Log "alert e-mail failed: $(Hide-Secrets $_.Exception.Message)" }
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists('Raavi Backup')) { New-EventLog -LogName Application -Source 'Raavi Backup' }
        Write-EventLog -LogName Application -Source 'Raavi Backup' -EntryType Error -EventId 4101 -Message $full
    } catch { <# not allowed to write the event log: fine #> }
}

$previous = $null
if (Test-Path -LiteralPath $statusFile) { try { $previous = Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json } catch { $previous = $null } }
$failuresBefore = if ($previous -and $previous.PSObject.Properties['consecutiveFailures']) { [int]$previous.consecutiveFailures } else { 0 }
$lastSuccess = if ($previous -and $previous.PSObject.Properties['lastSuccessUtc']) { $previous.lastSuccessUtc } else { $null }
$lastFile = if ($previous -and $previous.PSObject.Properties['lastBackupFile']) { $previous.lastBackupFile } else { $null }
$lastFailure = if ($previous -and $previous.PSObject.Properties['lastFailureUtc']) { $previous.lastFailureUtc } else { $null }

try {
    Write-Log "starting $Mode backup of $Database on $ServerInstance"
    $params = @{ ServerInstance = $ServerInstance; Database = $Database; BackupDirectory = $BackupDirectory; Mode = $Mode }
    if ($EncryptionCertificate) { $params.EncryptionCertificate = $EncryptionCertificate }
    if ($Compression) { $params.Compression = $true }
    if ($RetentionDays) { $params.RetentionDays = $RetentionDays }
    $result = & (Join-Path $PSScriptRoot 'Backup-Raavi.ps1') @params | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['sha256'] } | Select-Object -Last 1
    if (-not $result) { throw 'Backup-Raavi.ps1 returned no manifest.' }
    Write-Log ("ok: {0} ({1:N1} MB, {2}s)" -f $result.file, ($result.sizeBytes / 1MB), $result.seconds)
    $status = [ordered]@{
        database = $Database; mode = $Mode; lastSuccessUtc = (Get-Date).ToUniversalTime().ToString('o'); lastBackupFile = $result.file
        lastFailureUtc = $lastFailure; lastError = $null; consecutiveFailures = 0
    }
    $status | ConvertTo-Json | Set-Content -LiteralPath $statusFile -Encoding UTF8
    if ($failuresBefore -gt 0) { Send-Alert "Raavi backup recovered ($Database $Mode)" "The $Mode backup of $Database succeeded again after $failuresBefore failure(s)." }
    exit 0
}
catch {
    $message = Hide-Secrets $_.Exception.Message
    Write-Log "FAILED: $message"
    $status = [ordered]@{
        database = $Database; mode = $Mode; lastSuccessUtc = $lastSuccess; lastBackupFile = $lastFile
        lastFailureUtc = (Get-Date).ToUniversalTime().ToString('o'); lastError = $message; consecutiveFailures = $failuresBefore + 1
    }
    try { $status | ConvertTo-Json | Set-Content -LiteralPath $statusFile -Encoding UTF8 } catch { Write-Log 'could not write the status file' }
    Send-Alert "Raavi backup FAILED ($Database $Mode)" "The $Mode backup of $Database failed (consecutive failures: $($failuresBefore + 1)): $message"
    exit 1
}
