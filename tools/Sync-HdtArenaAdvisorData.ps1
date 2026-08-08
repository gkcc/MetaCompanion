[CmdletBinding()]
param(
	[string]$InputPath = "$env:APPDATA\HearthstoneDeckTracker\ArenaLastDrafts.xml",
	[string]$OutputRoot = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\AdvisorData\Arena",
	[string]$PatchMarkerPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\patch_marker.txt",
	[switch]$SelfTest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:RequiredSnapshotFiles = @(
	"card_pool.json",
	"card_priors.json",
	"hero_priors.json",
	"package_relations.json",
	"manifest.json"
)
$script:MaxXmlBytes = 32MB

function Write-Utf8NoBom([string]$Path, [string]$Value) {
	[System.IO.File]::WriteAllText($Path, $Value, $script:Utf8NoBom)
}

function Write-JsonFile([string]$Path, $Value) {
	$json = ConvertTo-Json -InputObject $Value -Depth 12
	Write-Utf8NoBom -Path $Path -Value ($json + [Environment]::NewLine)
}

function Format-UtcInstant([DateTimeOffset]$Value) {
	return $Value.ToUniversalTime().ToString(
		"yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
		[Globalization.CultureInfo]::InvariantCulture)
}

function ConvertTo-InstantOrNull([string]$Value) {
	if ([string]::IsNullOrWhiteSpace($Value)) {
		return $null
	}

	$result = [DateTimeOffset]::MinValue
	$styles = [Globalization.DateTimeStyles]::AllowWhiteSpaces -bor
		[Globalization.DateTimeStyles]::AssumeLocal
	if ([DateTimeOffset]::TryParse(
		$Value,
		[Globalization.CultureInfo]::InvariantCulture,
		$styles,
		[ref]$result)) {
		return $result
	}
	return $null
}

function Read-PatchMarker([string]$Path) {
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		throw "Arena advisor patch marker was not found: $Path"
	}

	$text = (Get-Content -LiteralPath $Path -Raw -Encoding UTF8).Trim()
	$value = ConvertTo-InstantOrNull $text
	if ($null -eq $value) {
		throw "Arena advisor patch marker is not a valid timestamp: $Path"
	}
	return $value
}

function Read-SafeXml([string]$Path) {
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		throw "HDT arena draft file was not found: $Path"
	}

	$item = Get-Item -LiteralPath $Path
	if ($item.Length -gt $script:MaxXmlBytes) {
		throw "HDT arena draft XML is larger than $script:MaxXmlBytes bytes."
	}

	$settings = New-Object System.Xml.XmlReaderSettings
	$settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
	$settings.XmlResolver = $null
	$settings.MaxCharactersInDocument = $script:MaxXmlBytes
	$settings.IgnoreComments = $true
	$settings.IgnoreProcessingInstructions = $true

	$reader = $null
	try {
		$reader = [System.Xml.XmlReader]::Create($item.FullName, $settings)
		$document = New-Object System.Xml.XmlDocument
		$document.XmlResolver = $null
		$document.PreserveWhitespace = $false
		$document.Load($reader)
	} catch {
		throw "HDT arena draft XML is malformed or unsafe: $($_.Exception.Message)"
	} finally {
		if ($null -ne $reader) {
			$reader.Dispose()
		}
	}

	if ($null -eq $document.DocumentElement -or
		-not [string]::Equals($document.DocumentElement.LocalName, "ArenaLastDrafts", [StringComparison]::Ordinal)) {
		throw "HDT arena draft XML has an unexpected root element."
	}

	return [pscustomobject]@{
		Document = $document
		Length = [long]$item.Length
		LastWriteUtc = [DateTimeOffset]$item.LastWriteTimeUtc
	}
}

function Get-DirectChildren($Node, [string]$LocalName) {
	$result = New-Object System.Collections.Generic.List[object]
	foreach ($child in @($Node.ChildNodes)) {
		if ($child.NodeType -eq [System.Xml.XmlNodeType]::Element -and
			[string]::Equals($child.LocalName, $LocalName, [StringComparison]::Ordinal)) {
			[void]$result.Add($child)
		}
	}
	return $result.ToArray()
}

function Get-FirstChildText($Node, [string]$LocalName) {
	$child = @(Get-DirectChildren -Node $Node -LocalName $LocalName | Select-Object -First 1)
	if ($child.Count -eq 0) {
		return ""
	}
	return ([string]$child[0].InnerText).Trim()
}

function Test-CardId([string]$Value) {
	return (-not [string]::IsNullOrWhiteSpace($Value) -and
		$Value.Length -le 160 -and
		$Value -match '\A[A-Za-z0-9][A-Za-z0-9_.:/-]*\z')
}

function Test-HeroId([string]$Value) {
	return (-not [string]::IsNullOrWhiteSpace($Value) -and
		$Value.StartsWith("HERO_", [StringComparison]::OrdinalIgnoreCase))
}

function Get-UniqueChildCardIds($Node, [string]$LocalName, [System.Collections.IDictionary]$Warnings) {
	$result = New-Object System.Collections.Generic.List[string]
	$seen = @{}
	foreach ($child in @(Get-DirectChildren -Node $Node -LocalName $LocalName)) {
		$id = ([string]$child.InnerText).Trim()
		if (-not (Test-CardId $id)) {
			$Warnings["invalid_card_id"] = [int]$Warnings["invalid_card_id"] + 1
			continue
		}
		if (-not $seen.ContainsKey($id)) {
			$seen[$id] = $true
			[void]$result.Add($id)
		}
	}
	return $result.ToArray()
}

