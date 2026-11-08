<#
.SYNOPSIS
  Restores Raavi backups (full, optionally one differential and log backups, in that order) into a database of the given name.

.DESCRIPTION
  Safe by default:
  * The file hashes are compared with the manifests BEFORE anything is restored.
  * An existing database is never touched unless -Replace is given AND -ConfirmOverwrite repeats its exact name.
  * Restoring into a new name (a "side database") is the normal way to recover one tenant or to run a drill; point the tenant export at it.
  * -StopAt restores log backups up to a point in time. Give it as UTC (or a [datetimeoffset]); RESTORE LOG compares it with the SERVER'S LOCAL time, so the script converts it using the server's current UTC offset (around a daylight-saving change, check the result).
  Data and log files are placed in -DataDirectory / -LogDirectory (default: the instance defaults) as <target>_<logical name>.<ext>.

.EXAMPLE
  .\Restore-Raavi.ps1 -ServerInstance sql02 -TargetDatabase Raavi_side -Backups D:\b\raavi-Raavi-Full-20261108-020000.bak.json, D:\b\raavi-Raavi-Differential-20261108-080000.dif.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerInstance,
    [Parameter(Mandatory)][string]$TargetDatabase,
    [Parameter(Mandatory)][string[]]$Backups,
    [string]$DataDirectory,
    [string]$LogDirectory,
    [switch]$Replace,
    [string]$ConfirmOverwrite,
    [datetimeoffset]$StopAt
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$null = Format-RaaviIdentifier $TargetDatabase
$sets = foreach ($path in $Backups) {
    $manifestPath = (Resolve-Path -LiteralPath $path).Path
    if ($manifestPath -notlike '*.json') { throw "Pass the manifest (.json) of each backup, not the backup file: $path" }
    $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $file = Join-Path (Split-Path -Parent $manifestPath) $m.file
    if (-not (Test-Path -LiteralPath $file)) { throw "Backup file missing: $file" }
    if ((Get-RaaviFileHash $file) -ne $m.sha256) { throw "Hash of $($m.file) does not match its manifest - the file is damaged or was changed." }
    [pscustomobject]@{ Manifest = $m; File = $file }
}
if (@($sets).Count -eq 0) { throw 'No backups given.' }
if ($sets[0].Manifest.mode -ne 'Full') { throw 'The first backup must be a full backup.' }
for ($i = 1; $i -lt @($sets).Count; $i++) {
    if ($sets[$i].Manifest.mode -eq 'Full') { throw 'Only the first backup may be a full backup.' }
    if ($sets[$i].Manifest.mode -eq 'Differential' -and $i -ne 1) { throw 'A differential backup must directly follow the full backup.' }
    if ($sets[$i].Manifest.mode -eq 'Log' -and $sets[1].Manifest.mode -eq 'Log' -and $i -gt 1 -and $sets[$i - 1].Manifest.mode -ne 'Log' -and $sets[$i - 1].Manifest.mode -ne 'Differential') { throw 'Log backups must follow the full/differential backup in order.' }
}

$conn = New-RaaviSqlConnection -ServerInstance $ServerInstance
try {
    $existing = Get-RaaviDatabaseInfo $conn $TargetDatabase
    if ($existing) {
        if (-not $Replace) { throw "Database '$TargetDatabase' already exists. Restore into a new name, or use -Replace with -ConfirmOverwrite '$TargetDatabase'." }
        if ($ConfirmOverwrite -cne $TargetDatabase) { throw "-Replace overwrites '$TargetDatabase'. Repeat its exact name in -ConfirmOverwrite to confirm." }
    }

    $paths = (Invoke-RaaviSql $conn "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260)) AS d, CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(260)) AS l")[0]
    $dataDir = if ($DataDirectory) { $DataDirectory } else { $paths.d }
    $logDir = if ($LogDirectory) { $LogDirectory } else { $paths.l }
    if (-not $dataDir -or -not $logDir) { throw 'The server reports no default data/log path; pass -DataDirectory and -LogDirectory.' }

    $first = $sets[0].File
    $files = Invoke-RaaviSql $conn "RESTORE FILELISTONLY FROM DISK = $(Format-RaaviLiteral $first)"
    $moves = foreach ($f in $files) {
        $ext = [System.IO.Path]::GetExtension($f.PhysicalName)
        $dir = if ($f.Type -eq 'L') { $logDir } else { $dataDir }
        $target = Join-Path $dir ("{0}_{1}{2}" -f $TargetDatabase, $f.LogicalName, $ext)
        "MOVE $(Format-RaaviLiteral $f.LogicalName) TO $(Format-RaaviLiteral $target)"
    }

    if ($existing) {
        [void](Invoke-RaaviSql $conn "ALTER DATABASE $(Format-RaaviIdentifier $TargetDatabase) SET SINGLE_USER WITH ROLLBACK IMMEDIATE")
    }

    $started = Get-Date
    $total = @($sets).Count
    for ($i = 0; $i -lt $total; $i++) {
        $s = $sets[$i]
        $last = $i -eq $total - 1
        $opts = @('CHECKSUM', 'STATS = 10', $(if ($last) { 'RECOVERY' } else { 'NORECOVERY' }))
        switch ($s.Manifest.mode) {
            'Full' {
                $opts += $moves
                if ($Replace) { $opts += 'REPLACE' }
                $sql = "RESTORE DATABASE $(Format-RaaviIdentifier $TargetDatabase) FROM DISK = $(Format-RaaviLiteral $s.File) WITH $($opts -join ', ')"
            }
            'Differential' { $sql = "RESTORE DATABASE $(Format-RaaviIdentifier $TargetDatabase) FROM DISK = $(Format-RaaviLiteral $s.File) WITH $($opts -join ', ')" }
            'Log' {
                if ($PSBoundParameters.ContainsKey('StopAt')) {
                    $offsetMinutes = [int](Invoke-RaaviSql $conn 'SELECT DATEPART(TZOFFSET, SYSDATETIMEOFFSET()) AS m')[0].m
                    $opts += "STOPAT = '$($StopAt.UtcDateTime.AddMinutes($offsetMinutes).ToString('yyyy-MM-ddTHH:mm:ss'))'"
                }
                $sql = "RESTORE LOG $(Format-RaaviIdentifier $TargetDatabase) FROM DISK = $(Format-RaaviLiteral $s.File) WITH $($opts -join ', ')"
            }
        }
        Write-Host "[$((Get-Date).ToString('s'))] restoring $($s.Manifest.mode): $($s.Manifest.file)"
        [void](Invoke-RaaviSql $conn $sql)
    }
    if ($existing) { [void](Invoke-RaaviSql $conn "ALTER DATABASE $(Format-RaaviIdentifier $TargetDatabase) SET MULTI_USER") }

    $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    Write-Host "Restore of $TargetDatabase finished in ${seconds}s."
    [pscustomobject]@{ database = $TargetDatabase; restoredBackups = $total; seconds = $seconds; stopAt = if ($PSBoundParameters.ContainsKey('StopAt')) { $StopAt.UtcDateTime.ToString('o') } else { $null } }
} finally { $conn.Dispose() }
