param(
	[string]$BuildPath = "",
	[switch]$IncludeTools
)

$ErrorActionPreference = "Stop"

$refreshTaskName = "Meta Companion Remote Cache Refresh"
$requiredToolScripts = @(
	"Run-MetaCompanionRefresh.ps1",
	"Install-MetaCompanionRefreshTask.ps1",
	"Update-MetaCompanionData.ps1",
	"Update-MetaCompanionPatchState.ps1",
	"Sync-HSReplayDeckCodes.ps1",
	"Sync-HSReplayPremiumData.ps1",
	"Sync-HSReplayMetaData.ps1",
	"Sync-HSReplayArchetypeDecks.ps1",
	"Export-HdtOpponentHistory.ps1",
	"Measure-HdtLocalMeta.ps1",
	"Get-MetaArchetypeRecommendations.ps1",
	"Get-PersonalMetaRecommendations.ps1",
	"Verify-DeckCodeImport.ps1"
)

function Resolve-ToolSourceDirectory([string[]]$RequiredScripts) {
	$candidates = @(
		$PSScriptRoot,
		(Join-Path $PSScriptRoot "..\tools")
	)
	foreach ($candidate in ($candidates | Select-Object -Unique)) {
		if (-not (Test-Path -LiteralPath $candidate)) {
			continue
		}
		$resolved = (Resolve-Path -LiteralPath $candidate).Path
		$missing = @($RequiredScripts | Where-Object {
			-not (Test-Path -LiteralPath (Join-Path $resolved $_))
		})
		if ($missing.Count -eq 0) {
			return $resolved
		}
	}
	throw "Refresh tool source scripts were not found. Run this installer from the source checkout, or install without -IncludeTools for the community DLL-only mode."
}

function Remove-MetaCompanionRefreshTask([string]$TaskName) {
	if (-not (Get-Command Get-ScheduledTask -ErrorAction SilentlyContinue)) {
		return
	}
	$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
	if (-not $task) {
		return
	}
	try {
		Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
		Write-Host "Removed refresh scheduled task: $TaskName"
	} catch {
		Write-Warning "Could not remove refresh scheduled task '$TaskName': $($_.Exception.Message)"
	}
}

if ([string]::IsNullOrWhiteSpace($BuildPath)) {
	$packagedDll = Join-Path $PSScriptRoot "MetaCompanion.dll"
	$distDll = Join-Path $PSScriptRoot "..\dist\MetaCompanion.dll"
	$repositoryDll = Join-Path $PSScriptRoot "..\MetaCompanion\bin\Release\MetaCompanion.dll"
	$BuildPath = if (Test-Path $packagedDll) {
		$packagedDll
	} elseif (Test-Path $distDll) {
		$distDll
	} elseif (Test-Path $repositoryDll) {
		$repositoryDll
	} else {
		""
	}
	if ([string]::IsNullOrWhiteSpace($BuildPath)) {
		throw "MetaCompanion.dll was not found in the packaged folder, dist, or MetaCompanion\bin\Release. Run tools\Build-MetaCompanion.ps1 from the repo root, or pass -BuildPath explicitly."
	}
}
if (-not (Test-Path $BuildPath)) {
	throw "MetaCompanion.dll was not found at $BuildPath"
}
$BuildPath = (Resolve-Path -LiteralPath $BuildPath).Path

function Read-UInt16([byte[]]$Bytes, [int]$Offset) {
	return [BitConverter]::ToUInt16($Bytes, $Offset)
}

function Read-UInt32([byte[]]$Bytes, [int]$Offset) {
	return [BitConverter]::ToUInt32($Bytes, $Offset)
}

function ConvertFrom-PeMachine([int]$Machine) {
	switch ($Machine) {
		0x014c { return "I386" }
		0x8664 { return "AMD64" }
		0xaa64 { return "ARM64" }
		default { return ("0x{0:X4}" -f $Machine) }
	}
}

function ConvertTo-PeFileOffset([byte[]]$Bytes, [int]$Rva, [int]$SectionOffset, [int]$SectionCount) {
	for ($index = 0; $index -lt $SectionCount; $index++) {
		$offset = $SectionOffset + ($index * 40)
		$virtualSize = Read-UInt32 $Bytes ($offset + 8)
		$virtualAddress = Read-UInt32 $Bytes ($offset + 12)
		$rawSize = Read-UInt32 $Bytes ($offset + 16)
		$rawPointer = Read-UInt32 $Bytes ($offset + 20)
		$span = [Math]::Max($virtualSize, $rawSize)
		if ($Rva -ge $virtualAddress -and $Rva -lt ($virtualAddress + $span)) {
			return [int]($rawPointer + ($Rva - $virtualAddress))
		}
	}
	return -1
}