function Get-OrAddCardStat([hashtable]$Map, [string]$CardId) {
	if (-not $Map.ContainsKey($CardId)) {
		$Map[$CardId] = [ordered]@{
			card_id = $CardId
			offered_count = 0
			picked_count = 0
			uniform_expected_pick_count = 0.0
			score_count = 0
			score_sum = 0.0
			score_min = $null
			score_max = $null
		}
	}
	return $Map[$CardId]
}

function Get-OrAddHeroStat([hashtable]$Map, [string]$HeroId) {
	if (-not $Map.ContainsKey($HeroId)) {
		$Map[$HeroId] = [ordered]@{
			hero_id = $HeroId
			offered_count = 0
			picked_count = 0
			uniform_expected_pick_count = 0.0
		}
	}
	return $Map[$HeroId]
}

function Get-OrAddRelationStat([hashtable]$Map, [string]$KeyCardId, [string]$RelatedCardId) {
	$mapKey = $KeyCardId + [char]0x1f + $RelatedCardId
	if (-not $Map.ContainsKey($mapKey)) {
		$Map[$mapKey] = [ordered]@{
			key_card_id = $KeyCardId
			related_card_id = $RelatedCardId
			observation_count = 0
			key_picked_count = 0
			related_already_picked_count = 0
		}
	}
	return $Map[$mapKey]
}

function ConvertTo-ValidScoreOrNull([string]$Value) {
	$result = 0.0
	if (-not [double]::TryParse(
		$Value,
		[Globalization.NumberStyles]::Float,
		[Globalization.CultureInfo]::InvariantCulture,
		[ref]$result)) {
		return $null
	}
	if ([double]::IsNaN($result) -or [double]::IsInfinity($result) -or
		$result -lt 0.0 -or $result -gt 10000.0) {
		return $null
	}
	return $result
}

function Limit-Prior([double]$Value) {
	return [Math]::Min(3.0, [Math]::Max(0.05, $Value))
}

function Round-Metric([double]$Value) {
	return [Math]::Round($Value, 6, [MidpointRounding]::AwayFromZero)
}

