param(
	[switch]$Full,
	[switch]$Premium,
	[switch]$Meta,
	[switch]$Branches,
	[switch]$Recommendations,
	[switch]$PersonalRecommendations,
	[switch]$LocalMeta,
	[switch]$SkipPersonalRecommendations,
	[string[]]$RankRanges = @(),
	[int]$LimitPerRange = 0,
	[int]$MaxDecks = 0,
	[int]$DeckPageTimeoutSeconds = 12,
	[int]$Parallelism = 1,
	[string]$PremiumCookiePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt",
	[string]$PremiumTimeRange = "LAST_7_DAYS",
	[int]$PremiumMaxDecks = 30,
	[switch]$PremiumStopOnUnsupported,
	[string]$MetaTimeRange = "LAST_7_DAYS",
	[string]$RemoteRankRange = "DIAMOND_THROUGH_LEGEND",
	[int]$MetaTopOverall = 20,
	[int]$MetaTopPerClass = 5,
	[int]$RecommendationTop = 20,
	[int]$RecommendationMinMatchupGames = 200,
	[double]$RecommendationMinCoveragePct = 50,
	[int]$PersonalRecommendationHistoryDays = 3,
	[int]$PersonalRecommendationHistoryMatches = 0,
	[double]$PersonalRecommendationLocalWeight = 0.35,
	[int]$LocalMetaMinConfidence = 35,
	[datetime]$PatchTime = [datetime]::MinValue,
	[double]$PrePatchWeight = 0.0,
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
$patchStateScript = Join-Path $PSScriptRoot "Update-MetaCompanionPatchState.ps1"
$hdtHistoryExportScript = Join-Path $PSScriptRoot "Export-HdtOpponentHistory.ps1"
$localMetaScript = Join-Path $PSScriptRoot "Measure-HdtLocalMeta.ps1"
$recommendationScript = Join-Path $PSScriptRoot "Get-MetaArchetypeRecommendations.ps1"
$personalRecommendationScript = Join-Path $PSScriptRoot "Get-PersonalMetaRecommendations.ps1"
$verifyScript = Join-Path $PSScriptRoot "Verify-DeckCodeImport.ps1"
$defaultRankRanges = @($RemoteRankRange)
$fullRankRanges = @(
	"DIAMOND_THROUGH_LEGEND",
	"DIAMOND_FOUR_THROUGH_DIAMOND_ONE",
	"PLATINUM",
	"GOLD",
	"BRONZE_THROUGH_GOLD"
)
$recommendationsOnly = ($Recommendations -or $PersonalRecommendations -or $LocalMeta) -and
	-not $Full -and -not $Premium -and -not $Meta -and
	-not $Branches -and $RankRanges.Count -eq 0
$dataDirectory = Split-Path -Parent $OutputPath
$representativeBranchPath = Join-Path $dataDirectory "archetype_deck_branches.tsv"
$modelBranchPath = Join-Path $dataDirectory "archetype_model_branches.tsv"

if (Test-Path -LiteralPath $patchStateScript) {
	. $patchStateScript
}

function Try-ParseDate([string]$Value) {
	$result = [DateTime]::MinValue
	if ([DateTime]::TryParse($Value, [ref]$result)) {
		return $result
	}
	return $null
}

function Get-OptionalDateMarker([string]$Path) {
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
		return $null
	}
	$parsed = Try-ParseDate ((Get-Content -LiteralPath $Path -Raw -Encoding UTF8).Trim())
	if (-not $parsed) {
		throw "本地样本时间标记无效：$Path"
	}
	return $parsed
}

function Get-LatestDate([object[]]$Values) {
	$dates = @($Values | Where-Object { $null -ne $_ -and $_ -ne [datetime]::MinValue })
	if ($dates.Count -eq 0) {
		return $null
	}
	return @($dates | Sort-Object -Descending)[0]
}

