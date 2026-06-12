param(
	[string]$CookiePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt",
	[string]$Cookie = "",
	[string]$SummaryPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\summary.tsv",
	[string]$OutputPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\archetype_deck_branches.tsv",
	[string]$CacheDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Branches",
	[string]$CandidateTimeRange = "LAST_7_DAYS",
	[string]$RankRange = "DIAMOND_THROUGH_LEGEND",
	[string]$GameType = "RANKED_STANDARD",
	[string]$Region = "ALL",
	[string]$PlayerInitiative = "ALL",
	[int]$BranchesPerArchetype = 5,
	[int]$MinGames = 100,
	[int]$Parallelism = 6,
	[int]$AnalyticsTimeoutSeconds = 30,
	[int]$DeckPageTimeoutSeconds = 12,
	[int]$Retries = 2,
	[int]$ProgressEvery = 10,
	[int]$RequestDelayMs = 50,
	[switch]$UseCachedCandidates
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
	throw "curl.exe was not found."
}
if (-not (Test-Path $SummaryPath)) {
	throw "Meta summary not found: $SummaryPath. Run Sync-HSReplayMetaData.ps1 first."
}
if ($BranchesPerArchetype -le 0) {
	throw "BranchesPerArchetype must be greater than zero."
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory, $CacheDirectory | Out-Null

function ConvertTo-QueryString([hashtable]$Parameters) {
	$parts = New-Object System.Collections.Generic.List[string]
	foreach ($key in ($Parameters.Keys | Sort-Object)) {
		$value = $Parameters[$key]
		if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
			continue
		}
		$encodedKey = [System.Uri]::EscapeDataString([string]$key)
		$encodedValue = [System.Uri]::EscapeDataString([string]$value)
		$parts.Add("$encodedKey=$encodedValue")
	}
	return $parts -join "&"
}

