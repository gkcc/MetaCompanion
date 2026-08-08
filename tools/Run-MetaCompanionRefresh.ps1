param(
	[int]$Parallelism = 6,
	[int]$BranchesPerArchetype = 5,
	[int]$BranchMinGames = 100,
	[int]$RecommendationTop = 20,
	[int]$HistoryDays = 3,
	[int]$HistoryMatches = 0,
	[double]$LocalWeight = 0.35,
	[int]$LocalMetaMinConfidence = 35,
	[string]$PrimaryTimeRange = "",
	[string]$RemoteRankRange = "",
	[string]$MetaFallbackTimeRange = "LAST_1_DAY",
	[string]$PremiumFallbackTimeRange = "LAST_7_DAYS",
	[string]$ModelBranchTimeRange = "CURRENT_PATCH",
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
$configPath = Join-Path $DataDirectory "config.xml"
if (Test-Path -LiteralPath $configPath) {
	try {
		[xml]$pluginConfig = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8
		$configRoot = $pluginConfig.PluginConfig
		if (-not $PSBoundParameters.ContainsKey("PrimaryTimeRange") -and
			-not [string]::IsNullOrWhiteSpace([string]$configRoot.PostGamePrimaryTimeRange)) {
			$PrimaryTimeRange = [string]$configRoot.PostGamePrimaryTimeRange
		}
		if (-not $PSBoundParameters.ContainsKey("RemoteRankRange") -and
			-not [string]::IsNullOrWhiteSpace([string]$configRoot.PostGameRankRange)) {
			$RemoteRankRange = [string]$configRoot.PostGameRankRange
		}
		if (-not $PSBoundParameters.ContainsKey("MetaFallbackTimeRange") -and
			-not [string]::IsNullOrWhiteSpace([string]$configRoot.PostGameMetaFallbackTimeRange)) {
			$MetaFallbackTimeRange = [string]$configRoot.PostGameMetaFallbackTimeRange
		}
		if (-not $PSBoundParameters.ContainsKey("PremiumFallbackTimeRange") -and
			-not [string]::IsNullOrWhiteSpace([string]$configRoot.PostGamePremiumFallbackTimeRange)) {
			$PremiumFallbackTimeRange = [string]$configRoot.PostGamePremiumFallbackTimeRange
		}
		if (-not $PSBoundParameters.ContainsKey("HistoryDays") -and
			-not [string]::IsNullOrWhiteSpace([string]$configRoot.LocalRecommendationHistoryDays)) {
			$HistoryDays = [Math]::Max(0, [int]$configRoot.LocalRecommendationHistoryDays)
		}
		if (-not $PSBoundParameters.ContainsKey("HistoryMatches") -and
			-not [string]::IsNullOrWhiteSpace([string]$configRoot.LocalRecommendationHistoryMatches)) {
			$HistoryMatches = [Math]::Max(0, [int]$configRoot.LocalRecommendationHistoryMatches)
		}
	} catch {
		Write-Warning "读取插件推荐口径失败，将使用安全默认值。"
	}
}
if ([string]::IsNullOrWhiteSpace($PrimaryTimeRange)) {
	$PrimaryTimeRange = "LAST_7_DAYS"
}
if ([string]::IsNullOrWhiteSpace($RemoteRankRange)) {
	$RemoteRankRange = "DIAMOND_THROUGH_LEGEND"
}
$logDirectory = Join-Path $DataDirectory "Logs"
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$logPath = Join-Path $logDirectory ("refresh-" + (Get-Date).ToString("yyyyMMdd-HHmmss") + ".log")

function Get-MetaCompanionPublicPatchVersion([string]$Value) {
	if ([string]::IsNullOrWhiteSpace($Value)) {
		return ""
	}
	$match = [regex]::Match($Value, "\b(\d+\.\d+\.\d+)(?:\.\d+)?\b")
	if ($match.Success) {
		return $match.Groups[1].Value
	}
	return $Value.Trim()
}

function Test-MetaCompanionCurrentPatchRange([string]$Value) {
	return [string]::Equals(
		$Value,
		"CURRENT_PATCH",
		[StringComparison]::OrdinalIgnoreCase)
}

function Test-PremiumTimeRangeSupportsAllEndpoints([string]$Value) {
	return @(
		"LAST_30_DAYS",
		"CURRENT_PATCH",
		"CURRENT_EXPANSION",
		"CURRENT_SEASON"
	) -contains ([string]$Value).Trim().ToUpperInvariant()
}

function ConvertTo-MetaCompanionInstant([object]$Value) {
	if ($Value -is [DateTimeOffset]) {
		return [DateTimeOffset]$Value
	}
	if ($Value -is [DateTime]) {
		$dateTime = [DateTime]$Value
		if ($dateTime.Kind -eq [DateTimeKind]::Unspecified) {
			return $null
		}
		return [DateTimeOffset]$dateTime
	}

	$text = if ($null -eq $Value) { "" } else { ([string]$Value).Trim() }
	if ([string]::IsNullOrWhiteSpace($text) -or
		$text -notmatch '(?i)(Z|[+-]\d{2}:\d{2})$') {
		return $null
	}
	$result = [DateTimeOffset]::MinValue
	if ([DateTimeOffset]::TryParse(
			$text,
			[Globalization.CultureInfo]::InvariantCulture,
			[Globalization.DateTimeStyles]::RoundtripKind,
			[ref]$result)) {
		return $result
	}
	return $null
}

function Test-MetaCompanionPublishedSnapshot([string]$Directory) {
	$manifestPath = Join-Path $Directory "manifest.json"
	$completionPath = Join-Path $Directory "publish-complete.json"
	if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
		-not (Test-Path -LiteralPath $completionPath -PathType Leaf)) {
		return $false
	}
	try {
		$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 |
			ConvertFrom-Json -ErrorAction Stop
		$completion = Get-Content -LiteralPath $completionPath -Raw -Encoding UTF8 |
			ConvertFrom-Json -ErrorAction Stop
		$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
		if ([string]::IsNullOrWhiteSpace([string]$manifest.run_id) -or
			[string]$completion.run_id -ne [string]$manifest.run_id -or
			[string]$completion.manifest_sha256 -ne $manifestHash) {
			return $false
		}
		if (-not [string]::IsNullOrWhiteSpace([string]$manifest.output_sha256) -and
			[string]$completion.output_sha256 -ne [string]$manifest.output_sha256) {
			return $false
		}
		return $true
	} catch {
		return $false
	}
}

