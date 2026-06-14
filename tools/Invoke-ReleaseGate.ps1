param(
	[string]$SolutionPath = "$PSScriptRoot\..\MetaCompanion.sln",
	[string]$PackagePath = "",
	[string]$ArtifactsDirectory = "$PSScriptRoot\..\artifacts\release-gate",
	[string]$CscToolPath = "",
	[switch]$SkipBuild,
	[switch]$SkipTests,
	[switch]$SelfTest
)

$ErrorActionPreference = "Stop"

function Get-ReleaseGateRepoRoot {
	return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Find-ReleaseGateRoslynCompiler([string]$PreferredPath, [string]$RepoRoot) {
	$candidates = New-Object System.Collections.Generic.List[string]
	if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
		if ((Test-Path -LiteralPath $PreferredPath -PathType Container) -and
			(Test-Path -LiteralPath (Join-Path $PreferredPath "csc.exe"))) {
			$candidates.Add((Join-Path $PreferredPath "csc.exe"))
		} elseif (Test-Path -LiteralPath $PreferredPath -PathType Leaf) {
			$candidates.Add($PreferredPath)
		}
	}

	$packageRoots = @(
		(Join-Path $RepoRoot "packages"),
		(Join-Path $env:USERPROFILE ".nuget\packages")
	) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }

	foreach ($root in $packageRoots) {
		Get-ChildItem -LiteralPath $root -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
			Where-Object { $_.FullName -match "\\microsoft\.net\.compilers(\\|\.|$)" } |
			Sort-Object FullName -Descending |
			ForEach-Object { $candidates.Add($_.FullName) }
	}

	$match = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
	if (-not $match) {
		throw "Roslyn csc.exe was not found. Install Microsoft.Net.Compilers or pass -CscToolPath."
	}
	return (Resolve-Path $match).Path
}

function Get-ReleaseGateSensitivePatterns {
	return @(
		@{ Name = "Cookie header"; Pattern = "(?im)\bCookie\s*:\s*(?![\$<%""'])[A-Za-z0-9_.-]+\s*=\s*[^\r\n]{12,}" },
		@{ Name = "Session cookie"; Pattern = "(?i)\b(sessionid|csrftoken|cf_clearance|__cf_bm|remember_token|auth_token|access_token|refresh_token)\s*=\s*(?![\$<])[A-Za-z0-9_\-%.]{12,}" },
		@{ Name = "Bearer token"; Pattern = "(?i)\bBearer\s+(?![\$<])[A-Za-z0-9._\-]{20,}" },
		@{ Name = "JWT token"; Pattern = "eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}" }
	)
}

function Test-ReleaseGateSensitiveText([string]$Text) {
	$hits = New-Object System.Collections.Generic.List[string]
	if ([string]::IsNullOrEmpty($Text)) {
		return $hits
	}
	foreach ($pattern in Get-ReleaseGateSensitivePatterns) {
		if ($Text -match $pattern.Pattern) {
			$hits.Add($pattern.Name)
		}
	}
	return $hits
}

function Get-ReleaseGateBlockedPackageReason([string]$EntryName) {
	$entry = ($EntryName -replace "\\", "/").TrimStart("/")
	if ($entry -match "(^|/)tools/.*\.ps1$") { return "PowerShell tools are not part of the community package." }
	if ($entry -match "(^|/)(bin|obj)/") { return "Build output directories must not be packaged." }
	if ($entry -match "(^|/)hsreplay_cookie\.txt$") { return "HSReplay cookie files must not be packaged." }
	if ($entry -match "(^|/)Premium/") { return "Premium cache must not be packaged." }
	if ($entry -match "(^|/)(match_history|hdt_opponent_history|prediction_timeline|match_corrections)\.tsv$") { return "Local match history must not be packaged." }
	if ($entry -match "(^|/)local_meta_.*\.(tsv|json)$") { return "Local meta cache must not be packaged." }
	if ($entry -match "(^|/)personal_recommendations\.(tsv|json)$") { return "Personal recommendations must not be packaged." }
	if ($entry -match "\.(pfx|publishsettings)$") { return "Certificate or publish settings must not be packaged." }
	return $null
}

