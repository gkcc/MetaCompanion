param(
	[string]$Version = "1.0.3",
	[string]$PackagesDirectory = "$PSScriptRoot\..\packages",
	[switch]$Quiet
)

$ErrorActionPreference = "Stop"

function Write-EnsureStatus([string]$Message) {
	if (-not $Quiet) {
		Write-Host $Message
	}
}

function Resolve-ReferencePath([string]$PackageDirectory) {
	$path = Join-Path $PackageDirectory "build\.NETFramework\v4.7.2"
	if (Test-Path -LiteralPath (Join-Path $path "mscorlib.dll")) {
		return (Resolve-Path $path).Path
	}
	return $null
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

$packagesRoot = (New-Item -ItemType Directory -Force -Path $PackagesDirectory).FullName
$packageDirectory = Join-Path $packagesRoot "Microsoft.NETFramework.ReferenceAssemblies.net472.$Version"
$referencePath = Resolve-ReferencePath $packageDirectory
if ($referencePath) {
	Write-Output $referencePath
	return
}

$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) (
	"MetaCompanion-NetFxReferences-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempDirectory | Out-Null
try {
	$packagePath = Join-Path $tempDirectory "references.nupkg"
	$url = "https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net472/$Version"
	Write-EnsureStatus "Downloading .NET Framework 4.7.2 reference assemblies $Version..."
	Invoke-WebRequest -Uri $url -OutFile $packagePath -UseBasicParsing
	Expand-NuGetPackage $packagePath $packageDirectory
} finally {
	Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

$referencePath = Resolve-ReferencePath $packageDirectory
if (-not $referencePath) {
	throw ".NET Framework 4.7.2 reference assemblies were installed, but mscorlib.dll was not found."
}

Write-Output $referencePath
