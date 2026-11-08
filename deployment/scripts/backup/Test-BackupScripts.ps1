<#
.SYNOPSIS
  Self-test of the backup / restore scripts on a throw-away database it creates and drops itself (never touches other databases).

.DESCRIPTION
  Checks: full backup + manifest + hash; restore drill; restore chain full -> differential -> log with point-in-time (STOPAT);
  refusal to overwrite an existing database; -Replace needing the exact name; damaged backup detected via the manifest hash;
  log backup refused on a SIMPLE recovery model; retention removes only old files written by the script.
  Exit code 0 = all passed.

.EXAMPLE
  .\Test-BackupScripts.ps1 -ServerInstance '(localdb)\MSSQLLocalDB'
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ServerInstance)

. (Join-Path $PSScriptRoot 'Common.ps1')

$suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
$db = "RaaviBackupSelfTest_$suffix"
$work = Join-Path ([System.IO.Path]::GetTempPath()) "raavi-backup-selftest-$suffix"
New-Item -ItemType Directory -Path $work -Force | Out-Null
$failures = New-Object System.Collections.Generic.List[string]
$passes = 0
function Check { param([string]$Name, [bool]$Condition) if ($Condition) { $script:passes++; Write-Host "  ok   $Name" } else { $script:failures.Add($Name); Write-Host "  FAIL $Name" } }
function Expect-Throw { param([string]$Name, [scriptblock]$Action, [string]$Pattern) try { & $Action | Out-Null; Check $Name $false } catch { Check $Name ($_.Exception.Message -match $Pattern) } }

