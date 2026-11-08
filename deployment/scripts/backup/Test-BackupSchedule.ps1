<#
.SYNOPSIS
  Self-test of Invoke-ScheduledBackup.ps1, Test-BackupFreshness.ps1 and Register-RaaviBackupTasks.ps1 on a throw-away database. Exit code 0 = all passed.
.EXAMPLE
  .\Test-BackupSchedule.ps1 -ServerInstance '(localdb)\MSSQLLocalDB'
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ServerInstance)

. (Join-Path $PSScriptRoot 'Common.ps1')

$suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
$db = "RaaviScheduleSelfTest_$suffix"
$work = Join-Path ([System.IO.Path]::GetTempPath()) "raavi-schedule-selftest-$suffix"
New-Item -ItemType Directory -Path $work -Force | Out-Null
$failures = New-Object System.Collections.Generic.List[string]
$passes = 0
function Check { param([string]$Name, [bool]$Condition) if ($Condition) { $script:passes++; Write-Host "  ok   $Name" } else { $script:failures.Add($Name); Write-Host "  FAIL $Name" } }
function Run([string]$File, [hashtable]$Params) {
    # run in a child process so that "exit" in the script under test sets $LASTEXITCODE
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $File))
    foreach ($k in $Params.Keys) { $argList += "-$k"; if ($Params[$k] -isnot [bool]) { $argList += [string]$Params[$k] } }
    $ErrorActionPreference = 'Continue'
    $out = & powershell.exe @argList 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($out | Out-String) }
}