function Get-ReleaseGateTextFileExtensions {
	return @(".config", ".cs", ".json", ".md", ".ps1", ".txt", ".tsv", ".xml", ".yml", ".yaml")
}

function Search-ReleaseGateRepoSecrets([string]$RepoRoot) {
	$matches = New-Object System.Collections.Generic.List[object]
	$files = & git -C $RepoRoot ls-files 2>$null
	if ($LASTEXITCODE -ne 0 -or -not $files) {
		$files = Get-ChildItem -LiteralPath $RepoRoot -Recurse -File |
			Where-Object { $_.FullName -notmatch "\\(bin|obj|\.git|artifacts|dist)\\" } |
			ForEach-Object { $_.FullName.Substring($RepoRoot.Length).TrimStart("\", "/") }
	}

	foreach ($relative in $files) {
		$path = Join-Path $RepoRoot $relative
		if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
		if ((Get-Item -LiteralPath $path).Length -gt 1048576) { continue }
		$extension = [IO.Path]::GetExtension($path)
		if ((Get-ReleaseGateTextFileExtensions) -notcontains $extension -and $extension -ne "") { continue }
		$text = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
		foreach ($hit in Test-ReleaseGateSensitiveText $text) {
			$matches.Add([pscustomobject]@{ Path = $relative; Reason = $hit })
		}
	}
	return $matches
}

function Get-ReleaseGatePackageEntries([string]$Path) {
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
		return @()
	}
	if (Test-Path -LiteralPath $Path -PathType Container) {
		$root = (Resolve-Path $Path).Path
		return Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
			$_.FullName.Substring($root.Length).TrimStart("\", "/")
		}
	}

	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Path).Path)
	try {
		return @($zip.Entries | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) } | ForEach-Object { $_.FullName })
	} finally {
		$zip.Dispose()
	}
}

function Search-ReleaseGatePackageSecrets([string]$Path) {
	$matches = New-Object System.Collections.Generic.List[object]
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
		return $matches
	}

	$textExtensions = Get-ReleaseGateTextFileExtensions
	if (Test-Path -LiteralPath $Path -PathType Container) {
		$root = (Resolve-Path $Path).Path
		Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
			if ($_.Length -gt 1048576) { return }
			if ($textExtensions -notcontains $_.Extension -and $_.Extension -ne "") { return }
			$relative = $_.FullName.Substring($root.Length).TrimStart("\", "/")
			$text = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue
			foreach ($hit in Test-ReleaseGateSensitiveText $text) {
				$matches.Add([pscustomobject]@{ Path = $relative; Reason = $hit })
			}
		}
		return $matches
	}

	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Path).Path)
	try {
		foreach ($entry in $zip.Entries) {
			if ([string]::IsNullOrWhiteSpace($entry.Name) -or $entry.Length -gt 1048576) { continue }
			$extension = [IO.Path]::GetExtension($entry.Name)
			if ($textExtensions -notcontains $extension -and $extension -ne "") { continue }
			$stream = $entry.Open()
			try {
				$reader = New-Object IO.StreamReader($stream)
				$text = $reader.ReadToEnd()
			} finally {
				$stream.Dispose()
			}
			foreach ($hit in Test-ReleaseGateSensitiveText $text) {
				$matches.Add([pscustomobject]@{ Path = $entry.FullName; Reason = $hit })
			}
		}
	} finally {
		$zip.Dispose()
	}
	return $matches
}

