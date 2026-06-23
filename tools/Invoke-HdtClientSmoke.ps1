param(
	[string]$BuildPath = "$PSScriptRoot\..\MetaCompanion\bin\x86\Release\MetaCompanion.dll",
	[string]$ArtifactsDirectory = "$PSScriptRoot\..\artifacts\client-smoke",
	[switch]$LaunchHearthstone,
	[switch]$IncludeTools,
	[switch]$NonInteractive
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

function Read-SmokeCheckpoint([string]$Prompt) {
	if ($NonInteractive) {
		return "MANUAL"
	}
	Write-Host ""
	Write-Host "[manual checkpoint] $Prompt"
	Write-Host "Type y for pass, n for fail, or s to mark manual/not-run."
	$answer = Read-Host "Result"
	switch ($answer.ToLowerInvariant()) {
		"y" { return "PASS" }
		"yes" { return "PASS" }
		"s" { return "MANUAL" }
		"skip" { return "MANUAL" }
		default { return "FAIL" }
	}
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
		$Failures.Add("Automatic check failed: ${Name}: $Details")
	}
}

function Update-SmokeResultLine([System.Collections.Generic.List[string]]$Report, [System.Collections.Generic.List[string]]$Failures) {
	for ($index = 0; $index -lt $Report.Count; $index++) {
		if ($Report[$index].StartsWith("- Result:")) {
			$Report[$index] = "- Result: " + ($(if ($Failures.Count -eq 0) { "PASS" } else { "FAIL" }))
			return
		}
	}
}

