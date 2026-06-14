param(
	[string]$TaskName = "Meta Companion Remote Cache Refresh",
	[string]$At = "08:05",
	[string]$DataDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion",
	[switch]$IncludeBranches,
	[switch]$SkipBranches
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
$trigger = New-ScheduledTaskTrigger -Daily -At $time
$settings = New-ScheduledTaskSettingsSet `
	-AllowStartIfOnBatteries `
	-DontStopIfGoingOnBatteries `
	-StartWhenAvailable

Register-ScheduledTask `
	-TaskName $TaskName `
	-Action $action `
	-Trigger $trigger `
	-Settings $settings `
	-Description "Daily external refresh for Meta Companion HSReplay remote cache and personal recommendations." `
	-Force | Out-Null

Write-Host "Installed scheduled task: $TaskName"
Write-Host "Daily time: $($time.ToString('HH:mm'))"
Write-Host "Script: $refreshScript"
Write-Host "Data directory: $DataDirectory"
