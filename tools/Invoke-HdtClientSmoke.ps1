param(
	[string]$BuildPath = "$PSScriptRoot\..\MetaCompanion\bin\x86\Release\MetaCompanion.dll",
	[string]$ArtifactsDirectory = "$PSScriptRoot\..\artifacts\client-smoke",
	[switch]$LaunchHearthstone,
	[switch]$IncludeTools,
	[switch]$NonInteractive,
	[switch]$RequireManualPass,
	[switch]$SelfTest
)

$ErrorActionPreference = "Stop"

function Get-SmokeHdtExecutablePath {
	$running = Get-Process HearthstoneDeckTracker -ErrorAction SilentlyContinue | Select-Object -First 1
	if ($running -and $running.Path) {
		return $running.Path
	}
	$hdtRoot = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
	if (Test-Path -LiteralPath $hdtRoot) {
		$latest = Get-ChildItem -LiteralPath $hdtRoot -Directory -Filter "app-*" |
			Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "HearthstoneDeckTracker.exe") } |
			Sort-Object Name -Descending |
			Select-Object -First 1
		if ($latest) {
			return (Join-Path $latest.FullName "HearthstoneDeckTracker.exe")
		}
	}
	throw "HearthstoneDeckTracker.exe was not found."
}

function Get-SmokeInstalledPluginTargets {
	$targets = New-Object System.Collections.Generic.List[string]
	$targets.Add((Join-Path $env:APPDATA "HearthstoneDeckTracker\Plugins\MetaCompanion\MetaCompanion.dll"))
	$hdtRoot = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
	if (Test-Path -LiteralPath $hdtRoot) {
		Get-ChildItem -LiteralPath $hdtRoot -Directory -Filter "app-*" |
			Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "HearthstoneDeckTracker.exe") } |
			ForEach-Object {
				$targets.Add((Join-Path $_.FullName "Plugins\MetaCompanion\MetaCompanion.dll"))
			}
	}
	return $targets
}