function Build-ArenaAdvisorSnapshot($XmlInfo, [DateTimeOffset]$PatchMarker) {
	$warnings = [ordered]@{
		invalid_start_time = 0
		invalid_card_id = 0
		invalid_score = 0
		orphan_score = 0
		empty_pick = 0
		picked_without_offer = 0
		invalid_package = 0
	}
	$counts = [ordered]@{
		drafts_seen = 0
		drafts_in_patch = 0
		drafts_before_patch = 0
		picks_seen_in_patch = 0
		card_picks = 0
		hero_picks = 0
	}
	$cardMap = @{}
	$heroMap = @{}
	$relationMap = @{}
	$packageKeyObservationMap = @{}

	$draftNodes = @($XmlInfo.Document.SelectNodes("//*[local-name()='Draft']"))
	foreach ($draft in $draftNodes) {
		$counts["drafts_seen"] = [int]$counts["drafts_seen"] + 1
		$startTime = ConvertTo-InstantOrNull ([string]$draft.GetAttribute("StartTime"))
		if ($null -eq $startTime) {
			$warnings["invalid_start_time"] = [int]$warnings["invalid_start_time"] + 1
			continue
		}
		if ($startTime.ToUniversalTime() -lt $PatchMarker.ToUniversalTime()) {
			$counts["drafts_before_patch"] = [int]$counts["drafts_before_patch"] + 1
			continue
		}

		$counts["drafts_in_patch"] = [int]$counts["drafts_in_patch"] + 1
		foreach ($pick in @(Get-DirectChildren -Node $draft -LocalName "Pick")) {
			$counts["picks_seen_in_patch"] = [int]$counts["picks_seen_in_patch"] + 1
			$choices = @(Get-UniqueChildCardIds -Node $pick -LocalName "Choice" -Warnings $warnings)
			$picked = (Get-FirstChildText -Node $pick -LocalName "Picked").Trim()
			if (-not [string]::IsNullOrWhiteSpace($picked) -and -not (Test-CardId $picked)) {
				$warnings["invalid_card_id"] = [int]$warnings["invalid_card_id"] + 1
				$picked = ""
			}
			if ($choices.Count -eq 0 -and [string]::IsNullOrWhiteSpace($picked)) {
				$warnings["empty_pick"] = [int]$warnings["empty_pick"] + 1
				continue
			}

			$isHeroPick = Test-HeroId $picked
			if (-not $isHeroPick) {
				foreach ($choice in $choices) {
					if (Test-HeroId $choice) {
						$isHeroPick = $true
						break
					}
				}
			}
			$offeredSet = @{}
			foreach ($choice in $choices) {
				$offeredSet[$choice] = $true
			}
			if (-not [string]::IsNullOrWhiteSpace($picked) -and -not $offeredSet.ContainsKey($picked)) {
				$warnings["picked_without_offer"] = [int]$warnings["picked_without_offer"] + 1
			}

			$uniformIncrement = if ($choices.Count -gt 0) { 1.0 / [double]$choices.Count } else { 0.0 }
			if ($isHeroPick) {
				$counts["hero_picks"] = [int]$counts["hero_picks"] + 1
				foreach ($choice in $choices) {
					if (-not (Test-HeroId $choice)) {
						continue
					}
					$stat = Get-OrAddHeroStat -Map $heroMap -HeroId $choice
					$stat["offered_count"] = [int]$stat["offered_count"] + 1
					$stat["uniform_expected_pick_count"] = [double]$stat["uniform_expected_pick_count"] + $uniformIncrement
				}
				if (Test-HeroId $picked) {
					$stat = Get-OrAddHeroStat -Map $heroMap -HeroId $picked
					$stat["picked_count"] = [int]$stat["picked_count"] + 1
				}
				continue
			}

			$counts["card_picks"] = [int]$counts["card_picks"] + 1
			foreach ($choice in $choices) {
				$stat = Get-OrAddCardStat -Map $cardMap -CardId $choice
				$stat["offered_count"] = [int]$stat["offered_count"] + 1
				$stat["uniform_expected_pick_count"] = [double]$stat["uniform_expected_pick_count"] + $uniformIncrement
			}
			if (-not [string]::IsNullOrWhiteSpace($picked)) {
				$stat = Get-OrAddCardStat -Map $cardMap -CardId $picked
				$stat["picked_count"] = [int]$stat["picked_count"] + 1
			}

			$scoreAllowed = @{}
			foreach ($choice in $choices) {
				$scoreAllowed[$choice] = $true
			}
			if (-not [string]::IsNullOrWhiteSpace($picked)) {
				$scoreAllowed[$picked] = $true
			}
			foreach ($scoreParent in @(Get-DirectChildren -Node $pick -LocalName "ArenasmithScores")) {
				foreach ($scoreNode in @(Get-DirectChildren -Node $scoreParent -LocalName "ArenasmithScore")) {
					$cardId = ([string]$scoreNode.GetAttribute("Card")).Trim()
					if (-not (Test-CardId $cardId)) {
						$warnings["invalid_card_id"] = [int]$warnings["invalid_card_id"] + 1
						continue
					}
					if (-not $scoreAllowed.ContainsKey($cardId)) {
						$warnings["orphan_score"] = [int]$warnings["orphan_score"] + 1
						continue
					}
					$score = ConvertTo-ValidScoreOrNull ([string]$scoreNode.GetAttribute("Score"))
					if ($null -eq $score) {
						$warnings["invalid_score"] = [int]$warnings["invalid_score"] + 1
						continue
					}
					$stat = Get-OrAddCardStat -Map $cardMap -CardId $cardId
					$stat["score_count"] = [int]$stat["score_count"] + 1
					$stat["score_sum"] = [double]$stat["score_sum"] + [double]$score
					if ($null -eq $stat["score_min"] -or [double]$score -lt [double]$stat["score_min"]) {
						$stat["score_min"] = [double]$score
					}
					if ($null -eq $stat["score_max"] -or [double]$score -gt [double]$stat["score_max"]) {
						$stat["score_max"] = [double]$score
					}
				}
			}

			$pickedBefore = @{}
			foreach ($cardId in @(Get-UniqueChildCardIds -Node $pick -LocalName "PickedCards" -Warnings $warnings)) {
				$pickedBefore[$cardId] = $true
			}
			foreach ($packagesNode in @(Get-DirectChildren -Node $pick -LocalName "Packages")) {
				foreach ($package in @(Get-DirectChildren -Node $packagesNode -LocalName "Package")) {
					$keyCardId = ([string]$package.GetAttribute("KeyCard")).Trim()
					if (-not (Test-CardId $keyCardId) -or (Test-HeroId $keyCardId)) {
						$warnings["invalid_package"] = [int]$warnings["invalid_package"] + 1
						continue
					}
					if (-not $packageKeyObservationMap.ContainsKey($keyCardId)) {
						$packageKeyObservationMap[$keyCardId] = 0
					}
					$packageKeyObservationMap[$keyCardId] = [int]$packageKeyObservationMap[$keyCardId] + 1
					$relatedIds = @(Get-UniqueChildCardIds -Node $package -LocalName "Card" -Warnings $warnings)
					foreach ($relatedCardId in $relatedIds) {
						if ([string]::Equals($keyCardId, $relatedCardId, [StringComparison]::OrdinalIgnoreCase)) {
							continue
						}
						$relation = Get-OrAddRelationStat -Map $relationMap -KeyCardId $keyCardId -RelatedCardId $relatedCardId
						$relation["observation_count"] = [int]$relation["observation_count"] + 1
						if ([string]::Equals($picked, $keyCardId, [StringComparison]::OrdinalIgnoreCase)) {
							$relation["key_picked_count"] = [int]$relation["key_picked_count"] + 1
						}
						if ($pickedBefore.ContainsKey($relatedCardId)) {
							$relation["related_already_picked_count"] = [int]$relation["related_already_picked_count"] + 1
						}
					}
				}
			}
		}
	}

	$patchId = "since-" + $PatchMarker.ToUniversalTime().ToString(
		"yyyyMMdd'T'HHmmss'Z'",
		[Globalization.CultureInfo]::InvariantCulture)
	$patch = [ordered]@{
		patch_id = $patchId
		marker_time_utc = Format-UtcInstant $PatchMarker
		filter = "draft_start_time_gte_patch_marker"
	}

	$cardStats = @($cardMap.Values | Where-Object {
		[int]$_["offered_count"] -gt 0 -or [int]$_["picked_count"] -gt 0
	} | Sort-Object { [string]$_["card_id"] })
	$totalScoreCount = 0
	$totalScoreSum = 0.0
	foreach ($stat in $cardStats) {
		$totalScoreCount += [int]$stat["score_count"]
		$totalScoreSum += [double]$stat["score_sum"]
	}
	$globalScoreMean = if ($totalScoreCount -gt 0) { $totalScoreSum / [double]$totalScoreCount } else { 0.0 }

	$poolRows = New-Object System.Collections.Generic.List[object]
	$cardPriorRows = New-Object System.Collections.Generic.List[object]
	foreach ($stat in $cardStats) {
		$offered = [int]$stat["offered_count"]
		$pickedCount = [int]$stat["picked_count"]
		$scoreCount = [int]$stat["score_count"]
		$pickRate = if ($offered -gt 0) { [double]$pickedCount / [double]$offered } else { 0.0 }
		$expected = [double]$stat["uniform_expected_pick_count"]
		$choicePreference = Limit-Prior (($pickedCount + 1.0) / ($expected + 1.0))
		$scoreMean = if ($scoreCount -gt 0) { [double]$stat["score_sum"] / [double]$scoreCount } else { $null }
		$priorWeight = $choicePreference
		if ($scoreCount -gt 0) {
			$scoreComponent = ([double]$scoreMean + 5.0) / ($globalScoreMean + 5.0)
			$priorWeight = Limit-Prior ((0.8 * $scoreComponent) + (0.2 * $choicePreference))
		}

		[void]$poolRows.Add([pscustomobject][ordered]@{
			card_id = [string]$stat["card_id"]
			offered_count = $offered
			picked_count = $pickedCount
			score_observation_count = $scoreCount
		})
		[void]$cardPriorRows.Add([pscustomobject][ordered]@{
			card_id = [string]$stat["card_id"]
			prior_weight = Round-Metric $priorWeight
			offered_count = $offered
			picked_count = $pickedCount
			pick_rate = Round-Metric $pickRate
			uniform_expected_pick_count = Round-Metric $expected
			arenasmith_score_count = $scoreCount
			arenasmith_score_mean = if ($null -eq $scoreMean) { $null } else { Round-Metric ([double]$scoreMean) }
			arenasmith_score_min = if ($null -eq $stat["score_min"]) { $null } else { Round-Metric ([double]$stat["score_min"]) }
			arenasmith_score_max = if ($null -eq $stat["score_max"]) { $null } else { Round-Metric ([double]$stat["score_max"]) }
		})
	}

	$heroRows = New-Object System.Collections.Generic.List[object]
	foreach ($stat in @($heroMap.Values | Sort-Object { [string]$_["hero_id"] })) {
		$offered = [int]$stat["offered_count"]
		$pickedCount = [int]$stat["picked_count"]
		$expected = [double]$stat["uniform_expected_pick_count"]
		$priorWeight = Limit-Prior (($pickedCount + 1.0) / ($expected + 1.0))
		[void]$heroRows.Add([pscustomobject][ordered]@{
			hero_id = [string]$stat["hero_id"]
			prior_weight = Round-Metric $priorWeight
			offered_count = $offered
			picked_count = $pickedCount
			pick_rate = if ($offered -gt 0) { Round-Metric ([double]$pickedCount / [double]$offered) } else { 0.0 }
			uniform_expected_pick_count = Round-Metric $expected
		})
	}

	$relationRows = New-Object System.Collections.Generic.List[object]
	foreach ($stat in @($relationMap.Values | Sort-Object {
		([string]$_["key_card_id"]) + [char]0x1f + ([string]$_["related_card_id"])
	})) {
		$observations = [int]$stat["observation_count"]
		$keyObservations = [int]$packageKeyObservationMap[[string]$stat["key_card_id"]]
		$relationRate = if ($keyObservations -gt 0) { [double]$observations / [double]$keyObservations } else { 0.0 }
		[void]$relationRows.Add([pscustomobject][ordered]@{
			key_card_id = [string]$stat["key_card_id"]
			related_card_id = [string]$stat["related_card_id"]
			prior_weight = Round-Metric (Limit-Prior (0.5 + $relationRate))
			observation_count = $observations
			key_package_observation_count = $keyObservations
			relation_observation_rate = Round-Metric $relationRate
			key_picked_count = [int]$stat["key_picked_count"]
			related_already_picked_count = [int]$stat["related_already_picked_count"]
		})
	}

	return [pscustomobject]@{
		Patch = $patch
		Warnings = $warnings
		Counts = $counts
		SourceLength = [long]$XmlInfo.Length
		SourceLastWriteUtc = [DateTimeOffset]$XmlInfo.LastWriteUtc
		CardPool = $poolRows.ToArray()
		CardPriors = $cardPriorRows.ToArray()
		HeroPriors = $heroRows.ToArray()
		PackageRelations = $relationRows.ToArray()
		GlobalScoreMean = $globalScoreMean
		TotalScoreCount = $totalScoreCount
	}
}