function ConvertTo-RequiredMetaInstant([object]$Value, [string]$Name) {
	if ($Value -is [DateTimeOffset]) {
		return [DateTimeOffset]$Value
	}
	if ($Value -is [DateTime]) {
		$dateTime = [DateTime]$Value
		if ($dateTime.Kind -eq [DateTimeKind]::Unspecified) {
			throw "环境缓存 $Name 的 as_of 缺少明确时区；无法确认它属于当前补丁。"
		}
		return [DateTimeOffset]$dateTime
	}

	$text = if ($null -eq $Value) { "" } else { ([string]$Value).Trim() }
	$result = [DateTimeOffset]::MinValue
	if ([string]::IsNullOrWhiteSpace($text) -or
		$text -notmatch '(?i)(Z|[+-]\d{2}:\d{2})$' -or
		-not [DateTimeOffset]::TryParse(
			$text,
			[Globalization.CultureInfo]::InvariantCulture,
			[Globalization.DateTimeStyles]::RoundtripKind,
			[ref]$result)) {
		throw "环境缓存 $Name 缺少有效且带时区的 as_of；无法确认它属于当前补丁。"
	}
	return $result
}

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

function Assert-MetaCacheScope {
	param(
		[string]$DataDirectory,
		[string]$MetaDirectory,
		[string]$ExpectedTimeRange,
		[string]$ExpectedRankRange
	)

	$summaryPath = Join-Path $MetaDirectory "summary.json"
	$matrixPath = Join-Path $MetaDirectory "head_to_head_archetype_matchups_v2.json"
	$manifestPath = Join-Path $MetaDirectory "manifest.json"
	$completionPath = Join-Path $MetaDirectory "publish-complete.json"
	if (-not (Test-Path -LiteralPath $summaryPath) -or
		-not (Test-Path -LiteralPath $matrixPath) -or
		-not (Test-Path -LiteralPath $manifestPath) -or
		-not (Test-Path -LiteralPath $completionPath)) {
		throw "环境缓存尚未就绪；请先完成一次远端刷新。"
	}

	$summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
	$matrix = Get-Content -LiteralPath $matrixPath -Raw -Encoding UTF8 | ConvertFrom-Json
	$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
	$completion = Get-Content -LiteralPath $completionPath -Raw -Encoding UTF8 | ConvertFrom-Json
	$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
	if ([string]::IsNullOrWhiteSpace([string]$manifest.run_id) -or
		[string]$completion.run_id -ne [string]$manifest.run_id -or
		[string]$completion.manifest_sha256 -ne $manifestHash) {
		throw "环境缓存发布标记与 manifest.json 不一致；已拒绝使用可能不完整的数据。"
	}
	if (-not [string]::IsNullOrWhiteSpace($ExpectedTimeRange) -and
		-not ([string]$summary.time_range).Equals(
			$ExpectedTimeRange,
			[StringComparison]::OrdinalIgnoreCase)) {
		throw "环境缓存时间范围是 $($summary.time_range)，目标是 $ExpectedTimeRange；请先刷新远端数据。"
	}
	$manifestTimeRange = [string]$manifest.selected_time_range
	if ([string]::IsNullOrWhiteSpace($manifestTimeRange)) {
		$manifestTimeRange = [string]$manifest.time_range
	}
	if (-not [string]::IsNullOrWhiteSpace($ExpectedTimeRange) -and
		-not $manifestTimeRange.Equals(
			$ExpectedTimeRange,
			[StringComparison]::OrdinalIgnoreCase)) {
		throw "环境缓存 manifest 时间范围是 $manifestTimeRange，目标是 $ExpectedTimeRange；请先刷新远端数据。"
	}
	if (-not [string]::IsNullOrWhiteSpace($ExpectedRankRange) -and
		-not ([string]$summary.rank_range).Equals(
			$ExpectedRankRange,
			[StringComparison]::OrdinalIgnoreCase)) {
		throw "环境缓存段位范围是 $($summary.rank_range)，目标是 $ExpectedRankRange；请先刷新远端数据。"
	}
	if (-not [string]::IsNullOrWhiteSpace($ExpectedRankRange) -and
		-not ([string]$manifest.rank_range).Equals(
			$ExpectedRankRange,
			[StringComparison]::OrdinalIgnoreCase)) {
		throw "环境缓存 manifest 段位范围是 $($manifest.rank_range)，目标是 $ExpectedRankRange；请先刷新远端数据。"
	}

	if (Test-MetaCompanionCurrentPatchRange $ExpectedTimeRange) {
		$patchVersionPath = Join-Path $DataDirectory "patch_version.txt"
		if (Test-Path -LiteralPath $patchVersionPath -PathType Leaf) {
			$localPatchVersion = Get-MetaCompanionPublicPatchVersion (
				(Get-Content -LiteralPath $patchVersionPath -Raw -Encoding UTF8).Trim())
			$manifestPatchVersion = Get-MetaCompanionPublicPatchVersion ([string]$manifest.patch_version)
			$summaryPatchVersion = Get-MetaCompanionPublicPatchVersion ([string]$summary.patch_version)
			if ([string]::IsNullOrWhiteSpace($localPatchVersion) -or
				-not [string]::Equals(
					$manifestPatchVersion,
					$localPatchVersion,
					[StringComparison]::OrdinalIgnoreCase) -or
				-not [string]::Equals(
					$summaryPatchVersion,
					$localPatchVersion,
					[StringComparison]::OrdinalIgnoreCase)) {
				throw "当前补丁环境缓存的版本身份与本机补丁不一致；请先刷新远端数据。"
			}
		}
		return
	}

	$patchMarkerPath = Join-Path $DataDirectory "patch_marker.txt"
	if (-not (Test-Path -LiteralPath $patchMarkerPath)) {
		return
	}
	$markerText = (Get-Content -LiteralPath $patchMarkerPath -Raw -Encoding UTF8).Trim()
	$marker = [DateTimeOffset]::MinValue
	if (-not [DateTimeOffset]::TryParse($markerText, [ref]$marker)) {
		throw "补丁时间标记无效：$patchMarkerPath"
	}

	foreach ($cache in @(
		[pscustomobject]@{ Name = "summary"; Value = $summary.as_of },
		[pscustomobject]@{ Name = "head-to-head matrix"; Value = $matrix.as_of }
	)) {
		$asOf = ConvertTo-RequiredMetaInstant $cache.Value $cache.Name
		if ($asOf -lt $marker) {
			throw "环境缓存 $($cache.Name) 早于当前补丁起点 $($marker.ToString('o'))；已拒绝用旧远端数据计算推荐。"
		}
	}
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

	$patchMarkerPath = Join-Path $dataDirectory "patch_marker.txt"
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

$patchState = $null
if (Get-Command Update-MetaCompanionPatchState -ErrorAction SilentlyContinue) {
	$patchState = Update-MetaCompanionPatchState -DataDirectory $dataDirectory -PatchTime $PatchTime
	if ($patchState.PatchChanged) {
		Write-Host "检测到新的炉石传说补丁 $($patchState.PatchVersion)；已归档 $($patchState.ArchivedFileCount) 个当前本地数据文件。"
	}
}
$remoteMinimumAsOf = if ($patchState -and $patchState.PatchTime) {
	[datetime]$patchState.PatchTime
} elseif ($PatchTime -ne [datetime]::MinValue) {
	$PatchTime
} else {
	[datetime]::MinValue
}
$remotePatchVersion = if ($patchState) { [string]$patchState.PatchVersion } else { "" }

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
	$rankRanges = $fullRankRanges
	if ($LimitPerRange -le 0) {
		$limitPerRange = 250
	}
	if ($MaxDecks -le 0) {
		$maxDecks = 500
	}
} else {
	$rankRanges = $defaultRankRanges
	if ($LimitPerRange -le 0) {
		$limitPerRange = 250
	}
	if ($MaxDecks -le 0) {
		$maxDecks = 500
	}
}

