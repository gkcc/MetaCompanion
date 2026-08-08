param(
	[string]$DataDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion",
	[string]$CardPoolDirectory = "",
	[string]$DeckCodePath = "",
	[string]$MetaSummaryPath = "",
	[string]$PremiumManifestPath = "",
	[string]$BranchDeckPath = "",
	[string]$RuleCatalogPath = "",
	[string]$HdtAppPath = "",
	[string]$OutputDirectory = "$PSScriptRoot\..\artifacts\card-modeling\current",
	[int]$TopArchetypes = 20
)

$ErrorActionPreference = "Stop"

function Resolve-RequiredFile([string]$Path, [string]$Label) {
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		throw "$Label was not found: $Path"
	}
	return (Resolve-Path -LiteralPath $Path).Path
}

function Get-FileSha256([string]$Path) {
	$stream = [System.IO.File]::OpenRead($Path)
	try {
		$sha = [System.Security.Cryptography.SHA256]::Create()
		try {
			return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "")
		} finally {
			$sha.Dispose()
		}
	} finally {
		$stream.Dispose()
	}
}

function Resolve-HdtApp([string]$RequestedPath) {
	if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
		$resolved = (Resolve-Path -LiteralPath $RequestedPath).Path
		if (-not (Test-Path -LiteralPath (Join-Path $resolved "HearthDb.dll") -PathType Leaf)) {
			throw "HearthDb.dll was not found under HDT app path: $resolved"
		}
		return $resolved
	}

	$root = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
	$candidate = Get-ChildItem -LiteralPath $root -Directory -Filter "app-*" -ErrorAction SilentlyContinue |
		Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "HearthDb.dll") -PathType Leaf } |
		Sort-Object { try { [Version]($_.Name.Substring(4)) } catch { [Version]"0.0" } } -Descending |
		Select-Object -First 1
	if (-not $candidate) {
		throw "No HDT app directory containing HearthDb.dll was found under $root"
	}
	return $candidate.FullName
}

function Convert-ToStringArray($Values) {
	$result = New-Object System.Collections.Generic.List[string]
	foreach ($value in @($Values)) {
		if ($null -eq $value) { continue }
		$text = [string]$value
		if (-not [string]::IsNullOrWhiteSpace($text)) {
			$result.Add($text)
		}
	}
	return @($result | Sort-Object -Unique)
}

function Convert-ToCanonicalCardType($Value) {
	$type = ([string]$Value).Trim().ToUpperInvariant()
	# Newer HearthDb builds expose collectible spells as ABILITY while the
	# CardDefs/HDT wire contract and solver schema use SPELL for CARDTYPE=5.
	if ($type -eq "ABILITY") { return "SPELL" }
	return $type
}

if ([string]::IsNullOrWhiteSpace($CardPoolDirectory)) {
	$CardPoolDirectory = Join-Path $DataDirectory "AdvisorData\OfficialCardPools\latest"
}
if ([string]::IsNullOrWhiteSpace($DeckCodePath)) {
	$DeckCodePath = Join-Path $DataDirectory "hsreplay_deckcodes.txt"
}
if ([string]::IsNullOrWhiteSpace($MetaSummaryPath)) {
	$MetaSummaryPath = Join-Path $DataDirectory "Premium\Meta\latest\summary.tsv"
}
if ([string]::IsNullOrWhiteSpace($PremiumManifestPath)) {
	$PremiumManifestPath = Join-Path $DataDirectory "Premium\latest\manifest.json"
}
if ([string]::IsNullOrWhiteSpace($BranchDeckPath)) {
	$BranchDeckPath = Join-Path $DataDirectory "archetype_model_branches.tsv"
}
if ([string]::IsNullOrWhiteSpace($RuleCatalogPath)) {
	$RuleCatalogPath = Join-Path (Split-Path -Parent $PSScriptRoot) `
		"solver\metacompanion_solver\rules_data\hdt-visible-point-effects-v1.json"
}

$deckCodePath = Resolve-RequiredFile $DeckCodePath "HSReplay deck-code snapshot"
$metaSummaryPath = Resolve-RequiredFile $MetaSummaryPath "HSReplay Meta summary"
$premiumManifestPath = Resolve-RequiredFile $PremiumManifestPath "HSReplay Premium manifest"
$standardPoolPath = Resolve-RequiredFile (Join-Path $CardPoolDirectory "standard.json") "Official Standard card pool"
$poolManifestPath = Resolve-RequiredFile (Join-Path $CardPoolDirectory "manifest.json") "Official card-pool manifest"
$ruleCatalogPath = Resolve-RequiredFile $RuleCatalogPath "Structured rule catalog"
$branchDeckPath = if (Test-Path -LiteralPath $BranchDeckPath -PathType Leaf) {
	(Resolve-Path -LiteralPath $BranchDeckPath).Path
} else {
	$null
}
$hdtAppPath = Resolve-HdtApp $HdtAppPath

if ($TopArchetypes -lt 1) {
	throw "TopArchetypes must be positive."
}

[void][Reflection.Assembly]::LoadFrom((Join-Path $hdtAppPath "HearthDb.dll"))
[HearthDb.Cards]::LoadBaseData()

$metaRows = @(Import-Csv -LiteralPath $metaSummaryPath -Delimiter "`t" |
	Where-Object { $_.scope -eq "overall" } |
	Sort-Object { [int]$_.rank } |
	Select-Object -First $TopArchetypes)