function Get-RequiredJsonProperty($Object, [string]$Name, [string]$Context) {
	if ($null -eq $Object) {
		throw "$Context is null; required property '$Name' is missing."
	}
	$property = $Object.PSObject.Properties[$Name]
	if ($null -eq $property) {
		throw "$Context is missing required property '$Name'."
	}
	return $property.Value
}

function ConvertTo-FiniteNumber($Value, [string]$Context) {
	if ($null -eq $Value -or $Value -is [bool]) {
		throw "$Context must be numeric."
	}
	try {
		$result = [Convert]::ToDouble($Value, [Globalization.CultureInfo]::InvariantCulture)
	} catch {
		throw "$Context must be numeric."
	}
	if ([double]::IsNaN($result) -or [double]::IsInfinity($result)) {
		throw "$Context must be finite."
	}
	return $result
}

function Assert-AdvisorSnapshot([string]$Directory) {
	foreach ($fileName in $script:RequiredSnapshotFiles) {
		$path = Join-Path $Directory $fileName
		if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
			throw "Arena advisor snapshot is incomplete; missing $fileName."
		}
	}

	foreach ($jsonFile in @(Get-ChildItem -LiteralPath $Directory -Filter "*.json" -File)) {
		$text = Get-Content -LiteralPath $jsonFile.FullName -Raw -Encoding UTF8
		if ($text -match '(?i)"(?:Player|DeckId|AccountId|BattleTag|player_id|deck_id|account_id|battle_tag)"\s*:') {
			throw "Arena advisor snapshot contains a forbidden identity field in $($jsonFile.Name)."
		}
		try {
			$null = $text | ConvertFrom-Json -ErrorAction Stop
		} catch {
			throw "Arena advisor snapshot contains invalid JSON in $($jsonFile.Name)."
		}
	}

	$manifestPath = Join-Path $Directory "manifest.json"
	$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
	if ([int](Get-RequiredJsonProperty $manifest "schema_version" "manifest") -ne 1 -or
		[string](Get-RequiredJsonProperty $manifest "dataset" "manifest") -ne "metacompanion.arena_advisor_priors" -or
		[string](Get-RequiredJsonProperty $manifest "status" "manifest") -ne "complete") {
		throw "Arena advisor manifest header is invalid."
	}

	$source = Get-RequiredJsonProperty $manifest "source" "manifest"
	if ([bool](Get-RequiredJsonProperty $source "complete_global_statistics" "manifest.source")) {
		throw "Arena advisor observations must not claim complete global statistics."
	}

	$manifestFiles = @(Get-RequiredJsonProperty $manifest "files" "manifest")
	$expectedDataFiles = @($script:RequiredSnapshotFiles | Where-Object { $_ -ne "manifest.json" })
	if ($manifestFiles.Count -ne $expectedDataFiles.Count) {
		throw "Arena advisor manifest file list is incomplete."
	}
	$seenFiles = @{}
	foreach ($file in $manifestFiles) {
		$name = [string](Get-RequiredJsonProperty $file "name" "manifest.files")
		if ($expectedDataFiles -notcontains $name -or $seenFiles.ContainsKey($name)) {
			throw "Arena advisor manifest contains an unexpected or duplicate file: $name"
		}
		$seenFiles[$name] = $true
		$path = Join-Path $Directory $name
		$actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
		$expectedHash = [string](Get-RequiredJsonProperty $file "sha256" "manifest.files")
		if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
			throw "Arena advisor snapshot hash mismatch: $name"
		}
		if ([long](Get-RequiredJsonProperty $file "bytes" "manifest.files") -ne (Get-Item -LiteralPath $path).Length) {
			throw "Arena advisor snapshot byte count mismatch: $name"
		}
	}

	$cardPool = Get-Content -LiteralPath (Join-Path $Directory "card_pool.json") -Raw -Encoding UTF8 | ConvertFrom-Json
	$cardPriors = Get-Content -LiteralPath (Join-Path $Directory "card_priors.json") -Raw -Encoding UTF8 | ConvertFrom-Json
	$heroPriors = Get-Content -LiteralPath (Join-Path $Directory "hero_priors.json") -Raw -Encoding UTF8 | ConvertFrom-Json
	$packageRelations = Get-Content -LiteralPath (Join-Path $Directory "package_relations.json") -Raw -Encoding UTF8 | ConvertFrom-Json
	if ([bool](Get-RequiredJsonProperty (Get-RequiredJsonProperty $cardPool "coverage" "card_pool") "is_complete" "card_pool.coverage")) {
		throw "Observed arena card pool must not be marked complete."
	}

	$poolCards = @(Get-RequiredJsonProperty $cardPool "cards" "card_pool")
	$priorCards = @(Get-RequiredJsonProperty $cardPriors "cards" "card_priors")
	$heroes = @(Get-RequiredJsonProperty $heroPriors "heroes" "hero_priors")
	$relations = @(Get-RequiredJsonProperty $packageRelations "relations" "package_relations")
	$counts = Get-RequiredJsonProperty $manifest "counts" "manifest"
	if ([int](Get-RequiredJsonProperty $counts "card_pool_cards" "manifest.counts") -ne $poolCards.Count -or
		[int](Get-RequiredJsonProperty $counts "card_priors" "manifest.counts") -ne $priorCards.Count -or
		[int](Get-RequiredJsonProperty $counts "hero_priors" "manifest.counts") -ne $heroes.Count -or
		[int](Get-RequiredJsonProperty $counts "package_relations" "manifest.counts") -ne $relations.Count) {
		throw "Arena advisor manifest row counts do not match the data files."
	}

	$seenCardIds = @{}
	foreach ($row in $priorCards) {
		$cardId = [string](Get-RequiredJsonProperty $row "card_id" "card_priors.cards")
		if (-not (Test-CardId $cardId) -or $seenCardIds.ContainsKey($cardId)) {
			throw "Arena advisor card priors contain an invalid or duplicate card id."
		}
		$seenCardIds[$cardId] = $true
		$weight = ConvertTo-FiniteNumber (Get-RequiredJsonProperty $row "prior_weight" "card_priors.cards") "card prior_weight"
		if ($weight -lt 0.05 -or $weight -gt 3.0) {
			throw "Arena advisor card prior_weight is out of range."
		}
	}
	foreach ($row in $heroes) {
		$heroId = [string](Get-RequiredJsonProperty $row "hero_id" "hero_priors.heroes")
		if (-not (Test-HeroId $heroId)) {
			throw "Arena advisor hero priors contain an invalid hero id."
		}
		$weight = ConvertTo-FiniteNumber (Get-RequiredJsonProperty $row "prior_weight" "hero_priors.heroes") "hero prior_weight"
		if ($weight -lt 0.05 -or $weight -gt 3.0) {
			throw "Arena advisor hero prior_weight is out of range."
		}
	}
	foreach ($row in $relations) {
		if (-not (Test-CardId ([string](Get-RequiredJsonProperty $row "key_card_id" "package_relations.relations"))) -or
			-not (Test-CardId ([string](Get-RequiredJsonProperty $row "related_card_id" "package_relations.relations"))) -or
			[int](Get-RequiredJsonProperty $row "observation_count" "package_relations.relations") -le 0) {
			throw "Arena advisor package relations contain an invalid row."
		}
	}

	$completionPath = Join-Path $Directory "publish-complete.json"
	if (Test-Path -LiteralPath $completionPath -PathType Leaf) {
		$completion = Get-Content -LiteralPath $completionPath -Raw -Encoding UTF8 | ConvertFrom-Json
		$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
		if (-not [string]::Equals(
			$manifestHash,
			[string](Get-RequiredJsonProperty $completion "manifest_sha256" "publish-complete"),
			[StringComparison]::OrdinalIgnoreCase) -or
			[string](Get-RequiredJsonProperty $completion "run_id" "publish-complete") -ne
			[string](Get-RequiredJsonProperty $manifest "run_id" "manifest")) {
			throw "Arena advisor publish-complete marker is invalid."
		}
	}
}

