param(
	[string]$MetaDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest",
	[string]$OutputPrefix = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\recommendations",
	[int]$Top = 20,
	[int]$MinMatchupGames = 200,
	[double]$MatchupPriorGames = 50,
	[double]$MinCoveragePct = 50,
	[switch]$IncludeClassTop,
	[switch]$UseAllCandidates
)

$ErrorActionPreference = "Stop"

function Get-RequiredJson([string]$Path) {
	if (-not (Test-Path $Path)) {
		throw "缺少必需文件：$Path"
	}
	return Get-Content -Path $Path -Encoding UTF8 -Raw | ConvertFrom-Json
}

function Add-ArchetypesToMap([object]$Archetypes, [hashtable]$Map) {
	$archetypeItems = if ($Archetypes -is [System.Array]) {
		@($Archetypes)
	} elseif ($Archetypes.PSObject.Properties.Name -contains "results") {
		@($Archetypes.results)
	} else {
		@($Archetypes)
	}

	foreach ($item in $archetypeItems) {
		if ($null -eq $item.id) {
			continue
		}
		$Map[[string]([int]$item.id)] = $item
	}
}

function Get-ArchetypeName([int]$Id, [hashtable]$Map) {
	$key = [string]$Id
	if ($Map.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace([string]$Map[$key].name)) {
		return [string]$Map[$key].name
	}
	return "流派 $Id"
}

function Get-ArchetypeClass([int]$Id, [hashtable]$Map) {
	$key = [string]$Id
	if ($Map.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace([string]$Map[$key].player_class_name)) {
		return [string]$Map[$key].player_class_name
	}
	return ""
}

function Get-PropertyValue($Object, [string]$Name) {
	if ($null -eq $Object) {
		return $null
	}
	$property = $Object.PSObject.Properties[$Name]
	if ($null -eq $property) {
		return $null
	}
	return $property.Value
}

function Get-MatchupCell($Matrix, [int]$CandidateId, [int]$OpponentId) {
	$row = Get-PropertyValue $Matrix.series.data ([string]$CandidateId)
	if ($null -eq $row) {
		return $null
	}
	return Get-PropertyValue $row ([string]$OpponentId)
}

function Get-Metadata($Matrix, [int]$ArchetypeId) {
	return Get-PropertyValue $Matrix.series.metadata ([string]$ArchetypeId)
}

function Format-TsvValue($Value) {
	if ($null -eq $Value) {
		return ""
	}
	return ([string]$Value) -replace "[`t`r`n]", " "
}

function Try-ParseDoubleValue($Value, [ref]$Result) {
	$parsed = 0.0
	if ($null -eq $Value -or
		-not [double]::TryParse(
			[string]$Value,
			[Globalization.NumberStyles]::Float,
			[Globalization.CultureInfo]::InvariantCulture,
			[ref]$parsed) -or
		[double]::IsNaN($parsed) -or [double]::IsInfinity($parsed)) {
		return $false
	}
	$Result.Value = $parsed
	return $true
}

$summaryPath = Join-Path $MetaDirectory "summary.json"
$matrixPath = Join-Path $MetaDirectory "head_to_head_archetype_matchups_v2.json"
$archetypesPath = Join-Path $MetaDirectory "archetypes.zh-hans.json"

$summary = Get-RequiredJson $summaryPath
$matrix = Get-RequiredJson $matrixPath
$archetypes = Get-RequiredJson $archetypesPath
$archetypeMap = @{}
Add-ArchetypesToMap $archetypes $archetypeMap

$environmentRows = New-Object System.Collections.Generic.List[object]
if ($summary.PSObject.Properties.Name -contains "all" -and @($summary.all).Count -gt 0) {
	foreach ($row in @($summary.all)) {
		$environmentRows.Add($row)
	}
} else {
	foreach ($row in @($summary.top_overall)) {
		$environmentRows.Add($row)
	}
}
if ($IncludeClassTop -and -not ($summary.PSObject.Properties.Name -contains "all")) {
	foreach ($classProperty in $summary.top_by_class.PSObject.Properties) {
		foreach ($row in @($classProperty.Value)) {
			if (-not ($environmentRows | Where-Object { [int]$_.archetype_id -eq [int]$row.archetype_id })) {
				$environmentRows.Add($row)
			}
		}
	}
}

