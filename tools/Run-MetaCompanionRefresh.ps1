param(
	[int]$Parallelism = 6,
	[int]$BranchesPerArchetype = 5,
	[int]$BranchMinGames = 100,
	[int]$RecommendationTop = 20,
	[int]$HistoryDays = 3,
	[double]$LocalWeight = 0.35,
	[int]$LocalMetaMinConfidence = 35,
	[string]$PrimaryTimeRange = "CURRENT_PATCH",
	[string]$MetaFallbackTimeRange = "LAST_1_DAY",
	[string]$PremiumFallbackTimeRange = "LAST_7_DAYS",
	[int]$PremiumMaxDecks = 30,
	[string]$DataDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion",
	[switch]$Force,
	[switch]$IncludeBranches,
	[switch]$SkipBranches
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$patchStateScript = Join-Path $PSScriptRoot "Update-MetaCompanionPatchState.ps1"
if (Test-Path -LiteralPath $patchStateScript) {
	. $patchStateScript
}
$logDirectory = Join-Path $DataDirectory "Logs"
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$logPath = Join-Path $logDirectory ("refresh-" + (Get-Date).ToString("yyyyMMdd-HHmmss") + ".log")

function Test-RemoteCacheRefreshedToday([string]$Root, [datetime]$PatchTime = [datetime]::MinValue) {
	$requiredPaths = @(
		(Join-Path $Root "hsreplay_deckcodes.txt"),
		(Join-Path $Root "archetype_deck_branches.tsv"),
		(Join-Path $Root "Premium\Meta\latest\summary.json"),
		(Join-Path $Root "Premium\Meta\latest\head_to_head_archetype_matchups_v2.json"),
		(Join-Path $Root "Premium\Meta\latest\manifest.json"),
		(Join-Path $Root "Premium\Branches\latest\manifest.json")
	)
	foreach ($path in $requiredPaths) {
		if (-not (Test-Path -LiteralPath $path)) {
			return $false
		}
		if ((Get-Item -LiteralPath $path).LastWriteTime.Date -ne (Get-Date).Date) {
			return $false
		}
	}

	$manifestPath = Join-Path $Root "Premium\Meta\latest\manifest.json"
	try {
		$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
		$timeRange = [string]$manifest.selected_time_range
		if ([string]::IsNullOrWhiteSpace($timeRange)) {
			$timeRange = [string]$manifest.time_range
		}
		if (-not [string]::Equals($timeRange, "CURRENT_PATCH", [StringComparison]::OrdinalIgnoreCase) -and
			-not [string]::Equals($timeRange, "LAST_1_DAY", [StringComparison]::OrdinalIgnoreCase)) {
			return $false
		}

		if ($PatchTime -ne [datetime]::MinValue) {
			$summaryPath = Join-Path $Root "Premium\Meta\latest\summary.json"
			$summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
			$asOf = Read-MetaCompanionDate ([string]$summary.as_of)
			if ($asOf -and $asOf -lt $PatchTime) {
				return $false
			}

			$branchManifestPath = Join-Path $Root "Premium\Branches\latest\manifest.json"
			$branchManifest = Get-Content -LiteralPath $branchManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
			if (-not [string]::Equals(
				[string]$branchManifest.candidate_time_range,
				"CURRENT_PATCH",
				[StringComparison]::OrdinalIgnoreCase)) {
				return $false
			}
			$branchAsOf = Read-MetaCompanionDate ([string]$branchManifest.candidate.as_of)
			if ($branchAsOf -and $branchAsOf -lt $PatchTime) {
				return $false
			}
		}
		return $true
	} catch {
		return $false
	}
}

Start-Transcript -Path $logPath | Out-Null
try {
	Set-Location $repoRoot
	$patchState = $null
	if (Get-Command Update-MetaCompanionPatchState -ErrorAction SilentlyContinue) {
		$patchState = Update-MetaCompanionPatchState -DataDirectory $DataDirectory
		if ($patchState.PatchChanged) {
			Write-Host "Detected new Hearthstone patch $($patchState.PatchVersion); archived $($patchState.ArchivedFileCount) active local data files."
		}
	}
	$patchTime = if ($patchState -and $patchState.PatchTime) { $patchState.PatchTime } else { [datetime]::MinValue }
	if (-not $Force -and (Test-RemoteCacheRefreshedToday $DataDirectory $patchTime)) {
		Write-Host "Remote cache already refreshed today; skipping. Use -Force to refresh anyway."
		return
	}

	function Invoke-MetaCompanionRefreshRun(
		[string]$PremiumTimeRange,
		[string]$MetaTimeRange,
		[string]$BranchCandidateTimeRange,
		[bool]$PremiumStopOnUnsupported
	) {
		$refreshArgs = @{
			Premium = $true
			Meta = $true
			Recommendations = $true
			PersonalRecommendations = $true
			LocalMeta = $true
			PremiumTimeRange = $PremiumTimeRange
			MetaTimeRange = $MetaTimeRange
			PremiumMaxDecks = $PremiumMaxDecks
			RecommendationTop = $RecommendationTop
			PersonalRecommendationHistoryDays = $HistoryDays
			PersonalRecommendationLocalWeight = $LocalWeight
			LocalMetaMinConfidence = $LocalMetaMinConfidence
			Parallelism = $Parallelism
		}
		if ($PremiumStopOnUnsupported) {
			$refreshArgs.PremiumStopOnUnsupported = $true
		}

		if (-not $SkipBranches) {
			$refreshArgs.Branches = $true
			$refreshArgs.BranchCandidateTimeRange = $BranchCandidateTimeRange
			$refreshArgs.BranchesPerArchetype = $BranchesPerArchetype
			$refreshArgs.BranchMinGames = $BranchMinGames
		}

		& (Join-Path $PSScriptRoot "Update-MetaCompanionData.ps1") @refreshArgs
	}

	function Invoke-MetaCompanionCachedRecommendationRun([string]$MetaTimeRange) {
		Write-Warning "Premium/meta refresh failed; recalculating recommendations from existing cache."
		& (Join-Path $PSScriptRoot "Update-MetaCompanionData.ps1") `
			-Recommendations `
			-PersonalRecommendations `
			-LocalMeta `
			-MetaTimeRange $MetaTimeRange `
			-RecommendationTop $RecommendationTop `
			-PersonalRecommendationHistoryDays $HistoryDays `
			-PersonalRecommendationLocalWeight $LocalWeight `
			-LocalMetaMinConfidence $LocalMetaMinConfidence
	}

	try {
		Invoke-MetaCompanionRefreshRun `
			-PremiumTimeRange $PrimaryTimeRange `
			-MetaTimeRange $PrimaryTimeRange `
			-BranchCandidateTimeRange $PrimaryTimeRange `
			-PremiumStopOnUnsupported $true
	} catch {
		Write-Warning "Primary refresh using $PrimaryTimeRange failed: $($_.Exception.Message)"
		Write-Warning "Retrying with Premium=$PremiumFallbackTimeRange, Meta=$MetaFallbackTimeRange."
		try {
			Invoke-MetaCompanionRefreshRun `
				-PremiumTimeRange $PremiumFallbackTimeRange `
				-MetaTimeRange $MetaFallbackTimeRange `
				-BranchCandidateTimeRange $PremiumFallbackTimeRange `
				-PremiumStopOnUnsupported $false
		} catch {
			Write-Warning "Fallback refresh failed: $($_.Exception.Message)"
			Invoke-MetaCompanionCachedRecommendationRun -MetaTimeRange $MetaFallbackTimeRange
		}
	}
}
finally {
	Stop-Transcript | Out-Null
	Write-Host "Refresh log: $logPath"
}