function Publish-AdvisorLatestAtomically([string]$RunDirectory, [string]$LatestDirectory, [string]$RunId) {
	Assert-AdvisorSnapshot -Directory $RunDirectory
	$parent = Split-Path -Parent $LatestDirectory
	$token = $RunId + "-" + [Guid]::NewGuid().ToString("N")
	$staging = Join-Path $parent (".latest-" + $token + ".staging")
	$backup = Join-Path $parent (".latest-" + $token + ".backup")
	$promoted = $false
	New-Item -ItemType Directory -Path $staging | Out-Null

	try {
		foreach ($fileName in $script:RequiredSnapshotFiles) {
			Copy-Item -LiteralPath (Join-Path $RunDirectory $fileName) -Destination (Join-Path $staging $fileName)
		}
		Assert-AdvisorSnapshot -Directory $staging
		$completion = [ordered]@{
			schema_version = 1
			run_id = $RunId
			published_at_utc = Format-UtcInstant ([DateTimeOffset]::UtcNow)
			manifest_sha256 = (Get-FileHash -LiteralPath (Join-Path $staging "manifest.json") -Algorithm SHA256).Hash
		}
		Write-JsonFile -Path (Join-Path $staging "publish-complete.json") -Value $completion
		Assert-AdvisorSnapshot -Directory $staging

		$hadLatest = Test-Path -LiteralPath $LatestDirectory -PathType Container
		if ($hadLatest) {
			[System.IO.Directory]::Move($LatestDirectory, $backup)
		}
		try {
			[System.IO.Directory]::Move($staging, $LatestDirectory)
			$promoted = $true
		} catch {
			if ($hadLatest -and -not (Test-Path -LiteralPath $LatestDirectory) -and
				(Test-Path -LiteralPath $backup -PathType Container)) {
				[System.IO.Directory]::Move($backup, $LatestDirectory)
			}
			throw
		}
	} finally {
		if (Test-Path -LiteralPath $staging) {
			Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
		}
		if ($promoted -and (Test-Path -LiteralPath $backup)) {
			Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
		}
	}
}

