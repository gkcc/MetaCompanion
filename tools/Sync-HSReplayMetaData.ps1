param(
	[string]$CookiePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt",
	[string]$Cookie = "",
	[string]$OutputDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Meta",
	[string]$TimeRange = "CURRENT_PATCH",
	[string]$PatchVersion = "",
	[string]$RankRange = "DIAMOND_THROUGH_LEGEND",
	[string]$GameType = "RANKED_STANDARD",
	[string]$Region = "ALL",
	[string]$Locale = "zh-hans",
	[int]$TopOverall = 20,
	[int]$TopPerClass = 5,
	[int]$TimeoutSeconds = 30,
	[int]$Retries = 2
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
	throw "curl.exe was not found."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

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

function Normalize-HearthstonePatchVersion([string]$Value) {
	if ([string]::IsNullOrWhiteSpace($Value)) {
		return ""
	}
	$match = [regex]::Match($Value, "\b(\d+\.\d+\.\d+)(?:\.\d+)?\b")
	if ($match.Success) {
		return $match.Groups[1].Value
	}
	return ""
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

function Resolve-HearthstonePatchVersion([string]$PreferredVersion) {
	$normalized = Normalize-HearthstonePatchVersion $PreferredVersion
	if (-not [string]::IsNullOrWhiteSpace($normalized)) {
		return $normalized
	}

	$patchVersionPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\patch_version.txt"
	if (Test-Path -LiteralPath $patchVersionPath) {
		$normalized = Normalize-HearthstonePatchVersion (
			Get-Content -LiteralPath $patchVersionPath -Raw -Encoding UTF8)
		if (-not [string]::IsNullOrWhiteSpace($normalized)) {
			return $normalized
		}
	}

	$exePath = Resolve-HearthstoneExePath
	if (-not [string]::IsNullOrWhiteSpace($exePath)) {
		$productDbPath = Join-Path (Split-Path -Parent $exePath) ".product.db"
		if (Test-Path -LiteralPath $productDbPath) {
			$text = [System.Text.Encoding]::ASCII.GetString(
				[System.IO.File]::ReadAllBytes($productDbPath))
			$normalized = Normalize-HearthstonePatchVersion $text
			if (-not [string]::IsNullOrWhiteSpace($normalized)) {
				return $normalized
			}
		}
	}

	return ""
}

function Invoke-HSReplayJson([string]$Url, [string]$Name, [object[]]$CookieArgs) {
	for ($attempt = 1; $attempt -le $Retries; $attempt++) {
		$bodyPath = [System.IO.Path]::GetTempFileName()
		try {
			$statusText = & curl.exe -s -L -A "Mozilla/5.0" -H "Accept: application/json" @CookieArgs `
				--connect-timeout 10 --max-time $TimeoutSeconds -w "%{http_code}" -o $bodyPath $Url 2>$null
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

function Get-AnalyticsUrl([string]$Endpoint, [hashtable]$Parameters) {
	return "https://hsreplay.net/api/v1/analytics/query/$Endpoint/?$(ConvertTo-QueryString $Parameters)"
}

function Add-ArchetypesToMap([string]$Json, [hashtable]$Map) {
	$archetypes = $Json | ConvertFrom-Json
	$archetypeItems = if ($archetypes -is [System.Array]) {
		@($archetypes)
	} elseif ($archetypes.PSObject.Properties.Name -contains "results") {
		@($archetypes.results)
	} else {
		@($archetypes)
	}

	foreach ($item in $archetypeItems) {
		if ($null -eq $item.id) {
			continue
		}
		$Map[[string]([int]$item.id)] = $item
	}
}

function Get-ArchetypeInfo([int]$Id, [string]$FallbackClass, [hashtable]$Map) {
	$key = [string]$Id
	if ($Map.ContainsKey($key)) {
		$item = $Map[$key]
		$name = [string]$item.name
		if ([string]::IsNullOrWhiteSpace($name)) {
			$name = "Archetype $Id"
		}
		$className = [string]$item.player_class_name
		if ([string]::IsNullOrWhiteSpace($className)) {
			$className = $FallbackClass
		}
		return [pscustomobject]@{
			name = $name
			player_class = $className
			url = [string]$item.url
		}
	}

	return [pscustomobject]@{
		name = "Archetype $Id"
		player_class = $FallbackClass
		url = ""
	}
}

function New-RankedRows([object[]]$Rows, [int]$Limit) {
	$result = New-Object System.Collections.ArrayList
	$rank = 1
	$items = if ($Limit -le 0) { @($Rows) } else { @($Rows | Select-Object -First $Limit) }
	foreach ($row in $items) {
		[void]$result.Add([pscustomobject][ordered]@{
			rank = $rank
			archetype_id = $row.archetype_id
			name = $row.name
			player_class = $row.player_class
			total_games = $row.total_games
			pct_of_total = $row.pct_of_total
			pct_of_class = $row.pct_of_class
			win_rate = $row.win_rate
			url = $row.url
		})
		$rank++
	}
	return @($result)
}

function Format-TsvValue($Value) {
	if ($null -eq $Value) {
		return ""
	}
	return ([string]$Value) -replace "[`t`r`n]", " "
}

function Write-MetaSummaryFiles([object]$PopularityDistribution, [string]$ArchetypesJson, [string]$SummaryJsonPath, [string]$SummaryTsvPath) {
	$archetypeMap = @{}
	Add-ArchetypesToMap $ArchetypesJson $archetypeMap

	$allRows = New-Object System.Collections.Generic.List[object]
	foreach ($classProperty in $PopularityDistribution.series[0].data.PSObject.Properties) {
		$className = [string]$classProperty.Name
		foreach ($item in @($classProperty.Value)) {
			if ($null -eq $item.archetype_id) {
				continue
			}

			$archetypeId = [int]$item.archetype_id
			if ($archetypeId -lt 0) {
				continue
			}

			$info = Get-ArchetypeInfo $archetypeId $className $archetypeMap
			$allRows.Add([pscustomobject][ordered]@{
				archetype_id = $archetypeId
				name = $info.name
				player_class = $info.player_class
				total_games = [int]$item.total_games
				pct_of_total = [double]$item.pct_of_total
				pct_of_class = [double]$item.pct_of_class
				win_rate = [double]$item.win_rate
				url = $info.url
			})
		}
	}

	$overallRows = @($allRows |
		Sort-Object @{ Expression = { $_.pct_of_total }; Descending = $true }, @{ Expression = { $_.total_games }; Descending = $true })
	$allRankedRows = New-RankedRows $overallRows 0
	$topOverallRows = New-RankedRows $overallRows $TopOverall

	$topByClass = [ordered]@{}
	foreach ($group in ($allRows | Group-Object player_class | Sort-Object Name)) {
		$classRows = @($group.Group |
			Sort-Object @{ Expression = { $_.pct_of_class }; Descending = $true }, @{ Expression = { $_.total_games }; Descending = $true })
		$topByClass[$group.Name] = @(New-RankedRows $classRows $TopPerClass)
	}

	$summary = [ordered]@{
		generated_at = (Get-Date).ToString("o")
		as_of = $PopularityDistribution.as_of
		time_range = $TimeRange
		patch_version = $effectivePatchVersion
		patch_label = if ([string]::IsNullOrWhiteSpace($effectivePatchVersion)) { "" } else { "$effectivePatchVersion patch" }
		game_type = $GameType
		rank_range = $RankRange
		region = $Region
		locale = $Locale
		source = "HSReplay meta overview analytics"
		all = @($allRankedRows)
		top_overall = @($topOverallRows)
		top_by_class = $topByClass
	}

	$summary | ConvertTo-Json -Depth 10 | Set-Content -Path $SummaryJsonPath -Encoding UTF8

	$tsvLines = New-Object System.Collections.Generic.List[string]
	$tsvLines.Add("scope`trank`tplayer_class`tarchetype_id`tname`ttotal_games`tpct_of_total`tpct_of_class`twin_rate`turl")
	foreach ($row in $topOverallRows) {
		$values = @("overall", $row.rank, $row.player_class, $row.archetype_id, $row.name, $row.total_games, $row.pct_of_total, $row.pct_of_class, $row.win_rate, $row.url)
		$tsvLines.Add(($values | ForEach-Object { Format-TsvValue $_ }) -join "`t")
	}
	foreach ($className in $topByClass.Keys) {
		foreach ($row in $topByClass[$className]) {
			$values = @("class:$className", $row.rank, $row.player_class, $row.archetype_id, $row.name, $row.total_games, $row.pct_of_total, $row.pct_of_class, $row.win_rate, $row.url)
			$tsvLines.Add(($values | ForEach-Object { Format-TsvValue $_ }) -join "`t")
		}
	}
	Set-Content -Path $SummaryTsvPath -Value $tsvLines -Encoding UTF8
}

function Get-MetaSummarySampleGames([string]$SummaryJsonPath) {
	if (-not (Test-Path -LiteralPath $SummaryJsonPath)) {
		throw "Meta summary not found: $SummaryJsonPath"
	}

	$summary = Get-Content -LiteralPath $SummaryJsonPath -Encoding UTF8 -Raw | ConvertFrom-Json
	$total = [int64]0
	foreach ($row in @($summary.all)) {
		if ($null -ne $row.total_games) {
			$total += [int64]$row.total_games
		}
	}
	return $total
}

function Copy-MetaCacheFiles([string]$SourceDirectory, [string]$DestinationDirectory) {
	if (-not (Test-Path -LiteralPath $SourceDirectory)) {
		throw "Meta cache source directory not found: $SourceDirectory"
	}

	New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
	Get-ChildItem -LiteralPath $SourceDirectory -File -Force |
		ForEach-Object {
			Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $DestinationDirectory $_.Name) -Force
		}
}

function Set-AutoTimeRangeManifest(
	[string]$ManifestPath,
	[object]$Selected,
	[object[]]$Candidates
) {
	if (-not (Test-Path -LiteralPath $ManifestPath)) {
		return
	}

	$manifest = Get-Content -LiteralPath $ManifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
	$candidateRows = @()
	foreach ($candidate in $Candidates) {
		$candidateRows += [ordered]@{
			time_range = $candidate.time_range
			sample_games = $candidate.sample_games
			summary_as_of = $candidate.summary_as_of
			run_directory = $candidate.run_directory
		}
	}

	$manifestMap = [ordered]@{}
	foreach ($property in $manifest.PSObject.Properties) {
		$manifestMap[$property.Name] = $property.Value
	}
	$manifestMap["auto_time_range_policy"] =
		"choose_smaller_sample_between_CURRENT_PATCH_and_LAST_3_DAYS"
	$manifestMap["selected_time_range"] = $Selected.time_range
	if ($manifestMap.Contains("candidate_sample_games")) {
		$manifestMap.Remove("candidate_sample_games")
	}
	$manifestMap.Add("candidate_sample_games", $candidateRows)
	$manifestMap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
}

function Invoke-AutoCurrentPatchOrLast3DaysMetaSync {
	$candidateTimeRanges = @("CURRENT_PATCH", "LAST_3_DAYS")
	$runId = Get-Date -Format "yyyyMMdd-HHmmss"
	$candidateRoot = Join-Path ([System.IO.Path]::GetTempPath()) "MetaCompanionMetaCandidates-$runId"
	$results = New-Object System.Collections.Generic.List[object]

	Write-Host "Auto-selecting HSReplay meta TimeRange from: $($candidateTimeRanges -join ', ')"
	foreach ($candidateTimeRange in $candidateTimeRanges) {
		$candidateOutput = Join-Path $candidateRoot $candidateTimeRange
		$candidateArgs = @{
			CookiePath = $CookiePath
			OutputDirectory = $candidateOutput
			TimeRange = $candidateTimeRange
			RankRange = $RankRange
			GameType = $GameType
			Region = $Region
			Locale = $Locale
			TopOverall = $TopOverall
			TopPerClass = $TopPerClass
			TimeoutSeconds = $TimeoutSeconds
			Retries = $Retries
			PatchVersion = $PatchVersion
		}
		if (-not [string]::IsNullOrWhiteSpace($Cookie)) {
			$candidateArgs.Cookie = $Cookie
		}

		try {
			& $PSCommandPath @candidateArgs
			$candidateLatest = Join-Path $candidateOutput "latest"
			$candidateSummary = Join-Path $candidateLatest "summary.json"
			$summary = Get-Content -LiteralPath $candidateSummary -Encoding UTF8 -Raw | ConvertFrom-Json
			$sampleGames = Get-MetaSummarySampleGames $candidateSummary
			$candidateRun = Get-ChildItem -LiteralPath (Join-Path $candidateOutput "runs") -Directory |
				Sort-Object LastWriteTime -Descending |
				Select-Object -First 1
			if ($null -eq $candidateRun) {
				throw "No run directory was created for TimeRange=$candidateTimeRange"
			}

			$results.Add([pscustomobject]@{
				time_range = $candidateTimeRange
				sample_games = $sampleGames
				summary_as_of = $summary.as_of
				latest_directory = $candidateLatest
				run_directory = $candidateRun.FullName
			}) | Out-Null
			Write-Host "Candidate TimeRange=$candidateTimeRange SampleGames=$sampleGames AsOf=$($summary.as_of)"
		} catch {
			Write-Warning "Candidate TimeRange=$candidateTimeRange failed: $($_.Exception.Message)"
		}
	}

	if ($results.Count -eq 0) {
		throw "No HSReplay meta TimeRange candidate succeeded."
	}

	$selected = $null
	foreach ($result in $results) {
		if ($null -eq $selected -or
			([int64]$result.sample_games) -lt ([int64]$selected.sample_games) -or
			(([int64]$result.sample_games) -eq ([int64]$selected.sample_games) -and
				[string]$result.time_range -lt [string]$selected.time_range)) {
			$selected = $result
		}
	}
	$realLatestDirectory = Join-Path $OutputDirectory "latest"
	$realRunDirectory = Join-Path (Join-Path $OutputDirectory "runs") "$runId-$($selected.time_range)"
	$candidateArray = $results.ToArray()

	Copy-MetaCacheFiles $selected.latest_directory $realLatestDirectory
	Copy-MetaCacheFiles $selected.run_directory $realRunDirectory
	Set-AutoTimeRangeManifest `
		-ManifestPath (Join-Path $realLatestDirectory "manifest.json") `
		-Selected $selected `
		-Candidates $candidateArray
	Set-AutoTimeRangeManifest `
		-ManifestPath (Join-Path $realRunDirectory "manifest.json") `
		-Selected $selected `
		-Candidates $candidateArray

	Write-Host "Selected HSReplay meta TimeRange=$($selected.time_range) with SampleGames=$($selected.sample_games)."
	Write-Host "Wrote HSReplay meta cache to $realRunDirectory"
	Write-Host "Summary: $(Join-Path $realLatestDirectory 'summary.tsv')"
}

if ($TimeRange -in @("AUTO_CURRENT_PATCH_OR_LAST_3_DAYS", "AUTO")) {
	Invoke-AutoCurrentPatchOrLast3DaysMetaSync
	return
}

$effectivePatchVersion = Resolve-HearthstonePatchVersion $PatchVersion
if (-not [string]::IsNullOrWhiteSpace($effectivePatchVersion)) {
	Write-Host "Detected Hearthstone patch version: $effectivePatchVersion"
}

$cookieArgs = Get-HSReplayCookieArgs
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDirectory = Join-Path (Join-Path $OutputDirectory "runs") $runId
$latestDirectory = Join-Path $OutputDirectory "latest"
New-Item -ItemType Directory -Force -Path $runDirectory, $latestDirectory | Out-Null

$metaParameters = @{
	TimeRange = $TimeRange
	GameType = $GameType
	LeagueRankRange = $RankRange
	Region = $Region
}
$rankParameters = @{
	TimeRange = $TimeRange
	GameType = $GameType
	Region = $Region
}
$localeQuery = if ([string]::IsNullOrWhiteSpace($Locale)) { "" } else { "?hl=$([System.Uri]::EscapeDataString($Locale))" }

$requests = @(
	[ordered]@{
		name = "archetype_popularity_distribution_stats_v2"
		file = "archetype_popularity_distribution_stats_v2.json"
		url = Get-AnalyticsUrl "archetype_popularity_distribution_stats_v2" $metaParameters
	},
	[ordered]@{
		name = "head_to_head_archetype_matchups_v2"
		file = "head_to_head_archetype_matchups_v2.json"
		url = Get-AnalyticsUrl "head_to_head_archetype_matchups_v2" $metaParameters
	},
	[ordered]@{
		name = "archetype_popularity_by_rank"
		file = "archetype_popularity_by_rank.json"
		url = Get-AnalyticsUrl "archetype_popularity_by_rank" $rankParameters
	},
	[ordered]@{
		name = "archetypes"
		file = "archetypes.$($Locale -replace '[^A-Za-z0-9_-]', '_').json"
		url = "https://hsreplay.net/api/v1/archetypes/$localeQuery"
	}
)

$manifest = [ordered]@{
	generated_at = (Get-Date).ToString("o")
	time_range = $TimeRange
	selected_time_range = $TimeRange
	patch_version = $effectivePatchVersion
	patch_label = if ([string]::IsNullOrWhiteSpace($effectivePatchVersion)) { "" } else { "$effectivePatchVersion patch" }
	game_type = $GameType
	rank_range = $RankRange
	region = $Region
	locale = $Locale
	items = @()
}

Write-Host "Fetching HSReplay meta analytics..."
Write-Host "TimeRange=$TimeRange GameType=$GameType LeagueRankRange=$RankRange Region=$Region Locale=$Locale"

$responses = @{}
foreach ($request in $requests) {
	Write-Host "Fetching $($request.name)..."
	$response = Invoke-HSReplayJson $request.url $request.name $cookieArgs
	$runPath = Join-Path $runDirectory $request.file
	$latestPath = Join-Path $latestDirectory $request.file
	Set-Content -Path $runPath -Value $response.Body -Encoding UTF8
	Set-Content -Path $latestPath -Value $response.Body -Encoding UTF8
	$responses[$request.name] = $response.Body
	$manifest.items += [ordered]@{
		name = $request.name
		status_code = $response.StatusCode
		file = $request.file
		url = $response.Url
	}
}

$popularityDistribution = $responses["archetype_popularity_distribution_stats_v2"] | ConvertFrom-Json
$runSummaryJsonPath = Join-Path $runDirectory "summary.json"
$runSummaryTsvPath = Join-Path $runDirectory "summary.tsv"
$latestSummaryJsonPath = Join-Path $latestDirectory "summary.json"
$latestSummaryTsvPath = Join-Path $latestDirectory "summary.tsv"

Write-MetaSummaryFiles $popularityDistribution $responses["archetypes"] $runSummaryJsonPath $runSummaryTsvPath
Copy-Item -LiteralPath $runSummaryJsonPath -Destination $latestSummaryJsonPath -Force
Copy-Item -LiteralPath $runSummaryTsvPath -Destination $latestSummaryTsvPath -Force

$manifestJson = $manifest | ConvertTo-Json -Depth 6
Set-Content -Path (Join-Path $runDirectory "manifest.json") -Value $manifestJson -Encoding UTF8
Set-Content -Path (Join-Path $latestDirectory "manifest.json") -Value $manifestJson -Encoding UTF8

Write-Host "Wrote HSReplay meta cache to $runDirectory"
Write-Host "Summary: $latestSummaryTsvPath"
