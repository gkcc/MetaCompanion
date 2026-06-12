param(
	[string]$CookiePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt",
	[string]$Cookie = "",
	[string]$DeckCodePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_deckcodes.txt",
	[string]$OutputDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium",
	[string[]]$DeckIds = @(),
	[int]$MaxDecks = 30,
	[string]$TimeRange = "LAST_7_DAYS",
	[string]$RankRange = "DIAMOND_THROUGH_LEGEND",
	[string]$GameType = "RANKED_STANDARD",
	[string]$Region = "ALL",
	[string]$PlayerInitiative = "ALL",
	[string[]]$Endpoints = @(
		"list_decks_by_win_rate_v2",
		"single_deck_base_winrate_by_opponent_class_v2",
		"single_deck_archetype_matchups_v2"
	),
	[int]$TimeoutSeconds = 30,
	[int]$Retries = 2,
	[int]$RequestDelayMs = 100,
	[int]$ProgressEvery = 5,
	[switch]$StopOnUnsupported,
	[switch]$ProbeOnly
)

$ErrorActionPreference = "Stop"

$supportedTimeRanges = @(
	"LAST_7_DAYS",
	"LAST_30_DAYS",
	"CURRENT_PATCH",
	"CURRENT_EXPANSION",
	"CURRENT_SEASON"
)

if ($supportedTimeRanges -notcontains $TimeRange) {
	throw "HSReplay analytics does not currently support TimeRange=$TimeRange for these queries. Use one of: $($supportedTimeRanges -join ', '). For a 3-day window, cache daily pulls locally and aggregate the last 3 days."
}

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