function Test-RemoteCacheRefreshedToday(
	[string]$Root,
	[string[]]$ExpectedMetaTimeRanges,
	[string[]]$ExpectedPremiumTimeRanges,
	[string]$ExpectedRankRange,
	[datetime]$PatchTime = [datetime]::MinValue,
	[string]$ExpectedPatchVersion = ""
) {
	$requiredPaths = @(
		(Join-Path $Root "hsreplay_deckcodes.txt"),
		(Join-Path $Root "archetype_model_branches.tsv"),
		(Join-Path $Root "Premium\latest\manifest.json"),
		(Join-Path $Root "Premium\latest\publish-complete.json"),
		(Join-Path $Root "Premium\Meta\latest\summary.json"),
		(Join-Path $Root "Premium\Meta\latest\head_to_head_archetype_matchups_v2.json"),
		(Join-Path $Root "Premium\Meta\latest\manifest.json"),
		(Join-Path $Root "Premium\Meta\latest\publish-complete.json"),
		(Join-Path $Root "Premium\ModelBranches\latest\manifest.json"),
		(Join-Path $Root "Premium\ModelBranches\latest\publish-complete.json")
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
		if (-not (Test-MetaCompanionPublishedSnapshot (Join-Path $Root "Premium\latest")) -or
			-not (Test-MetaCompanionPublishedSnapshot (Join-Path $Root "Premium\Meta\latest")) -or
			-not (Test-MetaCompanionPublishedSnapshot (Join-Path $Root "Premium\ModelBranches\latest"))) {
			return $false
		}
		$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
		$timeRange = [string]$manifest.selected_time_range
		if ([string]::IsNullOrWhiteSpace($timeRange)) {
			$timeRange = [string]$manifest.time_range
		}
		$metaTimeRangeMatches = @($ExpectedMetaTimeRanges | Where-Object {
			[string]::Equals(
				$timeRange,
				[string]$_,
				[StringComparison]::OrdinalIgnoreCase)
		}).Count -gt 0
		if (-not $metaTimeRangeMatches -or
			-not [string]::Equals(
				[string]$manifest.rank_range,
				$ExpectedRankRange,
				[StringComparison]::OrdinalIgnoreCase)) {
			return $false
		}

		$premiumManifestPath = Join-Path $Root "Premium\latest\manifest.json"
		$premiumManifest = Get-Content -LiteralPath $premiumManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
		$premiumTimeRangeMatches = @($ExpectedPremiumTimeRanges | Where-Object {
			[string]::Equals(
				[string]$premiumManifest.time_range,
				[string]$_,
				[StringComparison]::OrdinalIgnoreCase)
		}).Count -gt 0
		if (-not $premiumTimeRangeMatches -or
			-not [string]::Equals(
				[string]$premiumManifest.rank_range,
				$ExpectedRankRange,
				[StringComparison]::OrdinalIgnoreCase)) {
			return $false
		}

		$modelBranchManifestPath = Join-Path $Root "Premium\ModelBranches\latest\manifest.json"
		$modelBranchManifest = Get-Content -LiteralPath $modelBranchManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
		if (-not [string]::Equals(
			[string]$modelBranchManifest.candidate_time_range,
			$ModelBranchTimeRange,
			[StringComparison]::OrdinalIgnoreCase) -or
			-not [string]::Equals(
				[string]$modelBranchManifest.rank_range,
				$ExpectedRankRange,
				[StringComparison]::OrdinalIgnoreCase)) {
			return $false
		}
		if (-not [string]::IsNullOrWhiteSpace($ExpectedPatchVersion)) {
			$expectedPublicPatch = Get-MetaCompanionPublicPatchVersion $ExpectedPatchVersion
			$summaryPath = Join-Path $Root "Premium\Meta\latest\summary.json"
			$summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
			if (-not [string]::Equals(
					(Get-MetaCompanionPublicPatchVersion ([string]$manifest.patch_version)),
					$expectedPublicPatch,
					[StringComparison]::OrdinalIgnoreCase) -or
				-not [string]::Equals(
					(Get-MetaCompanionPublicPatchVersion ([string]$summary.patch_version)),
					$expectedPublicPatch,
					[StringComparison]::OrdinalIgnoreCase) -or
				-not [string]::Equals(
					(Get-MetaCompanionPublicPatchVersion ([string]$modelBranchManifest.patch_version)),
					$expectedPublicPatch,
					[StringComparison]::OrdinalIgnoreCase)) {
				return $false
			}
		}

		if ($PatchTime -ne [datetime]::MinValue) {
			$minimumAsOf = [DateTimeOffset]$PatchTime
			if (-not (Test-MetaCompanionCurrentPatchRange $timeRange)) {
				$summaryPath = Join-Path $Root "Premium\Meta\latest\summary.json"
				$matrixPath = Join-Path $Root "Premium\Meta\latest\head_to_head_archetype_matchups_v2.json"
				$summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
				$matrix = Get-Content -LiteralPath $matrixPath -Raw -Encoding UTF8 | ConvertFrom-Json
				$summaryAsOf = ConvertTo-MetaCompanionInstant $summary.as_of
				$matrixAsOf = ConvertTo-MetaCompanionInstant $matrix.as_of
				if ($null -eq $summaryAsOf -or $summaryAsOf -lt $minimumAsOf -or
					$null -eq $matrixAsOf -or $matrixAsOf -lt $minimumAsOf) {
					return $false
				}
			}

			$modelBranchTimeRange = [string]$modelBranchManifest.candidate_time_range
			if (-not (Test-MetaCompanionCurrentPatchRange $modelBranchTimeRange)) {
				$modelBranchAsOf = ConvertTo-MetaCompanionInstant $modelBranchManifest.candidate.as_of
				if ($null -eq $modelBranchAsOf -or $modelBranchAsOf -lt $minimumAsOf) {
					return $false
				}
			}
		}
		return $true
	} catch {
		return $false
	}
}

