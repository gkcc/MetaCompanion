param(
	[string]$DeckCodePath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_deckcodes.txt",
	[string]$HdtAppPath = "$env:LOCALAPPDATA\HearthstoneDeckTracker\app-1.52.19"
)

$ErrorActionPreference = "Stop"

[void][Reflection.Assembly]::LoadFrom((Join-Path $HdtAppPath "HearthDb.dll"))
[HearthDb.Cards]::LoadBaseData()

$classCounts = @{}
$unknownDbfIds = @{}
$importedDecks = 0
$invalidDecks = 0

foreach ($line in Get-Content $DeckCodePath) {
	if ($line -notmatch "AA[A-Za-z0-9+/=]+") {
		continue
	}

	try {
		$deckString = [HearthDb.Deckstrings.DeckSerializer]::Deserialize($matches[0])
		$hero = [HearthDb.Cards]::GetFromDbfId($deckString.HeroDbfId, $false)
		$knownCardCount = 0

		foreach ($entry in $deckString.CardDbfIds.GetEnumerator()) {
			$card = [HearthDb.Cards]::GetFromDbfId($entry.Key, $false)
			if ($card) {
				$knownCardCount += $entry.Value
			} else {
				$unknownDbfIds[$entry.Key] =
					1 + ($unknownDbfIds[$entry.Key] | ForEach-Object { if ($_){ $_ } else { 0 } })
			}
		}

		if ($hero -and $knownCardCount -gt 0) {
			$importedDecks++
			$className = $hero.Class.ToString()
			$classCounts[$className] =
				1 + ($classCounts[$className] | ForEach-Object { if ($_){ $_ } else { 0 } })
		} else {
			$invalidDecks++
		}
	} catch {
		$invalidDecks++
	}
}

"ImportedDecks=$importedDecks"
$classCounts.GetEnumerator() | Sort-Object Name | ForEach-Object {
	"$($_.Name)=$($_.Value)"
}
"InvalidDecks=$invalidDecks"
"UnknownDbfIds=$($unknownDbfIds.Count)"