function Get-PeAssemblyInfo([string]$Path) {
	$bytes = [IO.File]::ReadAllBytes($Path)
	if ($bytes.Length -lt 0x40) {
		throw "Invalid PE file: $Path"
	}
	$peOffset = [int](Read-UInt32 $bytes 0x3c)
	$machine = Read-UInt16 $bytes ($peOffset + 4)
	$sectionCount = Read-UInt16 $bytes ($peOffset + 6)
	$optionalHeaderSize = Read-UInt16 $bytes ($peOffset + 20)
	$optionalHeaderOffset = $peOffset + 24
	$magic = Read-UInt16 $bytes $optionalHeaderOffset
	$dataDirectoryOffset = if ($magic -eq 0x20b) {
		$optionalHeaderOffset + 112
	} else {
		$optionalHeaderOffset + 96
	}
	$clrDirectoryOffset = $dataDirectoryOffset + (14 * 8)
	$clrRva = Read-UInt32 $bytes $clrDirectoryOffset
	$sectionOffset = $optionalHeaderOffset + $optionalHeaderSize
	$corFlags = $null
	if ($clrRva -ne 0) {
		$clrOffset = ConvertTo-PeFileOffset $bytes $clrRva $sectionOffset $sectionCount
		if ($clrOffset -ge 0) {
			$corFlags = Read-UInt32 $bytes ($clrOffset + 16)
		}
	}
	return [pscustomobject]@{
		Path = $Path
		Machine = ConvertFrom-PeMachine $machine
		CorFlags = $corFlags
		Is32BitRequired = ($corFlags -ne $null -and (($corFlags -band 0x2) -ne 0))
	}
}

function Get-MetaCompanionFileHash([string]$Path) {
	if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) {
		return Get-FileHash -LiteralPath $Path -Algorithm SHA256
	}

	$sha256 = [System.Security.Cryptography.SHA256]::Create()
	$stream = [IO.File]::OpenRead($Path)
	try {
		$hash = $sha256.ComputeHash($stream)
	} finally {
		$stream.Dispose()
		if ($sha256 -is [IDisposable]) {
			$sha256.Dispose()
		}
	}
	return [pscustomobject]@{
		Algorithm = "SHA256"
		Hash = ([BitConverter]::ToString($hash) -replace "-", "")
		Path = $Path
	}
}

function Get-LatestHdtExecutablePath {
	$hdtLocalPath = "$env:LOCALAPPDATA\HearthstoneDeckTracker"
	if (-not (Test-Path -LiteralPath $hdtLocalPath)) {
		return $null
	}
	$latest = Get-ChildItem -LiteralPath $hdtLocalPath -Directory -Filter "app-*" |
		Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "HearthstoneDeckTracker.exe") } |
		Sort-Object @{Expression = {
			try { [version]$_.Name.Substring(4) } catch { [version]"0.0" }
		}; Descending = $true} |
		Select-Object -First 1
	if (-not $latest) {
		return $null
	}
	return (Join-Path $latest.FullName "HearthstoneDeckTracker.exe")
}

function Assert-BuildCompatibleWithLatestHdt([string]$PluginPath) {
	$hdtExe = Get-LatestHdtExecutablePath
	if (-not $hdtExe) {
		return
	}
	$hdtInfo = Get-PeAssemblyInfo $hdtExe
	$pluginInfo = Get-PeAssemblyInfo $PluginPath
	if ($hdtInfo.Machine -eq "AMD64" -and $pluginInfo.Is32BitRequired) {
		throw "The latest HDT is 64-bit ($hdtExe), but $PluginPath is x86-only. Build Release AnyCPU and install MetaCompanion\bin\Release\MetaCompanion.dll."
	}
}

Assert-BuildCompatibleWithLatestHdt $BuildPath

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
if ($IncludeTools) {
	$toolSourceDirectory = Resolve-ToolSourceDirectory $requiredToolScripts
	New-Item -ItemType Directory -Force -Path $toolTargetDirectory | Out-Null
	Get-ChildItem -Path $toolSourceDirectory -Filter "*.ps1" -File | ForEach-Object {
		Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $toolTargetDirectory $_.Name) -Force
	}
} else {
	if (Test-Path -LiteralPath $toolTargetDirectory) {
		Remove-Item -LiteralPath $toolTargetDirectory -Recurse -Force
		Write-Host "Removed bundled refresh tools: $toolTargetDirectory"
	}
	Remove-MetaCompanionRefreshTask $refreshTaskName
}

$configPath = Join-Path $dataTargetDirectory "config.xml"
if (Test-Path -LiteralPath $configPath) {
	[xml]$configXml = Get-Content -LiteralPath $configPath -Encoding UTF8
	$configChanged = $false
	if ($IncludeTools) {
		if ($configXml.PluginConfig.EnablePostGameMetaRefresh -and
			$configXml.PluginConfig.EnablePostGameMetaRefresh -ne "true") {
			$configXml.PluginConfig.EnablePostGameMetaRefresh = "true"
			$configChanged = $true
		}
		if ($configChanged) {
			$configXml.Save($configPath)
			Write-Host "Enabled post-game local meta refresh in config: $configPath"
		}
	} else {
		if ($configXml.PluginConfig.EnablePostGameMetaRefresh) {
			$configXml.PluginConfig.EnablePostGameMetaRefresh = "false"
			$configChanged = $true
		}
		if ($configXml.PluginConfig.EnablePostGameDataRefresh) {
			$configXml.PluginConfig.EnablePostGameDataRefresh = "false"
			$configChanged = $true
		}
		if ($configChanged) {
			$configXml.Save($configPath)
			Write-Host "Disabled post-game refresh in config: $configPath"
		}
	}
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
	Get-MetaCompanionFileHash $_
}
if ($IncludeTools) {
	Write-Host "Tools copied to $toolTargetDirectory"
} else {
	Write-Host "Refresh tools and scheduled task were not installed. Re-run with -IncludeTools for development or advanced manual sync."
}