function Get-SmokeHearthstonePath {
	$running = Get-Process Hearthstone -ErrorAction SilentlyContinue | Select-Object -First 1
	if ($running -and $running.Path) {
		return $running.Path
	}
	$candidates = @(
		"F:\Hearthstone\Hearthstone.exe",
		"C:\Program Files (x86)\Hearthstone\Hearthstone.exe",
		"C:\Program Files\Hearthstone\Hearthstone.exe"
	)
	return $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Convert-SmokeCheckpointAnswer([string]$Answer) {
	$normalized = if ($null -eq $Answer) { "" } else { $Answer.ToLowerInvariant() }
	switch ($normalized) {
		"y" { return "PASS" }
		"yes" { return "PASS" }
		"s" { return "MANUAL" }
		"skip" { return "MANUAL" }
		default { return "FAIL" }
	}
}

function Read-SmokeCheckpoint([string]$Prompt) {
	if ($NonInteractive) {
		return "MANUAL"
	}
	Write-Host ""
	Write-Host "[人工检查项] $Prompt"
	Write-Host "输入 y 表示通过，n 表示失败，s 表示保留人工确认/未运行。"
	$answer = Read-Host "结果"
	return Convert-SmokeCheckpointAnswer $answer
}

function Protect-SmokeText([string]$Text) {
	if ([string]::IsNullOrEmpty($Text)) {
		return ""
	}
	$value = $Text
	$value = [regex]::Replace($value, "(?im)\b(Cookie|Set-Cookie)\s*:\s*[^\r\n]+", '$1: [redacted]')
	$value = [regex]::Replace($value, "(?i)\b(Bearer)\s+[A-Za-z0-9._\-]{10,}", '$1 [redacted]')
	$value = [regex]::Replace($value, "(?i)\b(sessionid|csrftoken|cf_clearance|__cf_bm|remember_token|auth_token|access_token|refresh_token)\s*=\s*[^;\s\r\n]+", '$1=[redacted]')
	$value = [regex]::Replace($value, "eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", "[redacted-jwt]")
	return $value
}

function Test-SmokeSensitiveText([string]$Text) {
	$hits = New-Object System.Collections.Generic.List[string]
	if ([string]::IsNullOrEmpty($Text)) {
		return $hits
	}
	$patterns = @(
		@{ Name = "Cookie header"; Pattern = "(?im)\bCookie\s*:\s*(?!\[redacted\])[A-Za-z0-9_.-]+\s*=\s*[^\r\n]{12,}" },
		@{ Name = "Session cookie"; Pattern = "(?i)\b(sessionid|csrftoken|cf_clearance|__cf_bm|remember_token|auth_token|access_token|refresh_token)\s*=\s*(?!\[redacted\])[A-Za-z0-9_\-%.]{12,}" },
		@{ Name = "Bearer token"; Pattern = "(?i)\bBearer\s+(?!\[redacted\])[A-Za-z0-9._\-]{20,}" },
		@{ Name = "JWT token"; Pattern = "eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}" }
	)
	foreach ($pattern in $patterns) {
		if ($Text -match $pattern.Pattern) {
			$hits.Add($pattern.Name)
		}
	}
	return $hits
}

function Convert-SmokeAutomaticCheckName([string]$Name) {
	switch ($Name) {
		"hdt-started" { return "HDT 已启动" }
		"meta-deck-load-status" { return "牌组库加载状态" }
		"installed-dll-hash" { return "已安装 DLL 哈希" }
		"plugin-enabled" { return "插件已启用" }
		"config-xml-unchanged" { return "HDT config.xml 未被异常改动" }
		default { return $Name }
	}
}

function Format-SmokeAutomaticCheckDetails([string]$Name, [string]$Details) {
	if ([string]::IsNullOrWhiteSpace($Details)) {
		return ""
	}
	$value = Protect-SmokeText $Details
	switch ($Name) {
		"hdt-started" {
			if ($value -match "^pid=(.+)$") { return "进程 ID=$($matches[1])" }
			if ($value -eq "HDT did not start after install.") { return "安装后 HDT 未能启动。" }
		}
		"meta-deck-load-status" {
			if ($value -match "^status=(.+)$") { return "状态=$($matches[1])" }
		}
		"installed-dll-hash" {
			if ($value -match "^(\d+) targets match build hash$") { return "$($matches[1]) 个目标与构建哈希一致" }
			if ($value -match "^hash mismatch: (.+)$") { return "哈希不一致：$($matches[1])" }
			if ($value -match "^missing: (.+)$") { return "缺失：$($matches[1])" }
		}
		"plugin-enabled" {
			if ($value -eq "plugins.xml entry enabled") { return "plugins.xml 条目已启用" }
			if ($value -eq "Meta Companion is not enabled in plugins.xml.") { return "plugins.xml 中未启用 Meta Companion。" }
		}
		"config-xml-unchanged" {
			if ($value -match "^hash=(.+)$") { return "哈希=$($matches[1])" }
			if ($value -match "^config\.xml changed during smoke; review before=(.+) after=(.+)$") {
				return "config.xml 在烟测期间发生变化；请复核变更前=$($matches[1]) 变更后=$($matches[2])"
			}
			if ($value -eq "config.xml missing before and after") { return "变更前后均未发现 config.xml" }
			if ($value -eq "config.xml presence changed") { return "config.xml 存在状态发生变化" }
		}
	}
	return $value
}

function Convert-SmokeFileStatus([string]$Status) {
	switch ($Status) {
		"present" { return "存在" }
		"missing" { return "缺失" }
		default { return $Status }
	}
}

function Add-SmokeCheck(
	[System.Collections.Generic.List[object]]$Rows,
	[System.Collections.Generic.List[string]]$Failures,
	[string]$Name,
	[string]$Result,
	[string]$Details
) {
	$Rows.Add([pscustomobject]@{
		Name = $Name
		Result = $Result
		Details = Protect-SmokeText $Details
	})
	if ($Result -eq "FAIL") {
		$Failures.Add("自动检查失败：$(Convert-SmokeAutomaticCheckName $Name)：$(Format-SmokeAutomaticCheckDetails $Name $Details)")
	}
}

function Test-SmokeRowsHaveResult($Rows, [string]$Result) {
	foreach ($row in $Rows) {
		if ($row.Result -eq $Result) {
			return $true
		}
	}
	return $false
}

function Resolve-SmokeOverallResult($Rows, [System.Collections.Generic.List[string]]$Failures) {
	if ($Failures.Count -gt 0 -or (Test-SmokeRowsHaveResult $Rows "FAIL")) {
		return "FAIL"
	}
	if (Test-SmokeRowsHaveResult $Rows "MANUAL") {
		return "MANUAL_PENDING"
	}
	return "PASS"
}

function Get-SmokeExitCode([string]$OverallResult, [bool]$RequireManualPassValue) {
	if ($OverallResult -eq "FAIL") {
		return 1
	}
	if ($OverallResult -eq "MANUAL_PENDING" -and $RequireManualPassValue) {
		return 1
	}
	return 0
}

function Update-SmokeResultLine([System.Collections.Generic.List[string]]$Report, [string]$OverallResult) {
	for ($index = 0; $index -lt $Report.Count; $index++) {
		if ($Report[$index].StartsWith("- 总结果:") -or $Report[$index].StartsWith("- Result:")) {
			$Report[$index] = "- 总结果: $OverallResult"
			return
		}
	}
}

function Assert-SmokeSelfTest([bool]$Condition, [string]$Message) {
	if (-not $Condition) {
		throw $Message
	}
}

function Invoke-SmokeSelfTest {
	$emptyFailures = New-Object System.Collections.Generic.List[string]
	$oneFailure = New-Object System.Collections.Generic.List[string]
	$oneFailure.Add("simulated failure")

	$passRows = @(
		[pscustomobject]@{ Name = "automatic"; Result = "PASS" },
		[pscustomobject]@{ Name = "manual"; Result = "PASS" }
	)
	$manualRows = @(
		[pscustomobject]@{ Name = "automatic"; Result = "PASS" },
		[pscustomobject]@{ Name = "manual"; Result = "MANUAL" }
	)
	$failRows = @(
		[pscustomobject]@{ Name = "automatic"; Result = "PASS" },
		[pscustomobject]@{ Name = "manual"; Result = "FAIL" }
	)

	Assert-SmokeSelfTest ((Resolve-SmokeOverallResult $passRows $emptyFailures) -eq "PASS") "All PASS rows should produce PASS."
	Assert-SmokeSelfTest ((Resolve-SmokeOverallResult $manualRows $emptyFailures) -eq "MANUAL_PENDING") "MANUAL rows without failures should produce MANUAL_PENDING."
	Assert-SmokeSelfTest ((Resolve-SmokeOverallResult $failRows $emptyFailures) -eq "FAIL") "FAIL rows should produce FAIL."
	Assert-SmokeSelfTest ((Resolve-SmokeOverallResult $passRows $oneFailure) -eq "FAIL") "Failure list should produce FAIL."
	Assert-SmokeSelfTest ((Get-SmokeExitCode "PASS" $false) -eq 0) "PASS should exit 0."
	Assert-SmokeSelfTest ((Get-SmokeExitCode "MANUAL_PENDING" $false) -eq 0) "MANUAL_PENDING should exit 0 by default."
	Assert-SmokeSelfTest ((Get-SmokeExitCode "MANUAL_PENDING" $true) -eq 1) "MANUAL_PENDING should exit 1 with RequireManualPass."
	Assert-SmokeSelfTest ((Get-SmokeExitCode "FAIL" $false) -eq 1) "FAIL should exit 1."
	Assert-SmokeSelfTest ((Convert-SmokeCheckpointAnswer "y") -eq "PASS") "Manual y should produce PASS."
	Assert-SmokeSelfTest ((Convert-SmokeCheckpointAnswer "yes") -eq "PASS") "Manual yes should produce PASS."
	Assert-SmokeSelfTest ((Convert-SmokeCheckpointAnswer "s") -eq "MANUAL") "Manual s should produce MANUAL."
	Assert-SmokeSelfTest ((Convert-SmokeCheckpointAnswer "n") -eq "FAIL") "Manual n should produce FAIL."

	Write-Host "SELFTEST PASS: all pass => PASS"
	Write-Host "SELFTEST PASS: manual pending => MANUAL_PENDING"
	Write-Host "SELFTEST PASS: fail => FAIL"
	Write-Host "SELFTEST PASS: RequireManualPass + manual => exit 1"
	Write-Host "SELFTEST PASS: manual y => PASS and n => FAIL"
}

function Add-SmokeLogTail([System.Collections.Generic.List[string]]$Report, [string]$Directory) {
	if (-not (Test-Path -LiteralPath $Directory)) {
		$Report.Add("- 日志目录缺失：$Directory")
		return
	}
	$files = Get-ChildItem -LiteralPath $Directory -File -ErrorAction SilentlyContinue |
		Where-Object { $_.Extension -in @(".log", ".txt") } |
		Sort-Object LastWriteTime -Descending |
		Select-Object -First 4
	foreach ($file in $files) {
		$Report.Add("")
		$Report.Add("### $($file.FullName)")
		$Report.Add('```text')
		Get-Content -LiteralPath $file.FullName -Tail 40 -ErrorAction SilentlyContinue |
			ForEach-Object { $Report.Add((Protect-SmokeText $_)) }
		$Report.Add('```')
	}
}

function Get-SmokeFileStatus([string]$Path) {
	if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return [pscustomobject]@{
			Status = "missing"
			Length = 0
			LastWriteTime = ""
			SHA256 = ""
		}
	}
	$item = Get-Item -LiteralPath $Path
	$hash = Get-FileHash -LiteralPath $Path -Algorithm SHA256
	return [pscustomobject]@{
		Status = "present"
		Length = $item.Length
		LastWriteTime = $item.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
		SHA256 = $hash.Hash
	}
}

