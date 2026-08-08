param(
	[string]$SolutionPath = "$PSScriptRoot\..\MetaCompanion.sln",
	[string]$PackagePath = "",
	[string]$ArtifactsDirectory = "$PSScriptRoot\..\artifacts\release-gate",
	[string]$CscToolPath = "",
	[string]$PythonPath = "",
	[string]$RustSolverBinaryPath = "",
	[switch]$SkipBuild,
	[switch]$SkipTests,
	[switch]$SelfTest
)

$ErrorActionPreference = "Stop"

$minimumRustCombatFixtureCount = 7
$minimumRustFullFixtureCount = 40
$minimumVisibleResponseFixtureCount = 3

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

function Resolve-ReleaseGatePythonPath([string]$PreferredPath) {
	if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
		if (Test-Path -LiteralPath $PreferredPath -PathType Leaf) {
			return (Resolve-Path -LiteralPath $PreferredPath).Path
		}
		$preferredCommand = Get-Command $PreferredPath -ErrorAction SilentlyContinue
		if ($preferredCommand) {
			return $preferredCommand.Source
		}
		throw "Python executable was not found: $PreferredPath"
	}

	foreach ($name in @("python.exe", "python")) {
		$command = Get-Command $name -ErrorAction SilentlyContinue
		if ($command) {
			return $command.Source
		}
	}
	throw "Python 3 was not found. Install Python or pass -PythonPath."
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

function Resolve-ReleaseGateFrameworkPath([string]$RepoRoot) {
	$ensureScript = Join-Path $RepoRoot "tools\Ensure-NetFxReferenceAssemblies.ps1"
	if (-not (Test-Path -LiteralPath $ensureScript -PathType Leaf)) {
		throw ".NET Framework reference assembly resolver was not found: $ensureScript"
	}

	$frameworkPath = & $ensureScript `
		-PackagesDirectory (Join-Path $RepoRoot "packages") `
		-Quiet |
		Select-Object -Last 1
	if ([string]::IsNullOrWhiteSpace($frameworkPath) -or
		-not (Test-Path -LiteralPath (Join-Path $frameworkPath "mscorlib.dll") -PathType Leaf)) {
		throw ".NET Framework 4.7.2 reference assemblies could not be resolved."
	}
	return (Resolve-Path -LiteralPath $frameworkPath).Path
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
	if ($entry -match "(^|/)tools/.*\.ps1$" -and
		$entry -notmatch "^tools/(Sync-HdtArenaAdvisorData|Sync-BlizzardCardPools|Update-AdvisorBehaviorPrior)\.ps1$") {
		return "Only the reviewed anonymized data, public card-pool, and fail-closed behavior-prior tools are allowed under tools in the community package."
	}
	if ($entry -match "(^|/)(bin|obj)/") { return "Build output directories must not be packaged." }
	if ($entry -match "(^|/)(__pycache__|\.pytest_cache|\.mypy_cache|\.ruff_cache|\.venv|venv)(/|$)" -or
		$entry -match "\.(pyc|pyo)$") { return "Python caches and virtual environments must not be packaged." }
	if ($entry -match "(^|/)hsreplay_cookie\.txt$") { return "HSReplay cookie files must not be packaged." }
	if ($entry -match "(^|/)Premium/") { return "Premium cache must not be packaged." }
	if ($entry -match "(^|/)ArenaLastDrafts\.xml$") { return "Raw HDT arena drafts contain account identity and must not be packaged." }
	if ($entry -match "\.hdtreplay$") { return "Raw HDT replay files contain player identity and must not be packaged." }
	if ($entry -match "(^|/)AdvisorData(/|$)") { return "Local advisor snapshots must not be packaged." }
	if ($entry -match "(^|/)training(?:-v2)?\.jsonl$") { return "Local advisor training observations must not be packaged." }
	if ($entry -match "(^|/)training-v2-results\.jsonl$") { return "Imported local replay results must not be packaged." }
	if ($entry -match "(^|/)behavior-v1\.jsonl$") { return "Local advisor behavior observations must not be packaged." }
	if ($entry -match "(^|/)advisor-decision-frame-v1\.jsonl$") { return "Local advisor decision-frame observations must not be packaged." }
	if ($entry -match "(^|/)decision-frame-readiness\.json$") { return "Local advisor decision-frame audit reports must not be packaged." }
	if ($entry -match "(^|/)decision-solver-coverage(?:-[^/]*)?\.json$") { return "Local decision-solver coverage reports must not be packaged." }
	if ($entry -match "(^|/)hdt-replay-import-v1\.json$") { return "Local replay import manifests must not be packaged." }
	if ($entry -match "(^|/)behavior-imitation(?:-[^/]*)?\.(jsonl|manifest\.json)$") { return "Promoted local behavior-learning artifacts must not be packaged." }
	if ($entry -match "(^|/)behavior-prior-v1(?:\.install)?\.json$" -or
		$entry -match "(^|/)(behavior-imitation-prior-v2|decision-ranker-v1|observed-policy-evaluation-v1|advisor-ordering-models-v1\.install)\.json$" -or
		$entry -match "(^|/)(behavior-prior-builds|behavior-prior-archive|advisor-model-builds|advisor-model-archive)(/|$)") {
		return "Local behavior and decision-ranking models, evaluations, manifests, builds, and archives must not be packaged."
	}
	if ($entry -match "(^|/)(behavior-outbox-v1|result-outbox-v1)(/|$)") { return "Local reliable transport outboxes must not be packaged." }
	if ($entry -match "(^|/)(match_history|hdt_opponent_history|prediction_timeline|match_corrections)\.tsv$") { return "Local match history must not be packaged." }
	if ($entry -match "(^|/)local_meta_.*\.(tsv|json)$") { return "Local meta cache must not be packaged." }
	if ($entry -match "(^|/)personal_recommendations\.(tsv|json)$") { return "Personal recommendations must not be packaged." }
	if ($entry -match "\.(pfx|publishsettings)$") { return "Certificate or publish settings must not be packaged." }
	return $null
}