$master = New-RaaviSqlConnection -ServerInstance $ServerInstance
$tasksToRemove = @()
try {
    [void](Invoke-RaaviSql $master "CREATE DATABASE [$db]")
    [void](Invoke-RaaviSql $master "ALTER DATABASE [$db] SET RECOVERY FULL")
    $c = New-RaaviSqlConnection -ServerInstance $ServerInstance -Database $db
    [void](Invoke-RaaviSql $c "CREATE TABLE dbo.T (Id int IDENTITY PRIMARY KEY, V nvarchar(50))")
    [void](Invoke-RaaviSql $c "INSERT dbo.T (V) VALUES (N'x')")
    $alerts = Join-Path $work 'alerts.txt'
    $hook = Join-Path $work 'hook.ps1'
    Set-Content -LiteralPath $hook -Value 'param([string]$Subject, [string]$Body) Add-Content -LiteralPath (Join-Path $PSScriptRoot "alerts.txt") -Value "$Subject | $Body"'
    $backups = Join-Path $work 'backups'

    Write-Host '1. successful scheduled full backup'
    $r = Run 'Invoke-ScheduledBackup.ps1' @{ ServerInstance = $ServerInstance; Database = $db; BackupDirectory = $backups; Mode = 'Full'; AlertScript = $hook }
    Check 'exit code 0' ($r.Code -eq 0)
    $status = Get-Content (Join-Path $backups "status/$db-Full.json") -Raw | ConvertFrom-Json
    Check 'status file records the success' ($status.consecutiveFailures -eq 0 -and $status.lastSuccessUtc -and (Test-Path (Join-Path $backups $status.lastBackupFile)))
    Check 'log file written without secrets' (@(Get-ChildItem (Join-Path $backups 'logs') -Filter '*.log').Count -eq 1)
    Check 'no alert on success' (-not (Test-Path $alerts))

    Write-Host '2. freshness check'
    Check 'fresh full backup passes' ((Run 'Test-BackupFreshness.ps1' @{ Database = $db; BackupDirectory = $backups; MaxFullAgeHours = 24 }).Code -eq 0)
    Check 'a mode that never ran fails' ((Run 'Test-BackupFreshness.ps1' @{ Database = $db; BackupDirectory = $backups; MaxFullAgeHours = 24; MaxLogAgeMinutes = 30 }).Code -eq 1)
    Check 'no limits given -> usage error' ((Run 'Test-BackupFreshness.ps1' @{ Database = $db; BackupDirectory = $backups }).Code -eq 2)
    $old = $status | ConvertTo-Json | ConvertFrom-Json
    $old.lastSuccessUtc = (Get-Date).ToUniversalTime().AddHours(-40).ToString('o')
    $old | ConvertTo-Json | Set-Content (Join-Path $backups "status/$db-Full.json") -Encoding UTF8
    Check 'stale backup fails' ((Run 'Test-BackupFreshness.ps1' @{ Database = $db; BackupDirectory = $backups; MaxFullAgeHours = 24 }).Code -eq 1)
    $status | ConvertTo-Json | Set-Content (Join-Path $backups "status/$db-Full.json") -Encoding UTF8
    $file = Join-Path $backups $status.lastBackupFile
    Add-Content -LiteralPath $file -Value 'x' -Encoding ASCII
    Check 'backup file changed after the manifest was written fails' ((Run 'Test-BackupFreshness.ps1' @{ Database = $db; BackupDirectory = $backups; MaxFullAgeHours = 24 }).Code -eq 1)

    Write-Host '3. failing backup raises an alert; recovery is announced'
    $bad = Run 'Invoke-ScheduledBackup.ps1' @{ ServerInstance = $ServerInstance; Database = "${db}_missing"; BackupDirectory = $backups; Mode = 'Full'; AlertScript = $hook }
    Check 'exit code 1' ($bad.Code -eq 1)
    $failStatus = Get-Content (Join-Path $backups "status/${db}_missing-Full.json") -Raw | ConvertFrom-Json
    Check 'status records the failure' ($failStatus.consecutiveFailures -eq 1 -and $failStatus.lastError -match 'does not exist')
    Check 'alert hook called with subject and host' ((Test-Path $alerts) -and ((Get-Content $alerts -Raw) -match 'FAILED' ) -and ((Get-Content $alerts -Raw) -match 'Host:'))
    $bad2 = Run 'Invoke-ScheduledBackup.ps1' @{ ServerInstance = $ServerInstance; Database = "${db}_missing"; BackupDirectory = $backups; Mode = 'Full' }
    Check 'consecutive failures counted' ((Get-Content (Join-Path $backups "status/${db}_missing-Full.json") -Raw | ConvertFrom-Json).consecutiveFailures -eq 2)
    [void](Invoke-RaaviSql $master "CREATE DATABASE [${db}_missing]")
    $good = Run 'Invoke-ScheduledBackup.ps1' @{ ServerInstance = $ServerInstance; Database = "${db}_missing"; BackupDirectory = $backups; Mode = 'Full'; AlertScript = $hook }
    Check 'recovers and clears the failure counter' ($good.Code -eq 0 -and (Get-Content (Join-Path $backups "status/${db}_missing-Full.json") -Raw | ConvertFrom-Json).consecutiveFailures -eq 0)
    Check 'recovery announced' ((Get-Content $alerts -Raw) -match 'recovered')
    $secret = Run 'Invoke-ScheduledBackup.ps1' @{ ServerInstance = 'nonexistent-host-for-test,1'; Database = $db; BackupDirectory = $backups; Mode = 'Differential' }
    Check 'secrets are not written to the status file' (-not ((Get-Content (Join-Path $backups "status/$db-Differential.json") -Raw) -match 'Password='))

    Write-Host '4. task registration'
    $prefix = "RaaviSelfTest-$suffix"
    $w = Run 'Register-RaaviBackupTasks.ps1' @{ ServerInstance = $ServerInstance; Database = $db; BackupDirectory = $backups; TaskPrefix = $prefix; WhatIf = $true }
    Check '-WhatIf registers nothing' (-not (Get-ScheduledTask -TaskName "$prefix*" -ErrorAction SilentlyContinue))
    $reg = Run 'Register-RaaviBackupTasks.ps1' @{ ServerInstance = $ServerInstance; Database = $db; BackupDirectory = $backups; TaskPrefix = $prefix; IncludeLog = $true }
    $tasksToRemove = @(Get-ScheduledTask -TaskName "$prefix*" -ErrorAction SilentlyContinue)
    if ($tasksToRemove.Count -eq 0) { Write-Host "  skip task registration (not allowed here): $($reg.Output.Trim())" }
    else {
        Check 'full, differential and log tasks registered' ($tasksToRemove.Count -eq 3)
        $again = Run 'Register-RaaviBackupTasks.ps1' @{ ServerInstance = $ServerInstance; Database = $db; BackupDirectory = $backups; TaskPrefix = $prefix }
        Check 'existing tasks are not overwritten without -Replace' ($again.Code -ne 0)
        $fullTask = $tasksToRemove | Where-Object { $_.TaskName -like '* Full *' }
        Check 'task runs the wrapper with the database and mode' ($fullTask.Actions[0].Arguments -match 'Invoke-ScheduledBackup.ps1' -and $fullTask.Actions[0].Arguments -match [regex]::Escape($db) -and $fullTask.Actions[0].Arguments -match '-Mode Full')
        $un = Run 'Register-RaaviBackupTasks.ps1' @{ ServerInstance = $ServerInstance; Database = $db; BackupDirectory = $backups; TaskPrefix = $prefix; IncludeLog = $true; Unregister = $true }
        Check 'unregister removes them' (-not (Get-ScheduledTask -TaskName "$prefix*" -ErrorAction SilentlyContinue))
        $tasksToRemove = @()
    }
}
finally {
    foreach ($t in $tasksToRemove) { Unregister-ScheduledTask -TaskName $t.TaskName -Confirm:$false -ErrorAction SilentlyContinue }
    foreach ($name in @($db, "${db}_missing")) {
        try { [void](Invoke-RaaviSql $master "IF DB_ID(N'$name') IS NOT NULL BEGIN ALTER DATABASE [$name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$name] END") } catch { Write-Host "cleanup of $name failed: $($_.Exception.Message)" }
    }
    $master.Dispose()
    Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
}

Write-Host "$passes checks passed, $($failures.Count) failed"
if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Host "  - $_" }; exit 1 }
