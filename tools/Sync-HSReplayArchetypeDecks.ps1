param(
	[string]$CookiePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt",
	[string]$Cookie = "",
	[string]$SummaryPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\summary.tsv",
	[string]$OutputPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\archetype_deck_branches.tsv",
	[string]$CacheDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Premium\Branches",
	[string]$CandidateTimeRange = "LAST_7_DAYS",
	[string]$PatchVersion = "",
	[string]$RankRange = "DIAMOND_THROUGH_LEGEND",
	[string]$GameType = "RANKED_STANDARD",
	[string]$Region = "ALL",
	[string]$PlayerInitiative = "ALL",
	[int]$BranchesPerArchetype = 5,
	[int]$MinGames = 100,
	[int]$Parallelism = 6,
	[string]$UserAgent = "",
	[int]$AnalyticsTimeoutSeconds = 30,
	[int]$DeckPageTimeoutSeconds = 12,
	[int]$Retries = 2,
	[int]$ProcessingMaxPolls = 24,
	[int]$ProcessingPollDelaySeconds = 5,
	[int]$ProgressEvery = 10,
	[int]$RequestDelayMs = 50,
	[switch]$UseCachedCandidates,
	[DateTimeOffset]$MinimumAsOf = [DateTimeOffset]::MinValue,
	[switch]$SelfTest
)

$ErrorActionPreference = "Stop"

function ConvertTo-HSReplayCandidateDateTimeOffset([object]$Value) {
	if ($Value -is [DateTimeOffset]) {
		return [DateTimeOffset]$Value
	}
	if ($Value -is [DateTime]) {
		$dateTime = [DateTime]$Value
		if ($dateTime.Kind -eq [DateTimeKind]::Unspecified) {
			throw "候选响应的 as_of 缺少明确时区，不能提升为 Branches latest。"
		}
		return [DateTimeOffset]$dateTime
	}

	$text = if ($null -eq $Value) { "" } else { ([string]$Value).Trim() }
	if ([string]::IsNullOrWhiteSpace($text) -or
		$text -notmatch '(?i)(Z|[+-]\d{2}:\d{2})$') {
		throw "候选响应缺少有效且带时区的 as_of，不能提升为 Branches latest。"
	}
	$result = [DateTimeOffset]::MinValue
	if (-not [DateTimeOffset]::TryParse(
			$text,
			[Globalization.CultureInfo]::InvariantCulture,
			[Globalization.DateTimeStyles]::RoundtripKind,
			[ref]$result)) {
		throw "候选响应缺少有效且带时区的 as_of，不能提升为 Branches latest。"
	}
	return $result
}

function Assert-HSReplayCandidateAsOfNotBefore(
	[object]$Value,
	[DateTimeOffset]$Minimum
) {
	$asOf = ConvertTo-HSReplayCandidateDateTimeOffset $Value
	if ($Minimum -ne [DateTimeOffset]::MinValue -and $asOf -lt $Minimum) {
		throw "候选数据时间 $($asOf.ToString('o')) 早于当前补丁起点 $($Minimum.ToString('o'))；上游数据仍在生成，不能提升为 Branches latest。"
	}
	return $asOf
}

function Get-HSReplayBranchMinimumAsOf(
	[string]$RequestedTimeRange,
	[DateTimeOffset]$RequestedMinimum
) {
	# CURRENT_PATCH is selected by HSReplay itself. CandidateAsOf is the snapshot
	# time, not the time this PC first observed the patch.
	if ([string]::Equals(
			$RequestedTimeRange,
			"CURRENT_PATCH",
			[StringComparison]::OrdinalIgnoreCase)) {
		return [DateTimeOffset]::MinValue
	}
	return $RequestedMinimum
}

function Get-HSReplayPublicPatchVersion([string]$Value) {
	if ([string]::IsNullOrWhiteSpace($Value)) {
		return ""
	}
	$match = [regex]::Match($Value, "\b(\d+\.\d+\.\d+)(?:\.\d+)?\b")
	if ($match.Success) {
		return $match.Groups[1].Value
	}
	return $Value.Trim()
}