function Get-ReleaseGateTextFileExtensions {
	return @(
		".config", ".cs", ".ini", ".json", ".jsonl", ".md", ".ps1", ".py",
		".toml", ".txt", ".tsv", ".xml", ".yml", ".yaml"
	)
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
	$files = & git -c core.quotepath=false -C $RepoRoot ls-files --cached --others --exclude-standard 2>$null
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

function Get-ReleaseGateRequiredPackageEntries {
	return @(
		"MetaCompanion.dll",
		"Install-MetaCompanion.ps1",
		"Wait-AndInstall-MetaCompanion.ps1",
		"tools/Sync-HdtArenaAdvisorData.ps1",
		"tools/Sync-BlizzardCardPools.ps1",
		"tools/Update-AdvisorBehaviorPrior.ps1",
		"solver/launch_solver.py",
		"solver/metacompanion_solver/__main__.py",
		"solver/metacompanion_solver/card_pool.py",
		"solver/metacompanion_solver/card_rules.py",
		"solver/metacompanion_solver/behavior_candidate_alignment.py",
		"solver/metacompanion_solver/behavior.py",
		"solver/metacompanion_solver/behavior_learning.py",
		"solver/metacompanion_solver/behavior_prior.py",
		"solver/metacompanion_solver/cli.py",
		"solver/metacompanion_solver/counterplay.py",
		"solver/metacompanion_solver/decision_frame.py",
		"solver/metacompanion_solver/decision_ranker.py",
		"solver/metacompanion_solver/decision_solver_evaluation.py",
		"solver/metacompanion_solver/evaluation.py",
		"solver/metacompanion_solver/hdt_rule_evaluation.py",
		"solver/metacompanion_solver/hdt_replay_behavior.py",
		"solver/metacompanion_solver/observed_policy_evaluation.py",
		"solver/metacompanion_solver/rust_worker_client.py",
		"solver/metacompanion_solver/visible_response_evaluation.py",
		"solver/metacompanion_solver/search.py",
		"solver/metacompanion_solver/trajectory.py",
		"solver/metacompanion_solver/verification.py",
		"solver/metacompanion_solver/turnpair_evaluation.py",
		"solver/metacompanion_solver/rules_data/hdt-visible-point-effects-v1.json",
		"solver/fixtures/oracle-hdt-cardrules-v1.json",
		"solver/fixtures/oracle-turn-v1.json",
		"solver/fixtures/oracle-turnpair-v1.json",
		"solver/fixtures/visible-response-v1.json",
		"solver/fixtures/trajectory-readiness-policy-v1.json",
		"solver/fixtures/trajectory-readiness-v1.jsonl",
		"solver/fixtures/behavior-learning-readiness-policy-v1.json",
		"solver/fixtures/behavior-learning-readiness-v1.jsonl",
		"solver/fixtures/behavior-candidate-alignment-policy-v1.json",
		"solver/fixtures/behavior-prior-readiness-policy-v1.json",
		"solver/fixtures/behavior-prior-readiness-v1.jsonl",
		"solver/fixtures/behavior-prior-readiness-v1.manifest.json",
		"solver/tools/observed_policy_fixture.py",
		"docs/ADVISOR-DATA.md",
		"docs/OFFICIAL-CARD-POOLS.md"
	)
}

function Get-ReleaseGateMissingPackageEntries([string[]]$Entries) {
	$present = @{}
	foreach ($entry in $Entries) {
		$present[(ConvertTo-ReleaseGateEntryPath $entry)] = $true
	}
	return @(
		Get-ReleaseGateRequiredPackageEntries |
			Where-Object { -not $present.ContainsKey($_) }
	)
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

function Copy-ReleaseGateRequiredFile(
	[string]$Source,
	[string]$Destination,
	[string]$Description
) {
	if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
		throw "Required $Description was not found: $Source"
	}
	$parent = Split-Path -Parent $Destination
	if (-not [string]::IsNullOrWhiteSpace($parent)) {
		New-Item -ItemType Directory -Force -Path $parent | Out-Null
	}
	Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function New-ReleaseGateCommunityPackage(
	[string]$RepoRoot,
	[string]$BuildDll,
	[string]$OutputDirectory,
	[string]$PromotedRustSolverBinary = ""
) {
	$packageRoot = Join-Path $OutputDirectory "package-root"
	$zipPath = Join-Path $OutputDirectory "MetaCompanion-community.zip"
	if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
	if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
	New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
	Copy-Item -LiteralPath $BuildDll -Destination (Join-Path $packageRoot "MetaCompanion.dll") -Force
	foreach ($name in @("Install-MetaCompanion.ps1", "Wait-AndInstall-MetaCompanion.ps1")) {
		$source = Join-Path (Join-Path $RepoRoot "dist") $name
		Copy-ReleaseGateRequiredFile $source (Join-Path $packageRoot $name) "installer"
	}
	$arenaToolName = "Sync-HdtArenaAdvisorData.ps1"
	Copy-ReleaseGateRequiredFile `
		(Join-Path (Join-Path $RepoRoot "tools") $arenaToolName) `
		(Join-Path (Join-Path $packageRoot "tools") $arenaToolName) `
		"arena advisor data exporter"
	$officialPoolToolName = "Sync-BlizzardCardPools.ps1"
	Copy-ReleaseGateRequiredFile `
		(Join-Path (Join-Path $RepoRoot "tools") $officialPoolToolName) `
		(Join-Path (Join-Path $packageRoot "tools") $officialPoolToolName) `
		"official Blizzard card pool sync tool"
	$behaviorPriorToolName = "Update-AdvisorBehaviorPrior.ps1"
	Copy-ReleaseGateRequiredFile `
		(Join-Path (Join-Path $RepoRoot "tools") $behaviorPriorToolName) `
		(Join-Path (Join-Path $packageRoot "tools") $behaviorPriorToolName) `
		"fail-closed behavior-prior update tool"
	$solverSource = Join-Path $RepoRoot "solver"
	$solverTarget = Join-Path $packageRoot "solver"
	if (-not (Test-Path -LiteralPath (Join-Path $solverSource "launch_solver.py") -PathType Leaf)) {
		throw "Required advisor solver was not found: $solverSource"
	}
	$excludedSolverDirectories = @(
		"__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache",
		".venv", "venv", "data", "build", "dist", "tests"
	)
	foreach ($file in Get-ChildItem -LiteralPath $solverSource -Recurse -File) {
		$relative = $file.FullName.Substring($solverSource.Length).TrimStart("\", "/")
		$parts = $relative -split "[\\/]"
		if (@($parts | Where-Object { $excludedSolverDirectories -contains $_ }).Count -gt 0 -or
			$file.Extension -in @(".pyc", ".pyo")) {
			continue
		}
		$target = Join-Path $solverTarget $relative
		New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
		Copy-Item -LiteralPath $file.FullName -Destination $target -Force
	}
	if (-not [string]::IsNullOrWhiteSpace($PromotedRustSolverBinary)) {
		if (-not (Test-Path -LiteralPath $PromotedRustSolverBinary -PathType Leaf)) {
			throw "Promoted Rust solver binary was not found: $PromotedRustSolverBinary"
		}
		Copy-Item `
			-LiteralPath $PromotedRustSolverBinary `
			-Destination (Join-Path $solverTarget "metacompanion-solver.exe") `
			-Force
	}
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
	Copy-ReleaseGateRequiredFile `
		(Join-Path (Join-Path $RepoRoot "docs") "ADVISOR-DATA.md") `
		(Join-Path $docsTarget "ADVISOR-DATA.md") `
		"arena advisor data contract"
	Copy-ReleaseGateRequiredFile `
		(Join-Path (Join-Path $RepoRoot "docs") "OFFICIAL-CARD-POOLS.md") `
		(Join-Path $docsTarget "OFFICIAL-CARD-POOLS.md") `
		"official card pool data contract"
	Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -Force
	$entries = @(Get-ReleaseGatePackageEntries $zipPath)
	foreach ($missingEntry in Get-ReleaseGateMissingPackageEntries $entries) {
		throw "Community package is missing required entry: $missingEntry"
	}
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

function Read-ReleaseGateUtf8Json([string]$Path) {
	$text = [System.IO.File]::ReadAllText(
		$Path,
		[System.Text.Encoding]::UTF8
	)
	return $text | ConvertFrom-Json
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

function Get-ReleaseGateHdtRuleEvaluationSummary([string]$Path, [bool]$Skipped) {
	if ($Skipped) {
		return "Skipped"
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return "Unavailable"
	}

	try {
		$evaluation = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
		$requiredMetrics = @(
			"top1_rate",
			"top3_rate",
			"friendly_action_legality_rate",
			"response_action_legality_rate",
			"mean_minimax_regret",
			"max_minimax_regret",
			"false_safe_count",
			"false_exact_count",
			"rule_provenance_failure_count",
			"abstain_violation_count",
			"fixture_contract_failure_count",
			"latency_p95_ms"
		)
		if ($evaluation.passed -isnot [bool] -or $null -eq $evaluation.metrics) {
			return "Invalid report"
		}
		foreach ($metric in $requiredMetrics) {
			if ($evaluation.metrics.PSObject.Properties.Name -notcontains $metric -or
				$null -eq $evaluation.metrics.$metric) {
				return "Invalid report"
			}
		}

		$result = if ($evaluation.passed) { "PASS" } else { "FAIL" }
		return "$result (Top1=$($evaluation.metrics.top1_rate), Top3=$($evaluation.metrics.top3_rate), friendly-legal=$($evaluation.metrics.friendly_action_legality_rate), response-legal=$($evaluation.metrics.response_action_legality_rate), mean-regret=$($evaluation.metrics.mean_minimax_regret), max-regret=$($evaluation.metrics.max_minimax_regret), false-safe=$($evaluation.metrics.false_safe_count), false-exact=$($evaluation.metrics.false_exact_count), provenance=$($evaluation.metrics.rule_provenance_failure_count), abstain=$($evaluation.metrics.abstain_violation_count), fixture-contract=$($evaluation.metrics.fixture_contract_failure_count), P95=$($evaluation.metrics.latency_p95_ms)ms)"
	} catch {
		return "Invalid report"
	}
}

function Test-ReleaseGateJsonNumber([object]$Value) {
	return (
		$Value -is [byte] -or
		$Value -is [sbyte] -or
		$Value -is [int16] -or
		$Value -is [uint16] -or
		$Value -is [int32] -or
		$Value -is [uint32] -or
		$Value -is [int64] -or
		$Value -is [uint64] -or
		$Value -is [single] -or
		$Value -is [double] -or
		$Value -is [decimal]
	)
}

function Test-ReleaseGateFiniteJsonNumber([object]$Value) {
	if (-not (Test-ReleaseGateJsonNumber $Value)) {
		return $false
	}
	$number = [double]$Value
	return -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)
}

function Test-ReleaseGatePositiveJsonInteger([object]$Value, [bool]$AllowZero = $false) {
	if (-not (Test-ReleaseGateFiniteJsonNumber $Value)) {
		return $false
	}
	$number = [double]$Value
	$minimum = if ($AllowZero) { 0 } else { 1 }
	return $number -ge $minimum -and [Math]::Floor($number) -eq $number
}

function Test-ReleaseGateExactJsonProperties([object]$Value, [string[]]$Expected) {
	if ($null -eq $Value) {
		return $false
	}
	$actualNames = @($Value.PSObject.Properties.Name | Sort-Object)
	$expectedNames = @($Expected | Sort-Object)
	if ($actualNames.Count -ne $expectedNames.Count) {
		return $false
	}
	return @(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames).Count -eq 0
}

function Get-ReleaseGateTrajectoryAuditorFixtureSummary([string]$Path, [bool]$Skipped) {
	if ($Skipped) {
		return "Skipped"
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return "Unavailable"
	}

	$expectedCaveat = "Training-ready means only that anonymized trajectories satisfy this versioned join, exact-action, replay, split, and privacy contract. It does not prove that the online solver is healthy or optimal, that labels are unbiased, or that an RL policy exists; solver_runtime_ready reports online solve health separately."
	try {
		$audit = [System.IO.File]::ReadAllText(
			$Path,
			[System.Text.Encoding]::UTF8
		) | ConvertFrom-Json
		if ($audit.schema -ne "trajectory-readiness-report-v1" -or
			$audit.trajectory_schema -ne "trajectory-readiness-v1") {
			return "Invalid report (schema mismatch)"
		}
		if ($audit.source_kind -ne "synthetic_fixture") {
			return "Invalid report (fixture source kind mismatch)"
		}
		if ($audit.input_sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
			$audit.policy_sha256 -notmatch '^[0-9a-fA-F]{64}$') {
			return "Invalid report (input or policy SHA256 missing)"
		}
		if (-not (Test-ReleaseGateJsonNumber $audit.input_bytes) -or
			[double]$audit.input_bytes -le 0 -or
			[Math]::Floor([double]$audit.input_bytes) -ne [double]$audit.input_bytes) {
			return "Invalid report (input bytes missing)"
		}
		foreach ($property in @("contract_passed", "training_ready", "passed")) {
			if ($audit.$property -isnot [bool]) {
				return "Invalid report ($property must be boolean)"
			}
		}
		if (($audit.training_ready -and -not $audit.contract_passed) -or
			$audit.passed -ne $audit.training_ready) {
			return "Invalid report (readiness flags are inconsistent)"
		}
		if ($audit.caveat -ne $expectedCaveat) {
			return "Invalid report (required caveat mismatch)"
		}
		if ($audit.solve_status_semantics.schema -ne "solve-status-semantics-v1" -or
			(@($audit.solve_status_semantics.policy_buckets) -join ",") -cne
				"ok,partial,cancelled,unsupported,non_ok" -or
			(@($audit.solve_status_semantics.non_ok_members) -join ",") -cne
				"error,other" -or
			(@($audit.solve_status_semantics.unsuccessful_members) -join ",") -cne
				"partial,cancelled,unsupported,error,other") {
			return "Invalid report (solve status semantics mismatch)"
		}
		if ($null -eq $audit.metrics) {
			return "Invalid report (metrics missing)"
		}

		$countMetrics = @(
			"record_count",
			"invalid_json_or_record_count",
			"contract_issue_count",
			"privacy_violation_count",
			"solve_record_count",
			"ok_solve_count",
			"partial_solve_count",
			"cancelled_solve_count",
			"unsupported_solve_count",
			"error_solve_count",
			"other_solve_count",
			"non_ok_solve_count",
			"unsuccessful_solve_count",
			"unique_game_count",
			"canonical_decision_count",
			"action_observation_count",
			"exact_action_count",
			"replayable_transition_count",
			"candidate_transition_count",
			"candidate_evidence_consistent_count",
			"candidate_boundary_failure_count",
			"candidate_state_binding_failure_count",
			"candidate_state_hash_mismatch_count",
			"candidate_snapshot_sequence_mismatch_count",
			"candidate_state_order_failure_count",
			"terminal_result_game_count",
			"joined_decision_count",
			"duplicate_solve_count",
			"conflicting_final_solve_count",
			"state_content_conflict_count",
			"cross_game_state_id_reuse_count",
			"conflicting_result_game_count",
			"duplicate_result_observation_count",
			"replay_failure_count",
			"duplicate_action_sequence_count",
			"non_contiguous_action_sequence_game_count",
			"action_order_violation_count",
			"action_chain_break_count",
			"action_decision_join_failure_count",
			"pre_state_order_violation_count",
			"post_state_order_violation_count",
			"terminal_before_last_action_count",
			"terminal_state_mismatch_count",
			"split_assignment_mismatch_count",
			"cross_split_leakage_count"
		)
		$rateMetrics = @(
			"solve_result_join_rate",
			"exact_action_rate",
			"replayable_transition_rate",
			"partial_action_rate",
			"ok_solve_rate",
			"partial_solve_rate",
			"cancelled_solve_rate",
			"unsupported_solve_rate",
			"error_solve_rate",
			"other_solve_rate",
			"non_ok_solve_rate",
			"unsuccessful_solve_rate"
		)
		foreach ($metric in $countMetrics) {
			if ($audit.metrics.PSObject.Properties.Name -notcontains $metric -or
				-not (Test-ReleaseGateJsonNumber $audit.metrics.$metric)) {
				return "Invalid report (missing or non-numeric metric: $metric)"
			}
			$value = [double]$audit.metrics.$metric
			if ($value -lt 0 -or [Math]::Floor($value) -ne $value) {
				return "Invalid report (count metric must be a non-negative integer: $metric)"
			}
		}
		foreach ($metric in $rateMetrics) {
			if ($audit.metrics.PSObject.Properties.Name -notcontains $metric -or
				-not (Test-ReleaseGateJsonNumber $audit.metrics.$metric)) {
				return "Invalid report (missing or non-numeric metric: $metric)"
			}
			$value = [double]$audit.metrics.$metric
			if ($value -lt 0 -or $value -gt 1) {
				return "Invalid report (rate metric must be between zero and one: $metric)"
			}
		}
		$statusCountTotal = [double]$audit.metrics.ok_solve_count +
			[double]$audit.metrics.partial_solve_count +
			[double]$audit.metrics.cancelled_solve_count +
			[double]$audit.metrics.unsupported_solve_count +
			[double]$audit.metrics.error_solve_count +
			[double]$audit.metrics.other_solve_count
		if ($statusCountTotal -ne [double]$audit.metrics.solve_record_count -or
			[double]$audit.metrics.non_ok_solve_count -ne
				([double]$audit.metrics.error_solve_count + [double]$audit.metrics.other_solve_count) -or
			[double]$audit.metrics.unsuccessful_solve_count -ne
				([double]$audit.metrics.solve_record_count - [double]$audit.metrics.ok_solve_count) -or
			[double]$audit.metrics.exact_action_count -gt [double]$audit.metrics.action_observation_count -or
			[double]$audit.metrics.replayable_transition_count -gt [double]$audit.metrics.exact_action_count -or
			[double]$audit.metrics.candidate_transition_count -gt [double]$audit.metrics.action_observation_count -or
			[double]$audit.metrics.candidate_evidence_consistent_count -gt [double]$audit.metrics.candidate_transition_count -or
			[double]$audit.metrics.joined_decision_count -gt [double]$audit.metrics.canonical_decision_count -or
			[double]$audit.metrics.terminal_result_game_count -gt [double]$audit.metrics.unique_game_count) {
			return "Invalid report (trajectory metric counts are inconsistent)"
		}
		if ($null -eq $audit.issues -or $null -eq $audit.issues.reason_counts -or
			$null -eq $audit.issues.all_reason_counts -or
			$null -eq $audit.issues.truncated_counts) {
			return "Invalid report (complete issue reason aggregation missing)"
		}
		foreach ($candidateFailureMetric in @(
			"candidate_boundary_failure_count",
			"candidate_state_binding_failure_count",
			"candidate_state_hash_mismatch_count",
			"candidate_snapshot_sequence_mismatch_count",
			"candidate_state_order_failure_count"
		)) {
			if ([double]$audit.metrics.$candidateFailureMetric -gt [double]$audit.metrics.candidate_transition_count) {
				return "Invalid report ($candidateFailureMetric exceeds candidate count)"
			}
		}

		$verifiedTransitions = @($audit.verified_transitions)
		if ($verifiedTransitions.Count -ne [int]$audit.metrics.replayable_transition_count) {
			return "Invalid report (verified transition allowlist count mismatch)"
		}
		foreach ($transition in $verifiedTransitions) {
			if ($null -eq $transition -or
				$transition.game_id -notmatch '^anon-[0-9a-f]{16}$' -or
				-not (Test-ReleaseGateJsonNumber $transition.action_sequence) -or
				[double]$transition.action_sequence -lt 1 -or
				[string]::IsNullOrWhiteSpace([string]$transition.pre_state_id) -or
				[string]::IsNullOrWhiteSpace([string]$transition.post_state_id) -or
				$transition.normalized_pre_state_hash -notmatch '^[0-9a-f]{64}$' -or
				$transition.normalized_post_state_hash -notmatch '^[0-9a-f]{64}$') {
				return "Invalid report (verified transition allowlist entry is malformed)"
			}
		}

		$integrityAnomalyCount = 0
		foreach ($metric in @(
			"invalid_json_or_record_count",
			"contract_issue_count",
			"privacy_violation_count",
			"duplicate_solve_count",
			"conflicting_final_solve_count",
			"state_content_conflict_count",
			"cross_game_state_id_reuse_count",
			"conflicting_result_game_count",
			"duplicate_result_observation_count",
			"replay_failure_count",
			"duplicate_action_sequence_count",
			"non_contiguous_action_sequence_game_count",
			"action_order_violation_count",
			"action_chain_break_count",
			"action_decision_join_failure_count",
			"pre_state_order_violation_count",
			"post_state_order_violation_count",
			"terminal_before_last_action_count",
			"terminal_state_mismatch_count",
			"split_assignment_mismatch_count",
			"cross_split_leakage_count"
		)) {
			$integrityAnomalyCount += [int64]$audit.metrics.$metric
		}
		if ($audit.contract_passed -and $integrityAnomalyCount -ne 0) {
			return "Invalid report (contract flag contradicts integrity metrics)"
		}

		$result = if ($audit.passed -and $audit.contract_passed -and $audit.training_ready) {
			"PASS"
		} else {
			"FAIL"
		}
		return "$result (auditor-fixture, source=synthetic_fixture, contract=$($audit.contract_passed), fixture-ready=$($audit.training_ready), input-bytes=$($audit.input_bytes), input-sha256=$($audit.input_sha256), policy-sha256=$($audit.policy_sha256), records=$($audit.metrics.record_count), games=$($audit.metrics.unique_game_count), decisions=$($audit.metrics.canonical_decision_count), terminal-games=$($audit.metrics.terminal_result_game_count), solves=$($audit.metrics.solve_record_count), ok=$($audit.metrics.ok_solve_count), partial-solves=$($audit.metrics.partial_solve_count), cancelled=$($audit.metrics.cancelled_solve_count), unsupported=$($audit.metrics.unsupported_solve_count), errors=$($audit.metrics.error_solve_count), non-ok=$($audit.metrics.non_ok_solve_count), unsuccessful-rate=$($audit.metrics.unsuccessful_solve_rate), joined-decisions=$($audit.metrics.joined_decision_count), actions=$($audit.metrics.action_observation_count), exact-actions=$($audit.metrics.exact_action_count), replayable=$($audit.metrics.replayable_transition_count), candidates=$($audit.metrics.candidate_transition_count), candidate-consistent=$($audit.metrics.candidate_evidence_consistent_count), integrity-anomalies=$integrityAnomalyCount, caveat=verified)"
	} catch {
		return "Invalid report"
	}
}

function Get-ReleaseGateBehaviorLearningFixtureSummary([string]$Path, [bool]$Skipped) {
	if ($Skipped) {
		return "Skipped"
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or
		-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return "Unavailable"
	}

	try {
		$audit = [System.IO.File]::ReadAllText(
			$Path,
			[System.Text.Encoding]::UTF8
		) | ConvertFrom-Json
		if ($audit.schema -ne "behavior-learning-readiness-report-v1" -or
			$audit.behavior_schema -ne "advisor-behavior-v1" -or
			$audit.trajectory_schema -ne "trajectory-readiness-v1" -or
			$audit.source_kind -ne "synthetic_fixture") {
			return "Invalid report (schema or source kind mismatch)"
		}
		if ($audit.behavior_input -ne "behavior-learning-readiness-v1.jsonl" -or
			$audit.trajectory_input -ne "trajectory-readiness-v1.jsonl") {
			return "Invalid report (fixture input names mismatch)"
		}
		foreach ($property in @(
			"contract_passed",
			"imitation_ready",
			"rl_training_ready",
			"passed"
		)) {
			if ($audit.$property -isnot [bool]) {
				return "Invalid report ($property must be boolean)"
			}
		}
		if (-not $audit.contract_passed -or -not $audit.imitation_ready -or
			$audit.rl_training_ready -or -not $audit.passed) {
			return "FAIL (behavior learning fixture was not imitation-ready)"
		}
		foreach ($property in @(
			"behavior_input_sha256",
			"trajectory_input_sha256",
			"policy_sha256"
		)) {
			if ($audit.$property -notmatch '^[0-9a-fA-F]{64}$') {
				return "Invalid report ($property missing)"
			}
		}
		foreach ($property in @("behavior_input_bytes", "trajectory_input_bytes")) {
			if (-not (Test-ReleaseGateJsonNumber $audit.$property) -or
				[double]$audit.$property -le 0 -or
				[Math]::Floor([double]$audit.$property) -ne [double]$audit.$property) {
				return "Invalid report ($property must be a positive integer)"
			}
		}
		$countMetrics = @(
			"behavior_record_count",
			"behavior_invalid_record_count",
			"unique_behavior_game_count",
			"behavior_eligible_record_count",
			"local_eligible_record_count",
			"opponent_eligible_record_count",
			"board_position_record_count",
			"choice_item_count",
			"offered_choice_entity_count",
			"selected_choice_entity_count",
			"replay_behavior_record_count",
			"replay_play_card_record_count",
			"replay_play_source_still_actor_hand_post_count",
			"replay_play_source_still_actor_hand_post_affected_game_count",
			"replay_attack_record_count",
			"replay_attack_source_readiness_explicit_count",
			"replay_end_turn_record_count",
			"replay_end_turn_active_player_unchanged_count",
			"distinct_action_kind_count",
			"both_side_game_count",
			"terminal_result_game_count",
			"joined_result_game_count",
			"joined_behavior_record_count",
			"joined_behavior_eligible_record_count",
			"behavior_without_result_game_count",
			"conflicting_result_game_count",
			"duplicate_result_observation_count",
			"sequence_order_violation_count",
			"timestamp_regression_count"
		)
		$rateMetrics = @(
			"result_join_rate",
			"both_side_game_rate",
			"behavior_eligible_rate",
			"unknown_actor_rate",
			"unknown_identity_rate",
			"replay_play_source_left_actor_hand_post_rate",
			"replay_attack_source_readiness_explicit_rate"
		)
		foreach ($metric in $countMetrics) {
			if ($audit.metrics.PSObject.Properties.Name -notcontains $metric -or
				-not (Test-ReleaseGateJsonNumber $audit.metrics.$metric) -or
				[double]$audit.metrics.$metric -lt 0 -or
				[Math]::Floor([double]$audit.metrics.$metric) -ne
					[double]$audit.metrics.$metric) {
				return "Invalid report (invalid behavior metric: $metric)"
			}
		}
		foreach ($metric in $rateMetrics) {
			if ($audit.metrics.PSObject.Properties.Name -notcontains $metric -or
				-not (Test-ReleaseGateJsonNumber $audit.metrics.$metric) -or
				[double]$audit.metrics.$metric -lt 0 -or
				[double]$audit.metrics.$metric -gt 1) {
				return "Invalid report (invalid behavior rate: $metric)"
			}
		}
		if ([double]$audit.metrics.behavior_eligible_record_count -gt
				[double]$audit.metrics.behavior_record_count -or
			[double]$audit.metrics.joined_behavior_eligible_record_count -gt
				[double]$audit.metrics.behavior_eligible_record_count -or
			[double]$audit.metrics.joined_result_game_count -gt
				[double]$audit.metrics.unique_behavior_game_count -or
			[double]$audit.metrics.replay_attack_source_readiness_explicit_count -gt
				[double]$audit.metrics.replay_attack_record_count) {
			return "Invalid report (behavior learning metric counts are inconsistent)"
		}
		foreach ($metric in @(
			"behavior_invalid_record_count",
			"behavior_without_result_game_count",
			"conflicting_result_game_count",
			"duplicate_result_observation_count",
			"sequence_order_violation_count",
			"timestamp_regression_count",
			"replay_play_source_still_actor_hand_post_count",
			"replay_end_turn_active_player_unchanged_count"
		)) {
			if ([double]$audit.metrics.$metric -ne 0) {
				return "Invalid report (fixture contains behavior integrity anomalies)"
			}
		}
		if ([double]$audit.metrics.replay_attack_source_readiness_explicit_count -ne
				[double]$audit.metrics.replay_attack_record_count) {
			return "Invalid report (replay attack readiness evidence is incomplete)"
		}
		$requiredReplayChecks = @(
			"replay_play_source_still_actor_hand_post_count",
			"replay_attack_source_readiness_missing_count",
			"replay_end_turn_active_player_unchanged_count"
		)
		foreach ($requiredCheck in $requiredReplayChecks) {
			$matches = @(
				$audit.contract_checks |
					Where-Object { [string]$_.name -ceq $requiredCheck }
			)
			if ($matches.Count -ne 1 -or
				$matches[0].passed -ne $true -or
				[string]$matches[0].operator -cne "<=" -or
				-not (Test-ReleaseGateJsonNumber $matches[0].actual) -or
				[double]$matches[0].actual -ne 0 -or
				-not (Test-ReleaseGateJsonNumber $matches[0].expected) -or
				[double]$matches[0].expected -ne 0) {
				return "Invalid report (replay transition contract check missing: $requiredCheck)"
			}
		}
		if (-not (Test-ReleaseGateJsonNumber $audit.metrics.actor_side_counts.local) -or
			-not (Test-ReleaseGateJsonNumber $audit.metrics.actor_side_counts.opponent) -or
			[double]$audit.metrics.actor_side_counts.local -lt 1 -or
			[double]$audit.metrics.actor_side_counts.opponent -lt 1) {
			return "Invalid report (fixture must cover local and opponent behavior)"
		}
		if (-not (Test-ReleaseGateJsonNumber $audit.metrics.choice_status_counts.selected) -or
			[double]$audit.metrics.choice_status_counts.selected -lt 1 -or
			[double]$audit.metrics.board_position_record_count -lt 1 -or
			[double]$audit.metrics.choice_item_count -lt 1 -or
			[double]$audit.metrics.offered_choice_entity_count -lt 2 -or
			[double]$audit.metrics.selected_choice_entity_count -lt 1 -or
			[double]$audit.metrics.selected_choice_entity_count -gt
				[double]$audit.metrics.offered_choice_entity_count) {
			return "Invalid report (fixture must cover a complete selected choice)"
		}
		if (-not $audit.behavior_audit.valid -or
			[double]$audit.behavior_audit.privacy_violation_count -ne 0 -or
			-not $audit.trajectory_contract_audit.contract_passed) {
			return "Invalid report (nested behavior or result audit failed)"
		}
		$notOptimalBehaviorCaveat = '"\u4e0d\u7b49\u4e8e\u6700\u4f18\u52a8\u4f5c"' |
			ConvertFrom-Json
		$reinforcementLearningCaveat = '"\u5f3a\u5316\u5b66\u4e60"' |
			ConvertFrom-Json
		if ($audit.caveat -notmatch [regex]::Escape($notOptimalBehaviorCaveat) -or
			$audit.caveat -notmatch [regex]::Escape($reinforcementLearningCaveat)) {
			return "Invalid report (required behavior caveat mismatch)"
		}
		return "PASS (behavior-fixture, contract=True, imitation-ready=True, rl-ready=False, records=$($audit.metrics.behavior_record_count), games=$($audit.metrics.unique_behavior_game_count), joined-games=$($audit.metrics.joined_result_game_count), eligible=$($audit.metrics.behavior_eligible_record_count), local=$($audit.metrics.actor_side_counts.local), opponent=$($audit.metrics.actor_side_counts.opponent), action-kinds=$($audit.metrics.distinct_action_kind_count), choices=$($audit.metrics.choice_item_count), offered=$($audit.metrics.offered_choice_entity_count), selected=$($audit.metrics.selected_choice_entity_count), join-rate=$($audit.metrics.result_join_rate), behavior-sha256=$($audit.behavior_input_sha256), trajectory-sha256=$($audit.trajectory_input_sha256), policy-sha256=$($audit.policy_sha256))"
	} catch {
		return "Invalid report"
	}
}

function Get-ReleaseGateBehaviorPriorFixtureSummary(
	[string]$Path,
	[bool]$Skipped,
	[string]$ExpectedDatasetPath,
	[string]$ExpectedManifestPath,
	[string]$ExpectedPolicyPath
) {
	if ($Skipped) {
		return "Skipped"
	}
	foreach ($requiredPath in @(
		$Path,
		$ExpectedDatasetPath,
		$ExpectedManifestPath,
		$ExpectedPolicyPath
	)) {
		if ([string]::IsNullOrWhiteSpace($requiredPath) -or
			-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
			return "Unavailable"
		}
	}

	try {
		$artifactText = [System.IO.File]::ReadAllText(
			$Path,
			[System.Text.Encoding]::UTF8
		)
		$artifact = $artifactText | ConvertFrom-Json
		$rootFields = @(
			"schema", "model_type", "source_dataset", "source_manifest",
			"policy", "policy_sha256", "training", "evaluation",
			"quality_checks", "imitation_training_complete",
			"search_ordering_prior_ready", "live_policy_eligible",
			"rl_training_eligible", "optimality_verified",
			"candidate_generation_allowed", "outcome_used_for_training",
			"models", "approved_uses", "prohibited_uses", "caveat"
		)
		if (-not (Test-ReleaseGateExactJsonProperties $artifact $rootFields) -or
			$artifact.schema -ne "behavior-imitation-prior-v2" -or
			$artifact.model_type -ne "hierarchical-behavior-frequency-v1") {
			return "Invalid report (behavior prior schema or fields mismatch)"
		}
		foreach ($property in @(
			"imitation_training_complete",
			"search_ordering_prior_ready",
			"live_policy_eligible",
			"rl_training_eligible",
			"optimality_verified",
			"candidate_generation_allowed",
			"outcome_used_for_training"
		)) {
			if ($artifact.$property -isnot [bool]) {
				return "Invalid report ($property must be boolean)"
			}
		}
		if (-not $artifact.imitation_training_complete -or
			-not $artifact.search_ordering_prior_ready -or
			$artifact.live_policy_eligible -or
			$artifact.rl_training_eligible -or
			$artifact.optimality_verified -or
			$artifact.candidate_generation_allowed -or
			$artifact.outcome_used_for_training) {
			return "Invalid report (behavior prior readiness or safety flags mismatch)"
		}

		$datasetFields = @(
			"name", "sha256", "bytes", "record_count", "game_count",
			"split_record_counts", "split_game_counts"
		)
		if (-not (Test-ReleaseGateExactJsonProperties $artifact.source_dataset $datasetFields) -or
			$artifact.source_dataset.sha256 -notmatch '^[0-9a-f]{64}$' -or
			-not (Test-ReleaseGatePositiveJsonInteger $artifact.source_dataset.bytes) -or
			-not (Test-ReleaseGatePositiveJsonInteger $artifact.source_dataset.record_count) -or
			-not (Test-ReleaseGatePositiveJsonInteger $artifact.source_dataset.game_count)) {
			return "Invalid report (behavior prior dataset identity missing)"
		}
		$datasetFile = Get-Item -LiteralPath $ExpectedDatasetPath
		$datasetHash = (Get-FileHash -LiteralPath $ExpectedDatasetPath -Algorithm SHA256).Hash.ToLowerInvariant()
		if ($artifact.source_dataset.name -cne $datasetFile.Name -or
			[double]$artifact.source_dataset.bytes -ne [double]$datasetFile.Length -or
			$artifact.source_dataset.sha256 -cne $datasetHash -or
			[int]$artifact.source_dataset.record_count -ne 6 -or
			[int]$artifact.source_dataset.game_count -ne 6) {
			return "Invalid report (behavior prior dataset bytes are not fixture-bound)"
		}
		foreach ($split in @("train", "validation", "test")) {
			if (-not (Test-ReleaseGatePositiveJsonInteger $artifact.source_dataset.split_record_counts.$split) -or
				-not (Test-ReleaseGatePositiveJsonInteger $artifact.source_dataset.split_game_counts.$split) -or
				[int]$artifact.source_dataset.split_record_counts.$split -ne 2 -or
				[int]$artifact.source_dataset.split_game_counts.$split -ne 2) {
				return "Invalid report (behavior prior fixture split counts mismatch)"
			}
		}

		$manifestFields = @("name", "sha256", "schema")
		$manifestFile = Get-Item -LiteralPath $ExpectedManifestPath
		$manifestHash = (Get-FileHash -LiteralPath $ExpectedManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
		if (-not (Test-ReleaseGateExactJsonProperties $artifact.source_manifest $manifestFields) -or
			$artifact.source_manifest.name -cne $manifestFile.Name -or
			$artifact.source_manifest.sha256 -cne $manifestHash -or
			$artifact.source_manifest.schema -ne "behavior-imitation-manifest-v1") {
			return "Invalid report (behavior prior manifest is not fixture-bound)"
		}

		$policyDocument = [System.IO.File]::ReadAllText(
			$ExpectedPolicyPath,
			[System.Text.Encoding]::UTF8
		) | ConvertFrom-Json
		$policyKeys = @(
			"min_train_games", "min_validation_games", "min_test_games",
			"min_train_records", "min_validation_records", "min_test_records",
			"min_validation_seen_template_records",
			"max_validation_kind_log_loss_excess",
			"max_validation_seen_template_log_loss_excess",
			"max_validation_unseen_template_rate"
		)
		if ($policyDocument.schema -ne "behavior-imitation-prior-policy-v1" -or
			-not (Test-ReleaseGateExactJsonProperties $artifact.policy $policyKeys) -or
			-not (Test-ReleaseGateExactJsonProperties $policyDocument.thresholds $policyKeys) -or
			$artifact.policy_sha256 -notmatch '^[0-9a-f]{64}$') {
			return "Invalid report (behavior prior policy contract mismatch)"
		}
		foreach ($policyKey in $policyKeys) {
			$actualPolicyValue = $artifact.policy.PSObject.Properties[$policyKey].Value
			$fixturePolicyValue = $policyDocument.thresholds.PSObject.Properties[$policyKey].Value
			if (-not (Test-ReleaseGateFiniteJsonNumber $actualPolicyValue) -or
				-not (Test-ReleaseGateFiniteJsonNumber $fixturePolicyValue) -or
				[double]$actualPolicyValue -ne [double]$fixturePolicyValue) {
				return "Invalid report (behavior prior policy value mismatch: $policyKey)"
			}
		}

		$trainingFields = @(
			"split", "record_count", "game_count", "actor_side_record_counts",
			"action_kind_record_counts", "supported_modes", "supported_patches",
			"unit_of_analysis", "game_level_split", "actor_outcome_used",
			"local_outcome_used"
		)
		if (-not (Test-ReleaseGateExactJsonProperties $artifact.training $trainingFields) -or
			$artifact.training.split -ne "train" -or
			[int]$artifact.training.record_count -ne 2 -or
			[int]$artifact.training.game_count -ne 2 -or
			$artifact.training.unit_of_analysis -ne "observed_action" -or
			$artifact.training.game_level_split -isnot [bool] -or
			-not $artifact.training.game_level_split -or
			$artifact.training.actor_outcome_used -isnot [bool] -or
			$artifact.training.actor_outcome_used -or
			$artifact.training.local_outcome_used -isnot [bool] -or
			$artifact.training.local_outcome_used -or
			[int]$artifact.training.actor_side_record_counts.local -ne 1 -or
			[int]$artifact.training.actor_side_record_counts.opponent -ne 1 -or
			[int]$artifact.training.action_kind_record_counts.attack -ne 1 -or
			[int]$artifact.training.action_kind_record_counts.play_card -ne 1 -or
			(@($artifact.training.supported_modes) -join ",") -cne "standard" -or
			(@($artifact.training.supported_patches) -join ",") -cne "fixture-patch") {
			return "Invalid report (behavior prior train-only semantics mismatch)"
		}

		$heldOutMetricNames = @(
			"kind_log_loss", "global_kind_log_loss", "kind_log_loss_excess",
			"kind_top1_accuracy", "global_kind_top1_accuracy",
			"game_macro_kind_log_loss", "game_macro_global_kind_log_loss",
			"unseen_template_rate", "seen_template_log_loss",
			"global_seen_template_log_loss", "seen_template_log_loss_excess",
			"seen_template_top1_accuracy", "global_seen_template_top1_accuracy",
			"game_macro_seen_template_log_loss",
			"game_macro_global_seen_template_log_loss"
		)
		foreach ($split in @("validation", "test")) {
			$heldOut = $artifact.evaluation.PSObject.Properties[$split].Value
			if ($heldOut.status -ne "EVALUATED" -or
				[int]$heldOut.record_count -ne 2 -or [int]$heldOut.game_count -ne 2 -or
				[int]$heldOut.seen_template_record_count -ne 2 -or
				[int]$heldOut.unseen_template_count -ne 0 -or
				[double]$heldOut.unseen_template_rate -ne 0.0 -or
				[int]$heldOut.actor_side_record_counts.local -ne 1 -or
				[int]$heldOut.actor_side_record_counts.opponent -ne 1 -or
				[int]$heldOut.action_kind_record_counts.attack -ne 1 -or
				[int]$heldOut.action_kind_record_counts.play_card -ne 1) {
				return "Invalid report (behavior prior $split metrics mismatch)"
			}
			foreach ($metricName in $heldOutMetricNames) {
				if (-not (Test-ReleaseGateFiniteJsonNumber $heldOut.$metricName)) {
					return "Invalid report (behavior prior $split metric missing: $metricName)"
				}
			}
			if ($heldOut.caveat -notmatch "not legal-action coverage" -or
				$heldOut.caveat -notmatch "optimality") {
				return "Invalid report (behavior prior held-out caveat mismatch)"
			}
		}

		$qualitySpecs = @(
			@{ Name = "train_game_count"; Operator = ">="; Policy = "min_train_games" },
			@{ Name = "validation_game_count"; Operator = ">="; Policy = "min_validation_games" },
			@{ Name = "test_game_count"; Operator = ">="; Policy = "min_test_games" },
			@{ Name = "train_record_count"; Operator = ">="; Policy = "min_train_records" },
			@{ Name = "validation_record_count"; Operator = ">="; Policy = "min_validation_records" },
			@{ Name = "test_record_count"; Operator = ">="; Policy = "min_test_records" },
			@{ Name = "validation_seen_template_record_count"; Operator = ">="; Policy = "min_validation_seen_template_records" },
			@{ Name = "validation_kind_log_loss_excess"; Operator = "<="; Policy = "max_validation_kind_log_loss_excess" },
			@{ Name = "validation_seen_template_log_loss_excess"; Operator = "<="; Policy = "max_validation_seen_template_log_loss_excess" },
			@{ Name = "validation_unseen_template_rate"; Operator = "<="; Policy = "max_validation_unseen_template_rate" }
		)
		$qualityChecks = @($artifact.quality_checks)
		if ($qualityChecks.Count -ne $qualitySpecs.Count) {
			return "Invalid report (behavior prior quality checks are incomplete)"
		}
		for ($index = 0; $index -lt $qualitySpecs.Count; $index++) {
			$quality = $qualityChecks[$index]
			$spec = $qualitySpecs[$index]
			$policyValue = $artifact.policy.PSObject.Properties[$spec.Policy].Value
			if (-not (Test-ReleaseGateExactJsonProperties $quality @("name", "actual", "operator", "expected", "passed")) -or
				$quality.name -cne $spec.Name -or $quality.operator -cne $spec.Operator -or
				-not (Test-ReleaseGateFiniteJsonNumber $quality.actual) -or
				-not (Test-ReleaseGateFiniteJsonNumber $quality.expected) -or
				[double]$quality.expected -ne [double]$policyValue -or
				$quality.passed -isnot [bool]) {
				return "Invalid report (behavior prior quality check drifted: $($spec.Name))"
			}
			$computedPass = if ($spec.Operator -eq ">=") {
				[double]$quality.actual -ge [double]$quality.expected
			} else {
				[double]$quality.actual -le [double]$quality.expected
			}
			if (-not $computedPass -or $quality.passed -ne $computedPass) {
				return "FAIL (behavior prior fixture quality check failed: $($spec.Name))"
			}
		}

		if ((@($artifact.models.action_kind.labels) -join ",") -cne
				"attack,end_turn,hero_power,location_activate,play_card" -or
			[int]$artifact.models.action_kind.counts_by_level.global.'[]'.total -ne 2 -or
			[int]$artifact.models.action_kind.counts_by_level.global.'[]'.counts.attack -ne 1 -or
			[int]$artifact.models.action_kind.counts_by_level.global.'[]'.counts.play_card -ne 1 -or
			[int]$artifact.models.action_template_by_kind.attack.counts_by_level.global.'[]'.total -ne 1 -or
			[int]$artifact.models.action_template_by_kind.hero_power.counts_by_level.global.'[]'.total -ne 0 -or
			[int]$artifact.models.action_template_by_kind.location_activate.counts_by_level.global.'[]'.total -ne 0 -or
			[int]$artifact.models.action_template_by_kind.play_card.counts_by_level.global.'[]'.total -ne 1) {
			return "Invalid report (behavior prior model counts mismatch)"
		}
		if ((@($artifact.approved_uses) -join ",") -cne
				"offline_behavior_cloning_baseline,opponent_behavior_modeling,legal_action_search_ordering_prior" -or
			(@($artifact.prohibited_uses) -join ",") -cne
				"action_generation,direct_live_policy,direct_rl_trajectory,optimal_action_ground_truth,hidden_opponent_card_reconstruction") {
			return "Invalid report (behavior prior use restrictions drifted)"
		}
		$notOptimalCaveat = '"\u4e0d\u7b49\u4e8e\u6700\u4f18\u52a8\u4f5c"' | ConvertFrom-Json
		if ($artifact.caveat -notmatch [regex]::Escape($notOptimalCaveat) -or
			$artifactText -match "anon-") {
			return "Invalid report (behavior prior caveat or privacy contract mismatch)"
		}
		return "PASS (behavior-prior-fixture, train=2/2, validation=2/2, test=2/2, legal-ordering=True, live-policy=False, rl-ready=False, optimality=False, dataset-sha256=$datasetHash, policy-sha256=$($artifact.policy_sha256))"
	} catch {
		return "Invalid report"
	}
}

function Get-ReleaseGateBehaviorCandidateAlignmentSummary(
	[string]$Path,
	[bool]$Skipped,
	[string]$ExpectedDatasetPath,
	[string]$ExpectedManifestPath,
	[string]$ExpectedPolicyPath,
	[string]$ExpectedRulesPath
) {
	if ($Skipped) {
		return "Skipped"
	}
	foreach ($requiredPath in @(
		$Path,
		$ExpectedDatasetPath,
		$ExpectedManifestPath,
		$ExpectedPolicyPath,
		$ExpectedRulesPath
	)) {
		if ([string]::IsNullOrWhiteSpace($requiredPath) -or
			-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
			return "Unavailable"
		}
	}

	try {
		$reportText = [System.IO.File]::ReadAllText(
			$Path,
			[System.Text.Encoding]::UTF8
		)
		$alignment = $reportText | ConvertFrom-Json
		if ($alignment.schema -ne "behavior-candidate-alignment-report-v1" -or
			$alignment.status -ne "NOT_READY" -or
			$alignment.contract_passed -ne $true -or
			$alignment.candidate_set_audit_complete -ne $true -or
			$alignment.candidate_ranking_training_ready -ne $false -or
			$alignment.candidate_generation_allowed -ne $false -or
			$alignment.live_policy_eligible -ne $false -or
			$alignment.rl_training_eligible -ne $false -or
			$alignment.optimality_verified -ne $false) {
			return "Invalid report (candidate alignment status or safety flags mismatch)"
		}

		$datasetFile = Get-Item -LiteralPath $ExpectedDatasetPath
		$datasetHash = (Get-FileHash -LiteralPath $ExpectedDatasetPath -Algorithm SHA256).Hash.ToLowerInvariant()
		$manifestFile = Get-Item -LiteralPath $ExpectedManifestPath
		$manifestHash = (Get-FileHash -LiteralPath $ExpectedManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
		$rulesFile = Get-Item -LiteralPath $ExpectedRulesPath
		$rulesHash = (Get-FileHash -LiteralPath $ExpectedRulesPath -Algorithm SHA256).Hash.ToLowerInvariant()
		if ($alignment.source_dataset.name -cne $datasetFile.Name -or
			$alignment.source_dataset.sha256 -cne $datasetHash -or
			[int64]$alignment.source_dataset.bytes -ne [int64]$datasetFile.Length -or
			[int]$alignment.source_dataset.record_count -ne 6 -or
			[int]$alignment.source_dataset.game_count -ne 6 -or
			$alignment.source_dataset.manifest_bound -ne $true -or
			$alignment.source_manifest.name -cne $manifestFile.Name -or
			$alignment.source_manifest.sha256 -cne $manifestHash -or
			$alignment.source_manifest.source_dataset_sha256 -cne $datasetHash -or
			$alignment.structured_rules.name -cne $rulesFile.Name -or
			$alignment.structured_rules.sha256 -cne $rulesHash -or
			[int64]$alignment.structured_rules.bytes -ne [int64]$rulesFile.Length) {
			return "Invalid report (candidate alignment source bytes are not fixture-bound)"
		}

		$policyDocument = [System.IO.File]::ReadAllText(
			$ExpectedPolicyPath,
			[System.Text.Encoding]::UTF8
		) | ConvertFrom-Json
		$policyKeys = @(
			"min_train_eligible_games",
			"min_validation_eligible_games",
			"min_test_eligible_games",
			"min_train_eligible_records",
			"min_validation_eligible_records",
			"min_test_eligible_records",
			"min_local_exact_alignment_rate",
			"min_local_candidate_set_eligible_rate"
		)
		if ($policyDocument.schema -ne "behavior-candidate-alignment-policy-v1" -or
			-not (Test-ReleaseGateExactJsonProperties $alignment.policy $policyKeys) -or
			-not (Test-ReleaseGateExactJsonProperties $policyDocument.thresholds $policyKeys) -or
			$alignment.policy_sha256 -notmatch '^[0-9a-f]{64}$') {
			return "Invalid report (candidate alignment policy contract mismatch)"
		}
		foreach ($key in $policyKeys) {
			$actual = $alignment.policy.PSObject.Properties[$key].Value
			$expected = $policyDocument.thresholds.PSObject.Properties[$key].Value
			if (-not (Test-ReleaseGateFiniteJsonNumber $actual) -or
				-not (Test-ReleaseGateFiniteJsonNumber $expected) -or
				[double]$actual -ne [double]$expected) {
				return "Invalid report (candidate alignment policy value mismatch: $key)"
			}
		}

		if ($alignment.structured_rules.source_card_defs_build -ne "247416" -or
			[int]$alignment.structured_rules.build_match_record_count -ne 0 -or
			[int]$alignment.structured_rules.build_mismatch_record_count -ne 6 -or
			[int]$alignment.structured_rules.matched_entity_count -ne 0 -or
			$alignment.structured_rules.cross_build_rule_application_allowed -ne $false) {
			return "Invalid report (cross-build structured rules were not rejected)"
		}

		$overall = $alignment.metrics.overall
		$blockers = $alignment.metrics.candidate_set_blocker_record_counts
		if ([int]$overall.record_count -ne 6 -or
			[int]$overall.exact_count -ne 3 -or
			[int]$overall.target_mismatch_count -ne 0 -or
			[int]$overall.not_generated_count -ne 3 -or
			[int]$overall.candidate_set_eligible_count -ne 0 -or
			[int]$alignment.metrics.by_actor_side.local.record_count -ne 3 -or
			[int]$alignment.metrics.by_actor_side.opponent.record_count -ne 3 -or
			[int]$blockers.structured_rules_build_mismatch -ne 6 -or
			[int]$blockers.actionable_card_rules_unverified -lt 1 -or
			[int]$blockers.observed_action_not_exactly_generated -ne 3 -or
			[int]$blockers.opponent_actions_excluded_from_candidate_set_training -ne 3 -or
			[int]$blockers.opponent_hidden_hand_unavailable -ne 3) {
			return "Invalid report (candidate completeness negative fixture was not rejected)"
		}
		foreach ($split in @("train", "validation", "test")) {
			if ([int]$alignment.metrics.candidate_set_eligible_split_record_counts.$split -ne 0 -or
				[int]$alignment.metrics.candidate_set_eligible_split_game_counts.$split -ne 0) {
				return "Invalid report (candidate alignment split eligibility leaked: $split)"
			}
		}
		$notRlTrajectoryCaveat = '"\u4e0d\u662f\u5f3a\u5316\u5b66\u4e60\u8f68\u8ff9"' |
			ConvertFrom-Json
		$notOptimalActionCaveat = '"\u4e0d\u8bc1\u660e\u4efb\u4f55\u52a8\u4f5c\u6700\u4f18"' |
			ConvertFrom-Json
		if ($reportText -match "anon-" -or
			$alignment.caveat -notmatch [regex]::Escape($notRlTrajectoryCaveat) -or
			$alignment.caveat -notmatch [regex]::Escape($notOptimalActionCaveat)) {
			return "Invalid report (candidate alignment privacy or caveat mismatch)"
		}
		return "PASS (candidate-completeness-negative-fixture, observed-exact=3/6, observed-not-generated=3/6, candidate-set-eligible=0/6, cross-build-applied=False, live-policy=False, rl-ready=False, optimality=False, dataset-sha256=$datasetHash)"
	} catch {
		return "Invalid report"
	}
}

function Get-ReleaseGateRuntimeBehaviorLearningSummary([string]$Path, [bool]$Skipped) {
	if ($Skipped) {
		return "Skipped"
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or
		-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return "Unavailable"
	}

	try {
		$runtime = [System.IO.File]::ReadAllText(
			$Path,
			[System.Text.Encoding]::UTF8
		) | ConvertFrom-Json
		if ($runtime.schema -ne "runtime-behavior-learning-readiness-report-v1" -or
			$runtime.source_kind -ne "live_runtime_snapshot" -or
			$runtime.behavior_input -ne "behavior-v1.jsonl" -or
			$runtime.trajectory_input -ne "training-v2.jsonl") {
			return "Invalid report (schema, source kind, or runtime input mismatch)"
		}
		if ($runtime.status -notin @("READY", "NOT_READY", "NO_DATA")) {
			return "Invalid report (unknown runtime behavior status)"
		}
		foreach ($property in @(
			"contract_passed",
			"imitation_ready",
			"rl_training_ready",
			"snapshots_content_addressed"
		)) {
			if ($runtime.$property -isnot [bool]) {
				return "Invalid report ($property must be boolean)"
			}
		}
		if ($runtime.rl_training_ready) {
			return "Invalid report (behavior data cannot be RL-ready)"
		}
		if ($runtime.policy_sha256 -notmatch '^[0-9a-fA-F]{64}$') {
			return "Invalid report (behavior policy SHA256 missing)"
		}
		foreach ($property in @("behavior_input_bytes", "trajectory_input_bytes")) {
			if (-not (Test-ReleaseGateJsonNumber $runtime.$property) -or
				[double]$runtime.$property -lt 0 -or
				[Math]::Floor([double]$runtime.$property) -ne [double]$runtime.$property) {
				return "Invalid report ($property must be a non-negative integer)"
			}
		}

		if ($runtime.status -eq "NO_DATA") {
			if ($runtime.contract_passed -or $runtime.imitation_ready -or
				$runtime.snapshots_content_addressed -or
				[double]$runtime.behavior_input_bytes -ne 0 -or
				-not [string]::IsNullOrWhiteSpace([string]$runtime.behavior_input_sha256) -or
				-not [string]::IsNullOrWhiteSpace([string]$runtime.behavior_snapshot) -or
				$null -ne $runtime.audit -or
				$runtime.reason -notin @(
					"runtime_behavior_log_not_found",
					"runtime_behavior_log_empty"
				)) {
				return "Invalid report (behavior NO_DATA fields are inconsistent)"
			}
			return "NO_DATA (production behavior-v1.jsonl is absent or empty; non-blocking for plugin release, policy-sha256=$($runtime.policy_sha256))"
		}

		$snapshotDirectory = Join-Path `
			(Split-Path -Parent $Path) `
			"runtime-behavior-learning-snapshots"
		if ([double]$runtime.behavior_input_bytes -le 0 -or
			$runtime.behavior_input_sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
			[string]::IsNullOrWhiteSpace([string]$runtime.behavior_snapshot)) {
			return "Invalid report (runtime behavior snapshot identity missing)"
		}
		$expectedBehaviorSnapshot =
			"behavior-v1.$($runtime.behavior_input_sha256.ToLowerInvariant()).jsonl"
		if ($runtime.behavior_snapshot -cne $expectedBehaviorSnapshot) {
			return "Invalid report (runtime behavior snapshot name is not content-addressed)"
		}
		$behaviorSnapshotPath = Join-Path $snapshotDirectory $runtime.behavior_snapshot
		if (-not (Test-Path -LiteralPath $behaviorSnapshotPath -PathType Leaf)) {
			return "Invalid report (runtime behavior snapshot file missing)"
		}
		$behaviorHash =
			(Get-FileHash -LiteralPath $behaviorSnapshotPath -Algorithm SHA256).Hash.ToLowerInvariant()
		if ((Get-Item -LiteralPath $behaviorSnapshotPath).Length -ne
				[double]$runtime.behavior_input_bytes -or
			$behaviorHash -cne $runtime.behavior_input_sha256.ToLowerInvariant()) {
			return "Invalid report (runtime behavior snapshot bytes do not match identity)"
		}

		if ($runtime.reason -eq "runtime_trajectory_result_log_not_found") {
			if ($runtime.status -ne "NOT_READY" -or $runtime.contract_passed -or
				$runtime.imitation_ready -or $runtime.snapshots_content_addressed -or
				[double]$runtime.trajectory_input_bytes -ne 0 -or
				-not [string]::IsNullOrWhiteSpace([string]$runtime.trajectory_input_sha256) -or
				-not [string]::IsNullOrWhiteSpace([string]$runtime.trajectory_snapshot) -or
				$null -ne $runtime.audit) {
				return "Invalid report (missing result-log fields are inconsistent)"
			}
			return "NOT_READY (behavior exists but terminal-result log is absent; non-blocking for plugin release, rl-ready=False)"
		}

		if (-not $runtime.snapshots_content_addressed -or
			$runtime.trajectory_input_sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
			[string]::IsNullOrWhiteSpace([string]$runtime.trajectory_snapshot) -or
			$null -eq $runtime.audit) {
			return "Invalid report (joint runtime snapshot identity missing)"
		}
		$expectedTrajectorySnapshot =
			"training-v2.$($runtime.trajectory_input_sha256.ToLowerInvariant()).jsonl"
		if ($runtime.trajectory_snapshot -cne $expectedTrajectorySnapshot) {
			return "Invalid report (runtime result snapshot name is not content-addressed)"
		}
		$trajectorySnapshotPath = Join-Path $snapshotDirectory $runtime.trajectory_snapshot
		if (-not (Test-Path -LiteralPath $trajectorySnapshotPath -PathType Leaf)) {
			return "Invalid report (runtime result snapshot file missing)"
		}
		$trajectoryHash =
			(Get-FileHash -LiteralPath $trajectorySnapshotPath -Algorithm SHA256).Hash.ToLowerInvariant()
		if ((Get-Item -LiteralPath $trajectorySnapshotPath).Length -ne
				[double]$runtime.trajectory_input_bytes -or
			$trajectoryHash -cne $runtime.trajectory_input_sha256.ToLowerInvariant()) {
			return "Invalid report (runtime result snapshot bytes do not match identity)"
		}

		$audit = $runtime.audit
		if ($audit.schema -ne "behavior-learning-readiness-report-v1" -or
			$audit.source_kind -ne "live_runtime_snapshot" -or
			$audit.behavior_input_sha256 -ne $runtime.behavior_input_sha256 -or
			$audit.trajectory_input_sha256 -ne $runtime.trajectory_input_sha256 -or
			[double]$audit.behavior_input_bytes -ne [double]$runtime.behavior_input_bytes -or
			[double]$audit.trajectory_input_bytes -ne [double]$runtime.trajectory_input_bytes -or
			$audit.policy_sha256 -ne $runtime.policy_sha256 -or
			$audit.contract_passed -ne $runtime.contract_passed -or
			$audit.imitation_ready -ne $runtime.imitation_ready -or
			$audit.rl_training_ready -or
			$audit.passed -ne $runtime.imitation_ready) {
			return "Invalid report (joint runtime behavior audit binding mismatch)"
		}
		$notOptimalBehaviorCaveat = '"\u4e0d\u7b49\u4e8e\u6700\u4f18\u52a8\u4f5c"' |
			ConvertFrom-Json
		if ($audit.caveat -notmatch [regex]::Escape($notOptimalBehaviorCaveat)) {
			return "Invalid report (joint runtime behavior caveat mismatch)"
		}
		if (($runtime.status -eq "READY") -ne $runtime.imitation_ready -or
			($runtime.imitation_ready -and -not $runtime.contract_passed)) {
			return "Invalid report (runtime behavior status contradicts readiness)"
		}
		foreach ($metric in @(
			"behavior_record_count",
			"unique_behavior_game_count",
			"joined_result_game_count",
			"behavior_eligible_record_count",
			"local_eligible_record_count",
			"opponent_eligible_record_count"
		)) {
			if (-not (Test-ReleaseGateJsonNumber $audit.metrics.$metric) -or
				[double]$audit.metrics.$metric -lt 0 -or
				[Math]::Floor([double]$audit.metrics.$metric) -ne
					[double]$audit.metrics.$metric) {
				return "Invalid report (runtime behavior metric missing: $metric)"
			}
		}
		return "$($runtime.status) (behavior-records=$($audit.metrics.behavior_record_count), games=$($audit.metrics.unique_behavior_game_count), joined-games=$($audit.metrics.joined_result_game_count), eligible=$($audit.metrics.behavior_eligible_record_count), local-eligible=$($audit.metrics.local_eligible_record_count), opponent-eligible=$($audit.metrics.opponent_eligible_record_count), contract=$($runtime.contract_passed), imitation-ready=$($runtime.imitation_ready), rl-ready=False, non-blocking for plugin release)"
	} catch {
		return "Invalid report"
	}
}

function Get-ReleaseGateRustBehaviorPriorSummary([string]$Path, [bool]$Skipped) {
	if ($Skipped) {
		return "Skipped"
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or
		-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return "Unavailable"
	}
	try {
		$check = [System.IO.File]::ReadAllText(
			$Path,
			[System.Text.Encoding]::UTF8
		) | ConvertFrom-Json
		if ($check.schema -cne "metacompanion-rust-behavior-prior-check-v1" -or
			$check.status -cne "pass" -or
			[string]$check.artifact_sha256 -notmatch '^[0-9a-f]{64}$' -or
			$check.search_ordering_only -ne $true -or
			$check.candidate_generation_allowed -ne $false -or
			$check.live_policy_eligible -ne $false -or
			$check.rl_training_eligible -ne $false -or
			$check.optimality_verified -ne $false) {
			return "Invalid report"
		}
		return "PASS (Rust loader, ordering-only=True, generation=False, live-policy=False, rl-ready=False, optimality=False, sha256=$($check.artifact_sha256))"
	} catch {
		return "Invalid report"
	}
}

function Get-ReleaseGateRustDecisionRankerSummary([string]$Path, [bool]$Skipped) {
	if ($Skipped) {
		return "Skipped"
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or
		-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return "Unavailable"
	}
	try {
		$check = [System.IO.File]::ReadAllText(
			$Path,
			[System.Text.Encoding]::UTF8
		) | ConvertFrom-Json
		if ($check.schema -cne "metacompanion-rust-decision-ranker-check-v1" -or
			$check.status -cne "pass" -or
			[string]$check.artifact_sha256 -notmatch '^[0-9a-f]{64}$' -or
			$check.search_ordering_only -ne $true -or
			$check.local_actions_only -ne $true -or
			$check.candidate_generation_allowed -ne $false -or
			$check.live_policy_eligible -ne $false -or
			$check.rl_training_eligible -ne $false -or
			$check.optimality_verified -ne $false) {
			return "Invalid report"
		}
		return "PASS (Rust loader, local-only=True, ordering-only=True, generation=False, live-policy=False, rl-ready=False, optimality=False, sha256=$($check.artifact_sha256))"
	} catch {
		return "Invalid report"
	}
}

function Get-ReleaseGateObservedPolicyFixtureSummary(
	[string]$EvaluationPath,
	[string]$PriorPath,
	[string]$RankerPath,
	[string]$FixtureDirectory,
	[bool]$Skipped
) {
	if ($Skipped) {
		return "Skipped"
	}
	foreach ($path in @($EvaluationPath, $PriorPath, $RankerPath)) {
		if ([string]::IsNullOrWhiteSpace($path) -or
			-not (Test-Path -LiteralPath $path -PathType Leaf)) {
			return "Unavailable"
		}
	}
	try {
		$evaluation = Read-ReleaseGateUtf8Json $EvaluationPath
		$prior = Read-ReleaseGateUtf8Json $PriorPath
		$ranker = Read-ReleaseGateUtf8Json $RankerPath
		$decisionFrames = Join-Path $FixtureDirectory "advisor-decision-frame-v1.jsonl"
		$behavior = Join-Path $FixtureDirectory "behavior-v1.jsonl"
		$imitation = Join-Path $FixtureDirectory "behavior-imitation-v1.jsonl"
		$manifest = Join-Path $FixtureDirectory "behavior-imitation-v1.manifest.json"
		foreach ($source in @($decisionFrames, $behavior, $imitation, $manifest)) {
			if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
				return "Invalid report (fixture source missing)"
			}
		}
		if ($prior.schema -cne "behavior-imitation-prior-v2" -or
			$prior.search_ordering_prior_ready -ne $true -or
			$prior.candidate_generation_allowed -ne $false -or
			$prior.live_policy_eligible -ne $false -or
			$prior.rl_training_eligible -ne $false -or
			$prior.optimality_verified -ne $false -or
			$ranker.schema -cne "advisor-decision-ranker-v1" -or
			$ranker.candidate_ranking_ready -ne $true -or
			$ranker.user_visible_behavior_reference_eligible -ne $true -or
			$ranker.candidate_generation_allowed -ne $false -or
			$ranker.live_policy_eligible -ne $false -or
			$ranker.rl_training_eligible -ne $false -or
			$ranker.optimality_verified -ne $false) {
			return "Invalid report (dual model safety contract mismatch)"
		}
		if ($evaluation.schema -cne "observed-policy-evaluation-v1" -or
			$evaluation.status -cne "READY" -or
			$evaluation.source_binding_passed -ne $true -or
			$evaluation.decision_frame_contract_passed -ne $true -or
			$evaluation.candidate_ranking_evaluation_ready -ne $true -or
			$evaluation.opponent_behavior_modeling_ready -ne $true -or
			$evaluation.search_ordering_prior_ready -ne $true -or
			$evaluation.candidate_generation_allowed -ne $false -or
			$evaluation.live_policy_eligible -ne $false -or
			$evaluation.rl_training_eligible -ne $false -or
			$evaluation.optimality_verified -ne $false) {
			return "Invalid report (joint evaluation contract mismatch)"
		}
		$expectedHashes = @{
			decision = (Get-FileHash -LiteralPath $decisionFrames -Algorithm SHA256).Hash.ToLowerInvariant()
			behavior = (Get-FileHash -LiteralPath $behavior -Algorithm SHA256).Hash.ToLowerInvariant()
			imitation = (Get-FileHash -LiteralPath $imitation -Algorithm SHA256).Hash.ToLowerInvariant()
			manifest = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
			prior = (Get-FileHash -LiteralPath $PriorPath -Algorithm SHA256).Hash.ToLowerInvariant()
			ranker = (Get-FileHash -LiteralPath $RankerPath -Algorithm SHA256).Hash.ToLowerInvariant()
		}
		if ([string]$evaluation.source_decision_frames.sha256 -cne $expectedHashes.decision -or
			[string]$evaluation.source_behavior.sha256 -cne $expectedHashes.behavior -or
			[string]$evaluation.source_imitation_dataset.sha256 -cne $expectedHashes.imitation -or
			[string]$evaluation.source_manifest.sha256 -cne $expectedHashes.manifest -or
			[string]$evaluation.source_prior.sha256 -cne $expectedHashes.prior -or
			[string]$evaluation.source_decision_ranker.sha256 -cne $expectedHashes.ranker -or
			[int]$evaluation.source_decision_frames.record_count -ne 3 -or
			[int]$evaluation.source_imitation_dataset.record_count -ne 6) {
			return "Invalid report (joint evaluation is not fixture-bound)"
		}
		foreach ($split in @("train", "validation", "test")) {
			if ([int]$evaluation.candidate_ranking.$split.game_count -ne 1 -or
				[int]$evaluation.candidate_ranking.$split.record_count -ne 1 -or
				[int]$evaluation.opponent_behavior.$split.game_count -ne 1 -or
				[int]$evaluation.opponent_behavior.$split.record_count -ne 1) {
				return "Invalid report (joint fixture split isolation mismatch)"
			}
		}
		return "PASS (local-ranker=READY, opponent-prior=READY, train/validation/test=1/1 each scope, generation=False, live-policy=False, rl-ready=False, optimality=False)"
	} catch {
		return "Invalid report"
	}
}

function Get-ReleaseGateDecisionSolverCoverageStatus(
	[string]$Path,
	[string]$BinaryPath,
	[string]$FixtureDirectory,
	[bool]$Requested,
	[bool]$Skipped
) {
	$result = [pscustomobject]@{
		Ready = $false
		Summary = if (-not $Requested) { "Not requested" } elseif ($Skipped) { "Skipped" } else { "Unavailable" }
		BinarySha256 = ""
	}
	if (-not $Requested -or $Skipped) {
		return $result
	}
	$decisionFrames = Join-Path $FixtureDirectory "advisor-decision-frame-v1.jsonl"
	$behavior = Join-Path $FixtureDirectory "behavior-v1.jsonl"
	foreach ($required in @($Path, $BinaryPath, $decisionFrames, $behavior)) {
		if ([string]::IsNullOrWhiteSpace($required) -or
			-not (Test-Path -LiteralPath $required -PathType Leaf)) {
			return $result
		}
	}
	try {
		$raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
		$evaluation = $raw | ConvertFrom-Json
		$binaryHash = (Get-FileHash -LiteralPath $BinaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
		$decisionHash = (Get-FileHash -LiteralPath $decisionFrames -Algorithm SHA256).Hash.ToLowerInvariant()
		$behaviorHash = (Get-FileHash -LiteralPath $behavior -Algorithm SHA256).Hash.ToLowerInvariant()
		$privatePropertyPattern = '"(game_id|state_id|entity_id|source_entity_id|target_entity_id|request_id|decision_frame_id|selected_behavior_id|observed_at_utc|hdt_frame_id|session_token|token|url|path)"\s*:'
		$absolutePathPattern = '"(?:[A-Za-z]:[\\/]|file://|https?://)'
		if ($evaluation.schema -cne "advisor-decision-solver-evaluation-v1" -or
			$evaluation.status -cne "AUDITED" -or
			$evaluation.passed -ne $true -or
			$evaluation.decision_frame_contract_passed -ne $true -or
			$evaluation.source_binding_passed -ne $true -or
			$evaluation.worker_backend_ready -ne $true -or
			$evaluation.root_action_portfolio_capability -ne $true -or
			$evaluation.privacy_contract_passed -ne $true -or
			[int]$evaluation.privacy_violation_count -ne 0 -or
			$evaluation.observed_choice_used_as_optimality_label -ne $false -or
			$evaluation.outcome_used_as_action_optimality -ne $false -or
			$evaluation.counterfactual_dataset_written -ne $false -or
			$evaluation.candidate_generation_allowed -ne $false -or
			$evaluation.live_policy_eligible -ne $false -or
			$evaluation.rl_training_eligible -ne $false -or
			$evaluation.global_optimality_verified -ne $false -or
			[string]$evaluation.source_decision_frames.sha256 -cne $decisionHash -or
			[string]$evaluation.source_behavior.sha256 -cne $behaviorHash -or
			[string]$evaluation.source_rust_binary.sha256 -cne $binaryHash -or
			[int]$evaluation.source_decision_frames.record_count -ne 3 -or
			[int]$evaluation.source_behavior.record_count -ne 6 -or
			[int]$evaluation.sample.requested_max_frames -ne 0 -or
			[string]$evaluation.sample.sha256 -notmatch '^[0-9a-f]{64}$' -or
			[string]$evaluation.historical_candidate_adapter.contract -cne "decision_frame_legal_candidates_to_hdt_root_request_v1" -or
			$evaluation.historical_candidate_adapter.offline_evaluation_only -ne $true -or
			$evaluation.historical_candidate_adapter.source_candidate_set_preserved -ne $true -or
			$evaluation.historical_candidate_adapter.adapter_identity_used_as_training_evidence -ne $false -or
			[int]$evaluation.metrics.sampled_frame_count -ne 3 -or
			[int]$evaluation.metrics.hdt_candidate_count -ne 9 -or
			[int]$evaluation.metrics.hdt_supplied_request_structurally_valid_frame_count -ne 3 -or
			[int]$evaluation.metrics.hdt_supplied_response_contract_valid_frame_count -ne 3 -or
			[int]$evaluation.metrics.hdt_supplied_candidate_count -ne 9 -or
			[int]$evaluation.metrics.hdt_supplied_evaluated_count -ne 9 -or
			[int]$evaluation.metrics.hdt_supplied_omitted_count -ne 0 -or
			[double]$evaluation.metrics.hdt_supplied_evaluated_coverage -ne 1.0 -or
			[int]$evaluation.metrics.hdt_supplied_root_portfolio_fully_modeled_frame_count -ne 3 -or
			[int]$evaluation.metrics.independent_generated_root_action_count -ne 9 -or
			[int]$evaluation.metrics.independent_matched_hdt_candidate_count -ne 9 -or
			[int]$evaluation.metrics.independent_missing_hdt_candidate_count -ne 0 -or
			[int]$evaluation.metrics.independent_extra_root_action_count -ne 0 -or
			[double]$evaluation.metrics.independent_hdt_candidate_recall -ne 1.0 -or
			[double]$evaluation.metrics.independent_root_precision -ne 1.0 -or
			[int]$evaluation.metrics.independent_complete_candidate_set_match_count -ne 3 -or
			[double]$evaluation.metrics.hdt_candidate_recall -ne 1.0 -or
			[double]$evaluation.metrics.rust_root_precision -ne 1.0 -or
			[int]$evaluation.metrics.complete_candidate_set_match_count -ne 3 -or
			[int]$evaluation.metrics.protocol_error_count -ne 0 -or
			[int]$evaluation.metrics.false_exact_count -ne 0 -or
			[int]$evaluation.metrics.solver_scope_verified_frame_count -ne 3 -or
			[int]$evaluation.solver_scope_counterfactual_evidence_count -ne 3 -or
			@($evaluation.top_uncovered_public_cards).Count -ne 0 -or
			$raw -match $privatePropertyPattern -or
			$raw -match $absolutePathPattern) {
			$result.Summary = "Invalid report"
			return $result
		}
		$result.Ready = $true
		$result.BinarySha256 = $binaryHash.ToUpperInvariant()
		$result.Summary = "PASS (frames=3/3 exact-aligned, independent=9/9, HDT-evaluated=9/9, omitted=0, false-exact=0, privacy=True, live-policy=False, rl-ready=False, global-optimality=False, SHA256=$($result.BinarySha256))"
		return $result
	} catch {
		$result.Summary = "Invalid report"
		return $result
	}
}

function Get-ReleaseGateAdvisorModelUpdaterSummary([string]$Path, [bool]$Skipped) {
	if ($Skipped) {
		return "Skipped"
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or
		-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return "Unavailable"
	}
	$text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
	if ($text -match '(?m)^SELFTEST_OK=1\r?$') {
		return "PASS (dual training, joint evaluation, two Rust loaders, transactional update/unchanged/rollback)"
	}
	return "FAIL (dual-model updater self-test marker missing)"
}

function Get-ReleaseGateRustCardPoolStatus(
	[string]$Path,
	[string]$BinaryPath,
	[bool]$Requested,
	[bool]$Skipped
) {
	$result = [pscustomobject]@{
		Ready = $false
		Summary = if (-not $Requested) { "Not requested" } elseif ($Skipped) { "Skipped" } else { "Unavailable" }
		BinarySha256 = ""
	}
	if (-not $Requested -or $Skipped) {
		return $result
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or
		-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
		[string]::IsNullOrWhiteSpace($BinaryPath) -or
		-not (Test-Path -LiteralPath $BinaryPath -PathType Leaf)) {
		return $result
	}
	try {
		$gate = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
		$actualBinaryHash =
			(Get-FileHash -LiteralPath $BinaryPath -Algorithm SHA256).Hash.ToUpperInvariant()
		$checks = $gate.checks
		if ($gate.schema -cne "metacompanion-rust-official-card-pool-gate-v1" -or
			$gate.passed -ne $true -or
			[int]$gate.check_count -ne 5 -or
			$gate.binary_sha256 -cne $actualBinaryHash -or
			$gate.standard_count -ne 1 -or
			$gate.arena_count -ne 1 -or
			$gate.rules_coverage -ne $false -or
			$gate.enforces_action_legality -ne $false -or
			$checks.valid_python_rust_interop -ne $true -or
			$checks.publish_manifest_binding -ne $true -or
			$checks.page_freshness -ne $true -or
			$checks.card_defs_binding -ne $true -or
			$checks.duplicate_identity -ne $true) {
			$result.Summary = "Invalid report"
			return $result
		}
		$result.Ready = $true
		$result.BinarySha256 = $actualBinaryHash
		$result.Summary =
			"PASS (Rust/Python interop, checks=5/5, rules=False, legality=False, SHA256=$actualBinaryHash)"
		return $result
	} catch {
		$result.Summary = "Invalid report"
		return $result
	}
}

function Get-ReleaseGateRuntimeTrajectorySummary([string]$Path, [bool]$Skipped) {
	if ($Skipped) {
		return "Skipped"
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return "Unavailable"
	}

	try {
		$runtime = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
		if ($runtime.schema -ne "runtime-trajectory-readiness-report-v1" -or
			$runtime.source_kind -ne "live_runtime_snapshot") {
			return "Invalid report (schema or source kind mismatch)"
		}
		if ($runtime.input -ne "training-v2.jsonl") {
			return "Invalid report (runtime input must be production training-v2.jsonl)"
		}
		if ($runtime.status -notin @("READY", "NOT_READY", "NO_DATA")) {
			return "Invalid report (unknown runtime status)"
		}
		foreach ($property in @("contract_passed", "training_ready", "snapshot_content_addressed")) {
			if ($runtime.$property -isnot [bool]) {
				return "Invalid report ($property must be boolean)"
			}
		}
		if ($runtime.policy_sha256 -notmatch '^[0-9a-fA-F]{64}$') {
			return "Invalid report (policy SHA256 missing)"
		}
		if (-not (Test-ReleaseGateJsonNumber $runtime.input_bytes) -or
			[double]$runtime.input_bytes -lt 0 -or
			[Math]::Floor([double]$runtime.input_bytes) -ne [double]$runtime.input_bytes) {
			return "Invalid report (input bytes missing)"
		}

		if ($runtime.status -eq "NO_DATA") {
			if ($runtime.training_ready -or $runtime.contract_passed -or
				$runtime.snapshot_content_addressed -or [double]$runtime.input_bytes -ne 0 -or
				-not [string]::IsNullOrWhiteSpace([string]$runtime.input_sha256) -or
				-not [string]::IsNullOrWhiteSpace([string]$runtime.snapshot) -or
				$null -ne $runtime.audit -or
				$runtime.reason -notin @("runtime_training_log_not_found", "runtime_training_log_empty")) {
				return "Invalid report (NO_DATA fields are inconsistent)"
			}
			return "NO_DATA (production training-v2.jsonl is absent or empty; non-blocking for plugin release, policy-sha256=$($runtime.policy_sha256))"
		}

		if ([double]$runtime.input_bytes -le 0 -or
			$runtime.input_sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
			-not $runtime.snapshot_content_addressed -or
			[string]::IsNullOrWhiteSpace([string]$runtime.snapshot) -or
			$null -eq $runtime.audit) {
			return "Invalid report (runtime snapshot identity missing)"
		}
		$expectedSnapshotName = "training-v2.$($runtime.input_sha256.ToLowerInvariant()).jsonl"
		if ($runtime.snapshot -cne $expectedSnapshotName) {
			return "Invalid report (runtime snapshot name is not content-addressed)"
		}
		$snapshotPath = Join-Path `
			(Join-Path (Split-Path -Parent $Path) "runtime-trajectory-snapshots") `
			$runtime.snapshot
		if (-not (Test-Path -LiteralPath $snapshotPath -PathType Leaf)) {
			return "Invalid report (runtime snapshot file missing)"
		}
		$snapshotFile = Get-Item -LiteralPath $snapshotPath
		$snapshotHash = (Get-FileHash -LiteralPath $snapshotPath -Algorithm SHA256).Hash.ToLowerInvariant()
		if ([double]$snapshotFile.Length -ne [double]$runtime.input_bytes -or
			$snapshotHash -cne $runtime.input_sha256.ToLowerInvariant()) {
			return "Invalid report (runtime snapshot bytes do not match report identity)"
		}

		$expectedCaveat = "Training-ready means only that anonymized trajectories satisfy this versioned join, exact-action, replay, split, and privacy contract. It does not prove that the online solver is healthy or optimal, that labels are unbiased, or that an RL policy exists; solver_runtime_ready reports online solve health separately."
		if ($runtime.audit.schema -ne "trajectory-readiness-report-v1" -or
			$runtime.audit.trajectory_schema -ne "trajectory-readiness-v1" -or
			$runtime.audit.source_kind -ne "live_runtime_snapshot" -or
			$runtime.audit.input -cne $runtime.snapshot -or
			$runtime.audit.input_sha256 -ne $runtime.input_sha256 -or
			-not (Test-ReleaseGateJsonNumber $runtime.audit.input_bytes) -or
			[double]$runtime.audit.input_bytes -ne [double]$runtime.input_bytes -or
			$runtime.audit.policy_sha256 -ne $runtime.policy_sha256 -or
			$runtime.audit.training_ready -ne $runtime.training_ready -or
			$runtime.audit.contract_passed -ne $runtime.contract_passed -or
			$runtime.audit.passed -isnot [bool] -or
			$runtime.audit.passed -ne $runtime.training_ready -or
			$runtime.audit.caveat -ne $expectedCaveat) {
			return "Invalid report (runtime snapshot or readiness binding mismatch)"
		}
		if ($runtime.audit.solve_status_semantics.schema -ne "solve-status-semantics-v1" -or
			(@($runtime.audit.solve_status_semantics.policy_buckets) -join ",") -cne
				"ok,partial,cancelled,unsupported,non_ok" -or
			(@($runtime.audit.solve_status_semantics.non_ok_members) -join ",") -cne
				"error,other" -or
			(@($runtime.audit.solve_status_semantics.unsuccessful_members) -join ",") -cne
				"partial,cancelled,unsupported,error,other") {
			return "Invalid report (runtime solve status semantics mismatch)"
		}
		if (($runtime.training_ready -and -not $runtime.contract_passed) -or
			($runtime.status -eq "READY" -and $runtime.reason -ne "production_policy_passed") -or
			($runtime.status -eq "NOT_READY" -and $runtime.reason -ne "production_policy_failed")) {
			return "Invalid report (runtime status fields are inconsistent)"
		}

		$runtimeCountMetrics = @(
			"record_count",
			"unique_game_count",
			"canonical_decision_count",
			"terminal_result_game_count",
			"solve_record_count",
			"ok_solve_count",
			"partial_solve_count",
			"cancelled_solve_count",
			"unsupported_solve_count",
			"error_solve_count",
			"other_solve_count",
			"non_ok_solve_count",
			"unsuccessful_solve_count",
			"action_observation_count",
			"exact_action_count",
			"replayable_transition_count"
		)
		$runtimeRateMetrics = @(
			"ok_solve_rate",
			"partial_solve_rate",
			"cancelled_solve_rate",
			"unsupported_solve_rate",
			"error_solve_rate",
			"other_solve_rate",
			"non_ok_solve_rate",
			"unsuccessful_solve_rate"
		)
		if ($null -eq $runtime.audit.metrics) {
			return "Invalid report (runtime audit metrics missing)"
		}
		foreach ($metric in $runtimeCountMetrics) {
			if ($runtime.audit.metrics.PSObject.Properties.Name -notcontains $metric -or
				-not (Test-ReleaseGateJsonNumber $runtime.audit.metrics.$metric) -or
				[double]$runtime.audit.metrics.$metric -lt 0 -or
				[Math]::Floor([double]$runtime.audit.metrics.$metric) -ne [double]$runtime.audit.metrics.$metric) {
				return "Invalid report (invalid runtime count metric: $metric)"
			}
		}
		foreach ($metric in $runtimeRateMetrics) {
			if ($runtime.audit.metrics.PSObject.Properties.Name -notcontains $metric -or
				-not (Test-ReleaseGateJsonNumber $runtime.audit.metrics.$metric) -or
				[double]$runtime.audit.metrics.$metric -lt 0 -or
				[double]$runtime.audit.metrics.$metric -gt 1) {
				return "Invalid report (invalid runtime rate metric: $metric)"
			}
		}
		$runtimeStatusTotal = [double]$runtime.audit.metrics.ok_solve_count +
			[double]$runtime.audit.metrics.partial_solve_count +
			[double]$runtime.audit.metrics.cancelled_solve_count +
			[double]$runtime.audit.metrics.unsupported_solve_count +
			[double]$runtime.audit.metrics.error_solve_count +
			[double]$runtime.audit.metrics.other_solve_count
		if ($runtimeStatusTotal -ne [double]$runtime.audit.metrics.solve_record_count -or
			[double]$runtime.audit.metrics.non_ok_solve_count -ne
				([double]$runtime.audit.metrics.error_solve_count + [double]$runtime.audit.metrics.other_solve_count) -or
			[double]$runtime.audit.metrics.unsuccessful_solve_count -ne
				([double]$runtime.audit.metrics.solve_record_count - [double]$runtime.audit.metrics.ok_solve_count) -or
			[double]$runtime.audit.metrics.exact_action_count -gt
				[double]$runtime.audit.metrics.action_observation_count -or
			[double]$runtime.audit.metrics.replayable_transition_count -gt
				[double]$runtime.audit.metrics.exact_action_count) {
			return "Invalid report (runtime trajectory metric counts are inconsistent)"
		}
		if (($runtime.status -eq "READY") -ne [bool]$runtime.training_ready) {
			return "Invalid report (runtime status contradicts readiness)"
		}
		return "$($runtime.status) (source=live_runtime_snapshot, contract=$($runtime.contract_passed), training-ready=$($runtime.training_ready), input-bytes=$($runtime.input_bytes), input-sha256=$($runtime.input_sha256), policy-sha256=$($runtime.policy_sha256), records=$($runtime.audit.metrics.record_count), games=$($runtime.audit.metrics.unique_game_count), solves=$($runtime.audit.metrics.solve_record_count), ok=$($runtime.audit.metrics.ok_solve_count), partial=$($runtime.audit.metrics.partial_solve_count), cancelled=$($runtime.audit.metrics.cancelled_solve_count), unsupported=$($runtime.audit.metrics.unsupported_solve_count), errors=$($runtime.audit.metrics.error_solve_count), non-ok=$($runtime.audit.metrics.non_ok_solve_count), unsuccessful-rate=$($runtime.audit.metrics.unsuccessful_solve_rate))"
	} catch {
		return "Invalid report"
	}
}

function Get-ReleaseGateRustParityStatus(
	[string]$Path,
	[bool]$Requested,
	[string]$ExpectedProfile,
	[int]$MinimumFixtureCount,
	[string]$ExpectedBinaryPath
) {
	$result = [ordered]@{
		Ready = $false
		Summary = "Not requested"
		BinarySha256 = ""
	}
	if (-not $Requested) {
		return [pscustomobject]$result
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or
		-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		$result.Summary = "Unavailable"
		return [pscustomobject]$result
	}
	if ([string]::IsNullOrWhiteSpace($ExpectedBinaryPath) -or
		-not (Test-Path -LiteralPath $ExpectedBinaryPath -PathType Leaf)) {
		$result.Summary = "Invalid report (verified binary is unavailable)"
		return [pscustomobject]$result
	}

	try {
		$parity = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
		if ($parity.schema -ne "metacompanion-rust-parity-report-v1" -or
			$parity.profile -ne $ExpectedProfile) {
			$result.Summary = "Invalid report (schema or profile mismatch)"
			return [pscustomobject]$result
		}
		if ($parity.passed -isnot [bool] -or
			$parity.binary.available -isnot [bool]) {
			$result.Summary = "Invalid report (readiness flags must be boolean)"
			return [pscustomobject]$result
		}
		foreach ($metric in @("fixture_count", "passed_fixture_count", "failed_fixture_count")) {
			if ($parity.metrics.PSObject.Properties.Name -notcontains $metric -or
				-not (Test-ReleaseGateJsonNumber $parity.metrics.$metric)) {
				$result.Summary = "Invalid report (missing or non-numeric metric: $metric)"
				return [pscustomobject]$result
			}
		}
		$fixtureCount = [int]$parity.metrics.fixture_count
		$passedCount = [int]$parity.metrics.passed_fixture_count
		$failedCount = [int]$parity.metrics.failed_fixture_count
		if ($fixtureCount -lt $MinimumFixtureCount -or
			$passedCount -ne $fixtureCount -or
			$failedCount -ne 0) {
			$result.Summary = "FAIL (fixtures=$passedCount/$fixtureCount, minimum=$MinimumFixtureCount, failed=$failedCount)"
			return [pscustomobject]$result
		}
		$reportedHash = ([string]$parity.binary.sha256).Trim().ToLowerInvariant()
		if ($reportedHash -notmatch '^[0-9a-f]{64}$') {
			$result.Summary = "Invalid report (binary hash is malformed)"
			return [pscustomobject]$result
		}
		$actualHash = (Get-FileHash -LiteralPath $ExpectedBinaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
		if ($reportedHash -ne $actualHash) {
			$result.Summary = "Invalid report (binary changed after parity verification)"
			return [pscustomobject]$result
		}
		$result.BinarySha256 = $actualHash
		if (-not $parity.passed -or -not $parity.binary.available -or $parity.status -ne "passed") {
			$result.Summary = "FAIL (fixtures=$passedCount/$fixtureCount, status=$($parity.status))"
			return [pscustomobject]$result
		}
		$result.Ready = $true
		$result.Summary = "PASS (profile=$ExpectedProfile, fixtures=$passedCount/$fixtureCount, SHA256=$actualHash)"
		return [pscustomobject]$result
	} catch {
		$result.Summary = "Invalid report"
		return [pscustomobject]$result
	}
}

function Get-ReleaseGateVisibleResponseStatus(
	[string]$Path,
	[bool]$Requested,
	[int]$MinimumFixtureCount,
	[string]$ExpectedBinaryPath
) {
	$result = [ordered]@{
		Ready = $false
		Summary = "Not requested"
		BinarySha256 = ""
	}
	if (-not $Requested) {
		return [pscustomobject]$result
	}
	if ([string]::IsNullOrWhiteSpace($Path) -or
		-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		$result.Summary = "Unavailable"
		return [pscustomobject]$result
	}
	if ([string]::IsNullOrWhiteSpace($ExpectedBinaryPath) -or
		-not (Test-Path -LiteralPath $ExpectedBinaryPath -PathType Leaf)) {
		$result.Summary = "Invalid report (verified binary is unavailable)"
		return [pscustomobject]$result
	}

	try {
		$evaluation = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
		if ($evaluation.schema -ne "visible-response-report-v1" -or
			$evaluation.suite_id -ne "visible-response-v1") {
			$result.Summary = "Invalid report (schema or suite mismatch)"
			return [pscustomobject]$result
		}
		if ($evaluation.passed -isnot [bool] -or
			$evaluation.binary.available -isnot [bool]) {
			$result.Summary = "Invalid report (readiness flags must be boolean)"
			return [pscustomobject]$result
		}
		$requiredMetrics = @(
			"fixture_count",
			"passed_fixture_count",
			"failed_fixture_count",
			"contract_failure_count",
			"false_claim_count",
			"partial_status_count",
			"threat_fixture_count",
			"threat_priority_passed_count",
			"distinct_first_actions_fixture_count",
			"distinct_first_actions_passed_count",
			"duplicate_first_action_count",
			"unknown_source_fixture_count",
			"unknown_friendly_action_blocked_count",
			"approximation_fixture_count",
			"approximation_passed_count"
		)
		foreach ($metric in $requiredMetrics) {
			if ($evaluation.metrics.PSObject.Properties.Name -notcontains $metric -or
				-not (Test-ReleaseGateJsonNumber $evaluation.metrics.$metric)) {
				$result.Summary = "Invalid report (missing or non-numeric metric: $metric)"
				return [pscustomobject]$result
			}
			$value = [double]$evaluation.metrics.$metric
			if ($value -lt 0 -or [Math]::Floor($value) -ne $value) {
				$result.Summary = "Invalid report (count metric must be a non-negative integer: $metric)"
				return [pscustomobject]$result
			}
		}
		$fixtureCount = [int]$evaluation.metrics.fixture_count
		$passedCount = [int]$evaluation.metrics.passed_fixture_count
		$failedCount = [int]$evaluation.metrics.failed_fixture_count
		if ($fixtureCount -lt $MinimumFixtureCount -or
			$passedCount -ne $fixtureCount -or
			$failedCount -ne 0 -or
			[int]$evaluation.metrics.contract_failure_count -ne 0 -or
			[int]$evaluation.metrics.false_claim_count -ne 0 -or
			[int]$evaluation.metrics.partial_status_count -ne $fixtureCount -or
			[int]$evaluation.metrics.threat_fixture_count -lt 1 -or
			[int]$evaluation.metrics.threat_priority_passed_count -ne [int]$evaluation.metrics.threat_fixture_count -or
			[int]$evaluation.metrics.distinct_first_actions_passed_count -ne [int]$evaluation.metrics.distinct_first_actions_fixture_count -or
			[int]$evaluation.metrics.duplicate_first_action_count -ne 0 -or
			[int]$evaluation.metrics.unknown_source_fixture_count -lt 1 -or
			[int]$evaluation.metrics.unknown_friendly_action_blocked_count -ne [int]$evaluation.metrics.unknown_source_fixture_count -or
			[int]$evaluation.metrics.approximation_fixture_count -lt 1 -or
			[int]$evaluation.metrics.approximation_passed_count -ne [int]$evaluation.metrics.approximation_fixture_count) {
			$result.Summary = "FAIL (fixtures=$passedCount/$fixtureCount, minimum=$MinimumFixtureCount, failed=$failedCount, false-claims=$($evaluation.metrics.false_claim_count))"
			return [pscustomobject]$result
		}
		$reportedHash = ([string]$evaluation.binary.sha256).Trim().ToLowerInvariant()
		if ($reportedHash -notmatch '^[0-9a-f]{64}$') {
			$result.Summary = "Invalid report (binary hash is malformed)"
			return [pscustomobject]$result
		}
		$actualHash = (Get-FileHash -LiteralPath $ExpectedBinaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
		if ($reportedHash -ne $actualHash) {
			$result.Summary = "Invalid report (binary changed after visible-response verification)"
			return [pscustomobject]$result
		}
		$result.BinarySha256 = $actualHash
		if (-not $evaluation.passed -or -not $evaluation.binary.available -or
			$evaluation.status -ne "passed") {
			$result.Summary = "FAIL (fixtures=$passedCount/$fixtureCount, status=$($evaluation.status))"
			return [pscustomobject]$result
		}
		$result.Ready = $true
		$result.Summary = "PASS (suite=visible-response-v1, fixtures=$passedCount/$fixtureCount, false-claims=0, SHA256=$actualHash)"
		return [pscustomobject]$result
	} catch {
		$result.Summary = "Invalid report"
		return [pscustomobject]$result
	}
}

function Invoke-ReleaseGateSelfTest {
	$repoRoot = Get-ReleaseGateRepoRoot
	$csc = Find-ReleaseGateRoslynCompiler "" $repoRoot
	if (-not (Test-Path -LiteralPath $csc)) { throw "Roslyn self-test failed." }
	$frameworkPath = Resolve-ReleaseGateFrameworkPath $repoRoot
	if (-not (Test-Path -LiteralPath (Join-Path $frameworkPath "mscorlib.dll") -PathType Leaf)) {
		throw ".NET Framework reference assembly self-test failed."
	}

	$cleanHits = Test-ReleaseGateSensitiveText "Cookie: `$cookieHeader"
	if ($cleanHits.Count -ne 0) { throw "Placeholder cookie text should not be flagged." }
	$leakedCookie = "Cookie: " + "sessionid=" + ("a" * 24)
	if ((Test-ReleaseGateSensitiveText $leakedCookie).Count -eq 0) { throw "Cookie leak was not detected." }
	$leakedBearer = "Bearer " + ("b" * 24)
	if ((Test-ReleaseGateSensitiveText $leakedBearer).Count -eq 0) { throw "Bearer leak was not detected." }

	if ((ConvertTo-ReleaseGateEntryPath "\tools\Update-MetaCompanionData.ps1") -ne "tools/Update-MetaCompanionData.ps1") { throw "Package paths should be normalized." }
	if (-not (Get-ReleaseGateBlockedPackageReason "tools/Update-MetaCompanionData.ps1")) { throw "Tool script package block failed." }
	if (Get-ReleaseGateBlockedPackageReason "tools/Sync-HdtArenaAdvisorData.ps1") { throw "Anonymized arena exporter should be allowed." }
	if (Get-ReleaseGateBlockedPackageReason "tools/Sync-BlizzardCardPools.ps1") { throw "Public Blizzard card pool sync tool should be allowed." }
	if (Get-ReleaseGateBlockedPackageReason "tools/Update-AdvisorBehaviorPrior.ps1") { throw "Fail-closed behavior-prior updater should be allowed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "Premium/Meta/latest/cache.json")) { throw "Premium package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorData/Arena/latest/manifest.json")) { throw "Local advisor snapshot package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "ArenaLastDrafts.xml")) { throw "Raw arena draft package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "Replays/private-match.hdtreplay")) { throw "Raw HDT replay package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/data/training.jsonl")) { throw "Advisor training data package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/data/training-v2.jsonl")) { throw "Versioned advisor training data package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "HistoricalReplayImport/training-v2-results.jsonl")) { throw "Imported replay results package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "HistoricalReplayImport/hdt-replay-import-v1.json")) { throw "Replay import manifest package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/behavior-v1.jsonl")) { throw "Advisor behavior data package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "HistoricalReplayImport/advisor-decision-frame-v1.jsonl")) { throw "Advisor decision-frame data package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "HistoricalReplayImport/decision-frame-readiness.json")) { throw "Advisor decision-frame report package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "HistoricalReplayImport/decision-solver-coverage.json")) { throw "Decision-solver coverage report package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/behavior-imitation-v1.jsonl")) { throw "Advisor imitation data package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/behavior-imitation-v1.manifest.json")) { throw "Advisor imitation manifest package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/behavior-prior-v1.json")) { throw "Local behavior-prior model package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/behavior-prior-v1.install.json")) { throw "Local behavior-prior install manifest package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/decision-ranker-v1.json")) { throw "Local decision-ranker model package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/observed-policy-evaluation-v1.json")) { throw "Local observed-policy evaluation package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/advisor-ordering-models-v1.install.json")) { throw "Local dual-model install manifest package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "AdvisorWorker/advisor-model-archive/decision-ranker-v1.json/hash.json")) { throw "Local dual-model archive package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "behavior-outbox-v1/private-game/0001.json")) { throw "Behavior outbox package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "result-outbox-v1/0001.json")) { throw "Result outbox package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "solver/__pycache__/cli.pyc")) { throw "Python cache package block failed." }
	if (-not (Get-ReleaseGateBlockedPackageReason "local_meta_environment.tsv")) { throw "Local meta package block failed." }
	if (Get-ReleaseGateBlockedPackageReason "MetaCompanion.dll") { throw "DLL should be allowed." }
	if (Get-ReleaseGateBlockedPackageReason "docs/RELEASE-CHECKLIST.md") { throw "Docs should be allowed." }
	if ((Get-ReleaseGateTextFileExtensions) -notcontains ".py" -or
		(Get-ReleaseGateTextFileExtensions) -notcontains ".toml" -or
		(Get-ReleaseGateTextFileExtensions) -notcontains ".jsonl") { throw "Advisor worker text files must be secret-scanned." }

	if (Test-ReleaseGateRepoScanPath "packages/Microsoft.Net.Compilers.4.2.0/tools/csc.exe") { throw "Repo scan fallback should skip packages." }
	if (Test-ReleaseGateRepoScanPath ".idea/workspace.xml") { throw "Repo scan fallback should skip IDE state." }
	if (Test-ReleaseGateRepoScanPath "artifacts/release-gate/report.md") { throw "Repo scan fallback should skip artifacts." }
	if (-not (Test-ReleaseGateRepoScanPath "tools/Invoke-ReleaseGate.ps1")) { throw "Repo scan fallback should include source tools." }
	if (-not (Test-ReleaseGateRepoScanPath "solver/metacompanion_solver/cli.py")) { throw "Repo scan fallback should include solver source." }
	if (-not (Test-ReleaseGateRepoScanPath "docs/ADVISOR-DATA.md")) { throw "Repo scan fallback should include advisor data docs." }
	if (-not (Test-ReleaseGateRepoScanPath "docs/OFFICIAL-CARD-POOLS.md")) { throw "Repo scan fallback should include official card pool docs." }
	if (-not (Test-ReleaseGateRepoScanPath "dist/Install-MetaCompanion.ps1")) { throw "Repo scan fallback should include tracked dist installer scripts." }
	if (-not (Test-ReleaseGateRepoScanPath "dist/Wait-AndInstall-MetaCompanion.ps1")) { throw "Repo scan fallback should include tracked dist installer scripts." }
	if (Test-ReleaseGateRepoScanPath "dist/MetaCompanion.dll") { throw "Repo scan fallback should skip dist build outputs." }
	if (Test-ReleaseGateRepoScanPath "dist/MetaCompanion-community.zip") { throw "Repo scan fallback should skip dist build outputs." }

	foreach ($installerPath in @(
		(Join-Path $repoRoot "tools\Install-MetaCompanion.ps1"),
		(Join-Path $repoRoot "dist\Install-MetaCompanion.ps1")
	)) {
		$installerText = [System.IO.File]::ReadAllText($installerPath)
		if ($installerText -notmatch [regex]::Escape("Update-AdvisorBehaviorPrior.ps1") -or
			$installerText -notmatch [regex]::Escape("Copy-BehaviorPriorUpdateTool")) {
			throw "Community installer must install the behavior-prior updater."
		}
	}
	$behaviorPriorUpdaterText = [System.IO.File]::ReadAllText(
		(Join-Path $repoRoot "tools\Update-AdvisorBehaviorPrior.ps1"))
	if ($behaviorPriorUpdaterText -notmatch [regex]::Escape("..\AdvisorWorker")) {
		throw "Behavior-prior updater must resolve the installed AdvisorWorker."
	}
	foreach ($requiredUpdaterContract in @(
		"train-decision-ranker",
		"evaluate-observed-policy",
		"decision-ranker-check",
		"advisor-ordering-models-v1.install.json",
		"transactional_pair = `$true",
		"HistoricalSourceDirectory"
	)) {
		if ($behaviorPriorUpdaterText -notmatch [regex]::Escape($requiredUpdaterContract)) {
			throw "Dual-model updater contract is missing: $requiredUpdaterContract"
		}
	}

	$rustParitySelfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ("MetaCompanionRustParityTest-" + [guid]::NewGuid().ToString("N"))
	try {
		New-Item -ItemType Directory -Force -Path $rustParitySelfTestRoot | Out-Null
		$fakeRustBinary = Join-Path $rustParitySelfTestRoot "metacompanion-solver.exe"
		Set-Content -LiteralPath $fakeRustBinary -Value "verified-rust-binary" -Encoding UTF8
		$fakeRustHash = (Get-FileHash -LiteralPath $fakeRustBinary -Algorithm SHA256).Hash.ToLowerInvariant()
		$fakeRustHashUpper = $fakeRustHash.ToUpperInvariant()
		$fakeRustReport = Join-Path $rustParitySelfTestRoot "combat.json"
		@{
			schema = "metacompanion-rust-parity-report-v1"
			profile = "combat-v1"
			passed = $true
			status = "passed"
			binary = @{ available = $true; sha256 = $fakeRustHash }
			metrics = @{ fixture_count = 7; passed_fixture_count = 7; failed_fixture_count = 0 }
		} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fakeRustReport -Encoding UTF8
		$validRustStatus = Get-ReleaseGateRustParityStatus `
			$fakeRustReport $true "combat-v1" $minimumRustCombatFixtureCount $fakeRustBinary
		if (-not $validRustStatus.Ready -or $validRustStatus.Summary -notlike "PASS*") {
			throw "Valid Rust parity report was not accepted."
		}
		$fakeRustFullReport = Join-Path $rustParitySelfTestRoot "full.json"
		@{
			schema = "metacompanion-rust-parity-report-v1"
			profile = "full"
			passed = $true
			status = "passed"
			binary = @{ available = $true; sha256 = $fakeRustHash }
			metrics = @{ fixture_count = 39; passed_fixture_count = 39; failed_fixture_count = 0 }
		} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fakeRustFullReport -Encoding UTF8
		$undersizedRustFullStatus = Get-ReleaseGateRustParityStatus `
			$fakeRustFullReport $true "full" $minimumRustFullFixtureCount $fakeRustBinary
		if ($undersizedRustFullStatus.Ready -or $undersizedRustFullStatus.Summary -notlike "FAIL*") {
			throw "Rust full parity fixture floor did not reject a 39-case report."
		}
		@{
			schema = "metacompanion-rust-parity-report-v1"
			profile = "full"
			passed = $true
			status = "passed"
			binary = @{ available = $true; sha256 = $fakeRustHash }
			metrics = @{ fixture_count = 40; passed_fixture_count = 40; failed_fixture_count = 0 }
		} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fakeRustFullReport -Encoding UTF8
		$validRustFullStatus = Get-ReleaseGateRustParityStatus `
			$fakeRustFullReport $true "full" $minimumRustFullFixtureCount $fakeRustBinary
		if (-not $validRustFullStatus.Ready -or $validRustFullStatus.Summary -notlike "PASS*") {
			throw "Rust full parity fixture floor rejected a 40-case report."
		}
		$fakeVisibleResponseReport = Join-Path $rustParitySelfTestRoot "visible-response.json"
		$visibleResponseFixture = @{
			schema = "visible-response-report-v1"
			suite_id = "visible-response-v1"
			passed = $true
			status = "passed"
			binary = @{ available = $true; sha256 = $fakeRustHash }
			metrics = @{
				fixture_count = 3
				passed_fixture_count = 3
				failed_fixture_count = 0
				contract_failure_count = 0
				false_claim_count = 0
				partial_status_count = 3
				threat_fixture_count = 1
				threat_priority_passed_count = 1
				distinct_first_actions_fixture_count = 3
				distinct_first_actions_passed_count = 3
				duplicate_first_action_count = 0
				unknown_source_fixture_count = 1
				unknown_friendly_action_blocked_count = 1
				approximation_fixture_count = 1
				approximation_passed_count = 1
			}
		}
		$visibleResponseFixture | ConvertTo-Json -Depth 5 |
			Set-Content -LiteralPath $fakeVisibleResponseReport -Encoding UTF8
		$validVisibleResponseStatus = Get-ReleaseGateVisibleResponseStatus `
			$fakeVisibleResponseReport $true $minimumVisibleResponseFixtureCount $fakeRustBinary
		if (-not $validVisibleResponseStatus.Ready -or
			$validVisibleResponseStatus.Summary -notlike "PASS*") {
			throw "Valid visible-response report was not accepted."
		}
		$visibleResponseFixture.metrics.false_claim_count = 1
		$visibleResponseFixture | ConvertTo-Json -Depth 5 |
			Set-Content -LiteralPath $fakeVisibleResponseReport -Encoding UTF8
		$falseClaimVisibleResponseStatus = Get-ReleaseGateVisibleResponseStatus `
			$fakeVisibleResponseReport $true $minimumVisibleResponseFixtureCount $fakeRustBinary
		if ($falseClaimVisibleResponseStatus.Ready -or
			$falseClaimVisibleResponseStatus.Summary -notlike "FAIL*") {
			throw "Visible-response false-claim metric did not block promotion."
		}
		$fakeCardPoolReport = Join-Path $rustParitySelfTestRoot "official-card-pool.json"
		@{
			schema = "metacompanion-rust-official-card-pool-gate-v1"
			passed = $true
			binary_sha256 = $fakeRustHashUpper
			check_count = 5
			checks = @{
				valid_python_rust_interop = $true
				publish_manifest_binding = $true
				page_freshness = $true
				card_defs_binding = $true
				duplicate_identity = $true
			}
			standard_count = 1
			arena_count = 1
			rules_coverage = $false
			enforces_action_legality = $false
		} | ConvertTo-Json -Depth 5 |
			Set-Content -LiteralPath $fakeCardPoolReport -Encoding UTF8
		$validCardPoolStatus = Get-ReleaseGateRustCardPoolStatus `
			$fakeCardPoolReport $fakeRustBinary $true $false
		if (-not $validCardPoolStatus.Ready -or
			$validCardPoolStatus.Summary -notlike "PASS*") {
			throw "Valid Rust official card-pool report was not accepted."
		}
		Set-Content -LiteralPath $fakeRustBinary -Value "binary-changed-after-gate" -Encoding UTF8
		$changedRustStatus = Get-ReleaseGateRustParityStatus `
			$fakeRustReport $true "combat-v1" $minimumRustCombatFixtureCount $fakeRustBinary
		if ($changedRustStatus.Ready -or $changedRustStatus.Summary -notlike "Invalid report*") {
			throw "Changed Rust binary was not rejected."
		}
		$changedCardPoolStatus = Get-ReleaseGateRustCardPoolStatus `
			$fakeCardPoolReport $fakeRustBinary $true $false
		if ($changedCardPoolStatus.Ready -or
			$changedCardPoolStatus.Summary -notlike "Invalid report*") {
			throw "Official card-pool gate did not reject a changed Rust binary."
		}
		$notRequestedRustStatus = Get-ReleaseGateRustParityStatus `
			"" $false "combat-v1" $minimumRustCombatFixtureCount ""
		if ($notRequestedRustStatus.Ready -or $notRequestedRustStatus.Summary -ne "Not requested") {
			throw "Optional Rust parity status is inconsistent."
		}
	} finally {
		if (Test-Path -LiteralPath $rustParitySelfTestRoot) {
			Remove-Item -LiteralPath $rustParitySelfTestRoot -Recurse -Force
		}
	}

	$packageSelfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ("MetaCompanionReleaseGatePackageTest-" + [guid]::NewGuid().ToString("N"))
	try {
		New-Item -ItemType Directory -Force -Path $packageSelfTestRoot | Out-Null
		$fakeDll = Join-Path $packageSelfTestRoot "MetaCompanion.dll"
		Set-Content -LiteralPath $fakeDll -Value "release-gate self-test" -Encoding UTF8
		$packagePath = New-ReleaseGateCommunityPackage $repoRoot $fakeDll $packageSelfTestRoot
		$entries = @(Get-ReleaseGatePackageEntries $packagePath)
		foreach ($required in @(
			"tools/Sync-HdtArenaAdvisorData.ps1",
			"tools/Sync-BlizzardCardPools.ps1",
			"tools/Update-AdvisorBehaviorPrior.ps1",
			"solver/launch_solver.py",
			"solver/metacompanion_solver/cli.py",
			"solver/metacompanion_solver/behavior_candidate_alignment.py",
			"solver/metacompanion_solver/behavior.py",
			"solver/metacompanion_solver/behavior_learning.py",
			"solver/metacompanion_solver/behavior_prior.py",
			"solver/metacompanion_solver/card_rules.py",
			"solver/metacompanion_solver/counterplay.py",
			"solver/metacompanion_solver/decision_frame.py",
			"solver/metacompanion_solver/decision_ranker.py",
			"solver/metacompanion_solver/decision_solver_evaluation.py",
			"solver/metacompanion_solver/evaluation.py",
			"solver/metacompanion_solver/hdt_rule_evaluation.py",
			"solver/metacompanion_solver/hdt_replay_behavior.py",
			"solver/metacompanion_solver/observed_policy_evaluation.py",
			"solver/metacompanion_solver/rust_worker_client.py",
			"solver/metacompanion_solver/visible_response_evaluation.py",
			"solver/metacompanion_solver/trajectory.py",
			"solver/metacompanion_solver/verification.py",
			"solver/metacompanion_solver/turnpair_evaluation.py",
			"solver/metacompanion_solver/rules_data/hdt-visible-point-effects-v1.json",
			"solver/fixtures/oracle-hdt-cardrules-v1.json",
			"solver/fixtures/oracle-turn-v1.json",
			"solver/fixtures/oracle-turnpair-v1.json",
			"solver/fixtures/visible-response-v1.json",
			"solver/fixtures/trajectory-readiness-policy-v1.json",
			"solver/fixtures/trajectory-readiness-v1.jsonl",
			"solver/fixtures/behavior-learning-readiness-policy-v1.json",
			"solver/fixtures/behavior-learning-readiness-v1.jsonl",
			"solver/fixtures/behavior-candidate-alignment-policy-v1.json",
			"solver/fixtures/behavior-prior-readiness-policy-v1.json",
			"solver/fixtures/behavior-prior-readiness-v1.jsonl",
			"solver/fixtures/behavior-prior-readiness-v1.manifest.json",
			"solver/tools/observed_policy_fixture.py",
			"docs/ADVISOR-DATA.md",
			"docs/OFFICIAL-CARD-POOLS.md"
		)) {
			if ($entries -notcontains $required) { throw "Package self-test is missing $required" }
		}
		if (@($entries | Where-Object {
			$_ -match "(^|/)(tests|__pycache__|\.pytest_cache|data)(/|$)" -or $_ -match "\.(pyc|pyo)$"
		}).Count -ne 0) { throw "Package self-test found solver test/cache/data files." }
		$packageScan = Search-ReleaseGatePackageSecrets $packagePath
		if ($packageScan.Matches.Count -ne 0) { throw "Generated community package failed its secret scan." }
	} finally {
		if (Test-Path -LiteralPath $packageSelfTestRoot) {
			Remove-Item -LiteralPath $packageSelfTestRoot -Recurse -Force
		}
	}

	$logSelfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ("MetaCompanionReleaseGateLogTest-" + [guid]::NewGuid().ToString("N"))
	try {
		New-Item -ItemType Directory -Force -Path $logSelfTestRoot | Out-Null
		$buildLog = Join-Path $logSelfTestRoot "build.log"
		$testLog = Join-Path $logSelfTestRoot "tests.log"
		$utf8Json = Join-Path $logSelfTestRoot "utf8-without-bom.json"
		$utf8Value = '"\u4e2d\u6587\u95e8\u7981"' | ConvertFrom-Json
		$utf8JsonBytes = [System.Text.Encoding]::UTF8.GetBytes(
			(@{ caveat_zh = $utf8Value } | ConvertTo-Json)
		)
		[System.IO.File]::WriteAllBytes($utf8Json, $utf8JsonBytes)
		if ((Read-ReleaseGateUtf8Json $utf8Json).caveat_zh -cne $utf8Value) {
			throw "Observed-policy fixture summary should parse UTF-8 without a BOM."
		}
		$updaterSelfTestLog = Join-Path $logSelfTestRoot "advisor-model-updater-self-test.log"
		[System.IO.File]::WriteAllBytes(
			$updaterSelfTestLog,
			[System.Text.Encoding]::UTF8.GetBytes("SELFTEST_OK=1`n")
		)
		if ((Get-ReleaseGateAdvisorModelUpdaterSummary $updaterSelfTestLog $false) -notlike "PASS*") {
			throw "Dual-model updater summary should use an encoding-stable ASCII marker."
		}
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
		$hdtRuleReport = Join-Path $logSelfTestRoot "hdt-rule-evaluation.json"
		@{
			passed = $true
			metrics = @{
				top1_rate = 1.0
				top3_rate = 1.0
				friendly_action_legality_rate = 1.0
				response_action_legality_rate = 1.0
				mean_minimax_regret = 0
				max_minimax_regret = 0
				false_safe_count = 0
				false_exact_count = 0
				rule_provenance_failure_count = 0
				abstain_violation_count = 0
				fixture_contract_failure_count = 0
				latency_p95_ms = 250
			}
		} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $hdtRuleReport -Encoding UTF8
		$hdtRuleSummary = Get-ReleaseGateHdtRuleEvaluationSummary $hdtRuleReport $false
		if ($hdtRuleSummary -notlike "PASS*" -or
			$hdtRuleSummary -notmatch "friendly-legal=1" -or
			$hdtRuleSummary -notmatch "provenance=0" -or
			$hdtRuleSummary -notmatch "P95=250ms") {
			throw "HDT rule evaluation summary should parse all release metrics."
		}
		if ((Get-ReleaseGateHdtRuleEvaluationSummary $hdtRuleReport $true) -ne "Skipped") {
			throw "Skipped HDT rule evaluation summary should be explicit."
		}
		Set-Content -LiteralPath $hdtRuleReport -Value '{"passed":true,"metrics":{}}' -Encoding UTF8
		if ((Get-ReleaseGateHdtRuleEvaluationSummary $hdtRuleReport $false) -ne "Invalid report") {
			throw "Incomplete HDT rule evaluation reports should be invalid."
		}

		$trajectoryReport = Join-Path $logSelfTestRoot "trajectory-readiness.json"
		$trajectoryCaveat = "Training-ready means only that anonymized trajectories satisfy this versioned join, exact-action, replay, split, and privacy contract. It does not prove that the online solver is healthy or optimal, that labels are unbiased, or that an RL policy exists; solver_runtime_ready reports online solve health separately."
		$trajectoryFixture = @{
			schema = "trajectory-readiness-report-v1"
			trajectory_schema = "trajectory-readiness-v1"
			source_kind = "synthetic_fixture"
			input_sha256 = ("1" * 64)
			input_bytes = 9503
			policy_sha256 = ("2" * 64)
			contract_passed = $true
			training_ready = $true
			passed = $true
			caveat = $trajectoryCaveat
			solve_status_semantics = @{
				schema = "solve-status-semantics-v1"
				policy_buckets = @("ok", "partial", "cancelled", "unsupported", "non_ok")
				non_ok_members = @("error", "other")
				unsuccessful_members = @("partial", "cancelled", "unsupported", "error", "other")
			}
			issues = @{
				reason_counts = @{
					parse = @{}
					contract = @{}
					privacy = @{}
					replay = @{}
					candidate = @{}
					chain = @{}
				}
				all_reason_counts = @{}
				truncated_counts = @{}
			}
			verified_transitions = @(
				@{
					game_id = "anon-1111111111111111"
					action_sequence = 1
					pre_state_id = "g1-pre"
					post_state_id = "g1-post"
					normalized_pre_state_hash = ("a" * 64)
					normalized_post_state_hash = ("b" * 64)
				},
				@{
					game_id = "anon-2222222222222222"
					action_sequence = 1
					pre_state_id = "g2-pre"
					post_state_id = "g2-post"
					normalized_pre_state_hash = ("c" * 64)
					normalized_post_state_hash = ("d" * 64)
				}
			)
			metrics = @{
				record_count = 10
				invalid_json_or_record_count = 0
				contract_issue_count = 0
				privacy_violation_count = 0
				solve_record_count = 6
				ok_solve_count = 6
				partial_solve_count = 0
				cancelled_solve_count = 0
				unsupported_solve_count = 0
				error_solve_count = 0
				other_solve_count = 0
				non_ok_solve_count = 0
				unsuccessful_solve_count = 0
				unique_game_count = 2
				canonical_decision_count = 4
				action_observation_count = 2
				exact_action_count = 2
				replayable_transition_count = 2
				candidate_transition_count = 0
				candidate_evidence_consistent_count = 0
				candidate_boundary_failure_count = 0
				candidate_state_binding_failure_count = 0
				candidate_state_hash_mismatch_count = 0
				candidate_snapshot_sequence_mismatch_count = 0
				candidate_state_order_failure_count = 0
				terminal_result_game_count = 2
				joined_decision_count = 4
				duplicate_solve_count = 0
				conflicting_final_solve_count = 0
				state_content_conflict_count = 0
				cross_game_state_id_reuse_count = 0
				conflicting_result_game_count = 0
				duplicate_result_observation_count = 0
				replay_failure_count = 0
				duplicate_action_sequence_count = 0
				non_contiguous_action_sequence_game_count = 0
				action_order_violation_count = 0
				action_chain_break_count = 0
				action_decision_join_failure_count = 0
				pre_state_order_violation_count = 0
				post_state_order_violation_count = 0
				terminal_before_last_action_count = 0
				terminal_state_mismatch_count = 0
				split_assignment_mismatch_count = 0
				cross_split_leakage_count = 0
				solve_result_join_rate = 1.0
				exact_action_rate = 1.0
				replayable_transition_rate = 1.0
				partial_action_rate = 0.0
				ok_solve_rate = 1.0
				partial_solve_rate = 0.0
				cancelled_solve_rate = 0.0
				unsupported_solve_rate = 0.0
				error_solve_rate = 0.0
				other_solve_rate = 0.0
				non_ok_solve_rate = 0.0
				unsuccessful_solve_rate = 0.0
			}
		}
		$trajectoryFixture | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $trajectoryReport -Encoding UTF8
		$trajectorySummary = Get-ReleaseGateTrajectoryAuditorFixtureSummary $trajectoryReport $false
		if ($trajectorySummary -notlike "PASS*" -or
			$trajectorySummary -notmatch "contract=True" -or
			$trajectorySummary -notmatch "fixture-ready=True" -or
			$trajectorySummary -notmatch "source=synthetic_fixture" -or
			$trajectorySummary -notmatch "non-ok=0" -or
			$trajectorySummary -notmatch "unsuccessful-rate=0" -or
			$trajectorySummary -notmatch "integrity-anomalies=0" -or
			$trajectorySummary -notmatch "caveat=verified") {
			throw "Trajectory auditor fixture summary should strictly parse source, hashes, flags, metrics, and caveat."
		}
		if ((Get-ReleaseGateTrajectoryAuditorFixtureSummary $trajectoryReport $true) -ne "Skipped") {
			throw "Skipped trajectory readiness summary should be explicit."
		}
		$trajectoryFixture.training_ready = $false
		$trajectoryFixture.passed = $false
		$trajectoryFixture | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $trajectoryReport -Encoding UTF8
		if ((Get-ReleaseGateTrajectoryAuditorFixtureSummary $trajectoryReport $false) -notlike "FAIL*") {
			throw "Non-ready trajectory audit should fail the release summary."
		}
		$trajectoryFixture.training_ready = $true
		$trajectoryFixture.passed = $true
		$trajectoryFixture.schema = "trajectory-readiness-report-v0"
		$trajectoryFixture | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $trajectoryReport -Encoding UTF8
		if ((Get-ReleaseGateTrajectoryAuditorFixtureSummary $trajectoryReport $false) -ne "Invalid report (schema mismatch)") {
			throw "Trajectory readiness schema drift should be invalid."
		}
		$trajectoryFixture.schema = "trajectory-readiness-report-v1"
		$trajectoryFixture.caveat = "Training ready."
		$trajectoryFixture | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $trajectoryReport -Encoding UTF8
		if ((Get-ReleaseGateTrajectoryAuditorFixtureSummary $trajectoryReport $false) -ne "Invalid report (required caveat mismatch)") {
			throw "Trajectory readiness caveat drift should be invalid."
		}

		$behaviorReport = Join-Path $logSelfTestRoot "behavior-learning-readiness.json"
		$notOptimalBehaviorCaveat = '"\u4e0d\u7b49\u4e8e\u6700\u4f18\u52a8\u4f5c"' |
			ConvertFrom-Json
		$reinforcementLearningCaveat = '"\u5f3a\u5316\u5b66\u4e60"' |
			ConvertFrom-Json
		$behaviorCaveat =
			"Observed behavior $notOptimalBehaviorCaveat; it is not $reinforcementLearningCaveat data."
		$behaviorFixture = @{
			schema = "behavior-learning-readiness-report-v1"
			behavior_schema = "advisor-behavior-v1"
			trajectory_schema = "trajectory-readiness-v1"
			source_kind = "synthetic_fixture"
			behavior_input = "behavior-learning-readiness-v1.jsonl"
			behavior_input_sha256 = ("4" * 64)
			behavior_input_bytes = 4200
			trajectory_input = "trajectory-readiness-v1.jsonl"
			trajectory_input_sha256 = ("5" * 64)
			trajectory_input_bytes = 9503
			policy_sha256 = ("6" * 64)
			contract_passed = $true
			imitation_ready = $true
			rl_training_ready = $false
			passed = $true
			caveat = $behaviorCaveat
			metrics = @{
				behavior_record_count = 3
				behavior_invalid_record_count = 0
				unique_behavior_game_count = 1
				behavior_eligible_record_count = 3
				local_eligible_record_count = 2
				opponent_eligible_record_count = 1
				board_position_record_count = 1
				choice_item_count = 1
				offered_choice_entity_count = 2
				selected_choice_entity_count = 1
				replay_behavior_record_count = 0
				replay_play_card_record_count = 0
				replay_play_source_still_actor_hand_post_count = 0
				replay_play_source_still_actor_hand_post_affected_game_count = 0
				replay_play_source_left_actor_hand_post_rate = 0.0
				replay_attack_record_count = 0
				replay_attack_source_readiness_explicit_count = 0
				replay_attack_source_readiness_explicit_rate = 0.0
				replay_end_turn_record_count = 0
				replay_end_turn_active_player_unchanged_count = 0
				distinct_action_kind_count = 2
				both_side_game_count = 1
				terminal_result_game_count = 1
				joined_result_game_count = 1
				joined_behavior_record_count = 3
				joined_behavior_eligible_record_count = 3
				behavior_without_result_game_count = 0
				conflicting_result_game_count = 0
				duplicate_result_observation_count = 0
				sequence_order_violation_count = 0
				timestamp_regression_count = 0
				result_join_rate = 1.0
				both_side_game_rate = 1.0
				behavior_eligible_rate = 1.0
				unknown_actor_rate = 0.0
				unknown_identity_rate = 0.0
				actor_side_counts = @{
					local = 2
					opponent = 1
				}
				choice_status_counts = @{
					not_observed = 2
					selected = 1
				}
			}
			contract_checks = @(
				@{
					name = "replay_play_source_still_actor_hand_post_count"
					actual = 0
					operator = "<="
					expected = 0
					passed = $true
				},
				@{
					name = "replay_attack_source_readiness_missing_count"
					actual = 0
					operator = "<="
					expected = 0
					passed = $true
				},
				@{
					name = "replay_end_turn_active_player_unchanged_count"
					actual = 0
					operator = "<="
					expected = 0
					passed = $true
				}
			)
			behavior_audit = @{
				valid = $true
				privacy_violation_count = 0
			}
			trajectory_contract_audit = @{
				contract_passed = $true
			}
		}
		$behaviorJson = $behaviorFixture | ConvertTo-Json -Depth 6
		[System.IO.File]::WriteAllBytes(
			$behaviorReport,
			[System.Text.Encoding]::UTF8.GetBytes($behaviorJson)
		)
		$behaviorSummary =
			Get-ReleaseGateBehaviorLearningFixtureSummary $behaviorReport $false
		if ($behaviorSummary -notlike "PASS*" -or
			$behaviorSummary -notmatch "contract=True" -or
			$behaviorSummary -notmatch "imitation-ready=True" -or
			$behaviorSummary -notmatch "rl-ready=False" -or
			$behaviorSummary -notmatch "local=2" -or
			$behaviorSummary -notmatch "opponent=1") {
			throw "Behavior learning fixture summary should parse UTF-8 without a BOM."
		}
		if ((Get-ReleaseGateBehaviorLearningFixtureSummary $behaviorReport $true) -ne "Skipped") {
			throw "Skipped behavior learning fixture summary should be explicit."
		}
		$behaviorFixture.metrics.choice_item_count = 0
		$behaviorJson = $behaviorFixture | ConvertTo-Json -Depth 6
		[System.IO.File]::WriteAllBytes(
			$behaviorReport,
			[System.Text.Encoding]::UTF8.GetBytes($behaviorJson)
		)
		if ((Get-ReleaseGateBehaviorLearningFixtureSummary $behaviorReport $false) -ne
			"Invalid report (fixture must cover a complete selected choice)") {
			throw "Behavior learning fixture must fail when selected-choice coverage disappears."
		}
		$behaviorFixture.metrics.choice_item_count = 1
		$behaviorFixture.caveat = "Observed behavior only."
		$behaviorJson = $behaviorFixture | ConvertTo-Json -Depth 6
		[System.IO.File]::WriteAllBytes(
			$behaviorReport,
			[System.Text.Encoding]::UTF8.GetBytes($behaviorJson)
		)
		if ((Get-ReleaseGateBehaviorLearningFixtureSummary $behaviorReport $false) -ne
			"Invalid report (required behavior caveat mismatch)") {
			throw "Behavior learning fixture caveat drift should be invalid."
		}
		$behaviorFixture.caveat = $behaviorCaveat
		$behaviorFixture.imitation_ready = $false
		$behaviorFixture.passed = $false
		$behaviorJson = $behaviorFixture | ConvertTo-Json -Depth 6
		[System.IO.File]::WriteAllBytes(
			$behaviorReport,
			[System.Text.Encoding]::UTF8.GetBytes($behaviorJson)
		)
		if ((Get-ReleaseGateBehaviorLearningFixtureSummary $behaviorReport $false) -notlike
			"FAIL*") {
			throw "Non-ready behavior learning fixture should fail the release summary."
		}

		$behaviorPriorDataset = Join-Path $repoRoot "solver\fixtures\behavior-prior-readiness-v1.jsonl"
		$behaviorPriorManifest = Join-Path $repoRoot "solver\fixtures\behavior-prior-readiness-v1.manifest.json"
		$behaviorPriorPolicy = Join-Path $repoRoot "solver\fixtures\behavior-prior-readiness-policy-v1.json"
		$behaviorPriorArtifact = Join-Path $logSelfTestRoot "behavior-prior-fixture.json"
		$behaviorPriorLog = Join-Path $logSelfTestRoot "behavior-prior-fixture.log"
		$selfTestPython = Resolve-ReleaseGatePythonPath ""
		Invoke-ReleaseGateCommand "Behavior prior synthetic-fixture self-test" $selfTestPython @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"train-behavior-prior",
			"--input", $behaviorPriorDataset,
			"--manifest", $behaviorPriorManifest,
			"--policy", $behaviorPriorPolicy,
			"--output", $behaviorPriorArtifact
		) $behaviorPriorLog | Out-Null
		$behaviorPriorSummary = Get-ReleaseGateBehaviorPriorFixtureSummary `
			$behaviorPriorArtifact `
			$false `
			$behaviorPriorDataset `
			$behaviorPriorManifest `
			$behaviorPriorPolicy
		if ($behaviorPriorSummary -notlike "PASS*" -or
			$behaviorPriorSummary -notmatch "train=2/2" -or
			$behaviorPriorSummary -notmatch "validation=2/2" -or
			$behaviorPriorSummary -notmatch "test=2/2" -or
			$behaviorPriorSummary -notmatch "live-policy=False" -or
			$behaviorPriorSummary -notmatch "rl-ready=False" -or
			$behaviorPriorSummary -notmatch "optimality=False") {
			throw "Behavior prior fixture summary should strictly parse split isolation, readiness, and safety flags."
		}
		if ((Get-ReleaseGateBehaviorPriorFixtureSummary `
				$behaviorPriorArtifact `
				$true `
				$behaviorPriorDataset `
				$behaviorPriorManifest `
				$behaviorPriorPolicy) -ne "Skipped") {
			throw "Skipped behavior prior fixture summary should be explicit."
		}
		$behaviorPriorFixture = [System.IO.File]::ReadAllText(
			$behaviorPriorArtifact,
			[System.Text.Encoding]::UTF8
		) | ConvertFrom-Json
		$behaviorPriorFixture.search_ordering_prior_ready = $false
		$behaviorPriorJson = $behaviorPriorFixture | ConvertTo-Json -Depth 30
		[System.IO.File]::WriteAllBytes(
			$behaviorPriorArtifact,
			[System.Text.Encoding]::UTF8.GetBytes($behaviorPriorJson)
		)
		if ((Get-ReleaseGateBehaviorPriorFixtureSummary `
				$behaviorPriorArtifact `
				$false `
				$behaviorPriorDataset `
				$behaviorPriorManifest `
				$behaviorPriorPolicy) -ne
			"Invalid report (behavior prior readiness or safety flags mismatch)") {
			throw "Behavior prior readiness tampering should fail closed."
		}
		$behaviorPriorFixture.search_ordering_prior_ready = $true
		$behaviorPriorFixture.quality_checks[0].passed = $false
		$behaviorPriorJson = $behaviorPriorFixture | ConvertTo-Json -Depth 30
		[System.IO.File]::WriteAllBytes(
			$behaviorPriorArtifact,
			[System.Text.Encoding]::UTF8.GetBytes($behaviorPriorJson)
		)
		if ((Get-ReleaseGateBehaviorPriorFixtureSummary `
				$behaviorPriorArtifact `
				$false `
				$behaviorPriorDataset `
				$behaviorPriorManifest `
				$behaviorPriorPolicy) -notlike "FAIL*") {
			throw "Behavior prior quality-check tampering should fail closed."
		}

		$runtimeTrajectoryReport = Join-Path $logSelfTestRoot "runtime-trajectory-readiness.json"
		$runtimeTrajectoryFixture = @{
			schema = "runtime-trajectory-readiness-report-v1"
			source_kind = "live_runtime_snapshot"
			status = "NO_DATA"
			input = "training-v2.jsonl"
			input_sha256 = ""
			input_bytes = 0
			policy_sha256 = ("3" * 64)
			snapshot = ""
			snapshot_content_addressed = $false
			contract_passed = $false
			training_ready = $false
			audit = $null
			reason = "runtime_training_log_not_found"
		}
		$runtimeTrajectoryFixture | ConvertTo-Json -Depth 5 |
			Set-Content -LiteralPath $runtimeTrajectoryReport -Encoding UTF8
		$runtimeSummary = Get-ReleaseGateRuntimeTrajectorySummary $runtimeTrajectoryReport $false
		if ($runtimeSummary -notlike "NO_DATA*" -or $runtimeSummary -notmatch "non-blocking") {
			throw "Runtime trajectory NO_DATA summary should be explicit and non-blocking."
		}

		$runtimeSnapshotBytes = [System.Text.Encoding]::UTF8.GetBytes("{}`n")
		$sha256 = [System.Security.Cryptography.SHA256]::Create()
		try {
			$runtimeSnapshotSha256 = ([BitConverter]::ToString(
				$sha256.ComputeHash($runtimeSnapshotBytes)
			)).Replace("-", "").ToLowerInvariant()
		} finally {
			$sha256.Dispose()
		}
		$runtimeSnapshotName = "training-v2.$runtimeSnapshotSha256.jsonl"
		$runtimeSnapshotDirectory = Join-Path $logSelfTestRoot "runtime-trajectory-snapshots"
		New-Item -ItemType Directory -Force -Path $runtimeSnapshotDirectory | Out-Null
		$runtimeSnapshotPath = Join-Path $runtimeSnapshotDirectory $runtimeSnapshotName
		[System.IO.File]::WriteAllBytes($runtimeSnapshotPath, $runtimeSnapshotBytes)

		$trajectoryFixture.schema = "trajectory-readiness-report-v1"
		$trajectoryFixture.source_kind = "live_runtime_snapshot"
		$trajectoryFixture.input = $runtimeSnapshotName
		$trajectoryFixture.input_sha256 = $runtimeSnapshotSha256
		$trajectoryFixture.input_bytes = $runtimeSnapshotBytes.Length
		$trajectoryFixture.policy_sha256 = ("3" * 64)
		$trajectoryFixture.contract_passed = $true
		$trajectoryFixture.training_ready = $false
		$trajectoryFixture.passed = $false
		$trajectoryFixture.caveat = $trajectoryCaveat
		$runtimeTrajectoryFixture.status = "NOT_READY"
		$runtimeTrajectoryFixture.input_sha256 = $runtimeSnapshotSha256
		$runtimeTrajectoryFixture.input_bytes = $runtimeSnapshotBytes.Length
		$runtimeTrajectoryFixture.snapshot = $runtimeSnapshotName
		$runtimeTrajectoryFixture.snapshot_content_addressed = $true
		$runtimeTrajectoryFixture.contract_passed = $true
		$runtimeTrajectoryFixture.training_ready = $false
		$runtimeTrajectoryFixture.audit = $trajectoryFixture
		$runtimeTrajectoryFixture.reason = "production_policy_failed"
		$runtimeTrajectoryFixture | ConvertTo-Json -Depth 5 |
			Set-Content -LiteralPath $runtimeTrajectoryReport -Encoding UTF8
		$runtimeSummary = Get-ReleaseGateRuntimeTrajectorySummary $runtimeTrajectoryReport $false
		if ($runtimeSummary -notlike "NOT_READY*" -or
			$runtimeSummary -notmatch "source=live_runtime_snapshot" -or
			$runtimeSummary -notmatch "unsupported=0" -or
			$runtimeSummary -notmatch "non-ok=0" -or
			$runtimeSummary -notmatch "unsuccessful-rate=0") {
			throw "Runtime trajectory NOT_READY summary should require and expose a hash-bound production snapshot audit."
		}

		[System.IO.File]::WriteAllText($runtimeSnapshotPath, "tampered")
		if ((Get-ReleaseGateRuntimeTrajectorySummary $runtimeTrajectoryReport $false) -ne
			"Invalid report (runtime snapshot bytes do not match report identity)") {
			throw "Runtime trajectory summary must reject a changed content-addressed snapshot."
		}
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
$requestedRustSolverBinaryPath = Format-ReleaseGateInputPath $RustSolverBinaryPath "Not requested"
$buildLogPath = Join-Path $runDirectory "build.log"
$testLogPath = Join-Path $runDirectory "tests.log"
$decisionFrameTestLogPath = Join-Path $runDirectory "decision-frame-tests.log"
$decisionSolverUnitTestLogPath = Join-Path $runDirectory "decision-solver-evaluation-tests.log"
$turnpairEvaluationLogPath = Join-Path $runDirectory "turnpair-evaluation.log"
$turnpairEvaluationReportPath = Join-Path $runDirectory "turnpair-evaluation.json"
$hdtRuleEvaluationLogPath = Join-Path $runDirectory "hdt-rule-evaluation.log"
$hdtRuleEvaluationReportPath = Join-Path $runDirectory "hdt-rule-evaluation.json"
$trajectoryReadinessLogPath = Join-Path $runDirectory "trajectory-auditor-fixture.log"
$trajectoryReadinessReportPath = Join-Path $runDirectory "trajectory-auditor-fixture.json"
$runtimeTrajectoryLogPath = Join-Path $runDirectory "runtime-trajectory-readiness.log"
$runtimeTrajectoryReportPath = Join-Path $runDirectory "runtime-trajectory-readiness.json"
$runtimeTrajectorySnapshotDirectory = Join-Path $runDirectory "runtime-trajectory-snapshots"
$behaviorLearningLogPath = Join-Path $runDirectory "behavior-learning-auditor-fixture.log"
$behaviorLearningReportPath = Join-Path $runDirectory "behavior-learning-auditor-fixture.json"
$behaviorPriorLogPath = Join-Path $runDirectory "behavior-prior-fixture.log"
$behaviorPriorArtifactPath = Join-Path $runDirectory "behavior-prior-fixture.json"
$behaviorCandidateAlignmentLogPath = Join-Path $runDirectory "behavior-candidate-alignment-negative-fixture.log"
$behaviorCandidateAlignmentReportPath = Join-Path $runDirectory "behavior-candidate-alignment-negative-fixture.json"
$rustBehaviorPriorLogPath = Join-Path $runDirectory "rust-behavior-prior-loader.log"
$observedPolicyFixtureDirectory = Join-Path $runDirectory "observed-policy-fixture"
$observedPolicyFixtureLogPath = Join-Path $runDirectory "observed-policy-fixture-generation.log"
$observedPolicyPriorLogPath = Join-Path $runDirectory "observed-policy-prior.log"
$observedPolicyPriorArtifactPath = Join-Path $runDirectory "observed-policy-prior.json"
$decisionRankerLogPath = Join-Path $runDirectory "decision-ranker-fixture.log"
$decisionRankerArtifactPath = Join-Path $runDirectory "decision-ranker-fixture.json"
$observedPolicyEvaluationLogPath = Join-Path $runDirectory "observed-policy-evaluation.log"
$observedPolicyEvaluationReportPath = Join-Path $runDirectory "observed-policy-evaluation.json"
$decisionSolverCoverageLogPath = Join-Path $runDirectory "decision-solver-coverage.log"
$decisionSolverCoverageReportPath = Join-Path $runDirectory "decision-solver-coverage.json"
$rustDecisionRankerLogPath = Join-Path $runDirectory "rust-decision-ranker-loader.log"
$advisorModelUpdaterSelfTestLogPath = Join-Path $runDirectory "advisor-model-updater-self-test.log"
$rustCardPoolLogPath = Join-Path $runDirectory "rust-official-card-pool.log"
$rustCardPoolReportPath = Join-Path $runDirectory "rust-official-card-pool.json"
$behaviorPriorDatasetPath = Join-Path $repoRoot "solver\fixtures\behavior-prior-readiness-v1.jsonl"
$behaviorPriorManifestPath = Join-Path $repoRoot "solver\fixtures\behavior-prior-readiness-v1.manifest.json"
$behaviorPriorPolicyPath = Join-Path $repoRoot "solver\fixtures\behavior-prior-readiness-policy-v1.json"
$behaviorCandidateAlignmentPolicyPath = Join-Path $repoRoot "solver\fixtures\behavior-candidate-alignment-policy-v1.json"
$behaviorCandidateAlignmentRulesPath = Join-Path $repoRoot "solver\metacompanion_solver\rules_data\hdt-visible-point-effects-v1.json"
$runtimeBehaviorLearningLogPath = Join-Path $runDirectory "runtime-behavior-learning-readiness.log"
$runtimeBehaviorLearningReportPath = Join-Path $runDirectory "runtime-behavior-learning-readiness.json"
$runtimeBehaviorLearningSnapshotDirectory = Join-Path $runDirectory "runtime-behavior-learning-snapshots"
$rustCombatParityLogPath = Join-Path $runDirectory "rust-parity-combat-v1.log"
$rustCombatParityReportPath = Join-Path $runDirectory "rust-parity-combat-v1.json"
$rustFullParityLogPath = Join-Path $runDirectory "rust-parity-full.log"
$rustFullParityReportPath = Join-Path $runDirectory "rust-parity-full.json"
$visibleResponseLogPath = Join-Path $runDirectory "visible-response.log"
$visibleResponseReportPath = Join-Path $runDirectory "visible-response.json"

