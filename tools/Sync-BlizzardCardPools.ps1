param(
	[string]$OutputRoot = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\AdvisorData\OfficialCardPools",
	[string]$CardDefsPath = "$env:APPDATA\HearthstoneDeckTracker\CardDefs\CardDefs.base.xml",
	[string]$StandardFixturePath = "",
	[string]$ArenaFixturePath = "",
	[string]$SiteLocale = "en-us",
	[string]$ApiLocale = "en_US",
	[int]$PageSize = 450,
	[int]$RequestTimeoutSeconds = 30,
	[int]$MaxResponseBytes = 8 * 1024 * 1024,
	[switch]$SelfTest
)

$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 does not always preload this framework assembly. Load it
# explicitly before constructing HttpClient so the real network path behaves the
# same way as PowerShell 7 and the offline fixture path.
Add-Type -AssemblyName System.Net.Http

$script:RequiredSnapshotFiles = @("standard.json", "arena.json", "manifest.json")
$script:BlizzardHost = "hearthstone.blizzard.com"
$script:BlizzardPathSuffix = "/api/cards"

function Write-Utf8NoBom([string]$Path, [string]$Value) {
	$encoding = New-Object System.Text.UTF8Encoding($false)
	[System.IO.File]::WriteAllText($Path, $Value, $encoding)
}

function Write-JsonFile([string]$Path, $Value) {
	$json = $Value | ConvertTo-Json -Depth 16
	Write-Utf8NoBom -Path $Path -Value ($json + "`n")
}

function Format-UtcInstant([DateTimeOffset]$Value) {
	return $Value.ToUniversalTime().ToString("o", [Globalization.CultureInfo]::InvariantCulture)
}

function Assert-Condition([bool]$Condition, [string]$Message) {
	if (-not $Condition) {
		throw $Message
	}
}

function Assert-FixedBlizzardUri([string]$Url) {
	$uri = $null
	if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) {
		throw "Invalid Blizzard Card Library URL: $Url"
	}
	$expectedPath = "/" + [Uri]::EscapeDataString($SiteLocale) + $script:BlizzardPathSuffix
	$query = $uri.Query.TrimStart("?")
	$queryMatch = [regex]::Match(
		$query,
		"^(?:set=standard|gameMode=arena)&pageSize=(?<pageSize>[0-9]+)&page=(?<page>[0-9]+)&locale=(?<locale>[a-z]{2}_[A-Z]{2})$")
	if ($uri.Scheme -ne "https" -or
		-not $uri.Host.Equals($script:BlizzardHost, [StringComparison]::OrdinalIgnoreCase) -or
		-not $uri.IsDefaultPort -or
		-not $uri.AbsolutePath.Equals($expectedPath, [StringComparison]::Ordinal) -or
		-not $queryMatch.Success -or
		[int]$queryMatch.Groups["pageSize"].Value -lt 1 -or
		[int]$queryMatch.Groups["pageSize"].Value -gt 450 -or
		[int]$queryMatch.Groups["page"].Value -lt 1 -or
		-not $queryMatch.Groups["locale"].Value.Equals($ApiLocale, [StringComparison]::Ordinal)) {
		throw "Card pool requests are restricted to the fixed Blizzard Card Library origin, path, and query contract: $Url"
	}
	return $uri
}

function Get-BlizzardPoolUrl([string]$Format, [int]$Page) {
	$selector = switch ($Format) {
		"standard" { "set=standard" }
		"arena" { "gameMode=arena" }
		default { throw "Unsupported official card pool format: $Format" }
	}
	$site = [Uri]::EscapeDataString($SiteLocale)
	$locale = [Uri]::EscapeDataString($ApiLocale)
	$url = "https://$($script:BlizzardHost)/$site/api/cards?$selector&pageSize=$PageSize&page=$Page&locale=$locale"
	[void](Assert-FixedBlizzardUri $url)
	return $url
}