function Resolve-HSReplayBranchPatchVersion(
	[string]$RequestedPatchVersion,
	[string]$BranchOutputPath
) {
	if (-not [string]::IsNullOrWhiteSpace($RequestedPatchVersion)) {
		return (Get-HSReplayPublicPatchVersion $RequestedPatchVersion)
	}
	$dataDirectory = Split-Path -Parent $BranchOutputPath
	$versionPath = Join-Path $dataDirectory "patch_version.txt"
	if (Test-Path -LiteralPath $versionPath -PathType Leaf) {
		return (Get-HSReplayPublicPatchVersion (
			(Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8).Trim()))
	}
	return ""
}

function Invoke-HSReplayBranchFreshnessSelfTest {
	$minimum = [DateTimeOffset]::Parse("2026-08-05T20:18:25+08:00")
	if ((Get-HSReplayBranchMinimumAsOf "CURRENT_PATCH" $minimum) -ne
		[DateTimeOffset]::MinValue) {
		throw "Branch freshness self-test failed: CURRENT_PATCH kept the local marker gate."
	}
	if ((Get-HSReplayBranchMinimumAsOf "LAST_7_DAYS" $minimum) -ne $minimum) {
		throw "Branch freshness self-test failed: rolling range lost the local marker gate."
	}
	[void](Assert-HSReplayCandidateAsOfNotBefore "2026-08-05T12:18:26Z" $minimum)
	[void](Assert-HSReplayCandidateAsOfNotBefore "2026-08-05T12:18:25Z" $minimum)

	$staleRejected = $false
	try {
		[void](Assert-HSReplayCandidateAsOfNotBefore "2026-08-05T12:13:40Z" $minimum)
	} catch {
		$staleRejected = $_.Exception.Message -match "上游数据仍在生成"
	}
	if (-not $staleRejected) {
		throw "Branch freshness self-test failed: stale as_of was accepted."
	}

	$invalidRejected = $false
	try {
		[void](Assert-HSReplayCandidateAsOfNotBefore "not-a-date" $minimum)
	} catch {
		$invalidRejected = $_.Exception.Message -match "缺少有效"
	}
	if (-not $invalidRejected) {
		throw "Branch freshness self-test failed: invalid as_of was accepted."
	}

	Write-Host "Branch freshness self-test passed"
}

if ($SelfTest) {
	Invoke-HSReplayBranchFreshnessSelfTest
	return
}

$effectiveMinimumAsOf = Get-HSReplayBranchMinimumAsOf $CandidateTimeRange $MinimumAsOf
$effectivePatchVersion = Resolve-HSReplayBranchPatchVersion $PatchVersion $OutputPath

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
if (-not (Test-Path $SummaryPath)) {
	throw "未找到 Meta 摘要：$SummaryPath。请先运行 Sync-HSReplayMetaData.ps1。"
}
if ($BranchesPerArchetype -lt 2) {
	throw "BranchesPerArchetype 至少为 2，才能同时保留最高胜率和最高使用量牌组。"
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
	if ($StatusCode -ne 200) {
		return $false
	}
	return (-not [string]::IsNullOrWhiteSpace($Body) -and
		$Body -match '(?i)query\s+is\s+processing|check\s+back\s+later')
}

function Assert-HSReplayAnalyticsPayload([string]$Name, [int]$StatusCode, [string]$Body) {
	if ($StatusCode -ne 200) {
		throw "HSReplay 为 $Name 返回了未预期的成功状态 HTTP $StatusCode；只接受完整的 HTTP 200 JSON。"
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
		throw "HSReplay 查询 $Name 仍在处理中，不能写入 Branches latest 缓存。"
	}
	if (-not ($payload.PSObject.Properties.Name -contains "series")) {
		throw "HSReplay 为 $Name 返回的 JSON 不包含完整的 series[0].data。"
	}

	$series = @($payload.series)
	if ($series.Count -eq 0 -or $null -eq $series[0] -or
		-not ($series[0].PSObject.Properties.Name -contains "data") -or
		$null -eq $series[0].data) {
		throw "HSReplay 为 $Name 返回的 JSON 不包含完整的 series[0].data。"
	}
}