function Read-SmokeMetaDeckStatus([string]$DataDirectory) {
	$statusPath = Join-Path $DataDirectory "meta_deck_load_status.tsv"
	if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
		return [pscustomobject]@{
			Path = $statusPath
			Status = "Missing"
			Lines = @("meta_deck_load_status.tsv missing")
		}
	}
	$lines = @(Get-Content -LiteralPath $statusPath -Encoding UTF8 -ErrorAction SilentlyContinue |
		ForEach-Object { Protect-SmokeText $_ })
	$statusLine = $lines | Where-Object { $_ -match "^status\t" } | Select-Object -First 1
	$status = if ($statusLine) { ($statusLine -split "`t", 2)[1] } else { "Unknown" }
	return [pscustomobject]@{
		Path = $statusPath
		Status = $status
		Lines = $lines
	}
}

function Wait-SmokeMetaDeckStatus([string]$DataDirectory, [int]$TimeoutSeconds = 20) {
	$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
	do {
		$status = Read-SmokeMetaDeckStatus $DataDirectory
		if ($status.Status -in @("Loading", "Ready", "Empty", "Failed")) {
			return $status
		}
		Start-Sleep -Milliseconds 500
	} while ((Get-Date) -lt $deadline)
	return Read-SmokeMetaDeckStatus $DataDirectory
}