if ($metaRows.Count -eq 0) {
	throw "Meta summary did not contain overall archetype rows."
}

$archetypeById = @{}
foreach ($row in $metaRows) {
	$id = [int]$row.archetype_id
	$archetypeById[$id] = [ordered]@{
		archetype_id = $id
		rank = [int]$row.rank
		name = [string]$row.name
		player_class = [string]$row.player_class
		total_games = [long]$row.total_games
		pct_of_total = [double]$row.pct_of_total
		win_rate = [double]$row.win_rate
	}
}

$premiumManifest = Get-Content -LiteralPath $premiumManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$premiumDeckIds = @{}
foreach ($item in @($premiumManifest.items | Where-Object { $_.endpoint -eq "list_decks_by_win_rate_v2" })) {
	$premiumDeckIds[[string]$item.deck_id] = $true
}

$branchDeckIds = @{}
$branchMetadata = [ordered]@{
	available = [bool]$branchDeckPath
	candidate_time_range = ""
	candidate_as_of = ""
	generated_at = ""
}
if ($branchDeckPath) {
	foreach ($line in Get-Content -LiteralPath $branchDeckPath -Encoding UTF8) {
		if ($line.StartsWith("# CandidateTimeRange:")) {
			$branchMetadata.candidate_time_range = $line.Substring("# CandidateTimeRange:".Length).Trim()
			continue
		}
		if ($line.StartsWith("# CandidateAsOf:")) {
			$branchMetadata.candidate_as_of = $line.Substring("# CandidateAsOf:".Length).Trim()
			continue
		}
		if ($line.StartsWith("# GeneratedAt:")) {
			$branchMetadata.generated_at = $line.Substring("# GeneratedAt:".Length).Trim()
			continue
		}
		if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) { continue }
		$parts = $line -split "`t"
		if ($parts.Count -ge 4 -and -not [string]::IsNullOrWhiteSpace($parts[2])) {
			$branchDeckIds[$parts[2]] = $true
		}
	}
}

$standardPage = Get-Content -LiteralPath $standardPoolPath -Raw -Encoding UTF8 | ConvertFrom-Json
$poolManifest = Get-Content -LiteralPath $poolManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$standardByDbf = @{}
foreach ($card in @($standardPage.cards)) {
	$standardByDbf[[int]$card.dbf_id] = $card
}

$ruleCatalog = Get-Content -LiteralPath $ruleCatalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$rulesByCardId = @{}
foreach ($rule in @($ruleCatalog.rules)) {
	foreach ($cardId in @($rule.card_ids)) {
		if (-not $rulesByCardId.ContainsKey([string]$cardId)) {
			$rulesByCardId[[string]$cardId] = New-Object System.Collections.Generic.List[object]
		}
		$effectKinds = @($rule.effects | ForEach-Object { [string]$_.kind } | Sort-Object -Unique)
		$rulesByCardId[[string]$cardId].Add([ordered]@{
			rule_id = [string]$rule.rule_id
			effect_kinds = $effectKinds
		})
	}
}

$deckRows = New-Object System.Collections.Generic.List[object]
$deckCardRows = New-Object System.Collections.Generic.List[object]
$rejectedDecks = New-Object System.Collections.Generic.List[object]
$seenDeckIds = @{}