function Invoke-HSReplayJson([string]$Url, [string]$Name, [object[]]$CookieArgs) {
	$processingPollCount = 0
	while ($true) {
		$pollAgain = $false
		for ($attempt = 1; $attempt -le $Retries; $attempt++) {
			$bodyPath = [System.IO.Path]::GetTempFileName()
			try {
				$statusText = & curl.exe -s -L -A $effectiveUserAgent -H "Accept: application/json" @CookieArgs `
					--connect-timeout 10 --max-time $AnalyticsTimeoutSeconds -w "%{http_code}" -o $bodyPath $Url 2>$null
				$curlExitCode = $LASTEXITCODE
				$statusText = (@($statusText) -join "").Trim()
				$body = if (Test-Path $bodyPath) {
					Get-Content -Path $bodyPath -Encoding UTF8 -Raw
				} else {
					""
				}
			} finally {
				Remove-Item -LiteralPath $bodyPath -ErrorAction SilentlyContinue
			}

			if ($curlExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($statusText)) {
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
				Write-Host "HSReplay 查询仍在处理中：$Name；等待后进行处理中轮询第 $processingPollCount/$ProcessingMaxPolls 次。"
				if ($ProcessingPollDelaySeconds -gt 0) {
					Start-Sleep -Seconds $ProcessingPollDelaySeconds
				}
				$pollAgain = $true
				break
			}
			if ($statusCode -eq 200) {
				Assert-HSReplayAnalyticsPayload $Name $statusCode $body
				return @{
					Url = $Url
					StatusCode = $statusCode
					Body = $body
					ProcessingPollCount = $processingPollCount
				}
			}
			if ($statusCode -ge 200 -and $statusCode -lt 300) {
				throw "HSReplay 为 $Name 返回了未预期的成功状态 HTTP $statusCode；只接受完整的 HTTP 200 JSON。"
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
		throw "HSReplay 查询 $Name 未返回可用结果。"
	}
}

function Publish-BranchesLatestAtomically(
	[string]$RunDirectory,
	[string]$LatestDirectory,
	[string[]]$FileNames,
	[string]$RunOutputPath,
	[string]$OutputPath,
	[string]$RunId,
	[DateTimeOffset]$MinimumAsOf = [DateTimeOffset]::MinValue
) {
	$latestParent = Split-Path -Parent $LatestDirectory
	$outputDirectory = Split-Path -Parent $OutputPath
	$outputFileName = Split-Path -Leaf $OutputPath
	$publishToken = "$RunId-$([Guid]::NewGuid().ToString('N'))"
	$stagingDirectory = Join-Path $latestParent ".latest-$publishToken.staging"
	$backupDirectory = Join-Path $latestParent ".latest-$publishToken.backup"
	$outputStagingPath = Join-Path $outputDirectory ".$outputFileName.$publishToken.staging"
	$outputBackupPath = Join-Path $outputDirectory ".$outputFileName.$publishToken.backup"
	$latestBackedUp = $false
	$outputBackedUp = $false
	$latestPublished = $false
	$outputPublished = $false
	$publishSucceeded = $false
	New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null

	try {
		foreach ($fileName in $FileNames) {
			$sourcePath = Join-Path $RunDirectory $fileName
			if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
				throw "分支运行产物不完整，缺少：$sourcePath"
			}
			Copy-Item `
				-LiteralPath $sourcePath `
				-Destination (Join-Path $stagingDirectory $fileName) `
				-Force
		}
		if (-not (Test-Path -LiteralPath $RunOutputPath -PathType Leaf)) {
			throw "分支运行产物不完整，缺少：$RunOutputPath"
		}
		Copy-Item -LiteralPath $RunOutputPath -Destination $outputStagingPath -Force

		$stagedManifestPath = Join-Path $stagingDirectory "manifest.json"
		try {
			$stagedManifest = Get-Content -LiteralPath $stagedManifestPath -Encoding UTF8 -Raw |
				ConvertFrom-Json -ErrorAction Stop
		} catch {
			throw "分支 manifest.json 无效，不能提升为 Branches latest：$($_.Exception.Message)"
		}
		$stagedOutputHash = (Get-FileHash -LiteralPath $outputStagingPath -Algorithm SHA256).Hash
		$outputHeader = @(Get-Content -LiteralPath $outputStagingPath -Encoding UTF8 -TotalCount 12)
		if ($null -eq $stagedManifest.candidate -or
			[string]::IsNullOrWhiteSpace([string]$stagedManifest.candidate.file)) {
			throw "分支 manifest.json 不完整，不能提升为 Branches latest。"
		}
		$stagedCandidatePath = Join-Path $stagingDirectory ([string]$stagedManifest.candidate.file)
		try {
			$stagedCandidateData = Get-Content -LiteralPath $stagedCandidatePath -Encoding UTF8 -Raw |
				ConvertFrom-Json -ErrorAction Stop
			$stagedManifestCandidateAsOf = ConvertTo-HSReplayCandidateDateTimeOffset `
				$stagedManifest.candidate.as_of
			$stagedPayloadCandidateAsOf = ConvertTo-HSReplayCandidateDateTimeOffset `
				$stagedCandidateData.as_of
		} catch {
			throw "分支候选 JSON 无效，不能提升为 Branches latest：$($_.Exception.Message)"
		}
		if ([string]$stagedManifest.run_id -ne $RunId -or
			$outputHeader -notcontains "# RunId: $RunId" -or
			$outputHeader -notcontains "# PatchVersion: $([string]$stagedManifest.patch_version)" -or
			[string]$stagedManifest.output_sha256 -ne $stagedOutputHash -or
			$null -eq $stagedManifest.written_branch_count -or
			[int]$stagedManifest.written_branch_count -le 0 -or
			-not (Test-Path -LiteralPath $stagedCandidatePath -PathType Leaf) -or
			[string]$stagedManifest.candidate.sha256 -ne
				(Get-FileHash -LiteralPath $stagedCandidatePath -Algorithm SHA256).Hash -or
			$stagedManifestCandidateAsOf.UtcDateTime -ne
				$stagedPayloadCandidateAsOf.UtcDateTime) {
			throw "分支 manifest.json 不完整，不能提升为 Branches latest。"
		}
		[void](Assert-HSReplayCandidateAsOfNotBefore `
			$stagedManifestCandidateAsOf `
			$MinimumAsOf)

		$completion = [ordered]@{
			run_id = $RunId
			completed_at = (Get-Date).ToString("o")
			manifest_sha256 = (Get-FileHash -LiteralPath $stagedManifestPath -Algorithm SHA256).Hash
			output_sha256 = $stagedOutputHash
		}
		Set-Content `
			-LiteralPath (Join-Path $stagingDirectory "publish-complete.json") `
			-Value ($completion | ConvertTo-Json -Depth 3) `
			-Encoding UTF8

		try {
			if (Test-Path -LiteralPath $LatestDirectory) {
				[System.IO.Directory]::Move($LatestDirectory, $backupDirectory)
				$latestBackedUp = $true
			}
			if (Test-Path -LiteralPath $OutputPath) {
				[System.IO.File]::Move($OutputPath, $outputBackupPath)
				$outputBackedUp = $true
			}
			[System.IO.Directory]::Move($stagingDirectory, $LatestDirectory)
			$latestPublished = $true
			[System.IO.File]::Move($outputStagingPath, $OutputPath)
			$outputPublished = $true
			$publishSucceeded = $true
		} catch {
			if ($outputPublished -and (Test-Path -LiteralPath $OutputPath)) {
				Remove-Item -LiteralPath $OutputPath -Force
			}
			if ($outputBackedUp -and -not (Test-Path -LiteralPath $OutputPath) -and
				(Test-Path -LiteralPath $outputBackupPath)) {
				[System.IO.File]::Move($outputBackupPath, $OutputPath)
			}
			if ($latestPublished -and (Test-Path -LiteralPath $LatestDirectory)) {
				Remove-Item -LiteralPath $LatestDirectory -Recurse -Force
			}
			if ($latestBackedUp -and -not (Test-Path -LiteralPath $LatestDirectory) -and
				(Test-Path -LiteralPath $backupDirectory)) {
				[System.IO.Directory]::Move($backupDirectory, $LatestDirectory)
			}
			throw
		}
	} finally {
		if (Test-Path -LiteralPath $stagingDirectory) {
			Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
		}
		Remove-Item -LiteralPath $outputStagingPath -Force -ErrorAction SilentlyContinue
		if ($publishSucceeded) {
			Remove-Item -LiteralPath $backupDirectory -Recurse -Force -ErrorAction SilentlyContinue
			Remove-Item -LiteralPath $outputBackupPath -Force -ErrorAction SilentlyContinue
		}
	}
}

function Convert-DeckPageToInfo([string]$DeckId, [string]$Html) {
	$decoded = [System.Net.WebUtility]::HtmlDecode([string]$Html)
	$deckStringMatch = [regex]::Match($decoded,
		'<meta[^>]+property=["'']x-hearthstone:deck:deckstring["''][^>]+content=["''](?<deck>AAE[A-Za-z0-9+/=]{20,})["'']',
		[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	if (-not $deckStringMatch.Success) {
		$deckStringMatch = [regex]::Match($decoded,
			'<meta[^>]+content=["''](?<deck>AAE[A-Za-z0-9+/=]{20,})["''][^>]+property=["'']x-hearthstone:deck:deckstring["'']',
			[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
	}
	if (-not $deckStringMatch.Success) {
		$deckStringMatch = [regex]::Match($decoded, "Import it:\s*(?<deck>AAE[A-Za-z0-9+/=]{20,})")
	}
	if (-not $deckStringMatch.Success) {
		if ($decoded -match "(?i)challenges\.cloudflare\.com|<title>\s*Just a moment") {
			throw "HSReplay 为牌组 $DeckId 返回了 Cloudflare 验证页面。请从已登录的浏览器会话中更新 HSReplay Cookie，然后重试。"
		}
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
	throw "摘要中没有数据行：$SummaryPath"
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
	throw "摘要中没有流派数据：$SummaryPath"
}

$runId = Get-Date -Format "yyyyMMdd-HHmmss-fff"
$runDirectory = Join-Path (Join-Path $CacheDirectory "runs") $runId
$latestDirectory = Join-Path $CacheDirectory "latest"
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
[object[]]$cookieArgs = @(Get-HSReplayCookieArgs)

$candidateFileName = "list_decks_by_win_rate_v2.json"
$candidateRunPath = Join-Path $runDirectory $candidateFileName
$candidateLatestPath = Join-Path $latestDirectory $candidateFileName

if ($UseCachedCandidates) {
	if (-not (Test-Path $candidateLatestPath)) {
		throw "未找到缓存的候选文件：$candidateLatestPath"
	}
	$candidateLatestManifestPath = Join-Path $latestDirectory "manifest.json"
	$candidateLatestCompletionPath = Join-Path $latestDirectory "publish-complete.json"
	if (-not (Test-Path -LiteralPath $candidateLatestManifestPath -PathType Leaf) -or
		-not (Test-Path -LiteralPath $candidateLatestCompletionPath -PathType Leaf)) {
		throw "缓存候选缺少完整 manifest.json / publish-complete.json，拒绝按 $CandidateTimeRange 重新标记。"
	}
	try {
		$cachedManifestJson = Get-Content -LiteralPath $candidateLatestManifestPath -Encoding UTF8 -Raw
		$cachedManifest = $cachedManifestJson | ConvertFrom-Json -ErrorAction Stop
		$cachedCompletion = Get-Content -LiteralPath $candidateLatestCompletionPath -Encoding UTF8 -Raw |
			ConvertFrom-Json -ErrorAction Stop
	} catch {
		throw "缓存候选的 manifest 无效，拒绝复用：$($_.Exception.Message)"
	}
	$candidateJson = Get-Content -Path $candidateLatestPath -Encoding UTF8 -Raw
	$candidateUrl = "cached:$candidateLatestPath"
	$candidateStatusCode = 0
	$candidateProcessingPollCount = 0
	Assert-HSReplayAnalyticsPayload "list_decks_by_win_rate_v2（缓存）" 200 $candidateJson
	$cachedCandidateData = $candidateJson | ConvertFrom-Json -ErrorAction Stop
	$cachedManifestHash = (Get-FileHash -LiteralPath $candidateLatestManifestPath -Algorithm SHA256).Hash
	$cachedCandidateHash = (Get-FileHash -LiteralPath $candidateLatestPath -Algorithm SHA256).Hash
	try {
		$cachedManifestAsOf = ConvertTo-HSReplayCandidateDateTimeOffset `
			$cachedManifest.candidate.as_of
		$cachedPayloadAsOf = ConvertTo-HSReplayCandidateDateTimeOffset `
			$cachedCandidateData.as_of
	} catch {
		throw "缓存候选的 as_of 无效，拒绝复用：$($_.Exception.Message)"
	}
	if (-not [string]::Equals(
			[string]$cachedManifest.candidate_time_range,
			$CandidateTimeRange,
			[StringComparison]::OrdinalIgnoreCase) -or
		[string]::IsNullOrWhiteSpace([string]$cachedManifest.run_id) -or
		[string]$cachedCompletion.run_id -ne [string]$cachedManifest.run_id -or
		[string]$cachedCompletion.manifest_sha256 -ne $cachedManifestHash -or
		[string]$cachedManifest.candidate.file -ne $candidateFileName -or
		[string]$cachedManifest.candidate.sha256 -ne $cachedCandidateHash -or
		$cachedManifestAsOf.UtcDateTime -ne $cachedPayloadAsOf.UtcDateTime) {
		throw "缓存候选与请求的 $CandidateTimeRange 时间范围、as_of 或完整性标记不一致，拒绝复用和重新标记。"
	}
	if ([string]::Equals(
			$CandidateTimeRange,
			"CURRENT_PATCH",
			[StringComparison]::OrdinalIgnoreCase) -and
		-not [string]::IsNullOrWhiteSpace($effectivePatchVersion) -and
		-not [string]::Equals(
			(Get-HSReplayPublicPatchVersion ([string]$cachedManifest.patch_version)),
			$effectivePatchVersion,
			[StringComparison]::OrdinalIgnoreCase)) {
		throw "缓存候选属于其他炉石补丁，拒绝作为当前补丁候选复用。"
	}
} else {
	$parameters = @{
		TimeRange = $CandidateTimeRange
		GameType = $GameType
		LeagueRankRange = $RankRange
		Region = $Region
		PlayerInitiative = $PlayerInitiative
	}
	$candidateUrl = "https://hsreplay.net/api/v1/analytics/query/list_decks_by_win_rate_v2/?$(ConvertTo-QueryString $parameters)"
	Write-Host "正在获取 HSReplay 牌组分支候选项..."
	Write-Host "参数：CandidateTimeRange=$CandidateTimeRange，GameType=$GameType，段位范围=$RankRange，地区=$Region"
	$response = Invoke-HSReplayJson $candidateUrl "list_decks_by_win_rate_v2" $cookieArgs
	$candidateJson = $response.Body
	$candidateStatusCode = $response.StatusCode
	$candidateProcessingPollCount = [int]$response.ProcessingPollCount
}

Set-Content -Path $candidateRunPath -Value $candidateJson -Encoding UTF8
$candidateRunHash = (Get-FileHash -LiteralPath $candidateRunPath -Algorithm SHA256).Hash
$candidateData = $candidateJson | ConvertFrom-Json -ErrorAction Stop
$candidateAsOf = Assert-HSReplayCandidateAsOfNotBefore `
	$candidateData.as_of `
	$effectiveMinimumAsOf
$canonicalCandidateAsOf = $candidateAsOf.ToString(
	"o",
	[Globalization.CultureInfo]::InvariantCulture)

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
	$allEligible = @($candidatesByArchetype[$targetKey] |
		Where-Object { $_.total_games -ge $MinGames })
	$eligible = New-Object System.Collections.Generic.List[object]
	if ($allEligible.Count -gt 0) {
		$mostPopular = $allEligible |
			Sort-Object @{ Expression = { $_.total_games }; Descending = $true },
				@{ Expression = { $_.win_rate }; Descending = $true } |
			Select-Object -First 1
		$highestWinRate = $allEligible |
			Sort-Object @{ Expression = { $_.win_rate }; Descending = $true },
				@{ Expression = { $_.total_games }; Descending = $true } |
			Select-Object -First 1
		$eligible.Add($mostPopular)
		if ([string]$highestWinRate.deck_id -ne [string]$mostPopular.deck_id) {
			$eligible.Add($highestWinRate)
		}
		foreach ($candidate in ($allEligible |
			Sort-Object @{ Expression = { $_.total_games }; Descending = $true },
				@{ Expression = { $_.win_rate }; Descending = $true })) {
			if ($eligible.Count -ge $BranchesPerArchetype) {
				break
			}
			if (-not ($eligible | Where-Object { [string]$_.deck_id -eq [string]$candidate.deck_id })) {
				$eligible.Add($candidate)
			}
		}
	}
	# PowerShell 5.1 can throw "Argument types do not match" when an array
	# subexpression directly unwraps List[object]. Enumerate explicitly so both
	# the scheduled Windows PowerShell host and pwsh produce a normal object[].
	$eligibleRows = @($eligible | ForEach-Object { $_ })

	if ($eligibleRows.Count -eq 0) {
		$missingArchetypes.Add([pscustomobject]@{
			archetype_id = $targetKey
			name = $target.name
			player_class = $target.player_class
			reason = "below_min_games"
		})
		continue
	}

	foreach ($candidate in $eligibleRows) {
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
	throw "未选中任何分支候选项。请尝试降低 -MinGames。"
}

Write-Host "已为 $($targets.Count) 个流派选中 $($selectedCandidates.Count) 个分支候选项。"
Write-Host "正在获取 HSReplay 牌组页面..."

$resultsByDeckId = @{}
$failedPages = New-Object System.Collections.Generic.List[object]
$uniqueDeckIds = @($selectedCandidates | Select-Object -ExpandProperty deck_id -Unique)

if ($Parallelism -le 1) {
	$checked = 0
	foreach ($deckId in $uniqueDeckIds) {
		$deckUrl = "https://hsreplay.net/decks/$deckId/"
		$body = $null
		for ($attempt = 1; $attempt -le $Retries; $attempt++) {
			$body = & curl.exe -s -L -A $effectiveUserAgent -H "Accept: text/html,*/*" @cookieArgs `
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
			Write-Host "已检查 $checked/$($uniqueDeckIds.Count)，已提取 $($resultsByDeckId.Count) 个，失败 $($failedPages.Count) 个。"
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
			throw "curl.exe 调用失败，无法读取 $Url"
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
			$job = Start-Job -ScriptBlock $jobScript -ArgumentList $deckId, $Retries, $DeckPageTimeoutSeconds, $effectiveUserAgent, (,$cookieArgs)
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
			Write-Host "已检查 $checked/$($uniqueDeckIds.Count)，已提取 $($resultsByDeckId.Count) 个，失败 $($failedPages.Count) 个。"
		}
	}
}

