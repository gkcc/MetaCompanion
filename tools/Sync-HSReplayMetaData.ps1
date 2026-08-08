param(
	[string]$CookiePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt",
	[string]$Cookie = "",
	[string]$OutputDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Meta",
	[string]$DataDirectory = "",
	[string]$TimeRange = "LAST_7_DAYS",
	[string]$PatchVersion = "",
	[string]$RankRange = "DIAMOND_THROUGH_LEGEND",
	[string]$GameType = "RANKED_STANDARD",
	[string]$Region = "ALL",
	[string]$Locale = "zh-hans",
	[int]$TopOverall = 20,
	[int]$TopPerClass = 5,
	[string]$UserAgent = "",
	[int]$TimeoutSeconds = 30,
	[int]$Retries = 2,
	[int]$ProcessingMaxPolls = 24,
	[int]$ProcessingPollDelaySeconds = 5,
	[DateTimeOffset]$MinimumAsOf = [DateTimeOffset]::MinValue,
	[switch]$SelfTest
)

$ErrorActionPreference = "Stop"

function ConvertTo-HSReplayDateTimeOffset([object]$Value, [string]$Name) {
	if ($Value -is [DateTimeOffset]) {
		return [DateTimeOffset]$Value
	}
	if ($Value -is [DateTime]) {
		$dateTime = [DateTime]$Value
		if ($dateTime.Kind -eq [DateTimeKind]::Unspecified) {
			throw "HSReplay $Name 缺少明确时区，不能提升为 Meta latest。"
		}
		return [DateTimeOffset]$dateTime
	}

	$text = if ($null -eq $Value) { "" } else { ([string]$Value).Trim() }
	if ([string]::IsNullOrWhiteSpace($text) -or
		$text -notmatch '(?i)(Z|[+-]\d{2}:\d{2})$') {
		throw "HSReplay $Name 缺少有效且带时区的 as_of，不能提升为 Meta latest。"
	}
	$result = [DateTimeOffset]::MinValue
	if (-not [DateTimeOffset]::TryParse(
			$text,
			[Globalization.CultureInfo]::InvariantCulture,
			[Globalization.DateTimeStyles]::RoundtripKind,
			[ref]$result)) {
		throw "HSReplay $Name 缺少有效且带时区的 as_of，不能提升为 Meta latest。"
	}
	return $result
}

function Assert-HSReplayAsOfNotBefore(
	[string]$Name,
	[object]$Value,
	[DateTimeOffset]$Minimum
) {
	$asOf = ConvertTo-HSReplayDateTimeOffset $Value $Name
	if ($Minimum -ne [DateTimeOffset]::MinValue -and $asOf -lt $Minimum) {
		throw "HSReplay $Name 数据时间 $($asOf.ToString('o')) 早于当前补丁起点 $($Minimum.ToString('o'))；上游数据仍在生成，不能提升为 Meta latest。"
	}
	return $asOf
}

function Get-HSReplayMetaMinimumAsOf(
	[string]$RequestedTimeRange,
	[DateTimeOffset]$RequestedMinimum
) {
	# CURRENT_PATCH is an upstream HSReplay scope. Its as_of is the snapshot time,
	# not the start of the patch on this machine, so a local patch marker must not
	# be used to reject it.
	if ([string]::Equals(
			$RequestedTimeRange,
			"CURRENT_PATCH",
			[StringComparison]::OrdinalIgnoreCase)) {
		return [DateTimeOffset]::MinValue
	}
	return $RequestedMinimum
}