if ($SelfTest) {
	Invoke-SmokeSelfTest
	exit 0
}

$resolvedBuildPath = (Resolve-Path $BuildPath).Path
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDirectory = Join-Path $ArtifactsDirectory $runId
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$report = New-Object System.Collections.Generic.List[string]
$failures = New-Object System.Collections.Generic.List[string]
$automaticRows = New-Object System.Collections.Generic.List[object]

$buildHash = Get-FileHash -LiteralPath $resolvedBuildPath -Algorithm SHA256
$hdtPath = Get-SmokeHdtExecutablePath
$dataDirectory = Join-Path $env:APPDATA "HearthstoneDeckTracker\MetaCompanion"
$hdtConfigPath = Join-Path $env:APPDATA "HearthstoneDeckTracker\config.xml"
$hdtConfigBefore = if (Test-Path -LiteralPath $hdtConfigPath -PathType Leaf) {
	Get-FileHash -LiteralPath $hdtConfigPath -Algorithm SHA256
} else {
	$null
}
$hdtProcess = Get-Process HearthstoneDeckTracker -ErrorAction SilentlyContinue
if ($hdtProcess) {
	$hdtProcess | Stop-Process -Force
	Start-Sleep -Seconds 2
}

$installScript = Join-Path $PSScriptRoot "Install-MetaCompanion.ps1"
$ps32 = Join-Path $env:WINDIR "SysWOW64\WindowsPowerShell\v1.0\powershell.exe"
$installArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $installScript, "-BuildPath", $resolvedBuildPath)
if ($IncludeTools) {
	$installArgs += "-IncludeTools"
}
& $ps32 @installArgs
if ($LASTEXITCODE -ne 0) {
	throw "Install-MetaCompanion.ps1 failed with exit code $LASTEXITCODE."
}

