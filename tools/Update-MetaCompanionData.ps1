param(
	[switch]$Full,
	[switch]$Premium,
	[switch]$Meta,
	[switch]$Branches,
	[switch]$Recommendations,
	[switch]$PersonalRecommendations,
	[switch]$LocalMeta,
	[string[]]$RankRanges = @(),
	[int]$LimitPerRange = 0,
	[int]$MaxDecks = 0,
	[int]$DeckPageTimeoutSeconds = 12,
	[int]$Parallelism = 1,
	[string]$PremiumCookiePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt",
	[string]$PremiumTimeRange = "LAST_7_DAYS",
	[int]$PremiumMaxDecks = 30,
	[switch]$PremiumStopOnUnsupported,
	[string]$MetaTimeRange = "CURRENT_PATCH",
	[int]$MetaTopOverall = 20,
	[int]$MetaTopPerClass = 5,
	[int]$RecommendationTop = 20,
	[int]$RecommendationMinMatchupGames = 200,
	[double]$RecommendationMinCoveragePct = 50,
	[int]$PersonalRecommendationHistoryDays = 3,
	[double]$PersonalRecommendationLocalWeight = 0.35,
	[int]$LocalMetaMinConfidence = 35,
	[datetime]$PatchTime = [datetime]::MinValue,
	[double]$PrePatchWeight = 0.35,
	[string]$BranchCandidateTimeRange = "LAST_7_DAYS",
	[int]$BranchesPerArchetype = 5,
	[int]$BranchMinGames = 100,
	[string]$OutputPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_deckcodes.txt"
)

$ErrorActionPreference = "Stop"

$syncScript = Join-Path $PSScriptRoot "Sync-HSReplayDeckCodes.ps1"
$premiumSyncScript = Join-Path $PSScriptRoot "Sync-HSReplayPremiumData.ps1"
$metaSyncScript = Join-Path $PSScriptRoot "Sync-HSReplayMetaData.ps1"
$branchSyncScript = Join-Path $PSScriptRoot "Sync-HSReplayArchetypeDecks.ps1"
$hdtHistoryExportScript = Join-Path $PSScriptRoot "Export-HdtOpponentHistory.ps1"
$localMetaScript = Join-Path $PSScriptRoot "Measure-HdtLocalMeta.ps1"
$recommendationScript = Join-Path $PSScriptRoot "Get-MetaArchetypeRecommendations.ps1"
$personalRecommendationScript = Join-Path $PSScriptRoot "Get-PersonalMetaRecommendations.ps1"
$verifyScript = Join-Path $PSScriptRoot "Verify-DeckCodeImport.ps1"
$recommendationsOnly = ($Recommendations -or $PersonalRecommendations -or $LocalMeta) -and
	-not $Full -and -not $Premium -and -not $Meta -and
	-not $Branches -and $RankRanges.Count -eq 0

function Try-ParseDate([string]$Value) {
	$result = [DateTime]::MinValue
	if ([DateTime]::TryParse($Value, [ref]$result)) {
		return $result
	}
	return $null
}

