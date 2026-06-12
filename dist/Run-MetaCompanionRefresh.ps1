param(
	[int]$Parallelism = 6,
	[int]$BranchesPerArchetype = 5,
	[int]$BranchMinGames = 100,
	[int]$RecommendationTop = 20,
	[int]$HistoryDays = 3,
	[double]$LocalWeight = 0.35,
	[int]$LocalMetaMinConfidence = 35,
	[string]$PrimaryTimeRange = "CURRENT_PATCH",
	[string]$MetaFallbackTimeRange = "LAST_3_DAYS",
	[string]$PremiumFallbackTimeRange = "LAST_7_DAYS",
	[int]$PremiumMaxDecks = 30,
	[switch]$IncludeBranches,
	[switch]$SkipBranches
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$logDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Logs"
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$logPath = Join-Path $logDirectory ("refresh-" + (Get-Date).ToString("yyyyMMdd-HHmmss") + ".log")

Start-Transcript -Path $logPath | Out-Null
try {
	Set-Location $repoRoot
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