function Invoke-HSReplayMetaFreshnessSelfTest {
	$minimum = [DateTimeOffset]::Parse("2026-08-05T20:18:25+08:00")
	if ((Get-HSReplayMetaMinimumAsOf "CURRENT_PATCH" $minimum) -ne
		[DateTimeOffset]::MinValue) {
		throw "Meta freshness self-test failed: CURRENT_PATCH kept the local marker gate."
	}
	if ((Get-HSReplayMetaMinimumAsOf "LAST_7_DAYS" $minimum) -ne $minimum) {
		throw "Meta freshness self-test failed: rolling range lost the local marker gate."
	}
	[void](Assert-HSReplayAsOfNotBefore "fresh" "2026-08-05T12:18:26Z" $minimum)
	[void](Assert-HSReplayAsOfNotBefore "equal" "2026-08-05T12:18:25Z" $minimum)
	[void](Assert-HSReplayAsOfNotBefore `
		"utc-date" `
		([DateTime]::SpecifyKind([DateTime]"2026-08-05T12:18:25", [DateTimeKind]::Utc)) `
		$minimum)

	$staleRejected = $false
	try {
		[void](Assert-HSReplayAsOfNotBefore "stale" "2026-08-05T08:31:19Z" $minimum)
	} catch {
		$staleRejected = $_.Exception.Message -match "上游数据仍在生成"
	}
	if (-not $staleRejected) {
		throw "Meta freshness self-test failed: stale as_of was accepted."
	}

	$invalidRejected = $false
	try {
		[void](Assert-HSReplayAsOfNotBefore "invalid" "not-a-date" $minimum)
	} catch {
		$invalidRejected = $_.Exception.Message -match "缺少有效"
	}
	if (-not $invalidRejected) {
		throw "Meta freshness self-test failed: invalid as_of was accepted."
	}

	$unspecifiedRejected = $false
	try {
		[void](Assert-HSReplayAsOfNotBefore `
			"unspecified" `
			([DateTime]::SpecifyKind([DateTime]"2026-08-05T12:18:25", [DateTimeKind]::Unspecified)) `
			$minimum)
	} catch {
		$unspecifiedRejected = $_.Exception.Message -match "缺少明确时区"
	}
	if (-not $unspecifiedRejected) {
		throw "Meta freshness self-test failed: unspecified as_of was accepted."
	}

	Write-Host "Meta freshness self-test passed"
}

if ($SelfTest) {
	Invoke-HSReplayMetaFreshnessSelfTest
	return
}

function Get-MetaCompanionFileSha256([string]$Path) {
	$algorithm = [System.Security.Cryptography.SHA256]::Create()
	$stream = [System.IO.File]::Open(
		$Path,
		[System.IO.FileMode]::Open,
		[System.IO.FileAccess]::Read,
		[System.IO.FileShare]::ReadWrite)
	try {
		return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream)) -replace "-", "")
	} finally {
		$stream.Dispose()
		$algorithm.Dispose()
	}
}

if ($Retries -le 0) {
	throw "Retries 必须大于零。"
}
if ($ProcessingMaxPolls -lt 0 -or $ProcessingPollDelaySeconds -lt 0) {
	throw "ProcessingMaxPolls 和 ProcessingPollDelaySeconds 不能为负数。"
}

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
	throw "未找到 curl.exe。"
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