function Resolve-HearthstoneExePath {
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

function Resolve-EffectivePatchTime {
	if ($PatchTime -ne [datetime]::MinValue) {
		return $PatchTime
	}

	$patchMarkerPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\patch_marker.txt"
	if (Test-Path -LiteralPath $patchMarkerPath) {
		$markerTime = Try-ParseDate ((Get-Content -LiteralPath $patchMarkerPath -Raw -Encoding UTF8).Trim())
		if ($markerTime) {
			return $markerTime
		}
	}

	$exePath = Resolve-HearthstoneExePath
	if (-not [string]::IsNullOrWhiteSpace($exePath)) {
		return (Get-Item -LiteralPath $exePath).LastWriteTime
	}
	return $null
}

if ($recommendationsOnly) {
	$rankRanges = @()
	$limitPerRange = 0
	$maxDecks = 0
} elseif ($RankRanges.Count -gt 0) {
	$rankRanges = @($RankRanges | ForEach-Object { $_ -split "," } |
		ForEach-Object { $_.Trim() } |
		Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
	if ($LimitPerRange -le 0) {
		$limitPerRange = 250
	}
	if ($MaxDecks -le 0) {
		$maxDecks = 500
	}
} elseif ($Full) {
	$rankRanges = @(
		"DIAMOND_THROUGH_LEGEND",
		"DIAMOND_FOUR_THROUGH_DIAMOND_ONE",
		"PLATINUM",
		"GOLD",
		"BRONZE_THROUGH_GOLD"
	)
	if ($LimitPerRange -le 0) {
		$limitPerRange = 250
	}
	if ($MaxDecks -le 0) {
		$maxDecks = 500
	}
} else {
	$rankRanges = @(
		"DIAMOND_THROUGH_LEGEND",
		"DIAMOND_FOUR_THROUGH_DIAMOND_ONE",
		"PLATINUM",
		"GOLD",
		"BRONZE_THROUGH_GOLD"
	)
	if ($LimitPerRange -le 0) {
		$limitPerRange = 250
	}
	if ($MaxDecks -le 0) {
		$maxDecks = 500
	}
}

if (-not $recommendationsOnly) {
	Write-Host "Updating Meta Companion data..."
	& $syncScript `
		-RankRanges $rankRanges `
		-LimitPerRange $limitPerRange `
		-MaxDecks $maxDecks `
		-DeckPageTimeoutSeconds $DeckPageTimeoutSeconds `
		-Parallelism $Parallelism `
		-OutputPath $OutputPath

	$hdtRoot = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
	$hdtAppPath = $null
	if (Test-Path $hdtRoot) {
		$hdtAppPath = Get-ChildItem $hdtRoot -Directory -Filter "app-*" |
			Where-Object { Test-Path (Join-Path $_.FullName "HearthstoneDeckTracker.exe") } |
			Sort-Object LastWriteTime -Descending |
			Select-Object -First 1 -ExpandProperty FullName
	}

	if ($hdtAppPath -and (Test-Path $verifyScript)) {
		Write-Host ""
		Write-Host "Verifying imported deck codes..."
		& $verifyScript -DeckCodePath $OutputPath -HdtAppPath $hdtAppPath
	}
} else {
	Write-Host "Skipping deck-code sync; recalculating recommendations from existing meta cache."
}

if ($Premium) {
	if (-not (Test-Path $premiumSyncScript)) {
		throw "Premium sync script not found: $premiumSyncScript"
	}

	Write-Host ""
	Write-Host "Updating HSReplay Premium analytics cache..."
	$premiumArgs = @{
		CookiePath = $PremiumCookiePath
		DeckCodePath = $OutputPath
		TimeRange = $PremiumTimeRange
		MaxDecks = $PremiumMaxDecks
	}
	if ($PremiumStopOnUnsupported) {
		$premiumArgs.StopOnUnsupported = $true
	}
	& $premiumSyncScript @premiumArgs
}

if ($Meta) {
	if (-not (Test-Path $metaSyncScript)) {
		throw "Meta sync script not found: $metaSyncScript"
	}

	Write-Host ""
	Write-Host "Updating HSReplay meta analytics cache..."
	& $metaSyncScript `
		-CookiePath $PremiumCookiePath `
		-TimeRange $MetaTimeRange `
		-TopOverall $MetaTopOverall `
		-TopPerClass $MetaTopPerClass
}

if ($Branches) {
	if (-not (Test-Path $branchSyncScript)) {
		throw "Archetype branch sync script not found: $branchSyncScript"
	}

	Write-Host ""
	Write-Host "Updating HSReplay archetype deck branches..."
	& $branchSyncScript `
		-CookiePath $PremiumCookiePath `
		-CandidateTimeRange $BranchCandidateTimeRange `
		-BranchesPerArchetype $BranchesPerArchetype `
		-MinGames $BranchMinGames `
		-Parallelism $Parallelism
}

if ($Meta -or $Recommendations) {
	if (-not (Test-Path $recommendationScript)) {
		throw "Recommendation script not found: $recommendationScript"
	}

	Write-Host ""
	Write-Host "Calculating archetype recommendations..."
	& $recommendationScript `
		-Top $RecommendationTop `
		-MinMatchupGames $RecommendationMinMatchupGames `
		-MinCoveragePct $RecommendationMinCoveragePct `
		-IncludeClassTop
}

if ($LocalMeta -or $PersonalRecommendations) {
	if ((Test-Path $hdtHistoryExportScript) -and (Test-Path $localMetaScript) -and
		(Test-Path $OutputPath)) {
		Write-Host ""
		Write-Host "Measuring local HDT opponent meta..."
		$effectivePatchTime = Resolve-EffectivePatchTime
		$historyExportArgs = @{}
		if ($MetaTimeRange -eq "CURRENT_PATCH" -and $effectivePatchTime) {
			$historyExportArgs.Since = $effectivePatchTime
		} else {
			$historyExportArgs.Days = $PersonalRecommendationHistoryDays
		}
		& $hdtHistoryExportScript @historyExportArgs
		$localMetaArgs = @{
			DeckCodePath = $OutputPath
			Days = $PersonalRecommendationHistoryDays
			MinConfidence = $LocalMetaMinConfidence
			PrePatchWeight = $PrePatchWeight
		}
		if ($effectivePatchTime) {
			$localMetaArgs.PatchTime = $effectivePatchTime
		}
		& $localMetaScript @localMetaArgs
	} else {
		Write-Warning "Skipping local HDT meta measurement; required scripts or deck-code snapshot are missing."
	}
}

if ($Meta -or $Recommendations -or $PersonalRecommendations) {
	if (-not (Test-Path $personalRecommendationScript)) {
		throw "Personal recommendation script not found: $personalRecommendationScript"
	}

	Write-Host ""
	Write-Host "Calculating personal archetype recommendations..."
	& $personalRecommendationScript `
		-Top $RecommendationTop `
		-HistoryDays $PersonalRecommendationHistoryDays `
		-LocalWeight $PersonalRecommendationLocalWeight `
		-MinMatchupGames $RecommendationMinMatchupGames `
		-MinCoveragePct $RecommendationMinCoveragePct `
		-IncludeClassTop
}

Write-Host ""
if ($recommendationsOnly) {
	Write-Host "Recommendations recalculated from existing meta cache."
} else {
	Write-Host "Data file updated: $OutputPath"
	Write-Host "Restart HDT for the plugin to load the new snapshot."
}
