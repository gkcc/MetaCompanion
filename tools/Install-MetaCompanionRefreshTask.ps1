param(
	[string]$TaskName = "Meta Companion Remote Cache Refresh",
	[string]$At = "08:05",
	[int]$LogonDelayMinutes = 5,
	[string]$DataDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion",
	[switch]$IncludeBranches,
	[switch]$SkipBranches,
	[switch]$DisableLogonCatchUp,
	[switch]$KeepLegacyTasks,
	[string[]]$LegacyTaskNames = @("Meta Companion Daily Refresh")
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$refreshScript = Join-Path $PSScriptRoot "Run-MetaCompanionRefresh.ps1"
if (-not (Test-Path $refreshScript)) {
	throw "Refresh script not found: $refreshScript"
}

$time = [DateTime]::Parse($At)
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$refreshScript`" -DataDirectory `"$DataDirectory`""
if ($IncludeBranches) {
	$arguments += " -IncludeBranches"
}
if ($SkipBranches) {
	$arguments += " -SkipBranches"
}

$action = New-ScheduledTaskAction `
	-Execute "powershell.exe" `
	-Argument $arguments `
	-WorkingDirectory $repoRoot
$dailyTrigger = New-ScheduledTaskTrigger -Daily -At $time
$triggers = @($dailyTrigger)
if (-not $DisableLogonCatchUp) {
	$logonTrigger = New-ScheduledTaskTrigger -AtLogOn
	$delayMinutes = [Math]::Max(0, $LogonDelayMinutes)
	if ($delayMinutes -gt 0) {
		$logonTrigger.Delay = "PT${delayMinutes}M"
	}
	$triggers += $logonTrigger
}
$settings = New-ScheduledTaskSettingsSet `
	-AllowStartIfOnBatteries `
	-DontStopIfGoingOnBatteries `
	-StartWhenAvailable

Register-ScheduledTask `
	-TaskName $TaskName `
	-Action $action `
	-Trigger $triggers `
	-Settings $settings `
	-Description "Daily external refresh for Meta Companion HSReplay remote cache and personal recommendations." `
	-Force | Out-Null

if (-not $KeepLegacyTasks) {
	foreach ($legacyTaskName in $LegacyTaskNames) {
		if ($legacyTaskName -and $legacyTaskName -ne $TaskName -and
			(Get-ScheduledTask -TaskName $legacyTaskName -ErrorAction SilentlyContinue)) {
			Unregister-ScheduledTask -TaskName $legacyTaskName -Confirm:$false
			Write-Host "Removed legacy scheduled task: $legacyTaskName"
		}
	}
}

Write-Host "Installed scheduled task: $TaskName"
Write-Host "Daily time: $($time.ToString('HH:mm'))"
if ($DisableLogonCatchUp) {
	Write-Host "Logon catch-up: disabled"
} else {
	Write-Host "Logon catch-up: enabled after $([Math]::Max(0, $LogonDelayMinutes)) minute(s)"
}
Write-Host "Script: $refreshScript"
Write-Host "Data directory: $DataDirectory"