$failures = New-Object System.Collections.Generic.List[string]
$report = New-Object System.Collections.Generic.List[string]
$buildDll = Join-Path $repoRoot "MetaCompanion\bin\Release\MetaCompanion.dll"
$resolvedPackagePath = $PackagePath
$csc = $null
$hdtAppPath = $null
$msbuildPath = $null
$testPowerShellPath = $null
$python = $null
$frameworkPath = $null
$rustPromotionRequested = -not [string]::IsNullOrWhiteSpace($RustSolverBinaryPath)
$resolvedRustSolverBinaryPath = ""
$promotedRustSolverBinary = ""
$gitInfo = Get-ReleaseGateGitInfo $repoRoot

try {
	if ($rustPromotionRequested) {
		if ($SkipTests) {
			throw "Rust solver promotion cannot skip tests; both fixed Rust parity profiles are mandatory."
		}
		if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
			throw "Rust solver promotion requires the release gate to create the package so the verified binary is staged atomically."
		}
		if (-not (Test-Path -LiteralPath $RustSolverBinaryPath -PathType Leaf)) {
			throw "Rust solver binary was not found: $RustSolverBinaryPath"
		}
		$resolvedRustSolverBinaryPath = (Resolve-Path -LiteralPath $RustSolverBinaryPath).Path
	}
	if (-not $SkipBuild) {
		$csc = Find-ReleaseGateRoslynCompiler $CscToolPath $repoRoot
		$hdtAppPath = Resolve-ReleaseGateHdtAppPath
		$frameworkPath = Resolve-ReleaseGateFrameworkPath $repoRoot
		$msbuildPath = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
		Invoke-ReleaseGateCommand "Release AnyCPU build" $msbuildPath @(
			(Resolve-Path $SolutionPath).Path,
			"/p:Configuration=Release",
			"/p:Platform=AnyCPU",
			"/p:HdtAppPath=$hdtAppPath",
			"/p:CscToolPath=$(Split-Path -Parent $csc)",
			"/p:CscToolExe=csc.exe",
			"/p:FrameworkPathOverride=$frameworkPath",
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
		$python = Resolve-ReleaseGatePythonPath $PythonPath
		Invoke-ReleaseGateCommand "HDT decision-frame contract unit gate" $testPowerShellPath @(
			"-NoProfile",
			"-ExecutionPolicy",
			"Bypass",
			"-File",
			(Join-Path $repoRoot "tools\Run-HdtDecisionFrameTests.ps1"),
			"-PythonPath",
			$python,
			"-TestPath",
			(Join-Path $repoRoot "solver\tests\test_hdt_replay_behavior.py")
		) $decisionFrameTestLogPath | Out-Null
		Invoke-ReleaseGateCommand "Decision-solver coverage unit gate" $testPowerShellPath @(
			"-NoProfile",
			"-ExecutionPolicy",
			"Bypass",
			"-File",
			(Join-Path $repoRoot "tools\Run-HdtDecisionFrameTests.ps1"),
			"-PythonPath",
			$python,
			"-TestPath",
			(Join-Path $repoRoot "solver\tests\test_decision_solver_evaluation.py")
		) $decisionSolverUnitTestLogPath | Out-Null
		Invoke-ReleaseGateCommand "Counterplay turn-pair oracle gate" $python @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"evaluate-turnpair",
			"--fixtures",
			(Join-Path $repoRoot "solver\fixtures\oracle-turnpair-v1.json"),
			"--output",
			$turnpairEvaluationReportPath
		) $turnpairEvaluationLogPath | Out-Null
		if (-not (Test-Path -LiteralPath $turnpairEvaluationReportPath -PathType Leaf)) {
			throw "Counterplay turn-pair oracle gate did not write its report: $turnpairEvaluationReportPath"
		}
		Invoke-ReleaseGateCommand "HDT visible point-effect oracle gate" $python @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"evaluate-hdt-rules",
			"--fixtures",
			(Join-Path $repoRoot "solver\fixtures\oracle-hdt-cardrules-v1.json"),
			"--output",
			$hdtRuleEvaluationReportPath
		) $hdtRuleEvaluationLogPath | Out-Null
		if (-not (Test-Path -LiteralPath $hdtRuleEvaluationReportPath -PathType Leaf)) {
			throw "HDT visible point-effect oracle gate did not write its report: $hdtRuleEvaluationReportPath"
		}
		Invoke-ReleaseGateCommand "Trajectory auditor synthetic-fixture self-test" $python @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"audit-trajectories",
			"--input",
			(Join-Path $repoRoot "solver\fixtures\trajectory-readiness-v1.jsonl"),
			"--policy",
			(Join-Path $repoRoot "solver\fixtures\trajectory-readiness-policy-v1.json"),
			"--source-kind",
			"synthetic_fixture",
			"--output",
			$trajectoryReadinessReportPath
		) $trajectoryReadinessLogPath | Out-Null
		if (-not (Test-Path -LiteralPath $trajectoryReadinessReportPath -PathType Leaf)) {
			throw "Trajectory auditor fixture self-test did not write its report: $trajectoryReadinessReportPath"
		}
		Invoke-ReleaseGateCommand "Behavior learning auditor synthetic-fixture self-test" $python @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"audit-behavior-learning",
			"--behavior",
			(Join-Path $repoRoot "solver\fixtures\behavior-learning-readiness-v1.jsonl"),
			"--trajectory",
			(Join-Path $repoRoot "solver\fixtures\trajectory-readiness-v1.jsonl"),
			"--policy",
			(Join-Path $repoRoot "solver\fixtures\behavior-learning-readiness-policy-v1.json"),
			"--source-kind",
			"synthetic_fixture",
			"--output",
			$behaviorLearningReportPath
		) $behaviorLearningLogPath | Out-Null
		if (-not (Test-Path -LiteralPath $behaviorLearningReportPath -PathType Leaf)) {
			throw "Behavior learning auditor fixture self-test did not write its report: $behaviorLearningReportPath"
		}
		Invoke-ReleaseGateCommand "Behavior prior synthetic-fixture gate" $python @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"train-behavior-prior",
			"--input", $behaviorPriorDatasetPath,
			"--manifest", $behaviorPriorManifestPath,
			"--policy", $behaviorPriorPolicyPath,
			"--output", $behaviorPriorArtifactPath
		) $behaviorPriorLogPath | Out-Null
		if (-not (Test-Path -LiteralPath $behaviorPriorArtifactPath -PathType Leaf)) {
			throw "Behavior prior fixture gate did not write its artifact: $behaviorPriorArtifactPath"
		}
		Write-Host "== Behavior candidate-completeness negative-fixture gate =="
		$behaviorCandidateAlignmentArguments = @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"audit-behavior-candidates",
			"--input", $behaviorPriorDatasetPath,
			"--manifest", $behaviorPriorManifestPath,
			"--policy", $behaviorCandidateAlignmentPolicyPath,
			"--rules", $behaviorCandidateAlignmentRulesPath,
			"--output", $behaviorCandidateAlignmentReportPath
		)
		$behaviorCandidateAlignmentOutput = & $python @behaviorCandidateAlignmentArguments 2>&1
		$behaviorCandidateAlignmentExitCode = $LASTEXITCODE
		$behaviorCandidateAlignmentOutput |
			Set-Content -LiteralPath $behaviorCandidateAlignmentLogPath -Encoding UTF8
		if ($behaviorCandidateAlignmentExitCode -ne 3) {
			throw "Behavior candidate-completeness negative fixture must be rejected with exit code 3; actual=$behaviorCandidateAlignmentExitCode. See $behaviorCandidateAlignmentLogPath"
		}
		if (-not (Test-Path -LiteralPath $behaviorCandidateAlignmentReportPath -PathType Leaf)) {
			throw "Behavior candidate-completeness gate did not write its report: $behaviorCandidateAlignmentReportPath"
		}
		Invoke-ReleaseGateCommand "Observed-policy synthetic-fixture generation" $python @(
			(Join-Path $repoRoot "solver\tools\observed_policy_fixture.py"),
			"--output-dir", $observedPolicyFixtureDirectory
		) $observedPolicyFixtureLogPath | Out-Null
		Invoke-ReleaseGateCommand "Observed-policy opponent-prior trainer gate" $python @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"train-behavior-prior",
			"--input", (Join-Path $observedPolicyFixtureDirectory "behavior-imitation-v1.jsonl"),
			"--manifest", (Join-Path $observedPolicyFixtureDirectory "behavior-imitation-v1.manifest.json"),
			"--policy", (Join-Path $observedPolicyFixtureDirectory "behavior-prior-policy-v1.json"),
			"--output", $observedPolicyPriorArtifactPath
		) $observedPolicyPriorLogPath | Out-Null
		Invoke-ReleaseGateCommand "Decision-ranker synthetic-fixture trainer gate" $python @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"train-decision-ranker",
			"--decision-frames", (Join-Path $observedPolicyFixtureDirectory "advisor-decision-frame-v1.jsonl"),
			"--behavior", (Join-Path $observedPolicyFixtureDirectory "behavior-v1.jsonl"),
			"--policy", (Join-Path $observedPolicyFixtureDirectory "decision-ranker-policy-v1.json"),
			"--epochs", "1",
			"--output", $decisionRankerArtifactPath
		) $decisionRankerLogPath | Out-Null
		Invoke-ReleaseGateCommand "Observed-policy joint evaluation gate" $python @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"evaluate-observed-policy",
			"--decision-frames", (Join-Path $observedPolicyFixtureDirectory "advisor-decision-frame-v1.jsonl"),
			"--behavior", (Join-Path $observedPolicyFixtureDirectory "behavior-v1.jsonl"),
			"--imitation", (Join-Path $observedPolicyFixtureDirectory "behavior-imitation-v1.jsonl"),
			"--manifest", (Join-Path $observedPolicyFixtureDirectory "behavior-imitation-v1.manifest.json"),
			"--prior", $observedPolicyPriorArtifactPath,
			"--ranker", $decisionRankerArtifactPath,
			"--policy", (Join-Path $observedPolicyFixtureDirectory "observed-policy-evaluation-policy-v1.json"),
			"--output", $observedPolicyEvaluationReportPath
		) $observedPolicyEvaluationLogPath | Out-Null
		foreach ($requiredObservedPolicyArtifact in @(
			$observedPolicyPriorArtifactPath,
			$decisionRankerArtifactPath,
			$observedPolicyEvaluationReportPath
		)) {
			if (-not (Test-Path -LiteralPath $requiredObservedPolicyArtifact -PathType Leaf)) {
				throw "Observed-policy dual-model gate did not write $requiredObservedPolicyArtifact"
			}
		}
		if ($rustPromotionRequested) {
			Invoke-ReleaseGateCommand "Rust behavior-prior loader gate" `
				$resolvedRustSolverBinaryPath `
				@("behavior-prior-check", "--path", $behaviorPriorArtifactPath) `
				$rustBehaviorPriorLogPath | Out-Null
			Invoke-ReleaseGateCommand "Rust decision-ranker loader gate" `
				$resolvedRustSolverBinaryPath `
				@("decision-ranker-check", "--path", $decisionRankerArtifactPath) `
				$rustDecisionRankerLogPath | Out-Null
			Invoke-ReleaseGateCommand "Transactional dual-model updater self-test" $testPowerShellPath @(
				"-NoProfile",
				"-ExecutionPolicy", "Bypass",
				"-File", (Join-Path $repoRoot "tools\Update-AdvisorBehaviorPrior.ps1"),
				"-SelfTest",
				"-SolverDirectory", (Join-Path $repoRoot "solver"),
				"-RustSolverBinaryPath", $resolvedRustSolverBinaryPath,
				"-PythonExecutable", $python
			) $advisorModelUpdaterSelfTestLogPath | Out-Null
			$rustCardPoolGate = Join-Path $repoRoot "solver\tools\rust_card_pool_gate.py"
			if (-not (Test-Path -LiteralPath $rustCardPoolGate -PathType Leaf)) {
				throw "Rust official card-pool gate was not found: $rustCardPoolGate"
			}
			Invoke-ReleaseGateCommand "Rust official card-pool gate" $python @(
				$rustCardPoolGate,
				"--binary", $resolvedRustSolverBinaryPath,
				"--output", $rustCardPoolReportPath
			) $rustCardPoolLogPath | Out-Null
			if (-not (Test-Path -LiteralPath $rustCardPoolReportPath -PathType Leaf)) {
				throw "Rust official card-pool gate did not write its report: $rustCardPoolReportPath"
			}
		}

		$runtimeTrajectoryArguments = @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"audit-runtime-trajectories",
			"--snapshot-dir",
			$runtimeTrajectorySnapshotDirectory,
			"--output",
			$runtimeTrajectoryReportPath
		)
		$runtimeTrajectoryOutput = & $python @runtimeTrajectoryArguments 2>&1
		$runtimeTrajectoryExitCode = $LASTEXITCODE
		$runtimeTrajectoryOutput | Set-Content -LiteralPath $runtimeTrajectoryLogPath -Encoding UTF8
		if ($runtimeTrajectoryExitCode -notin @(0, 3, 4)) {
			throw "Runtime trajectory audit failed with exit code $runtimeTrajectoryExitCode. See $runtimeTrajectoryLogPath"
		}
		if (-not (Test-Path -LiteralPath $runtimeTrajectoryReportPath -PathType Leaf)) {
			throw "Runtime trajectory audit did not write its report: $runtimeTrajectoryReportPath"
		}

		$runtimeBehaviorArguments = @(
			(Join-Path $repoRoot "solver\launch_solver.py"),
			"audit-runtime-behavior-learning",
			"--snapshot-dir",
			$runtimeBehaviorLearningSnapshotDirectory,
			"--output",
			$runtimeBehaviorLearningReportPath
		)
		$runtimeBehaviorOutput = & $python @runtimeBehaviorArguments 2>&1
		$runtimeBehaviorExitCode = $LASTEXITCODE
		$runtimeBehaviorOutput |
			Set-Content -LiteralPath $runtimeBehaviorLearningLogPath -Encoding UTF8
		if ($runtimeBehaviorExitCode -notin @(0, 3, 4)) {
			throw "Runtime behavior learning audit failed with exit code $runtimeBehaviorExitCode. See $runtimeBehaviorLearningLogPath"
		}
		if (-not (Test-Path -LiteralPath $runtimeBehaviorLearningReportPath -PathType Leaf)) {
			throw "Runtime behavior learning audit did not write its report: $runtimeBehaviorLearningReportPath"
		}
		if ($rustPromotionRequested) {
			$rustParityGate = Join-Path $repoRoot "solver\tools\rust_parity_gate.py"
			if (-not (Test-Path -LiteralPath $rustParityGate -PathType Leaf)) {
				throw "Rust parity gate was not found: $rustParityGate"
			}
			Invoke-ReleaseGateCommand "Rust combat parity gate" $python @(
				$rustParityGate,
				"--profile", "combat-v1",
				"--require-binary",
				"--binary", $resolvedRustSolverBinaryPath,
				"--output", $rustCombatParityReportPath
			) $rustCombatParityLogPath | Out-Null
			if (-not (Test-Path -LiteralPath $rustCombatParityReportPath -PathType Leaf)) {
				throw "Rust combat parity gate did not write its report: $rustCombatParityReportPath"
			}
			Invoke-ReleaseGateCommand "Rust full parity gate" $python @(
				$rustParityGate,
				"--profile", "full",
				"--require-binary",
				"--binary", $resolvedRustSolverBinaryPath,
				"--output", $rustFullParityReportPath
			) $rustFullParityLogPath | Out-Null
			if (-not (Test-Path -LiteralPath $rustFullParityReportPath -PathType Leaf)) {
				throw "Rust full parity gate did not write its report: $rustFullParityReportPath"
			}
			Invoke-ReleaseGateCommand "Rust visible-response partial honesty gate" $python @(
				(Join-Path $repoRoot "solver\launch_solver.py"),
				"evaluate-visible-response",
				"--fixtures",
				(Join-Path $repoRoot "solver\fixtures\visible-response-v1.json"),
				"--binary",
				$resolvedRustSolverBinaryPath,
				"--output",
				$visibleResponseReportPath
			) $visibleResponseLogPath | Out-Null
			if (-not (Test-Path -LiteralPath $visibleResponseReportPath -PathType Leaf)) {
				throw "Rust visible-response gate did not write its report: $visibleResponseReportPath"
			}
			Invoke-ReleaseGateCommand "Rust decision-frame solver-coverage honesty gate" $python @(
				(Join-Path $repoRoot "solver\launch_solver.py"),
				"audit-decision-solver-coverage",
				"--decision-frames",
				(Join-Path $observedPolicyFixtureDirectory "advisor-decision-frame-v1.jsonl"),
				"--behavior",
				(Join-Path $observedPolicyFixtureDirectory "behavior-v1.jsonl"),
				"--binary",
				$resolvedRustSolverBinaryPath,
				"--max-frames", "0",
				"--time-budget-ms", "1000",
				"--max-iterations", "100000",
				"--max-depth", "8",
				"--top-k", "10",
				"--output",
				$decisionSolverCoverageReportPath
			) $decisionSolverCoverageLogPath | Out-Null
			if (-not (Test-Path -LiteralPath $decisionSolverCoverageReportPath -PathType Leaf)) {
				throw "Rust decision-solver coverage gate did not write its report: $decisionSolverCoverageReportPath"
			}
		}
	}
} catch {
	$failures.Add($_.Exception.Message)
}