function Add-SmokeLogTail([System.Collections.Generic.List[string]]$Report, [string]$Directory) {
	if (-not (Test-Path -LiteralPath $Directory)) {
		$Report.Add("- Missing log directory: $Directory")
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
	@{ Name = "hdt-startup"; Prompt = "Confirm HDT starts normally, Meta Companion is enabled, and no plugin crash dialog appears." },
	@{ Name = "meta-deck-loading-state"; Prompt = "Confirm Settings/Data Health can show the deck library loading or unavailable state immediately after startup or when status is Loading." },
	@{ Name = "meta-deck-ready-state"; Prompt = "Confirm a Ready deck library state enables predictions for the next eligible Standard game." },
	@{ Name = "meta-deck-empty-state"; Prompt = "With no deck snapshots available, confirm Settings/Data Health reports deck library unavailable and HDT keeps running." },
	@{ Name = "meta-deck-failed-state"; Prompt = "With a deliberately broken snapshot/status, confirm Settings/Data Health and logs show a failure summary without sensitive values." },
	@{ Name = "standard-game-start"; Prompt = "Start or spectate an eligible Standard game. Confirm Meta Companion enables prediction only after the deck library is Ready." },
	@{ Name = "non-standard-not-enabled"; Prompt = "Open or observe a non-Standard, Tavern Brawl, or Battlegrounds context. Confirm Meta Companion does not enable PredictionController." },
	@{ Name = "settings-data-health"; Prompt = "Open Meta Companion Settings. Confirm Data Health shows source status, deck load status, and readable detail lines." },
	@{ Name = "auto-refresh-entry"; Prompt = "In Settings, confirm the Auto Refresh area shows tool/task/log status and buttons degrade safely when Tools are missing." },
	@{ Name = "copy-diagnostics"; Prompt = "Click Copy Diagnostics. Confirm clipboard text includes health/refresh/log summaries and no authentication secret values." },
	@{ Name = "recent-game-explanation"; Prompt = "After a recorded game, confirm Recent Game Explanation shows Top 3 candidates, confidence, score, branchCount, and key evidence cards." },
	@{ Name = "correct-current-game"; Prompt = "Use candidate buttons or text input to correct the latest game archetype. Confirm match_corrections.tsv appends one legal row." },
	@{ Name = "correction-refresh"; Prompt = "After correction, confirm local environment refreshes or Settings/dashboard indicates the refreshed local meta on the next game." }
)
$checkpointRows = New-Object System.Collections.Generic.List[object]
foreach ($checkpoint in $checkpoints) {
	$result = Read-SmokeCheckpoint $checkpoint.Prompt
	$checkpointRows.Add([pscustomobject]@{ Name = $checkpoint.Name; Result = $result; Prompt = $checkpoint.Prompt })
	if ($result -eq "FAIL") {
		$failures.Add("Manual checkpoint failed: $($checkpoint.Name)")
	}
}

$report.Add("# Meta Companion HDT Client Smoke")
$report.Add("")
$report.Add("- Run: $runId")
$report.Add("- Build DLL: $resolvedBuildPath")
$report.Add("- Build SHA256: $($buildHash.Hash)")
$report.Add("- HDT: $hdtPath")
$report.Add("- HDT process: " + ($(if ($newHdtProcess) { "$($newHdtProcess.Id)" } else { "missing" })))
$report.Add("- Result: " + ($(if ($failures.Count -eq 0) { "PASS" } else { "FAIL" })))
$report.Add("")
$report.Add("## Result Legend")
$report.Add("- PASS: automated check passed or manual checkpoint was confirmed.")
$report.Add("- FAIL: automated check or manual checkpoint failed.")
$report.Add("- MANUAL: human confirmation or review is still required.")
$report.Add("")
$report.Add("## Automatic Checks")
foreach ($row in $automaticRows) {
	$report.Add("- $($row.Result) $($row.Name): $($row.Details)")
}
$report.Add("")
$report.Add("## Installed DLLs")
foreach ($row in $hashRows) { $report.Add("- $row") }
$report.Add("")
$report.Add("## Plugin State")
$report.Add("- plugins.xml: $pluginsXmlPath")
$report.Add("- enabled: $pluginEnabled")
$report.Add("")
$report.Add("## HDT Config Guard")
$report.Add("- Path: $hdtConfigPath")
$report.Add("- Before: " + ($(if ($hdtConfigBefore) { $hdtConfigBefore.Hash } else { "missing" })))
$report.Add("- After: " + ($(if ($hdtConfigAfter) { $hdtConfigAfter.Hash } else { "missing" })))
$report.Add("")
$report.Add("## Meta Deck Load Status")
$report.Add("- Path: $($metaDeckStatus.Path)")
$report.Add("- Status: $($metaDeckStatus.Status)")
foreach ($line in $metaDeckStatus.Lines) {
	$report.Add("- $line")
}
$report.Add("")
$report.Add("## Key Data Files")
foreach ($file in $keyFiles) {
	$path = Join-Path $dataDirectory $file
	$status = Get-SmokeFileStatus $path
	$report.Add("- $($file): $($status.Status); length=$($status.Length); updated=$($status.LastWriteTime); sha256=$($status.SHA256)")
}
$report.Add("")
$report.Add("## Manual Checkpoints")
foreach ($row in $checkpointRows) {
	$report.Add("- $($row.Result) $($row.Name): $($row.Prompt)")
}
$report.Add("")
$report.Add("## Log Tails")
Add-SmokeLogTail $report (Join-Path $dataDirectory "Logs")
Add-SmokeLogTail $report (Join-Path $env:APPDATA "HearthstoneDeckTracker\Logs")
$report.Add("")

$safePreview = ($report | ForEach-Object { Protect-SmokeText $_ }) -join [Environment]::NewLine
$sensitiveHits = Test-SmokeSensitiveText $safePreview
foreach ($hit in $sensitiveHits) {
	$failures.Add("Sensitive value in smoke report after sanitization: $hit")
}
Update-SmokeResultLine $report $failures

$report.Add("## Failures")
if ($failures.Count -eq 0) {
	$report.Add("- None")
} else {
	foreach ($failure in $failures) { $report.Add("- $(Protect-SmokeText $failure)") }
}

$reportPath = Join-Path $runDirectory "hdt-client-smoke.md"
$report | ForEach-Object { Protect-SmokeText $_ } | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "Smoke report: $reportPath"

if ($failures.Count -gt 0) {
	$failures | ForEach-Object { Write-Error $_ }
	exit 1
}

Write-Host "HDT CLIENT SMOKE PASS"
