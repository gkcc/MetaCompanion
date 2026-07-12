function Normalize-MetaCompanionPatchVersion([string]$Value) {
	if ([string]::IsNullOrWhiteSpace($Value)) {
		return ""
	}
	$match = [regex]::Match($Value, "\b(\d+\.\d+\.\d+)(?:\.\d+)?\b")
	if ($match.Success) {
		return $match.Groups[1].Value
	}
	return ""
}

function Read-MetaCompanionDate([string]$Value) {
	$result = [DateTime]::MinValue
	if ([string]::IsNullOrWhiteSpace($Value)) {
		return $null
	}
	if ([DateTime]::TryParse($Value.Trim(), [ref]$result)) {
		return $result
	}
	return $null
}

function Resolve-MetaCompanionHearthstoneExePath {
	$process = Get-Process -Name "Hearthstone" -ErrorAction SilentlyContinue | Select-Object -First 1
	if ($process -and -not [string]::IsNullOrWhiteSpace($process.Path) -and
		(Test-Path -LiteralPath $process.Path)) {
		return $process.Path
	}

	$candidates = @(
		"F:\Hearthstone\Hearthstone.exe",
		"C:\Program Files (x86)\Hearthstone\Hearthstone.exe",
		"C:\Program Files\Hearthstone\Hearthstone.exe"
	)
	foreach ($candidate in $candidates) {
		if (Test-Path -LiteralPath $candidate) {
			return $candidate
		}
	}
	return ""
}

function Get-MetaCompanionDetectedPatch {
	param([string]$PatchVersion = "", [datetime]$PatchTime = [datetime]::MinValue)

	$version = Normalize-MetaCompanionPatchVersion $PatchVersion
	$time = if ($PatchTime -ne [datetime]::MinValue) { $PatchTime } else { $null }
	$source = ""

	$exePath = Resolve-MetaCompanionHearthstoneExePath
	if (Test-Path -LiteralPath $exePath) {
		$source = $exePath
		if (-not $time) {
			$time = (Get-Item -LiteralPath $exePath).LastWriteTime
		}
		if ([string]::IsNullOrWhiteSpace($version)) {
			$productDbPath = Join-Path (Split-Path -Parent $exePath) ".product.db"
			if (Test-Path -LiteralPath $productDbPath) {
				$text = [System.Text.Encoding]::ASCII.GetString(
					[System.IO.File]::ReadAllBytes($productDbPath))
				$version = Normalize-MetaCompanionPatchVersion $text
			}
		}
		if ([string]::IsNullOrWhiteSpace($version)) {
			$version = Normalize-MetaCompanionPatchVersion (
				(Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion)
		}
	}

	return [pscustomobject]@{
		Version = $version
		PatchTime = $time
		Source = $source
	}
}

function Get-MetaCompanionUniquePath([string]$Path) {
	if (-not (Test-Path -LiteralPath $Path)) {
		return $Path
	}

	$directory = Split-Path -Parent $Path
	$name = [System.IO.Path]::GetFileNameWithoutExtension($Path)
	$extension = [System.IO.Path]::GetExtension($Path)
	for ($index = 1; ; $index++) {
		$candidate = Join-Path $directory ("$name.$index$extension")
		if (-not (Test-Path -LiteralPath $candidate)) {
			return $candidate
		}
	}
}

function Move-MetaCompanionActiveLocalData {
	param(
		[string]$DataDirectory,
		[string]$PatchVersion,
		[datetime]$Now = (Get-Date)
	)

	$relativePaths = @(
		"match_history.tsv",
		"match_corrections.tsv",
		"prediction_timeline.tsv",
		"hdt_opponent_history.tsv",
		"local_meta_archetypes.tsv",
		"local_meta_environment.tsv",
		"local_meta_summary.json",
		"post_game_data_refresh.last",
		"Premium\Meta\latest\personal_recommendations.tsv",
		"Premium\Meta\latest\personal_recommendations.json"
	)
	$safeVersion = if ([string]::IsNullOrWhiteSpace($PatchVersion)) {
		"unknown"
	} else {
		$PatchVersion -replace '[\\/:*?"<>|]', "_"
	}
	$archiveDirectory = Join-Path $DataDirectory (
		"PatchArchives\" + $Now.ToString("yyyyMMdd-HHmmss") + "-" + $safeVersion)
	$moved = 0

	foreach ($relativePath in $relativePaths) {
		$source = Join-Path $DataDirectory $relativePath
		if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
			continue
		}
		$destination = Join-Path $archiveDirectory $relativePath
		$destinationDirectory = Split-Path -Parent $destination
		New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
		Move-Item -LiteralPath $source -Destination (Get-MetaCompanionUniquePath $destination) -Force
		$moved++
	}

	return [pscustomobject]@{
		ArchiveDirectory = if ($moved -gt 0) { $archiveDirectory } else { "" }
		ArchivedFileCount = $moved
	}
}

function Update-MetaCompanionPatchState {
	param(
		[string]$DataDirectory,
		[string]$PatchVersion = "",
		[datetime]$PatchTime = [datetime]::MinValue,
		[datetime]$Now = (Get-Date)
	)

	if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
		return [pscustomobject]@{
			PatchChanged = $false
			PatchVersion = ""
			PatchTime = $null
			ArchivedFileCount = 0
			ArchiveDirectory = ""
		}
	}

	New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
	$detected = Get-MetaCompanionDetectedPatch -PatchVersion $PatchVersion -PatchTime $PatchTime
	$version = Normalize-MetaCompanionPatchVersion $detected.Version
	$time = $detected.PatchTime
	$versionPath = Join-Path $DataDirectory "patch_version.txt"
	$markerPath = Join-Path $DataDirectory "patch_marker.txt"
	$storedVersion = if (Test-Path -LiteralPath $versionPath) {
		Normalize-MetaCompanionPatchVersion (
			Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8)
	} else { "" }
	$storedMarker = if (Test-Path -LiteralPath $markerPath) {
		Read-MetaCompanionDate (Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8)
	} else { $null }

	$patchChanged = $false
	if (-not [string]::IsNullOrWhiteSpace($storedVersion) -and
		-not [string]::IsNullOrWhiteSpace($version) -and
		-not [string]::Equals($storedVersion, $version, [StringComparison]::OrdinalIgnoreCase)) {
		$patchChanged = $true
	} elseif ($time -and $storedMarker -and $storedMarker -lt $time.AddMinutes(-1)) {
		$patchChanged = $true
	}

	$archive = [pscustomobject]@{
		ArchiveDirectory = ""
		ArchivedFileCount = 0
	}
	if ($patchChanged) {
		$archive = Move-MetaCompanionActiveLocalData `
			-DataDirectory $DataDirectory `
			-PatchVersion $version `
			-Now $Now
	}

	if (-not [string]::IsNullOrWhiteSpace($version)) {
		$version | Set-Content -LiteralPath $versionPath -Encoding UTF8
	}
	if ($time -and ($patchChanged -or -not $storedMarker -or $storedMarker -lt $time.AddMinutes(-1))) {
		$time.ToString("o") | Set-Content -LiteralPath $markerPath -Encoding UTF8
	}

	return [pscustomobject]@{
		PatchChanged = $patchChanged
		PatchVersion = $version
		PatchTime = $time
		ArchivedFileCount = $archive.ArchivedFileCount
		ArchiveDirectory = $archive.ArchiveDirectory
	}
}