$effectiveUserAgent = Get-DefaultBrowserUserAgent $UserAgent

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
		throw "未找到 HSReplay Cookie。请创建 $CookiePath 并写入已登录会话的 HSReplay Cookie 请求头，或传入 -CookiePath / -Cookie。请勿提交此文件。"
	}

	$cookieText = Get-Content -Path $CookiePath -Raw
	if ([string]::IsNullOrWhiteSpace($cookieText)) {
		throw "Cookie 文件为空：$CookiePath"
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

function Format-HSReplayResponseBody([string]$Body) {
	if ([string]::IsNullOrWhiteSpace($Body)) {
		return "响应正文为空"
	}

	$decoded = [System.Net.WebUtility]::HtmlDecode($Body)
	$oneLine = ($decoded -replace "\s+", " ").Trim()
	if ($decoded -match "(?is)<title>\s*(?<title>.*?)\s*</title>") {
		$title = (($Matches["title"] -replace "\s+", " ").Trim())
		if ($title -match "(?i)^Just a moment") {
			return "Cloudflare 验证页面（标题：$title）。请从已登录的浏览器会话中更新 HSReplay Premium Cookie，然后重试。如果问题持续，HSReplay 可能暂时阻止了脚本访问。"
		}
		return "HTML 响应（标题：$title）"
	}
	if ($decoded -match "(?i)cloudflare|challenges\.cloudflare\.com") {
		return "Cloudflare 验证页面。请从已登录的浏览器会话中更新 HSReplay Premium Cookie，然后重试。如果问题持续，HSReplay 可能暂时阻止了脚本访问。"
	}
	if ($oneLine.StartsWith("<")) {
		return "无标题的 HTML 响应"
	}
	if ($oneLine.Length -gt 500) {
		return $oneLine.Substring(0, 500) + "... [已截断]"
	}
	return $oneLine
}

function New-HSReplayHttpError([int]$StatusCode, [string]$Name, [string]$Body, [bool]$AuthenticationFailure) {
	$response = Format-HSReplayResponseBody $Body
	if ($AuthenticationFailure -and $response -match "(?i)Cloudflare") {
		return "HSReplay 返回 HTTP $StatusCode（$Name）。HSReplay 返回的是 Cloudflare 验证页面，而非 JSON。请从已登录的浏览器会话中更新 HSReplay Premium Cookie，然后重试。如果问题持续，HSReplay 可能暂时阻止了脚本访问。"
	}
	if ($AuthenticationFailure) {
		return "HSReplay 返回 HTTP $StatusCode（$Name）。Cookie 缺失、已过期，或没有有效的 Premium 订阅。响应：$response"
	}
	return "HSReplay 返回 HTTP $StatusCode（$Name）。响应：$response"
}

function Test-HSReplayProcessingResponse([int]$StatusCode, [string]$Body) {
	if ($StatusCode -eq 202) {
		return $true
	}
	return (-not [string]::IsNullOrWhiteSpace($Body) -and
		$Body -match '(?i)query\s+is\s+processing|check\s+back\s+later')
}

function Assert-HSReplayMetaPayload([string]$Name, [int]$StatusCode, [string]$Body) {
	if ($StatusCode -ne 200) {
		if ($StatusCode -eq 204) {
			throw "HSReplay 为 $Name 返回 HTTP 204 空结果，不能提升为 Meta latest。"
		}
		throw "HSReplay 为 $Name 返回了未预期的成功状态 HTTP $StatusCode。"
	}
	if ([string]::IsNullOrWhiteSpace($Body)) {
		throw "HSReplay 为 $Name 返回 HTTP 200，但响应正文为空。"
	}

	try {
		$payload = $Body | ConvertFrom-Json -ErrorAction Stop
	} catch {
		throw "HSReplay 为 $Name 返回 HTTP 200，但响应不是有效 JSON：$($_.Exception.Message)"
	}
	if ($payload.PSObject.Properties.Name -contains "msg" -and
		[string]$payload.msg -match '(?i)query\s+is\s+processing|check\s+back\s+later') {
		throw "HSReplay 查询 $Name 仍在处理中，不能写入 Meta latest 缓存。"
	}

	if ([string]::Equals($Name, "archetypes", [StringComparison]::OrdinalIgnoreCase)) {
		$archetypeItems = if ($payload -is [System.Array]) {
			@($payload)
		} elseif ($payload.PSObject.Properties.Name -contains "results") {
			@($payload.results)
		} elseif ($payload.PSObject.Properties.Name -contains "id") {
			@($payload)
		} else {
			@()
		}
		if ($archetypeItems.Count -eq 0 -or $null -eq $archetypeItems[0].id) {
			throw "HSReplay 的 archetypes 响应不包含有效流派数组。"
		}
		return
	}

	$series = @($payload.series)
	if ($series.Count -eq 0 -or $null -eq $series[0] -or
		-not ($series[0].PSObject.Properties.Name -contains "data") -or
		$null -eq $series[0].data) {
		throw "HSReplay 为 $Name 返回的 JSON 不包含完整的 series[0].data。"
	}
}

function Publish-MetaLatestAtomically(
	[string]$RunDirectory,
	[string]$LatestDirectory,
	[string[]]$FileNames,
	[string]$RunId,
	[DateTimeOffset]$MinimumAsOf = [DateTimeOffset]::MinValue
) {
	$latestParent = Split-Path -Parent $LatestDirectory
	$publishToken = "$RunId-$([Guid]::NewGuid().ToString('N'))"
	$stagingDirectory = Join-Path $latestParent ".latest-$publishToken.staging"
	$backupDirectory = Join-Path $latestParent ".latest-$publishToken.backup"
	New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null

	try {
		if (Test-Path -LiteralPath $LatestDirectory) {
			Get-ChildItem -LiteralPath $LatestDirectory -Force | ForEach-Object {
				Copy-Item -LiteralPath $_.FullName -Destination $stagingDirectory -Recurse -Force
			}
		}
		foreach ($fileName in $FileNames) {
			$sourcePath = Join-Path $RunDirectory $fileName
			if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
				throw "Meta 运行产物不完整，缺少：$sourcePath"
			}
			Copy-Item `
				-LiteralPath $sourcePath `
				-Destination (Join-Path $stagingDirectory $fileName) `
				-Force
		}

		$stagedManifestPath = Join-Path $stagingDirectory "manifest.json"
		$stagedSummaryPath = Join-Path $stagingDirectory "summary.json"
		$stagedMatrixPath = Join-Path $stagingDirectory "head_to_head_archetype_matchups_v2.json"
		try {
			$stagedManifest = Get-Content -LiteralPath $stagedManifestPath -Encoding UTF8 -Raw |
				ConvertFrom-Json -ErrorAction Stop
			$stagedSummary = Get-Content -LiteralPath $stagedSummaryPath -Encoding UTF8 -Raw |
				ConvertFrom-Json -ErrorAction Stop
			$stagedMatrix = Get-Content -LiteralPath $stagedMatrixPath -Encoding UTF8 -Raw |
				ConvertFrom-Json -ErrorAction Stop
		} catch {
			throw "Meta staging 产物无效，不能提升为 latest：$($_.Exception.Message)"
		}
		if ([string]::IsNullOrWhiteSpace([string]$stagedManifest.run_id) -or
			-not [string]::Equals(
				[string]$stagedManifest.run_id,
				$RunId,
				[StringComparison]::Ordinal)) {
			throw "Meta manifest.json 的 run_id 与本次发布不一致，不能提升为 latest。"
		}
		[void](Assert-HSReplayAsOfNotBefore "summary" $stagedSummary.as_of $MinimumAsOf)
		[void](Assert-HSReplayAsOfNotBefore `
			"head_to_head_archetype_matchups_v2" `
			$stagedMatrix.as_of `
			$MinimumAsOf)

		$completion = [ordered]@{
			run_id = $RunId
			completed_at = (Get-Date).ToString("o")
			manifest_sha256 = Get-MetaCompanionFileSha256 $stagedManifestPath
		}
		Set-Content `
			-LiteralPath (Join-Path $stagingDirectory "publish-complete.json") `
			-Value ($completion | ConvertTo-Json -Depth 3) `
			-Encoding UTF8

		$hadLatest = Test-Path -LiteralPath $LatestDirectory
		if ($hadLatest) {
			[System.IO.Directory]::Move($LatestDirectory, $backupDirectory)
		}
		try {
			[System.IO.Directory]::Move($stagingDirectory, $LatestDirectory)
		} catch {
			if ($hadLatest -and -not (Test-Path -LiteralPath $LatestDirectory) -and
				(Test-Path -LiteralPath $backupDirectory)) {
				[System.IO.Directory]::Move($backupDirectory, $LatestDirectory)
			}
			throw
		}
	} finally {
		if (Test-Path -LiteralPath $stagingDirectory) {
			Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
		}
		if ((Test-Path -LiteralPath $backupDirectory) -and
			(Test-Path -LiteralPath $LatestDirectory)) {
			Remove-Item -LiteralPath $backupDirectory -Recurse -Force -ErrorAction SilentlyContinue
		}
	}
}

function Normalize-HearthstonePatchVersion([string]$Value) {
	if ([string]::IsNullOrWhiteSpace($Value)) {
		return ""
	}
	$match = [regex]::Match($Value, "\b(\d+\.\d+\.\d+(?:\.\d+)?)\b")
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

function Resolve-HearthstonePatchVersion([string]$PreferredVersion, [string]$PreferredDataDirectory) {
	$normalized = Normalize-HearthstonePatchVersion $PreferredVersion
	if (-not [string]::IsNullOrWhiteSpace($normalized)) {
		return $normalized
	}

	$effectiveDataDirectory = if ([string]::IsNullOrWhiteSpace($PreferredDataDirectory)) {
		"$env:APPDATA\HearthstoneDeckTracker\MetaCompanion"
	} else {
		$PreferredDataDirectory
	}
	$patchVersionPath = Join-Path $effectiveDataDirectory "patch_version.txt"
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
	$processingPollCount = 0
	while ($true) {
		$pollAgain = $false
		for ($attempt = 1; $attempt -le $Retries; $attempt++) {
			$bodyPath = [System.IO.Path]::GetTempFileName()
			try {
				$statusText = & curl.exe -s -L -A $effectiveUserAgent -H "Accept: application/json" @CookieArgs `
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
				throw "curl.exe 调用失败，无法读取 $Name"
			}
			if ($statusText -notmatch "^\d{3}$") {
				throw "无法读取 $Name 的 HTTP 状态。curl 返回：$statusText"
			}

			$statusCode = [int]$statusText
			if (Test-HSReplayProcessingResponse $statusCode $body) {
				$processingPollCount++
				if ($processingPollCount -gt $ProcessingMaxPolls) {
					throw "HSReplay 查询 $Name 在 $ProcessingMaxPolls 次有界轮询后仍在处理中。"
				}
				Write-Host "HSReplay 查询仍在处理中：$Name；等待后进行第 $processingPollCount/$ProcessingMaxPolls 次轮询。"
				if ($ProcessingPollDelaySeconds -gt 0) {
					Start-Sleep -Seconds $ProcessingPollDelaySeconds
				}
				$pollAgain = $true
				break
			}
			if ($statusCode -ge 200 -and $statusCode -lt 300) {
				return @{
					Url = $Url
					StatusCode = $statusCode
					Body = $body
					ProcessingPollCount = $processingPollCount
				}
			}

			if ($statusCode -eq 401 -or $statusCode -eq 403) {
				throw (New-HSReplayHttpError $statusCode $Name $body $true)
			}

			if ($statusCode -eq 400) {
				throw "HSReplay 返回 HTTP 400（$Name），参数被拒绝。响应：$(Format-HSReplayResponseBody $body)"
			}

			if ($attempt -lt $Retries) {
				Start-Sleep -Milliseconds (500 * $attempt)
				continue
			}
			throw (New-HSReplayHttpError $statusCode $Name $body $false)
		}
		if ($pollAgain) {
			continue
		}
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
			$name = "流派 $Id"
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
		name = "流派 $Id"
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