function Test-MetaCacheMatchesEpoch(
	[string]$Root,
	[string]$ExpectedTimeRange,
	[string]$ExpectedRankRange,
	[datetime]$PatchTime = [datetime]::MinValue,
	[string]$ExpectedPatchVersion = ""
) {
	$latestDirectory = Join-Path $Root "Premium\Meta\latest"
	$manifestPath = Join-Path $latestDirectory "manifest.json"
	$summaryPath = Join-Path $latestDirectory "summary.json"
	$matrixPath = Join-Path $latestDirectory "head_to_head_archetype_matchups_v2.json"
	$completionPath = Join-Path $latestDirectory "publish-complete.json"
	foreach ($requiredPath in @($manifestPath, $summaryPath, $matrixPath, $completionPath)) {
		if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
			return $false
		}
	}

	try {
		if (-not (Test-MetaCompanionPublishedSnapshot $latestDirectory)) {
			return $false
		}
		$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
		$timeRange = [string]$manifest.selected_time_range
		if ([string]::IsNullOrWhiteSpace($timeRange)) {
			$timeRange = [string]$manifest.time_range
		}
		if (-not [string]::Equals($timeRange, $ExpectedTimeRange, [StringComparison]::OrdinalIgnoreCase)) {
			return $false
		}
		if (-not [string]::Equals(
			[string]$manifest.rank_range,
			$ExpectedRankRange,
			[StringComparison]::OrdinalIgnoreCase)) {
			return $false
		}
		if (-not [string]::IsNullOrWhiteSpace($ExpectedPatchVersion)) {
			$expectedPublicPatch = Get-MetaCompanionPublicPatchVersion $ExpectedPatchVersion
			$manifestPublicPatch = Get-MetaCompanionPublicPatchVersion ([string]$manifest.patch_version)
			$summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
			$summaryPublicPatch = Get-MetaCompanionPublicPatchVersion ([string]$summary.patch_version)
			if (-not [string]::Equals(
				$manifestPublicPatch,
				$expectedPublicPatch,
				[StringComparison]::OrdinalIgnoreCase) -or
				-not [string]::Equals(
				$summaryPublicPatch,
				$expectedPublicPatch,
				[StringComparison]::OrdinalIgnoreCase)) {
				return $false
			}
		}

		if ($PatchTime -ne [datetime]::MinValue -and
			-not (Test-MetaCompanionCurrentPatchRange $timeRange)) {
			$summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
			$matrix = Get-Content -LiteralPath $matrixPath -Raw -Encoding UTF8 | ConvertFrom-Json
			$minimumAsOf = [DateTimeOffset]$PatchTime
			$summaryAsOf = ConvertTo-MetaCompanionInstant $summary.as_of
			$matrixAsOf = ConvertTo-MetaCompanionInstant $matrix.as_of
			if ($null -eq $summaryAsOf -or $summaryAsOf -lt $minimumAsOf -or
				$null -eq $matrixAsOf -or $matrixAsOf -lt $minimumAsOf) {
				return $false
			}
		}
		return $true
	} catch {
		return $false
	}
}