function Invoke-FixedBlizzardGet([string]$Url) {
	$uri = Assert-FixedBlizzardUri $Url
	$handler = New-Object System.Net.Http.HttpClientHandler
	$handler.AllowAutoRedirect = $false
	$handler.UseCookies = $false
	$handler.AutomaticDecompression =
		[System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
	$client = New-Object System.Net.Http.HttpClient($handler)
	$client.Timeout = [TimeSpan]::FromSeconds($RequestTimeoutSeconds)
	$client.DefaultRequestHeaders.UserAgent.ParseAdd("MetaCompanion-OfficialCardPools/1.0")
	$client.DefaultRequestHeaders.Accept.ParseAdd("application/json")
	$response = $null
	$stream = $null
	$memory = $null
	try {
		$response = $client.GetAsync(
			$uri,
			[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
		$statusCode = [int]$response.StatusCode
		if ($statusCode -ge 300 -and $statusCode -lt 400) {
			$location = if ($null -ne $response.Headers.Location) { [string]$response.Headers.Location } else { "(missing)" }
			throw "Blizzard Card Library redirect was rejected: HTTP $statusCode Location=$location"
		}
		if (-not $response.IsSuccessStatusCode) {
			throw "Blizzard Card Library returned HTTP $statusCode for $Url"
		}
		$contentLength = $response.Content.Headers.ContentLength
		if ($contentLength.HasValue -and $contentLength.Value -gt $MaxResponseBytes) {
			throw "Blizzard Card Library response exceeds the $MaxResponseBytes byte limit: $($contentLength.Value)"
		}

		$stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
		$memory = New-Object System.IO.MemoryStream
		$buffer = New-Object byte[] 16384
		$total = 0L
		while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
			$total += $read
			if ($total -gt $MaxResponseBytes) {
				throw "Blizzard Card Library response exceeded the $MaxResponseBytes byte streaming limit."
			}
			$memory.Write($buffer, 0, $read)
		}
		$body = [Text.Encoding]::UTF8.GetString($memory.ToArray())
		return [pscustomobject]@{
			StatusCode = $statusCode
			Body = $body
			ETag = if ($null -ne $response.Headers.ETag) { [string]$response.Headers.ETag } else { "" }
			LastModified = if ($response.Content.Headers.LastModified.HasValue) {
				$response.Content.Headers.LastModified.Value.ToUniversalTime().ToString("R", [Globalization.CultureInfo]::InvariantCulture)
			} else { "" }
			SourceKind = "network"
		}
	} finally {
		if ($null -ne $memory) { $memory.Dispose() }
		if ($null -ne $stream) { $stream.Dispose() }
		if ($null -ne $response) { $response.Dispose() }
		$client.Dispose()
		$handler.Dispose()
	}
}

function Get-SourceResponse(
	[string]$Format,
	[int]$Page,
	[string]$Url,
	[string]$FixturePath,
	[scriptblock]$ResponseProvider
) {
	if ($null -ne $ResponseProvider) {
		$response = & $ResponseProvider $Format $Page $Url $FixturePath
		if ($null -eq $response) { throw "Response provider returned null for $Format page $Page." }
		return $response
	}
	if (-not [string]::IsNullOrWhiteSpace($FixturePath)) {
		if ($Page -ne 1) {
			throw "Offline fixture responses must contain a single API page: $FixturePath"
		}
		if (-not (Test-Path -LiteralPath $FixturePath -PathType Leaf)) {
			throw "Official card pool fixture was not found: $FixturePath"
		}
		$file = Get-Item -LiteralPath $FixturePath
		if ($file.Length -gt $MaxResponseBytes) {
			throw "Official card pool fixture exceeds the $MaxResponseBytes byte limit: $FixturePath"
		}
		return [pscustomobject]@{
			StatusCode = 200
			Body = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
			ETag = '"fixture-' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '"'
			LastModified = ([DateTimeOffset]$file.LastWriteTimeUtc).ToString("R", [Globalization.CultureInfo]::InvariantCulture)
			SourceKind = "fixture"
		}
	}
	return Invoke-FixedBlizzardGet -Url $Url
}

function ConvertTo-RequiredInt($Value, [string]$Field, [string]$Context, [int]$Minimum = 0) {
	$result = 0
	if ($null -eq $Value -or -not [int]::TryParse(
		[string]$Value,
		[Globalization.NumberStyles]::Integer,
		[Globalization.CultureInfo]::InvariantCulture,
		[ref]$result) -or $result -lt $Minimum) {
		throw "$Context has invalid $Field."
	}
	return $result
}

function ConvertTo-OptionalInt($Value, [string]$Field, [string]$Context) {
	if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return $null }
	return ConvertTo-RequiredInt -Value $Value -Field $Field -Context $Context
}

function ConvertTo-IntArray($Value, [string]$Field, [string]$Context) {
	$result = New-Object System.Collections.Generic.List[int]
	foreach ($item in @($Value)) {
		if ($null -eq $item) { continue }
		$result.Add((ConvertTo-RequiredInt -Value $item -Field $Field -Context $Context))
	}
	return @($result.ToArray() | Sort-Object -Unique)
}

function Read-CardDefsIndex([string]$Path) {
	if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		throw "HDT CardDefs file was not found: $Path"
	}
	$file = Get-Item -LiteralPath $Path
	if ($file.Length -le 0 -or $file.Length -gt 128MB) {
		throw "HDT CardDefs file size is outside the allowed range: $($file.Length) bytes"
	}
	$settings = New-Object System.Xml.XmlReaderSettings
	$settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
	$settings.XmlResolver = $null
	$settings.MaxCharactersInDocument = 128MB
	$reader = [System.Xml.XmlReader]::Create($file.FullName, $settings)
	$byDbfId = @{}
	$cardIds = @{}
	$build = ""
	try {
		while ($reader.Read()) {
			if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
			if ($reader.Name -eq "CardDefs") {
				$build = ([string]$reader.GetAttribute("build")).Trim()
				continue
			}
			if ($reader.Name -ne "Entity") { continue }
			$cardId = ([string]$reader.GetAttribute("CardID")).Trim()
			$dbfText = ([string]$reader.GetAttribute("ID")).Trim()
			$dbfId = 0
			if ([string]::IsNullOrWhiteSpace($cardId) -or
				-not [int]::TryParse($dbfText, [ref]$dbfId) -or $dbfId -le 0) {
				continue
			}
			if ($byDbfId.ContainsKey($dbfId) -and $byDbfId[$dbfId] -ne $cardId) {
				throw "CardDefs contains duplicate dbfId $dbfId."
			}
			if ($cardIds.ContainsKey($cardId) -and [int]$cardIds[$cardId] -ne $dbfId) {
				throw "CardDefs contains duplicate CardID $cardId."
			}
			$byDbfId[$dbfId] = $cardId
			$cardIds[$cardId] = $dbfId
		}
	} finally {
		$reader.Dispose()
	}
	if ([string]::IsNullOrWhiteSpace($build) -or $byDbfId.Count -eq 0) {
		throw "HDT CardDefs did not contain a build and card index."
	}
	return [pscustomobject]@{
		Build = $build
		ByDbfId = $byDbfId
		EntityCount = $byDbfId.Count
		Bytes = [long]$file.Length
		Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
	}
}