if (-not $recommendationsOnly) {
	Write-Host "正在刷新 Meta Companion 数据..."
	& $syncScript `
		-RankRanges $rankRanges `
		-LimitPerRange $limitPerRange `
		-MaxDecks $maxDecks `
		-DeckPageTimeoutSeconds $DeckPageTimeoutSeconds `
		-Parallelism $Parallelism `
		-CookiePath $PremiumCookiePath `
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
		Write-Host "正在验证导入的牌组代码..."
		& $verifyScript -DeckCodePath $OutputPath -HdtAppPath $hdtAppPath
	}
} else {
	Write-Host "已跳过牌组代码同步；将使用现有环境缓存重新计算推荐。"
}

if ($Premium) {
	if (-not (Test-Path $premiumSyncScript)) {
		throw "未找到 Premium 数据同步脚本：$premiumSyncScript"
	}

	Write-Host ""
	Write-Host "正在刷新 HSReplay Premium 分析缓存..."
	$premiumArgs = @{
		CookiePath = $PremiumCookiePath
		DeckCodePath = $OutputPath
		TimeRange = $PremiumTimeRange
		RankRange = $RemoteRankRange
		MaxDecks = $PremiumMaxDecks
	}
	if ($PremiumStopOnUnsupported) {
		$premiumArgs.StopOnUnsupported = $true
	}
	& $premiumSyncScript @premiumArgs
}

if ($Meta) {
	if (-not (Test-Path $metaSyncScript)) {
		throw "未找到环境数据同步脚本：$metaSyncScript"
	}

	Write-Host ""
	Write-Host "正在刷新 HSReplay 环境分析缓存..."
	$metaArgs = @{
		CookiePath = $PremiumCookiePath
		OutputDirectory = (Join-Path $dataDirectory "Premium\Meta")
		DataDirectory = $dataDirectory
		TimeRange = $MetaTimeRange
		RankRange = $RemoteRankRange
		TopOverall = $MetaTopOverall
		TopPerClass = $MetaTopPerClass
	}
	if ($remoteMinimumAsOf -ne [datetime]::MinValue -and
		-not (Test-MetaCompanionCurrentPatchRange $MetaTimeRange)) {
		$metaArgs.MinimumAsOf = [DateTimeOffset]$remoteMinimumAsOf
	}
	if (-not [string]::IsNullOrWhiteSpace($remotePatchVersion)) {
		$metaArgs.PatchVersion = $remotePatchVersion
	}
	& $metaSyncScript @metaArgs
}

if ($Branches) {
	if (-not (Test-Path $branchSyncScript)) {
		throw "未找到流派分支同步脚本：$branchSyncScript"
	}

	Write-Host ""
	Write-Host "正在刷新 HSReplay 流派牌组分支..."
	$branchArgs = @{
		CookiePath = $PremiumCookiePath
		SummaryPath = (Join-Path $dataDirectory "Premium\Meta\latest\summary.tsv")
		OutputPath = $representativeBranchPath
		CacheDirectory = (Join-Path $dataDirectory "Premium\Branches")
		CandidateTimeRange = $BranchCandidateTimeRange
		RankRange = $RemoteRankRange
		BranchesPerArchetype = $BranchesPerArchetype
		MinGames = $BranchMinGames
		Parallelism = $Parallelism
	}
	if ($remoteMinimumAsOf -ne [datetime]::MinValue -and
		-not (Test-MetaCompanionCurrentPatchRange $BranchCandidateTimeRange)) {
		$branchArgs.MinimumAsOf = [DateTimeOffset]$remoteMinimumAsOf
	}
	if (-not [string]::IsNullOrWhiteSpace($remotePatchVersion)) {
		$branchArgs.PatchVersion = $remotePatchVersion
	}
	& $branchSyncScript @branchArgs
}

if ($Meta -or $Recommendations) {
	if (-not (Test-Path $recommendationScript)) {
		throw "未找到流派推荐脚本：$recommendationScript"
	}

	Assert-MetaCacheScope `
		-DataDirectory $dataDirectory `
		-MetaDirectory (Join-Path $dataDirectory "Premium\Meta\latest") `
		-ExpectedTimeRange $MetaTimeRange `
		-ExpectedRankRange $RemoteRankRange

	Write-Host ""
	Write-Host "正在计算流派推荐..."
	& $recommendationScript `
		-Top $RecommendationTop `
		-MinMatchupGames $RecommendationMinMatchupGames `
		-MinCoveragePct $RecommendationMinCoveragePct `
		-IncludeClassTop `
		-UseAllCandidates
}

