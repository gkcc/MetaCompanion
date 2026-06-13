param(
	[string]$MetaDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest",
	[string]$HistoryPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\match_history.tsv",
	[string]$LocalMetaPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\local_meta_archetypes.tsv",
	[string]$CorrectionsPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\match_corrections.tsv",
	[string]$OutputPrefix = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\personal_recommendations",
	[int]$Top = 20,
	[int]$HistoryDays = 3,
	[double]$LocalWeight = 0.35,
	[bool]$UseHdtLocalMeta = $true,
	[int]$LocalMetaMinConfidence = 35,
	[int]$MinMatchupGames = 200,
	[double]$MinCoveragePct = 50,
	[switch]$IncludeClassTop,
	[switch]$EnvironmentCandidatesOnly
)

$ErrorActionPreference = "Stop"

function Get-RequiredJson([string]$Path) {
	if (-not (Test-Path $Path)) {
		throw "Required file not found: $Path"
	}
	return Get-Content -Path $Path -Encoding UTF8 -Raw | ConvertFrom-Json
}

function Add-ArchetypesToMap([object]$Archetypes, [hashtable]$IdMap, [hashtable]$NameMap) {
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
		$id = [int]$item.id
		$IdMap[[string]$id] = $item
		if (-not [string]::IsNullOrWhiteSpace([string]$item.name)) {
			$NameMap[[string]$item.name] = $id
		}
	}
}

