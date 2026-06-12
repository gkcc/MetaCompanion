param(
	[string]$BuildPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($BuildPath)) {
	$packagedDll = Join-Path $PSScriptRoot "MetaCompanion.dll"
	$distDll = Join-Path $PSScriptRoot "..\dist\MetaCompanion.dll"
	$repositoryDll = Join-Path $PSScriptRoot "..\MetaCompanion\bin\x86\Release\MetaCompanion.dll"
	$BuildPath = if (Test-Path $packagedDll) {
		$packagedDll
	} elseif (Test-Path $distDll) {
		$distDll
	} else {
		$repositoryDll
	}
}
if (-not (Test-Path $BuildPath)) {
	throw "MetaCompanion.dll was not found at $BuildPath"
}

$process = Get-Process HearthstoneDeckTracker -ErrorAction SilentlyContinue
if ($process) {
	throw "Hearthstone Deck Tracker is running. Close HDT first, then run this script again."
}

$targets = New-Object System.Collections.Generic.List[string]
$targets.Add("$env:APPDATA\HearthstoneDeckTracker\Plugins\MetaCompanion\MetaCompanion.dll")
$legacyTargets = New-Object System.Collections.Generic.List[string]
$legacyTargets.Add("$env:APPDATA\HearthstoneDeckTracker\Plugins\DeckPredictor\DeckPredictor.dll")
$legacyPluginDirectories = New-Object System.Collections.Generic.List[string]
$legacyPluginDirectories.Add("$env:APPDATA\HearthstoneDeckTracker\Plugins\DeckPredictor")

$hdtLocalPath = "$env:LOCALAPPDATA\HearthstoneDeckTracker"
if (Test-Path $hdtLocalPath) {
	Get-ChildItem $hdtLocalPath -Directory -Filter "app-*" |
		Where-Object { Test-Path (Join-Path $_.FullName "HearthstoneDeckTracker.exe") } |
		ForEach-Object {
			$targets.Add((Join-Path $_.FullName "Plugins\MetaCompanion\MetaCompanion.dll"))
			$legacyTargets.Add((Join-Path $_.FullName "Plugins\DeckPredictor\DeckPredictor.dll"))
			$legacyPluginDirectories.Add((Join-Path $_.FullName "Plugins\DeckPredictor"))
		}
}

foreach ($target in $targets) {
	New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
	Copy-Item -LiteralPath $BuildPath -Destination $target -Force
}

foreach ($legacyTarget in $legacyTargets) {
	if (Test-Path -LiteralPath $legacyTarget) {
		Remove-Item -LiteralPath $legacyTarget -Force
		Write-Host "Removed legacy plugin DLL: $legacyTarget"
	}
}

foreach ($legacyPluginDirectory in $legacyPluginDirectories) {
	if (Test-Path -LiteralPath $legacyPluginDirectory) {
		Remove-Item -LiteralPath $legacyPluginDirectory -Recurse -Force
		Write-Host "Removed legacy plugin directory: $legacyPluginDirectory"
	}
}

$toolTargetDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Tools"
$dataTargetDirectory = Split-Path -Parent $toolTargetDirectory
$legacyDataDirectory = "$env:APPDATA\HearthstoneDeckTracker\DeckPredictor"
if ((Test-Path -LiteralPath $legacyDataDirectory) -and -not (Test-Path -LiteralPath $dataTargetDirectory)) {
	Write-Host "Migrating local data to $dataTargetDirectory"
	New-Item -ItemType Directory -Force -Path $dataTargetDirectory | Out-Null
	Get-ChildItem -LiteralPath $legacyDataDirectory -Force | Where-Object {
		$_.Name -notin @("Logs", "Tools")
	} | ForEach-Object {
		$target = Join-Path $dataTargetDirectory $_.Name
		Copy-Item -LiteralPath $_.FullName -Destination $target -Recurse -Force
	}
}
if (Test-Path -LiteralPath $legacyDataDirectory) {
	Remove-Item -LiteralPath $legacyDataDirectory -Recurse -Force
	Write-Host "Removed legacy data directory: $legacyDataDirectory"
}
New-Item -ItemType Directory -Force -Path $toolTargetDirectory | Out-Null
Get-ChildItem -Path $PSScriptRoot -Filter "*.ps1" -File | ForEach-Object {
	Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $toolTargetDirectory $_.Name) -Force
}

$pluginsXmlPath = "$env:APPDATA\HearthstoneDeckTracker\plugins.xml"
if (Test-Path $pluginsXmlPath) {
	[xml]$xml = Get-Content $pluginsXmlPath
	$legacyEntries = @($xml.ArrayOfPluginSettings.PluginSettings |
		Where-Object { $_.FileName -eq "Plugins/DeckPredictor/DeckPredictor.dll" -or
			$_.Name -eq "Deck Predictor" })
	foreach ($legacyEntry in $legacyEntries) {
		[void]$xml.ArrayOfPluginSettings.RemoveChild($legacyEntry)
	}
	$existing = $xml.ArrayOfPluginSettings.PluginSettings |
		Where-Object { $_.FileName -eq "Plugins/MetaCompanion/MetaCompanion.dll" -or
			$_.Name -eq "Meta Companion" }
	if ($existing) {
		$existing.IsEnabled = "true"
		$existing.Name = "Meta Companion"
	} else {
		$node = $xml.CreateElement("PluginSettings")
		$fileName = $xml.CreateElement("FileName")
		$fileName.InnerText = "Plugins/MetaCompanion/MetaCompanion.dll"
		$node.AppendChild($fileName) | Out-Null
		$isEnabled = $xml.CreateElement("IsEnabled")
		$isEnabled.InnerText = "true"
		$node.AppendChild($isEnabled) | Out-Null
		$name = $xml.CreateElement("Name")
		$name.InnerText = "Meta Companion"
		$node.AppendChild($name) | Out-Null
		$xml.ArrayOfPluginSettings.AppendChild($node) | Out-Null
	}
	$xml.Save($pluginsXmlPath)
}

@($BuildPath) + $targets | ForEach-Object {
	Get-FileHash $_ -Algorithm SHA256
}
Write-Host "Tools copied to $toolTargetDirectory"