if ($LocalMeta -or $PersonalRecommendations) {
	if ((Test-Path $hdtHistoryExportScript) -and (Test-Path $localMetaScript) -and
		(Test-Path $OutputPath)) {
		Write-Host ""
		Write-Host "正在统计 HDT 本地对手环境..."
		$effectivePatchTime = Resolve-EffectivePatchTime
		$historyClearedAt = Get-OptionalDateMarker (Join-Path $dataDirectory "local_history_cleared_at.txt")
		$historyDaysStart = if ($PersonalRecommendationHistoryDays -gt 0) {
			(Get-Date).AddDays(-1 * $PersonalRecommendationHistoryDays)
		} else {
			$null
		}
		$historyStart = Get-LatestDate @($effectivePatchTime, $historyDaysStart, $historyClearedAt)
		$historyExportArgs = @{}
		if ($historyStart) {
			$historyExportArgs.Since = $historyStart
		} else {
			$historyExportArgs.Days = 0
		}
		try {
			& $hdtHistoryExportScript @historyExportArgs
			$recognitionBranchPath = if (Test-Path -LiteralPath $modelBranchPath -PathType Leaf) {
				$modelBranchPath
			} else {
				# Migration fallback. Measure-HdtLocalMeta validates the patch epoch and
				# otherwise falls back to the public deck-code inventory.
				$representativeBranchPath
			}
			$localMetaArgs = @{
				DeckCodePath = $OutputPath
				BranchPath = $recognitionBranchPath
				Days = $PersonalRecommendationHistoryDays
				Matches = $PersonalRecommendationHistoryMatches
				MinConfidence = $LocalMetaMinConfidence
				PrePatchWeight = $PrePatchWeight
				PatchMarkerPath = (Join-Path $dataDirectory "patch_marker.txt")
			}
			if ($effectivePatchTime) {
				$localMetaArgs.PatchTime = $effectivePatchTime
			}
			if ($historyClearedAt) {
				$localMetaArgs.HistoryClearedAt = $historyClearedAt
			}
			& $localMetaScript @localMetaArgs
		} catch {
			Write-Warning "HDT 本地环境统计失败，已跳过：$($_.Exception.Message)"
		}
	} else {
		Write-Warning "缺少所需脚本或牌组代码快照，已跳过 HDT 本地环境统计。"
	}
}

