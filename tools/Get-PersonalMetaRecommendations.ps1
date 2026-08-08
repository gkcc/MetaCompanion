param(
	[string]$MetaDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest",
	[string]$HistoryPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\match_history.tsv",
	[string]$LocalMetaPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\local_meta_archetypes.tsv",
	[string]$BranchPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\archetype_deck_branches.tsv",
	[string]$CorrectionsPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\match_corrections.tsv",
	[string]$OutputPrefix = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\personal_recommendations",
	[int]$Top = 20,
	[int]$HistoryDays = 3,
	[int]$HistoryMatches = 0,
	[double]$LocalWeight = 0.35,
	[bool]$UseHdtLocalMeta = $true,
	[int]$LocalMetaMinConfidence = 35,
	[int]$MinMatchupGames = 200,
	[double]$MinCoveragePct = 50,
	[double]$RemotePriorGames = 30,
	[double]$MatchupPriorGames = 50,
	[int]$PosteriorDraws = 2000,
	[switch]$IncludeClassTop,
	[switch]$EnvironmentCandidatesOnly,
	[string]$PatchMarkerPath = "",
	[datetime]$HistoryClearedAt = [datetime]::MinValue
)

$ErrorActionPreference = "Stop"

function Get-RequiredJson([string]$Path) {
	if (-not (Test-Path $Path)) {
		throw "缺少必需文件：$Path"
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

function Add-Weight([hashtable]$Weights, [int]$Id, [double]$Weight) {
	$key = [string]$Id
	$current = 0.0
	if ($Weights.ContainsKey($key)) {
		$current = [double]$Weights[$key]
	}
	$Weights[$key] = $current + $Weight
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

function Get-CandidateProbabilityAssignments(
	[string]$Value,
	[hashtable]$NameMap
) {
	$assignments = @{}
	if ([string]::IsNullOrWhiteSpace($Value)) {
		return $assignments
	}
	foreach ($part in ($Value -split " / ")) {
		if ($part -notmatch "^(?<name>.*?)(?::|\s+)(?<pct>\d+(?:\.\d+)?)\s*%") {
			continue
		}
		$name = [string]$matches.name
		if (-not $NameMap.ContainsKey($name)) {
			continue
		}
		$probability = [Math]::Max(0.0, [Math]::Min(1.0, [double]$matches.pct / 100.0))
		$key = [string][int]$NameMap[$name]
		$current = if ($assignments.ContainsKey($key)) { [double]$assignments[$key] } else { 0.0 }
		$assignments[$key] = $current + $probability
	}
	$total = ($assignments.Values | Measure-Object -Sum).Sum
	if ($total -gt 1.0) {
		foreach ($key in @($assignments.Keys)) {
			$assignments[$key] = [double]$assignments[$key] / $total
		}
	}
	return $assignments
}

function Get-StandardNormal([Random]$Random) {
	$first = [Math]::Max([double]::Epsilon, $Random.NextDouble())
	$second = $Random.NextDouble()
	return [Math]::Sqrt(-2.0 * [Math]::Log($first)) * [Math]::Cos(2.0 * [Math]::PI * $second)
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

function Get-PatchCutoff(
	[string]$ExplicitPath,
	[string]$LocalPath,
	[string]$FallbackHistoryPath
) {
	$candidates = New-Object System.Collections.Generic.List[string]
	if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
		$candidates.Add($ExplicitPath)
	}
	foreach ($sourcePath in @($LocalPath, $FallbackHistoryPath)) {
		$directory = Split-Path -Parent $sourcePath
		if (-not [string]::IsNullOrWhiteSpace($directory)) {
			$candidate = Join-Path $directory "patch_marker.txt"
			if (-not $candidates.Contains($candidate)) {
				$candidates.Add($candidate)
			}
		}
	}
	foreach ($candidate in $candidates) {
		if (-not (Test-Path -LiteralPath $candidate)) {
			continue
		}
		$parsed = Try-ParseDate ((Get-Content -LiteralPath $candidate -Encoding UTF8 -Raw).Trim())
		if ($parsed) {
			return [pscustomobject]@{ Path = $candidate; Time = $parsed }
		}
	}
	return $null
}

function Test-RepresentativeDeckScope(
	[string]$Path,
	[string]$ExpectedTimeRange,
	[string]$ExpectedRankRange
) {
	$timeRange = ""
	$rankRange = ""
	foreach ($line in (Get-Content -LiteralPath $Path -Encoding UTF8 -TotalCount 24)) {
		if ($line.StartsWith("# CandidateTimeRange:", [StringComparison]::OrdinalIgnoreCase)) {
			$timeRange = $line.Substring("# CandidateTimeRange:".Length).Trim()
		} elseif ($line.StartsWith("# RankRange:", [StringComparison]::OrdinalIgnoreCase)) {
			$rankRange = $line.Substring("# RankRange:".Length).Trim()
		}
	}
	if (-not [string]::Equals($timeRange, $ExpectedTimeRange, [StringComparison]::OrdinalIgnoreCase) -or
		-not [string]::Equals($rankRange, $ExpectedRankRange, [StringComparison]::OrdinalIgnoreCase)) {
		return $false
	}
	return $true
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

$representativeDeckMap = @{}
$representativeScopeMatches = $false
if (Test-Path -LiteralPath $BranchPath) {
	$representativeScopeMatches = Test-RepresentativeDeckScope `
		-Path $BranchPath `
		-ExpectedTimeRange ([string]$summary.time_range) `
		-ExpectedRankRange ([string]$summary.rank_range)
	if (-not $representativeScopeMatches) {
		Write-Warning "当前口径暂无同范围卡组代码；推荐排序会继续生成，复制按钮暂不显示。"
	}
	$representativeLines = if ($representativeScopeMatches) {
		Get-Content -LiteralPath $BranchPath -Encoding UTF8
	} else {
		@()
	}
	foreach ($line in $representativeLines) {
		if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith("#")) {
			continue
		}
		$values = $line -split "`t"
		if ($values.Count -lt 8) {
			continue
		}
		$archetypeId = 0
		$games = 0
		$winRate = 0.0
		if (-not [int]::TryParse([string]$values[3], [ref]$archetypeId) -or
			$archetypeId -le 0 -or [string]::IsNullOrWhiteSpace([string]$values[1])) {
			continue
		}
		[void]([int]::TryParse([string]$values[6], [ref]$games))
		[void](Try-ParseDoubleValue $values[7] ([ref]$winRate))
		$key = [string]$archetypeId
		if (-not $representativeDeckMap.ContainsKey($key)) {
			$representativeDeckMap[$key] = New-Object System.Collections.Generic.List[object]
		}
		$representativeDeckMap[$key].Add([pscustomobject]@{
			deck_code = [string]$values[1]
			deck_id = [string]$values[2]
			games = [Math]::Max(0, $games)
			win_rate = $winRate
		})
	}
} else {
	Write-Warning "当前口径暂无同范围卡组代码；推荐排序会继续生成，复制按钮暂不显示。"
}

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
	throw "远端环境数据中没有可用的 pct_of_total 权重。"
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
$localUnknownEvidence = 0.0
$localKnownRowWeights = New-Object System.Collections.Generic.List[double]
$cutoff = if ($HistoryDays -gt 0) {
	(Get-Date).AddDays(-1 * $HistoryDays)
} else {
	[DateTime]::MinValue
}
if ($HistoryClearedAt -ne [datetime]::MinValue -and $HistoryClearedAt -gt $cutoff) {
	$cutoff = $HistoryClearedAt
}
$patchCutoff = Get-PatchCutoff $PatchMarkerPath $LocalMetaPath $HistoryPath
if ($UseHdtLocalMeta -and (Test-Path $LocalMetaPath)) {
	$localSource = "hdt_deckstats"
	$localMetaCutoff = Get-LocalMetaCutoff $LocalMetaPath $cutoff
	if ($patchCutoff -and $localMetaCutoff -lt $patchCutoff.Time) {
		$localMetaCutoff = $patchCutoff.Time
	}
	$localMetaRows = @(Import-Csv -Path $LocalMetaPath -Delimiter "`t" |
		Sort-Object @{ Expression = {
			$date = Try-ParseDate ([string]$_.end_time)
			if ($date) { $date } else { Try-ParseDate ([string]$_.start_time) }
		}; Descending = $true })
	if ($HistoryMatches -gt 0) {
		$localMetaRows = @($localMetaRows | Select-Object -First $HistoryMatches)
	}
	foreach ($row in $localMetaRows) {
		$date = Try-ParseDate $row.end_time
		if ($null -eq $date) {
			$date = Try-ParseDate $row.start_time
		}
		$gameTime = Try-ParseDate $row.start_time
		if ($null -eq $gameTime) {
			$gameTime = $date
		}
		if ($null -eq $date -or $null -eq $gameTime -or $gameTime -lt $localMetaCutoff) {
			continue
		}
		if (($row.PSObject.Properties.Name -contains "format") -and
			-not [string]::IsNullOrWhiteSpace([string]$row.format) -and
			-not ([string]$row.format).Equals("Standard", [StringComparison]::OrdinalIgnoreCase)) {
			continue
		}
		if (($row.PSObject.Properties.Name -contains "mode") -and
			-not [string]::IsNullOrWhiteSpace([string]$row.mode) -and
			-not ([string]$row.mode).Equals("Ranked", [StringComparison]::OrdinalIgnoreCase)) {
			continue
		}

		$assignments = @{}
		$correctionKey = [string]$row.game_id
		$correction = if ($corrections.ContainsKey($correctionKey)) { $corrections[$correctionKey] } else { $null }
		$usedPreciseDistribution = $false
		$hasValidCorrection = $correction -and
			-not [string]::IsNullOrWhiteSpace([string]$correction.corrected_archetype) -and
			$archetypeNameMap.ContainsKey([string]$correction.corrected_archetype)
		if ($hasValidCorrection) {
			$assignments[[string][int]$archetypeNameMap[[string]$correction.corrected_archetype]] = 1.0
		} elseif ($row.PSObject.Properties.Name -contains "archetype_distribution_json" -and
			-not [string]::IsNullOrWhiteSpace([string]$row.archetype_distribution_json)) {
			try {
				$distributionItems = @(([string]$row.archetype_distribution_json | ConvertFrom-Json))
				$usedPreciseDistribution = $distributionItems.Count -gt 0
				foreach ($item in $distributionItems) {
					$id = [int]$item.id
					if ($id -le 0 -and -not [string]::IsNullOrWhiteSpace([string]$item.name) -and
						-not ([string]$item.name).Equals("Unknown", [StringComparison]::OrdinalIgnoreCase) -and
						$archetypeNameMap.ContainsKey([string]$item.name)) {
						$id = [int]$archetypeNameMap[[string]$item.name]
					}
					$probability = [Math]::Max(0.0, [Math]::Min(1.0, [double]$item.probability))
					if ($id -gt 0 -and $archetypeMap.ContainsKey([string]$id) -and $probability -gt 0) {
						Add-Weight $assignments $id $probability
					}
				}
			} catch {
				$assignments = @{}
				$usedPreciseDistribution = $false
			}
		}
		if (-not $hasValidCorrection -and -not $usedPreciseDistribution) {
			$archetypeId = 0
			$confidencePct = 0
			if ([int]::TryParse([string]$row.predicted_archetype_id, [ref]$archetypeId) -and
				$archetypeId -gt 0 -and
				[int]::TryParse([string]$row.confidence_pct, [ref]$confidencePct) -and
				$confidencePct -ge $LocalMetaMinConfidence) {
				$assignments[[string]$archetypeId] = 1.0
			}
		}

		$knownProbability = [double](($assignments.Values | Measure-Object -Sum).Sum)
		if ($knownProbability -gt 1.0) {
			foreach ($key in @($assignments.Keys)) {
				$assignments[$key] = [double]$assignments[$key] / $knownProbability
			}
			$knownProbability = 1.0
		}
		$evidenceWeight = 0.0
		if ($row.PSObject.Properties.Name -contains "evidence_weight") {
			[void](Try-ParseDoubleValue $row.evidence_weight ([ref]$evidenceWeight))
		}
		if ($evidenceWeight -le 0.0 -and
			($row.PSObject.Properties.Name -contains "patch_weight") -and
			($row.PSObject.Properties.Name -contains "recency_weight")) {
			$patchWeight = 0.0
			$recencyWeight = 0.0
			if ((Try-ParseDoubleValue $row.patch_weight ([ref]$patchWeight)) -and
				(Try-ParseDoubleValue $row.recency_weight ([ref]$recencyWeight))) {
				$evidenceWeight = [Math]::Max(0.0, $patchWeight) * [Math]::Max(0.0, $recencyWeight)
			}
		}
		if ($evidenceWeight -le 0.0) {
			$legacyWeight = 0.0
			if (Try-ParseDoubleValue $row.weight ([ref]$legacyWeight)) {
				$evidenceWeight = if ($row.PSObject.Properties.Name -contains "archetype_distribution_json") {
					if ($knownProbability -gt 0) { $legacyWeight / $knownProbability } else { 0.0 }
				} else {
					$legacyWeight
				}
			}
		}
		$evidenceWeight = [Math]::Max(0.0, $evidenceWeight)
		$rowKnownWeight = 0.0
		foreach ($key in @($assignments.Keys)) {
			$contribution = $evidenceWeight * [double]$assignments[$key]
			if ($contribution -gt 0) {
				Add-Weight $localWeights ([int]$key) $contribution
				$rowKnownWeight += $contribution
			}
		}
		$localKnownRowWeights.Add($rowKnownWeight)
		$localUnknownEvidence += $evidenceWeight * [Math]::Max(0.0, 1.0 - $knownProbability)
		$localMatchCount++
	}
} elseif (Test-Path $HistoryPath) {
	$localSource = "plugin_match_history"
	$historyCutoff = if ($patchCutoff -and $cutoff -lt $patchCutoff.Time) {
		$patchCutoff.Time
	} else {
		$cutoff
	}
	$historyRows = @(Import-Csv -Path $HistoryPath -Delimiter "`t" |
		Sort-Object @{ Expression = {
			$date = Try-ParseDate ([string]$_.ended_at)
			if ($date) { $date } else { Try-ParseDate ([string]$_.started_at) }
		}; Descending = $true })
	if ($HistoryMatches -gt 0) {
		$historyRows = @($historyRows | Select-Object -First $HistoryMatches)
	}
	foreach ($row in $historyRows) {
		$date = Try-ParseDate $row.ended_at
		if ($null -eq $date) {
			$date = Try-ParseDate $row.started_at
		}
		$gameTime = Try-ParseDate $row.started_at
		if ($null -eq $gameTime) {
			$gameTime = $date
		}
		if ($null -eq $date -or $null -eq $gameTime -or $gameTime -lt $historyCutoff) {
			continue
		}
		if (-not [string]::IsNullOrWhiteSpace([string]$row.format) -and
			-not ([string]$row.format).Equals("Standard", [StringComparison]::OrdinalIgnoreCase)) {
			continue
		}
		if (-not [string]::IsNullOrWhiteSpace([string]$row.mode) -and
			-not ([string]$row.mode).Equals("Ranked", [StringComparison]::OrdinalIgnoreCase)) {
			continue
		}

		$correction = if ($corrections.ContainsKey($row.match_id)) { $corrections[$row.match_id] } else { $null }
		$assignments = @{}
		if ($correction -and -not [string]::IsNullOrWhiteSpace([string]$correction.corrected_archetype) -and
			$archetypeNameMap.ContainsKey([string]$correction.corrected_archetype)) {
			$assignments[[string][int]$archetypeNameMap[[string]$correction.corrected_archetype]] = 1.0
		} else {
			$assignments = Get-CandidateProbabilityAssignments ([string]$row.candidate_archetypes) $archetypeNameMap
			if ($assignments.Count -eq 0) {
				$name = [string]$row.predicted_archetype
				$parsedConfidence = 0.0
				if ($archetypeNameMap.ContainsKey($name) -and
					(Try-ParseDoubleValue $row.confidence_pct ([ref]$parsedConfidence))) {
					$assignments[[string][int]$archetypeNameMap[$name]] =
						[Math]::Max(0.0, [Math]::Min(1.0, $parsedConfidence / 100.0))
				}
			}
		}
		$knownProbability = [Math]::Min(1.0, [double](($assignments.Values | Measure-Object -Sum).Sum))
		$ageDays = [Math]::Max(0.0, ((Get-Date) - $date).TotalDays)
		$recencyHalfLifeDays = if ($HistoryDays -gt 0) { $HistoryDays } else { 3 }
		$evidenceWeight = [Math]::Pow(0.5, $ageDays / [Math]::Max(0.1, $recencyHalfLifeDays))
		$rowKnownWeight = 0.0
		foreach ($key in @($assignments.Keys)) {
			$contribution = $evidenceWeight * [double]$assignments[$key]
			Add-Weight $localWeights ([int]$key) $contribution
			$rowKnownWeight += $contribution
		}
		$localKnownRowWeights.Add($rowKnownWeight)
		$localUnknownEvidence += $evidenceWeight * [Math]::Max(0.0, 1.0 - $knownProbability)
		$localMatchCount++
	}
}

$environmentWeights = @{}
$effectiveRemotePriorGames = [Math]::Max(0.001, $RemotePriorGames)

foreach ($row in $remoteRows) {
	Add-Weight $environmentWeights ([int]$row.archetype_id) `
		([double]$row.pct_of_total / $remoteTotal * $effectiveRemotePriorGames)
}

$localTotal = ($localWeights.Values | Measure-Object -Sum).Sum
if ($localTotal -gt 0) {
	foreach ($key in $localWeights.Keys) {
		Add-Weight $environmentWeights ([int]$key) ([double]$localWeights[$key])
	}
}

$totalAlpha = ($environmentWeights.Values | Measure-Object -Sum).Sum
$localFactor = if ($totalAlpha -gt 0) { [double]$localTotal / $totalAlpha } else { 0.0 }
$remoteFactor = 1.0 - $localFactor
$localWeightSumSquares = ($localKnownRowWeights | ForEach-Object { [double]$_ * [double]$_ } |
	Measure-Object -Sum).Sum
$localEffectiveGames = if ($localWeightSumSquares -gt 0) {
	[double]$localTotal * [double]$localTotal / [double]$localWeightSumSquares
} else {
	0.0
}

$environmentRows = @($environmentWeights.Keys | ForEach-Object {
	$id = [int]$_
	[pscustomobject][ordered]@{
		archetype_id = $id
		name = Get-ArchetypeName $id $archetypeMap
		player_class = Get-ArchetypeClass $id $archetypeMap
		alpha = [double]$environmentWeights[$_]
		weight = if ($totalAlpha -gt 0) { [double]$environmentWeights[$_] / $totalAlpha } else { 0.0 }
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
$effectiveMatchupPriorGames = [Math]::Max(0.001, $MatchupPriorGames)
foreach ($candidateId in $candidateIds) {
	$weightedWinRate = 0.0
	$coverageWeight = 0.0
	$legacyCoverageWeight = 0.0
	$weightedGames = 0.0
	$matchupsUsed = 0
	$legacyMatchupsUsed = 0
	$matchupVariance = 0.0
	$alphaWeightedMeanSquare = 0.0
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
		$weight = [double]$opponent.weight
		$alpha = [double]$opponent.alpha
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
		$posteriorMean = ($effectiveMatchupPriorGames * $fallbackWinRate +
			$observedGames * $observedWinRate) /
			($effectiveMatchupPriorGames + $observedGames)
		$priorProbability = [Math]::Max(0.000001,
			[Math]::Min(0.999999, $fallbackWinRate / 100.0))
		$observedProbability = [Math]::Max(0.0,
			[Math]::Min(1.0, $observedWinRate / 100.0))
		$posteriorAlpha = $effectiveMatchupPriorGames * $priorProbability +
			$observedGames * $observedProbability
		$posteriorBeta = $effectiveMatchupPriorGames * (1.0 - $priorProbability) +
			$observedGames * (1.0 - $observedProbability)
		$posteriorStrength = $posteriorAlpha + $posteriorBeta
		$posteriorVariance = $posteriorAlpha * $posteriorBeta /
			($posteriorStrength * $posteriorStrength * ($posteriorStrength + 1.0)) * 10000.0
		$dataShare = if ($hasObservedCell) {
			$observedGames / ($observedGames + $effectiveMatchupPriorGames)
		} else {
			0.0
		}
		$weightedWinRate += $weight * $posteriorMean
		$coverageWeight += $weight * $dataShare
		$alphaWeightedMeanSquare += $alpha * $posteriorMean * $posteriorMean
		$matchupVariance += $alpha * ($alpha + 1.0) /
			($totalAlpha * ($totalAlpha + 1.0)) * $posteriorVariance
	}
	$environmentVariance = [Math]::Max(0.0,
		($alphaWeightedMeanSquare / $totalAlpha - $weightedWinRate * $weightedWinRate) /
		($totalAlpha + 1.0))
	$totalVariance = $environmentVariance + $matchupVariance
	$deviation = [Math]::Sqrt([Math]::Max(0.0, $totalVariance))

	$recommendations.Add([pscustomobject][ordered]@{
		archetype_id = $candidateId
		name = Get-ArchetypeName $candidateId $archetypeMap
		player_class = Get-ArchetypeClass $candidateId $archetypeMap
		expected_win_rate_raw = $weightedWinRate
		expected_win_rate = [Math]::Round($weightedWinRate, 2)
		expected_win_rate_low_90 = [Math]::Round([Math]::Max(0.0, $weightedWinRate - 1.64485362695147 * $deviation), 2)
		expected_win_rate_high_90 = [Math]::Round([Math]::Min(100.0, $weightedWinRate + 1.64485362695147 * $deviation), 2)
		probability_best_pct = 0.0
		tier = 0
		coverage_pct_raw = $coverageWeight * 100.0
		coverage_pct = [Math]::Round($coverageWeight * 100, 2)
		legacy_coverage_pct = [Math]::Round($legacyCoverageWeight * 100, 2)
		weighted_sample_games_raw = $weightedGames
		weighted_sample_games = [Math]::Round($weightedGames, 1)
		matchups_used = $matchupsUsed
		legacy_matchups_used = $legacyMatchupsUsed
		fallback_win_rate = [Math]::Round($fallbackWinRate, 2)
		posterior_variance = $totalVariance
	})
}

$eligible = @($recommendations | Where-Object { $_.coverage_pct_raw -ge $MinCoveragePct })
$effectivePosteriorDraws = [Math]::Max(200, [Math]::Min(5000, $PosteriorDraws))
$random = New-Object Random 1729
$samplesById = @{}
$bestCounts = @{}
foreach ($candidate in $eligible) {
	$samplesById[[string]$candidate.archetype_id] = New-Object double[] $effectivePosteriorDraws
	$bestCounts[[string]$candidate.archetype_id] = 0
}
for ($draw = 0; $draw -lt $effectivePosteriorDraws; $draw++) {
	$bestCandidateId = 0
	$bestValue = [double]::MinValue
	foreach ($candidate in $eligible) {
		$value = [Math]::Max(0.0, [Math]::Min(100.0,
			[double]$candidate.expected_win_rate_raw +
			[Math]::Sqrt([Math]::Max(0.0, [double]$candidate.posterior_variance)) *
			(Get-StandardNormal $random)))
		$samplesById[[string]$candidate.archetype_id][$draw] = $value
		if ($value -gt $bestValue) {
			$bestValue = $value
			$bestCandidateId = [int]$candidate.archetype_id
		}
	}
	if ($bestCandidateId -gt 0) {
		$bestCounts[[string]$bestCandidateId] = [int]$bestCounts[[string]$bestCandidateId] + 1
	}
}
foreach ($candidate in $eligible) {
	$candidate.probability_best_pct = [Math]::Round(
		[int]$bestCounts[[string]$candidate.archetype_id] / [double]$effectivePosteriorDraws * 100.0,
		2)
}

$orderedForTiers = @($eligible | Sort-Object @{ Expression = { $_.expected_win_rate_raw }; Descending = $true })
if ($orderedForTiers.Count -gt 0) {
	$tier = 1
	$tierLeader = $orderedForTiers[0]
	$tierLeader.tier = $tier
	foreach ($candidate in @($orderedForTiers | Select-Object -Skip 1)) {
		$beatsLeader = 0
		$candidateSamples = $samplesById[[string]$candidate.archetype_id]
		$leaderSamples = $samplesById[[string]$tierLeader.archetype_id]
		for ($draw = 0; $draw -lt $effectivePosteriorDraws; $draw++) {
			if ($candidateSamples[$draw] -ge $leaderSamples[$draw]) {
				$beatsLeader++
			}
		}
		if ($beatsLeader / [double]$effectivePosteriorDraws -lt 0.20) {
			$tier++
			$tierLeader = $candidate
		}
		$candidate.tier = $tier
	}
}

$rank = 1
$ranked = @($eligible |
	Sort-Object @{ Expression = { $_.expected_win_rate_raw }; Descending = $true },
		@{ Expression = { $_.coverage_pct_raw }; Descending = $true },
		@{ Expression = { $_.weighted_sample_games_raw }; Descending = $true } |
	Select-Object -First $Top |
	ForEach-Object {
		$recommendation = $_
		$representatives = if ($representativeDeckMap.ContainsKey([string]$recommendation.archetype_id)) {
			@($representativeDeckMap[[string]$recommendation.archetype_id].ToArray())
		} else {
			@()
		}
		$highestWinRateDeck = $representatives |
			Sort-Object @{ Expression = { $_.win_rate }; Descending = $true },
				@{ Expression = { $_.games }; Descending = $true } |
			Select-Object -First 1
		$mostPopularDeck = $representatives |
			Sort-Object @{ Expression = { $_.games }; Descending = $true },
				@{ Expression = { $_.win_rate }; Descending = $true } |
			Select-Object -First 1
		[pscustomobject][ordered]@{
			rank = $rank++
			archetype_id = $recommendation.archetype_id
			name = $recommendation.name
			player_class = $recommendation.player_class
			expected_win_rate = $recommendation.expected_win_rate
			expected_win_rate_low_90 = $recommendation.expected_win_rate_low_90
			expected_win_rate_high_90 = $recommendation.expected_win_rate_high_90
			probability_best_pct = $recommendation.probability_best_pct
			tier = $recommendation.tier
			coverage_pct = $recommendation.coverage_pct
			legacy_coverage_pct = $recommendation.legacy_coverage_pct
			weighted_sample_games = $recommendation.weighted_sample_games
			matchups_used = $recommendation.matchups_used
			legacy_matchups_used = $recommendation.legacy_matchups_used
			fallback_win_rate = $recommendation.fallback_win_rate
			highest_winrate_deck_code = if ($highestWinRateDeck) { $highestWinRateDeck.deck_code } else { "" }
			highest_winrate_deck_id = if ($highestWinRateDeck) { $highestWinRateDeck.deck_id } else { "" }
			highest_winrate_deck_win_rate = if ($highestWinRateDeck) { [Math]::Round([double]$highestWinRateDeck.win_rate, 2) } else { "" }
			highest_winrate_deck_games = if ($highestWinRateDeck) { [int]$highestWinRateDeck.games } else { 0 }
			most_popular_deck_code = if ($mostPopularDeck) { $mostPopularDeck.deck_code } else { "" }
			most_popular_deck_id = if ($mostPopularDeck) { $mostPopularDeck.deck_id } else { "" }
			most_popular_deck_win_rate = if ($mostPopularDeck) { [Math]::Round([double]$mostPopularDeck.win_rate, 2) } else { "" }
			most_popular_deck_games = if ($mostPopularDeck) { [int]$mostPopularDeck.games } else { 0 }
		}
	})

$json = [ordered]@{
	schema_version = 2
	model_version = "beta_dirichlet_soft_v2"
	generated_at = (Get-Date).ToString("o")
	meta_directory = $MetaDirectory
	history_path = $HistoryPath
	local_meta_path = $LocalMetaPath
	patch_marker_path = if ($patchCutoff) { [string]$patchCutoff.Path } else { "" }
	patch_cutoff = if ($patchCutoff) { $patchCutoff.Time.ToString("o") } else { $null }
	corrections_path = $CorrectionsPath
	time_range = $summary.time_range
	game_type = $summary.game_type
	rank_range = $summary.rank_range
	region = $summary.region
	matrix_as_of = $matrix.as_of
	history_days = $HistoryDays
	history_matches = $HistoryMatches
	local_history_cleared_at = if ($HistoryClearedAt -ne [datetime]::MinValue) { $HistoryClearedAt.ToString("o") } else { $null }
	branch_path = $BranchPath
	representative_deck_scope_matches = $representativeScopeMatches
	representative_deck_notice = if ($representativeScopeMatches) { "" } else { "当前口径暂无同范围卡组代码" }
	local_weight = $localFactor
	remote_weight = $remoteFactor
	legacy_local_weight_setting = $LocalWeight
	remote_prior_games = $effectiveRemotePriorGames
	matchup_prior_games = $effectiveMatchupPriorGames
	local_known_evidence = [Math]::Round([double]$localTotal, 4)
	local_unknown_evidence = [Math]::Round([double]$localUnknownEvidence, 4)
	local_effective_sample_size = [Math]::Round([double]$localEffectiveGames, 4)
	posterior_draws = $effectivePosteriorDraws
	uncertainty_method = "dirichlet_beta_moments_with_normal_rank_draws"
	coverage_model = "posterior_data_share"
	recommendation_mode = "Standard Ranked"
	local_source = $localSource
	local_match_count = $localMatchCount
	min_matchup_games = $MinMatchupGames
	min_matchup_games_mode = "legacy_diagnostic_only"
	min_coverage_pct = $MinCoveragePct
	environment_archetypes = @($environmentRows | ForEach-Object {
		[pscustomobject][ordered]@{
			archetype_id = [int]$_.archetype_id
			name = [string]$_.name
			player_class = [string]$_.player_class
			weight_pct = [Math]::Round([double]$_.weight * 100, 2)
			alpha = [Math]::Round([double]$_.alpha, 6)
		}
	})
	recommendations = @($ranked)
}

$jsonPath = "$OutputPrefix.json"
$tsvPath = "$OutputPrefix.tsv"
$json | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8

$tsvLines = New-Object System.Collections.Generic.List[string]
$tsvLines.Add("rank`tarchetype_id`tname`tplayer_class`texpected_win_rate`tcoverage_pct`tweighted_sample_games`tmatchups_used`tfallback_win_rate`texpected_win_rate_low_90`texpected_win_rate_high_90`tprobability_best_pct`ttier`tmodel_version`tlegacy_coverage_pct`tlegacy_matchups_used`thighest_winrate_deck_code`thighest_winrate_deck_id`thighest_winrate_deck_win_rate`thighest_winrate_deck_games`tmost_popular_deck_code`tmost_popular_deck_id`tmost_popular_deck_win_rate`tmost_popular_deck_games")
foreach ($row in $ranked) {
	$values = @($row.rank, $row.archetype_id, $row.name, $row.player_class,
		$row.expected_win_rate, $row.coverage_pct, $row.weighted_sample_games,
		$row.matchups_used, $row.fallback_win_rate,
		$row.expected_win_rate_low_90, $row.expected_win_rate_high_90,
		$row.probability_best_pct, $row.tier, "beta_dirichlet_soft_v2",
		$row.legacy_coverage_pct, $row.legacy_matchups_used,
		$row.highest_winrate_deck_code, $row.highest_winrate_deck_id,
		$row.highest_winrate_deck_win_rate, $row.highest_winrate_deck_games,
		$row.most_popular_deck_code, $row.most_popular_deck_id,
		$row.most_popular_deck_win_rate, $row.most_popular_deck_games)
	$tsvLines.Add(($values | ForEach-Object { Format-TsvValue $_ }) -join "`t")
}
Set-Content -Path $tsvPath -Value $tsvLines -Encoding UTF8

Write-Host "个人推荐结果已写入："
Write-Host "  $tsvPath"
Write-Host "  $jsonPath"
Write-Host "使用的本地对局数：$localMatchCount"
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