$buildLogIssues = Search-ReleaseGateLogIssues $buildLogPath "Build"
$testLogIssues = Search-ReleaseGateLogIssues $testLogPath "Test"
$testResultSummary = Get-ReleaseGateTestResultSummary $testLogPath $SkipTests.IsPresent
$decisionFrameTestSummary = if ($SkipTests) {
	"Skipped"
} elseif (Test-Path -LiteralPath $decisionFrameTestLogPath -PathType Leaf) {
	"PASS"
} else {
	"Unavailable"
}
if (-not $SkipTests -and $decisionFrameTestSummary -ne "PASS") {
	$failures.Add("HDT decision-frame contract unit gate result: $decisionFrameTestSummary")
}
$decisionSolverUnitTestSummary = if ($SkipTests) {
	"Skipped"
} elseif (Test-Path -LiteralPath $decisionSolverUnitTestLogPath -PathType Leaf) {
	"PASS"
} else {
	"Unavailable"
}
if (-not $SkipTests -and $decisionSolverUnitTestSummary -ne "PASS") {
	$failures.Add("Decision-solver coverage unit gate result: $decisionSolverUnitTestSummary")
}
$turnpairEvaluationSummary = if ($SkipTests) {
	"Skipped"
} elseif (Test-Path -LiteralPath $turnpairEvaluationReportPath -PathType Leaf) {
	try {
		$turnpairReport = Get-Content -LiteralPath $turnpairEvaluationReportPath -Raw | ConvertFrom-Json
		if ($turnpairReport.passed -eq $true) {
			"PASS (Top1=$($turnpairReport.metrics.top1_rate), Top3=$($turnpairReport.metrics.top3_rate), false-safe=$($turnpairReport.metrics.false_safe_rate))"
		} else {
			"FAIL"
		}
	} catch {
		"Invalid report"
	}
} else {
	"Unavailable"
}
if (-not $SkipTests -and $turnpairEvaluationSummary -notlike "PASS*") {
	$failures.Add("Counterplay turn-pair oracle gate result: $turnpairEvaluationSummary")
}
$hdtRuleEvaluationSummary = Get-ReleaseGateHdtRuleEvaluationSummary `
	$hdtRuleEvaluationReportPath `
	$SkipTests.IsPresent
