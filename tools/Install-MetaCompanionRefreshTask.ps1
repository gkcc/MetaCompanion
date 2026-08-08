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
	throw "未找到刷新脚本：$refreshScript"
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

$limitedDailyOnly = $false
try {
	Register-ScheduledTask `
		-TaskName $TaskName `
		-Action $action `
		-Trigger $triggers `
		-Settings $settings `
		-Description "每天刷新 Meta Companion 的 HSReplay 远端缓存和个人推荐。" `
		-Force | Out-Null
} catch {
	$registrationError = [string]$_.Exception.Message
	if ($registrationError -notmatch "(?i)(access is denied|0x80070005|拒绝访问)") {
		throw
	}

	# Some Windows installations reject Register-ScheduledTask without elevation even
	# for the current user. A limited daily task preserves the primary refresh guarantee
	# without storing credentials or requesting administrator rights.
	$limitedArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$refreshScript`""
	$defaultDataDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion"
	if (-not [System.IO.Path]::GetFullPath($DataDirectory).Equals(
		[System.IO.Path]::GetFullPath($defaultDataDirectory),
		[StringComparison]::OrdinalIgnoreCase)) {
		$limitedArguments += " -DataDirectory `"$DataDirectory`""
	}
	if ($IncludeBranches) {
		$limitedArguments += " -IncludeBranches"
	}
	if ($SkipBranches) {
		$limitedArguments += " -SkipBranches"
	}
	$taskCommand = "powershell.exe $limitedArguments"
	if ($taskCommand.Length -gt 260) {
		throw "限权计划任务命令超过 Windows 长度限制。请缩短数据目录路径或使用管理员 PowerShell 注册完整任务。"
	}
	$taskArguments = @(
		"/Create",
		"/TN", $TaskName,
		"/TR", $taskCommand,
		"/SC", "DAILY",
		"/ST", $time.ToString("HH:mm"),
		"/RL", "LIMITED",
		"/F"
	)
	$taskOutput = @(& schtasks.exe @taskArguments 2>&1)
	if ($LASTEXITCODE -ne 0) {
		throw "当前账户无法注册每日刷新任务。请使用管理员 PowerShell 重试此脚本。"
	}
	$limitedDailyOnly = $true
}

if (-not $KeepLegacyTasks) {
	foreach ($legacyTaskName in $LegacyTaskNames) {
		if ($legacyTaskName -and $legacyTaskName -ne $TaskName -and
			(Get-ScheduledTask -TaskName $legacyTaskName -ErrorAction SilentlyContinue)) {
			Unregister-ScheduledTask -TaskName $legacyTaskName -Confirm:$false
			Write-Host "已删除旧计划任务：$legacyTaskName"
		}
	}
}

Write-Host "计划任务已安装：$TaskName"
Write-Host "每日执行时间：$($time.ToString('HH:mm'))"
if ($limitedDailyOnly) {
	Write-Host "注册方式：当前用户限权任务（无需管理员权限）"
	Write-Host "登录后补查：系统权限不足，未注册；每日刷新仍会正常执行"
} elseif ($DisableLogonCatchUp) {
	Write-Host "登录后补查：已禁用"
} else {
	Write-Host "登录后补查：登录 $([Math]::Max(0, $LogonDelayMinutes)) 分钟后执行"
}
Write-Host "脚本：$refreshScript"
Write-Host "数据目录：$DataDirectory"
