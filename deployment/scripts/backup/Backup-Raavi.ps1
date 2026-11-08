<#
.SYNOPSIS
  Backs up the Raavi SQL Server database (full, differential or transaction log) and writes a manifest next to the backup file.

.DESCRIPTION
  * CHECKSUM is always on, and the file is verified with RESTORE VERIFYONLY unless -SkipVerify is given.
  * -EncryptionCertificate adds AES-256 backup encryption with that server certificate (not available on Express/LocalDB editions).
    The certificate and its private key must be stored separately from the backups: without them the backups cannot be restored.
  * -CopyOnly takes a backup that does not become the base of later differential backups and does not touch the log chain (the restore drill uses it, so a drill never disturbs the real backup schedule).
  * Log backups need the FULL (or BULK_LOGGED) recovery model and an earlier full backup; the script says so instead of failing obscurely.
  * Old backup sets of this database are deleted only with -RetentionDays and only files this script wrote (matching its file pattern and manifest).
  * The manifest (<file>.json) records database, mode, times, size, SHA-256, server version, LSNs. Keep it with the backup.

.EXAMPLE
  .\Backup-Raavi.ps1 -ServerInstance 'sql01' -Database Raavi -BackupDirectory 'D:\backups\raavi' -Mode Full -EncryptionCertificate RaaviBackupCert
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerInstance,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$BackupDirectory,
    [ValidateSet('Full', 'Differential', 'Log')][string]$Mode = 'Full',
    [string]$EncryptionCertificate,
    [switch]$Compression,
    [switch]$SkipVerify,
    [switch]$CopyOnly,
    [ValidateRange(1, 3650)][int]$RetentionDays
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$null = Format-RaaviIdentifier $Database
if (-not (Test-Path -LiteralPath $BackupDirectory)) { New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null }
$BackupDirectory = (Resolve-Path -LiteralPath $BackupDirectory).Path

$conn = New-RaaviSqlConnection -ServerInstance $ServerInstance
try {
    $info = Get-RaaviDatabaseInfo $conn $Database
    if (-not $info) { throw "Database '$Database' does not exist on '$ServerInstance'." }
    if ($info.state_desc -ne 'ONLINE') { throw "Database '$Database' is $($info.state_desc), not ONLINE." }
    if ($Mode -eq 'Log' -and $info.recovery_model_desc -eq 'SIMPLE') {
        throw "Log backups need the FULL recovery model; '$Database' uses SIMPLE. Switch it deliberately (ALTER DATABASE ... SET RECOVERY FULL) and take a full backup first."
    }

    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff')
    $ext = if ($Mode -eq 'Log') { 'trn' } elseif ($Mode -eq 'Differential') { 'dif' } else { 'bak' }
    $file = Join-Path $BackupDirectory "raavi-$Database-$Mode-$stamp.$ext"
    if (Test-Path -LiteralPath $file) { throw "Backup file already exists (refusing to overwrite a backup): $file" }
    $options = @('FORMAT', 'INIT', 'CHECKSUM', 'STATS = 10', "NAME = N'raavi-$Mode-$stamp'")
    if ($Mode -eq 'Differential') { $options += 'DIFFERENTIAL' }
    if ($CopyOnly) {
        if ($Mode -eq 'Differential') { throw '-CopyOnly does not apply to differential backups.' }
        $options += 'COPY_ONLY'
    }
    if ($Compression) { $options += 'COMPRESSION' }
    if ($EncryptionCertificate) { $options += "ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = $(Format-RaaviIdentifier $EncryptionCertificate))" }
    $statement = if ($Mode -eq 'Log') { 'BACKUP LOG' } else { 'BACKUP DATABASE' }
    $sql = "$statement $(Format-RaaviIdentifier $Database) TO DISK = $(Format-RaaviLiteral $file) WITH $($options -join ', ')"

    $started = Get-Date
    Write-Host "[$($started.ToString('s'))] $Mode backup of $Database -> $file"
    [void](Invoke-RaaviSql $conn $sql)
    $finished = Get-Date

    $verified = $false
    if (-not $SkipVerify) {
        [void](Invoke-RaaviSql $conn "RESTORE VERIFYONLY FROM DISK = $(Format-RaaviLiteral $file) WITH CHECKSUM")
        $verified = $true
    }

    $lsn = (Invoke-RaaviSql $conn @'
SELECT TOP 1 first_lsn, last_lsn, database_backup_lsn, checkpoint_lsn, backup_size, is_encrypted = CASE WHEN key_algorithm IS NULL THEN 0 ELSE 1 END, compressed_backup_size
FROM msdb.dbo.backupset WHERE database_name = @db ORDER BY backup_finish_date DESC
'@ @{ db = $Database })
    $version = (Invoke-RaaviSql $conn "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) AS v, CAST(SERVERPROPERTY('Edition') AS nvarchar(100)) AS e")[0]

    $manifest = [ordered]@{
        formatVersion = 1
        database      = $Database
        server        = $ServerInstance
        mode          = $Mode
        file          = [System.IO.Path]::GetFileName($file)
        startedUtc    = $started.ToUniversalTime().ToString('o')
        finishedUtc   = $finished.ToUniversalTime().ToString('o')
        seconds       = [math]::Round(($finished - $started).TotalSeconds, 1)
        sizeBytes     = (Get-Item -LiteralPath $file).Length
        sha256        = Get-RaaviFileHash $file
        verified      = $verified
        encrypted     = [bool]$EncryptionCertificate
        compressed    = [bool]$Compression
        copyOnly      = [bool]$CopyOnly
        recoveryModel = $info.recovery_model_desc
        compatibility = $info.compatibility_level
        sqlVersion    = $version.v
        sqlEdition    = $version.e
        firstLsn      = if ($lsn.Count -gt 0) { [string]$lsn[0].first_lsn } else { $null }
        lastLsn       = if ($lsn.Count -gt 0) { [string]$lsn[0].last_lsn } else { $null }
        databaseBackupLsn = if ($lsn.Count -gt 0) { [string]$lsn[0].database_backup_lsn } else { $null }
        writtenBy     = 'Backup-Raavi.ps1'
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$file.json" -Encoding UTF8
    Write-Host ("Done in {0}s, {1:N1} MB, SHA-256 {2}" -f $manifest.seconds, ($manifest.sizeBytes / 1MB), $manifest.sha256)

    if ($RetentionDays) {
        $cutoff = (Get-Date).AddDays(-$RetentionDays)
        Get-ChildItem -LiteralPath $BackupDirectory -Filter "raavi-$Database-*.json" | Where-Object { $_.LastWriteTime -lt $cutoff } | ForEach-Object {
            $m = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
            $target = Join-Path $BackupDirectory $m.file
            if ($m.writtenBy -eq 'Backup-Raavi.ps1' -and $m.database -eq $Database -and (Test-Path -LiteralPath $target)) {
                Write-Host "Retention: removing $($m.file)"
                Remove-Item -LiteralPath $target, $_.FullName -Force
            }
        }
    }

    [pscustomobject]$manifest
} finally { $conn.Dispose() }