function Get-ArchetypeName([int]$Id, [hashtable]$Map) {
	$key = [string]$Id
	if ($Map.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace([string]$Map[$key].name)) {
		return [string]$Map[$key].name
	}
	return "Archetype $Id"
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

function Add-Weight([hashtable]$Weights, [int]$Id, [double]$Weight) {
	$key = [string]$Id
	$current = 0.0
	if ($Weights.ContainsKey($key)) {
		$current = [double]$Weights[$key]
	}
	$Weights[$key] = $current + $Weight
}

function Format-TsvValue($Value) {
	if ($null -eq $Value) {
		return ""
	}
	return ([string]$Value) -replace "[`t`r`n]", " "
}

function Try-ParseDate([string]$Value) {
	$result = [DateTime]::MinValue
	if ([DateTime]::TryParse($Value, [ref]$result)) {
		return $result
	}
	return $null
}

function Get-LocalMetaCutoff([string]$Path, [datetime]$Fallback) {
	$directory = Split-Path -Parent $Path
	if ([string]::IsNullOrWhiteSpace($directory)) {
		return $Fallback
	}

	$summaryPath = Join-Path $directory "local_meta_summary.json"
	if (-not (Test-Path -LiteralPath $summaryPath)) {
		return $Fallback
	}

	try {
		$summary = Get-Content -LiteralPath $summaryPath -Encoding UTF8 -Raw | ConvertFrom-Json
		if ($summary.PSObject.Properties.Name -contains "sample_window_start") {
			$parsed = Try-ParseDate ([string]$summary.sample_window_start)
			if ($parsed) {
				return $parsed
			}
		}
	} catch {
		return $Fallback
	}

	return $Fallback
}

$summaryPath = Join-Path $MetaDirectory "summary.json"
$matrixPath = Join-Path $MetaDirectory "head_to_head_archetype_matchups_v2.json"
$archetypesPath = Join-Path $MetaDirectory "archetypes.zh-hans.json"

$summary = Get-RequiredJson $summaryPath
$matrix = Get-RequiredJson $matrixPath
$archetypes = Get-RequiredJson $archetypesPath
$archetypeMap = @{}
$archetypeNameMap = @{}
Add-ArchetypesToMap $archetypes $archetypeMap $archetypeNameMap

$remoteRows = New-Object System.Collections.Generic.List[object]
if ($summary.PSObject.Properties.Name -contains "all" -and @($summary.all).Count -gt 0) {
	foreach ($row in @($summary.all)) {
		$remoteRows.Add($row)
	}
} else {
	foreach ($row in @($summary.top_overall)) {
		$remoteRows.Add($row)
	}
}
if ($IncludeClassTop -and -not ($summary.PSObject.Properties.Name -contains "all")) {
	foreach ($classProperty in $summary.top_by_class.PSObject.Properties) {
		foreach ($row in @($classProperty.Value)) {
			if (-not ($remoteRows | Where-Object { [int]$_.archetype_id -eq [int]$row.archetype_id })) {
				$remoteRows.Add($row)
			}
		}
	}
}

$remoteRows = @($remoteRows |
	Where-Object { [int]$_.archetype_id -gt 0 -and [double]$_.pct_of_total -gt 0 })
$remoteTotal = ($remoteRows | Measure-Object -Property pct_of_total -Sum).Sum
if ($remoteTotal -le 0) {
	throw "Remote environment rows have no usable pct_of_total weights."
}

$corrections = @{}
if (Test-Path $CorrectionsPath) {
	foreach ($row in Import-Csv -Path $CorrectionsPath -Delimiter "`t") {
		if (-not [string]::IsNullOrWhiteSpace($row.match_id)) {
			$corrections[$row.match_id] = $row
		}
	}
}

$localWeights = @{}
$localMatchCount = 0
$localSource = "none"
$cutoff = (Get-Date).AddDays(-1 * [Math]::Max(1, $HistoryDays))
if ($UseHdtLocalMeta -and (Test-Path $LocalMetaPath)) {
	$localSource = "hdt_deckstats"
	$localMetaCutoff = Get-LocalMetaCutoff $LocalMetaPath $cutoff
	foreach ($row in Import-Csv -Path $LocalMetaPath -Delimiter "`t") {
		$date = Try-ParseDate $row.end_time
		if ($null -eq $date) {
			$date = Try-ParseDate $row.start_time
		}
		if ($null -eq $date -or $date -lt $localMetaCutoff) {
			continue
		}

		$archetypeId = 0
		if (-not [int]::TryParse([string]$row.predicted_archetype_id, [ref]$archetypeId) -or
			$archetypeId -le 0) {
			continue
		}

		$confidencePct = 0
		if (-not [int]::TryParse([string]$row.confidence_pct, [ref]$confidencePct) -or
			$confidencePct -lt $LocalMetaMinConfidence) {
			continue
		}

		$confidence = [Math]::Max(0.25, [Math]::Min(1.0, $confidencePct / 100.0))
		$rowWeight = $confidence
		if ($row.PSObject.Properties.Name -contains "weight") {
			$parsedWeight = 0.0
			if ([double]::TryParse([string]$row.weight, [ref]$parsedWeight)) {
				$rowWeight = [Math]::Max(0.0, [Math]::Min(1.0, $parsedWeight))
			}
		}
		if ($rowWeight -le 0) {
			continue
		}
		Add-Weight $localWeights $archetypeId $rowWeight
		$localMatchCount++
	}
} elseif (Test-Path $HistoryPath) {
	$localSource = "plugin_match_history"
	foreach ($row in Import-Csv -Path $HistoryPath -Delimiter "`t") {
		$date = Try-ParseDate $row.ended_at
		if ($null -eq $date) {
			$date = Try-ParseDate $row.started_at
		}
		if ($null -eq $date -or $date -lt $cutoff) {
			continue
		}

		$correction = if ($corrections.ContainsKey($row.match_id)) { $corrections[$row.match_id] } else { $null }
		$name = if ($correction -and -not [string]::IsNullOrWhiteSpace($correction.corrected_archetype)) {
			[string]$correction.corrected_archetype
		} else {
			[string]$row.predicted_archetype
		}
		if ([string]::IsNullOrWhiteSpace($name) -or -not $archetypeNameMap.ContainsKey($name)) {
			continue
		}

		$confidence = 0.25
		$parsedConfidence = 0.0
		if ([double]::TryParse([string]$row.confidence_pct, [ref]$parsedConfidence)) {
			$confidence = [Math]::Max(0.25, [Math]::Min(1.0, $parsedConfidence / 100.0))
		}
		if ($correction) {
			$confidence = 1.0
		}

		Add-Weight $localWeights ([int]$archetypeNameMap[$name]) $confidence
		$localMatchCount++
	}
}

$environmentWeights = @{}
$remoteFactor = if ($localWeights.Count -gt 0) {
	[Math]::Max(0.0, [Math]::Min(1.0, 1.0 - $LocalWeight))
} else {
	1.0
}
$localFactor = if ($localWeights.Count -gt 0) {
	[Math]::Max(0.0, [Math]::Min(1.0, $LocalWeight))
} else {
	0.0
}

foreach ($row in $remoteRows) {
	Add-Weight $environmentWeights ([int]$row.archetype_id) ([double]$row.pct_of_total / $remoteTotal * $remoteFactor)
}

$localTotal = ($localWeights.Values | Measure-Object -Sum).Sum
if ($localTotal -gt 0) {
	foreach ($key in $localWeights.Keys) {
		Add-Weight $environmentWeights ([int]$key) ([double]$localWeights[$key] / $localTotal * $localFactor)
	}
}

$environmentRows = @($environmentWeights.Keys | ForEach-Object {
	$id = [int]$_
	[pscustomobject][ordered]@{
		archetype_id = $id
		name = Get-ArchetypeName $id $archetypeMap
		player_class = Get-ArchetypeClass $id $archetypeMap
		weight = [double]$environmentWeights[$_]
	}
} | Sort-Object @{ Expression = { $_.weight }; Descending = $true })

$candidateIds = if ($EnvironmentCandidatesOnly) {
	@($environmentRows | Select-Object -ExpandProperty archetype_id -Unique | ForEach-Object { [int]$_ })
} else {
	@($matrix.series.metadata.PSObject.Properties.Name |
		Where-Object { $_ -match "^\d+$" } |
		ForEach-Object { [int]$_ })
}

$recommendations = New-Object System.Collections.Generic.List[object]
foreach ($candidateId in $candidateIds) {
	$weightedWinRate = 0.0
	$coverageWeight = 0.0
	$weightedGames = 0.0
	$matchupsUsed = 0
	$candidateMeta = Get-Metadata $matrix $candidateId
	$fallbackWinRate = if ($candidateMeta -and $candidateMeta.win_rate) {
		[double]$candidateMeta.win_rate
	} else {
		50.0
	}

	foreach ($opponent in $environmentRows) {
		$opponentId = [int]$opponent.archetype_id
		$weight = [double]$opponent.weight
		$cell = Get-MatchupCell $matrix $candidateId $opponentId
		if ($cell -ne $null -and [int]$cell.total_games -ge $MinMatchupGames) {
			$winRate = [double]$cell.win_rate
			$coverageWeight += $weight
			$weightedGames += $weight * [int]$cell.total_games
			$matchupsUsed++
		} else {
			$winRate = $fallbackWinRate
		}
		$weightedWinRate += $weight * $winRate
	}

	$recommendations.Add([pscustomobject][ordered]@{
		archetype_id = $candidateId
		name = Get-ArchetypeName $candidateId $archetypeMap
		player_class = Get-ArchetypeClass $candidateId $archetypeMap
		expected_win_rate = [Math]::Round($weightedWinRate, 2)
		coverage_pct = [Math]::Round($coverageWeight * 100, 2)
		weighted_sample_games = [Math]::Round($weightedGames, 1)
		matchups_used = $matchupsUsed
		fallback_win_rate = [Math]::Round($fallbackWinRate, 2)
	})
}

$rank = 1
$ranked = @($recommendations |
	Where-Object { $_.coverage_pct -ge $MinCoveragePct } |
	Sort-Object @{ Expression = { $_.expected_win_rate }; Descending = $true },
		@{ Expression = { $_.coverage_pct }; Descending = $true },
		@{ Expression = { $_.weighted_sample_games }; Descending = $true } |
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
		}
	})