function Invoke-HSReplayPremiumJson([string]$Endpoint, [hashtable]$Parameters, [object[]]$CookieArgs) {
	$url = "https://hsreplay.net/api/v1/analytics/query/$Endpoint/?$(ConvertTo-QueryString $Parameters)"
	for ($attempt = 1; $attempt -le $Retries; $attempt++) {
		$bodyPath = [System.IO.Path]::GetTempFileName()
		try {
			$statusText = & curl.exe -s -L -A "Mozilla/5.0" -H "Accept: application/json" @CookieArgs `
				--connect-timeout 10 --max-time $TimeoutSeconds -w "%{http_code}" -o $bodyPath $url 2>$null
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
			throw "curl.exe failed while reading $Endpoint"
		}
		if ($statusText -notmatch "^\d{3}$") {
			throw "Unable to read HTTP status for $Endpoint. curl returned: $statusText"
		}

		$statusCode = [int]$statusText
		if ($statusCode -ge 200 -and $statusCode -lt 300) {
			return @{
				Url = $url
				StatusCode = $statusCode
				Body = $body
			}
		}

		if ($statusCode -eq 401 -or $statusCode -eq 403) {
			throw "HSReplay returned HTTP $statusCode for $Endpoint. The cookie is missing, expired, or does not have an active Premium subscription. Body: $body"
		}

		if ($statusCode -eq 400) {
			throw "HSReplay rejected $Endpoint parameters with HTTP 400. Body: $body"
		}

		if ($attempt -lt $Retries) {
			Start-Sleep -Milliseconds (500 * $attempt)
			continue
		}
		throw "HSReplay returned HTTP $statusCode for $Endpoint. Body: $body"
	}
}

function Get-DeckIdsFromSnapshot([string]$Path) {
	if (-not (Test-Path $Path)) {
		throw "Deck code snapshot not found: $Path"
	}

	$ids = New-Object System.Collections.Generic.List[string]
	foreach ($line in Get-Content -Path $Path -Encoding UTF8) {
		if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) {
			continue
		}
		$parts = $line -split "`t"
		if ($parts.Count -ge 3 -and $parts[2] -match "^[A-Za-z0-9]+$") {
			$ids.Add($parts[2])
		}
	}
	return @($ids | Select-Object -Unique)
}

$deckIdsToFetch = @($DeckIds | ForEach-Object { $_ -split "," } |
	ForEach-Object { $_.Trim() } |
	Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
	Select-Object -Unique)
if ($deckIdsToFetch.Count -eq 0) {
	$deckIdsToFetch = Get-DeckIdsFromSnapshot $DeckCodePath
}
if ($MaxDecks -gt 0) {
	$deckIdsToFetch = @($deckIdsToFetch | Select-Object -First $MaxDecks)
}
if ($deckIdsToFetch.Count -eq 0) {
	throw "No HSReplay deck ids found. Re-run Sync-HSReplayDeckCodes.ps1 so the snapshot includes deck ids."
}

$cookieArgs = Get-HSReplayCookieArgs
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDirectory = Join-Path (Join-Path $OutputDirectory "runs") $runId
$latestDirectory = Join-Path $OutputDirectory "latest"
New-Item -ItemType Directory -Force -Path $runDirectory, $latestDirectory | Out-Null

$manifest = [ordered]@{
	generated_at = (Get-Date).ToString("o")
	time_range = $TimeRange
	game_type = $GameType
	rank_range = $RankRange
	region = $Region
	player_initiative = $PlayerInitiative
	deck_count = $deckIdsToFetch.Count
	endpoints = $Endpoints
	items = @()
}

Write-Host "Fetching HSReplay Premium analytics..."
Write-Host "TimeRange=$TimeRange GameType=$GameType LeagueRankRange=$RankRange Region=$Region"
Write-Host "Decks=$($deckIdsToFetch.Count) Endpoints=$($Endpoints -join ', ')"

$totalRequests = $deckIdsToFetch.Count * $Endpoints.Count
$completed = 0
foreach ($deckId in $deckIdsToFetch) {
	foreach ($endpoint in $Endpoints) {
		$parameters = @{
			TimeRange = $TimeRange
			GameType = $GameType
			LeagueRankRange = $RankRange
			Region = $Region
			PlayerInitiative = $PlayerInitiative
			deck_id = $deckId
		}

		try {
			$response = Invoke-HSReplayPremiumJson $endpoint $parameters $cookieArgs
		} catch {
			$message = $_.Exception.Message
			if (-not $StopOnUnsupported -and $message -match "HTTP 400") {
				Write-Warning "Skipping unsupported query for $deckId / $endpoint / $TimeRange"
				$skippedFileName = "$deckId.$endpoint.json"
				$skippedLatestPath = Join-Path $latestDirectory $skippedFileName
				Remove-Item -LiteralPath $skippedLatestPath -ErrorAction SilentlyContinue
				$manifest.items += [ordered]@{
					deck_id = $deckId
					endpoint = $endpoint
					status_code = 400
					file = $skippedFileName
					url = "https://hsreplay.net/api/v1/analytics/query/$endpoint/"
					skipped = $true
					reason = $message
				}
				$completed++
				if ($ProgressEvery -gt 0 -and ($completed % $ProgressEvery -eq 0 -or $completed -eq $totalRequests)) {
					Write-Host "Fetched $completed/$totalRequests premium responses."
				}
				continue
			}
			throw
		}
		$fileName = "$deckId.$endpoint.json"
		$runPath = Join-Path $runDirectory $fileName
		$latestPath = Join-Path $latestDirectory $fileName
		Set-Content -Path $runPath -Value $response.Body -Encoding UTF8
		Set-Content -Path $latestPath -Value $response.Body -Encoding UTF8
		$manifest.items += [ordered]@{
			deck_id = $deckId
			endpoint = $endpoint
			status_code = $response.StatusCode
			file = $fileName
			url = $response.Url
		}

		$completed++
		if ($RequestDelayMs -gt 0) {
			Start-Sleep -Milliseconds $RequestDelayMs
		}
		if ($ProgressEvery -gt 0 -and ($completed % $ProgressEvery -eq 0 -or $completed -eq $totalRequests)) {
			Write-Host "Fetched $completed/$totalRequests premium responses."
		}
		if ($ProbeOnly) {
			break
		}
	}
	if ($ProbeOnly) {
		break
	}
}

$manifestJson = $manifest | ConvertTo-Json -Depth 6
Set-Content -Path (Join-Path $runDirectory "manifest.json") -Value $manifestJson -Encoding UTF8
Set-Content -Path (Join-Path $latestDirectory "manifest.json") -Value $manifestJson -Encoding UTF8
Write-Host "Wrote premium analytics cache to $runDirectory"
