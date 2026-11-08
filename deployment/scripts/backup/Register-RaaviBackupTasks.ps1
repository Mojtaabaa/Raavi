<#
.SYNOPSIS
  Creates (or removes) Windows Task Scheduler tasks that run Invoke-ScheduledBackup.ps1. (Phase 12f, ADR-042)
.DESCRIPTION
  Default schedule (change with the parameters): full backup daily at 02:00, differential backup every 6 hours, log backup every 15 minutes (only with -IncludeLog,
  which needs the FULL recovery model). Tasks are named "Raavi Backup <Mode> (<Database>)", never overwrite a task that exists (use -Replace), and support -WhatIf.
  The account that runs them needs: BACKUP DATABASE on the database, write access to the backup directory and (for the Event Log alert) nothing else.
  Default account is the current user, run whether or not they are logged on requires -Credential; "SYSTEM" works when the SQL login NT AUTHORITY\SYSTEM may back up.
  Alerts and retention are passed through to Invoke-ScheduledBackup.ps1; secrets (webhook tokens in URLs) are visible in the task definition - prefer -AlertScript.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$ServerInstance,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$BackupDirectory,
    [string]$FullAt = '02:00',
    [ValidateRange(1, 24)][int]$DifferentialEveryHours = 6,
    [ValidateRange(5, 1440)][int]$LogEveryMinutes = 15,
    [switch]$IncludeLog,
    [ValidateRange(1, 3650)][int]$RetentionDays = 14,
    [string]$AlertScript,
    [string]$AlertWebhookUrl,
    [string]$EncryptionCertificate,
    [string]$RunAsUser,
    [switch]$Replace,
    [switch]$Unregister,
    [string]$TaskPrefix = 'Raavi Backup'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$null = Format-RaaviIdentifier $Database
$script = Join-Path $PSScriptRoot 'Invoke-ScheduledBackup.ps1'
$modes = @('Full', 'Differential') + $(if ($IncludeLog) { 'Log' } else { @() })

foreach ($mode in $modes) {
    $name = "$TaskPrefix $mode ($Database)"
    $exists = [bool](Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)
    if ($Unregister) {
        if ($exists -and $PSCmdlet.ShouldProcess($name, 'unregister')) { Unregister-ScheduledTask -TaskName $name -Confirm:$false; Write-Host "removed $name" }
        continue
    }
    if ($exists -and -not $Replace) { throw "Task '$name' already exists (use -Replace to change it)." }

    $argList = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "`"$script`"", '-ServerInstance', "`"$ServerInstance`"", '-Database', $Database,
        '-BackupDirectory', "`"$BackupDirectory`"", '-Mode', $mode, '-Compression', '-RetentionDays', $RetentionDays)
    if ($AlertScript) { $argList += @('-AlertScript', "`"$AlertScript`"") }
    if ($AlertWebhookUrl) { $argList += @('-AlertWebhookUrl', "`"$AlertWebhookUrl`"") }
    if ($EncryptionCertificate) { $argList += @('-EncryptionCertificate', $EncryptionCertificate) }
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ($argList -join ' ')

    $trigger = switch ($mode) {
        'Full' { New-ScheduledTaskTrigger -Daily -At $FullAt }
        'Differential' { New-ScheduledTaskTrigger -Once -At ([DateTime]::Today.AddMinutes(30)) -RepetitionInterval (New-TimeSpan -Hours $DifferentialEveryHours) }
        'Log' { New-ScheduledTaskTrigger -Once -At ([DateTime]::Today.AddMinutes(5)) -RepetitionInterval (New-TimeSpan -Minutes $LogEveryMinutes) }
    }
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 6) -MultipleInstances IgnoreNew -StartWhenAvailable
    if ($PSCmdlet.ShouldProcess($name, 'register')) {
        $reg = @{ TaskName = $name; Action = $action; Trigger = $trigger; Settings = $settings; Force = [bool]$Replace }
        if ($RunAsUser) { $reg.User = $RunAsUser; $reg.RunLevel = 'Highest' }
        Register-ScheduledTask @reg | Out-Null
        Write-Host "registered $name"
    }
}