function New-ReleaseGateCommunityPackage([string]$RepoRoot, [string]$BuildDll, [string]$OutputDirectory) {
	$packageRoot = Join-Path $OutputDirectory "package-root"
	$zipPath = Join-Path $OutputDirectory "MetaCompanion-community.zip"
	if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
	if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
	New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
	Copy-Item -LiteralPath $BuildDll -Destination (Join-Path $packageRoot "MetaCompanion.dll") -Force
	foreach ($name in @("README.md", "LICENSE", "NOTICE.md")) {
		$source = Join-Path $RepoRoot $name
		if (Test-Path -LiteralPath $source) {
			Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $name) -Force
		}
	}
	$docsTarget = Join-Path $packageRoot "docs"
	New-Item -ItemType Directory -Force -Path $docsTarget | Out-Null
	foreach ($name in @("IMPLEMENTATION-SUMMARY.md", "LOCAL-HSREPLAY.md", "PROJECT-STRUCTURE.md", "RECOMMENDATION-DESIGN.md", "RELEASE-CHECKLIST.md")) {
		$source = Join-Path (Join-Path $RepoRoot "docs") $name
		if (Test-Path -LiteralPath $source) {
			Copy-Item -LiteralPath $source -Destination (Join-Path $docsTarget $name) -Force
		}
	}
	Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -Force
	return $zipPath
}

function Invoke-ReleaseGateCommand([string]$Name, [string]$FilePath, [string[]]$Arguments, [string]$LogPath) {
	Write-Host "== $Name =="
	$output = & $FilePath @Arguments 2>&1
	$output | Set-Content -LiteralPath $LogPath -Encoding UTF8
	if ($LASTEXITCODE -ne 0) {
		throw "$Name failed with exit code $LASTEXITCODE. See $LogPath"
	}
	return $output
}

function Invoke-ReleaseGateSelfTest {
	$repoRoot = Get-ReleaseGateRepoRoot
	$csc = Find-ReleaseGateRoslynCompiler "" $repoRoot
	if (-not (Test-Path -LiteralPath $csc)) { throw "Roslyn self-test failed." }

	$cleanHits = Test-ReleaseGateSensitiveText "Cookie: `$cookieHeader"
	if ($cleanHits.Count -ne 0) { throw "Placeholder cookie text should not be flagged." }
	$leakedCookie = "Cookie: " + "sessionid=" + ("a" * 24)
	if ((Test-ReleaseGateSensitiveText $leakedCookie).Count -eq 0) { throw "Cookie leak was not detected." }
	$leakedBearer = "Bearer " + ("b" * 24)
	if ((Test-ReleaseGateSensitiveText $leakedBearer).Count -eq 0) { throw "Bearer leak was not detected." }

	if (-not (Get-ReleaseGateBlockedPackageReason "tools/Update-MetaCompanionData.ps1")) { throw "Tool script package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "Premium/Meta/latest/cache.json")) { throw "Premium package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "local_meta_environment.tsv")) { throw "Local meta package block failed." }
	if (Get-ReleaseGateBlockedPackageReason "MetaCompanion.dll") { throw "DLL should be allowed." }
	if (Get-ReleaseGateBlockedPackageReason "docs/RELEASE-CHECKLIST.md") { throw "Docs should be allowed." }

	Write-Host "SELFTEST OK"
}

if ($SelfTest) {
	Invoke-ReleaseGateSelfTest
	return
}

$repoRoot = Get-ReleaseGateRepoRoot
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runDirectory = Join-Path $ArtifactsDirectory $runId
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
$report = New-Object System.Collections.Generic.List[string]
$buildDll = Join-Path $repoRoot "MetaCompanion\bin\x86\Release\MetaCompanion.dll"
$resolvedPackagePath = $PackagePath
$csc = $null