function Open-AdvisorDataLock([string]$OutputRoot, [int]$TimeoutSeconds = 10) {
	$lockPath = Join-Path $OutputRoot ".sync.lock"
	$watch = [Diagnostics.Stopwatch]::StartNew()
	while ($true) {
		try {
			return [System.IO.File]::Open(
				$lockPath,
				[System.IO.FileMode]::OpenOrCreate,
				[System.IO.FileAccess]::ReadWrite,
				[System.IO.FileShare]::None)
		} catch [System.IO.IOException] {
			if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
				throw "Timed out waiting for the arena advisor data lock: $lockPath"
			}
			Start-Sleep -Milliseconds 100
		}
	}
}

function Invoke-ArenaAdvisorExport([string]$SourcePath, [string]$DestinationRoot, [string]$MarkerPath) {
	$patchMarker = Read-PatchMarker -Path $MarkerPath
	$xmlInfo = Read-SafeXml -Path $SourcePath
	$aggregate = Build-ArenaAdvisorSnapshot -XmlInfo $xmlInfo -PatchMarker $patchMarker

	$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
	$runsRoot = Join-Path $DestinationRoot "runs"
	$latestDirectory = Join-Path $DestinationRoot "latest"
	New-Item -ItemType Directory -Force -Path $runsRoot | Out-Null
	$lock = Open-AdvisorDataLock -OutputRoot $DestinationRoot
	try {
		$now = [DateTimeOffset]::UtcNow
		$runId = $now.ToString("yyyyMMdd'T'HHmmssfff'Z'", [Globalization.CultureInfo]::InvariantCulture) +
			"-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
		$runDirectory = Join-Path $runsRoot $runId
		$runStaging = Join-Path $runsRoot ("." + $runId + ".staging")
		New-Item -ItemType Directory -Path $runStaging | Out-Null
		try {
			$cardPool = [ordered]@{
				schema_version = 1
				dataset = "metacompanion.arena_observed_card_pool"
				patch = $aggregate.Patch
				coverage = [ordered]@{
					kind = "observed_hdt_draft_choices"
					is_complete = $false
				}
				cards = @($aggregate.CardPool)
			}
			$cardPriors = [ordered]@{
				schema_version = 1
				dataset = "metacompanion.arena_card_priors"
				patch = $aggregate.Patch
				model = [ordered]@{
					name = "arenasmith_score_plus_local_choice_v1"
					score_observation_count = [int]$aggregate.TotalScoreCount
					global_observed_score_mean = if ([int]$aggregate.TotalScoreCount -gt 0) { Round-Metric ([double]$aggregate.GlobalScoreMean) } else { $null }
				}
				cards = @($aggregate.CardPriors)
			}
			$heroPriors = [ordered]@{
				schema_version = 1
				dataset = "metacompanion.arena_hero_priors"
				patch = $aggregate.Patch
				model = "local_choice_vs_uniform_shrunk_v1"
				heroes = @($aggregate.HeroPriors)
			}
			$packageRelations = [ordered]@{
				schema_version = 1
				dataset = "metacompanion.arena_package_relations"
				patch = $aggregate.Patch
				coverage = [ordered]@{
					kind = "observed_arenasmith_packages"
					is_complete = $false
				}
				relations = @($aggregate.PackageRelations)
			}

			Write-JsonFile -Path (Join-Path $runStaging "card_pool.json") -Value $cardPool
			Write-JsonFile -Path (Join-Path $runStaging "card_priors.json") -Value $cardPriors
			Write-JsonFile -Path (Join-Path $runStaging "hero_priors.json") -Value $heroPriors
			Write-JsonFile -Path (Join-Path $runStaging "package_relations.json") -Value $packageRelations

			$fileRecords = New-Object System.Collections.Generic.List[object]
			foreach ($fileName in @($script:RequiredSnapshotFiles | Where-Object { $_ -ne "manifest.json" })) {
				$path = Join-Path $runStaging $fileName
				[void]$fileRecords.Add([pscustomobject][ordered]@{
					name = $fileName
					bytes = [long](Get-Item -LiteralPath $path).Length
					sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
				})
			}
			$manifestCounts = [ordered]@{}
			foreach ($key in $aggregate.Counts.Keys) {
				$manifestCounts[$key] = $aggregate.Counts[$key]
			}
			$manifestCounts["card_pool_cards"] = @($aggregate.CardPool).Count
			$manifestCounts["card_priors"] = @($aggregate.CardPriors).Count
			$manifestCounts["hero_priors"] = @($aggregate.HeroPriors).Count
			$manifestCounts["package_relations"] = @($aggregate.PackageRelations).Count
			$manifest = [ordered]@{
				schema_version = 1
				dataset = "metacompanion.arena_advisor_priors"
				status = "complete"
				run_id = $runId
				generated_at_utc = Format-UtcInstant $now
				patch = $aggregate.Patch
				source = [ordered]@{
					kind = "HDT ArenaLastDrafts.xml with cached Arenasmith observations"
					file_name = "ArenaLastDrafts.xml"
					bytes = [long]$aggregate.SourceLength
					last_write_utc = Format-UtcInstant ([DateTimeOffset]$aggregate.SourceLastWriteUtc)
					complete_global_statistics = $false
					raw_xml_persisted = $false
				}
				privacy = [ordered]@{
					anonymized = $true
					draft_identity_persisted = $false
				}
				counts = $manifestCounts
				warnings = $aggregate.Warnings
				files = $fileRecords.ToArray()
			}
			Write-JsonFile -Path (Join-Path $runStaging "manifest.json") -Value $manifest
			Assert-AdvisorSnapshot -Directory $runStaging
			[System.IO.Directory]::Move($runStaging, $runDirectory)
		} finally {
			if (Test-Path -LiteralPath $runStaging) {
				Remove-Item -LiteralPath $runStaging -Recurse -Force -ErrorAction SilentlyContinue
			}
		}

		Publish-AdvisorLatestAtomically -RunDirectory $runDirectory -LatestDirectory $latestDirectory -RunId $runId
		return [pscustomobject]@{
			RunId = $runId
			RunDirectory = $runDirectory
			LatestDirectory = $latestDirectory
			DraftsIncluded = [int]$aggregate.Counts["drafts_in_patch"]
			CardPriors = @($aggregate.CardPriors).Count
			HeroPriors = @($aggregate.HeroPriors).Count
			PackageRelations = @($aggregate.PackageRelations).Count
		}
	} finally {
		if ($null -ne $lock) {
			$lock.Dispose()
		}
	}
}

