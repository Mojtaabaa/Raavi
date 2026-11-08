<#
.SYNOPSIS
  Restore drill: proves that the newest backup of a database can really be restored, and how long that takes.

.DESCRIPTION
  1. Takes a full backup of -SourceDatabase (CHECKSUM, verified) into -WorkDirectory.
  2. Restores it into a NEW database named <source>_drill_<timestamp> (never over anything).
  3. Runs DBCC CHECKDB on the restored copy.
  4. Compares row counts of every table and the structure (tables, row-level-security policies, indexes, foreign keys) with the source.
  5. Drops the drill database again (only this script's own *_drill_* database; -Keep leaves it for inspection) and writes drill-report.json.
  Exit code 0 = passed, 1 = failed. Run it on a quiet source (or accept row-count differences caused by writes during the drill: they are listed, not hidden).
  The measured restore time is a lower bound of the real RTO (same machine, local disk, no network copy).

.EXAMPLE
  .\Test-RestoreDrill.ps1 -ServerInstance sql01 -SourceDatabase Raavi -WorkDirectory D:\drills
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerInstance,
    [Parameter(Mandatory)][string]$SourceDatabase,
    [string]$WorkDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'raavi-drill'),
    [string]$EncryptionCertificate,
    [switch]$Keep
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$null = Format-RaaviIdentifier $SourceDatabase
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')
$drill = "${SourceDatabase}_drill_$stamp"
$report = [ordered]@{ source = $SourceDatabase; drillDatabase = $drill; startedUtc = (Get-Date).ToUniversalTime().ToString('o'); passed = $false; problems = @() }
$problems = New-Object System.Collections.Generic.List[string]
$srcConn = $null
$master = $null
$created = $false
try {
    $backupArgs = @{ ServerInstance = $ServerInstance; Database = $SourceDatabase; BackupDirectory = $WorkDirectory; Mode = 'Full'; CopyOnly = $true }
    if ($EncryptionCertificate) { $backupArgs.EncryptionCertificate = $EncryptionCertificate }
    $backup = & (Join-Path $PSScriptRoot 'Backup-Raavi.ps1') @backupArgs
    $report.backupSeconds = $backup.seconds
    $report.backupBytes = $backup.sizeBytes
    $report.backupVerified = $backup.verified

    $srcConn = New-RaaviSqlConnection -ServerInstance $ServerInstance -Database $SourceDatabase
    $before = Get-RaaviTableCounts $srcConn
    $structure = Get-RaaviStructureSummary $srcConn

    $created = $true
    $restore = & (Join-Path $PSScriptRoot 'Restore-Raavi.ps1') -ServerInstance $ServerInstance -TargetDatabase $drill -Backups (Join-Path $WorkDirectory "$($backup.file).json")
    $report.restoreSeconds = $restore.seconds

    $master = New-RaaviSqlConnection -ServerInstance $ServerInstance
    $check = Get-Date
    $dbcc = Invoke-RaaviSql $master "DBCC CHECKDB ($(Format-RaaviIdentifier $drill)) WITH NO_INFOMSGS, ALL_ERRORMSGS"
    $report.checkdbSeconds = [math]::Round(((Get-Date) - $check).TotalSeconds, 1)
    if ($dbcc.Count -gt 0) { $problems.Add("DBCC CHECKDB reported $($dbcc.Count) message(s): $(($dbcc | Select-Object -First 3 | ForEach-Object { $_.MessageText }) -join ' | ')") }

    $drillConn = New-RaaviSqlConnection -ServerInstance $ServerInstance -Database $drill
    try {
        $after = Get-RaaviTableCounts $drillConn
        $structureAfter = Get-RaaviStructureSummary $drillConn
    } finally { $drillConn.Dispose() }

    foreach ($t in $before.Keys) {
        if (-not $after.ContainsKey($t)) { $problems.Add("table missing after restore: $t"); continue }
        if ($after[$t] -ne $before[$t]) { $problems.Add("row count differs in ${t}: source $($before[$t]), restored $($after[$t])") }
    }
    foreach ($t in $after.Keys) { if (-not $before.ContainsKey($t)) { $problems.Add("unexpected table in the restored copy: $t") } }
    foreach ($p in 'tables', 'securityPolicies', 'indexes', 'foreignKeys') {
        if ($structure.$p -ne $structureAfter.$p) { $problems.Add("$p differs: source $($structure.$p), restored $($structureAfter.$p)") }
    }
    $report.tables = $before.Count
    $report.rows = ($before.Values | Measure-Object -Sum).Sum
    $report.securityPolicies = $structure.securityPolicies
} catch {
    $problems.Add("drill aborted: $($_.Exception.Message)")
} finally {
    if ($srcConn) { $srcConn.Dispose() }
    if ($created -and -not $Keep) {
        try {
            if (-not $master) { $master = New-RaaviSqlConnection -ServerInstance $ServerInstance }
            # Only this run's own drill database is ever dropped.
            if ($drill -match "^$([regex]::Escape($SourceDatabase))_drill_\d{14}$" -and (Get-RaaviDatabaseInfo $master $drill)) {
                [void](Invoke-RaaviSql $master "ALTER DATABASE $(Format-RaaviIdentifier $drill) SET SINGLE_USER WITH ROLLBACK IMMEDIATE")
                [void](Invoke-RaaviSql $master "DROP DATABASE $(Format-RaaviIdentifier $drill)")
            }
        } catch { $problems.Add("cleanup of ${drill} failed: $($_.Exception.Message)") }
    }
    if ($master) { $master.Dispose() }
}

$report.problems = @($problems)
$report.passed = ($problems.Count -eq 0)
$report.finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
$reportPath = Join-Path $WorkDirectory "drill-report-$stamp.json"
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host ($(if ($report.passed) { 'RESTORE DRILL PASSED' } else { 'RESTORE DRILL FAILED' }) + " - report: $reportPath")
$problems | ForEach-Object { Write-Host "  - $_" }
if (-not $report.passed) { exit 1 }
exit 0
