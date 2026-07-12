param(
	[string]$SolutionPath = "$PSScriptRoot\..\MetaCompanion.sln",
	[string]$Configuration = "Release",
	[string]$Platform = "AnyCPU",
	[string]$HdtAppPath = "",
	[string]$CscToolPath = "",
	[string]$RoslynVersion = "4.2.0"
)

$ErrorActionPreference = "Stop"

function Resolve-HdtAppPath {
	if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
		throw "LOCALAPPDATA is not set. Pass -HdtAppPath with the Hearthstone Deck Tracker app directory."
	}
	$hdtRoot = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
	if (-not (Test-Path -LiteralPath $hdtRoot)) {
		throw "Hearthstone Deck Tracker was not found under $hdtRoot."
	}

	$latest = Get-ChildItem -LiteralPath $hdtRoot -Directory -Filter "app-*" |
		Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "HearthstoneDeckTracker.exe") } |
		Sort-Object @{Expression = {
			try { [version]$_.Name.Substring(4) } catch { [version]"0.0" }
		}; Descending = $true} |
		Select-Object -First 1
	if (-not $latest) {
		throw "HearthstoneDeckTracker.exe was not found under $hdtRoot."
	}
	return $latest.FullName
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$solution = (Resolve-Path $SolutionPath).Path
$hdtPath = if ([string]::IsNullOrWhiteSpace($HdtAppPath)) {
	Resolve-HdtAppPath
} else {
	(Resolve-Path $HdtAppPath).Path
}

$ensureScript = Join-Path $PSScriptRoot "Ensure-RoslynCompiler.ps1"
$csc = (& $ensureScript `
	-Version $RoslynVersion `
	-PackagesDirectory (Join-Path $repoRoot "packages") `
	-PreferredPath $CscToolPath `
	-Quiet | Select-Object -Last 1)
if ([string]::IsNullOrWhiteSpace($csc) -or -not (Test-Path -LiteralPath $csc)) {
	throw "Roslyn csc.exe was not found. Run tools\Ensure-RoslynCompiler.ps1 for details."
}

$referenceAssemblyScript = Join-Path $PSScriptRoot "Ensure-NetFxReferenceAssemblies.ps1"
$frameworkPath = (& $referenceAssemblyScript `
	-PackagesDirectory (Join-Path $repoRoot "packages") `
	-Quiet | Select-Object -Last 1)
if ([string]::IsNullOrWhiteSpace($frameworkPath) -or
	-not (Test-Path -LiteralPath (Join-Path $frameworkPath "mscorlib.dll"))) {
	throw ".NET Framework 4.7.2 reference assemblies could not be resolved."
}

$msbuild = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
if (-not (Test-Path -LiteralPath $msbuild)) {
	throw "MSBuild.exe was not found: $msbuild"
}

Write-Host "[Meta Companion] HDT app path: $hdtPath"
Write-Host "[Meta Companion] Roslyn compiler: $csc"
Write-Host "[Meta Companion] .NET reference assemblies: $frameworkPath"
& $msbuild $solution `
	"/p:Configuration=$Configuration" `
	"/p:Platform=$Platform" `
	"/p:HdtAppPath=$hdtPath" `
	"/p:CscToolPath=$(Split-Path -Parent $csc)" `
	"/p:CscToolExe=csc.exe" `
	"/p:FrameworkPathOverride=$frameworkPath" `
	"/p:LangVersion=latest" `
	"/m" `
	"/v:minimal"
exit $LASTEXITCODE