function Get-HSReplayCookieArgs {
	if (-not [string]::IsNullOrWhiteSpace($Cookie)) {
		return @("-H", "Cookie: $Cookie")
	}

	if (-not (Test-Path $CookiePath)) {
		throw "No HSReplay cookie found. Create $CookiePath with your logged-in HSReplay Cookie header, or pass -CookiePath / -Cookie. Do not commit this file."
	}

	$cookieText = Get-Content -Path $CookiePath -Raw
	if ([string]::IsNullOrWhiteSpace($cookieText)) {
		throw "Cookie file is empty: $CookiePath"
	}

	$firstLine = ($cookieText -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
	if ($firstLine -like "# Netscape*" -or $cookieText -match "hsreplay\.net`t") {
		return @("-b", $CookiePath)
	}

	$cookieHeader = $cookieText.Trim()
	if ($cookieHeader.StartsWith("Cookie:", [StringComparison]::OrdinalIgnoreCase)) {
		$cookieHeader = $cookieHeader.Substring("Cookie:".Length).Trim()
	}
	return @("-H", "Cookie: $cookieHeader")
}

function Invoke-HSReplayJson([string]$Url, [string]$Name, [object[]]$CookieArgs) {
	for ($attempt = 1; $attempt -le $Retries; $attempt++) {
		$bodyPath = [System.IO.Path]::GetTempFileName()
		try {
			$statusText = & curl.exe -s -L -A "Mozilla/5.0" -H "Accept: application/json" @CookieArgs `
				--connect-timeout 10 --max-time $AnalyticsTimeoutSeconds -w "%{http_code}" -o $bodyPath $Url 2>$null
			$statusText = (@($statusText) -join "").Trim()
			$body = if (Test-Path $bodyPath) {
				Get-Content -Path $bodyPath -Encoding UTF8 -Raw
			} else {
				""
			}
		} finally {
			Remove-Item -LiteralPath $bodyPath -ErrorAction SilentlyContinue
		}

		if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($statusText)) {
			if ($attempt -lt $Retries) {
				Start-Sleep -Milliseconds (500 * $attempt)
				continue
			}
			throw "curl.exe failed while reading $Name"
		}
		if ($statusText -notmatch "^\d{3}$") {
			throw "Unable to read HTTP status for $Name. curl returned: $statusText"
		}

		$statusCode = [int]$statusText
		if ($statusCode -ge 200 -and $statusCode -lt 300) {
			return @{
				Url = $Url
				StatusCode = $statusCode
				Body = $body
			}
		}

		if ($statusCode -eq 401 -or $statusCode -eq 403) {
			throw "HSReplay returned HTTP $statusCode for $Name. The cookie is missing, expired, or does not have an active Premium subscription. Body: $body"
		}
		if ($statusCode -eq 400) {
			throw "HSReplay rejected $Name parameters with HTTP 400. Body: $body"
		}
		if ($attempt -lt $Retries) {
			Start-Sleep -Milliseconds (500 * $attempt)
			continue
		}
		throw "HSReplay returned HTTP $statusCode for $Name. Body: $body"
	}
}

function Convert-DeckPageToInfo([string]$DeckId, [string]$Html) {
	$decoded = [System.Net.WebUtility]::HtmlDecode([string]$Html)
	$deckStringMatch = [regex]::Match($decoded,
		'<meta[^>]+property=["'']x-hearthstone:deck:deckstring["''][^>]+content=["''](?<deck>AA[A-Za-z0-9+/=]+)["'']',
		[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	if (-not $deckStringMatch.Success) {
		$deckStringMatch = [regex]::Match($decoded,
			'<meta[^>]+content=["''](?<deck>AA[A-Za-z0-9+/=]+)["''][^>]+property=["'']x-hearthstone:deck:deckstring["'']',
			[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	}
	if (-not $deckStringMatch.Success) {
		$deckStringMatch = [regex]::Match($decoded, "Import it:\s*(?<deck>AA[A-Za-z0-9+/=]+)")
	}
	if (-not $deckStringMatch.Success) {
		$deckStringMatch = [regex]::Match($decoded, "(?<deck>AA[A-Za-z0-9+/=]+)")
	}
	if (-not $deckStringMatch.Success) {
		return $null
	}

	$nameMatch = [regex]::Match($decoded, 'data-deck-name\s*=\s*"(?<name>[^"]*)"',
		[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	$archetypeIdMatch = [regex]::Match($decoded, 'data-archetype-id\s*=\s*"(?<id>\d+)"',
		[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	$classMatch = [regex]::Match($decoded, 'data-deck-class\s*=\s*"(?<class>[^"]*)"',
		[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

	return [pscustomobject]@{
		deck_id = $DeckId
		deckstring = $deckStringMatch.Groups["deck"].Value
		page_deck_name = if ($nameMatch.Success) { $nameMatch.Groups["name"].Value } else { "" }
		page_archetype_id = if ($archetypeIdMatch.Success) { $archetypeIdMatch.Groups["id"].Value } else { "" }
		page_class = if ($classMatch.Success) { $classMatch.Groups["class"].Value } else { "" }
	}
}

function Format-TsvValue($Value) {
	if ($null -eq $Value) {
		return ""
	}
	return ([string]$Value) -replace "[`t`r`n]", " "
}

$summaryRows = @(Import-Csv -Path $SummaryPath -Delimiter "`t")
if ($summaryRows.Count -eq 0) {
	throw "No rows found in summary: $SummaryPath"
}

$targets = [ordered]@{}
foreach ($row in $summaryRows) {
	if ([string]::IsNullOrWhiteSpace($row.archetype_id)) {
		continue
	}
	$key = [string]$row.archetype_id
	if (-not $targets.Contains($key)) {
		$targets[$key] = [ordered]@{
			archetype_id = $key
			name = $row.name
			player_class = $row.player_class
			scopes = New-Object System.Collections.Generic.List[string]
		}
	}
	$targets[$key].scopes.Add([string]$row.scope)
}
if ($targets.Count -eq 0) {
	throw "No archetypes found in summary: $SummaryPath"
}

$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDirectory = Join-Path (Join-Path $CacheDirectory "runs") $runId
$latestDirectory = Join-Path $CacheDirectory "latest"
New-Item -ItemType Directory -Force -Path $runDirectory, $latestDirectory | Out-Null

$candidateFileName = "list_decks_by_win_rate_v2.json"
$candidateRunPath = Join-Path $runDirectory $candidateFileName
$candidateLatestPath = Join-Path $latestDirectory $candidateFileName

if ($UseCachedCandidates) {
	if (-not (Test-Path $candidateLatestPath)) {
		throw "Cached candidate file not found: $candidateLatestPath"
	}
	$candidateJson = Get-Content -Path $candidateLatestPath -Encoding UTF8 -Raw
	Copy-Item -LiteralPath $candidateLatestPath -Destination $candidateRunPath -Force
	$candidateUrl = "cached:$candidateLatestPath"
	$candidateStatusCode = 0
} else {
	$parameters = @{
		TimeRange = $CandidateTimeRange
		GameType = $GameType
		LeagueRankRange = $RankRange
		Region = $Region
		PlayerInitiative = $PlayerInitiative
	}
	$candidateUrl = "https://hsreplay.net/api/v1/analytics/query/list_decks_by_win_rate_v2/?$(ConvertTo-QueryString $parameters)"
	Write-Host "Fetching HSReplay deck branch candidates..."
	Write-Host "CandidateTimeRange=$CandidateTimeRange GameType=$GameType LeagueRankRange=$RankRange Region=$Region"
	$response = Invoke-HSReplayJson $candidateUrl "list_decks_by_win_rate_v2" (Get-HSReplayCookieArgs)
	$candidateJson = $response.Body
	$candidateStatusCode = $response.StatusCode
	Set-Content -Path $candidateRunPath -Value $candidateJson -Encoding UTF8
	Set-Content -Path $candidateLatestPath -Value $candidateJson -Encoding UTF8
}

$candidateData = $candidateJson | ConvertFrom-Json
if (-not $candidateData.series -or -not $candidateData.series[0].data) {
	throw "Candidate response did not contain series[0].data."
}

$candidatesByArchetype = @{}
foreach ($classProperty in $candidateData.series[0].data.PSObject.Properties) {
	foreach ($candidate in @($classProperty.Value)) {
		if ($null -eq $candidate.archetype_id -or [string]::IsNullOrWhiteSpace([string]$candidate.deck_id)) {
			continue
		}
		$archetypeId = [string]([int]$candidate.archetype_id)
		if (-not $targets.Contains($archetypeId)) {
			continue
		}
		if (-not $candidatesByArchetype.ContainsKey($archetypeId)) {
			$candidatesByArchetype[$archetypeId] = New-Object System.Collections.Generic.List[object]
		}
		$candidatesByArchetype[$archetypeId].Add([pscustomobject]@{
			player_class = [string]$classProperty.Name
			archetype_id = $archetypeId
			deck_id = [string]$candidate.deck_id
			total_games = [int]$candidate.total_games
			win_rate = [double]$candidate.win_rate
			avg_game_length_seconds = if ($null -ne $candidate.avg_game_length_seconds) { [double]$candidate.avg_game_length_seconds } else { $null }
			avg_num_player_turns = if ($null -ne $candidate.avg_num_player_turns) { [double]$candidate.avg_num_player_turns } else { $null }
		})
	}
}

$selectedCandidates = New-Object System.Collections.Generic.List[object]
$missingArchetypes = New-Object System.Collections.Generic.List[object]
foreach ($targetKey in $targets.Keys) {
	$target = $targets[$targetKey]
	if (-not $candidatesByArchetype.ContainsKey($targetKey)) {
		$missingArchetypes.Add([pscustomobject]@{
			archetype_id = $targetKey
			name = $target.name
			player_class = $target.player_class
			reason = "no_candidates"
		})
		continue
	}

	$rank = 1
	$eligible = @($candidatesByArchetype[$targetKey] |
		Where-Object { $_.total_games -ge $MinGames } |
		Sort-Object @{ Expression = { $_.total_games }; Descending = $true }, @{ Expression = { $_.win_rate }; Descending = $true } |
		Select-Object -First $BranchesPerArchetype)

	if ($eligible.Count -eq 0) {
		$missingArchetypes.Add([pscustomobject]@{
			archetype_id = $targetKey
			name = $target.name
			player_class = $target.player_class
			reason = "below_min_games"
		})
		continue
	}

	foreach ($candidate in $eligible) {
		$selectedCandidates.Add([pscustomobject]@{
			branch_rank = $rank
			archetype_id = $targetKey
			archetype_name = $target.name
			player_class = $target.player_class
			source_scopes = (@($target.scopes | Select-Object -Unique) -join ",")
			deck_id = $candidate.deck_id
			total_games = $candidate.total_games
			win_rate = $candidate.win_rate
			avg_game_length_seconds = $candidate.avg_game_length_seconds
			avg_num_player_turns = $candidate.avg_num_player_turns
		})
		$rank++
	}
}

if ($selectedCandidates.Count -eq 0) {
	throw "No branch candidates were selected. Try lowering -MinGames."
}

Write-Host "Selected $($selectedCandidates.Count) branch candidates across $($targets.Count) archetypes."
Write-Host "Fetching HSReplay deck pages..."

$resultsByDeckId = @{}
$failedPages = New-Object System.Collections.Generic.List[object]
$uniqueDeckIds = @($selectedCandidates | Select-Object -ExpandProperty deck_id -Unique)

if ($Parallelism -le 1) {
	$checked = 0
	foreach ($deckId in $uniqueDeckIds) {
		$deckUrl = "https://hsreplay.net/decks/$deckId/"
		$body = $null
		for ($attempt = 1; $attempt -le $Retries; $attempt++) {
			$body = & curl.exe -s -L -A "Mozilla/5.0" -H "Accept: text/html,*/*" `
				--connect-timeout 10 --max-time $DeckPageTimeoutSeconds $deckUrl 2>$null
			if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($body)) {
				break
			}
			if ($attempt -lt $Retries) {
				Start-Sleep -Milliseconds (500 * $attempt)
			}
		}

		$info = if (-not [string]::IsNullOrWhiteSpace($body)) { Convert-DeckPageToInfo $deckId $body } else { $null }
		if ($info) {
			$resultsByDeckId[$deckId] = $info
		} else {
			$failedPages.Add([pscustomobject]@{ deck_id = $deckId; url = $deckUrl; reason = "deckstring_not_found" })
		}

		$checked++
		if ($RequestDelayMs -gt 0) {
			Start-Sleep -Milliseconds $RequestDelayMs
		}
		if ($ProgressEvery -gt 0 -and ($checked % $ProgressEvery -eq 0 -or $checked -eq $uniqueDeckIds.Count)) {
			Write-Host "Checked $checked/$($uniqueDeckIds.Count), extracted $($resultsByDeckId.Count), failed $($failedPages.Count)."
		}
	}
} else {
	$queue = New-Object System.Collections.Generic.Queue[string]
	foreach ($deckId in $uniqueDeckIds) {
		$queue.Enqueue($deckId)
	}
	$jobs = @{}
	$checked = 0
	$jobScript = {
		param([string]$DeckId, [int]$Retries, [int]$TimeoutSeconds)
		function Invoke-CurlTextLocal([string]$Url, [int]$Retries, [int]$TimeoutSeconds) {
			for ($attempt = 1; $attempt -le $Retries; $attempt++) {
				$text = & curl.exe -s -L -A "Mozilla/5.0" -H "Accept: text/html,*/*" `
					--connect-timeout 10 --max-time $TimeoutSeconds $Url 2>$null
				if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($text)) {
					return [string]$text
				}
				if ($attempt -lt $Retries) {
					Start-Sleep -Milliseconds (500 * $attempt)
				}
			}
			throw "curl.exe failed while reading $Url"
		}

		$deckUrl = "https://hsreplay.net/decks/$DeckId/"
		try {
			[pscustomobject]@{
				DeckId = $DeckId
				Url = $deckUrl
				Html = (Invoke-CurlTextLocal $deckUrl $Retries $TimeoutSeconds)
				Error = $null
			}
		} catch {
			[pscustomobject]@{
				DeckId = $DeckId
				Url = $deckUrl
				Html = $null
				Error = $_.Exception.Message
			}
		}
	}

	while ($queue.Count -gt 0 -or $jobs.Count -gt 0) {
		while ($queue.Count -gt 0 -and $jobs.Count -lt $Parallelism) {
			$deckId = $queue.Dequeue()
			$job = Start-Job -ScriptBlock $jobScript -ArgumentList $deckId, $Retries, $DeckPageTimeoutSeconds
			$jobs[$job.Id] = $job
		}

		$finishedJob = Wait-Job -Job @($jobs.Values) -Any -Timeout 2
		if (-not $finishedJob) {
			continue
		}

		$result = Receive-Job -Job $finishedJob
		Remove-Job -Job $finishedJob
		$jobs.Remove($finishedJob.Id)
		$checked++

		if ($result.Error) {
			$failedPages.Add([pscustomobject]@{ deck_id = $result.DeckId; url = $result.Url; reason = $result.Error })
		} else {
			$info = Convert-DeckPageToInfo $result.DeckId $result.Html
			if ($info) {
				$resultsByDeckId[$result.DeckId] = $info
			} else {
				$failedPages.Add([pscustomobject]@{ deck_id = $result.DeckId; url = $result.Url; reason = "deckstring_not_found" })
			}
		}

		if ($ProgressEvery -gt 0 -and ($checked % $ProgressEvery -eq 0 -or $checked -eq $uniqueDeckIds.Count)) {
			Write-Host "Checked $checked/$($uniqueDeckIds.Count), extracted $($resultsByDeckId.Count), failed $($failedPages.Count)."
		}
	}
}

