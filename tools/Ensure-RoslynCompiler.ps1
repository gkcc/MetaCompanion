param(
	[string]$Version = "4.2.0",
	[string]$PackagesDirectory = "$PSScriptRoot\..\packages",
	[string]$PreferredPath = "",
	[switch]$Quiet
)

$ErrorActionPreference = "Stop"

function Write-EnsureStatus([string]$Message) {
	if (-not $Quiet) {
		Write-Host $Message
	}
}

function Resolve-CscPath([string]$Path) {
	if ([string]::IsNullOrWhiteSpace($Path)) {
		return $null
	}
	if ((Test-Path -LiteralPath $Path -PathType Container) -and
		(Test-Path -LiteralPath (Join-Path $Path "csc.exe"))) {
		return (Resolve-Path (Join-Path $Path "csc.exe")).Path
	}
	if ((Test-Path -LiteralPath $Path -PathType Leaf) -and
		[IO.Path]::GetFileName($Path).Equals("csc.exe", [StringComparison]::OrdinalIgnoreCase)) {
		return (Resolve-Path $Path).Path
	}
	return $null
}

function Get-RepoPackageDirectory([string]$Root, [string]$PackageVersion) {
	return Join-Path $Root "Microsoft.Net.Compilers.$PackageVersion"
}

function Get-UserPackageDirectory([string]$PackageVersion) {
	if ([string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
		return $null
	}
	return Join-Path $env:USERPROFILE ".nuget\packages\microsoft.net.compilers\$PackageVersion"
}

function Copy-RoslynPackage([string]$SourceDirectory, [string]$DestinationDirectory) {
	if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
		return
	}

	if (Test-Path -LiteralPath $DestinationDirectory) {
		Remove-Item -LiteralPath $DestinationDirectory -Recurse -Force
	}
	New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationDirectory) | Out-Null
	Copy-Item -LiteralPath $SourceDirectory -Destination $DestinationDirectory -Recurse -Force
}

function Expand-NuGetPackage([string]$PackagePath, [string]$DestinationDirectory) {
	$tempZip = [IO.Path]::ChangeExtension($PackagePath, ".zip")
	Copy-Item -LiteralPath $PackagePath -Destination $tempZip -Force
	try {
		if (Test-Path -LiteralPath $DestinationDirectory) {
			Remove-Item -LiteralPath $DestinationDirectory -Recurse -Force
		}
		New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
		Expand-Archive -LiteralPath $tempZip -DestinationPath $DestinationDirectory -Force
	} finally {
		Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
	}
}

$preferredCsc = Resolve-CscPath $PreferredPath
if ($preferredCsc) {
	Write-Output $preferredCsc
	return
}

$packagesRoot = (New-Item -ItemType Directory -Force -Path $PackagesDirectory).FullName
$repoPackageDirectory = Get-RepoPackageDirectory $packagesRoot $Version
$repoCsc = Resolve-CscPath (Join-Path $repoPackageDirectory "tools")
if ($repoCsc) {
	Write-Output $repoCsc
	return
}

$userPackageDirectory = Get-UserPackageDirectory $Version
$userCsc = $null
if (-not [string]::IsNullOrWhiteSpace($userPackageDirectory)) {
	$userCsc = Resolve-CscPath (Join-Path $userPackageDirectory "tools")
}
if ($userCsc) {
	Write-EnsureStatus "Copying Microsoft.Net.Compilers $Version from NuGet user cache to local packages."
	Copy-RoslynPackage $userPackageDirectory $repoPackageDirectory
	$repoCsc = Resolve-CscPath (Join-Path $repoPackageDirectory "tools")
	if ($repoCsc) {
		Write-Output $repoCsc
		return
	}
}

$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ("MetaCompanion-Roslyn-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempDirectory | Out-Null
try {
	$packagePath = Join-Path $tempDirectory "Microsoft.Net.Compilers.$Version.nupkg"
	$url = "https://www.nuget.org/api/v2/package/Microsoft.Net.Compilers/$Version"
	Write-EnsureStatus "Downloading Microsoft.Net.Compilers $Version..."
	Invoke-WebRequest -Uri $url -OutFile $packagePath -UseBasicParsing
	Expand-NuGetPackage $packagePath $repoPackageDirectory
} finally {
	Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

$repoCsc = Resolve-CscPath (Join-Path $repoPackageDirectory "tools")
if (-not $repoCsc) {
	throw "Microsoft.Net.Compilers $Version was installed, but csc.exe was not found under $repoPackageDirectory."
}

Write-Output $repoCsc
