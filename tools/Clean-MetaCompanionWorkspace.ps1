param(
	[switch]$WhatIf
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$targets = @(
	".tmp_hsreplay_js",
	"pending-install-*.log",
	"MetaCompanion\bin",
	"MetaCompanion\obj",
	"MetaCompanionTests\bin",
	"MetaCompanionTests\obj"
)

function Assert-InRepo([string]$Path) {
	$resolved = (Resolve-Path -LiteralPath $Path).Path
	if (-not $resolved.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
		throw "Refusing to clean outside workspace: $resolved"
	}
	return $resolved
}

foreach ($target in $targets) {
	$matches = Get-ChildItem -Path $repoRoot -Filter $target -Force -ErrorAction SilentlyContinue
	if ($target.Contains("\") -or $target.Contains("/")) {
		$path = Join-Path $repoRoot $target
		$matches = if (Test-Path -LiteralPath $path) { @(Get-Item -LiteralPath $path -Force) } else { @() }
	}

	foreach ($match in $matches) {
		$path = Assert-InRepo $match.FullName
		if ($WhatIf) {
			Write-Host "Would remove $path"
		} else {
			Remove-Item -LiteralPath $path -Recurse -Force
			Write-Host "Removed $path"
		}
	}
}
