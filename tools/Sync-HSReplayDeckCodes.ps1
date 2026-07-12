param(
	[string[]]$RankRanges = @(
		"DIAMOND_THROUGH_LEGEND",
		"DIAMOND_FOUR_THROUGH_DIAMOND_ONE",
		"PLATINUM",
		"GOLD",
		"BRONZE_THROUGH_GOLD"
	),
	[string]$GameType = "RANKED_STANDARD",
	[int]$LimitPerRange = 250,
	[int]$MaxDecks = 500,
	[int]$Retries = 2,
	[int]$InventoryTimeoutSeconds = 30,
	[int]$DeckPageTimeoutSeconds = 12,
	[int]$ArchetypeTimeoutSeconds = 20,
	[int]$RequestDelayMs = 100,
	[int]$ProgressEvery = 10,
	[int]$Parallelism = 1,
	[string]$Locale = "zh-hans",
	[string]$CookiePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt",
	[string]$Cookie = "",
	[string]$DeckPageUserAgent = "",
	[string]$OutputPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_deckcodes.txt"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
	throw "curl.exe was not found. It is required because HSReplay blocks .NET WebClient requests."
}

function Get-OptionalHSReplayCookieArgs {
	if (-not [string]::IsNullOrWhiteSpace($Cookie)) {
		return @("-H", "Cookie: $Cookie")
	}

	if (-not (Test-Path -LiteralPath $CookiePath)) {
		return @()
	}

	$cookieText = Get-Content -LiteralPath $CookiePath -Raw
	if ([string]::IsNullOrWhiteSpace($cookieText)) {
		return @()
	}

	$firstLine = ($cookieText -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
	if ($firstLine -like "# Netscape*" -or $cookieText -match "hsreplay\.net`t") {
		return @("-b", $CookiePath)
	}

	$cookieHeader = $cookieText.Trim()
	if ($cookieHeader.StartsWith("Cookie:", [StringComparison]::OrdinalIgnoreCase)) {
		$cookieHeader = $cookieHeader.Substring("Cookie:".Length).Trim()
	}
	if ([string]::IsNullOrWhiteSpace($cookieHeader)) {
		return @()
	}
	return @("-H", "Cookie: $cookieHeader")
}

function Get-DefaultBrowserUserAgent([string]$PreferredUserAgent) {
	if (-not [string]::IsNullOrWhiteSpace($PreferredUserAgent)) {
		return $PreferredUserAgent
	}

	$chromePaths = @(
		"$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
		"${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
		"$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
	)
	foreach ($path in $chromePaths) {
		if (-not (Test-Path -LiteralPath $path)) {
			continue
		}
		$version = (Get-Item -LiteralPath $path).VersionInfo.ProductVersion
		if (-not [string]::IsNullOrWhiteSpace($version)) {
			return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/$version Safari/537.36"
		}
	}

	return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
}

function Format-DiagnosticText([string]$Text, [int]$MaxLength = 240) {
	if ([string]::IsNullOrWhiteSpace($Text)) {
		return ""
	}

	$decoded = [System.Net.WebUtility]::HtmlDecode($Text)
	if ($decoded -match "(?is)<title>\s*(?<title>.*?)\s*</title>") {
		$title = (($Matches["title"] -replace "\s+", " ").Trim())
		if ($title -match "(?i)^Just a moment") {
			return "Cloudflare challenge page (title: $title)"
		}
		return "HTML response (title: $title)"
	}
	if ($decoded -match "(?i)cloudflare|challenges\.cloudflare\.com") {
		return "Cloudflare challenge page"
	}

	$singleLine = ($decoded.Trim() -replace "\s+", " ")
	if ($singleLine.StartsWith("<")) {
		return "HTML response without a title"
	}
	if ($singleLine.Length -le $MaxLength) {
		return $singleLine
	}
	return $singleLine.Substring(0, $MaxLength) + "..."
}

function Format-CurlErrorText([string]$Text) {
	$diagnostic = Format-DiagnosticText $Text
	if ($diagnostic -match "curl(?:\.exe)?\s*:\s*(curl:\s*\(\d+\).*?)(?:\s+At\s+[A-Za-z]:\\|$)") {
		return $matches[1].Trim()
	}
	return $diagnostic
}

$RankRanges = @($RankRanges | ForEach-Object { $_ -split "," } |
	ForEach-Object { $_.Trim() } |
	Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
[object[]]$deckPageCookieArgs = @(Get-OptionalHSReplayCookieArgs)
if ($deckPageCookieArgs.Count -gt 0) {
	Write-Host "Using HSReplay cookie for deck pages."
}
$effectiveDeckPageUserAgent = Get-DefaultBrowserUserAgent $DeckPageUserAgent

function Invoke-CurlText(
	[string]$Url,
	[string]$UserAgent,
	[string]$Accept,
	[int]$TimeoutSeconds,
	[string]$AcceptLanguage = $null,
	[object[]]$ExtraCurlArgs = @()
) {
	$lastFailure = ""
	for ($attempt = 1; $attempt -le $Retries; $attempt++) {
		$headers = @("-H", "Accept: $Accept")
		if (-not [string]::IsNullOrWhiteSpace($AcceptLanguage)) {
			$headers += @("-H", "Accept-Language: $AcceptLanguage")
		}

		$tempPath = [System.IO.Path]::GetTempFileName()
		$errorPath = [System.IO.Path]::GetTempFileName()
		try {
			$previousErrorActionPreference = $ErrorActionPreference
			try {
				$ErrorActionPreference = "Continue"
				$statusText = & curl.exe -sS -L -A $UserAgent @headers @ExtraCurlArgs `
					--connect-timeout 10 --max-time $TimeoutSeconds `
					-w "%{http_code}" -o $tempPath $Url 2>$errorPath
				$exitCode = $LASTEXITCODE
			} finally {
				$ErrorActionPreference = $previousErrorActionPreference
			}
			$statusText = (@($statusText) -join "").Trim()
			$errorText = if (Test-Path $errorPath) {
				Get-Content -LiteralPath $errorPath -Encoding UTF8 -Raw
			} else {
				""
			}
			$bodyLength = if (Test-Path $tempPath) { (Get-Item $tempPath).Length } else { 0 }
			$bodyText = ""
			if ($bodyLength -gt 0) {
				$text = [System.Text.Encoding]::UTF8.GetString(
					[System.IO.File]::ReadAllBytes($tempPath))
				if (-not [string]::IsNullOrWhiteSpace($text)) {
					$bodyText = $text
				}
			}

			$statusCode = 0
			$hasStatusCode = [int]::TryParse($statusText, [ref]$statusCode)
			if ($exitCode -eq 0 -and $hasStatusCode -and $statusCode -ge 200 -and
				$statusCode -lt 300 -and -not [string]::IsNullOrWhiteSpace($bodyText)) {
				return $bodyText
			}

			$details = New-Object System.Collections.Generic.List[string]
			$details.Add("curl exit code $exitCode")
			if ($hasStatusCode) {
				$details.Add("HTTP $statusCode")
			} elseif (-not [string]::IsNullOrWhiteSpace($statusText)) {
				$details.Add("HTTP status output '$statusText'")
			}
			if ($bodyLength -eq 0 -or [string]::IsNullOrWhiteSpace($bodyText)) {
				$details.Add("empty response body")
			} elseif ($hasStatusCode -and ($statusCode -lt 200 -or $statusCode -ge 300)) {
				$details.Add("body: $(Format-DiagnosticText $bodyText)")
			}
			if (-not [string]::IsNullOrWhiteSpace($errorText)) {
				$details.Add("stderr: $(Format-CurlErrorText $errorText)")
			}
			$lastFailure = $details -join "; "
		} finally {
			Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
			Remove-Item -LiteralPath $errorPath -Force -ErrorAction SilentlyContinue
		}
		if ($attempt -lt $Retries) {
			Start-Sleep -Milliseconds (500 * $attempt)
		}
	}
	if ([string]::IsNullOrWhiteSpace($lastFailure)) {
		throw "curl.exe failed while reading $Url"
	}
	throw "curl.exe failed while reading $Url ($lastFailure)"
}

$deckIds = New-Object System.Collections.Generic.List[string]
$seenDeckIds = @{}
$sourceUrls = New-Object System.Collections.Generic.List[string]
$asOfValues = New-Object System.Collections.Generic.List[string]
$archetypeNames = @{}
$localeQuery = if ([string]::IsNullOrWhiteSpace($Locale)) { "" } else { "?hl=$Locale" }

function Add-ArchetypeNamesFromJson([string]$Json, [hashtable]$Target) {
	$archetypes = $Json | ConvertFrom-Json
	$archetypeItems = if ($archetypes -is [System.Array]) {
		@($archetypes)
	} elseif ($archetypes.PSObject.Properties.Name -contains "results") {
		@($archetypes.results)
	} else {
		@($archetypes)
	}

	$added = 0
	foreach ($archetype in $archetypeItems) {
		if ($archetype.id -and -not [string]::IsNullOrWhiteSpace([string]$archetype.name)) {
			$Target[[string]$archetype.id] = [string]$archetype.name
			$added++
		}
	}
	return $added
}

if (-not [string]::IsNullOrWhiteSpace($Locale)) {
	$archetypesUrl = "https://hsreplay.net/api/v1/archetypes/$localeQuery"
	$archetypeCachePath = Join-Path $outputDirectory (
		"hsreplay_archetypes.$($Locale -replace '[^A-Za-z0-9_-]', '_').json")
	$sourceUrls.Add($archetypesUrl)
	Write-Host "Reading localized archetype names from HSReplay for $Locale..."
	try {
		$archetypesJson = Invoke-CurlText $archetypesUrl "Hearthstone Deck Tracker" "application/json" `
			$ArchetypeTimeoutSeconds $Locale
		$loadedCount = Add-ArchetypeNamesFromJson $archetypesJson $archetypeNames
		if ($loadedCount -gt 0) {
			Set-Content -Path $archetypeCachePath -Value $archetypesJson -Encoding UTF8
		}
		Write-Host "Loaded $loadedCount localized archetype names."
	} catch {
		if (Test-Path $archetypeCachePath) {
			$cachedArchetypesJson = Get-Content -Path $archetypeCachePath -Encoding UTF8 -Raw
			$loadedCount = Add-ArchetypeNamesFromJson $cachedArchetypesJson $archetypeNames
			Write-Warning "Failed to fetch localized archetype names: $($_.Exception.Message); using cached $Locale names ($loadedCount entries)."
		} else {
			Write-Warning "Failed to fetch localized archetype names: $($_.Exception.Message); deck page titles will be used instead."
		}
	}
}

foreach ($rankRange in $RankRanges) {
	$inventoryUrl = "https://hsreplay.net/api/v1/analytics/query/list_deck_inventory_v2/?GameType=$GameType&RankRange=$rankRange"
	$sourceUrls.Add($inventoryUrl)
	Write-Host "Reading deck inventory from HSReplay for $rankRange..."
	$inventoryJson = Invoke-CurlText $inventoryUrl "Hearthstone Deck Tracker" "application/json" $InventoryTimeoutSeconds
	$inventory = $inventoryJson | ConvertFrom-Json
	if (-not $inventory.series) {
		throw "HSReplay inventory response did not contain a series object: $inventoryJson"
	}

	if ($inventory.as_of) {
		$asOfValues.Add([string]$inventory.as_of)
	}

	$rangeCount = 0
	foreach ($property in $inventory.series.PSObject.Properties) {
		$modes = $property.Value.PSObject.Properties[$GameType]
		if (-not $modes -or -not ($modes.Value -contains $rankRange)) {
			continue
		}

		$rangeCount++
		if (-not $seenDeckIds.ContainsKey($property.Name)) {
			$seenDeckIds[$property.Name] = $true
			$deckIds.Add($property.Name)
		}

		if ($rangeCount -ge $LimitPerRange) {
			break
		}
		if ($MaxDecks -gt 0 -and $deckIds.Count -ge $MaxDecks) {
			break
		}
	}

	Write-Host "Added $rangeCount ids from $rankRange; $($deckIds.Count) unique ids queued."
	if ($MaxDecks -gt 0 -and $deckIds.Count -ge $MaxDecks) {
		break
	}
}

if ($deckIds.Count -eq 0) {
	throw "No deck ids found for $GameType / $($RankRanges -join ', ')."
}

function Convert-DeckPageToEntry([string]$DeckId, [string]$Html) {
	$decoded = [System.Net.WebUtility]::HtmlDecode([string]$Html)
	$match = [regex]::Match($decoded,
		'<meta[^>]+property=["'']x-hearthstone:deck:deckstring["''][^>]+content=["''](?<deck>AAE[A-Za-z0-9+/=]{20,})["'']',
		[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	if (-not $match.Success) {
		$match = [regex]::Match($decoded,
			'<meta[^>]+content=["''](?<deck>AAE[A-Za-z0-9+/=]{20,})["''][^>]+property=["'']x-hearthstone:deck:deckstring["'']',
			[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	}
	if (-not $match.Success) {
		$match = [regex]::Match($decoded, "Import it:\s*(?<deck>AAE[A-Za-z0-9+/=]{20,})")
	}
	if (-not $match.Success) {
		$match = [regex]::Match($decoded,
			'["'']deckstring["'']\s*:\s*["''](?<deck>AAE[A-Za-z0-9+/=]{20,})["'']',
			[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	}
	if (-not $match.Success) {
		if ($decoded -match "(?i)challenges\.cloudflare\.com|<title>\s*Just a moment") {
			throw "HSReplay returned a Cloudflare challenge page for deck $DeckId. Refresh the HSReplay cookie from a logged-in browser session, then retry."
		}
		return $null
	}

	$titleMatch = [regex]::Match($decoded, "<title>\s*(.*?)\s*-\s*HSReplay\.net\s*</title>",
		[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	$title = if ($titleMatch.Success) { $titleMatch.Groups[1].Value.Trim() } else { "HSReplay deck" }
	$archetypeIdMatch = [regex]::Match($decoded, 'data-archetype-id\s*=\s*"(\d+)"',
		[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	$archetypeId = if ($archetypeIdMatch.Success) { $archetypeIdMatch.Groups[1].Value } else { $null }
	$deckName = if ($archetypeId -and $archetypeNames.ContainsKey($archetypeId)) {
		$archetypeNames[$archetypeId]
	} else {
		$title
	}
	$deckCode = $match.Groups["deck"].Value
	return "$deckName`t$deckCode`t$DeckId`t$archetypeId"
}

function Test-DeckSnapshotHasValidCode([string]$Path) {
	if (-not (Test-Path -LiteralPath $Path)) {
		return $false
	}
	foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
		if ($line -cmatch "AAE[A-Za-z0-9+/=]{20,}") {
			return $true
		}
	}
	return $false
}

Write-Host "Found $($deckIds.Count) deck ids. Fetching deck codes..."
$deckCodes = New-Object System.Collections.Generic.List[string]
$fetched = 0
$failed = 0
$skipped = 0

if ($Parallelism -le 1) {
	for ($index = 0; $index -lt $deckIds.Count; $index++) {
		$deckId = $deckIds[$index]
		$deckUrl = "https://hsreplay.net/decks/$deckId/"
		try {
			$html = Invoke-CurlText `
				-Url $deckUrl `
				-UserAgent $effectiveDeckPageUserAgent `
				-Accept "text/html,*/*" `
				-TimeoutSeconds $DeckPageTimeoutSeconds `
				-ExtraCurlArgs $deckPageCookieArgs
			$entry = Convert-DeckPageToEntry $deckId $html
			if ($entry) {
				$deckCodes.Add($entry)
				$fetched++
			} else {
				$skipped++
			}
		} catch {
			$failed++
			Write-Warning "Failed to fetch $deckUrl`: $($_.Exception.Message)"
		}

		if ($RequestDelayMs -gt 0) {
			Start-Sleep -Milliseconds $RequestDelayMs
		}
		if ($ProgressEvery -gt 0 -and (($index + 1) % $ProgressEvery -eq 0 -or ($index + 1) -eq $deckIds.Count)) {
			Write-Host "Checked $($index + 1)/$($deckIds.Count), fetched $fetched deck codes, failed $failed, skipped $skipped."
		}
	}
} else {
	$queue = New-Object System.Collections.Generic.Queue[string]
	foreach ($deckId in $deckIds) {
		$queue.Enqueue($deckId)
	}
	$jobs = @{}
	$checked = 0
	$jobScript = {
		param([string]$DeckId, [int]$Retries, [int]$TimeoutSeconds, [string]$UserAgent, [object[]]$CookieArgs)
		function Invoke-CurlTextLocal([string]$Url, [int]$Retries, [int]$TimeoutSeconds, [string]$UserAgent, [object[]]$CookieArgs) {
			for ($attempt = 1; $attempt -le $Retries; $attempt++) {
				$text = & curl.exe -s -L -A $UserAgent -H "Accept: text/html,*/*" @CookieArgs `
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
				Html = (Invoke-CurlTextLocal `
					-Url $deckUrl `
					-Retries $Retries `
					-TimeoutSeconds $TimeoutSeconds `
					-UserAgent $UserAgent `
					-CookieArgs $CookieArgs)
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
			$job = Start-Job -ScriptBlock $jobScript -ArgumentList $deckId, $Retries, $DeckPageTimeoutSeconds, $effectiveDeckPageUserAgent, (,$deckPageCookieArgs)
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
			$failed++
			Write-Warning "Failed to fetch $($result.Url): $($result.Error)"
		} else {
			try {
				$entry = Convert-DeckPageToEntry $result.DeckId $result.Html
				if ($entry) {
					$deckCodes.Add($entry)
					$fetched++
				} else {
					$skipped++
				}
			} catch {
				$failed++
				Write-Warning "Failed to parse $($result.Url): $($_.Exception.Message)"
			}
		}

		if ($ProgressEvery -gt 0 -and ($checked % $ProgressEvery -eq 0 -or $checked -eq $deckIds.Count)) {
			Write-Host "Checked $checked/$($deckIds.Count), fetched $fetched deck codes, failed $failed, skipped $skipped."
		}
	}
}

$uniqueDeckCodes = $deckCodes |
	Group-Object {
		if ($_ -cmatch "AAE[A-Za-z0-9+/=]{20,}") {
			$matches[0]
		} else {
			$_
		}
	} |
	ForEach-Object { $_.Group[0] }
if ($uniqueDeckCodes.Count -eq 0) {
	if ((Test-Path -LiteralPath $OutputPath) -and -not (Test-DeckSnapshotHasValidCode $OutputPath)) {
		$invalidPath = $OutputPath + ".invalid-" + (Get-Date).ToString("yyyyMMdd-HHmmss")
		Move-Item -LiteralPath $OutputPath -Destination $invalidPath -Force
		Write-Warning "Moved invalid deck-code snapshot to $invalidPath"
	}
	throw "No deck codes were extracted."
}

$header = @(
	"# Generated by tools/Sync-HSReplayDeckCodes.ps1",
	"# Source: $($sourceUrls -join ' ; ')",
	"# Locale: $Locale",
	"# Format: deckName<TAB>deckstring<TAB>hsreplayDeckId<TAB>archetypeId",
	"# HSReplayAsOf: $((($asOfValues | Select-Object -Unique) -join ' ; '))",
	"# GeneratedAt: $((Get-Date).ToString("o"))",
	"# Count: $($uniqueDeckCodes.Count)",
	""
)

$tempOutputPath = Join-Path $outputDirectory ((Split-Path -Leaf $OutputPath) + ".tmp")
Set-Content -Path $tempOutputPath -Value ($header + $uniqueDeckCodes) -Encoding UTF8
Move-Item -LiteralPath $tempOutputPath -Destination $OutputPath -Force
Write-Host "Wrote $($uniqueDeckCodes.Count) deck codes to $OutputPath"