$environmentRows = @($environmentRows |
	Where-Object { [int]$_.archetype_id -gt 0 -and [double]$_.pct_of_total -gt 0 })
$totalWeight = ($environmentRows | Measure-Object -Property pct_of_total -Sum).Sum
if ($totalWeight -le 0) {
	throw "环境数据中没有可用的 pct_of_total 权重。"
}

$candidateIds = if ($UseAllCandidates) {
	@($matrix.series.metadata.PSObject.Properties.Name |
		Where-Object { $_ -match "^\d+$" } |
		ForEach-Object { [int]$_ })
} else {
	@($environmentRows | Select-Object -ExpandProperty archetype_id -Unique | ForEach-Object { [int]$_ })
}

$effectiveMatchupPriorGames = if ($MatchupPriorGames -gt 0.0 -and
	-not [double]::IsNaN($MatchupPriorGames) -and
	-not [double]::IsInfinity($MatchupPriorGames)) {
	[double]$MatchupPriorGames
} else {
	50.0
}

$recommendations = New-Object System.Collections.Generic.List[object]
foreach ($candidateId in $candidateIds) {
	$weightedWinRate = 0.0
	$coverageWeight = 0.0
	$legacyCoverageWeight = 0.0
	$weightedGames = 0.0
	$matchupsUsed = 0
	$legacyMatchupsUsed = 0
	$candidateMeta = Get-Metadata $matrix $candidateId
	$fallbackWinRate = 50.0
	$parsedFallback = 0.0
	if ($candidateMeta -and
		(Try-ParseDoubleValue $candidateMeta.win_rate ([ref]$parsedFallback)) -and
		$parsedFallback -ge 0.0 -and $parsedFallback -le 100.0) {
		$fallbackWinRate = $parsedFallback
	}

	foreach ($opponent in $environmentRows) {
		$opponentId = [int]$opponent.archetype_id
		$weight = [double]$opponent.pct_of_total / $totalWeight
		$cell = Get-MatchupCell $matrix $candidateId $opponentId
		$observedGames = 0.0
		$observedWinRate = $fallbackWinRate
		$parsedGames = 0.0
		$parsedWinRate = 0.0
		$hasObservedCell = $cell -ne $null -and
			(Try-ParseDoubleValue $cell.total_games ([ref]$parsedGames)) -and
			(Try-ParseDoubleValue $cell.win_rate ([ref]$parsedWinRate)) -and
			$parsedGames -gt 0.0 -and $parsedWinRate -ge 0.0 -and $parsedWinRate -le 100.0
		if ($hasObservedCell) {
			$observedGames = $parsedGames
			$observedWinRate = $parsedWinRate
			$weightedGames += $weight * $observedGames
			$matchupsUsed++
			if ($observedGames -ge $MinMatchupGames) {
				$legacyCoverageWeight += $weight
				$legacyMatchupsUsed++
			}
		}
		$posteriorMean = ($observedGames * $observedWinRate +
			$effectiveMatchupPriorGames * $fallbackWinRate) /
			($observedGames + $effectiveMatchupPriorGames)
		$dataShare = if ($hasObservedCell) {
			$observedGames / ($observedGames + $effectiveMatchupPriorGames)
		} else {
			0.0
		}
		$weightedWinRate += $weight * $posteriorMean
		$coverageWeight += $weight * $dataShare
	}

	$recommendations.Add([pscustomobject][ordered]@{
		archetype_id = $candidateId
		name = Get-ArchetypeName $candidateId $archetypeMap
		player_class = Get-ArchetypeClass $candidateId $archetypeMap
		expected_win_rate_raw = $weightedWinRate
		expected_win_rate = [Math]::Round($weightedWinRate, 2)
		coverage_pct_raw = $coverageWeight * 100.0
		coverage_pct = [Math]::Round($coverageWeight * 100, 2)
		legacy_coverage_pct = [Math]::Round($legacyCoverageWeight * 100, 2)
		weighted_sample_games_raw = $weightedGames
		weighted_sample_games = [Math]::Round($weightedGames, 1)
		matchups_used = $matchupsUsed
		legacy_matchups_used = $legacyMatchupsUsed
		fallback_win_rate = [Math]::Round($fallbackWinRate, 2)
	})
}