$outputLines = New-Object System.Collections.Generic.List[string]
$outputLines.Add("# Generated by tools/Sync-HSReplayArchetypeDecks.ps1")
$outputLines.Add("# RunId: $runId")
$outputLines.Add("# Summary: $SummaryPath")
$outputLines.Add("# CandidateSource: $candidateUrl")
$outputLines.Add("# CandidateAsOf: $canonicalCandidateAsOf")
$outputLines.Add("# CandidateTimeRange: $CandidateTimeRange")
$outputLines.Add("# PatchVersion: $effectivePatchVersion")
$outputLines.Add("# RankRange: $RankRange")
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
	throw "未能从选中的分支候选项中提取任何 deckstring。"
}

$runOutputFileName = "archetype_deck_branches.tsv"
$runOutputPath = Join-Path $runDirectory $runOutputFileName
Set-Content -Path $runOutputPath -Value $outputLines -Encoding UTF8
$runOutputHash = (Get-FileHash -LiteralPath $runOutputPath -Algorithm SHA256).Hash

$manifest = [ordered]@{
	run_id = $runId
	generated_at = (Get-Date).ToString("o")
	minimum_as_of = if ($effectiveMinimumAsOf -eq [DateTimeOffset]::MinValue) { "" } else { $effectiveMinimumAsOf.ToString("o") }
	summary_path = $SummaryPath
	output_path = $OutputPath
	output_file = $runOutputFileName
	output_sha256 = $runOutputHash
	candidate_time_range = $CandidateTimeRange
	patch_version = $effectivePatchVersion
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
		processing_polls = $candidateProcessingPollCount
		url = $candidateUrl
		file = $candidateFileName
		sha256 = $candidateRunHash
		as_of = $canonicalCandidateAsOf
	}
}
$manifestJson = $manifest | ConvertTo-Json -Depth 8
Set-Content -Path (Join-Path $runDirectory "manifest.json") -Value $manifestJson -Encoding UTF8
Publish-BranchesLatestAtomically `
	-RunDirectory $runDirectory `
	-LatestDirectory $latestDirectory `
	-FileNames @($candidateFileName, "manifest.json") `
	-RunOutputPath $runOutputPath `
	-OutputPath $OutputPath `
	-RunId $runId `
	-MinimumAsOf $effectiveMinimumAsOf

Write-Host "已将 $written 个流派牌组分支写入 $OutputPath"
if ($missingArchetypes.Count -gt 0) {
	Write-Warning "$($missingArchetypes.Count) 个流派没有符合条件的分支候选项。请查看 manifest.json。"
}
if ($failedPages.Count -gt 0) {
	Write-Warning "$($failedPages.Count) 个牌组页面获取失败或未提供 deckstring。请查看 manifest.json。"
}
