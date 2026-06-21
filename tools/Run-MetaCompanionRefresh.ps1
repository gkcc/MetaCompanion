param(
	[int]$Parallelism = 6,
	[int]$BranchesPerArchetype = 5,
	[int]$BranchMinGames = 100,
	[int]$RecommendationTop = 20,
	[int]$HistoryDays = 3,
	[double]$LocalWeight = 0.35,
	[int]$LocalMetaMinConfidence = 35,
	[string]$PrimaryTimeRange = "CURRENT_PATCH",
	[string]$MetaFallbackTimeRange = "CURRENT_PATCH",
	[string]$PremiumFallbackTimeRange = "LAST_7_DAYS",
	[int]$PremiumMaxDecks = 30,
	[string]$DataDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion",
	[switch]$Force,
	[switch]$IncludeBranches,
	[switch]$SkipBranches
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$logDirectory = Join-Path $DataDirectory "Logs"
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$logPath = Join-Path $logDirectory ("refresh-" + (Get-Date).ToString("yyyyMMdd-HHmmss") + ".log")

function Test-RemoteCacheRefreshedToday([string]$Root) {
	$requiredPaths = @(
		(Join-Path $Root "hsreplay_deckcodes.txt"),
		(Join-Path $Root "Premium\Meta\latest\summary.json"),
		(Join-Path $Root "Premium\Meta\latest\head_to_head_archetype_matchups_v2.json"),
		(Join-Path $Root "Premium\Meta\latest\manifest.json")
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
		return [string]::Equals($timeRange, "CURRENT_PATCH", [StringComparison]::OrdinalIgnoreCase)
	} catch {
		return $false
	}
}

Start-Transcript -Path $logPath | Out-Null
try {
	Set-Location $repoRoot
	if (-not $Force -and (Test-RemoteCacheRefreshedToday $DataDirectory)) {
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

		if ($IncludeBranches -and -not $SkipBranches) {
			$refreshArgs.Branches = $true
			$refreshArgs.BranchCandidateTimeRange = $BranchCandidateTimeRange
			$refreshArgs.BranchesPerArchetype = $BranchesPerArchetype
			$refreshArgs.BranchMinGames = $BranchMinGames
		}

		& (Join-Path $PSScriptRoot "Update-MetaCompanionData.ps1") @refreshArgs
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
		Invoke-MetaCompanionRefreshRun `
			-PremiumTimeRange $PremiumFallbackTimeRange `
			-MetaTimeRange $MetaFallbackTimeRange `
			-BranchCandidateTimeRange $PremiumFallbackTimeRange `
			-PremiumStopOnUnsupported $false
	}
}
finally {
	Stop-Transcript | Out-Null
	Write-Host "Refresh log: $logPath"
}