if (-not $SkipTests -and $hdtRuleEvaluationSummary -notlike "PASS*") {
	$failures.Add("HDT visible point-effect oracle gate result: $hdtRuleEvaluationSummary")
}
$trajectoryReadinessSummary = Get-ReleaseGateTrajectoryAuditorFixtureSummary `
	$trajectoryReadinessReportPath `
	$SkipTests.IsPresent
if (-not $SkipTests -and $trajectoryReadinessSummary -notlike "PASS*") {
	$failures.Add("Trajectory auditor fixture self-test result: $trajectoryReadinessSummary")
}
$behaviorLearningSummary = Get-ReleaseGateBehaviorLearningFixtureSummary `
	$behaviorLearningReportPath `
	$SkipTests.IsPresent
if (-not $SkipTests -and $behaviorLearningSummary -notlike "PASS*") {
	$failures.Add("Behavior learning auditor fixture self-test result: $behaviorLearningSummary")
}
$behaviorPriorSummary = Get-ReleaseGateBehaviorPriorFixtureSummary `
	$behaviorPriorArtifactPath `
	$SkipTests.IsPresent `
	$behaviorPriorDatasetPath `
	$behaviorPriorManifestPath `
	$behaviorPriorPolicyPath
if (-not $SkipTests -and $behaviorPriorSummary -notlike "PASS*") {
	$failures.Add("Behavior prior synthetic-fixture gate result: $behaviorPriorSummary")
}
$behaviorCandidateAlignmentSummary = Get-ReleaseGateBehaviorCandidateAlignmentSummary `
	$behaviorCandidateAlignmentReportPath `
	$SkipTests.IsPresent `
	$behaviorPriorDatasetPath `
	$behaviorPriorManifestPath `
	$behaviorCandidateAlignmentPolicyPath `
	$behaviorCandidateAlignmentRulesPath
if (-not $SkipTests -and $behaviorCandidateAlignmentSummary -notlike "PASS*") {
	$failures.Add("Behavior candidate-completeness negative-fixture result: $behaviorCandidateAlignmentSummary")
}
$rustBehaviorPriorSummary = Get-ReleaseGateRustBehaviorPriorSummary `
	$rustBehaviorPriorLogPath `
	(-not $rustPromotionRequested -or $SkipTests.IsPresent)
if ($rustPromotionRequested -and -not $SkipTests -and
	$rustBehaviorPriorSummary -notlike "PASS*") {
	$failures.Add("Rust behavior-prior loader gate result: $rustBehaviorPriorSummary")
}
$observedPolicySummary = Get-ReleaseGateObservedPolicyFixtureSummary `
	$observedPolicyEvaluationReportPath `
	$observedPolicyPriorArtifactPath `
	$decisionRankerArtifactPath `
	$observedPolicyFixtureDirectory `
	$SkipTests.IsPresent
