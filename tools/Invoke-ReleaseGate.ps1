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

function Resolve-ReleaseGateHdtAppPath {
	if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
		throw "LOCALAPPDATA is not set. Install Hearthstone Deck Tracker for this user or set LOCALAPPDATA before running the release gate."
	}
	$hdtRoot = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
	if (-not (Test-Path -LiteralPath $hdtRoot)) {
		throw "HDT local app directory was not found: $hdtRoot"
	}
	$latest = Get-ChildItem -LiteralPath $hdtRoot -Directory -Filter "app-*" |
		Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "HearthstoneDeckTracker.exe") } |
		Sort-Object @{Expression = {
			try { [version]$_.Name.Substring(4) } catch { [version]"0.0" }
		}; Descending = $true} |
		Select-Object -First 1
	if (-not $latest) {
		throw "HearthstoneDeckTracker.exe was not found under $hdtRoot"
	}
	return $latest.FullName
}

function Get-ReleaseGateNativePowerShellPath {
	$system32PowerShell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
	if (Test-Path -LiteralPath $system32PowerShell) {
		return $system32PowerShell
	}
	return "powershell.exe"
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

	$packageRoots = @((Join-Path $RepoRoot "packages"))
	if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
		$packageRoots += (Join-Path $env:USERPROFILE ".nuget\packages")
	}
	$packageRoots = $packageRoots |
		Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }

	foreach ($root in $packageRoots) {
		Get-ChildItem -LiteralPath $root -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
			Where-Object { $_.FullName -match "\\microsoft\.net\.compilers(\\|\.|$)" } |
			Sort-Object FullName -Descending |
			ForEach-Object { $candidates.Add($_.FullName) }
	}

	$match = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
	if (-not $match) {
		$ensureScript = Join-Path $RepoRoot "tools\Ensure-RoslynCompiler.ps1"
		if (Test-Path -LiteralPath $ensureScript) {
			$ensured = & $ensureScript `
				-PreferredPath $PreferredPath `
				-PackagesDirectory (Join-Path $RepoRoot "packages") `
				-Quiet |
				Select-Object -Last 1
			if (-not [string]::IsNullOrWhiteSpace($ensured) -and
				(Test-Path -LiteralPath $ensured)) {
				return (Resolve-Path $ensured).Path
			}
		}
		throw "Roslyn csc.exe was not found. Run tools\Ensure-RoslynCompiler.ps1 or pass -CscToolPath."
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

function ConvertTo-ReleaseGateEntryPath([string]$Path) {
	if ([string]::IsNullOrWhiteSpace($Path)) {
		return ""
	}
	return ($Path -replace "\\", "/").TrimStart("/")
}

function Get-ReleaseGateBlockedPackageReason([string]$EntryName) {
	$entry = ConvertTo-ReleaseGateEntryPath $EntryName
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

function New-ReleaseGateSecretScanResult([object]$Matches, [int]$ScannedFileCount, [string]$Source = "") {
	return [pscustomobject]@{
		Matches = $Matches
		ScannedFileCount = $ScannedFileCount
		Source = $Source
	}
}

function Test-ReleaseGateRepoScanPath([string]$RelativePath) {
	$entry = ConvertTo-ReleaseGateEntryPath $RelativePath
	if ([string]::IsNullOrWhiteSpace($entry)) {
		return $false
	}

	if ($entry -match "^dist/(Install-MetaCompanion|Wait-AndInstall-MetaCompanion)\.ps1$") {
		return $true
	}
	if ($entry -match "(^|/)(\.git|\.idea|\.vs|artifacts|bin|obj|dist|packages|node_modules|bower_components|Premium|Logs)(/|$)") {
		return $false
	}
	if ($entry -match "(^|/)hsreplay_cookie\.txt$") { return $false }
	if ($entry -match "(^|/)pending-install-.*\.log$") { return $false }
	if ($entry -match "(^|/)(match_history|hdt_opponent_history|prediction_timeline|match_corrections)\.tsv$") { return $false }
	if ($entry -match "(^|/)local_meta_.*\.(tsv|json)$") { return $false }
	if ($entry -match "(^|/)personal_recommendations\.(tsv|json)$") { return $false }
	return $true
}

function Search-ReleaseGateRepoSecrets([string]$RepoRoot) {
	$matches = New-Object System.Collections.Generic.List[object]
	$scannedFiles = 0
	$source = "git ls-files"
	$files = & git -c core.quotepath=false -C $RepoRoot ls-files 2>$null
	if ($LASTEXITCODE -ne 0 -or -not $files) {
		$source = "filesystem fallback"
		$files = Get-ChildItem -LiteralPath $RepoRoot -Recurse -File |
			ForEach-Object { ConvertTo-ReleaseGateEntryPath ($_.FullName.Substring($RepoRoot.Length)) } |
			Where-Object { Test-ReleaseGateRepoScanPath $_ }
	}

	foreach ($relative in $files) {
		$path = Join-Path $RepoRoot $relative
		try {
			if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
		} catch {
			continue
		}
		if ((Get-Item -LiteralPath $path).Length -gt 1048576) { continue }
		$extension = [IO.Path]::GetExtension($path)
		if ((Get-ReleaseGateTextFileExtensions) -notcontains $extension -and $extension -ne "") { continue }
		$scannedFiles++
		$text = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
		foreach ($hit in Test-ReleaseGateSensitiveText $text) {
			$matches.Add([pscustomobject]@{ Path = $relative; Reason = $hit })
		}
	}
	return New-ReleaseGateSecretScanResult $matches $scannedFiles $source
}

function Get-ReleaseGatePackageEntries([string]$Path) {
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
		return @()
	}
	if (Test-Path -LiteralPath $Path -PathType Container) {
		$root = (Resolve-Path $Path).Path
		return Get-ChildItem -LiteralPath $root -Recurse -File |
			Sort-Object FullName |
			ForEach-Object {
				ConvertTo-ReleaseGateEntryPath ($_.FullName.Substring($root.Length))
			}
	}

	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Path).Path)
	try {
		return @(
			$zip.Entries |
				Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) } |
				ForEach-Object { ConvertTo-ReleaseGateEntryPath $_.FullName } |
				Sort-Object
		)
	} finally {
		$zip.Dispose()
	}
}

function Search-ReleaseGatePackageSecrets([string]$Path) {
	$matches = New-Object System.Collections.Generic.List[object]
	$scannedFiles = 0
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
		return New-ReleaseGateSecretScanResult $matches $scannedFiles
	}

	$textExtensions = Get-ReleaseGateTextFileExtensions
	if (Test-Path -LiteralPath $Path -PathType Container) {
		$root = (Resolve-Path $Path).Path
		foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName) {
			if ($file.Length -gt 1048576) { continue }
			if ($textExtensions -notcontains $file.Extension -and $file.Extension -ne "") { continue }
			$relative = ConvertTo-ReleaseGateEntryPath ($file.FullName.Substring($root.Length))
			$scannedFiles++
			$text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
			foreach ($hit in Test-ReleaseGateSensitiveText $text) {
				$matches.Add([pscustomobject]@{ Path = $relative; Reason = $hit })
			}
		}
		return New-ReleaseGateSecretScanResult $matches $scannedFiles
	}

	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Path).Path)
	try {
		foreach ($entry in $zip.Entries) {
			if ([string]::IsNullOrWhiteSpace($entry.Name) -or $entry.Length -gt 1048576) { continue }
			$extension = [IO.Path]::GetExtension($entry.Name)
			if ($textExtensions -notcontains $extension -and $extension -ne "") { continue }
			$scannedFiles++
			$stream = $entry.Open()
			try {
				$reader = New-Object IO.StreamReader($stream)
				$text = $reader.ReadToEnd()
			} finally {
				$stream.Dispose()
			}
			foreach ($hit in Test-ReleaseGateSensitiveText $text) {
				$matches.Add([pscustomobject]@{ Path = (ConvertTo-ReleaseGateEntryPath $entry.FullName); Reason = $hit })
			}
		}
	} finally {
		$zip.Dispose()
	}
	return New-ReleaseGateSecretScanResult $matches $scannedFiles
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

function Format-ReleaseGateOptionalValue([string]$Value, [bool]$Required) {
	if (-not [string]::IsNullOrWhiteSpace($Value)) {
		return $Value
	}
	if ($Required) {
		return "Not resolved"
	}
	return "Not required"
}

function Format-ReleaseGateLogValue([string]$Path, [string]$SkippedReason) {
	if (-not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return (Resolve-ReleaseGateExistingPath $Path)
	}
	if (-not [string]::IsNullOrWhiteSpace($SkippedReason)) {
		return "Not generated ($SkippedReason)"
	}
	return "Not generated"
}

function Resolve-ReleaseGateExistingPath([string]$Path) {
	if ([string]::IsNullOrWhiteSpace($Path)) {
		return $Path
	}
	try {
		if (Test-Path -LiteralPath $Path) {
			return (Resolve-Path -LiteralPath $Path).Path
		}
	} catch {
	}
	return $Path
}

function Format-ReleaseGateInputPath([string]$Path, [string]$EmptyValue) {
	if ([string]::IsNullOrWhiteSpace($Path)) {
		return $EmptyValue
	}
	return Resolve-ReleaseGateExistingPath $Path
}

function Get-ReleaseGateGitInfo([string]$RepoRoot) {
	$branch = "Unavailable"
	$commit = "Unavailable"
	$dirtyFiles = "Unavailable"

	try {
		$branchOutput = & git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null
		if ($LASTEXITCODE -eq 0 -and $branchOutput) {
			$branch = ($branchOutput | Select-Object -First 1)
		}
	} catch {
	}

	try {
		$commitOutput = & git -C $RepoRoot rev-parse --short=12 HEAD 2>$null
		if ($LASTEXITCODE -eq 0 -and $commitOutput) {
			$commit = ($commitOutput | Select-Object -First 1)
		}
	} catch {
	}

	try {
		$statusOutput = @(& git -C $RepoRoot status --short 2>$null)
		if ($LASTEXITCODE -eq 0) {
			$dirtyFiles = $statusOutput.Count
		}
	} catch {
	}

	return [pscustomobject]@{
		Branch = $branch
		Commit = $commit
		DirtyFiles = $dirtyFiles
	}
}

function Search-ReleaseGateLogIssues([string]$Path, [string]$Kind) {
	$issues = New-Object System.Collections.Generic.List[string]
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return ,$issues
	}

	foreach ($line in Get-Content -LiteralPath $Path) {
		if ($Kind -eq "Build" -and $line -match "(?i):\s*(warning|error)\b") {
			$issues.Add("Build log issue: $line")
			continue
		}
		if ($Kind -eq "Test" -and ($line -match "^FAIL\s+" -or $line -match "^RESULT\s+passed=\d+\s+failed=([1-9]\d*)")) {
			$issues.Add("Test log issue: $line")
		}
	}
	return ,$issues
}

function Get-ReleaseGateTestResultSummary([string]$Path, [bool]$Skipped) {
	if ($Skipped) {
		return "Skipped"
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return "Unavailable"
	}

	foreach ($line in Get-Content -LiteralPath $Path) {
		if ($line -match "^RESULT\s+passed=(\d+)\s+failed=(\d+)") {
			return "passed=$($Matches[1]) failed=$($Matches[2])"
		}
	}
	return "Unavailable"
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

	if ((ConvertTo-ReleaseGateEntryPath "\tools\Update-MetaCompanionData.ps1") -ne "tools/Update-MetaCompanionData.ps1") { throw "Package paths should be normalized." }
	if (-not (Get-ReleaseGateBlockedPackageReason "tools/Update-MetaCompanionData.ps1")) { throw "Tool script package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "Premium/Meta/latest/cache.json")) { throw "Premium package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "local_meta_environment.tsv")) { throw "Local meta package block failed." }
	if (Get-ReleaseGateBlockedPackageReason "MetaCompanion.dll") { throw "DLL should be allowed." }
	if (Get-ReleaseGateBlockedPackageReason "docs/RELEASE-CHECKLIST.md") { throw "Docs should be allowed." }

	if (Test-ReleaseGateRepoScanPath "packages/Microsoft.Net.Compilers.4.2.0/tools/csc.exe") { throw "Repo scan fallback should skip packages." }
	if (Test-ReleaseGateRepoScanPath ".idea/workspace.xml") { throw "Repo scan fallback should skip IDE state." }
	if (Test-ReleaseGateRepoScanPath "artifacts/release-gate/report.md") { throw "Repo scan fallback should skip artifacts." }
	if (-not (Test-ReleaseGateRepoScanPath "tools/Invoke-ReleaseGate.ps1")) { throw "Repo scan fallback should include source tools." }
	if (-not (Test-ReleaseGateRepoScanPath "dist/Install-MetaCompanion.ps1")) { throw "Repo scan fallback should include tracked dist installer scripts." }
	if (-not (Test-ReleaseGateRepoScanPath "dist/Wait-AndInstall-MetaCompanion.ps1")) { throw "Repo scan fallback should include tracked dist installer scripts." }
	if (Test-ReleaseGateRepoScanPath "dist/MetaCompanion.dll") { throw "Repo scan fallback should skip dist build outputs." }
	if (Test-ReleaseGateRepoScanPath "dist/MetaCompanion-community.zip") { throw "Repo scan fallback should skip dist build outputs." }

	$logSelfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ("MetaCompanionReleaseGateLogTest-" + [guid]::NewGuid().ToString("N"))
	try {
		New-Item -ItemType Directory -Force -Path $logSelfTestRoot | Out-Null
		$buildLog = Join-Path $logSelfTestRoot "build.log"
		$testLog = Join-Path $logSelfTestRoot "tests.log"
		Set-Content -LiteralPath $buildLog -Value "MetaCompanion.cs(1,1): warning CS0168: sample" -Encoding UTF8
		if ((Search-ReleaseGateLogIssues $buildLog "Build").Count -eq 0) { throw "Build log warnings should be flagged." }
		Set-Content -LiteralPath $testLog -Value @("PASS ExampleTest.WarningName_IsAllowed", "RESULT passed=1 failed=0") -Encoding UTF8
		if ((Search-ReleaseGateLogIssues $testLog "Test").Count -ne 0) { throw "Passing test names should not be log issues." }
		if ((Get-ReleaseGateTestResultSummary $testLog $false) -ne "passed=1 failed=0") { throw "Test result summary should parse." }
		if ((Get-ReleaseGateTestResultSummary $testLog $true) -ne "Skipped") { throw "Skipped test result summary should be explicit." }
		Set-Content -LiteralPath $testLog -Value "PASS ExampleTest.NoSummary" -Encoding UTF8
		if ((Get-ReleaseGateTestResultSummary $testLog $false) -ne "Unavailable") { throw "Missing test result summary should be unavailable." }
		Set-Content -LiteralPath $testLog -Value "RESULT passed=1 failed=1" -Encoding UTF8
		if ((Search-ReleaseGateLogIssues $testLog "Test").Count -eq 0) { throw "Failed test summaries should be flagged." }
	} finally {
		if (Test-Path -LiteralPath $logSelfTestRoot) {
			Remove-Item -LiteralPath $logSelfTestRoot -Recurse -Force
		}
	}

	Write-Host "SELFTEST OK"
}

if ($SelfTest) {
	Invoke-ReleaseGateSelfTest
	return
}

$repoRoot = Get-ReleaseGateRepoRoot
$runStarted = Get-Date
$runId = $runStarted.ToString("yyyyMMdd-HHmmss")
$runDirectory = Join-Path $ArtifactsDirectory $runId
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$runDirectory = Resolve-ReleaseGateExistingPath $runDirectory
$resolvedArtifactsDirectory = Resolve-ReleaseGateExistingPath $ArtifactsDirectory
$resolvedSolutionPath = Format-ReleaseGateInputPath $SolutionPath "Not provided"
$requestedPackagePath = Format-ReleaseGateInputPath $PackagePath "Not provided (auto-create)"
$buildLogPath = Join-Path $runDirectory "build.log"
$testLogPath = Join-Path $runDirectory "tests.log"

$failures = New-Object System.Collections.Generic.List[string]
$report = New-Object System.Collections.Generic.List[string]
$buildDll = Join-Path $repoRoot "MetaCompanion\bin\Release\MetaCompanion.dll"
$resolvedPackagePath = $PackagePath
$csc = $null
$hdtAppPath = $null
$msbuildPath = $null
$testPowerShellPath = $null
$gitInfo = Get-ReleaseGateGitInfo $repoRoot

try {
	if (-not $SkipBuild) {
		$csc = Find-ReleaseGateRoslynCompiler $CscToolPath $repoRoot
		$hdtAppPath = Resolve-ReleaseGateHdtAppPath
		$msbuildPath = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
		Invoke-ReleaseGateCommand "Release AnyCPU build" $msbuildPath @(
			(Resolve-Path $SolutionPath).Path,
			"/p:Configuration=Release",
			"/p:Platform=AnyCPU",
			"/p:HdtAppPath=$hdtAppPath",
			"/p:CscToolPath=$(Split-Path -Parent $csc)",
			"/p:CscToolExe=csc.exe",
			"/p:LangVersion=latest",
			"/m",
			"/v:minimal"
		) $buildLogPath | Out-Null
	}
	if ((-not $SkipBuild -or [string]::IsNullOrWhiteSpace($resolvedPackagePath)) -and
		-not (Test-Path -LiteralPath $buildDll)) {
		throw "Release DLL was not found: $buildDll"
	}
	if (-not $SkipTests) {
		$testPowerShellPath = Get-ReleaseGateNativePowerShellPath
		Invoke-ReleaseGateCommand "MSTest reflection runner" $testPowerShellPath @(
			"-NoProfile",
			"-ExecutionPolicy",
			"Bypass",
			"-File",
			(Join-Path $repoRoot "tools\Run-Tests.ps1")
		) $testLogPath | Out-Null
	}
} catch {
	$failures.Add($_.Exception.Message)
}

$buildLogIssues = Search-ReleaseGateLogIssues $buildLogPath "Build"
$testLogIssues = Search-ReleaseGateLogIssues $testLogPath "Test"
$testResultSummary = Get-ReleaseGateTestResultSummary $testLogPath $SkipTests.IsPresent
foreach ($issue in $buildLogIssues) {
	$failures.Add($issue)
}
foreach ($issue in $testLogIssues) {
	$failures.Add($issue)
}
if (-not $SkipTests -and $testResultSummary -eq "Unavailable") {
	$failures.Add("Test result summary was not found in test log.")
}

$repoSecretScan = Search-ReleaseGateRepoSecrets $repoRoot
$repoSecretMatches = $repoSecretScan.Matches
foreach ($match in $repoSecretMatches) {
	$failures.Add("Sensitive value in tracked file: $($match.Path) ($($match.Reason))")
}
$packageSecretMatches = New-Object System.Collections.Generic.List[object]
$packageSecretScannedFiles = 0

if ([string]::IsNullOrWhiteSpace($resolvedPackagePath) -and (Test-Path -LiteralPath $buildDll)) {
	try {
		$resolvedPackagePath = New-ReleaseGateCommunityPackage $repoRoot $buildDll $runDirectory
	} catch {
		$failures.Add("Community package creation failed: $($_.Exception.Message)")
	}
}

$packageEntries = @()
$blockedPackageEntries = New-Object System.Collections.Generic.List[object]
if (-not [string]::IsNullOrWhiteSpace($resolvedPackagePath) -and (Test-Path -LiteralPath $resolvedPackagePath)) {
	$resolvedPackagePath = Resolve-ReleaseGateExistingPath $resolvedPackagePath
	$packageEntries = @(Get-ReleaseGatePackageEntries $resolvedPackagePath)
	foreach ($entry in $packageEntries) {
		$reason = Get-ReleaseGateBlockedPackageReason $entry
		if ($reason) {
			$blockedPackageEntries.Add([pscustomobject]@{ Path = $entry; Reason = $reason })
			$failures.Add("Blocked package entry: $entry ($reason)")
		}
	}
	$packageSecretScan = Search-ReleaseGatePackageSecrets $resolvedPackagePath
	$packageSecretScannedFiles = $packageSecretScan.ScannedFileCount
	foreach ($match in $packageSecretScan.Matches) {
		$packageSecretMatches.Add($match)
		$failures.Add("Sensitive value in package: $($match.Path) ($($match.Reason))")
	}
} else {
	$failures.Add("Package path was not provided and a package could not be created.")
}

$runFinished = Get-Date
$durationSeconds = [int][Math]::Ceiling(($runFinished - $runStarted).TotalSeconds)

$report.Add("# Meta Companion Release Gate")
$report.Add("")
$report.Add("- Run: $runId")
$report.Add("- Started: $($runStarted.ToString("o"))")
$report.Add("- Finished: $($runFinished.ToString("o"))")
$report.Add("- Duration seconds: $durationSeconds")
$report.Add("- Repo: $repoRoot")
$report.Add("- Git branch: $($gitInfo.Branch)")
$report.Add("- Git commit: $($gitInfo.Commit)")
$report.Add("- Git dirty files: $($gitInfo.DirtyFiles)")
$report.Add("- Build: " + ($(if ($SkipBuild) { "Skipped" } else { "Release AnyCPU" })))
$report.Add("- Tests: " + ($(if ($SkipTests) { "Skipped" } else { "MSTest reflection runner" })))
$report.Add("- MSBuild: " + (Format-ReleaseGateOptionalValue $msbuildPath (-not $SkipBuild)))
$report.Add("- Test PowerShell: " + (Format-ReleaseGateOptionalValue $testPowerShellPath (-not $SkipTests)))
$report.Add("- Roslyn: " + (Format-ReleaseGateOptionalValue $csc (-not $SkipBuild)))
$report.Add("- HDT app: " + (Format-ReleaseGateOptionalValue $hdtAppPath (-not $SkipBuild)))
$report.Add("- Result: " + ($(if ($failures.Count -eq 0) { "PASS" } else { "FAIL" })))
$report.Add("- Failure count: $($failures.Count)")
$report.Add("")
$report.Add("## Inputs")
$report.Add("- Solution: $resolvedSolutionPath")
$report.Add("- Artifacts directory: $resolvedArtifactsDirectory")
$report.Add("- Requested package: $requestedPackagePath")
$report.Add("- Skip build: $($SkipBuild.IsPresent)")
$report.Add("- Skip tests: $($SkipTests.IsPresent)")
$report.Add("")
$report.Add("## Logs")
$report.Add("- Build log: " + (Format-ReleaseGateLogValue $buildLogPath ($(if ($SkipBuild) { "build skipped" } else { "" }))))
$report.Add("- Build log issues: $($buildLogIssues.Count)")
$report.Add("- Test log: " + (Format-ReleaseGateLogValue $testLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Test result: $testResultSummary")
$report.Add("- Test log issues: $($testLogIssues.Count)")
$report.Add("")
$report.Add("## Build Artifact")
if ($SkipBuild) {
	$report.Add("- Build skipped")
} elseif (Test-Path -LiteralPath $buildDll) {
	$buildArtifact = Get-Item -LiteralPath $buildDll
	$hash = Get-FileHash -LiteralPath $buildDll -Algorithm SHA256
	$report.Add("- DLL: $buildDll")
	$report.Add("- Size bytes: $($buildArtifact.Length)")
	$report.Add("- SHA256: $($hash.Hash)")
} else {
	$report.Add("- DLL missing: $buildDll")
}
$report.Add("")
$report.Add("## Package")
if (-not [string]::IsNullOrWhiteSpace($resolvedPackagePath) -and (Test-Path -LiteralPath $resolvedPackagePath)) {
	$report.Add("- Path: $resolvedPackagePath")
	if (Test-Path -LiteralPath $resolvedPackagePath -PathType Container) {
		$report.Add("- Type: Directory")
		$report.Add("- Size bytes: Not applicable")
		$report.Add("- SHA256: Not applicable")
	} else {
		$packageFile = Get-Item -LiteralPath $resolvedPackagePath
		$packageHash = Get-FileHash -LiteralPath $resolvedPackagePath -Algorithm SHA256
		$report.Add("- Type: File")
		$report.Add("- Size bytes: $($packageFile.Length)")
		$report.Add("- SHA256: $($packageHash.Hash)")
	}
	$report.Add("- Entry count: $($packageEntries.Count)")
	$report.Add("- Blocked entries: $($blockedPackageEntries.Count)")
	$report.Add("- Entries:")
	foreach ($entry in $packageEntries) { $report.Add("  - $entry") }
} else {
	$report.Add("- Package missing")
}
$report.Add("")
$report.Add("## Secret Scan")
$report.Add("- Tracked-file source: $($repoSecretScan.Source)")
$report.Add("- Tracked files scanned: $($repoSecretScan.ScannedFileCount)")
$report.Add("- Tracked-file matches: $($repoSecretMatches.Count)")
$report.Add("- Package files scanned: $packageSecretScannedFiles")
$report.Add("- Package matches: $($packageSecretMatches.Count)")
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