Start-Process -FilePath $hdtPath
Start-Sleep -Seconds 4
$newHdtProcess = Get-Process HearthstoneDeckTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $newHdtProcess) {
	Add-SmokeCheck $automaticRows $failures "hdt-started" "FAIL" "HDT did not start after install."
} else {
	Add-SmokeCheck $automaticRows $failures "hdt-started" "PASS" "pid=$($newHdtProcess.Id)"
}
$metaDeckStatus = Wait-SmokeMetaDeckStatus $dataDirectory 20
if ($metaDeckStatus.Status -in @("Loading", "Ready", "Empty", "Failed")) {
	Add-SmokeCheck $automaticRows $failures "meta-deck-load-status" "PASS" "status=$($metaDeckStatus.Status)"
} else {
	Add-SmokeCheck $automaticRows $failures "meta-deck-load-status" "FAIL" "status=$($metaDeckStatus.Status)"
}

if ($LaunchHearthstone) {
	$hearthstonePath = Get-SmokeHearthstonePath
	if ($hearthstonePath) {
		Start-Process -FilePath $hearthstonePath
	} else {
		$failures.Add("Hearthstone.exe was not found.")
	}
}

$hashRows = New-Object System.Collections.Generic.List[string]
foreach ($target in Get-SmokeInstalledPluginTargets) {
	if (Test-Path -LiteralPath $target) {
		$targetHash = Get-FileHash -LiteralPath $target -Algorithm SHA256
		$hashRows.Add("$target`t$($targetHash.Hash)")
		if ($targetHash.Hash -ne $buildHash.Hash) {
			Add-SmokeCheck $automaticRows $failures "installed-dll-hash" "FAIL" "hash mismatch: $target"
		}
	} else {
		Add-SmokeCheck $automaticRows $failures "installed-dll-hash" "FAIL" "missing: $target"
	}
}
if ($hashRows.Count -gt 0 -and -not ($failures | Where-Object { $_ -like "*installed-dll-hash*" })) {
	Add-SmokeCheck $automaticRows $failures "installed-dll-hash" "PASS" "$($hashRows.Count) targets match build hash"
}

$pluginsXmlPath = Join-Path $env:APPDATA "HearthstoneDeckTracker\plugins.xml"
$pluginEnabled = $false
if (Test-Path -LiteralPath $pluginsXmlPath) {
	[xml]$pluginsXml = Get-Content -LiteralPath $pluginsXmlPath
	$entry = $pluginsXml.ArrayOfPluginSettings.PluginSettings |
		Where-Object { $_.FileName -eq "Plugins/MetaCompanion/MetaCompanion.dll" -or $_.Name -eq "Meta Companion" } |
		Select-Object -First 1
	$pluginEnabled = $entry -and $entry.IsEnabled -eq "true"
}
if (-not $pluginEnabled) {
	Add-SmokeCheck $automaticRows $failures "plugin-enabled" "FAIL" "Meta Companion is not enabled in plugins.xml."
} else {
	Add-SmokeCheck $automaticRows $failures "plugin-enabled" "PASS" "plugins.xml entry enabled"
}

$hdtConfigAfter = if (Test-Path -LiteralPath $hdtConfigPath -PathType Leaf) {
	Get-FileHash -LiteralPath $hdtConfigPath -Algorithm SHA256
} else {
	$null
}
if ($hdtConfigBefore -and $hdtConfigAfter) {
	if ($hdtConfigBefore.Hash -eq $hdtConfigAfter.Hash) {
		Add-SmokeCheck $automaticRows $failures "config-xml-unchanged" "PASS" "hash=$($hdtConfigAfter.Hash)"
	} else {
		Add-SmokeCheck $automaticRows $failures "config-xml-unchanged" "MANUAL" "config.xml changed during smoke; review before=$($hdtConfigBefore.Hash) after=$($hdtConfigAfter.Hash)"
	}
} elseif (-not $hdtConfigBefore -and -not $hdtConfigAfter) {
	Add-SmokeCheck $automaticRows $failures "config-xml-unchanged" "PASS" "config.xml missing before and after"
} else {
	Add-SmokeCheck $automaticRows $failures "config-xml-unchanged" "MANUAL" "config.xml presence changed"
}