$refreshOutcome = "FAILED"
Start-Transcript -Path $logPath | Out-Null
try {
	Set-Location $repoRoot
	$patchState = $null
	if (Get-Command Update-MetaCompanionPatchState -ErrorAction SilentlyContinue) {
		$patchState = Update-MetaCompanionPatchState -DataDirectory $DataDirectory
		if ($patchState.PatchChanged) {
			Write-Host "检测到新的炉石传说补丁 $($patchState.PatchVersion)；已归档 $($patchState.ArchivedFileCount) 个当前本地数据文件。"
		}
	}
	$patchTime = if ($patchState -and $patchState.PatchTime) { $patchState.PatchTime } else { [datetime]::MinValue }
	$expectedPatchVersion = if ($patchState) { [string]$patchState.PatchVersion } else { "" }
	if (-not $Force -and (Test-RemoteCacheRefreshedToday `
		-Root $DataDirectory `
		-ExpectedMetaTimeRanges @($PrimaryTimeRange, $MetaFallbackTimeRange, "CURRENT_PATCH") `
		-ExpectedPremiumTimeRanges @($PrimaryTimeRange, $PremiumFallbackTimeRange) `
		-ExpectedRankRange $RemoteRankRange `
		-PatchTime $patchTime `
		-ExpectedPatchVersion $expectedPatchVersion)) {
		Write-Host "远端缓存今天已经刷新完成，已跳过。如需强制刷新，请使用 -Force。"
		$refreshOutcome = "SKIPPED"
		return
	}

	$updateScript = Join-Path $PSScriptRoot "Update-MetaCompanionData.ps1"
	$premiumSyncScript = Join-Path $PSScriptRoot "Sync-HSReplayPremiumData.ps1"
	$metaSyncScript = Join-Path $PSScriptRoot "Sync-HSReplayMetaData.ps1"
	$branchSyncScript = Join-Path $PSScriptRoot "Sync-HSReplayArchetypeDecks.ps1"
	$cookiePath = Join-Path $DataDirectory "hsreplay_cookie.txt"
	$deckCodePath = Join-Path $DataDirectory "hsreplay_deckcodes.txt"
	$premiumOutputDirectory = Join-Path $DataDirectory "Premium"
	$metaOutputDirectory = Join-Path $premiumOutputDirectory "Meta"
	$branchCacheDirectory = Join-Path $premiumOutputDirectory "Branches"
	$modelBranchCacheDirectory = Join-Path $premiumOutputDirectory "ModelBranches"
	$metaSummaryPath = Join-Path (Join-Path $metaOutputDirectory "latest") "summary.tsv"
	$branchOutputPath = Join-Path $DataDirectory "archetype_deck_branches.tsv"
	$modelBranchOutputPath = Join-Path $DataDirectory "archetype_model_branches.tsv"

	function Invoke-MetaCompanionPremiumStage(
		[string]$TimeRange,
		[bool]$StopOnUnsupported,
		[bool]$RefreshDeckSnapshot
	) {
		if ($RefreshDeckSnapshot) {
			$premiumArgs = @{
				Premium = $true
				PremiumTimeRange = $TimeRange
				RemoteRankRange = $RemoteRankRange
				PremiumMaxDecks = $PremiumMaxDecks
				PremiumCookiePath = $cookiePath
				OutputPath = $deckCodePath
				Parallelism = $Parallelism
			}
			if ($StopOnUnsupported) {
				$premiumArgs.PremiumStopOnUnsupported = $true
			}
			& $updateScript @premiumArgs
			return
		}

		$premiumArgs = @{
			CookiePath = $cookiePath
			DeckCodePath = $deckCodePath
			OutputDirectory = $premiumOutputDirectory
			TimeRange = $TimeRange
			RankRange = $RemoteRankRange
			MaxDecks = $PremiumMaxDecks
		}
		if ($StopOnUnsupported) {
			$premiumArgs.StopOnUnsupported = $true
		}
		& $premiumSyncScript @premiumArgs
	}

	function Invoke-MetaCompanionMetaStage([string]$TimeRange) {
		$metaArgs = @{
			CookiePath = $cookiePath
			OutputDirectory = $metaOutputDirectory
			DataDirectory = $DataDirectory
			TimeRange = $TimeRange
			RankRange = $RemoteRankRange
		}
		if ($patchTime -ne [datetime]::MinValue -and
			-not (Test-MetaCompanionCurrentPatchRange $TimeRange)) {
			$metaArgs.MinimumAsOf = [DateTimeOffset]$patchTime
		}
		if (-not [string]::IsNullOrWhiteSpace($expectedPatchVersion)) {
			$metaArgs.PatchVersion = $expectedPatchVersion
		}
		& $metaSyncScript @metaArgs
		if (-not (Test-MetaCacheMatchesEpoch `
			-Root $DataDirectory `
			-ExpectedTimeRange $TimeRange `
			-ExpectedRankRange $RemoteRankRange `
			-PatchTime $patchTime `
			-ExpectedPatchVersion $expectedPatchVersion)) {
			throw "Meta 阶段返回后未通过范围与补丁 epoch 校验；生产 latest 不可用于后续计算。"
		}
	}

	function Invoke-MetaCompanionBranchStage(
		[string]$TimeRange,
		[string]$OutputPath,
		[string]$CacheDirectory
	) {
		$branchArgs = @{
			CookiePath = $cookiePath
			SummaryPath = $metaSummaryPath
			OutputPath = $OutputPath
			CacheDirectory = $CacheDirectory
			CandidateTimeRange = $TimeRange
			RankRange = $RemoteRankRange
			BranchesPerArchetype = $BranchesPerArchetype
			MinGames = $BranchMinGames
			Parallelism = $Parallelism
		}
		if ($patchTime -ne [datetime]::MinValue -and
			-not (Test-MetaCompanionCurrentPatchRange $TimeRange)) {
			$branchArgs.MinimumAsOf = [DateTimeOffset]$patchTime
		}
		if (-not [string]::IsNullOrWhiteSpace($expectedPatchVersion)) {
			$branchArgs.PatchVersion = $expectedPatchVersion
		}
		& $branchSyncScript @branchArgs
	}

	function Invoke-MetaCompanionRecommendationRun([string]$MetaTimeRange) {
		& $updateScript `
			-Recommendations `
			-PersonalRecommendations `
			-LocalMeta `
			-MetaTimeRange $MetaTimeRange `
			-RemoteRankRange $RemoteRankRange `
			-RecommendationTop $RecommendationTop `
			-PersonalRecommendationHistoryDays $HistoryDays `
			-PersonalRecommendationHistoryMatches $HistoryMatches `
			-PersonalRecommendationLocalWeight $LocalWeight `
			-LocalMetaMinConfidence $LocalMetaMinConfidence `
			-OutputPath $deckCodePath
	}

	function Invoke-MetaCompanionCachedRecommendationRun([string]$MetaTimeRange) {
		Write-Warning "Premium / 环境数据刷新失败；将使用现有缓存重新计算推荐。"
		Invoke-MetaCompanionRecommendationRun -MetaTimeRange $MetaTimeRange
	}

	function Get-MetaCompanionReusableCacheTimeRange([string[]]$Candidates) {
		foreach ($candidate in @($Candidates | Select-Object -Unique)) {
			if ([string]::IsNullOrWhiteSpace($candidate)) {
				continue
			}
			if (Test-MetaCacheMatchesEpoch `
				-Root $DataDirectory `
				-ExpectedTimeRange $candidate `
				-ExpectedRankRange $RemoteRankRange `
				-PatchTime $patchTime `
				-ExpectedPatchVersion $expectedPatchVersion) {
				return $candidate
			}
		}
		return ""
	}

	$premiumSucceeded = $false
	$premiumPrimaryStrict = Test-PremiumTimeRangeSupportsAllEndpoints $PrimaryTimeRange
	try {
		Invoke-MetaCompanionPremiumStage `
			-TimeRange $PrimaryTimeRange `
			-StopOnUnsupported $premiumPrimaryStrict `
			-RefreshDeckSnapshot $true
		$premiumSucceeded = $true
	} catch {
		Write-Warning "Premium 主刷新使用 $PrimaryTimeRange 失败：$($_.Exception.Message)"
		Write-Warning "仅以兼容模式重试 Premium 阶段，时间范围=$PremiumFallbackTimeRange，并复用现有牌组快照。"
		try {
			Invoke-MetaCompanionPremiumStage `
				-TimeRange $PremiumFallbackTimeRange `
				-StopOnUnsupported $false `
				-RefreshDeckSnapshot $false
			$premiumSucceeded = $true
		} catch {
			Write-Warning "Premium 兜底刷新失败：$($_.Exception.Message)"
		}
	}
	if (-not $premiumSucceeded) {
		Write-Warning "Premium 数据未刷新；后续 Meta 阶段将继续，现有 Premium latest 保持不变。"
	}

	$metaSucceeded = $false
	$effectiveMetaTimeRange = $PrimaryTimeRange
	$metaTimeRangeCandidates = New-Object System.Collections.Generic.List[string]
	$candidateValues = if (Test-MetaCompanionCurrentPatchRange $PrimaryTimeRange) {
		@($PrimaryTimeRange)
	} else {
		@($PrimaryTimeRange, $MetaFallbackTimeRange, "CURRENT_PATCH")
	}
	foreach ($candidateValue in $candidateValues) {
		if ([string]::IsNullOrWhiteSpace($candidateValue) -or
			@($metaTimeRangeCandidates | Where-Object {
				[string]::Equals($_, $candidateValue, [StringComparison]::OrdinalIgnoreCase)
			}).Count -gt 0) {
			continue
		}
		$metaTimeRangeCandidates.Add($candidateValue.Trim())
	}

	foreach ($candidateTimeRange in $metaTimeRangeCandidates) {
		if (-not [string]::Equals(
				$candidateTimeRange,
				$PrimaryTimeRange,
				[StringComparison]::OrdinalIgnoreCase)) {
			if (Test-MetaCompanionCurrentPatchRange $candidateTimeRange) {
				Write-Warning "滚动时间范围无法确认当前补丁边界；改用 HSReplay CURRENT_PATCH 远端环境。"
			} else {
				Write-Warning "仅重试 Meta 阶段，兜底时间范围=$candidateTimeRange。"
			}
		}
		try {
			Invoke-MetaCompanionMetaStage -TimeRange $candidateTimeRange
			$effectiveMetaTimeRange = $candidateTimeRange
			$metaSucceeded = $true
			break
		} catch {
			$scope = if ([string]::Equals(
					$candidateTimeRange,
					$PrimaryTimeRange,
					[StringComparison]::OrdinalIgnoreCase)) { "主" } else { "兜底" }
			Write-Warning "Meta $scope 刷新使用 $candidateTimeRange 失败：$($_.Exception.Message)"
		}
	}
	if (-not $metaSucceeded) {
		$reusableTimeRange = Get-MetaCompanionReusableCacheTimeRange $metaTimeRangeCandidates.ToArray()
		if (-not [string]::IsNullOrWhiteSpace($reusableTimeRange)) {
			Write-Warning "HSReplay 新结果暂不可用；生产 latest 未被覆盖，将使用通过范围与补丁 epoch 校验的 $reusableTimeRange 缓存。"
			Invoke-MetaCompanionCachedRecommendationRun -MetaTimeRange $reusableTimeRange
			$refreshOutcome = "COMPLETED_WITH_CACHED_META"
		} else {
			Write-Warning "HSReplay 数据仍在生成，且现有 Meta latest 未通过范围与补丁 epoch 校验；本次停止分支与推荐刷新，请稍后重试。"
			$refreshOutcome = "DEFERRED"
		}
		return
	}

	if (-not $SkipBranches) {
		$branchSucceeded = $false
		try {
			Invoke-MetaCompanionBranchStage `
				-TimeRange $effectiveMetaTimeRange `
				-OutputPath $branchOutputPath `
				-CacheDirectory $branchCacheDirectory
			$branchSucceeded = $true
		} catch {
			Write-Warning "分支主刷新使用 $effectiveMetaTimeRange 失败：$($_.Exception.Message)"
		}
		if (-not $branchSucceeded) {
			Write-Warning "同口径代表卡组未刷新；将保留旧文件但不会把不同范围的代码显示到复制按钮。"
		}

		$modelBranchSucceeded = $false
		try {
			Invoke-MetaCompanionBranchStage `
				-TimeRange $ModelBranchTimeRange `
				-OutputPath $modelBranchOutputPath `
				-CacheDirectory $modelBranchCacheDirectory
			$modelBranchSucceeded = $true
		} catch {
			Write-Warning "本地识别牌组语料刷新使用 $ModelBranchTimeRange 失败：$($_.Exception.Message)"
		}
		if (-not $modelBranchSucceeded) {
			Write-Warning "本地识别将继续使用现有当前补丁语料或公共牌组库存，不影响远端分段推荐排序。"
		}
	}

	Invoke-MetaCompanionRecommendationRun -MetaTimeRange $effectiveMetaTimeRange
	$refreshOutcome = "COMPLETED"
}
finally {
	Write-Host "META_COMPANION_REFRESH_OUTCOME=$refreshOutcome"
	Stop-Transcript | Out-Null
	Write-Host "刷新日志：$logPath"
}
