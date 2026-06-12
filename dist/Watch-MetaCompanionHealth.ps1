param(
	[int]$IntervalSeconds = 8,
	[int]$MaxIterations = 0,
	[switch]$Once,
	[string]$DataRoot = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion",
	[string]$HdtRoot = "$env:APPDATA\HearthstoneDeckTracker",
	[string]$ExpectedDllHash = "6C5FC6B0CACF150FC69CF1D7EFFD8CAF0A9E992136982345CDE30381509FE48F",
	[int]$PredictionStaleMinutes = 5,
	[int]$MaxPredictedCards = 24,
	[int]$MaxPossibleCardsAfterEvidence = 35
)

$ErrorActionPreference = "Stop"

if ($Once) {
	$MaxIterations = 1
}

$logDir = Join-Path $DataRoot "Logs"
$monitorLogPath = Join-Path $logDir "health-monitor.log"
$statePath = Join-Path $logDir "health-monitor-state.json"
$anomalyPath = Join-Path $DataRoot "anomalies.tsv"

function Ensure-Directory([string]$Path) {
	if (-not (Test-Path -LiteralPath $Path)) {
		New-Item -ItemType Directory -Path $Path -Force | Out-Null
	}
}

function Write-Monitor([string]$Message) {
	Ensure-Directory $logDir
	$line = "{0}`t{1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
	Add-Content -LiteralPath $monitorLogPath -Value $line -Encoding UTF8
	if (-not $Once) {
		Write-Host $line
	}
}

function New-State() {
	return @{
		Offsets = @{}
		SeenKeys = @()
		LastProcessState = @{}
		LastTimelineMatchId = ""
		LastPredictionSignature = ""
	}
}

function Read-State() {
	if (-not (Test-Path -LiteralPath $statePath)) {
		return New-State
	}

	try {
		$json = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8
		if ([string]::IsNullOrWhiteSpace($json)) {
			return New-State
		}

		$source = $json | ConvertFrom-Json
		$state = New-State
		if ($source.Offsets) {
			foreach ($property in $source.Offsets.PSObject.Properties) {
				$state.Offsets[$property.Name] = [int64]$property.Value
			}
		}
		if ($source.SeenKeys) {
			$state.SeenKeys = @($source.SeenKeys)
		}
		if ($source.LastProcessState) {
			foreach ($property in $source.LastProcessState.PSObject.Properties) {
				$state.LastProcessState[$property.Name] = [string]$property.Value
			}
		}
		if ($source.LastTimelineMatchId) {
			$state.LastTimelineMatchId = [string]$source.LastTimelineMatchId
		}
		if ($source.LastPredictionSignature) {
			$state.LastPredictionSignature = [string]$source.LastPredictionSignature
		}
		return $state
	} catch {
		Write-Monitor "State file was unreadable; starting a fresh monitor state. $($_.Exception.Message)"
		return New-State
	}
}

function Save-State($State) {
	Ensure-Directory $logDir
	$State.SeenKeys = @($State.SeenKeys | Select-Object -Last 500)
	$State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Ensure-AnomalyHeader() {
	Ensure-Directory $DataRoot
	if (-not (Test-Path -LiteralPath $anomalyPath)) {
		"timestamp`tseverity`tsource`tcode`tmessage`tdetails" | Set-Content -LiteralPath $anomalyPath -Encoding UTF8
	}
}

function Sanitize-Field([string]$Value) {
	if ($null -eq $Value) {
		return ""
	}
	return ($Value -replace "`r|`n|`t", " ").Trim()
}

function Add-Anomaly($State, [string]$Severity, [string]$Source, [string]$Code, [string]$Message, [string]$Details) {
	Ensure-AnomalyHeader
	$keySource = "{0}|{1}|{2}|{3}" -f $Severity, $Source, $Code, $Details
	$key = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($keySource))
	if ($State.SeenKeys -contains $key) {
		return
	}

	$State.SeenKeys += $key
	$line = "{0}`t{1}`t{2}`t{3}`t{4}`t{5}" -f `
		(Get-Date -Format "yyyy-MM-dd HH:mm:ss"), `
		(Sanitize-Field $Severity), `
		(Sanitize-Field $Source), `
		(Sanitize-Field $Code), `
		(Sanitize-Field $Message), `
		(Sanitize-Field $Details)
	Add-Content -LiteralPath $anomalyPath -Value $line -Encoding UTF8
	Write-Monitor "ANOMALY [$Severity][$Source][$Code] $Message :: $Details"
}