if (-not $SkipTests -and $observedPolicySummary -notlike "PASS*") {
	$failures.Add("Observed-policy dual-model fixture gate result: $observedPolicySummary")
}
$rustDecisionRankerSummary = Get-ReleaseGateRustDecisionRankerSummary `
	$rustDecisionRankerLogPath `
	(-not $rustPromotionRequested -or $SkipTests.IsPresent)
if ($rustPromotionRequested -and -not $SkipTests -and
	$rustDecisionRankerSummary -notlike "PASS*") {
	$failures.Add("Rust decision-ranker loader gate result: $rustDecisionRankerSummary")
}
$advisorModelUpdaterSummary = Get-ReleaseGateAdvisorModelUpdaterSummary `
	$advisorModelUpdaterSelfTestLogPath `
	(-not $rustPromotionRequested -or $SkipTests.IsPresent)
if ($rustPromotionRequested -and -not $SkipTests -and
	$advisorModelUpdaterSummary -notlike "PASS*") {
	$failures.Add("Transactional dual-model updater self-test result: $advisorModelUpdaterSummary")
}
$rustCardPoolStatus = Get-ReleaseGateRustCardPoolStatus `
	$rustCardPoolReportPath `
	$resolvedRustSolverBinaryPath `
	$rustPromotionRequested `
	$SkipTests.IsPresent