foreach ($line in Get-Content -LiteralPath $deckCodePath -Encoding UTF8) {
	if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) { continue }
	$parts = $line -split "`t"
	if ($parts.Count -lt 4) { continue }
	$deckName = [string]$parts[0]
	$deckCode = [string]$parts[1]
	$deckId = [string]$parts[2]
	$archetypeId = 0
	if (-not [int]::TryParse([string]$parts[3], [ref]$archetypeId)) { continue }
	if (-not $archetypeById.ContainsKey($archetypeId)) { continue }
	if ($seenDeckIds.ContainsKey($deckId)) { continue }
	$seenDeckIds[$deckId] = $true

	try {
		$decoded = [HearthDb.Deckstrings.DeckSerializer]::Deserialize($deckCode)
	} catch {
		$rejectedDecks.Add([ordered]@{
			deck_id = $deckId
			archetype_id = $archetypeId
			reason = "deckstring_decode_failed"
		})
		continue
	}

	$cardsInDeck = New-Object System.Collections.Generic.List[object]
	foreach ($entry in $decoded.CardDbfIds.GetEnumerator()) {
		$cardsInDeck.Add([ordered]@{
			dbf_id = [int]$entry.Key
			count = [int]$entry.Value
			is_sideboard = $false
			sideboard_owner_dbf_id = $null
		})
	}
	foreach ($sideboard in $decoded.Sideboards.GetEnumerator()) {
		foreach ($entry in $sideboard.Value.GetEnumerator()) {
			$cardsInDeck.Add([ordered]@{
				dbf_id = [int]$entry.Key
				count = [int]$entry.Value
				is_sideboard = $true
				sideboard_owner_dbf_id = [int]$sideboard.Key
			})
		}
	}

	$missingDbfIds = @($cardsInDeck |
		Where-Object { -not $standardByDbf.ContainsKey([int]$_.dbf_id) } |
		ForEach-Object { [int]$_.dbf_id } |
		Sort-Object -Unique)
	if ($missingDbfIds.Count -gt 0) {
		$rejectedDecks.Add([ordered]@{
			deck_id = $deckId
			archetype_id = $archetypeId
			reason = "contains_non_standard_or_unmapped_cards"
			missing_dbf_ids = $missingDbfIds
		})
		continue
	}

	$archetype = $archetypeById[$archetypeId]
	$isPremium = $premiumDeckIds.ContainsKey($deckId)
	$isBranch = $branchDeckIds.ContainsKey($deckId)
	$deckRows.Add([ordered]@{
		deck_id = $deckId
		deck_name = $deckName
		deck_code = $deckCode
		archetype_id = $archetypeId
		archetype_name = $archetype.name
		archetype_rank = $archetype.rank
		player_class = $archetype.player_class
		archetype_meta_share_pct = $archetype.pct_of_total
		archetype_games = $archetype.total_games
		hero_dbf_id = [int]$decoded.HeroDbfId
		format = [string]$decoded.Format
		main_deck_cards = [int](($cardsInDeck | Where-Object { -not $_.is_sideboard } | Measure-Object -Property count -Sum).Sum)
		sideboard_cards = [int](($cardsInDeck | Where-Object { $_.is_sideboard } | Measure-Object -Property count -Sum).Sum)
		premium_current_patch_evidence = $isPremium
		branch_current_patch_evidence = $isBranch
		core_evidence = [bool]($isPremium -or $isBranch)
	})

	foreach ($deckCard in $cardsInDeck) {
		$official = $standardByDbf[[int]$deckCard.dbf_id]
		$runtime = [HearthDb.Cards]::GetFromDbfId([int]$deckCard.dbf_id, $false)
		if ($null -eq $runtime) {
			throw "HearthDb could not resolve dbf id $($deckCard.dbf_id)."
		}
		$referencedTags = @()
		if ($null -ne $runtime.Entity) {
			$referencedTags = @($runtime.Entity.ReferencedTags | ForEach-Object {
				[ordered]@{
					name = [string]$_.Name
					enum_id = [int]$_.EnumId
					value = [int]$_.Value
				}
			})
		}
		$deckCardRows.Add([ordered]@{
			deck_id = $deckId
			deck_name = $deckName
			archetype_id = $archetypeId
			archetype_name = $archetype.name
			archetype_rank = $archetype.rank
			player_class = $archetype.player_class
			archetype_meta_share_pct = $archetype.pct_of_total
			core_evidence = [bool]($isPremium -or $isBranch)
			is_sideboard = [bool]$deckCard.is_sideboard
			sideboard_owner_dbf_id = $deckCard.sideboard_owner_dbf_id
			copy_count = [int]$deckCard.count
			dbf_id = [int]$official.dbf_id
			card_id = [string]$official.card_id
			name = [string]$official.name
			official_text = [string]$official.text
			runtime_name = [string]$runtime.Name
			runtime_text = [string]$runtime.Text
			card_type = Convert-ToCanonicalCardType $runtime.Type
			card_class = [string]$runtime.Class
			card_set = [string]$runtime.Set
			rarity = [string]$runtime.Rarity
			cost = [int]$runtime.Cost
			attack = [int]$runtime.Attack
			health = [int]$runtime.Health
			durability = [int]$runtime.Durability
			spell_school = [string]$runtime.SpellSchool
			mechanics = @(Convert-ToStringArray $runtime.Mechanics)
			entourage_card_ids = @(Convert-ToStringArray $runtime.EntourageCardIds)
			referenced_tags = $referencedTags
		})
	}
}