$keyFiles = @(
	"config.xml",
	"meta_deck_load_status.tsv",
	"hsreplay_deckcodes.txt",
	"hsguru_deckcodes.txt",
	"archetype_deck_branches.tsv",
	"metaDecks.xml",
	"local_meta_environment.tsv",
	"local_meta_archetypes.tsv",
	"match_history.tsv",
	"match_corrections.tsv",
	"Tools\Run-MetaCompanionRefresh.ps1",
	"Tools\Install-MetaCompanionRefreshTask.ps1",
	"Premium\Meta\latest\summary.json",
	"Premium\Meta\latest\head_to_head_archetype_matchups_v2.json",
	"Premium\Meta\latest\personal_recommendations.tsv"
)

$checkpoints = @(
	@{ Name = "hdt-startup"; Title = "HDT 启动"; Prompt = "确认 HDT 正常启动，Meta Companion 已启用，且没有出现插件崩溃对话框。" },
	@{ Name = "meta-deck-loading-state"; Title = "牌组库 Loading 状态"; Prompt = "确认刚启动后或状态为 Loading 时，设置/数据健康能显示牌组库加载中或不可用状态。" },
	@{ Name = "meta-deck-ready-state"; Title = "牌组库 Ready 状态"; Prompt = "确认 Ready 的牌组库状态会在下一场符合条件的标准模式对局中启用预测。" },
	@{ Name = "meta-deck-empty-state"; Title = "牌组库 Empty 状态"; Prompt = "在没有牌组快照时，确认设置/数据健康显示牌组库不可用，并且 HDT 继续运行。" },
	@{ Name = "meta-deck-failed-state"; Title = "牌组库 Failed 状态"; Prompt = "使用故意损坏的快照或状态时，确认设置/数据健康和日志显示失败摘要，且不包含敏感值。" },
	@{ Name = "standard-game-start"; Title = "标准模式对局开始"; Prompt = "开始或旁观一场符合条件的标准模式对局，确认只有牌组库 Ready 后 Meta Companion 才启用预测。" },
	@{ Name = "non-standard-not-enabled"; Title = "非标准模式不启用"; Prompt = "打开或观察非标准、乱斗或战棋场景，确认 Meta Companion 不启用 PredictionController。" },
	@{ Name = "settings-data-health"; Title = "设置页数据健康"; Prompt = "打开 Meta Companion 设置页，确认数据健康显示来源状态、牌组加载状态和可读详情。" },
	@{ Name = "auto-refresh-entry"; Title = "自动刷新入口"; Prompt = "在设置页确认自动刷新区域显示工具、计划任务和日志状态；缺少 Tools 时按钮能安全降级。" },
	@{ Name = "copy-diagnostics"; Title = "复制诊断信息"; Prompt = "点击复制诊断信息，确认剪贴板文本包含健康、刷新和日志摘要，并且没有认证密钥类敏感值。" },
	@{ Name = "recent-game-explanation"; Title = "最近一局解释"; Prompt = "记录一局后，确认最近一局解释显示 Top 3 候选、置信度、score、branchCount 和关键证据牌。" },
	@{ Name = "correct-current-game"; Title = "修正本局形态"; Prompt = "使用候选按钮或文本输入修正最近一局形态，确认 match_corrections.tsv 追加一行合法记录。" },
	@{ Name = "correction-refresh"; Title = "修正后刷新"; Prompt = "修正后，确认本地环境已刷新，或设置页/仪表板提示下一局后会使用刷新后的本地环境。" }
)
$checkpointRows = New-Object System.Collections.Generic.List[object]
foreach ($checkpoint in $checkpoints) {
	$result = Read-SmokeCheckpoint $checkpoint.Prompt
	$checkpointRows.Add([pscustomobject]@{ Name = $checkpoint.Name; Title = $checkpoint.Title; Result = $result; Prompt = $checkpoint.Prompt })
	if ($result -eq "FAIL") {
		$failures.Add("人工检查失败：$($checkpoint.Title)")
	}
}

$resultRows = New-Object System.Collections.Generic.List[object]
foreach ($row in $automaticRows) { $resultRows.Add($row) }
foreach ($row in $checkpointRows) { $resultRows.Add($row) }
$overallResult = Resolve-SmokeOverallResult $resultRows $failures