function Get-NewUtf8Lines($State, [string]$Key, [string]$Path) {
	if (-not (Test-Path -LiteralPath $Path)) {
		return @()
	}

	$item = Get-Item -LiteralPath $Path
	$length = [int64]$item.Length
	if (-not $State.Offsets.ContainsKey($Key)) {
		$State.Offsets[$Key] = $length
		return @()
	}

	$offset = [int64]$State.Offsets[$Key]
	if ($offset -gt $length) {
		$offset = 0
	}
	if ($length -le $offset) {
		$State.Offsets[$Key] = $length
		return @()
	}

	$stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
	try {
		[void]$stream.Seek($offset, [System.IO.SeekOrigin]::Begin)
		$buffer = New-Object byte[] ($length - $offset)
		[void]$stream.Read($buffer, 0, $buffer.Length)
	} finally {
		$stream.Close()
	}

	$State.Offsets[$Key] = $length
	$text = [System.Text.Encoding]::UTF8.GetString($buffer)
	return @($text -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-ProcessSummary([string]$Name) {
	$items = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
	if ($items.Count -eq 0) {
		return "missing"
	}
	return ($items | Sort-Object StartTime -ErrorAction SilentlyContinue | ForEach-Object {
		"{0}:{1}" -f $_.Id, ($_.StartTime.ToString("yyyy-MM-dd HH:mm:ss"))
	}) -join ","
}

function Check-Processes($State) {
	foreach ($name in @("HearthstoneDeckTracker", "Hearthstone")) {
		$status = Get-ProcessSummary $name
		$previous = if ($State.LastProcessState.ContainsKey($name)) { $State.LastProcessState[$name] } else { "" }
		if ($status -ne $previous) {
			Write-Monitor "Process $name => $status"
			$State.LastProcessState[$name] = $status
			if ($status -eq "missing" -and $name -eq "HearthstoneDeckTracker") {
				Add-Anomaly $State "info" "process" "HDT_NOT_RUNNING" "Hearthstone Deck Tracker is not running" $name
			} elseif ($status -eq "missing" -and $name -eq "Hearthstone") {
				Add-Anomaly $State "info" "process" "HEARTHSTONE_NOT_RUNNING" "Hearthstone is not running" $name
			}
		}
	}
}

function Get-ActivePluginAssemblyPath() {
	$pluginLog = Join-Path $logDir "log.txt"
	if (-not (Test-Path -LiteralPath $pluginLog)) {
		return $null
	}

	$lines = Get-Content -LiteralPath $pluginLog -Tail 80 -Encoding UTF8
	$match = $null
	foreach ($line in $lines) {
		if ($line -match "Plugin assembly:\s*(.+MetaCompanion\.dll)\s*$") {
			$match = $Matches[1]
		}
	}
	return $match
}

function Get-DllPathsToCheck() {
	$paths = @()
	$active = Get-ActivePluginAssemblyPath
	if ($active) {
		$paths += $active
	}

	$roamingPath = Join-Path $HdtRoot "Plugins\MetaCompanion\MetaCompanion.dll"
	if (Test-Path -LiteralPath $roamingPath) {
		$paths += $roamingPath
	}

	$localRoot = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
	if (Test-Path -LiteralPath $localRoot) {
		$latestApp = Get-ChildItem -LiteralPath $localRoot -Directory -Filter "app-*" -ErrorAction SilentlyContinue |
			Sort-Object LastWriteTime -Descending |
			Select-Object -First 1
		if ($latestApp) {
			$paths += (Join-Path $latestApp.FullName "Plugins\MetaCompanion\MetaCompanion.dll")
		}
	}

	return @($paths | Where-Object { $_ } | Select-Object -Unique)
}

function Check-DllHash($State) {
	if ([string]::IsNullOrWhiteSpace($ExpectedDllHash)) {
		return
	}
	$expectedHashes = @($ExpectedDllHash -split "[,;\s]+" |
		ForEach-Object { $_.Trim().ToUpperInvariant() } |
		Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
	if ($expectedHashes.Count -eq 0) {
		return
	}

	$paths = @(Get-DllPathsToCheck)
	if ($paths.Count -eq 0) {
		Add-Anomaly $State "critical" "dll" "DLL_NOT_FOUND" "No Meta Companion DLL path could be found" "Expected SHA256=$ExpectedDllHash"
		return
	}

	foreach ($path in $paths) {
		if (-not (Test-Path -LiteralPath $path)) {
			Add-Anomaly $State "warning" "dll" "DLL_PATH_MISSING" "Expected Meta Companion DLL path is missing" $path
			continue
		}

		$hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
		if ($expectedHashes -notcontains $hash.ToUpperInvariant()) {
			Add-Anomaly $State "critical" "dll" "DLL_HASH_MISMATCH" "Installed Meta Companion DLL hash differs from the verified build" "$path hash=$hash expected=$ExpectedDllHash"
		}
	}
}

function Check-PluginLog($State) {
	$pluginLog = Join-Path $logDir "log.txt"
	$lines = Get-NewUtf8Lines $State "plugin-log" $pluginLog
	foreach ($line in $lines) {
		if ($line -match "(?i)\b(error|exception|failed|unable|invalidoperationexception|argumentoutofrangeexception)\b") {
			Add-Anomaly $State "warning" "plugin-log" "PLUGIN_LOG_ERROR" "Meta Companion log reported a possible error" $line
		} elseif ($line -match "(?i)Unknown hero") {
			Add-Anomaly $State "warning" "plugin-log" "UNKNOWN_HERO" "Meta Companion could not map an opponent hero" $line
		}
	}
}

function Check-HdtLog($State) {
	$hdtLog = Join-Path $HdtRoot "Logs\hdt_log.txt"
	$lines = Get-NewUtf8Lines $State "hdt-log" $hdtLog
	foreach ($line in $lines) {
		$isPluginRelated = $line -match "(?i)(MetaCompanion|AnimatedCardList|PredictionController|PredictionEngine)"
		$isError = $line -match "(?i)(\|Error\||Exception|ArgumentOutOfRangeException|InvalidOperationException)"
		if ($isPluginRelated -and $isError) {
			Add-Anomaly $State "critical" "hdt-log" "HDT_PLUGIN_ERROR" "HDT reported a plugin/overlay related error" $line
		} elseif ($isError -and $line -notmatch "(?i)(HearthMirror|MirrorRPC|Helper\.NormalizeImage)") {
			Add-Anomaly $State "info" "hdt-log" "HDT_GENERAL_ERROR" "HDT reported a general error" $line
		}
	}
}

function Read-Utf8File([string]$Path) {
	$stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
	try {
		$reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
		return $reader.ReadToEnd()
	} finally {
		if ($reader) {
			$reader.Close()
		} else {
			$stream.Close()
		}
	}
}

function Get-FirstInt([string]$Text, [string]$Pattern) {
	if ($Text -match $Pattern) {
		return [int]$Matches[1]
	}
	return $null
}

function Test-LatestPredictionMatchOpen() {
	$timelinePath = Join-Path $DataRoot "prediction_timeline.tsv"
	if (-not (Test-Path -LiteralPath $timelinePath)) {
		return $false
	}

	$timelineRows = Import-TsvIfExists $timelinePath
	if ($timelineRows.Count -eq 0) {
		return $false
	}

	$latestMatchId = $timelineRows[-1].match_id
	if ([string]::IsNullOrWhiteSpace($latestMatchId)) {
		return $false
	}

	$historyPath = Join-Path $DataRoot "match_history.tsv"
	$historyRows = Import-TsvIfExists $historyPath
	$completed = @($historyRows | Where-Object {
		$_.match_id -eq $latestMatchId -and
		-not [string]::IsNullOrWhiteSpace($_.ended_at) -and
		-not [string]::IsNullOrWhiteSpace($_.end_reason)
	})
	return ($completed.Count -eq 0)
}

function Check-PredictionSnapshot($State) {
	$predictionPath = Join-Path $logDir "prediction.txt"
	if (-not (Test-Path -LiteralPath $predictionPath)) {
		return
	}

	$item = Get-Item -LiteralPath $predictionPath
	$ageMinutes = ((Get-Date) - $item.LastWriteTime).TotalMinutes
	if ($ageMinutes -ge $PredictionStaleMinutes -and (Get-Process -Name Hearthstone -ErrorAction SilentlyContinue) -and (Test-LatestPredictionMatchOpen)) {
		Add-Anomaly $State "warning" "prediction" "PREDICTION_STALE" "Prediction snapshot has not been updated recently" ("ageMinutes={0:N1} path={1}" -f $ageMinutes, $predictionPath)
	}

	$text = Read-Utf8File $predictionPath
	$possibleDecks = Get-FirstInt $text "(\d+)\s+possible decks"
	$possibleCards = Get-FirstInt $text "(\d+)\s+possible cards"
	$evidenceCards = Get-FirstInt $text "(\d+)\s+evidence cards"
	$remainingCards = Get-FirstInt $text "Remaining deck cards:\s*(-?\d+)"
	$predictedCards = Get-FirstInt $text "(\d+)\s+predicted cards:"
	$closestDeck = ""
	if ($text -match "Closest deck:\s*(.+)") {
		$closestDeck = $Matches[1].Trim()
	}

	if ($remainingCards -ne $null -and ($remainingCards -lt 0 -or $remainingCards -gt 30)) {
		Add-Anomaly $State "critical" "prediction" "REMAINING_DECK_OUT_OF_RANGE" "Remaining deck card count is outside the expected range" "remaining=$remainingCards"
	}
	if ($predictedCards -ne $null -and $evidenceCards -ne $null -and $evidenceCards -ge 4 -and $predictedCards -gt $MaxPredictedCards) {
		Add-Anomaly $State "warning" "prediction" "PREDICTED_LIST_TOO_WIDE" "Predicted card list is still too wide after several evidence cards" "predicted=$predictedCards evidence=$evidenceCards closest=$closestDeck"
	}
	if ($possibleCards -ne $null -and $evidenceCards -ne $null -and $evidenceCards -ge 3 -and $possibleCards -gt $MaxPossibleCardsAfterEvidence) {
		Add-Anomaly $State "warning" "prediction" "POSSIBLE_CARD_POOL_TOO_WIDE" "Possible card pool stayed broad after evidence cards" "possible=$possibleCards evidence=$evidenceCards closest=$closestDeck"
	}
	if ($possibleDecks -ne $null -and $evidenceCards -ne $null -and $evidenceCards -ge 3 -and $possibleDecks -gt 6) {
		Add-Anomaly $State "warning" "prediction" "POSSIBLE_DECK_POOL_TOO_WIDE" "Possible deck pool stayed broad after evidence cards" "possibleDecks=$possibleDecks evidence=$evidenceCards closest=$closestDeck"
	}

	$signature = "decks=$possibleDecks cards=$possibleCards evidence=$evidenceCards remaining=$remainingCards predicted=$predictedCards closest=$closestDeck"
	if ($signature -ne $State.LastPredictionSignature) {
		Write-Monitor "Prediction snapshot => $signature"
		$State.LastPredictionSignature = $signature
	}
}

function Import-TsvIfExists([string]$Path) {
	if (-not (Test-Path -LiteralPath $Path)) {
		return @()
	}
	return @(Import-Csv -LiteralPath $Path -Delimiter "`t" -Encoding UTF8)
}

function Check-Timeline($State) {
	$timelinePath = Join-Path $DataRoot "prediction_timeline.tsv"
	$rows = Import-TsvIfExists $timelinePath
	if ($rows.Count -eq 0) {
		return
	}

	$latest = $rows[-1]
	if ($latest.match_id -and $latest.match_id -ne $State.LastTimelineMatchId) {
		Write-Monitor "Current prediction match => $($latest.match_id) $($latest.opponent_class) $($latest.predicted_archetype)"
		$State.LastTimelineMatchId = $latest.match_id
	}

	$latestGroup = @($rows | Where-Object { $_.match_id -eq $latest.match_id })
	$previousEvidence = -1
	foreach ($row in $latestGroup) {
		$value = 0
		if ([int]::TryParse([string]$row.evidence_cards, [ref]$value)) {
			if ($previousEvidence -gt $value) {
				Add-Anomaly $State "warning" "timeline" "EVIDENCE_COUNT_DECREASED" "Evidence card count decreased within the same match" "match=$($row.match_id) previous=$previousEvidence current=$value"
			}
			$previousEvidence = $value
		}
	}

	$remaining = 0
	if ([int]::TryParse([string]$latest.remaining_deck_cards, [ref]$remaining)) {
		if ($remaining -lt 0 -or $remaining -gt 30) {
			Add-Anomaly $State "critical" "timeline" "TIMELINE_REMAINING_OUT_OF_RANGE" "Timeline remaining deck count is outside the expected range" "match=$($latest.match_id) remaining=$remaining"
		}
	}
}

function Check-MatchHistory($State) {
	$historyPath = Join-Path $DataRoot "match_history.tsv"
	$rows = Import-TsvIfExists $historyPath
	if ($rows.Count -eq 0) {
		return
	}

	$duplicates = @($rows | Group-Object match_id | Where-Object { $_.Name -and $_.Count -gt 1 })
	foreach ($duplicate in $duplicates) {
		Add-Anomaly $State "warning" "match-history" "DUPLICATE_MATCH_ID" "Match history contains duplicate rows for a single match" "match=$($duplicate.Name) count=$($duplicate.Count)"
	}

	foreach ($row in $rows | Select-Object -Last 10) {
		if ($row.end_reason -eq "replaced_by_new_game") {
			Add-Anomaly $State "warning" "match-history" "MATCH_REPLACED_BY_NEW_GAME" "A match was closed because a new game started before normal game_end" "match=$($row.match_id) class=$($row.opponent_class) archetype=$($row.predicted_archetype)"
		}
	}
}

function Check-LocalMetaFreshness($State) {
	$deckStatsPath = Join-Path $HdtRoot "DeckStats.xml"
	$summaryPath = Join-Path $DataRoot "local_meta_summary.json"
	if (-not (Test-Path -LiteralPath $deckStatsPath) -or -not (Test-Path -LiteralPath $summaryPath)) {
		return
	}

	$deckStats = Get-Item -LiteralPath $deckStatsPath
	$summary = Get-Item -LiteralPath $summaryPath
	$lagMinutes = ($deckStats.LastWriteTime - $summary.LastWriteTime).TotalMinutes
	if ($lagMinutes -gt 5) {
		Add-Anomaly $State "warning" "local-meta" "LOCAL_META_STALE" "HDT DeckStats is newer than Meta Companion local meta summary" ("lagMinutes={0:N1} deckStats={1} summary={2}" -f $lagMinutes, $deckStats.LastWriteTime, $summary.LastWriteTime)
	}

	try {
		$parsed = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
		if (-not $parsed.environment -or $parsed.environment.Count -eq 0) {
			Add-Anomaly $State "warning" "local-meta" "LOCAL_META_EMPTY" "Local meta summary has no environment rows" $summaryPath
		}
	} catch {
		Add-Anomaly $State "critical" "local-meta" "LOCAL_META_INVALID_JSON" "Local meta summary JSON could not be parsed" $_.Exception.Message
	}
}

function Check-PostGameRefreshLogs($State) {
	if (-not (Test-Path -LiteralPath $logDir)) {
		return
	}

	$logs = @(Get-ChildItem -LiteralPath $logDir -Filter "post-game-refresh-*.log" -ErrorAction SilentlyContinue)
	foreach ($log in $logs) {
		$key = "post-game-refresh:$($log.FullName)"
		$lines = Get-NewUtf8Lines $State $key $log.FullName
		foreach ($line in $lines) {
			if ($line -match "(?i)(error|exception|failed|unable|throw)") {
				Add-Anomaly $State "warning" "post-game-refresh" "POST_GAME_REFRESH_ERROR" "Post-game refresh log contains an error" "$($log.Name): $line"
			} elseif ($line -match "(?i)(complete|success|wrote|recommendation)") {
				Write-Monitor "Post-game refresh: $($log.Name): $line"
			}
		}
	}
}

function Invoke-HealthCheck($State) {
	Check-Processes $State
	Check-DllHash $State
	Check-PluginLog $State
	Check-HdtLog $State
	Check-PredictionSnapshot $State
	Check-Timeline $State
	Check-MatchHistory $State
	Check-LocalMetaFreshness $State
	Check-PostGameRefreshLogs $State
}

Ensure-Directory $logDir
Ensure-AnomalyHeader
Write-Monitor "Meta Companion health monitor started. interval=${IntervalSeconds}s dataRoot=$DataRoot"

$state = Read-State
$iteration = 0
while ($true) {
	$iteration++
	try {
		Invoke-HealthCheck $state
		Save-State $state
	} catch {
		Add-Anomaly $state "critical" "monitor" "MONITOR_LOOP_ERROR" "Health monitor loop failed" $_.Exception.Message
		Save-State $state
	}

	if ($MaxIterations -gt 0 -and $iteration -ge $MaxIterations) {
		break
	}

	Start-Sleep -Seconds $IntervalSeconds
}

Write-Monitor "Meta Companion health monitor stopped after $iteration iteration(s)."