$rank = 1
$ranked = @($recommendations |
	Where-Object { $_.coverage_pct_raw -ge $MinCoveragePct } |
	Sort-Object @{ Expression = { $_.expected_win_rate_raw }; Descending = $true },
		@{ Expression = { $_.coverage_pct_raw }; Descending = $true },
		@{ Expression = { $_.weighted_sample_games_raw }; Descending = $true } |
	Select-Object -First $Top |
	ForEach-Object {
		[pscustomobject][ordered]@{
			rank = $rank++
			archetype_id = $_.archetype_id
			name = $_.name
			player_class = $_.player_class
			expected_win_rate = $_.expected_win_rate
			coverage_pct = $_.coverage_pct
			weighted_sample_games = $_.weighted_sample_games
			matchups_used = $_.matchups_used
			fallback_win_rate = $_.fallback_win_rate
			legacy_coverage_pct = $_.legacy_coverage_pct
			legacy_matchups_used = $_.legacy_matchups_used
		}
	})

$json = [ordered]@{
	schema_version = 2
	model_version = "beta_dirichlet_soft_v2"
	generated_at = (Get-Date).ToString("o")
	meta_directory = $MetaDirectory
	time_range = $summary.time_range
	game_type = $summary.game_type
	rank_range = $summary.rank_range
	region = $summary.region
	matrix_as_of = $matrix.as_of
	environment_archetypes = @($environmentRows | ForEach-Object {
		[pscustomobject][ordered]@{
			archetype_id = [int]$_.archetype_id
			name = [string]$_.name
			player_class = [string]$_.player_class
			pct_of_total = [double]$_.pct_of_total
		}
	})
	min_matchup_games = $MinMatchupGames
	min_matchup_games_mode = "legacy_diagnostic_only"
	matchup_prior_games = $effectiveMatchupPriorGames
	min_coverage_pct = $MinCoveragePct
	coverage_model = "posterior_data_share"
	environment_model = "remote_fixed_distribution"
	matchup_observation_model = "fractional_beta_pseudocounts_from_total_games_and_win_rate"
	include_class_top = [bool]$IncludeClassTop
	use_all_candidates = [bool]$UseAllCandidates
	recommendations = @($ranked)
}

$jsonPath = "$OutputPrefix.json"
$tsvPath = "$OutputPrefix.tsv"
$json | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8

$tsvLines = New-Object System.Collections.Generic.List[string]
$tsvLines.Add("rank`tarchetype_id`tname`tplayer_class`texpected_win_rate`tcoverage_pct`tweighted_sample_games`tmatchups_used`tfallback_win_rate`tmodel_version`tlegacy_coverage_pct`tlegacy_matchups_used")
foreach ($row in $ranked) {
	$values = @($row.rank, $row.archetype_id, $row.name, $row.player_class,
		$row.expected_win_rate, $row.coverage_pct, $row.weighted_sample_games,
		$row.matchups_used, $row.fallback_win_rate, "beta_dirichlet_soft_v2",
		$row.legacy_coverage_pct, $row.legacy_matchups_used)
	$tsvLines.Add(($values | ForEach-Object { Format-TsvValue $_ }) -join "`t")
}
Set-Content -Path $tsvPath -Value $tsvLines -Encoding UTF8

Write-Host "推荐结果已写入："
Write-Host "  $tsvPath"
Write-Host "  $jsonPath"
$displayColumns = @(
	@{ Label = "排名"; Expression = { $_.rank } },
	@{ Label = "流派 ID"; Expression = { $_.archetype_id } },
	@{ Label = "名称"; Expression = { $_.name } },
	@{ Label = "职业"; Expression = { $_.player_class } },
	@{ Label = "预期胜率"; Expression = { $_.expected_win_rate } },
	@{ Label = "覆盖率"; Expression = { $_.coverage_pct } },
	@{ Label = "加权样本"; Expression = { $_.weighted_sample_games } },
	@{ Label = "有效对阵"; Expression = { $_.matchups_used } },
	@{ Label = "回退胜率"; Expression = { $_.fallback_win_rate } }
)
$ranked | Format-Table -Property $displayColumns -AutoSize