try {
	$csc = Find-ReleaseGateRoslynCompiler $CscToolPath $repoRoot
	$msbuild = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
	if (-not $SkipBuild) {
		Invoke-ReleaseGateCommand "Release x86 build" $msbuild @(
			(Resolve-Path $SolutionPath).Path,
			"/p:Configuration=Release",
			"/p:Platform=x86",
			"/p:CscToolPath=$(Split-Path -Parent $csc)",
			"/p:CscToolExe=csc.exe",
			"/p:LangVersion=latest",
			"/m",
			"/v:minimal"
		) (Join-Path $runDirectory "build.log") | Out-Null
	}
	if (-not (Test-Path -LiteralPath $buildDll)) {
		throw "Release DLL was not found: $buildDll"
	}
	if (-not $SkipTests) {
		$testPowerShell = Join-Path $env:WINDIR "SysWOW64\WindowsPowerShell\v1.0\powershell.exe"
		Invoke-ReleaseGateCommand "MSTest reflection runner" $testPowerShell @(
			"-NoProfile",
			"-ExecutionPolicy",
			"Bypass",
			"-File",
			(Join-Path $repoRoot "tools\Run-Tests.ps1")
		) (Join-Path $runDirectory "tests.log") | Out-Null
	}
} catch {
	$failures.Add($_.Exception.Message)
}

$repoSecretMatches = Search-ReleaseGateRepoSecrets $repoRoot
foreach ($match in $repoSecretMatches) {
	$failures.Add("Sensitive value in tracked file: $($match.Path) ($($match.Reason))")
}

if ([string]::IsNullOrWhiteSpace($resolvedPackagePath) -and (Test-Path -LiteralPath $buildDll)) {
	try {
		$resolvedPackagePath = New-ReleaseGateCommunityPackage $repoRoot $buildDll $runDirectory
	} catch {
		$failures.Add("Community package creation failed: $($_.Exception.Message)")
	}
}

$packageEntries = @()
if (-not [string]::IsNullOrWhiteSpace($resolvedPackagePath) -and (Test-Path -LiteralPath $resolvedPackagePath)) {
	$packageEntries = @(Get-ReleaseGatePackageEntries $resolvedPackagePath)
	foreach ($entry in $packageEntries) {
		$reason = Get-ReleaseGateBlockedPackageReason $entry
		if ($reason) {
			$failures.Add("Blocked package entry: $entry ($reason)")
		}
	}
	foreach ($match in Search-ReleaseGatePackageSecrets $resolvedPackagePath) {
		$failures.Add("Sensitive value in package: $($match.Path) ($($match.Reason))")
	}
} else {
	$failures.Add("Package path was not provided and a package could not be created.")
}

$report.Add("# Meta Companion Release Gate")
$report.Add("")
$report.Add("- Run: $runId")
$report.Add("- Repo: $repoRoot")
$report.Add("- Roslyn: $csc")
$report.Add("- Result: " + ($(if ($failures.Count -eq 0) { "PASS" } else { "FAIL" })))
$report.Add("")
$report.Add("## Build Artifact")
if (Test-Path -LiteralPath $buildDll) {
	$hash = Get-FileHash -LiteralPath $buildDll -Algorithm SHA256
	$report.Add("- DLL: $buildDll")
	$report.Add("- SHA256: $($hash.Hash)")
} else {
	$report.Add("- DLL missing: $buildDll")
}
$report.Add("")
$report.Add("## Package")
if (-not [string]::IsNullOrWhiteSpace($resolvedPackagePath) -and (Test-Path -LiteralPath $resolvedPackagePath)) {
	$packageHash = Get-FileHash -LiteralPath $resolvedPackagePath -Algorithm SHA256
	$report.Add("- Path: $resolvedPackagePath")
	$report.Add("- SHA256: $($packageHash.Hash)")
	$report.Add("- Entries:")
	foreach ($entry in $packageEntries) { $report.Add("  - $entry") }
} else {
	$report.Add("- Package missing")
}
$report.Add("")
$report.Add("## Secret Scan")
$report.Add("- Tracked-file matches: $($repoSecretMatches.Count)")
$report.Add("")
$report.Add("## Failures")
if ($failures.Count -eq 0) {
	$report.Add("- None")
} else {
	foreach ($failure in $failures) { $report.Add("- $failure") }
}

$reportPath = Join-Path $runDirectory "release-gate.md"
$report | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "Release gate report: $reportPath"

if ($failures.Count -gt 0) {
	$failures | ForEach-Object { Write-Error $_ }
	exit 1
}

Write-Host "RELEASE GATE PASS"
