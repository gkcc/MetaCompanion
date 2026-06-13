param(
	[int]$PollSeconds = 2,
	[string]$BuildPath = "",
	[string]$LogPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\pending-install.log",
	[switch]$IncludeTools
)

$ErrorActionPreference = "Stop"

$logDirectory = Split-Path -Parent $LogPath
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

function Write-PendingLog([string]$message) {
	$timestamp = (Get-Date).ToString("o")
	Add-Content -Path $LogPath -Value "$timestamp $message"
}

Write-PendingLog "Waiting for Hearthstone Deck Tracker to exit..."
while (Get-Process HearthstoneDeckTracker -ErrorAction SilentlyContinue) {
	Start-Sleep -Seconds $PollSeconds
}

Write-PendingLog "Hearthstone Deck Tracker exited. Installing Meta Companion..."
try {
	if ($IncludeTools) {
		if ([string]::IsNullOrWhiteSpace($BuildPath)) {
			& "$PSScriptRoot\Install-MetaCompanion.ps1" -IncludeTools *>&1 | ForEach-Object {
				Write-PendingLog $_
			}
		} else {
			Write-PendingLog "Using build path: $BuildPath"
			& "$PSScriptRoot\Install-MetaCompanion.ps1" -BuildPath $BuildPath -IncludeTools *>&1 | ForEach-Object {
				Write-PendingLog $_
			}
		}
	} elseif ([string]::IsNullOrWhiteSpace($BuildPath)) {
		& "$PSScriptRoot\Install-MetaCompanion.ps1" *>&1 | ForEach-Object {
			Write-PendingLog $_
		}
	} else {
		Write-PendingLog "Using build path: $BuildPath"
		& "$PSScriptRoot\Install-MetaCompanion.ps1" -BuildPath $BuildPath *>&1 | ForEach-Object {
			Write-PendingLog $_
		}
	}
	Write-PendingLog "Install complete."
} catch {
	Write-PendingLog "Install failed: $($_.Exception.Message)"
	throw
}
