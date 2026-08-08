param(
	[string]$OpponentHistoryPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hdt_opponent_history.tsv",
	[string]$DeckCodePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_deckcodes.txt",
	[string]$BranchPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\archetype_model_branches.tsv",
	[string]$OutputPrefix = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\local_meta",
	[string]$HdtAppPath = "",
	[int]$Days = 3,
	[Alias("Matches")]
	[int]$HistoryMatches = 0,
	[int]$MinRelevantCards = 2,
	[int]$MinConfidence = 35,
	[int]$TopCandidates = 3,
	[datetime]$PatchTime = [datetime]::MinValue,
	[datetime]$HistoryClearedAt = [datetime]::MinValue,
	[double]$PrePatchWeight = 0.0,
	[double]$RecencyHalfLifeDays = 3.0,
	[bool]$UsePatchWindow = $true,
	[string]$PatchMarkerPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\patch_marker.txt"
)

$ErrorActionPreference = "Stop"

$RecognitionModel = "softmax_v1_t12_legacy_confidence_mass"
$RecognitionSoftmaxTemperature = 12.0

function Resolve-HdtAppPath {
	if (-not [string]::IsNullOrWhiteSpace($HdtAppPath) -and
		(Test-Path (Join-Path $HdtAppPath "HearthDb.dll"))) {
		return $HdtAppPath
	}

	$root = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
	if (-not (Test-Path $root)) {
		throw "未找到 HearthstoneDeckTracker 本地程序目录：$root"
	}

	$app = Get-ChildItem $root -Directory -Filter "app-*" |
		Where-Object { Test-Path (Join-Path $_.FullName "HearthDb.dll") } |
		Sort-Object LastWriteTime -Descending |
		Select-Object -First 1
	if (-not $app) {
		throw "在 $root 下未找到包含 HearthDb.dll 的 HDT app-* 目录。"
	}
	return $app.FullName
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

function Try-ParseDateTimeOffset([string]$Value) {
	$result = [DateTimeOffset]::MinValue
	if ([DateTimeOffset]::TryParse($Value, [ref]$result)) {
		return $result
	}
	return $null
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

function Test-CurrentPatchBranchSnapshot([string]$Path) {
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
		return $false
	}

	$candidateTimeRange = ""
	$candidatePatchVersion = ""
	$candidateAsOf = $null
	foreach ($rawLine in (Get-Content -LiteralPath $Path -Encoding UTF8 -TotalCount 32)) {
		$line = $rawLine.Trim()
		if ($line.StartsWith("# CandidateTimeRange:", [StringComparison]::OrdinalIgnoreCase)) {
			$candidateTimeRange = $line.Substring("# CandidateTimeRange:".Length).Trim()
		} elseif ($line.StartsWith("# PatchVersion:", [StringComparison]::OrdinalIgnoreCase)) {
			$candidatePatchVersion = $line.Substring("# PatchVersion:".Length).Trim()
		} elseif ($line.StartsWith("# CandidateAsOf:", [StringComparison]::OrdinalIgnoreCase)) {
			$candidateAsOf = Try-ParseDateTimeOffset $line.Substring("# CandidateAsOf:".Length).Trim()
		} elseif (-not $line.StartsWith("#") -and $line.Length -gt 0) {
			break
		}
	}

	if ($candidateTimeRange -notin @("CURRENT_PATCH", "LAST_1_DAY", "LAST_3_DAYS", "LAST_7_DAYS")) {
		return $false
	}
	if ([string]::Equals(
			$candidateTimeRange,
			"CURRENT_PATCH",
			[StringComparison]::OrdinalIgnoreCase)) {
		# HSReplay owns this scope. CandidateAsOf is a snapshot timestamp and may
		# legitimately precede the time this machine first noticed the patch.
		$versionDirectory = if (-not [string]::IsNullOrWhiteSpace($PatchMarkerPath)) {
			Split-Path -Parent $PatchMarkerPath
		} else {
			Split-Path -Parent $Path
		}
		$versionPath = Join-Path $versionDirectory "patch_version.txt"
		if (Test-Path -LiteralPath $versionPath -PathType Leaf) {
			$localPatchVersion = Get-MetaCompanionPublicPatchVersion (
				(Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8).Trim())
			if ([string]::IsNullOrWhiteSpace($candidatePatchVersion) -or
				-not [string]::Equals(
					(Get-MetaCompanionPublicPatchVersion $candidatePatchVersion),
					$localPatchVersion,
					[StringComparison]::OrdinalIgnoreCase)) {
				return $false
			}
		}
		return $true
	}
	if ([string]::IsNullOrWhiteSpace($PatchMarkerPath) -or -not (Test-Path -LiteralPath $PatchMarkerPath)) {
		return $true
	}

	$patchMarker = Try-ParseDateTimeOffset ((Get-Content -LiteralPath $PatchMarkerPath -Raw -Encoding UTF8).Trim())
	if (-not $patchMarker) {
		return $true
	}

	if ($candidateAsOf) {
		return $candidateAsOf -ge $patchMarker
	}

	return ([DateTimeOffset](Get-Item -LiteralPath $Path).LastWriteTime) -ge $patchMarker
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

	if (-not [string]::IsNullOrWhiteSpace($PatchMarkerPath) -and
		(Test-Path -LiteralPath $PatchMarkerPath)) {
		$markerText = Get-Content -LiteralPath $PatchMarkerPath -Raw -Encoding UTF8
		$markerTime = Try-ParseDate $markerText.Trim()
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

function Get-RecencyWeight([datetime]$Value, [datetime]$Now) {
	if ($RecencyHalfLifeDays -le 0) {
		return 1.0
	}

	$ageDays = [Math]::Max(0.0, ($Now - $Value).TotalDays)
	return [Math]::Pow(0.5, $ageDays / [Math]::Max(0.1, $RecencyHalfLifeDays))
}

function Normalize-Class([string]$ClassName) {
	$key = if ($null -eq $ClassName) { "" } else { $ClassName }
	$key = $key -replace "[\s_-]", ""
	$key = $key.ToUpperInvariant()
	switch ($key) {
		"DEATHKNIGHT" { return "DEATHKNIGHT" }
		"DEMONHUNTER" { return "DEMONHUNTER" }
		"DRUID" { return "DRUID" }
		"HUNTER" { return "HUNTER" }
		"MAGE" { return "MAGE" }
		"PALADIN" { return "PALADIN" }
		"PRIEST" { return "PRIEST" }
		"ROGUE" { return "ROGUE" }
		"SHAMAN" { return "SHAMAN" }
		"WARLOCK" { return "WARLOCK" }
		"WARRIOR" { return "WARRIOR" }
		default { return $key }
	}
}

function Add-CardCount([hashtable]$Counts, [string]$CardId, [int]$Count) {
	if ([string]::IsNullOrWhiteSpace($CardId)) {
		return
	}
	$current = 0
	if ($Counts.ContainsKey($CardId)) {
		$current = [int]$Counts[$CardId]
	}
	$Counts[$CardId] = $current + [Math]::Max(1, $Count)
}

function Decode-DeckCode([string]$DeckCode) {
	$deckString = [HearthDb.Deckstrings.DeckSerializer]::Deserialize($DeckCode)
	$counts = @{}
	foreach ($entry in $deckString.CardDbfIds.GetEnumerator()) {
		$card = [HearthDb.Cards]::GetFromDbfId([int]$entry.Key, $false)
		if ($card -and -not [string]::IsNullOrWhiteSpace([string]$card.Id)) {
			Add-CardCount $counts ([string]$card.Id) ([int]$entry.Value)
		}
	}
	return $counts
}

function Get-KnownHeroClass([int]$HeroDbfId) {
	switch ($HeroDbfId) {
		637 { return "MAGE" }
		274 { return "DRUID" }
		31 { return "HUNTER" }
		1066 { return "SHAMAN" }
		813 { return "PRIEST" }
		930 { return "ROGUE" }
		893 { return "WARLOCK" }
		671 { return "PALADIN" }
		7 { return "WARRIOR" }
		56550 { return "DEMONHUNTER" }
		78065 { return "DEATHKNIGHT" }
		default { return "" }
	}
}

function Convert-DeckCodeToInfo([string]$DeckCode) {
	$deckString = [HearthDb.Deckstrings.DeckSerializer]::Deserialize($DeckCode)
	$counts = @{}
	$classCounts = @{}
	foreach ($entry in $deckString.CardDbfIds.GetEnumerator()) {
		$card = [HearthDb.Cards]::GetFromDbfId([int]$entry.Key, $false)
		if ($card -and -not [string]::IsNullOrWhiteSpace([string]$card.Id)) {
			Add-CardCount $counts ([string]$card.Id) ([int]$entry.Value)
			$className = Normalize-Class ([string]$card.Class)
			if (-not [string]::IsNullOrWhiteSpace($className) -and $className -ne "NEUTRAL") {
				$current = if ($classCounts.ContainsKey($className)) { [int]$classCounts[$className] } else { 0 }
				$classCounts[$className] = $current + [int]$entry.Value
			}
		}
	}

	$heroClass = ""
	$hero = [HearthDb.Cards]::GetFromDbfId([int]$deckString.HeroDbfId, $false)
	if ($hero) {
		$heroClass = Normalize-Class ([string]$hero.Class)
	}
	if ([string]::IsNullOrWhiteSpace($heroClass) -or $heroClass -eq "NEUTRAL") {
		$heroClass = Get-KnownHeroClass ([int]$deckString.HeroDbfId)
	}
	if ([string]::IsNullOrWhiteSpace($heroClass) -and $classCounts.Count -gt 0) {
		$heroClass = [string]($classCounts.GetEnumerator() |
			Sort-Object @{ Expression = { $_.Value }; Descending = $true } |
			Select-Object -First 1 -ExpandProperty Key)
	}

	return [pscustomobject][ordered]@{
		cards = $counts
		player_class = $heroClass
	}
}

function Parse-OpponentCards([string]$Value) {
	$counts = @{}
	if ([string]::IsNullOrWhiteSpace($Value)) {
		return $counts
	}

	foreach ($part in ($Value -split ";")) {
		if ($part -notmatch "^(?<id>[^:]+):(?<count>\d+)$") {
			continue
		}
		$count = [Math]::Min(2, [int]$matches.count)
		Add-CardCount $counts ([string]$matches.id) $count
	}
	return $counts
}

function Get-CardWeight([string]$ClassName, [string]$CardId, [hashtable]$ClassCardFrequency) {
	$classMap = $ClassCardFrequency[$ClassName]
	if ($null -eq $classMap -or -not $classMap.ContainsKey($CardId)) {
		return 0.0
	}
	$frequency = [Math]::Max(1, [int]$classMap[$CardId])
	return 1.0 / [Math]::Sqrt([double]$frequency)
}

function Join-Candidates($Candidates) {
	return (@($Candidates) | Select-Object -First $TopCandidates | ForEach-Object {
		$pct = if ($_.PSObject.Properties.Name -contains "confidence_pct") {
			[int]$_.confidence_pct
		} elseif ($_.PSObject.Properties.Name -contains "coverage") {
			[int][Math]::Round([double]$_.coverage * 100.0)
		} else {
			0
		}
		"$($_.name):$pct%"
	}) -join " / "
}

function Get-SoftRecognitionDistribution($Candidates, [int]$Confidence, [double]$Temperature) {
	$candidateRows = @($Candidates | Where-Object { [int]$_.archetype_id -gt 0 })
	$hasEvidence = $candidateRows.Count -gt 0 -and
		[int]$candidateRows[0].relevant_cards -gt 0
	$knownMass = if ($hasEvidence) {
		[Math]::Max(0.0, [Math]::Min(1.0, $Confidence / 100.0))
	} else {
		0.0
	}
	$probabilities = New-Object System.Collections.Generic.List[object]

	if ($knownMass -gt 0.0 -and $candidateRows.Count -gt 0) {
		$effectiveTemperature = [Math]::Max(0.001, $Temperature)
		$maxScore = [double](($candidateRows | Measure-Object -Property score -Maximum).Maximum)
		$weightedCandidates = @($candidateRows | ForEach-Object {
			$exponent = ([double]$_.score - $maxScore) / $effectiveTemperature
			[pscustomobject][ordered]@{
				candidate = $_
				weight = [Math]::Exp($exponent)
			}
		})
		$totalSoftmaxWeight = [double](($weightedCandidates | Measure-Object -Property weight -Sum).Sum)
		if ($totalSoftmaxWeight -gt 0.0 -and
			-not [double]::IsNaN($totalSoftmaxWeight) -and
			-not [double]::IsInfinity($totalSoftmaxWeight)) {
			foreach ($weightedCandidate in $weightedCandidates) {
				$candidate = $weightedCandidate.candidate
				$probability = [Math]::Round(
					$knownMass * [double]$weightedCandidate.weight / $totalSoftmaxWeight,
					8)
				$probabilities.Add([pscustomobject][ordered]@{
					id = [int]$candidate.archetype_id
					name = [string]$candidate.name
					probability = $probability
				})
			}
		} else {
			$knownMass = 0.0
		}
	}

	$roundedKnownMass = if ($probabilities.Count -gt 0) {
		[double](($probabilities | Measure-Object -Property probability -Sum).Sum)
	} else {
		0.0
	}
	if ($roundedKnownMass -gt 1.0) {
		$scale = 1.0 / $roundedKnownMass
		foreach ($row in $probabilities) {
			$row.probability = [Math]::Round([double]$row.probability * $scale, 8)
		}
		$roundedKnownMass = [double](($probabilities | Measure-Object -Property probability -Sum).Sum)
	}
	$unknownProbability = [Math]::Max(0.0, 1.0 - $roundedKnownMass)
	$probabilities.Add([pscustomobject][ordered]@{
		id = 0
		name = "Unknown"
		probability = $unknownProbability
	})

	# Assign the floating-point normalization residue to Unknown so the serialized
	# distribution always sums to one in the same arithmetic used by consumers.
	$distributionTotal = [double](($probabilities | Measure-Object -Property probability -Sum).Sum)
	$probabilities[$probabilities.Count - 1].probability =
		[Math]::Max(0.0, [double]$probabilities[$probabilities.Count - 1].probability + (1.0 - $distributionTotal))

	$topProbability = 0.0
	$secondProbability = 0.0
	if ($candidateRows.Count -gt 0) {
		$topId = [int]$candidateRows[0].archetype_id
		$topProbabilityRow = $probabilities | Where-Object { [int]$_.id -eq $topId } | Select-Object -First 1
		if ($topProbabilityRow) {
			$topProbability = [double]$topProbabilityRow.probability
		}
		$knownProbabilities = @($probabilities |
			Where-Object { [int]$_.id -gt 0 } |
			Sort-Object @{ Expression = { [double]$_.probability }; Descending = $true })
		if ($knownProbabilities.Count -gt 1) {
			$secondProbability = [double]$knownProbabilities[1].probability
		}
	}
	$unknownProbability = [double]$probabilities[$probabilities.Count - 1].probability
	$tier = if (-not $hasEvidence -or $unknownProbability -ge $topProbability) {
		"unknown"
	} elseif ($topProbability -ge 0.70 -and ($topProbability - $secondProbability) -ge 0.25) {
		"confirmed"
	} elseif ($topProbability -ge 0.40 -and ($topProbability - $secondProbability) -ge 0.10) {
		"likely"
	} else {
		"mixed"
	}

	return [pscustomobject][ordered]@{
		distribution = [object[]]@($probabilities | ForEach-Object { $_ })
		known_probability = 1.0 - $unknownProbability
		unknown_probability = $unknownProbability
		top_probability = $topProbability
		tier = $tier
	}
}

if (-not (Test-Path $OpponentHistoryPath)) {
	throw "未找到对手历史记录：$OpponentHistoryPath"
}

$resolvedHdtAppPath = Resolve-HdtAppPath
[void][Reflection.Assembly]::LoadFrom((Join-Path $resolvedHdtAppPath "HearthDb.dll"))
[HearthDb.Cards]::LoadBaseData()

$libraryPath = ""
$librarySource = ""
if (Test-CurrentPatchBranchSnapshot $BranchPath) {
	$libraryPath = $BranchPath
	$librarySource = "current_patch_branch"
} elseif (-not [string]::IsNullOrWhiteSpace($DeckCodePath) -and (Test-Path $DeckCodePath)) {
	$libraryPath = $DeckCodePath
	$librarySource = "deckcodes"
} elseif (-not [string]::IsNullOrWhiteSpace($BranchPath) -and (Test-Path $BranchPath)) {
	$libraryPath = $BranchPath
	$librarySource = "branch_fallback"
} else {
	throw "未找到牌组代码快照。预期路径：$DeckCodePath；同时也缺少分支兜底文件：$BranchPath"
}

$branches = New-Object System.Collections.Generic.List[object]
$branchRanks = @{}
$invalidLibraryEntryCount = 0
foreach ($line in Get-Content -Path $libraryPath -Encoding UTF8) {
	if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) {
		continue
	}

	$parts = $line -split "`t"
	try {
		$name = ""
		$deckCode = ""
		$deckId = ""
		$archetypeId = 0
		$playerClass = ""
		$branchRank = 0
		$totalGames = 0
		$winRate = 0.0
		$pageDeckName = ""

		if ($librarySource -eq "current_patch_branch" -or $librarySource -eq "branch_fallback") {
			if ($parts.Count -lt 6 -or $parts[1] -cnotmatch "AAE[A-Za-z0-9+/=]{20,}") {
				continue
			}
			$name = [string]$parts[0]
			$deckCode = [string]$parts[1]
			$deckId = [string]$parts[2]
			$archetypeId = [int]$parts[3]
			$playerClass = Normalize-Class $parts[4]
			$branchRank = [int]$parts[5]
			$totalGames = if ($parts.Count -gt 6 -and $parts[6] -match "^\d+$") { [int]$parts[6] } else { 0 }
			$winRate = if ($parts.Count -gt 7 -and $parts[7] -match "^-?\d+(\.\d+)?$") { [double]$parts[7] } else { 0.0 }
			$pageDeckName = if ($parts.Count -gt 11) { [string]$parts[11] } else { "" }
		} else {
			if ($parts.Count -ge 2 -and $parts[1] -cmatch "^AAE[A-Za-z0-9+/=]{20,}$") {
				$name = [string]$parts[0]
				$deckCode = [string]$parts[1]
				$deckId = if ($parts.Count -gt 2) { [string]$parts[2] } else { "" }
				$archetypeText = if ($parts.Count -gt 3) { [string]$parts[3] } else { "" }
				if (-not [int]::TryParse($archetypeText, [ref]$archetypeId)) {
					continue
				}
			} else {
				$deckCodeMatch = [regex]::Match($line, "AAE[A-Za-z0-9+/=]{20,}")
				if (-not $deckCodeMatch.Success) {
					continue
				}
				$name = $line.Substring(0, $deckCodeMatch.Index).Trim().TrimEnd("|", "-").Trim()
				$deckCode = $deckCodeMatch.Value
			}
			if ($archetypeId -le 0) {
				continue
			}
			$pageDeckName = $name
		}

		$deckInfo = Convert-DeckCodeToInfo $deckCode
		$cards = $deckInfo.cards
		if ($cards.Count -eq 0) {
			continue
		}
		if ([string]::IsNullOrWhiteSpace($playerClass)) {
			$playerClass = Normalize-Class $deckInfo.player_class
		}
		if ($branchRank -le 0) {
			$key = [string]$archetypeId
			$currentRank = if ($branchRanks.ContainsKey($key)) { [int]$branchRanks[$key] } else { 0 }
			$branchRank = $currentRank + 1
			$branchRanks[$key] = $branchRank
		}
		if ([string]::IsNullOrWhiteSpace($name)) {
			$name = if ([string]::IsNullOrWhiteSpace($pageDeckName)) { "流派 $archetypeId" } else { $pageDeckName }
		}

		$branches.Add([pscustomobject][ordered]@{
			name = $name
			deck_code = $deckCode
			deck_id = $deckId
			archetype_id = $archetypeId
			player_class = $playerClass
			branch_rank = $branchRank
			total_games = $totalGames
			win_rate = $winRate
			english_name = $pageDeckName
			cards = $cards
		})
	} catch {
		$invalidLibraryEntryCount++
	}
}

if ($invalidLibraryEntryCount -gt 0) {
	Write-Warning "牌组识别语料中有 $invalidLibraryEntryCount 条记录无法解析，已统一跳过；请重新刷新识别语料。"
}

if ($branches.Count -eq 0) {
	throw "未能从 $libraryPath 加载任何可用的牌组代码条目。"
}

$archetypes = @($branches |
	Group-Object archetype_id |
	ForEach-Object {
		$items = @($_.Group)
		$cardSet = @{}
		foreach ($branch in $items) {
			foreach ($cardId in $branch.cards.Keys) {
				$cardSet[$cardId] = $true
			}
		}
		[pscustomobject][ordered]@{
			archetype_id = [int]$items[0].archetype_id
			name = [string]$items[0].name
			player_class = [string]$items[0].player_class
			branches = $items
			card_set = $cardSet
		}
	})

$classCardFrequency = @{}
foreach ($group in ($archetypes | Group-Object player_class)) {
	$frequency = @{}
	foreach ($archetype in $group.Group) {
		foreach ($cardId in $archetype.card_set.Keys) {
			$frequency[$cardId] = 1 + ($frequency[$cardId] | ForEach-Object { if ($_){ $_ } else { 0 } })
		}
	}
	$classCardFrequency[$group.Name] = $frequency
}

$now = Get-Date
$historyDays = [Math]::Max(0, $Days)
$defaultCutoff = if ($historyDays -gt 0) {
	$now.AddDays(-1 * $historyDays)
} else {
	[DateTime]::MinValue
}
$effectivePatchTime = Resolve-EffectivePatchTime
$sampleWindowStart = $defaultCutoff
$sampleWindow = if ($historyDays -gt 0) { "last_$($historyDays)_days" } else { "all_available_history" }
if ($UsePatchWindow -and $effectivePatchTime) {
	if ($effectivePatchTime -gt $sampleWindowStart) {
		$sampleWindowStart = $effectivePatchTime
		$sampleWindow = "current_patch"
	} else {
		$sampleWindow += "_within_current_patch"
	}
}
if ($HistoryClearedAt -ne [datetime]::MinValue -and $HistoryClearedAt -gt $sampleWindowStart) {
	$sampleWindowStart = $HistoryClearedAt
	$sampleWindow = "after_local_clear"
}
# Retain the parameter for command-line compatibility, but patch epochs are a
# hard evidence boundary. Pre-patch games must never reach downstream weights.
$prePatchWeightFactor = 0.0
$gameRows = New-Object System.Collections.Generic.List[object]
$summary = @{}

$sourceGames = @(Import-Csv -Path $OpponentHistoryPath -Delimiter "`t" |
	Where-Object {
		$start = Try-ParseDate ([string]$_.start_time)
		$start -and $start -ge $sampleWindowStart
	} |
	Sort-Object @{ Expression = { Try-ParseDate ([string]$_.start_time) }; Descending = $true })
if ($HistoryMatches -gt 0) {
	$sourceGames = @($sourceGames | Select-Object -First $HistoryMatches)
}
foreach ($game in $sourceGames) {
	$startTime = Try-ParseDate $game.start_time
	if ($null -eq $startTime -or $startTime -lt $sampleWindowStart) {
		continue
	}

	$className = Normalize-Class $game.opponent_hero
	$classUniverse = $classCardFrequency[$className]
	if ($null -eq $classUniverse) {
		$classUniverse = @{}
	}

	$observed = Parse-OpponentCards $game.opponent_cards
	$relevantCards = @($observed.Keys | Where-Object { $classUniverse.ContainsKey($_) })
	$relevantWeight = 0.0
	foreach ($cardId in $relevantCards) {
		$relevantWeight += (Get-CardWeight $className $cardId $classCardFrequency) *
			[Math]::Min(2, [int]$observed[$cardId])
	}

	$candidates = @($archetypes |
		Where-Object { $_.player_class -eq $className } |
		ForEach-Object {
			$archetype = $_
			$matchedWeight = 0.0
			$matchedCards = 0
			foreach ($cardId in $relevantCards) {
				if ($archetype.card_set.ContainsKey($cardId)) {
					$matchedCards++
					$matchedWeight += (Get-CardWeight $className $cardId $classCardFrequency) *
						[Math]::Min(2, [int]$observed[$cardId])
				}
			}

			$bestBranch = $null
			$bestBranchWeight = -1.0
			foreach ($branch in $archetype.branches) {
				$branchWeight = 0.0
				foreach ($cardId in $relevantCards) {
					if ($branch.cards.ContainsKey($cardId)) {
						$branchWeight += (Get-CardWeight $className $cardId $classCardFrequency) *
							[Math]::Min([int]$observed[$cardId], [int]$branch.cards[$cardId])
					}
				}
				if ($branchWeight -gt $bestBranchWeight) {
					$bestBranchWeight = $branchWeight
					$bestBranch = $branch
				}
			}

			$coverage = if ($relevantWeight -gt 0) { $matchedWeight / $relevantWeight } else { 0.0 }
			$branchCoverage = if ($relevantWeight -gt 0) { [Math]::Max(0.0, $bestBranchWeight) / $relevantWeight } else { 0.0 }
			[pscustomobject][ordered]@{
				archetype_id = [int]$archetype.archetype_id
				name = [string]$archetype.name
				player_class = [string]$archetype.player_class
				matched_cards = $matchedCards
				relevant_cards = $relevantCards.Count
				coverage = $coverage
				branch_coverage = $branchCoverage
				score = ($coverage * 100.0) + ($branchCoverage * 20.0) + ([Math]::Min(1.0, $relevantCards.Count / 6.0) * 10.0)
				best_branch_rank = if ($bestBranch) { [int]$bestBranch.branch_rank } else { 0 }
				best_branch_deck_id = if ($bestBranch) { [string]$bestBranch.deck_id } else { "" }
			}
		} |
		Sort-Object @{ Expression = { $_.score }; Descending = $true },
			@{ Expression = { $_.coverage }; Descending = $true },
			@{ Expression = { $_.matched_cards }; Descending = $true })

	$top = if ($candidates.Count -gt 0) { $candidates[0] } else { $null }
	$second = if ($candidates.Count -gt 1) { $candidates[1] } else { $null }
	$gap = if ($top -and $second) {
		[double]$top.score - [double]$second.score
	} elseif ($top) {
		20.0
	} else {
		0.0
	}
	$evidenceFactor = if ($top) {
		[Math]::Min(1.0, [double]$top.relevant_cards / 6.0)
	} else {
		0.0
	}
	$confidence = if ($top) {
		[int][Math]::Round(
			([double]$top.coverage * 70.0) +
			([double]$top.branch_coverage * 15.0) +
			([Math]::Min(20.0, [Math]::Max(0.0, $gap)) * 0.5) +
			($evidenceFactor * 10.0))
	} else {
		0
	}
	if ($top -and [int]$top.relevant_cards -lt $MinRelevantCards) {
		$confidence = [Math]::Min($confidence, 45)
	}
	$confidence = [Math]::Max(0, [Math]::Min(95, $confidence))
	$softRecognition = Get-SoftRecognitionDistribution `
		-Candidates $candidates `
		-Confidence $confidence `
		-Temperature $RecognitionSoftmaxTemperature
	$softDistributionJson = ConvertTo-Json `
		-InputObject $softRecognition.distribution `
		-Depth 4 `
		-Compress

	$baseWeight = if ($confidence -ge $MinConfidence) {
		[Math]::Max(0.25, $confidence / 100.0)
	} else {
		0.0
	}
	$patchWeight = if ($effectivePatchTime -and $startTime -lt $effectivePatchTime) {
		0.0
	} else {
		1.0
	}
	$recencyWeight = Get-RecencyWeight $startTime $now
	$weight = $baseWeight * $patchWeight * $recencyWeight
	$evidenceWeight = $patchWeight * $recencyWeight
	$softKnownWeight = $evidenceWeight * [double]$softRecognition.known_probability
	$softUnknownWeight = $evidenceWeight * [double]$softRecognition.unknown_probability

	$isWin = ([string]$game.result).Equals("Win", [StringComparison]::OrdinalIgnoreCase)
	$isLoss = ([string]$game.result).Equals("Loss", [StringComparison]::OrdinalIgnoreCase)
	$key = if ($top) { [string]$top.archetype_id } else { "" }
	if ($top -and $weight -gt 0 -and -not $summary.ContainsKey($key)) {
		$summary[$key] = [pscustomobject][ordered]@{
			archetype_id = [int]$top.archetype_id
			name = [string]$top.name
			player_class = [string]$top.player_class
			games = 0
			weighted_games = 0.0
			confidence_sum = 0.0
			wins = 0
			losses = 0
		}
	}
	if ($top -and $weight -gt 0) {
		$summary[$key].games++
		$summary[$key].weighted_games += $weight
		$summary[$key].confidence_sum += $confidence
		if ($isWin) { $summary[$key].wins++ }
		if ($isLoss) { $summary[$key].losses++ }
	}

	$gameRows.Add([pscustomobject][ordered]@{
		game_id = [string]$game.game_id
		start_time = [string]$game.start_time
		end_time = [string]$game.end_time
		result = [string]$game.result
		player_deck_name = [string]$game.player_deck_name
		player_hero = [string]$game.player_hero
		opponent_hero = [string]$game.opponent_hero
		opponent_class = $className
		opponent_card_count = [int]$game.opponent_card_count
		relevant_cards = if ($top) { [int]$top.relevant_cards } else { 0 }
		matched_cards = if ($top) { [int]$top.matched_cards } else { 0 }
		predicted_archetype_id = if ($top) { [int]$top.archetype_id } else { 0 }
		predicted_archetype = if ($top) { [string]$top.name } else { "" }
		confidence_pct = $confidence
		weight = [Math]::Round($weight, 4)
		patch_weight = [Math]::Round($patchWeight, 4)
		recency_weight = [Math]::Round($recencyWeight, 4)
		age_days = [Math]::Round([Math]::Max(0.0, ($now - $startTime).TotalDays), 3)
		coverage_pct = if ($top) { [Math]::Round([double]$top.coverage * 100.0, 2) } else { 0.0 }
		best_branch_rank = if ($top) { [int]$top.best_branch_rank } else { 0 }
		best_branch_deck_id = if ($top) { [string]$top.best_branch_deck_id } else { "" }
		candidate_archetypes = Join-Candidates $candidates
		recognition_model = $RecognitionModel
		top_probability_pct = [Math]::Round([double]$softRecognition.top_probability * 100.0, 2)
		unknown_probability_pct = [Math]::Round([double]$softRecognition.unknown_probability * 100.0, 2)
		recognition_tier = [string]$softRecognition.tier
		archetype_distribution_json = $softDistributionJson
		evidence_weight = [Math]::Round($evidenceWeight, 4)
		soft_known_weight = [Math]::Round($softKnownWeight, 4)
		soft_unknown_weight = [Math]::Round($softUnknownWeight, 4)
		format = [string]$game.format
		mode = [string]$game.game_mode
		replay_file = [string]$game.replay_file
		replay_path = [string]$game.replay_path
		hsreplay_upload_id = [string]$game.hsreplay_upload_id
		hsreplay_url = [string]$game.hsreplay_url
	})
}

$outputDirectory = Split-Path -Parent $OutputPrefix
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
	New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$gamesPath = "$OutputPrefix`_archetypes.tsv"
$summaryPath = "$OutputPrefix`_environment.tsv"
$jsonPath = "$OutputPrefix`_summary.json"

$gameHeader = "game_id`tstart_time`tend_time`tresult`tplayer_deck_name`tplayer_hero`topponent_hero`topponent_class`topponent_card_count`trelevant_cards`tmatched_cards`tpredicted_archetype_id`tpredicted_archetype`tconfidence_pct`tweight`tpatch_weight`trecency_weight`tage_days`tcoverage_pct`tbest_branch_rank`tbest_branch_deck_id`tcandidate_archetypes`treplay_file`treplay_path`thsreplay_upload_id`thsreplay_url`trecognition_model`ttop_probability_pct`tunknown_probability_pct`trecognition_tier`tarchetype_distribution_json`tevidence_weight`tsoft_known_weight`tsoft_unknown_weight`tformat`tmode"
$gameLines = New-Object System.Collections.Generic.List[string]
$gameLines.Add($gameHeader)
foreach ($row in $gameRows) {
	$values = @($row.game_id, $row.start_time, $row.end_time, $row.result,
		$row.player_deck_name, $row.player_hero, $row.opponent_hero, $row.opponent_class,
		$row.opponent_card_count, $row.relevant_cards, $row.matched_cards,
		$row.predicted_archetype_id, $row.predicted_archetype, $row.confidence_pct,
		$row.weight, $row.patch_weight, $row.recency_weight, $row.age_days,
		$row.coverage_pct, $row.best_branch_rank, $row.best_branch_deck_id,
		$row.candidate_archetypes, $row.replay_file, $row.replay_path,
		$row.hsreplay_upload_id, $row.hsreplay_url, $row.recognition_model,
		$row.top_probability_pct, $row.unknown_probability_pct, $row.recognition_tier,
		$row.archetype_distribution_json, $row.evidence_weight,
		$row.soft_known_weight, $row.soft_unknown_weight, $row.format, $row.mode)
	$gameLines.Add(($values | ForEach-Object { Format-TsvValue $_ }) -join "`t")
}
Set-Content -Path $gamesPath -Value $gameLines -Encoding UTF8

$totalWeighted = ($summary.Values | Measure-Object -Property weighted_games -Sum).Sum
$totalEvidenceWeight = ($gameRows | Measure-Object -Property evidence_weight -Sum).Sum
$totalSoftKnownWeight = ($gameRows | Measure-Object -Property soft_known_weight -Sum).Sum
$totalSoftUnknownWeight = ($gameRows | Measure-Object -Property soft_unknown_weight -Sum).Sum
$rank = 1
$summaryRows = @($summary.Values |
	Sort-Object @{ Expression = { $_.weighted_games }; Descending = $true },
		@{ Expression = { $_.games }; Descending = $true } |
	ForEach-Object {
		$finished = [int]$_.wins + [int]$_.losses
		[pscustomobject][ordered]@{
			rank = $rank++
			archetype_id = [int]$_.archetype_id
			name = [string]$_.name
			player_class = [string]$_.player_class
			games = [int]$_.games
			weighted_games = [Math]::Round([double]$_.weighted_games, 3)
			local_pct = if ($totalWeighted -gt 0) { [Math]::Round([double]$_.weighted_games / $totalWeighted * 100.0, 2) } else { 0.0 }
			avg_confidence = if ([int]$_.games -gt 0) { [Math]::Round([double]$_.confidence_sum / [int]$_.games, 1) } else { 0.0 }
			wins = [int]$_.wins
			losses = [int]$_.losses
			win_rate = if ($finished -gt 0) { [Math]::Round([int]$_.wins / [double]$finished * 100.0, 2) } else { "" }
		}
	})

$summaryHeader = "rank`tarchetype_id`tname`tplayer_class`tgames`tweighted_games`tlocal_pct`tavg_confidence`twins`tlosses`twin_rate"
$summaryLines = New-Object System.Collections.Generic.List[string]
$summaryLines.Add($summaryHeader)
foreach ($row in $summaryRows) {
	$values = @($row.rank, $row.archetype_id, $row.name, $row.player_class,
		$row.games, $row.weighted_games, $row.local_pct, $row.avg_confidence,
		$row.wins, $row.losses, $row.win_rate)
	$summaryLines.Add(($values | ForEach-Object { Format-TsvValue $_ }) -join "`t")
}
Set-Content -Path $summaryPath -Value $summaryLines -Encoding UTF8

$json = New-Object System.Collections.Specialized.OrderedDictionary
$patchTimeValue = $null
if ($effectivePatchTime) {
	$patchTimeValue = $effectivePatchTime.ToString("o")
}
[void]$json.Add("generated_at", (Get-Date).ToString("o"))
[void]$json.Add("opponent_history_path", $OpponentHistoryPath)
[void]$json.Add("deck_code_path", $DeckCodePath)
[void]$json.Add("branch_path", $BranchPath)
[void]$json.Add("library_path", $libraryPath)
[void]$json.Add("library_source", $librarySource)
[void]$json.Add("history_days", $Days)
[void]$json.Add("history_matches", [Math]::Max(0, $HistoryMatches))
$historyClearedAtValue = $null
if ($HistoryClearedAt -ne [datetime]::MinValue) {
	$historyClearedAtValue = $HistoryClearedAt.ToString("o")
}
[void]$json.Add("local_history_cleared_at", $historyClearedAtValue)
[void]$json.Add("sample_window", $sampleWindow)
[void]$json.Add("sample_window_start", $sampleWindowStart.ToString("o"))
[void]$json.Add("min_relevant_cards", $MinRelevantCards)
[void]$json.Add("min_confidence", $MinConfidence)
[void]$json.Add("recognition_model", $RecognitionModel)
[void]$json.Add("recognition_softmax_temperature", $RecognitionSoftmaxTemperature)
[void]$json.Add("patch_time", $patchTimeValue)
[void]$json.Add("pre_patch_weight", $prePatchWeightFactor)
[void]$json.Add("recency_half_life_days", $RecencyHalfLifeDays)
[void]$json.Add("use_patch_window", $UsePatchWindow)
[void]$json.Add("patch_marker_path", $PatchMarkerPath)
[void]$json.Add("deck_count", $branches.Count)
[void]$json.Add("branch_count", $branches.Count)
[void]$json.Add("archetype_count", $archetypes.Count)
[void]$json.Add("game_count", $gameRows.Count)
[void]$json.Add("weighted_game_count", [Math]::Round([double]$totalWeighted, 3))
[void]$json.Add("evidence_weight", [Math]::Round([double]$totalEvidenceWeight, 3))
[void]$json.Add("soft_known_weight", [Math]::Round([double]$totalSoftKnownWeight, 3))
[void]$json.Add("soft_unknown_weight", [Math]::Round([double]$totalSoftUnknownWeight, 3))
[void]$json.Add("games_path", $gamesPath)
[void]$json.Add("environment_path", $summaryPath)
[void]$json.Add("environment", [object[]]@($summaryRows))
$json | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8

Write-Host "本地环境统计已写入："
Write-Host "  $gamesPath"
Write-Host "  $summaryPath"
Write-Host "  $jsonPath"
Write-Host "已识别对局数：$($gameRows.Count)"
$displayColumns = @(
	@{ Label = "排名"; Expression = { $_.rank } },
	@{ Label = "流派 ID"; Expression = { $_.archetype_id } },
	@{ Label = "名称"; Expression = { $_.name } },
	@{ Label = "职业"; Expression = { $_.player_class } },
	@{ Label = "对局数"; Expression = { $_.games } },
	@{ Label = "加权对局"; Expression = { $_.weighted_games } },
	@{ Label = "本地占比"; Expression = { $_.local_pct } },
	@{ Label = "平均置信度"; Expression = { $_.avg_confidence } },
	@{ Label = "胜"; Expression = { $_.wins } },
	@{ Label = "负"; Expression = { $_.losses } },
	@{ Label = "胜率"; Expression = { $_.win_rate } }
)
$summaryRows | Format-Table -Property $displayColumns -AutoSize
