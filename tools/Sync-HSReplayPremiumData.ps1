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
	[string]$UserAgent = "",
	[int]$TimeoutSeconds = 30,
	[int]$Retries = 2,
	[int]$ProcessingMaxPolls = 24,
	[int]$ProcessingPollDelaySeconds = 5,
	[int]$RequestDelayMs = 100,
	[int]$ProgressEvery = 5,
	[switch]$StopOnUnsupported,
	[switch]$ProbeOnly
)

$ErrorActionPreference = "Stop"

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

$supportedTimeRanges = @(
	"LAST_7_DAYS",
	"LAST_30_DAYS",
	"CURRENT_PATCH",
	"CURRENT_EXPANSION",
	"CURRENT_SEASON"
)

if ($supportedTimeRanges -notcontains $TimeRange) {
	throw "HSReplay 分析接口当前不支持这些查询使用 TimeRange=$TimeRange。请使用以下值之一：$($supportedTimeRanges -join ', ')。如需 3 天窗口，请在本地缓存每日拉取结果，再聚合最近 3 天的数据。"
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

function Assert-HSReplayAnalyticsPayload([string]$Endpoint, [int]$StatusCode, [string]$Body) {
	if ($StatusCode -eq 204) {
		return
	}
	if ($StatusCode -ne 200) {
		throw "HSReplay 为 $Endpoint 返回了未预期的成功状态 HTTP $StatusCode。"
	}
	if ([string]::IsNullOrWhiteSpace($Body)) {
		throw "HSReplay 为 $Endpoint 返回 HTTP 200，但响应正文为空。"
	}

	try {
		$payload = $Body | ConvertFrom-Json -ErrorAction Stop
	} catch {
		throw "HSReplay 为 $Endpoint 返回 HTTP 200，但响应不是有效 JSON：$($_.Exception.Message)"
	}
	if ($payload.PSObject.Properties.Name -contains "msg" -and
		[string]$payload.msg -match '(?i)query\s+is\s+processing|check\s+back\s+later') {
		throw "HSReplay 查询 $Endpoint 仍在处理中，不能写入 Premium latest 缓存。"
	}

	$series = @($payload.series)
	if ($series.Count -eq 0 -or $null -eq $series[0] -or
		-not ($series[0].PSObject.Properties.Name -contains "data") -or
		$null -eq $series[0].data) {
		throw "HSReplay 为 $Endpoint 返回的 JSON 不包含完整的 series[0].data。"
	}
}

function Test-HSReplayEndpointSupportsTimeRange([string]$Endpoint, [string]$RequestedTimeRange) {
	if ([string]::Equals(
		$Endpoint,
		"single_deck_base_winrate_by_opponent_class_v2",
		[StringComparison]::OrdinalIgnoreCase)) {
		return @("LAST_30_DAYS", "CURRENT_PATCH", "CURRENT_EXPANSION", "CURRENT_SEASON") -contains $RequestedTimeRange
	}
	return $true
}

function Publish-PremiumLatestAtomically(
	[string]$RunDirectory,
	[string]$LatestDirectory,
	[object[]]$Items,
	[string]$RunId
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
		foreach ($item in $Items) {
			$stagedPath = Join-Path $stagingDirectory $item.file
			if ($item.skipped) {
				Remove-Item -LiteralPath $stagedPath -Force -ErrorAction SilentlyContinue
				continue
			}
			Copy-Item `
				-LiteralPath (Join-Path $RunDirectory $item.file) `
				-Destination $stagedPath `
				-Force
		}
		Copy-Item `
			-LiteralPath (Join-Path $RunDirectory "manifest.json") `
			-Destination (Join-Path $stagingDirectory "manifest.json") `
			-Force

		$stagedManifestPath = Join-Path $stagingDirectory "manifest.json"
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

function Invoke-HSReplayPremiumJson([string]$Endpoint, [hashtable]$Parameters, [object[]]$CookieArgs) {
	$url = "https://hsreplay.net/api/v1/analytics/query/$Endpoint/?$(ConvertTo-QueryString $Parameters)"
	$processingPollCount = 0
	while ($true) {
		$pollAgain = $false
		for ($attempt = 1; $attempt -le $Retries; $attempt++) {
			$bodyPath = [System.IO.Path]::GetTempFileName()
			try {
				$statusText = & curl.exe -s -L -A $effectiveUserAgent -H "Accept: application/json" @CookieArgs `
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
			throw "curl.exe 调用失败，无法读取 $Endpoint"
		}
			if ($statusText -notmatch "^\d{3}$") {
			throw "无法读取 $Endpoint 的 HTTP 状态。curl 返回：$statusText"
		}

			$statusCode = [int]$statusText
		if (Test-HSReplayProcessingResponse $statusCode $body) {
			$processingPollCount++
			if ($processingPollCount -gt $ProcessingMaxPolls) {
				throw "HSReplay 查询 $Endpoint 在 $ProcessingMaxPolls 次有界轮询后仍在处理中。"
			}
			Write-Host "HSReplay 查询仍在处理中：$Endpoint；等待后进行第 $processingPollCount/$ProcessingMaxPolls 次轮询。"
			if ($ProcessingPollDelaySeconds -gt 0) {
				Start-Sleep -Seconds $ProcessingPollDelaySeconds
			}
			$pollAgain = $true
			break
		}
		if ($statusCode -ge 200 -and $statusCode -lt 300) {
			return @{
				Url = $url
				StatusCode = $statusCode
				Body = $body
				ProcessingPollCount = $processingPollCount
			}
		}

		if ($statusCode -eq 401 -or $statusCode -eq 403) {
			throw (New-HSReplayHttpError $statusCode $Endpoint $body $true)
		}

		if ($statusCode -eq 400) {
			throw "HSReplay 返回 HTTP 400（$Endpoint），参数被拒绝。响应：$(Format-HSReplayResponseBody $body)"
		}

		if ($attempt -lt $Retries) {
			Start-Sleep -Milliseconds (500 * $attempt)
			continue
		}
		throw (New-HSReplayHttpError $statusCode $Endpoint $body $false)
		}
		if ($pollAgain) {
			continue
		}
	}
}

function Get-DeckIdsFromSnapshot([string]$Path) {
	if (-not (Test-Path $Path)) {
		throw "未找到牌组代码快照：$Path"
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
	throw "未找到 HSReplay 牌组 ID。请重新运行 Sync-HSReplayDeckCodes.ps1，确保快照中包含牌组 ID。"
}

$cookieArgs = Get-HSReplayCookieArgs
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDirectory = Join-Path (Join-Path $OutputDirectory "runs") $runId
$latestDirectory = Join-Path $OutputDirectory "latest"
New-Item -ItemType Directory -Force -Path $runDirectory, $latestDirectory | Out-Null

$manifest = [ordered]@{
	run_id = $runId
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

Write-Host "正在获取 HSReplay Premium 分析数据..."
Write-Host "参数：TimeRange=$TimeRange，GameType=$GameType，段位范围=$RankRange，地区=$Region"
Write-Host "牌组数=$($deckIdsToFetch.Count)，接口=$($Endpoints -join ', ')"

$unsupportedEndpoints = @($Endpoints | Where-Object {
	-not (Test-HSReplayEndpointSupportsTimeRange $_ $TimeRange)
})
if ($StopOnUnsupported -and $unsupportedEndpoints.Count -gt 0) {
	throw "以下 Premium 接口不支持 TimeRange=$TimeRange：$($unsupportedEndpoints -join ', ')"
}
foreach ($unsupportedEndpoint in $unsupportedEndpoints) {
	Write-Host "兼容模式：已在请求前跳过不受支持的组合 $unsupportedEndpoint / $TimeRange；不会逐牌组发送 HTTP 请求。"
}

function Write-PremiumProgress(
	[int]$CompletedCount,
	[int]$TotalCount,
	[int]$SuccessCount,
	[int]$ProcessingCount,
	[int]$NoContentCount,
	[int]$SkippedCount
) {
	Write-Host "Premium 进度 $CompletedCount/$TotalCount：成功=$SuccessCount，处理中轮询=$ProcessingCount，空结果=$NoContentCount，跳过=$SkippedCount。"
}

$totalRequests = $deckIdsToFetch.Count * $Endpoints.Count
$completed = 0
$successCount = 0
$processingPollCount = 0
$noContentCount = 0
$skippedCount = 0
$http400SkippedCount = 0
foreach ($deckId in $deckIdsToFetch) {
	foreach ($endpoint in $Endpoints) {
		$fileName = "$deckId.$endpoint.json"
		if (-not (Test-HSReplayEndpointSupportsTimeRange $endpoint $TimeRange)) {
			$manifest.items += [ordered]@{
				deck_id = $deckId
				endpoint = $endpoint
				status_code = 0
				file = $fileName
				url = "https://hsreplay.net/api/v1/analytics/query/$endpoint/"
				skipped = $true
				reason = "unsupported_time_range:$TimeRange"
			}
			$skippedCount++
			$completed++
			if ($ProgressEvery -gt 0 -and ($completed % $ProgressEvery -eq 0 -or $completed -eq $totalRequests)) {
				Write-PremiumProgress $completed $totalRequests $successCount $processingPollCount $noContentCount $skippedCount
			}
			continue
		}

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
				$manifest.items += [ordered]@{
					deck_id = $deckId
					endpoint = $endpoint
					status_code = 400
					file = $fileName
					url = "https://hsreplay.net/api/v1/analytics/query/$endpoint/"
					skipped = $true
					reason = $message
				}
				$http400SkippedCount++
				$skippedCount++
				$completed++
				if ($ProgressEvery -gt 0 -and ($completed % $ProgressEvery -eq 0 -or $completed -eq $totalRequests)) {
					Write-PremiumProgress $completed $totalRequests $successCount $processingPollCount $noContentCount $skippedCount
				}
				continue
			}
			throw
		}
		Assert-HSReplayAnalyticsPayload $endpoint $response.StatusCode $response.Body
		$processingPollCount += [int]$response.ProcessingPollCount
		$runPath = Join-Path $runDirectory $fileName
		Set-Content -Path $runPath -Value $response.Body -Encoding UTF8
		$manifest.items += [ordered]@{
			deck_id = $deckId
			endpoint = $endpoint
			status_code = $response.StatusCode
			file = $fileName
			url = $response.Url
		}
		if ($response.StatusCode -eq 204) {
			$noContentCount++
		} else {
			$successCount++
		}

		$completed++
		if ($RequestDelayMs -gt 0) {
			Start-Sleep -Milliseconds $RequestDelayMs
		}
		if ($ProgressEvery -gt 0 -and ($completed % $ProgressEvery -eq 0 -or $completed -eq $totalRequests)) {
			Write-PremiumProgress $completed $totalRequests $successCount $processingPollCount $noContentCount $skippedCount
		}
		if ($ProbeOnly) {
			break
		}
	}
	if ($ProbeOnly) {
		break
	}
}

$manifest["summary"] = [ordered]@{
	success = $successCount
	processing_polls = $processingPollCount
	no_content = $noContentCount
	skipped = $skippedCount
}
$manifestJson = $manifest | ConvertTo-Json -Depth 6
$runManifestPath = Join-Path $runDirectory "manifest.json"
Set-Content -Path $runManifestPath -Value $manifestJson -Encoding UTF8

if ($http400SkippedCount -gt 0) {
	Write-Warning "$http400SkippedCount 个 Premium 请求返回 HTTP 400 并已跳过；详情见 manifest.json。"
}

if (-not $ProbeOnly) {
	Publish-PremiumLatestAtomically `
		-RunDirectory $runDirectory `
		-LatestDirectory $latestDirectory `
		-Items @($manifest.items) `
		-RunId $runId
}

Write-PremiumProgress $completed $totalRequests $successCount $processingPollCount $noContentCount $skippedCount
Write-Host "已将 Premium 分析缓存写入 $runDirectory"
