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
		return "SKIPPED"
	}
	Write-Host ""
	Write-Host "[manual checkpoint] $Prompt"
	Write-Host "Type y for pass, n for fail, or s to skip."
	$answer = Read-Host "Result"
	switch ($answer.ToLowerInvariant()) {
		"y" { return "PASS" }
		"yes" { return "PASS" }
		"s" { return "SKIPPED" }
		"skip" { return "SKIPPED" }
		default { return "FAIL" }
	}
}

function Add-SmokeLogTail([System.Collections.Generic.List[string]]$Report, [string]$Directory) {
	if (-not (Test-Path -LiteralPath $Directory)) {
		$Report.Add("- Missing log directory: $Directory")
		return
	}
	$files = Get-ChildItem -LiteralPath $Directory -File -Filter "*.log" -ErrorAction SilentlyContinue |
		Sort-Object LastWriteTime -Descending |
		Select-Object -First 2
	foreach ($file in $files) {
		$Report.Add("")
		$Report.Add("### $($file.FullName)")
		$Report.Add('```text')
		Get-Content -LiteralPath $file.FullName -Tail 40 -ErrorAction SilentlyContinue |
			ForEach-Object { $Report.Add($_) }
		$Report.Add('```')
	}
}

$resolvedBuildPath = (Resolve-Path $BuildPath).Path
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDirectory = Join-Path $ArtifactsDirectory $runId
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$report = New-Object System.Collections.Generic.List[string]
$failures = New-Object System.Collections.Generic.List[string]

$buildHash = Get-FileHash -LiteralPath $resolvedBuildPath -Algorithm SHA256
$hdtPath = Get-SmokeHdtExecutablePath
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
	$failures.Add("HDT did not start after install.")
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
			$failures.Add("Installed DLL hash mismatch: $target")
		}
	} else {
		$failures.Add("Installed plugin DLL missing: $target")
	}
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
	$failures.Add("Meta Companion is not enabled in plugins.xml.")
}

$dataDirectory = Join-Path $env:APPDATA "HearthstoneDeckTracker\MetaCompanion"
$keyFiles = @(
	"config.xml",
	"local_meta_environment.tsv",
	"local_meta_archetypes.tsv",
	"Premium\Meta\latest\personal_recommendations.tsv"
)

$checkpoints = @(
	@{ Name = "standard-menu-dashboard"; Prompt = "Open Standard Play deck selection. Confirm archetype recommendation dashboard is visible and can be closed." },
	@{ Name = "dashboard-interaction"; Prompt = "Confirm the dashboard can be dragged and hover tooltips appear for recommendation/environment sections." },
	@{ Name = "gameplay-hide-dashboard"; Prompt = "Enter an actual match. Confirm the Standard meta dashboard hides during gameplay." },
	@{ Name = "early-native-prediction"; Prompt = "After the first few opponent cards, confirm native embedded predictions are stable and do not flicker away." },
	@{ Name = "remaining-cards-panel"; Prompt = "Confirm Remaining Cards Prediction appears only near the remaining-deck threshold, can be dragged, and position persists after HDT restart." },
	@{ Name = "post-game-refresh"; Prompt = "After a match ends, confirm local history refreshes and dashboard updated time/sample count changes reasonably." }
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
$report.Add("## Installed DLLs")
foreach ($row in $hashRows) { $report.Add("- $row") }
$report.Add("")
$report.Add("## Plugin State")
$report.Add("- plugins.xml: $pluginsXmlPath")
$report.Add("- enabled: $pluginEnabled")
$report.Add("")
$report.Add("## Key Data Files")
foreach ($file in $keyFiles) {
	$path = Join-Path $dataDirectory $file
	$status = if (Test-Path -LiteralPath $path) { "present" } else { "missing" }
	$report.Add("- $($file): $status")
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
$report.Add("## Failures")
if ($failures.Count -eq 0) {
	$report.Add("- None")
} else {
	foreach ($failure in $failures) { $report.Add("- $failure") }
}

$reportPath = Join-Path $runDirectory "hdt-client-smoke.md"
$report | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "Smoke report: $reportPath"

if ($failures.Count -gt 0) {
	$failures | ForEach-Object { Write-Error $_ }
	exit 1
}

Write-Host "HDT CLIENT SMOKE PASS"