function Write-MetaSummaryFiles(
	[object]$PopularityDistribution,
	[string]$ArchetypesJson,
	[string]$CanonicalAsOf,
	[string]$SummaryJsonPath,
	[string]$SummaryTsvPath
) {
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
		as_of = $CanonicalAsOf
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
		throw "未找到 Meta 摘要：$SummaryJsonPath"
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
		throw "未找到 Meta 缓存源目录：$SourceDirectory"
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
	[object[]]$Candidates,
	[string]$RunId
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
	$manifestMap["run_id"] = $RunId
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

	Write-Host "正在从以下候选值自动选择 HSReplay Meta TimeRange：$($candidateTimeRanges -join ', ')"
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
			PatchVersion = $effectivePatchVersion
		}
		if (-not [string]::IsNullOrWhiteSpace($DataDirectory)) {
			$candidateArgs.DataDirectory = $DataDirectory
		}
		if ($MinimumAsOf -ne [DateTimeOffset]::MinValue) {
			$candidateArgs.MinimumAsOf = $MinimumAsOf
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
				throw "未为 TimeRange=$candidateTimeRange 创建运行目录。"
			}

			$results.Add([pscustomobject]@{
				time_range = $candidateTimeRange
				sample_games = $sampleGames
				summary_as_of = $summary.as_of
				latest_directory = $candidateLatest
				run_directory = $candidateRun.FullName
			}) | Out-Null
			Write-Host "候选项：TimeRange=$candidateTimeRange，样本对局数=$sampleGames，数据时间=$($summary.as_of)"
		} catch {
			Write-Warning "候选 TimeRange=$candidateTimeRange 失败：$($_.Exception.Message)"
		}
	}

	if ($results.Count -eq 0) {
		throw "没有任何 HSReplay Meta TimeRange 候选值成功。"
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

	Copy-MetaCacheFiles $selected.run_directory $realRunDirectory
	Set-AutoTimeRangeManifest `
		-ManifestPath (Join-Path $realRunDirectory "manifest.json") `
		-Selected $selected `
		-Candidates $candidateArray `
		-RunId $runId

	$realManifest = Get-Content `
		-LiteralPath (Join-Path $realRunDirectory "manifest.json") `
		-Encoding UTF8 `
		-Raw | ConvertFrom-Json -ErrorAction Stop
	$publishFileNames = @($realManifest.items | ForEach-Object { [string]$_.file }) + @(
		"summary.json",
		"summary.tsv",
		"manifest.json"
	)
	Publish-MetaLatestAtomically `
		-RunDirectory $realRunDirectory `
		-LatestDirectory $realLatestDirectory `
		-FileNames $publishFileNames `
		-RunId $runId `
		-MinimumAsOf (Get-HSReplayMetaMinimumAsOf `
			$selected.time_range `
			$MinimumAsOf)

	Write-Host "已选择 HSReplay Meta TimeRange=$($selected.time_range)，样本对局数=$($selected.sample_games)。"
	Write-Host "已将 HSReplay Meta 缓存写入 $realRunDirectory"
	Write-Host "摘要：$(Join-Path $realLatestDirectory 'summary.tsv')"
}

