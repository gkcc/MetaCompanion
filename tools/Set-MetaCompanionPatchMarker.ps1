param(
	[datetime]$PatchTime = (Get-Date),
	[string]$PatchMarkerPath = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\patch_marker.txt"
)

$ErrorActionPreference = "Stop"

$directory = Split-Path -Parent $PatchMarkerPath
if (-not [string]::IsNullOrWhiteSpace($directory)) {
	New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$PatchTime.ToString("o") | Set-Content -LiteralPath $PatchMarkerPath -Encoding UTF8
Write-Host "Meta Companion patch marker written:"
Write-Host "  $PatchMarkerPath"
Write-Host "  $($PatchTime.ToString("yyyy-MM-dd HH:mm:ss zzz"))"