$outputLines = New-Object System.Collections.Generic.List[string]
$outputLines.Add("# Generated by tools/Sync-HSReplayArchetypeDecks.ps1")
$outputLines.Add("# Summary: $SummaryPath")
$outputLines.Add("# CandidateSource: $candidateUrl")
$outputLines.Add("# CandidateAsOf: $($candidateData.as_of)")
$outputLines.Add("# CandidateTimeRange: $CandidateTimeRange")
$outputLines.Add("# MinGames: $MinGames")
$outputLines.Add("# BranchesPerArchetype: $BranchesPerArchetype")
$outputLines.Add("# GeneratedAt: $((Get-Date).ToString("o"))")
$outputLines.Add("# Format: archetypeName<TAB>deckstring<TAB>deckId<TAB>archetypeId<TAB>playerClass<TAB>branchRank<TAB>totalGames<TAB>winRate<TAB>avgGameLengthSeconds<TAB>avgNumPlayerTurns<TAB>sourceScopes<TAB>pageDeckName")
$outputLines.Add("")

$written = 0
foreach ($candidate in ($selectedCandidates | Sort-Object @{ Expression = { [int]$_.archetype_id } }, branch_rank)) {
	if (-not $resultsByDeckId.ContainsKey($candidate.deck_id)) {
		continue
	}
	$deckInfo = $resultsByDeckId[$candidate.deck_id]
	$values = @(
		$candidate.archetype_name,
		$deckInfo.deckstring,
		$candidate.deck_id,
		$candidate.archetype_id,
		$candidate.player_class,
		$candidate.branch_rank,
		$candidate.total_games,
		$candidate.win_rate,
		$candidate.avg_game_length_seconds,
		$candidate.avg_num_player_turns,
		$candidate.source_scopes,
		$deckInfo.page_deck_name
	)
	$outputLines.Add(($values | ForEach-Object { Format-TsvValue $_ }) -join "`t")
	$written++
}

