param(
	[string]$DeckStatsPath = "$env:APPDATA\HearthstoneDeckTracker\DeckStats.xml",
	[string]$OutputPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hdt_opponent_history.tsv",
	[int]$Days = 3,
	[string]$Format = "Standard",
	[string]$GameMode = "Ranked"
)

$ErrorActionPreference = "Stop"

function Format-TsvValue($Value) {
	if ($null -eq $Value) {
		return ""
	}
	return ([string]$Value) -replace "[`t`r`n]", " "
}

function Get-NodeText($Node, [string]$Name) {
	$child = $Node.SelectSingleNode($Name)
	if ($null -eq $child) {
		return ""
	}
	return [string]$child.InnerText
}

function Try-ParseDate([string]$Value) {
	$result = [DateTime]::MinValue
	if ([DateTime]::TryParse($Value, [ref]$result)) {
		return $result
	}
	return $null
}

function Get-OpponentCardSummary($GameNode) {
	$cards = @($GameNode.SelectNodes("OpponentCards/Card"))
	if ($cards.Count -eq 0) {
		return ""
	}

	return ($cards | ForEach-Object {
		$id = [string]$_.GetAttribute("Id")
		$count = [string]$_.GetAttribute("Count")
		if ([string]::IsNullOrWhiteSpace($count)) {
			$count = "1"
		}
		"${id}:${count}"
	}) -join ";"
}

if (-not (Test-Path $DeckStatsPath)) {
	throw "DeckStats.xml was not found: $DeckStatsPath"
}

$since = (Get-Date).AddDays(-1 * [Math]::Abs($Days))
$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
	New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$doc = New-Object System.Xml.XmlDocument
$doc.Load($DeckStatsPath)

$rows = New-Object System.Collections.Generic.List[string]
$rows.Add((
	"game_id",
	"start_time",
	"end_time",
	"format",
	"game_mode",
	"result",
	"turns",
	"player_deck_id",
	"player_deck_name",
	"player_hero",
	"opponent_hero",
	"league_id",
	"star_level",
	"opponent_star_level",
	"replay_file",
	"hsreplay_url",
	"hsreplay_upload_id",
	"replay_path",
	"opponent_card_count",
	"opponent_cards"
) -join "`t")

foreach ($game in @($doc.SelectNodes("//Game"))) {
	$startText = Get-NodeText -Node $game -Name "StartTime"
	$startTime = Try-ParseDate $startText
	if ($null -eq $startTime -or $startTime -lt $since) {
		continue
	}

	$gameFormat = Get-NodeText -Node $game -Name "Format"
	$mode = Get-NodeText -Node $game -Name "GameMode"
	if ($gameFormat -ne $Format -or $mode -ne $GameMode) {
		continue
	}

	$cardNodes = @($game.SelectNodes("OpponentCards/Card"))
	$hsReplayUrl = ""
	$hsReplayUploadId = ""
	$hsReplay = $game.SelectSingleNode("HsReplay/ReplayUrl")
	if ($null -ne $hsReplay) {
		$hsReplayUrl = [string]$hsReplay.InnerText
	}
	$hsReplayUpload = $game.SelectSingleNode("HsReplay/UploadId")
	if ($null -ne $hsReplayUpload) {
		$hsReplayUploadId = [string]$hsReplayUpload.InnerText
	}
	$replayFile = Get-NodeText -Node $game -Name "ReplayFile"
	$replayPath = ""
	if (-not [string]::IsNullOrWhiteSpace($replayFile)) {
		$candidateReplayPath = Join-Path "$env:APPDATA\HearthstoneDeckTracker\Replays" $replayFile
		if (Test-Path -LiteralPath $candidateReplayPath) {
			$replayPath = (Resolve-Path -LiteralPath $candidateReplayPath).Path
		}
	}

	$values = @(
		(Get-NodeText -Node $game -Name "GameId"),
		$startTime.ToString("yyyy-MM-dd HH:mm:ss"),
		(Get-NodeText -Node $game -Name "EndTime"),
		$gameFormat,
		$mode,
		(Get-NodeText -Node $game -Name "Result"),
		(Get-NodeText -Node $game -Name "Turns"),
		(Get-NodeText -Node $game -Name "DeckId"),
		(Get-NodeText -Node $game -Name "DeckName"),
		(Get-NodeText -Node $game -Name "PlayerHero"),
		(Get-NodeText -Node $game -Name "OpponentHero"),
		(Get-NodeText -Node $game -Name "LeagueId"),
		(Get-NodeText -Node $game -Name "StarLevel"),
		(Get-NodeText -Node $game -Name "OpponentStarLevel"),
		$replayFile,
		$hsReplayUrl,
		$hsReplayUploadId,
		$replayPath,
		$cardNodes.Count,
		(Get-OpponentCardSummary -GameNode $game)
	)

	$rows.Add(($values | ForEach-Object { Format-TsvValue $_ }) -join "`t")
}

Set-Content -Path $OutputPath -Value $rows -Encoding UTF8
Write-Host "Wrote $($rows.Count - 1) HDT games to $OutputPath"