if (-not $SkipPersonalRecommendations -and ($Meta -or $Recommendations -or $PersonalRecommendations)) {
	if (-not (Test-Path $personalRecommendationScript)) {
		throw "未找到个人推荐脚本：$personalRecommendationScript"
	}

	Assert-MetaCacheScope `
		-DataDirectory $dataDirectory `
		-MetaDirectory (Join-Path $dataDirectory "Premium\Meta\latest") `
		-ExpectedTimeRange $MetaTimeRange `
		-ExpectedRankRange $RemoteRankRange
	$historyClearedAt = Get-OptionalDateMarker (Join-Path $dataDirectory "local_history_cleared_at.txt")
	$historyClearedAtArgument = if ($historyClearedAt) {
		$historyClearedAt
	} else {
		[datetime]::MinValue
	}

	Write-Host ""
	Write-Host "正在计算个人流派推荐..."
	& $personalRecommendationScript `
		-Top $RecommendationTop `
		-HistoryDays $PersonalRecommendationHistoryDays `
		-HistoryMatches $PersonalRecommendationHistoryMatches `
		-BranchPath $representativeBranchPath `
		-PatchMarkerPath (Join-Path $dataDirectory "patch_marker.txt") `
		-HistoryClearedAt $historyClearedAtArgument `
		-LocalWeight $PersonalRecommendationLocalWeight `
		-MinMatchupGames $RecommendationMinMatchupGames `
		-MinCoveragePct $RecommendationMinCoveragePct `
		-IncludeClassTop
}

Write-Host ""
if ($recommendationsOnly) {
	Write-Host "已使用现有环境缓存重新计算推荐。"
} else {
	Write-Host "数据文件已更新：$OutputPath"
	Write-Host "请重启 HDT，让插件加载新的数据快照。"
}