$json = [ordered]@{
	generated_at = (Get-Date).ToString("o")
	meta_directory = $MetaDirectory
	history_path = $HistoryPath
	local_meta_path = $LocalMetaPath
	corrections_path = $CorrectionsPath
	time_range = $summary.time_range
	game_type = $summary.game_type
	rank_range = $summary.rank_range
	region = $summary.region
	matrix_as_of = $matrix.as_of
	history_days = $HistoryDays
	local_weight = $localFactor
	remote_weight = $remoteFactor
	local_source = $localSource
	local_match_count = $localMatchCount
	min_matchup_games = $MinMatchupGames
	min_coverage_pct = $MinCoveragePct
	environment_archetypes = @($environmentRows | ForEach-Object {
		[pscustomobject][ordered]@{
			archetype_id = [int]$_.archetype_id
			name = [string]$_.name
			player_class = [string]$_.player_class
			weight_pct = [Math]::Round([double]$_.weight * 100, 2)
		}
	})
	recommendations = @($ranked)
}

$jsonPath = "$OutputPrefix.json"
$tsvPath = "$OutputPrefix.tsv"
$json | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8

$tsvLines = New-Object System.Collections.Generic.List[string]
$tsvLines.Add("rank`tarchetype_id`tname`tplayer_class`texpected_win_rate`tcoverage_pct`tweighted_sample_games`tmatchups_used`tfallback_win_rate")
foreach ($row in $ranked) {
	$values = @($row.rank, $row.archetype_id, $row.name, $row.player_class,
		$row.expected_win_rate, $row.coverage_pct, $row.weighted_sample_games,
		$row.matchups_used, $row.fallback_win_rate)
	$tsvLines.Add(($values | ForEach-Object { Format-TsvValue $_ }) -join "`t")
}
Set-Content -Path $tsvPath -Value $tsvLines -Encoding UTF8

Write-Host "Wrote personal recommendations:"
Write-Host "  $tsvPath"
Write-Host "  $jsonPath"
Write-Host "Local matches used: $localMatchCount"
$ranked | Format-Table -AutoSize