if ($rustPromotionRequested -and -not $rustCardPoolStatus.Ready) {
	$failures.Add("Rust official card-pool gate result: $($rustCardPoolStatus.Summary)")
}
$runtimeTrajectorySummary = Get-ReleaseGateRuntimeTrajectorySummary `
	$runtimeTrajectoryReportPath `
	$SkipTests.IsPresent
if (-not $SkipTests -and
	($runtimeTrajectorySummary -like "Invalid report*" -or $runtimeTrajectorySummary -eq "Unavailable")) {
	$failures.Add("Runtime trajectory audit result: $runtimeTrajectorySummary")
}
$runtimeBehaviorLearningSummary = Get-ReleaseGateRuntimeBehaviorLearningSummary `
	$runtimeBehaviorLearningReportPath `
	$SkipTests.IsPresent
if (-not $SkipTests -and
	($runtimeBehaviorLearningSummary -like "Invalid report*" -or
		$runtimeBehaviorLearningSummary -eq "Unavailable")) {
	$failures.Add("Runtime behavior learning audit result: $runtimeBehaviorLearningSummary")
}
$rustCombatParityStatus = Get-ReleaseGateRustParityStatus `
	$rustCombatParityReportPath `
	$rustPromotionRequested `
	"combat-v1" `
	$minimumRustCombatFixtureCount `
	$resolvedRustSolverBinaryPath
$rustFullParityStatus = Get-ReleaseGateRustParityStatus `
	$rustFullParityReportPath `
	$rustPromotionRequested `
	"full" `
	$minimumRustFullFixtureCount `
	$resolvedRustSolverBinaryPath