if ($deckRows.Count -eq 0 -or $deckCardRows.Count -eq 0) {
	throw "No current Standard mainstream decks could be expanded."
}

$decksByArchetype = @{}
foreach ($deck in $deckRows) {
	$key = [int]$deck['archetype_id']
	if (-not $decksByArchetype.ContainsKey($key)) { $decksByArchetype[$key] = 0 }
	$decksByArchetype[$key]++
}

$cardRows = New-Object System.Collections.Generic.List[object]
foreach ($group in @($deckCardRows | Group-Object { $_['card_id'] })) {
	$rows = @($group.Group)
	$first = $rows[0]
	$uniqueDeckRows = @($rows | Group-Object { $_['deck_id'] } | ForEach-Object { $_.Group[0] })
	$uniqueCoreDecks = @($uniqueDeckRows | Where-Object { [bool]$_['core_evidence'] })
	$archetypePresence = New-Object System.Collections.Generic.List[object]
	$estimatedMetaShare = 0.0
	foreach ($archetypeGroup in @($uniqueDeckRows | Group-Object { [string]$_['archetype_id'] })) {
		$archetypeId = [int]$archetypeGroup.Name
		$coveredDecks = @($archetypeGroup.Group).Count
		$totalDecks = [int]$decksByArchetype[$archetypeId]
		$inclusionRate = if ($totalDecks -gt 0) { [double]$coveredDecks / $totalDecks } else { 0.0 }
		$meta = $archetypeById[$archetypeId]
		$shareContribution = [double]$meta.pct_of_total * $inclusionRate
		$estimatedMetaShare += $shareContribution
		$archetypePresence.Add([ordered]@{
			archetype_id = $archetypeId
			archetype_name = $meta.name
			player_class = $meta.player_class
			meta_share_pct = $meta.pct_of_total
			deck_variants_with_card = $coveredDecks
			deck_variants_total = $totalDecks
			variant_inclusion_rate = [Math]::Round($inclusionRate, 6)
			estimated_meta_share_contribution_pct = [Math]::Round($shareContribution, 6)
		})
	}

	$cardId = [string]$first['card_id']
	$matchedRules = if ($rulesByCardId.ContainsKey($cardId)) {
		@($rulesByCardId[$cardId].ToArray())
	} else {
		@()
	}
	$cardRows.Add([ordered]@{
		card_id = $cardId
		dbf_id = [int]$first['dbf_id']
		name = [string]$first['name']
		runtime_name = [string]$first['runtime_name']
		card_type = [string]$first['card_type']
		card_class = [string]$first['card_class']
		card_set = [string]$first['card_set']
		rarity = [string]$first['rarity']
		cost = [int]$first['cost']
		attack = [int]$first['attack']
		health = [int]$first['health']
		durability = [int]$first['durability']
		spell_school = [string]$first['spell_school']
		mechanics = @(Convert-ToStringArray ($rows | ForEach-Object { $_['mechanics'] }))
		entourage_card_ids = @($first['entourage_card_ids'])
		referenced_tags = @($first['referenced_tags'])
		official_text = [string]$first['official_text']
		runtime_text = [string]$first['runtime_text']
		deck_variant_count = $uniqueDeckRows.Count
		core_deck_variant_count = $uniqueCoreDecks.Count
		main_deck_variant_count = @($uniqueDeckRows | Where-Object { -not [bool]$_['is_sideboard'] }).Count
		sideboard_variant_count = @($uniqueDeckRows | Where-Object { [bool]$_['is_sideboard'] }).Count
		copy_count_across_variants = [int](($rows |
			ForEach-Object { [int]$_['copy_count'] } |
			Measure-Object -Sum).Sum)
		estimated_meta_share_pct = [Math]::Round($estimatedMetaShare, 6)
		archetypes = @($archetypePresence | Sort-Object archetype_id)
		existing_rule_coverage = [bool]($matchedRules.Count -gt 0)
		existing_rules = $matchedRules
	})
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$deckOutput = Join-Path $outputDirectory "mainstream-decks.json"
$deckCardOutput = Join-Path $outputDirectory "mainstream-deck-cards.json"
$cardOutput = Join-Path $outputDirectory "mainstream-cards.json"
$manifestOutput = Join-Path $outputDirectory "corpus-manifest.json"

@($deckRows | Sort-Object archetype_rank, deck_name, deck_id) |
	ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $deckOutput -Encoding UTF8
@($deckCardRows | Sort-Object archetype_rank, deck_id, is_sideboard, card_id) |
	ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $deckCardOutput -Encoding UTF8
@($cardRows | Sort-Object @{Expression="estimated_meta_share_pct";Descending=$true}, card_id) |
	ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $cardOutput -Encoding UTF8

$coreEvidenceDeckCount = 0
foreach ($deck in $deckRows) {
	if ([bool]$deck['core_evidence']) { $coreEvidenceDeckCount++ }
}
$existingRuleCardCount = 0
foreach ($card in $cardRows) {
	if ([bool]$card['existing_rule_coverage']) { $existingRuleCardCount++ }
}

$manifest = [ordered]@{
	schema_version = 1
	generated_at = [DateTimeOffset]::Now.ToString("o")
	scope = [ordered]@{
		format = "RANKED_STANDARD"
		rank_range = "DIAMOND_THROUGH_LEGEND"
		meta_time_range = "CURRENT_PATCH"
		top_archetypes = $metaRows.Count
		meta_share_pct = [Math]::Round((($metaRows | Measure-Object -Property pct_of_total -Sum).Sum), 6)
		selection = "Top overall Meta archetypes; HSReplay inventory deck codes retained only when every encoded card maps to the current official Standard pool."
		core_evidence = "Premium CURRENT_PATCH deck ids or CURRENT_PATCH branch deck ids."
	}
	counts = [ordered]@{
		selected_decks = $deckRows.Count
		core_evidence_decks = $coreEvidenceDeckCount
		rejected_decks = $rejectedDecks.Count
		unique_cards = $cardRows.Count
		cards_with_existing_rules = $existingRuleCardCount
		deck_card_rows = $deckCardRows.Count
	}
	card_defs = [ordered]@{
		build = [string]$poolManifest.card_defs.build
		entities = [int]$poolManifest.card_defs.entities
		bytes = [long]$poolManifest.card_defs.bytes
		sha256 = [string]$poolManifest.card_defs.sha256
	}
	branch_snapshot = $branchMetadata
	sources = @(
		[ordered]@{ name = "hsreplay_deckcodes"; path = $deckCodePath; sha256 = (Get-FileSha256 $deckCodePath) },
		[ordered]@{ name = "hsreplay_meta_summary"; path = $metaSummaryPath; sha256 = (Get-FileSha256 $metaSummaryPath) },
		[ordered]@{ name = "hsreplay_premium_manifest"; path = $premiumManifestPath; sha256 = (Get-FileSha256 $premiumManifestPath) },
		[ordered]@{ name = "official_standard_pool"; path = $standardPoolPath; sha256 = (Get-FileSha256 $standardPoolPath) },
		[ordered]@{ name = "official_pool_manifest"; path = $poolManifestPath; sha256 = (Get-FileSha256 $poolManifestPath) },
		[ordered]@{ name = "structured_rule_catalog"; path = $ruleCatalogPath; sha256 = (Get-FileSha256 $ruleCatalogPath) }
	)
	rejected_decks = $rejectedDecks.ToArray()
	outputs = [ordered]@{
		decks = [ordered]@{ file = "mainstream-decks.json"; sha256 = (Get-FileSha256 $deckOutput) }
		deck_cards = [ordered]@{ file = "mainstream-deck-cards.json"; sha256 = (Get-FileSha256 $deckCardOutput) }
		cards = [ordered]@{ file = "mainstream-cards.json"; sha256 = (Get-FileSha256 $cardOutput) }
	}
}
$manifest | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $manifestOutput -Encoding UTF8

Write-Host "Mainstream card corpus exported."
Write-Host "  Decks: $($deckRows.Count) (core evidence: $coreEvidenceDeckCount)"
Write-Host "  Cards: $($cardRows.Count)"
Write-Host "  Existing structured-rule coverage: $existingRuleCardCount"
Write-Host "  Rejected decks: $($rejectedDecks.Count)"
Write-Host "  Output: $outputDirectory"