$report.Add("# Meta Companion HDT 客户端烟测报告")
$report.Add("")
$report.Add("- 运行编号: $runId")
$report.Add("- 构建 DLL: $resolvedBuildPath")
$report.Add("- 构建 SHA256: $($buildHash.Hash)")
$report.Add("- HDT 路径: $hdtPath")
$report.Add("- HDT 进程 ID: " + ($(if ($newHdtProcess) { "$($newHdtProcess.Id)" } else { "缺失" })))
$report.Add("- 总结果: $overallResult")
$report.Add("")
$report.Add("## 结果说明")
$report.Add("- PASS: 所有自动检查通过，且所有人工检查项都已确认通过。")
$report.Add("- MANUAL_PENDING: 未检测到失败，但至少有一个人工检查项仍需人工确认。")
$report.Add("- FAIL: 自动检查或人工检查项失败。")
$report.Add("- MANUAL: 需要人工确认或复核。")
$report.Add("")
$report.Add("## 自动检查")
foreach ($row in $automaticRows) {
	$report.Add("- $($row.Result) $(Convert-SmokeAutomaticCheckName $row.Name)：$(Format-SmokeAutomaticCheckDetails $row.Name $row.Details)")
}
$report.Add("")
$report.Add("## 已安装 DLL")
foreach ($row in $hashRows) { $report.Add("- $row") }
$report.Add("")
$report.Add("## 插件状态")
$report.Add("- plugins.xml 路径: $pluginsXmlPath")
$report.Add("- 已启用: $pluginEnabled")
$report.Add("")
$report.Add("## HDT 配置保护")
$report.Add("- 路径: $hdtConfigPath")
$report.Add("- 变更前: " + ($(if ($hdtConfigBefore) { $hdtConfigBefore.Hash } else { "缺失" })))
$report.Add("- 变更后: " + ($(if ($hdtConfigAfter) { $hdtConfigAfter.Hash } else { "缺失" })))
$report.Add("")
$report.Add("## 牌组库加载状态")
$report.Add("- 路径: $($metaDeckStatus.Path)")
$report.Add("- 状态: $($metaDeckStatus.Status)")
foreach ($line in $metaDeckStatus.Lines) {
	$report.Add("- $line")
}
$report.Add("")
$report.Add("## 关键数据文件")
foreach ($file in $keyFiles) {
	$path = Join-Path $dataDirectory $file
	$status = Get-SmokeFileStatus $path
	$report.Add("- $($file): $(Convert-SmokeFileStatus $status.Status)；长度=$($status.Length)；更新时间=$($status.LastWriteTime)；SHA256=$($status.SHA256)")
}
$report.Add("")
$report.Add("## 人工检查项")
foreach ($row in $checkpointRows) {
	$report.Add("- $($row.Result) $($row.Title)：$($row.Prompt)")
}
$report.Add("")
$report.Add("## 日志尾部")
Add-SmokeLogTail $report (Join-Path $dataDirectory "Logs")
Add-SmokeLogTail $report (Join-Path $env:APPDATA "HearthstoneDeckTracker\Logs")
$report.Add("")

$safePreview = ($report | ForEach-Object { Protect-SmokeText $_ }) -join [Environment]::NewLine
$sensitiveHits = Test-SmokeSensitiveText $safePreview
foreach ($hit in $sensitiveHits) {
	$failures.Add("Sensitive value in smoke report after sanitization: $hit")
}
$overallResult = Resolve-SmokeOverallResult $resultRows $failures
Update-SmokeResultLine $report $overallResult

$report.Add("## 失败项")
if ($failures.Count -eq 0) {
	$report.Add("- 无")
} else {
	foreach ($failure in $failures) { $report.Add("- $(Protect-SmokeText $failure)") }
}

$reportPath = Join-Path $runDirectory "hdt-client-smoke.md"
$report | ForEach-Object { Protect-SmokeText $_ } | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "Smoke report: $reportPath"

$exitCode = Get-SmokeExitCode $overallResult $RequireManualPass.IsPresent
if ($exitCode -ne 0) {
	if ($overallResult -eq "FAIL") {
		if ($failures.Count -gt 0) {
			$failures | ForEach-Object { Write-Error $_ }
		} else {
			Write-Error "烟测总结果为 FAIL。"
		}
	} elseif ($overallResult -eq "MANUAL_PENDING") {
		Write-Error "存在待人工确认的检查项，且已指定 -RequireManualPass。"
	}
	exit $exitCode
}

Write-Host "HDT CLIENT SMOKE $overallResult"