if ($written -eq 0) {
	throw "No deckstrings were extracted from selected branch candidates."
}

$tempOutputPath = Join-Path $outputDirectory ((Split-Path -Leaf $OutputPath) + ".tmp")
Set-Content -Path $tempOutputPath -Value $outputLines -Encoding UTF8
Move-Item -LiteralPath $tempOutputPath -Destination $OutputPath -Force

$manifest = [ordered]@{
	generated_at = (Get-Date).ToString("o")
	summary_path = $SummaryPath
	output_path = $OutputPath
	candidate_time_range = $CandidateTimeRange
	game_type = $GameType
	rank_range = $RankRange
	region = $Region
	player_initiative = $PlayerInitiative
	branches_per_archetype = $BranchesPerArchetype
	min_games = $MinGames
	target_archetype_count = $targets.Count
	selected_candidate_count = $selectedCandidates.Count
	written_branch_count = $written
	failed_page_count = $failedPages.Count
	missing_archetypes = @($missingArchetypes.ToArray())
	failed_pages = @($failedPages.ToArray())
	candidate = [ordered]@{
		status_code = $candidateStatusCode
		url = $candidateUrl
		file = $candidateFileName
		as_of = $candidateData.as_of
	}
}
$manifestJson = $manifest | ConvertTo-Json -Depth 8
Set-Content -Path (Join-Path $runDirectory "manifest.json") -Value $manifestJson -Encoding UTF8
Set-Content -Path (Join-Path $latestDirectory "manifest.json") -Value $manifestJson -Encoding UTF8

Write-Host "Wrote $written archetype deck branches to $OutputPath"
if ($missingArchetypes.Count -gt 0) {
	Write-Warning "$($missingArchetypes.Count) archetypes had no eligible branch candidates. See manifest.json."
}
if ($failedPages.Count -gt 0) {
	Write-Warning "$($failedPages.Count) deck pages failed or did not expose a deckstring. See manifest.json."
}