$effectivePatchVersion = Resolve-HearthstonePatchVersion $PatchVersion $DataDirectory
if (-not [string]::IsNullOrWhiteSpace($effectivePatchVersion)) {
	Write-Host "检测到炉石传说补丁版本：$effectivePatchVersion"
}

if ($TimeRange -in @("AUTO_CURRENT_PATCH_OR_LAST_3_DAYS", "AUTO")) {
	Invoke-AutoCurrentPatchOrLast3DaysMetaSync
	return
}

$effectiveMinimumAsOf = Get-HSReplayMetaMinimumAsOf $TimeRange $MinimumAsOf

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
	run_id = $runId
	generated_at = (Get-Date).ToString("o")
	minimum_as_of = if ($effectiveMinimumAsOf -eq [DateTimeOffset]::MinValue) { "" } else { $effectiveMinimumAsOf.ToString("o") }
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

Write-Host "正在获取 HSReplay Meta 分析数据..."
Write-Host "参数：TimeRange=$TimeRange，GameType=$GameType，段位范围=$RankRange，地区=$Region，语言=$Locale"

$responses = @{}
$completedCount = 0
$successCount = 0
$processingPollCount = 0
$noContentCount = 0
$skippedCount = 0
foreach ($request in $requests) {
	Write-Host "正在获取 $($request.name)..."
	$response = Invoke-HSReplayJson $request.url $request.name $cookieArgs
	$processingPollCount += [int]$response.ProcessingPollCount
	Assert-HSReplayMetaPayload $request.name $response.StatusCode $response.Body
	$runPath = Join-Path $runDirectory $request.file
	Set-Content -Path $runPath -Value $response.Body -Encoding UTF8
	$responses[$request.name] = $response.Body
	$manifest.items += [ordered]@{
		name = $request.name
		status_code = $response.StatusCode
		file = $request.file
		url = $response.Url
	}
	$successCount++
	$completedCount++
	Write-Host "Meta 进度 $completedCount/$($requests.Count)：成功=$successCount，处理中轮询=$processingPollCount，空结果=$noContentCount，跳过=$skippedCount。"
}