function Read-OfficialPool(
	[string]$Format,
	[string]$FixturePath,
	$CardDefs,
	[string]$RunId,
	[scriptblock]$ResponseProvider
) {
	$allCards = New-Object System.Collections.Generic.List[object]
	$pageRecords = New-Object System.Collections.Generic.List[object]
	$page = 1
	$expectedPages = 0
	$declaredCount = -1
	$fetchedAt = [DateTimeOffset]::UtcNow
	do {
		$url = Get-BlizzardPoolUrl -Format $Format -Page $page
		$response = Get-SourceResponse -Format $Format -Page $page -Url $url `
			-FixturePath $FixturePath -ResponseProvider $ResponseProvider
		if ([int]$response.StatusCode -ne 200) {
			throw "Official $Format card pool returned HTTP $($response.StatusCode)."
		}
		$body = [string]$response.Body
		if ([Text.Encoding]::UTF8.GetByteCount($body) -gt $MaxResponseBytes) {
			throw "Official $Format card pool response exceeds the $MaxResponseBytes byte limit."
		}
		try { $payload = $body | ConvertFrom-Json } catch { throw "Official $Format card pool returned invalid JSON: $($_.Exception.Message)" }
		$context = "$Format page $page"
		$currentCount = ConvertTo-RequiredInt -Value $payload.cardCount -Field "cardCount" -Context $context -Minimum 1
		$currentPages = ConvertTo-RequiredInt -Value $payload.pageCount -Field "pageCount" -Context $context -Minimum 1
		$currentPage = ConvertTo-RequiredInt -Value $payload.page -Field "page" -Context $context -Minimum 1
		if ($currentPage -ne $page) { throw "$context reported page $currentPage." }
		if ($page -eq 1) {
			$declaredCount = $currentCount
			$expectedPages = $currentPages
			if (-not [string]::IsNullOrWhiteSpace($FixturePath) -and $expectedPages -ne 1) {
				throw "Offline fixture must contain exactly one page: $FixturePath"
			}
		} elseif ($currentCount -ne $declaredCount -or $currentPages -ne $expectedPages) {
			throw "Official $Format pagination metadata changed during the sync."
		}
		$cards = @($payload.cards)
		if ($cards.Count -eq 0) { throw "$context returned no cards." }
		foreach ($card in $cards) { $allCards.Add($card) }
		$pageRecords.Add([pscustomobject][ordered]@{
			page = $page
			url = $url
			source_kind = [string]$response.SourceKind
			status_code = [int]$response.StatusCode
			etag = [string]$response.ETag
			last_modified = [string]$response.LastModified
			row_count = $cards.Count
			declared_count = $currentCount
		})
		$page++
	} while ($page -le $expectedPages)

	if ($allCards.Count -ne $declaredCount) {
		throw "Official $Format card pool declared $declaredCount cards but returned $($allCards.Count)."
	}
	$dbfIds = @{}
	$cardIds = @{}
	$normalized = New-Object System.Collections.Generic.List[object]
	foreach ($raw in $allCards) {
		$dbfId = ConvertTo-RequiredInt -Value $raw.id -Field "id" -Context "$Format card" -Minimum 1
		if ($dbfIds.ContainsKey($dbfId)) { throw "Official $Format card pool contains duplicate dbfId $dbfId." }
		if (-not $CardDefs.ByDbfId.ContainsKey($dbfId)) { throw "Official $Format dbfId $dbfId is missing from local CardDefs." }
		$cardId = [string]$CardDefs.ByDbfId[$dbfId]
		if ($cardIds.ContainsKey($cardId)) { throw "Official $Format card pool contains duplicate CardID $cardId." }
		$collectible = $false
		if ($raw.collectible -is [bool]) { $collectible = [bool]$raw.collectible }
		else { $collectible = ([string]$raw.collectible -eq "1") }
		if (-not $collectible) { throw "Official $Format card $cardId is not collectible." }
		$dbfIds[$dbfId] = $true
		$cardIds[$cardId] = $true
		$normalized.Add([pscustomobject][ordered]@{
			card_id = $cardId
			dbf_id = $dbfId
			slug = [string]$raw.slug
			name = [string]$raw.name
			collectible = $true
			card_set_id = ConvertTo-OptionalInt -Value $raw.cardSetId -Field "cardSetId" -Context "$Format card $cardId"
			class_id = ConvertTo-OptionalInt -Value $raw.classId -Field "classId" -Context "$Format card $cardId"
			multi_class_ids = @(ConvertTo-IntArray -Value $raw.multiClassIds -Field "multiClassIds" -Context "$Format card $cardId")
			card_type_id = ConvertTo-OptionalInt -Value $raw.cardTypeId -Field "cardTypeId" -Context "$Format card $cardId"
			spell_school_id = ConvertTo-OptionalInt -Value $raw.spellSchoolId -Field "spellSchoolId" -Context "$Format card $cardId"
			minion_type_id = ConvertTo-OptionalInt -Value $raw.minionTypeId -Field "minionTypeId" -Context "$Format card $cardId"
			multi_type_ids = @(ConvertTo-IntArray -Value $raw.multiTypeIds -Field "multiTypeIds" -Context "$Format card $cardId")
			keyword_ids = @(ConvertTo-IntArray -Value $raw.keywordIds -Field "keywordIds" -Context "$Format card $cardId")
			rarity_id = ConvertTo-OptionalInt -Value $raw.rarityId -Field "rarityId" -Context "$Format card $cardId"
			mana_cost = ConvertTo-OptionalInt -Value $raw.manaCost -Field "manaCost" -Context "$Format card $cardId"
			attack = ConvertTo-OptionalInt -Value $raw.attack -Field "attack" -Context "$Format card $cardId"
			health = ConvertTo-OptionalInt -Value $raw.health -Field "health" -Context "$Format card $cardId"
			durability = ConvertTo-OptionalInt -Value $raw.durability -Field "durability" -Context "$Format card $cardId"
			text = [string]$raw.text
		})
	}
	$orderedCards = @($normalized.ToArray() | Sort-Object dbf_id)
	return [pscustomobject]@{
		Format = $Format
		FileName = "$Format.json"
		DeclaredCount = $declaredCount
		UniqueDbfIds = $dbfIds.Count
		UniqueCardIds = $cardIds.Count
		FetchedAt = $fetchedAt
		Pages = $pageRecords.ToArray()
		Document = [ordered]@{
			schema_version = 1
			dataset = "metacompanion.official_$($Format)_card_pool"
			format = $Format
			run_id = $RunId
			fetched_at_utc = Format-UtcInstant $fetchedAt
			source = [ordered]@{
				provider = "Blizzard"
					product = "Hearthstone Card Library"
					authentication = "none"
					browser_required = $false
					cookies = "disabled"
					url_template = (Get-BlizzardPoolUrl -Format $Format -Page 1).Replace("page=1", "page={page}")
				pages = $pageRecords.ToArray()
			}
			coverage = [ordered]@{
				kind = "official_current_constructible_or_draftable_pool"
				is_complete = $true
				generation_pool_metadata = $true
				generation_pool_fields = @(
					"cost", "card_type", "class", "spell_school", "minion_type",
					"rarity", "card_set", "keywords", "collectible"
				)
				rules_coverage = $false
				generated_entities_coverage = $false
			}
			declared_count = $declaredCount
			cards = $orderedCards
		}
	}
}

function Assert-OfficialCardPoolSnapshot([string]$Directory, [bool]$RequirePublishMarker = $false) {
	foreach ($fileName in $script:RequiredSnapshotFiles) {
		$path = Join-Path $Directory $fileName
		if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Official card pool snapshot is missing $fileName." }
	}
	$manifestPath = Join-Path $Directory "manifest.json"
	$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
	if ([int]$manifest.schema_version -ne 1 -or $manifest.status -ne "complete" -or [string]::IsNullOrWhiteSpace([string]$manifest.run_id)) {
		throw "Official card pool manifest is incomplete."
	}
	$pools = @($manifest.pools)
	if ($pools.Count -ne 2) { throw "Official card pool manifest must contain Standard and Arena records." }
	foreach ($format in @("standard", "arena")) {
		$records = @($pools | Where-Object { $_.format -eq $format })
		if ($records.Count -ne 1) { throw "Official card pool manifest is missing $format." }
		$record = $records[0]
		$poolPath = Join-Path $Directory ([string]$record.file)
		if (-not (Test-Path -LiteralPath $poolPath -PathType Leaf)) { throw "Official card pool file is missing: $($record.file)" }
		if ((Get-FileHash -LiteralPath $poolPath -Algorithm SHA256).Hash -ne [string]$record.sha256) { throw "Official $format card pool hash mismatch." }
		$pool = Get-Content -LiteralPath $poolPath -Raw -Encoding UTF8 | ConvertFrom-Json
		$cards = @($pool.cards)
		if ([int]$pool.schema_version -ne 1 -or $pool.format -ne $format -or $pool.run_id -ne $manifest.run_id) { throw "Official $format card pool contract mismatch." }
		if ([bool]$pool.coverage.rules_coverage -or [bool]$pool.coverage.generated_entities_coverage) { throw "Official $format pool must not claim rules or generated entity coverage." }
		if ($cards.Count -ne [int]$pool.declared_count -or
			$cards.Count -ne [int]$record.unique_dbf_ids -or
			$cards.Count -ne [int]$record.unique_card_ids) { throw "Official $format card pool count mismatch." }
		if (@($cards | Group-Object dbf_id | Where-Object Count -ne 1).Count -ne 0 -or
			@($cards | Group-Object card_id | Where-Object Count -ne 1).Count -ne 0 -or
			@($cards | Where-Object { -not [bool]$_.collectible }).Count -ne 0) { throw "Official $format card pool uniqueness or collectible validation failed." }
	}
	if ($RequirePublishMarker) {
		$publishPath = Join-Path $Directory "publish-complete.json"
		if (-not (Test-Path -LiteralPath $publishPath -PathType Leaf)) { throw "Official card pool latest is missing publish-complete.json." }
		$publish = Get-Content -LiteralPath $publishPath -Raw -Encoding UTF8 | ConvertFrom-Json
		if ($publish.run_id -ne $manifest.run_id -or
			$publish.manifest_sha256 -ne (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash) {
			throw "Official card pool publish marker does not bind the current manifest."
		}
	}
	return $manifest
}

function Publish-LatestAtomically([string]$RunDirectory, [string]$LatestDirectory, [string]$RunId) {
	$parent = Split-Path -Parent $LatestDirectory
	$staging = Join-Path $parent (".latest." + [Guid]::NewGuid().ToString("N") + ".staging")
	$backup = Join-Path $parent (".latest." + [Guid]::NewGuid().ToString("N") + ".backup")
	$promoted = $false
	New-Item -ItemType Directory -Path $staging | Out-Null
	try {
		foreach ($fileName in $script:RequiredSnapshotFiles) {
			Copy-Item -LiteralPath (Join-Path $RunDirectory $fileName) -Destination (Join-Path $staging $fileName)
		}
		$null = Assert-OfficialCardPoolSnapshot -Directory $staging
		$completion = [ordered]@{
			schema_version = 1
			run_id = $RunId
			published_at_utc = Format-UtcInstant ([DateTimeOffset]::UtcNow)
			manifest_sha256 = (Get-FileHash -LiteralPath (Join-Path $staging "manifest.json") -Algorithm SHA256).Hash
		}
		Write-JsonFile -Path (Join-Path $staging "publish-complete.json") -Value $completion
		$null = Assert-OfficialCardPoolSnapshot -Directory $staging -RequirePublishMarker $true
		$hadLatest = Test-Path -LiteralPath $LatestDirectory -PathType Container
		if ($hadLatest) { [IO.Directory]::Move($LatestDirectory, $backup) }
		try {
			[IO.Directory]::Move($staging, $LatestDirectory)
			$promoted = $true
		} catch {
			if ($hadLatest -and -not (Test-Path -LiteralPath $LatestDirectory) -and (Test-Path -LiteralPath $backup)) {
				[IO.Directory]::Move($backup, $LatestDirectory)
			}
			throw
		}
	} finally {
		if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
		if ($promoted -and (Test-Path -LiteralPath $backup)) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
	}
}

function Open-CardPoolLock([string]$Root, [int]$TimeoutSeconds = 10) {
	$path = Join-Path $Root ".sync.lock"
	$watch = [Diagnostics.Stopwatch]::StartNew()
	while ($true) {
		try {
			return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
		} catch [IO.IOException] {
			if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw "Timed out waiting for the official card pool lock: $path" }
			Start-Sleep -Milliseconds 100
		}
	}
}

function Invoke-OfficialCardPoolSync(
	[string]$DestinationRoot,
	[string]$DefsPath,
	[string]$StandardFixture,
	[string]$ArenaFixture,
	[scriptblock]$ResponseProvider = $null
) {
	$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
	$runsRoot = Join-Path $DestinationRoot "runs"
	$latestDirectory = Join-Path $DestinationRoot "latest"
	New-Item -ItemType Directory -Force -Path $runsRoot | Out-Null
	$lock = Open-CardPoolLock -Root $DestinationRoot
	try {
		$cardDefs = Read-CardDefsIndex -Path $DefsPath
		$now = [DateTimeOffset]::UtcNow
		$runId = $now.ToString("yyyyMMdd'T'HHmmssfff'Z'", [Globalization.CultureInfo]::InvariantCulture) +
			"-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
		$runDirectory = Join-Path $runsRoot $runId
		$staging = Join-Path $runsRoot ("." + $runId + ".staging")
		New-Item -ItemType Directory -Path $staging | Out-Null
		try {
			$standard = Read-OfficialPool -Format "standard" -FixturePath $StandardFixture `
				-CardDefs $cardDefs -RunId $runId -ResponseProvider $ResponseProvider
			$arena = Read-OfficialPool -Format "arena" -FixturePath $ArenaFixture `
				-CardDefs $cardDefs -RunId $runId -ResponseProvider $ResponseProvider
			$pools = @($standard, $arena)
			foreach ($pool in $pools) {
				Write-JsonFile -Path (Join-Path $staging $pool.FileName) -Value $pool.Document
			}
			$poolRecords = @($pools | ForEach-Object {
				$path = Join-Path $staging $_.FileName
				[pscustomobject][ordered]@{
					format = $_.Format
					file = $_.FileName
					bytes = [long](Get-Item -LiteralPath $path).Length
					sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
					declared_count = $_.DeclaredCount
					unique_dbf_ids = $_.UniqueDbfIds
					unique_card_ids = $_.UniqueCardIds
					fetched_at_utc = Format-UtcInstant $_.FetchedAt
					pages = $_.Pages
				}
			})
			$manifest = [ordered]@{
				schema_version = 1
				dataset = "metacompanion.official_card_pools"
				status = "complete"
				run_id = $runId
				generated_at_utc = Format-UtcInstant $now
				source = [ordered]@{
					provider = "Blizzard"
					product = "Hearthstone Card Library"
					authentication = "none"
					browser_required = $false
					cookies = "disabled"
					allowed_origin = "https://$($script:BlizzardHost)"
					redirects_allowed = $false
					max_response_bytes = $MaxResponseBytes
				}
				card_defs = [ordered]@{
					file_name = "CardDefs.base.xml"
					build = $cardDefs.Build
					entities = $cardDefs.EntityCount
					bytes = $cardDefs.Bytes
					sha256 = $cardDefs.Sha256
				}
				coverage = [ordered]@{
					format_pool_only = $true
					rules_coverage = $false
					generated_entities_coverage = $false
				}
				pools = $poolRecords
			}
			Write-JsonFile -Path (Join-Path $staging "manifest.json") -Value $manifest
			$null = Assert-OfficialCardPoolSnapshot -Directory $staging
			[IO.Directory]::Move($staging, $runDirectory)
		} finally {
			if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
		}
		Publish-LatestAtomically -RunDirectory $runDirectory -LatestDirectory $latestDirectory -RunId $runId
		return [pscustomobject]@{
			RunId = $runId
			RunDirectory = $runDirectory
			LatestDirectory = $latestDirectory
			StandardCards = $standard.DeclaredCount
			ArenaCards = $arena.DeclaredCount
			CardDefsBuild = $cardDefs.Build
		}
	} finally {
		if ($null -ne $lock) { $lock.Dispose() }
	}
}

function Get-DirectoryFingerprint([string]$Directory) {
	return (@(Get-ChildItem -LiteralPath $Directory -File | Sort-Object Name | ForEach-Object {
		$_.Name + ":" + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
	}) -join "|")
}

function Invoke-OfficialCardPoolSelfTest {
	$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
	$tempRoot = Join-Path $tempBase ("MetaCompanion-OfficialCardPools-" + [Guid]::NewGuid().ToString("N"))
	New-Item -ItemType Directory -Path $tempRoot | Out-Null
	try {
		$defs = Join-Path $tempRoot "CardDefs.base.xml"
		$standardFixture = Join-Path $tempRoot "standard.json"
		$arenaFixture = Join-Path $tempRoot "arena.json"
		$output = Join-Path $tempRoot "OfficialCardPools"
		Write-Utf8NoBom -Path $defs -Value @'
<?xml version="1.0" encoding="utf-8"?>
<CardDefs build="999999">
  <Entity CardID="STD_A" ID="1001" />
  <Entity CardID="STD_B" ID="1002" />
  <Entity CardID="ARENA_A" ID="2001" />
</CardDefs>
'@
		Write-Utf8NoBom -Path $standardFixture -Value @'
{"cards":[{"id":1001,"slug":"1001-std-a","name":"Standard A","collectible":1,"cardSetId":10,"classId":2,"cardTypeId":4,"minionTypeId":24,"multiTypeIds":[20],"keywordIds":[1,8],"rarityId":1,"manaCost":1},{"id":1002,"slug":"1002-std-b","name":"Standard B","collectible":1,"cardSetId":10,"classId":null,"multiClassIds":[2,3],"cardTypeId":5,"spellSchoolId":1,"rarityId":2,"manaCost":2}],"cardCount":2,"pageCount":1,"page":1}
'@
		Write-Utf8NoBom -Path $arenaFixture -Value @'
{"cards":[{"id":1001,"slug":"1001-std-a","name":"Standard A","collectible":1,"cardSetId":10,"classId":2,"cardTypeId":4,"rarityId":1,"manaCost":1},{"id":2001,"slug":"2001-arena-a","name":"Arena A","collectible":true,"cardSetId":20,"classId":3,"cardTypeId":4,"rarityId":3,"manaCost":3}],"cardCount":2,"pageCount":1,"page":1}
'@
		$first = Invoke-OfficialCardPoolSync -DestinationRoot $output -DefsPath $defs `
			-StandardFixture $standardFixture -ArenaFixture $arenaFixture
		$manifest = Assert-OfficialCardPoolSnapshot -Directory $first.LatestDirectory -RequirePublishMarker $true
		Assert-Condition ($first.StandardCards -eq 2 -and $first.ArenaCards -eq 2) "Self-test pool counts are incorrect."
		Assert-Condition ($manifest.card_defs.build -eq "999999") "Self-test CardDefs build was not recorded."
		$standard = Get-Content -LiteralPath (Join-Path $first.LatestDirectory "standard.json") -Raw | ConvertFrom-Json
		$arena = Get-Content -LiteralPath (Join-Path $first.LatestDirectory "arena.json") -Raw | ConvertFrom-Json
		Assert-Condition (@($standard.cards | Where-Object card_id -eq "STD_B").Count -eq 1) "Standard CardID mapping failed."
		Assert-Condition (@($arena.cards | Where-Object card_id -eq "ARENA_A").Count -eq 1) "Arena CardID mapping failed."
		Assert-Condition (-not [bool]$arena.coverage.rules_coverage) "Pool incorrectly claimed rules coverage."
		$optionalCard = @($standard.cards | Where-Object card_id -eq "STD_B")[0]
		Assert-Condition ($optionalCard.PSObject.Properties.Name -contains "class_id") "Optional integer fields must remain present in JSON."
		Assert-Condition ($null -eq $optionalCard.class_id) "Optional null integer fields were not preserved."
		Assert-Condition ($optionalCard.spell_school_id -eq 1) "Spell-school metadata was not preserved."
		$typedCard = @($standard.cards | Where-Object card_id -eq "STD_A")[0]
		Assert-Condition ($typedCard.minion_type_id -eq 24) "Minion-type metadata was not preserved."
		Assert-Condition (@($typedCard.multi_type_ids).Count -eq 1) "Multi-type metadata was not preserved."
		Assert-Condition (@($typedCard.keyword_ids).Count -eq 2) "Keyword metadata was not preserved."
		Assert-Condition ([bool]$standard.coverage.generation_pool_metadata) "Generation-pool metadata coverage was not declared."
		$standardManifest = @($manifest.pools | Where-Object format -eq "standard")[0]
		Assert-Condition ($standardManifest.pages[0].source_kind -eq "fixture") "Fixture provenance was not recorded."

		foreach ($untrustedUrl in @(
			"https://example.com/en-us/api/cards?set=standard&pageSize=450&page=1&locale=en_US",
			"https://hearthstone.blizzard.com/en-us/api/cards?set=standard&pageSize=450&page=1&locale=en_US&unexpected=1",
			"https://hearthstone.blizzard.com/not-en-us/api/cards?set=standard&pageSize=450&page=1&locale=en_US"
		)) {
			$rejected = $false
			try { [void](Assert-FixedBlizzardUri $untrustedUrl) } catch { $rejected = $true }
			Assert-Condition $rejected "Untrusted Card Library URL was not rejected: $untrustedUrl"
		}

		$firstRunId = $first.RunId
		Write-Utf8NoBom -Path $standardFixture -Value ((Get-Content -LiteralPath $standardFixture -Raw).Replace("Standard B", "Standard B updated"))
		$second = Invoke-OfficialCardPoolSync -DestinationRoot $output -DefsPath $defs `
			-StandardFixture $standardFixture -ArenaFixture $arenaFixture
		Assert-Condition ($second.RunId -ne $firstRunId) "Second sync did not create a versioned run."
		Assert-Condition (Test-Path -LiteralPath (Join-Path (Join-Path $output "runs") $firstRunId)) "Second sync removed the prior run."
		$fingerprint = Get-DirectoryFingerprint -Directory $second.LatestDirectory

		$networkFailed = $false
		try {
			$offline = { param($Format, $Page, $Url, $FixturePath) throw "simulated network unavailable" }
			$null = Invoke-OfficialCardPoolSync -DestinationRoot $output -DefsPath $defs `
				-StandardFixture "" -ArenaFixture "" -ResponseProvider $offline
		} catch { $networkFailed = $true }
		Assert-Condition $networkFailed "Simulated network outage unexpectedly succeeded."
		Assert-Condition ((Get-DirectoryFingerprint $second.LatestDirectory) -eq $fingerprint) "Latest changed after a network failure."

		Write-Utf8NoBom -Path $arenaFixture -Value @'
{"cards":[{"id":2001,"slug":"duplicate-a","name":"Duplicate A","collectible":1},{"id":2001,"slug":"duplicate-b","name":"Duplicate B","collectible":1}],"cardCount":2,"pageCount":1,"page":1}
'@
		$validationFailed = $false
		try {
			$null = Invoke-OfficialCardPoolSync -DestinationRoot $output -DefsPath $defs `
				-StandardFixture $standardFixture -ArenaFixture $arenaFixture
		} catch { $validationFailed = $true }
		Assert-Condition $validationFailed "Duplicate fixture unexpectedly passed validation."
		Assert-Condition ((Get-DirectoryFingerprint $second.LatestDirectory) -eq $fingerprint) "Latest changed after validation failure."
		Write-Host "Official Blizzard card pool self-test passed (fixtures, mapping, hashes, versioning, atomic latest, outage preservation)."
	} finally {
		$resolved = [IO.Path]::GetFullPath($tempRoot)
		if ($resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
			[IO.Path]::GetFileName($resolved).StartsWith("MetaCompanion-OfficialCardPools-", [StringComparison]::Ordinal)) {
			Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
		}
	}
}

if ($SiteLocale -notmatch "^[a-z]{2}-[a-z]{2}$" -or $ApiLocale -notmatch "^[a-z]{2}_[A-Z]{2}$") {
	throw "SiteLocale/ApiLocale must use fixed locale shapes such as en-us and en_US."
}
if ($PageSize -lt 1 -or $PageSize -gt 450) { throw "PageSize must be between 1 and 450." }
if ($RequestTimeoutSeconds -lt 5 -or $RequestTimeoutSeconds -gt 120) { throw "RequestTimeoutSeconds must be between 5 and 120." }
if ($MaxResponseBytes -lt 65536 -or $MaxResponseBytes -gt 32MB) { throw "MaxResponseBytes must be between 65536 and 33554432." }

if ($SelfTest) {
	Invoke-OfficialCardPoolSelfTest
	return
}

$result = Invoke-OfficialCardPoolSync -DestinationRoot $OutputRoot -DefsPath $CardDefsPath `
	-StandardFixture $StandardFixturePath -ArenaFixture $ArenaFixturePath
Write-Host "Official Blizzard card pools published:"
Write-Host "  run:      $($result.RunDirectory)"
Write-Host "  latest:   $($result.LatestDirectory)"
Write-Host "  CardDefs: build $($result.CardDefsBuild)"
Write-Host "  Standard: $($result.StandardCards) cards"
Write-Host "  Arena:    $($result.ArenaCards) cards"
Write-Host "These snapshots define current format pools only; they do not provide card rules or generated-entity coverage."