$visibleResponseStatus = Get-ReleaseGateVisibleResponseStatus `
	$visibleResponseReportPath `
	$rustPromotionRequested `
	$minimumVisibleResponseFixtureCount `
	$resolvedRustSolverBinaryPath
$decisionSolverCoverageStatus = Get-ReleaseGateDecisionSolverCoverageStatus `
	$decisionSolverCoverageReportPath `
	$resolvedRustSolverBinaryPath `
	$observedPolicyFixtureDirectory `
	$rustPromotionRequested `
	$SkipTests.IsPresent
if ($rustPromotionRequested -and -not $rustCombatParityStatus.Ready) {
	$failures.Add("Rust combat parity gate result: $($rustCombatParityStatus.Summary)")
}
if ($rustPromotionRequested -and -not $rustFullParityStatus.Ready) {
	$failures.Add("Rust full parity gate result: $($rustFullParityStatus.Summary)")
}
if ($rustPromotionRequested -and -not $visibleResponseStatus.Ready) {
	$failures.Add("Rust visible-response partial honesty gate result: $($visibleResponseStatus.Summary)")
}
if ($rustPromotionRequested -and -not $decisionSolverCoverageStatus.Ready) {
	$failures.Add("Rust decision-solver coverage honesty gate result: $($decisionSolverCoverageStatus.Summary)")
}
if ($rustCombatParityStatus.Ready -and $rustFullParityStatus.Ready -and
	$visibleResponseStatus.Ready -and $rustCardPoolStatus.Ready -and
	$decisionSolverCoverageStatus.Ready) {
	if ($rustCombatParityStatus.BinarySha256 -ne $rustFullParityStatus.BinarySha256 -or
		$rustCombatParityStatus.BinarySha256 -ne $visibleResponseStatus.BinarySha256 -or
		$rustCombatParityStatus.BinarySha256 -ne $rustCardPoolStatus.BinarySha256 -or
		$rustCombatParityStatus.BinarySha256 -ne $decisionSolverCoverageStatus.BinarySha256) {
		$failures.Add("Rust parity, visible-response, card-pool, and decision-solver coverage gates verified different solver binaries.")
	} else {
		$promotedRustSolverBinary = $resolvedRustSolverBinaryPath
	}
}
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
		$resolvedPackagePath = New-ReleaseGateCommunityPackage `
			$repoRoot `
			$buildDll `
			$runDirectory `
			$promotedRustSolverBinary
	} catch {
		$failures.Add("Community package creation failed: $($_.Exception.Message)")
	}
}

$packageEntries = @()
$blockedPackageEntries = New-Object System.Collections.Generic.List[object]
if (-not [string]::IsNullOrWhiteSpace($resolvedPackagePath) -and (Test-Path -LiteralPath $resolvedPackagePath)) {
	$resolvedPackagePath = Resolve-ReleaseGateExistingPath $resolvedPackagePath
	$packageEntries = @(Get-ReleaseGatePackageEntries $resolvedPackagePath)
	if (-not [string]::IsNullOrWhiteSpace($promotedRustSolverBinary) -and
		$packageEntries -notcontains "solver/metacompanion-solver.exe") {
		$failures.Add("Verified Rust solver was not staged in the package.")
	}
	foreach ($missingEntry in Get-ReleaseGateMissingPackageEntries $packageEntries) {
		$failures.Add("Community package is missing required entry: $missingEntry")
	}
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
$rustLogSkippedReason = if (-not $rustPromotionRequested) {
	"Rust promotion not requested"
} elseif ($SkipTests) {
	"tests skipped"
} else {
	""
}

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
$report.Add("- HDT decision-frame contract unit gate: $decisionFrameTestSummary")
$report.Add("- Decision-solver coverage unit gate: $decisionSolverUnitTestSummary")
$report.Add("- Counterplay turn-pair gate: $turnpairEvaluationSummary")
$report.Add("- HDT visible point-effect gate: $hdtRuleEvaluationSummary")
$report.Add("- Trajectory auditor fixture self-test: $trajectoryReadinessSummary")
$report.Add("- Behavior learning auditor fixture self-test: $behaviorLearningSummary")
$report.Add("- Behavior prior synthetic-fixture gate: $behaviorPriorSummary")
$report.Add("- Behavior candidate-completeness negative-fixture gate: $behaviorCandidateAlignmentSummary")
$report.Add("- Rust behavior-prior loader gate: $rustBehaviorPriorSummary")
$report.Add("- Observed-policy dual-model fixture gate: $observedPolicySummary")
$report.Add("- Rust decision-solver coverage honesty gate: $($decisionSolverCoverageStatus.Summary)")
$report.Add("- Rust decision-ranker loader gate: $rustDecisionRankerSummary")
$report.Add("- Transactional dual-model updater self-test: $advisorModelUpdaterSummary")
$report.Add("- Rust official card-pool gate: $($rustCardPoolStatus.Summary)")
$report.Add("- Runtime training readiness (non-blocking for plugin release): $runtimeTrajectorySummary")
$report.Add("- Runtime behavior learning readiness (non-blocking for plugin release): $runtimeBehaviorLearningSummary")
$trajectoryFixtureScopeNote = '"\u8bad\u7ec3\u6570\u636e\u95e8\u7981\u8bf4\u660e\uff1a\u4e0a\u9762\u7684 trajectory auditor fixture \u4ec5\u9a8c\u8bc1\u5ba1\u8ba1\u5668\u5408\u6210\u5939\u5177\uff0c\u7edd\u4e0d\u4ee3\u8868\u771f\u5b9e\u751f\u4ea7\u8f68\u8ff9\u53ef\u8bad\u7ec3\u3002"' | ConvertFrom-Json
$behaviorFixtureScopeNote = '"\u884c\u4e3a\u8bed\u6599\u95e8\u7981\u8bf4\u660e\uff1a\u4e0a\u9762\u7684 behavior fixture \u4ec5\u9a8c\u8bc1\u53cc\u65b9\u884c\u4e3a\u4e0e\u7ec8\u5c40\u8054\u5ba1\uff1b\u6a21\u4eff\u5b66\u4e60\u5c31\u7eea\u4e0d\u7b49\u4e8e RL \u5c31\u7eea\uff0c\u66f4\u4e0d\u7b49\u4e8e\u6700\u4f18\u52a8\u4f5c\u8bc1\u660e\u3002"' | ConvertFrom-Json
$behaviorPriorScopeNote = '"\u884c\u4e3a\u5148\u9a8c\u95e8\u7981\u8bf4\u660e\uff1a\u53ea\u4f7f\u7528 train \u5206\u5272\u5b66\u4e60\u53cc\u65b9\u89c2\u5bdf\u884c\u4e3a\uff0cvalidation/test \u53ea\u505a\u8bc4\u4f30\uff1b\u4ea7\u7269\u4ec5\u53ef\u7ed9\u5916\u90e8\u5df2\u786e\u8ba4\u5408\u6cd5\u7684\u5019\u9009\u52a8\u4f5c\u6392\u5e8f\uff0c\u4e0d\u751f\u6210\u52a8\u4f5c\u3001\u4e0d\u662f\u7ebf\u4e0a\u7b56\u7565\u3001\u4e0d\u8bc1\u660e\u6700\u4f18\u6027\u3002"' | ConvertFrom-Json
$behaviorCandidateScopeNote = '"\u5019\u9009\u5b8c\u6574\u6027\u95e8\u7981\u8bf4\u660e\uff1a\u53cc\u65b9\u884c\u4e3a\u4ecd\u4f5c\u4e3a\u6709\u4ef7\u503c\u7684\u89c2\u5bdf\u8bed\u6599\uff1b\u53ea\u6709\u672c\u65b9\u52a8\u4f5c\u7cbe\u786e\u547d\u4e2d\u4e14\u5f53\u65f6\u5168\u90e8\u5408\u6cd5\u5019\u9009\u53ef\u8bc1\u660e\u5b8c\u6574\u65f6\uff0c\u624d\u80fd\u8fdb\u5165\u5019\u9009\u6392\u5e8f\u8bad\u7ec3\u3002"' | ConvertFrom-Json
$observedPolicyScopeNote = '"\u53cc\u6a21\u578b\u95e8\u7981\u8bf4\u660e\uff1a\u672c\u65b9\u53ea\u5b66\u4e60 HDT \u5b8c\u6574\u5019\u9009\u4e2d\u7684\u5b9e\u9645\u9009\u62e9\uff0c\u5bf9\u624b\u53ea\u5b66\u4e60\u516c\u5f00\u884c\u4e3a\uff1b\u4e24\u8005\u90fd\u662f\u6a21\u4eff\u8bc1\u636e\uff0c\u4e0d\u662f\u6700\u4f18\u52a8\u4f5c\u6216 RL \u771f\u503c\u3002"' | ConvertFrom-Json
$decisionSolverCoverageScopeNote = '"\u6c42\u89e3\u8986\u76d6\u95e8\u7981\u8bf4\u660e\uff1a\u73a9\u5bb6\u5b9e\u9645\u9009\u62e9\u53ea\u505a\u4e00\u81f4\u7387\u89c2\u6d4b\uff1b\u53ea\u6709 Rust exact\u3001\u6839\u52a8\u4f5c\u5168\u96c6\u5b8c\u6574\u3001\u7ec4\u5408\u6700\u4f18\u6027\u5df2\u8bc1\u660e\u4e14\u4e0e HDT \u5019\u9009\u5168\u96c6\u4e00\u81f4\u7684\u5e27\uff0c\u624d\u8ba1\u5165\u6c42\u89e3\u5668\u8303\u56f4\u5185\u53ef\u590d\u6838\u8bc1\u636e\uff0c\u4ecd\u4e0d\u81ea\u52a8\u664b\u5347 RL \u6216\u5168\u5c40\u6700\u4f18\u6807\u7b7e\u3002"' | ConvertFrom-Json
$runtimeReadinessScopeNote = '"\u751f\u4ea7\u8bad\u7ec3\u5c31\u7eea\u8bf4\u660e\uff1a\u4ee5 Runtime training readiness \u4e3a\u51c6\uff1bNOT_READY/NO_DATA \u4e0d\u963b\u65ad\u63d2\u4ef6\u53d1\u5e03\uff0c\u4f46\u7981\u6b62\u5c06\u6570\u636e\u7528\u4e8e\u8bad\u7ec3\u3002"' | ConvertFrom-Json
$solveStatusScopeNote = '"\u6c42\u89e3\u72b6\u6001\u53e3\u5f84\uff1apartial\u3001cancelled\u3001unsupported\u3001non-ok \u4e92\u65a5\uff1bnon-ok \u4ec5\u542b error/unknown\uff0cunsuccessful \u6c47\u603b\u5168\u90e8\u975e ok\u3002"' | ConvertFrom-Json
$report.Add("- $trajectoryFixtureScopeNote")
$report.Add("- $behaviorFixtureScopeNote")
$report.Add("- $behaviorPriorScopeNote")
$report.Add("- $behaviorCandidateScopeNote")
$report.Add("- $observedPolicyScopeNote")
$report.Add("- $decisionSolverCoverageScopeNote")
$report.Add("- $runtimeReadinessScopeNote")
$report.Add("- $solveStatusScopeNote")
$report.Add("- Rust combat parity gate: $($rustCombatParityStatus.Summary)")
$report.Add("- Rust full parity gate: $($rustFullParityStatus.Summary)")
$report.Add("- Rust visible-response honesty gate: $($visibleResponseStatus.Summary)")
$report.Add("- Rust decision-solver coverage honesty gate: $($decisionSolverCoverageStatus.Summary)")
$report.Add("- Rust solver promoted: $(-not [string]::IsNullOrWhiteSpace($promotedRustSolverBinary))")
$report.Add("- MSBuild: " + (Format-ReleaseGateOptionalValue $msbuildPath (-not $SkipBuild)))
$report.Add("- Test PowerShell: " + (Format-ReleaseGateOptionalValue $testPowerShellPath (-not $SkipTests)))
$report.Add("- Python: " + (Format-ReleaseGateOptionalValue $python (-not $SkipTests)))
$report.Add("- Roslyn: " + (Format-ReleaseGateOptionalValue $csc (-not $SkipBuild)))
$report.Add("- HDT app: " + (Format-ReleaseGateOptionalValue $hdtAppPath (-not $SkipBuild)))
$report.Add("- Framework path override: " + (Format-ReleaseGateOptionalValue $frameworkPath (-not $SkipBuild)))
$report.Add("- Result: " + ($(if ($failures.Count -eq 0) { "PASS" } else { "FAIL" })))
$report.Add("- Failure count: $($failures.Count)")
$report.Add("")
$report.Add("## Inputs")
$report.Add("- Solution: $resolvedSolutionPath")
$report.Add("- Artifacts directory: $resolvedArtifactsDirectory")
$report.Add("- Requested package: $requestedPackagePath")
$report.Add("- Requested Rust solver binary: $requestedRustSolverBinaryPath")
$report.Add("- Skip build: $($SkipBuild.IsPresent)")
$report.Add("- Skip tests: $($SkipTests.IsPresent)")
$report.Add("")
$report.Add("## Logs")
$report.Add("- Build log: " + (Format-ReleaseGateLogValue $buildLogPath ($(if ($SkipBuild) { "build skipped" } else { "" }))))
$report.Add("- Build log issues: $($buildLogIssues.Count)")
$report.Add("- Test log: " + (Format-ReleaseGateLogValue $testLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Test result: $testResultSummary")
$report.Add("- Test log issues: $($testLogIssues.Count)")
$report.Add("- HDT decision-frame unit-test log: " + (Format-ReleaseGateLogValue $decisionFrameTestLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Decision-solver coverage unit-test log: " + (Format-ReleaseGateLogValue $decisionSolverUnitTestLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Turn-pair evaluation log: " + (Format-ReleaseGateLogValue $turnpairEvaluationLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Turn-pair evaluation report: " + (Format-ReleaseGateLogValue $turnpairEvaluationReportPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Turn-pair evaluation result: $turnpairEvaluationSummary")
$report.Add("- HDT rule evaluation log: " + (Format-ReleaseGateLogValue $hdtRuleEvaluationLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- HDT rule evaluation report: " + (Format-ReleaseGateLogValue $hdtRuleEvaluationReportPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- HDT rule evaluation result: $hdtRuleEvaluationSummary")
$report.Add("- Trajectory auditor fixture log: " + (Format-ReleaseGateLogValue $trajectoryReadinessLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Trajectory auditor fixture report: " + (Format-ReleaseGateLogValue $trajectoryReadinessReportPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Trajectory auditor fixture result: $trajectoryReadinessSummary")
$report.Add("- Runtime trajectory audit log: " + (Format-ReleaseGateLogValue $runtimeTrajectoryLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Runtime trajectory audit report: " + (Format-ReleaseGateLogValue $runtimeTrajectoryReportPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Runtime training readiness result: $runtimeTrajectorySummary")
$report.Add("- Behavior learning auditor fixture log: " + (Format-ReleaseGateLogValue $behaviorLearningLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Behavior learning auditor fixture report: " + (Format-ReleaseGateLogValue $behaviorLearningReportPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Behavior learning auditor fixture result: $behaviorLearningSummary")
$report.Add("- Behavior prior fixture log: " + (Format-ReleaseGateLogValue $behaviorPriorLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Behavior prior fixture artifact: " + (Format-ReleaseGateLogValue $behaviorPriorArtifactPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Behavior prior fixture result: $behaviorPriorSummary")
$report.Add("- Behavior candidate-completeness fixture log: " + (Format-ReleaseGateLogValue $behaviorCandidateAlignmentLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Behavior candidate-completeness fixture report: " + (Format-ReleaseGateLogValue $behaviorCandidateAlignmentReportPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Behavior candidate-completeness fixture result: $behaviorCandidateAlignmentSummary")
$report.Add("- Rust behavior-prior loader log: " + (Format-ReleaseGateLogValue $rustBehaviorPriorLogPath $rustLogSkippedReason))
$report.Add("- Rust behavior-prior loader result: $rustBehaviorPriorSummary")
$report.Add("- Observed-policy fixture generation log: " + (Format-ReleaseGateLogValue $observedPolicyFixtureLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Observed-policy opponent-prior log: " + (Format-ReleaseGateLogValue $observedPolicyPriorLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Decision-ranker fixture log: " + (Format-ReleaseGateLogValue $decisionRankerLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Observed-policy joint evaluation log: " + (Format-ReleaseGateLogValue $observedPolicyEvaluationLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Observed-policy joint evaluation report: " + (Format-ReleaseGateLogValue $observedPolicyEvaluationReportPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Observed-policy dual-model result: $observedPolicySummary")
$report.Add("- Decision-solver coverage log: " + (Format-ReleaseGateLogValue $decisionSolverCoverageLogPath $rustLogSkippedReason))
$report.Add("- Decision-solver coverage report: " + (Format-ReleaseGateLogValue $decisionSolverCoverageReportPath $rustLogSkippedReason))
$report.Add("- Decision-solver coverage result: $($decisionSolverCoverageStatus.Summary)")
$report.Add("- Rust decision-ranker loader log: " + (Format-ReleaseGateLogValue $rustDecisionRankerLogPath $rustLogSkippedReason))
$report.Add("- Rust decision-ranker loader result: $rustDecisionRankerSummary")
$report.Add("- Transactional dual-model updater log: " + (Format-ReleaseGateLogValue $advisorModelUpdaterSelfTestLogPath $rustLogSkippedReason))
$report.Add("- Transactional dual-model updater result: $advisorModelUpdaterSummary")
$report.Add("- Rust official card-pool log: " + (Format-ReleaseGateLogValue $rustCardPoolLogPath $rustLogSkippedReason))
$report.Add("- Rust official card-pool report: " + (Format-ReleaseGateLogValue $rustCardPoolReportPath $rustLogSkippedReason))
$report.Add("- Rust official card-pool result: $($rustCardPoolStatus.Summary)")
$report.Add("- Runtime behavior learning audit log: " + (Format-ReleaseGateLogValue $runtimeBehaviorLearningLogPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Runtime behavior learning audit report: " + (Format-ReleaseGateLogValue $runtimeBehaviorLearningReportPath ($(if ($SkipTests) { "tests skipped" } else { "" }))))
$report.Add("- Runtime behavior learning readiness result: $runtimeBehaviorLearningSummary")
$report.Add("- Rust combat parity log: " + (Format-ReleaseGateLogValue $rustCombatParityLogPath $rustLogSkippedReason))
$report.Add("- Rust combat parity report: " + (Format-ReleaseGateLogValue $rustCombatParityReportPath $rustLogSkippedReason))
$report.Add("- Rust combat parity result: $($rustCombatParityStatus.Summary)")
$report.Add("- Rust full parity log: " + (Format-ReleaseGateLogValue $rustFullParityLogPath $rustLogSkippedReason))
$report.Add("- Rust full parity report: " + (Format-ReleaseGateLogValue $rustFullParityReportPath $rustLogSkippedReason))
$report.Add("- Rust full parity result: $($rustFullParityStatus.Summary)")
$report.Add("- Rust visible-response log: " + (Format-ReleaseGateLogValue $visibleResponseLogPath $rustLogSkippedReason))
$report.Add("- Rust visible-response report: " + (Format-ReleaseGateLogValue $visibleResponseReportPath $rustLogSkippedReason))
$report.Add("- Rust visible-response result: $($visibleResponseStatus.Summary)")
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
	$report.Add("- Rust solver included: $($packageEntries -contains 'solver/metacompanion-solver.exe')")
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