$master = New-RaaviSqlConnection -ServerInstance $ServerInstance
$created = @($db)
try {
    [void](Invoke-RaaviSql $master "CREATE DATABASE [$db]")
    [void](Invoke-RaaviSql $master "ALTER DATABASE [$db] SET RECOVERY FULL")
    $c = New-RaaviSqlConnection -ServerInstance $ServerInstance -Database $db
    [void](Invoke-RaaviSql $c "CREATE SCHEMA security")
    [void](Invoke-RaaviSql $c "CREATE TABLE dbo.Items (Id int IDENTITY PRIMARY KEY, TenantId uniqueidentifier NOT NULL, Name nvarchar(50) NOT NULL)")
    [void](Invoke-RaaviSql $c "CREATE TABLE dbo.Notes (Id int IDENTITY PRIMARY KEY, ItemId int NOT NULL REFERENCES dbo.Items(Id), Body nvarchar(max))")
    [void](Invoke-RaaviSql $c "CREATE FUNCTION security.Pred(@t uniqueidentifier) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS ok WHERE @t = @t")
    [void](Invoke-RaaviSql $c "CREATE SECURITY POLICY security.Isolation ADD FILTER PREDICATE security.Pred(TenantId) ON dbo.Items WITH (STATE = ON)")
    [void](Invoke-RaaviSql $c "INSERT dbo.Items (TenantId, Name) SELECT NEWID(), CONCAT(N'item-', n) FROM (SELECT TOP 200 ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS n FROM sys.objects a CROSS JOIN sys.objects b) x")
    [void](Invoke-RaaviSql $c "INSERT dbo.Notes (ItemId, Body) SELECT Id, REPLICATE(N'persian: سلام ', 20) FROM dbo.Items")

    Write-Host '1. full backup, manifest and drill'
    $full = & (Join-Path $PSScriptRoot 'Backup-Raavi.ps1') -ServerInstance $ServerInstance -Database $db -BackupDirectory $work -Mode Full
    Check 'manifest written next to the backup' (Test-Path -LiteralPath (Join-Path $work "$($full.file).json"))
    Check 'backup verified and hash recorded' ($full.verified -and $full.sha256.Length -eq 64)
    $global:LASTEXITCODE = $null
    & (Join-Path $PSScriptRoot 'Test-RestoreDrill.ps1') -ServerInstance $ServerInstance -SourceDatabase $db -WorkDirectory $work | Out-Null
    Check 'restore drill passes (counts, CHECKDB, row-level-security policy)' ($LASTEXITCODE -eq 0)
    $report = Get-ChildItem -LiteralPath $work -Filter 'drill-report-*.json' | Select-Object -First 1 | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json }
    Check 'drill report records tables, rows and the policy' ($report.tables -eq 2 -and $report.rows -eq 400 -and $report.securityPolicies -eq 1)
    Check 'drill database is gone afterwards' ((Invoke-RaaviSql $master "SELECT name FROM sys.databases WHERE name LIKE '${db}_drill_%'").Count -eq 0)

    Write-Host '2. chain: full -> more data -> differential -> more data -> log, point in time'
    [void](Invoke-RaaviSql $c "INSERT dbo.Items (TenantId, Name) VALUES (NEWID(), N'after-full')")
    $diff = & (Join-Path $PSScriptRoot 'Backup-Raavi.ps1') -ServerInstance $ServerInstance -Database $db -BackupDirectory $work -Mode Differential
    [void](Invoke-RaaviSql $c "INSERT dbo.Items (TenantId, Name) VALUES (NEWID(), N'after-diff-1')")
    Start-Sleep -Seconds 2
    $cut = (Get-Date).ToUniversalTime()
    Start-Sleep -Seconds 2
    [void](Invoke-RaaviSql $c "INSERT dbo.Items (TenantId, Name) VALUES (NEWID(), N'after-cut')")
    $log = & (Join-Path $PSScriptRoot 'Backup-Raavi.ps1') -ServerInstance $ServerInstance -Database $db -BackupDirectory $work -Mode Log
    $manifests = @($full, $diff, $log) | ForEach-Object { Join-Path $work "$($_.file).json" }

    $all = "${db}_all"; $created += $all
    & (Join-Path $PSScriptRoot 'Restore-Raavi.ps1') -ServerInstance $ServerInstance -TargetDatabase $all -Backups $manifests | Out-Null
    $conn = New-RaaviSqlConnection -ServerInstance $ServerInstance -Database $all
    Check 'full chain restores everything (200 + 3 rows)' ((Invoke-RaaviSql $conn 'SELECT COUNT(*) AS n FROM dbo.Items')[0].n -eq 203)
    $conn.Dispose()

    $pit = "${db}_pit"; $created += $pit
    & (Join-Path $PSScriptRoot 'Restore-Raavi.ps1') -ServerInstance $ServerInstance -TargetDatabase $pit -Backups $manifests -StopAt $cut | Out-Null
    $conn = New-RaaviSqlConnection -ServerInstance $ServerInstance -Database $pit
    $names = (Invoke-RaaviSql $conn "SELECT Name FROM dbo.Items WHERE Name LIKE N'after-%'") | ForEach-Object { $_.Name }
    $conn.Dispose()
    Check 'point-in-time restore stops before the later row' (($names -contains 'after-diff-1') -and -not ($names -contains 'after-cut'))

    Write-Host '3. guards'
    Expect-Throw 'refuses to restore over an existing database' { & (Join-Path $PSScriptRoot 'Restore-Raavi.ps1') -ServerInstance $ServerInstance -TargetDatabase $all -Backups $manifests[0] } 'already exists'
    Expect-Throw '-Replace needs the exact name' { & (Join-Path $PSScriptRoot 'Restore-Raavi.ps1') -ServerInstance $ServerInstance -TargetDatabase $all -Backups $manifests[0] -Replace -ConfirmOverwrite 'wrong' } 'Repeat its exact name'
    & (Join-Path $PSScriptRoot 'Restore-Raavi.ps1') -ServerInstance $ServerInstance -TargetDatabase $all -Backups $manifests[0] -Replace -ConfirmOverwrite $all | Out-Null
    $conn = New-RaaviSqlConnection -ServerInstance $ServerInstance -Database $all
    Check '-Replace with the confirmed name restores the older state (200 rows)' ((Invoke-RaaviSql $conn 'SELECT COUNT(*) AS n FROM dbo.Items')[0].n -eq 200)
    $conn.Dispose()
    Expect-Throw 'a chain must start with a full backup' { & (Join-Path $PSScriptRoot 'Restore-Raavi.ps1') -ServerInstance $ServerInstance -TargetDatabase "${db}_x" -Backups $manifests[1] } 'first backup must be a full'

    $copy = Join-Path $work 'damaged'; New-Item -ItemType Directory -Path $copy -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $work $full.file) -Destination $copy
    Copy-Item -LiteralPath $manifests[0] -Destination $copy
    $fs = [System.IO.File]::Open((Join-Path $copy $full.file), 'Open', 'ReadWrite'); $fs.Seek(4096, 'Begin') | Out-Null; $fs.WriteByte(0xFF); $fs.Dispose()
    Expect-Throw 'a damaged backup file is detected from the manifest hash' { & (Join-Path $PSScriptRoot 'Restore-Raavi.ps1') -ServerInstance $ServerInstance -TargetDatabase "${db}_y" -Backups (Join-Path $copy "$($full.file).json") } 'does not match its manifest'

    $simple = "${db}_simple"; $created += $simple
    [void](Invoke-RaaviSql $master "CREATE DATABASE [$simple]")
    Expect-Throw 'a log backup of a SIMPLE-recovery database is refused with an explanation' { & (Join-Path $PSScriptRoot 'Backup-Raavi.ps1') -ServerInstance $ServerInstance -Database $simple -BackupDirectory $work -Mode Log } 'FULL recovery model'
    Expect-Throw 'database names are validated' { & (Join-Path $PSScriptRoot 'Backup-Raavi.ps1') -ServerInstance $ServerInstance -Database 'x]; DROP DATABASE master;--' -BackupDirectory $work } 'Not a valid database'

    Write-Host '4. retention'
    $old = Get-Item -LiteralPath $manifests[0]
    $old.LastWriteTime = (Get-Date).AddDays(-40)
    $stranger = Join-Path $work 'raavi-keepme.json'; '{"writtenBy":"someone else"}' | Set-Content -LiteralPath $stranger; (Get-Item $stranger).LastWriteTime = (Get-Date).AddDays(-400)
    & (Join-Path $PSScriptRoot 'Backup-Raavi.ps1') -ServerInstance $ServerInstance -Database $db -BackupDirectory $work -Mode Full -RetentionDays 30 | Out-Null
    Check 'retention removes the old backup set of this script' (-not (Test-Path -LiteralPath (Join-Path $work $full.file)))
    Check 'retention keeps unrelated files' (Test-Path -LiteralPath $stranger)
} catch {
    $failures.Add("self-test aborted: $($_.Exception.Message)")
    Write-Host $_.ScriptStackTrace
} finally {
    foreach ($name in $created | Select-Object -Unique) {
        try {
            if ($name -like 'RaaviBackupSelfTest_*' -and (Get-RaaviDatabaseInfo $master $name)) {
                try { [void](Invoke-RaaviSql $master "ALTER DATABASE [$name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE") } catch { } # not possible while a database is still in the RESTORING state
                [void](Invoke-RaaviSql $master "DROP DATABASE [$name]")
            }
        } catch { Write-Host "cleanup of $name failed: $($_.Exception.Message)" }
    }
    if ($c) { $c.Dispose() }
    $master.Dispose()
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ("`n{0} checks passed, {1} failed" -f $passes, $failures.Count)
$failures | ForEach-Object { Write-Host "  FAILED: $_" }
if ($failures.Count -gt 0) { exit 1 }
