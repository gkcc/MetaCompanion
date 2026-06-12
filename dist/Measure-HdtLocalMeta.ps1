param(
	[string]$OpponentHistoryPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hdt_opponent_history.tsv",
	[string]$DeckCodePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_deckcodes.txt",
	[string]$BranchPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\archetype_deck_branches.tsv",
	[string]$OutputPrefix = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\local_meta",
	[string]$HdtAppPath = "",
	[int]$Days = 3,
	[int]$MinRelevantCards = 2,
	[int]$MinConfidence = 35,
	[int]$TopCandidates = 3,
	[datetime]$PatchTime = [datetime]::MinValue,
	[double]$PrePatchWeight = 0.35,
	[string]$PatchMarkerPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\patch_marker.txt"
)

$ErrorActionPreference = "Stop"

function Resolve-HdtAppPath {
	if (-not [string]::IsNullOrWhiteSpace($HdtAppPath) -and
		(Test-Path (Join-Path $HdtAppPath "HearthDb.dll"))) {
		return $HdtAppPath
	}

	$root = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
	if (-not (Test-Path $root)) {
		throw "HearthstoneDeckTracker local app directory was not found: $root"
	}

	$app = Get-ChildItem $root -Directory -Filter "app-*" |
		Where-Object { Test-Path (Join-Path $_.FullName "HearthDb.dll") } |
		Sort-Object LastWriteTime -Descending |
		Select-Object -First 1
	if (-not $app) {
		throw "No HDT app-* directory containing HearthDb.dll was found under $root"
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

function Resolve-HearthstoneExePath {
	$process = Get-Process -Name "Hearthstone" -ErrorAction SilentlyContinue | Select-Object -First 1
	if ($process -and -not [string]::IsNullOrWhiteSpace($process.Path) -and
		(Test-Path -LiteralPath $process.Path)) {
		return $process.Path
	}

	$candidates = @(
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

if (-not (Test-Path $OpponentHistoryPath)) {
	throw "Opponent history was not found: $OpponentHistoryPath"
}

$resolvedHdtAppPath = Resolve-HdtAppPath
[void][Reflection.Assembly]::LoadFrom((Join-Path $resolvedHdtAppPath "HearthDb.dll"))
[HearthDb.Cards]::LoadBaseData()

$libraryPath = ""
$librarySource = ""
if (-not [string]::IsNullOrWhiteSpace($DeckCodePath) -and (Test-Path $DeckCodePath)) {
	$libraryPath = $DeckCodePath
	$librarySource = "deckcodes"
} elseif (-not [string]::IsNullOrWhiteSpace($BranchPath) -and (Test-Path $BranchPath)) {
	$libraryPath = $BranchPath
	$librarySource = "branch_fallback"
} else {
	throw "No deck-code snapshot was found. Expected $DeckCodePath; branch fallback was also missing: $BranchPath"
}

$branches = New-Object System.Collections.Generic.List[object]
$branchRanks = @{}
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

		if ($librarySource -eq "branch_fallback") {
			if ($parts.Count -lt 6 -or $parts[1] -notmatch "AA[A-Za-z0-9+/=]+") {
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
			if ($parts.Count -ge 2 -and $parts[1] -match "^AA[A-Za-z0-9+/=]+$") {
				$name = [string]$parts[0]
				$deckCode = [string]$parts[1]
				$deckId = if ($parts.Count -gt 2) { [string]$parts[2] } else { "" }
				$archetypeText = if ($parts.Count -gt 3) { [string]$parts[3] } else { "" }
				if (-not [int]::TryParse($archetypeText, [ref]$archetypeId)) {
					continue
				}
			} else {
				$deckCodeMatch = [regex]::Match($line, "AA[A-Za-z0-9+/=]+")
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
			$name = if ([string]::IsNullOrWhiteSpace($pageDeckName)) { "Archetype $archetypeId" } else { $pageDeckName }
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
		Write-Warning "Ignoring invalid deck code library entry: $($_.Exception.Message)"
	}
}

if ($branches.Count -eq 0) {
	throw "No usable deck-code entries were loaded from $libraryPath"
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

$cutoff = (Get-Date).AddDays(-1 * [Math]::Max(1, $Days))
$effectivePatchTime = Resolve-EffectivePatchTime
$prePatchWeightFactor = [Math]::Max(0.0, [Math]::Min(1.0, $PrePatchWeight))
$gameRows = New-Object System.Collections.Generic.List[object]
$summary = @{}

foreach ($game in Import-Csv -Path $OpponentHistoryPath -Delimiter "`t") {
	$startTime = Try-ParseDate $game.start_time
	if ($null -eq $startTime -or $startTime -lt $cutoff) {
		continue
	}

	$className = Normalize-Class $game.opponent_hero
	$classUniverse = $classCardFrequency[$className]
	if ($null -eq $classUniverse) {
		continue
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

	if ($candidates.Count -eq 0) {
		continue
	}

	$top = $candidates[0]
	$second = if ($candidates.Count -gt 1) { $candidates[1] } else { $null }
	$gap = if ($second) { [double]$top.score - [double]$second.score } else { 20.0 }
	$evidenceFactor = [Math]::Min(1.0, [double]$top.relevant_cards / 6.0)
	$confidence = [int][Math]::Round(
		([double]$top.coverage * 70.0) +
		([double]$top.branch_coverage * 15.0) +
		([Math]::Min(20.0, [Math]::Max(0.0, $gap)) * 0.5) +
		($evidenceFactor * 10.0))
	if ([int]$top.relevant_cards -lt $MinRelevantCards) {
		$confidence = [Math]::Min($confidence, 45)
	}
	$confidence = [Math]::Max(0, [Math]::Min(95, $confidence))

	$baseWeight = if ($confidence -ge $MinConfidence) {
		[Math]::Max(0.25, $confidence / 100.0)
	} else {
		0.0
	}
	$patchWeight = if ($effectivePatchTime -and $startTime -lt $effectivePatchTime) {
		$prePatchWeightFactor
	} else {
		1.0
	}
	$weight = $baseWeight * $patchWeight

	$isWin = ([string]$game.result).Equals("Win", [StringComparison]::OrdinalIgnoreCase)
	$isLoss = ([string]$game.result).Equals("Loss", [StringComparison]::OrdinalIgnoreCase)
	$key = [string]$top.archetype_id
	if ($weight -gt 0 -and -not $summary.ContainsKey($key)) {
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
	if ($weight -gt 0) {
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
		relevant_cards = [int]$top.relevant_cards
		matched_cards = [int]$top.matched_cards
		predicted_archetype_id = [int]$top.archetype_id
		predicted_archetype = [string]$top.name
		confidence_pct = $confidence
		weight = [Math]::Round($weight, 4)
		patch_weight = [Math]::Round($patchWeight, 4)
		coverage_pct = [Math]::Round([double]$top.coverage * 100.0, 2)
		best_branch_rank = [int]$top.best_branch_rank
		best_branch_deck_id = [string]$top.best_branch_deck_id
		candidate_archetypes = Join-Candidates $candidates
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

$gameHeader = "game_id`tstart_time`tend_time`tresult`tplayer_deck_name`tplayer_hero`topponent_hero`topponent_class`topponent_card_count`trelevant_cards`tmatched_cards`tpredicted_archetype_id`tpredicted_archetype`tconfidence_pct`tweight`tpatch_weight`tcoverage_pct`tbest_branch_rank`tbest_branch_deck_id`tcandidate_archetypes`treplay_file`treplay_path`thsreplay_upload_id`thsreplay_url"
$gameLines = New-Object System.Collections.Generic.List[string]
$gameLines.Add($gameHeader)
foreach ($row in $gameRows) {
	$values = @($row.game_id, $row.start_time, $row.end_time, $row.result,
		$row.player_deck_name, $row.player_hero, $row.opponent_hero, $row.opponent_class,
		$row.opponent_card_count, $row.relevant_cards, $row.matched_cards,
		$row.predicted_archetype_id, $row.predicted_archetype, $row.confidence_pct,
		$row.weight, $row.patch_weight, $row.coverage_pct, $row.best_branch_rank, $row.best_branch_deck_id,
		$row.candidate_archetypes, $row.replay_file, $row.replay_path,
		$row.hsreplay_upload_id, $row.hsreplay_url)
	$gameLines.Add(($values | ForEach-Object { Format-TsvValue $_ }) -join "`t")
}
Set-Content -Path $gamesPath -Value $gameLines -Encoding UTF8

$totalWeighted = ($summary.Values | Measure-Object -Property weighted_games -Sum).Sum
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
[void]$json.Add("min_relevant_cards", $MinRelevantCards)
[void]$json.Add("min_confidence", $MinConfidence)
[void]$json.Add("patch_time", $patchTimeValue)
[void]$json.Add("pre_patch_weight", $prePatchWeightFactor)
[void]$json.Add("patch_marker_path", $PatchMarkerPath)
[void]$json.Add("deck_count", $branches.Count)
[void]$json.Add("branch_count", $branches.Count)
[void]$json.Add("archetype_count", $archetypes.Count)
[void]$json.Add("game_count", $gameRows.Count)
[void]$json.Add("weighted_game_count", [Math]::Round([double]$totalWeighted, 3))
[void]$json.Add("games_path", $gamesPath)
[void]$json.Add("environment_path", $summaryPath)
[void]$json.Add("environment", [object[]]@($summaryRows))
$json | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8

Write-Host "Wrote local meta:"
Write-Host "  $gamesPath"
Write-Host "  $summaryPath"
Write-Host "  $jsonPath"
Write-Host "Games classified: $($gameRows.Count)"
$summaryRows | Format-Table -AutoSize