$popularityDistribution = $responses["archetype_popularity_distribution_stats_v2"] | ConvertFrom-Json
$headToHeadMatrix = $responses["head_to_head_archetype_matchups_v2"] | ConvertFrom-Json
$popularityAsOf = Assert-HSReplayAsOfNotBefore `
	"archetype_popularity_distribution_stats_v2" `
	$popularityDistribution.as_of `
	$effectiveMinimumAsOf
$headToHeadAsOf = Assert-HSReplayAsOfNotBefore `
	"head_to_head_archetype_matchups_v2" `
	$headToHeadMatrix.as_of `
	$effectiveMinimumAsOf
$runSummaryJsonPath = Join-Path $runDirectory "summary.json"
$runSummaryTsvPath = Join-Path $runDirectory "summary.tsv"
$latestSummaryJsonPath = Join-Path $latestDirectory "summary.json"
$latestSummaryTsvPath = Join-Path $latestDirectory "summary.tsv"

Write-MetaSummaryFiles `
	$popularityDistribution `
	$responses["archetypes"] `
	$popularityAsOf.ToString("o", [Globalization.CultureInfo]::InvariantCulture) `
	$runSummaryJsonPath `
	$runSummaryTsvPath

$manifest["summary"] = [ordered]@{
	success = $successCount
	processing_polls = $processingPollCount
	no_content = $noContentCount
	skipped = $skippedCount
}
$manifestJson = $manifest | ConvertTo-Json -Depth 6
$runManifestPath = Join-Path $runDirectory "manifest.json"
Set-Content -Path $runManifestPath -Value $manifestJson -Encoding UTF8

$publishFileNames = @($requests | ForEach-Object { $_.file }) + @(
	"summary.json",
	"summary.tsv",
	"manifest.json"
)
Publish-MetaLatestAtomically `
	-RunDirectory $runDirectory `
	-LatestDirectory $latestDirectory `
	-FileNames $publishFileNames `
	-RunId $runId `
	-MinimumAsOf $effectiveMinimumAsOf

Write-Host "已将 HSReplay Meta 缓存写入 $runDirectory"
Write-Host "摘要：$latestSummaryTsvPath"