function Assert-SelfTest([bool]$Condition, [string]$Message) {
	if (-not $Condition) {
		throw "Self-test failed: $Message"
	}
}

function Get-DirectoryFingerprint([string]$Directory) {
	$rows = @(Get-ChildItem -LiteralPath $Directory -File | Sort-Object Name | ForEach-Object {
		$_.Name + ":" + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
	})
	return ($rows -join "|")
}

function Invoke-ArenaAdvisorSelfTest {
	$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
	$tempRoot = Join-Path $tempBase ("MetaCompanion-ArenaAdvisor-" + [Guid]::NewGuid().ToString("N"))
	New-Item -ItemType Directory -Path $tempRoot | Out-Null
	try {
		$fixturePath = Join-Path $tempRoot "ArenaLastDrafts.xml"
		$markerPath = Join-Path $tempRoot "patch_marker.txt"
		$outputRoot = Join-Path $tempRoot "AdvisorData\Arena"
		Write-Utf8NoBom -Path $markerPath -Value "2026-07-01T00:00:00+08:00`n"
		$fixture = @'
<?xml version="1.0" encoding="utf-8"?>
<ArenaLastDrafts>
  <Draft Player="old-private-player" DeckId="old-secret-deck" StartTime="2026-06-20T10:00:00+08:00">
    <Pick><Picked>OLD_CARD</Picked><Choice>OLD_CARD</Choice><Choice>OLD_TWO</Choice><Choice>OLD_THREE</Choice><ArenasmithScores><ArenasmithScore Card="OLD_CARD" Score="99" /></ArenasmithScores></Pick>
  </Draft>
  <Draft Player="private-player-42" DeckId="secret-deck-9000" StartTime="2026-07-15T10:00:00+08:00">
    <Pick>
      <Picked>HERO_B</Picked><Choice>HERO_A</Choice><Choice>HERO_B</Choice><Choice>HERO_C</Choice>
      <ArenasmithScores /><Packages />
    </Pick>
    <Pick>
      <Picked>CARD_B</Picked><Choice>CARD_A</Choice><Choice>CARD_B</Choice><Choice>CARD_C</Choice>
      <ArenasmithScores>
        <ArenasmithScore Card="CARD_A" Score="10" />
        <ArenasmithScore Card="CARD_B" Score="40.5" />
        <ArenasmithScore Card="CARD_C" Score="20" />
      </ArenasmithScores>
      <PickedCards>SYNERGY_1</PickedCards>
      <Packages><Package KeyCard="CARD_B"><Card>SYNERGY_1</Card></Package></Packages>
    </Pick>
  </Draft>
</ArenaLastDrafts>
'@
		Write-Utf8NoBom -Path $fixturePath -Value $fixture
		$first = Invoke-ArenaAdvisorExport -SourcePath $fixturePath -DestinationRoot $outputRoot -MarkerPath $markerPath
		Assert-SelfTest ($first.DraftsIncluded -eq 1) "patch cutoff did not exclude the pre-patch draft"
		Assert-SelfTest ((Test-Path -LiteralPath (Join-Path $first.LatestDirectory "publish-complete.json"))) "latest was not atomically marked complete"

		$allJson = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -Filter "*.json" -File)
		foreach ($json in $allJson) {
			$text = Get-Content -LiteralPath $json.FullName -Raw -Encoding UTF8
			Assert-SelfTest (-not $text.Contains("private-player-42")) "player identity leaked into $($json.Name)"
			Assert-SelfTest (-not $text.Contains("secret-deck-9000")) "deck identity leaked into $($json.Name)"
		}

		$cardPriors = Get-Content -LiteralPath (Join-Path $first.LatestDirectory "card_priors.json") -Raw -Encoding UTF8 | ConvertFrom-Json
		$cardB = @($cardPriors.cards | Where-Object { $_.card_id -eq "CARD_B" })
		Assert-SelfTest ($cardB.Count -eq 1) "CARD_B prior is missing"
		Assert-SelfTest ([Math]::Abs([double]$cardB[0].arenasmith_score_mean - 40.5) -lt 0.000001) "ArenaSmith score was not preserved"
		Assert-SelfTest (@($cardPriors.cards | Where-Object { $_.card_id -eq "OLD_CARD" }).Count -eq 0) "pre-patch card leaked into priors"
		$relations = Get-Content -LiteralPath (Join-Path $first.LatestDirectory "package_relations.json") -Raw -Encoding UTF8 | ConvertFrom-Json
		Assert-SelfTest (@($relations.relations | Where-Object { $_.key_card_id -eq "CARD_B" -and $_.related_card_id -eq "SYNERGY_1" }).Count -eq 1) "package relation is missing"

		$firstRunId = $first.RunId
		$fixture2 = $fixture.Replace("CARD_C", "CARD_D")
		Write-Utf8NoBom -Path $fixturePath -Value $fixture2
		$second = Invoke-ArenaAdvisorExport -SourcePath $fixturePath -DestinationRoot $outputRoot -MarkerPath $markerPath
		Assert-SelfTest ($second.RunId -ne $firstRunId) "second export did not create a versioned run"
		Assert-SelfTest ((Test-Path -LiteralPath (Join-Path (Join-Path $outputRoot "runs") $firstRunId))) "atomic promotion removed the prior run"
		$latestPriors = Get-Content -LiteralPath (Join-Path $second.LatestDirectory "card_priors.json") -Raw -Encoding UTF8 | ConvertFrom-Json
		Assert-SelfTest (@($latestPriors.cards | Where-Object { $_.card_id -eq "CARD_D" }).Count -eq 1) "latest did not promote the second run"
		Assert-SelfTest (@($latestPriors.cards | Where-Object { $_.card_id -eq "CARD_C" }).Count -eq 0) "latest retained a stale file payload"

		$latestBeforeFailure = Get-DirectoryFingerprint -Directory $second.LatestDirectory
		$badRun = Join-Path $tempRoot "invalid-run"
		New-Item -ItemType Directory -Path $badRun | Out-Null
		foreach ($fileName in $script:RequiredSnapshotFiles) {
			Write-Utf8NoBom -Path (Join-Path $badRun $fileName) -Value "{}`n"
		}
		$validationFailed = $false
		try {
			Publish-AdvisorLatestAtomically -RunDirectory $badRun -LatestDirectory $second.LatestDirectory -RunId "invalid-run"
		} catch {
			$validationFailed = $true
		}
		Assert-SelfTest $validationFailed "invalid snapshot unexpectedly reached latest"
		Assert-SelfTest ((Get-DirectoryFingerprint -Directory $second.LatestDirectory) -eq $latestBeforeFailure) "latest changed after validation failure"

		Write-Utf8NoBom -Path $fixturePath -Value "<ArenaLastDrafts><Draft"
		$malformedFailed = $false
		try {
			$null = Invoke-ArenaAdvisorExport -SourcePath $fixturePath -DestinationRoot $outputRoot -MarkerPath $markerPath
		} catch {
			$malformedFailed = $true
		}
		Assert-SelfTest $malformedFailed "malformed XML unexpectedly succeeded"
		Assert-SelfTest ((Get-DirectoryFingerprint -Directory $second.LatestDirectory) -eq $latestBeforeFailure) "latest changed after malformed input"
		Write-Host "Arena advisor data self-test passed (privacy, patch cutoff, scores, atomic promotion, validation, malformed XML)."
	} finally {
		$resolvedRoot = [IO.Path]::GetFullPath($tempRoot)
		if ($resolvedRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
			[IO.Path]::GetFileName($resolvedRoot).StartsWith("MetaCompanion-ArenaAdvisor-", [StringComparison]::Ordinal)) {
			Remove-Item -LiteralPath $resolvedRoot -Recurse -Force -ErrorAction SilentlyContinue
		}
	}
}

if ($SelfTest) {
	Invoke-ArenaAdvisorSelfTest
	return
}

$result = Invoke-ArenaAdvisorExport -SourcePath $InputPath -DestinationRoot $OutputRoot -MarkerPath $PatchMarkerPath
Write-Host "Arena advisor snapshot published:"
Write-Host "  run:    $($result.RunDirectory)"
Write-Host "  latest: $($result.LatestDirectory)"
Write-Host "  patch drafts: $($result.DraftsIncluded); card priors: $($result.CardPriors); hero priors: $($result.HeroPriors); package relations: $($result.PackageRelations)"
