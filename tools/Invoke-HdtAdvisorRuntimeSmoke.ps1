[CmdletBinding()]
param(
	[string]$ExpectedPluginDll = "",
	[string]$ExpectedRustBinary = "",
	[string]$DataRoot = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion",
	[string]$PluginRoot = "$env:APPDATA\HearthstoneDeckTracker\Plugins\MetaCompanion",
	[switch]$ExpectTrainingOnly,
	[switch]$SelfTest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$script:ReportSchema = "metacompanion-hdt-advisor-runtime-smoke-v1"

function New-RuntimeCheck(
	[string]$Id,
	[string]$Status,
	[string]$Summary,
	[object]$Evidence,
	[bool]$Required = $true
) {
	return [pscustomobject][ordered]@{
		id = $Id
		status = $Status
		required = $Required
		summary = $Summary
		evidence = $Evidence
	}
}

function Resolve-RuntimeOverallStatus([object[]]$Checks) {
	$requiredChecks = @($Checks | Where-Object { $_.required })
	if (@($requiredChecks | Where-Object { $_.status -eq "fail" }).Count -gt 0) {
		return "fail"
	}
	if (@($requiredChecks | Where-Object { $_.status -eq "warning" }).Count -gt 0) {
		return "warning"
	}
	if (@($requiredChecks | Where-Object { $_.status -eq "not_exercised" }).Count -gt 0) {
		return "partial"
	}
	return "pass"
}

function Get-RuntimeExitCode([string]$Status) {
	switch ($Status) {
		"pass" { return 0 }
		"fail" { return 1 }
		"warning" { return 2 }
		"partial" { return 2 }
		default { return 1 }
	}
}

function Test-IsUnauthorizedStatusCode([object]$StatusCode) {
	if ($null -eq $StatusCode) {
		return $false
	}
	try {
		return ([int]$StatusCode -eq 401)
	} catch {
		return $false
	}
}

function Get-ForbiddenUiTextCategories([string[]]$Texts) {
	$rules = @(
		@{ Name = "http"; Pattern = '(?i)(?:\bhttps?://|\bHTTP(?:/\d(?:\.\d+)?)?\b|\b(?:GET|POST|PUT|PATCH|DELETE)\s+/v\d)' },
		@{ Name = "exception"; Pattern = '(?i)(?:\b[A-Za-z_][A-Za-z0-9_.]*(?:Exception|Error)\b|\bstack\s*trace\b|\btraceback\b|\bat\s+[A-Za-z_][A-Za-z0-9_.]+\s*\()' },
		@{ Name = "path"; Pattern = '(?i)(?:\b[A-Z]:\\|\\\\[^\\\s]+\\[^\\\s]+|/(?:Users|home|var|tmp|AppData|Program Files|Workspace)(?:/[^/\s]+)+|[A-Za-z0-9_.-]+\.(?:cs|rs|py):\d+)' },
		@{ Name = "request_identity"; Pattern = '(?i)\b(?:request[_ -]?id|state[_ -]?id|api[_ -]?version|trace[_ -]?id)\b' },
		@{ Name = "credential"; Pattern = '(?i)(?:\bAuthorization(?:\s*:)?\s+(?:Basic|Bearer)\b|\b(?:bearer|x-advisor-token|session[_ -]?token|access[_ -]?token|refresh[_ -]?token|auth[_ -]?token|api[_ -]?key|token)\b)' },
		@{ Name = "identity"; Pattern = '(?i)\b(?:user[_ -]?name|email|account[_ -]?name)\b' },
		@{ Name = "runtime_detail"; Pattern = '(?i)(?:\b(?:localhost|socket|connection refused|timed out|json)\b|127\.0\.0\.1|::1|\bSystem\.[A-Za-z])' },
		@{ Name = "technical_english"; Pattern = '(?i)\b(?:worker|backend|solver|payload|schema|endpoint|timeout|failed|failure|invalid|unsupported|unavailable|connection|response|request)\b' },
		@{ Name = "api_route"; Pattern = '(?i)(?:^|\s)/v\d+/[A-Za-z0-9_./-]+' },
		@{ Name = "latin_text"; Pattern = '(?<![A-Za-z])[A-Za-z]{3,}(?![A-Za-z])' }
	)

	$hits = New-Object System.Collections.Generic.List[string]
	foreach ($text in @($Texts)) {
		if ([string]::IsNullOrWhiteSpace($text)) {
			continue
		}
		foreach ($rule in $rules) {
			if ($text -match $rule.Pattern -and -not $hits.Contains($rule.Name)) {
				$hits.Add($rule.Name)
			}
		}
	}
	return @($hits | Sort-Object)
}

function New-TextClassificationEvidence([string[]]$Texts) {
	$items = @($Texts)
	$categories = @(Get-ForbiddenUiTextCategories $items)
	return [pscustomobject][ordered]@{
		text_count = $items.Count
		category_count = $categories.Count
		categories = $categories
	}
}

function Assert-RuntimeSelfTest([bool]$Condition, [string]$Message) {
	if (-not $Condition) {
		throw $Message
	}
}

function Invoke-RuntimeSelfTest {
	$tests = New-Object System.Collections.Generic.List[string]

	$cleanCategories = @(Get-ForbiddenUiTextCategories @("实战策略建议", "计算完成，以下方案仅供参考。"))
	Assert-RuntimeSelfTest ($cleanCategories.Count -eq 0) "Chinese user-facing text must remain clean."
	$tests.Add("clean_chinese_text")

	$technicalCategories = @(Get-ForbiddenUiTextCategories @(
		"HTTP 500 System.InvalidOperationException at C:\Users\example\worker.cs:42",
		"request_id=abc token=secret GET /v1/health on localhost; worker backend failed"
	))
	foreach ($expected in @("http", "exception", "path", "request_identity", "credential", "runtime_detail", "technical_english", "api_route", "latin_text")) {
		Assert-RuntimeSelfTest ($technicalCategories -contains $expected) "Missing UI technical-text category: $expected"
	}
	$tests.Add("technical_text_classification")

	$plainEnglishCategories = @(Get-ForbiddenUiTextCategories @("An error occurred. Something went wrong."))
	Assert-RuntimeSelfTest ($plainEnglishCategories -contains "latin_text") "Plain ASCII English must be classified as latin_text."
	$tests.Add("latin_text_classification")

	$unsafeSource = @(
		"Authorization Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ==",
		"api_key=short-secret",
		"user_name=Alice"
	)
	$allowedEvidence = New-TextClassificationEvidence $unsafeSource
	Assert-RuntimeSelfTest ($allowedEvidence.categories -contains "credential") "Authorization and API keys must be classified as credentials."
	Assert-RuntimeSelfTest ($allowedEvidence.categories -contains "identity") "User names must be classified as identity text."
	$allowedJson = $allowedEvidence | ConvertTo-Json -Depth 4 -Compress
	foreach ($forbiddenLiteral in @("Authorization", "Basic", "QWxhZGRpbjpvcGVuIHNlc2FtZQ==", "api_key", "short-secret", "user_name", "Alice")) {
		Assert-RuntimeSelfTest (-not $allowedJson.Contains($forbiddenLiteral)) "Allow-list evidence projection emitted raw input."
	}
	$tests.Add("allow_list_output_projection")

	Assert-RuntimeSelfTest (Test-IsUnauthorizedStatusCode 401) "HTTP 401 must satisfy the unauthenticated health gate."
	Assert-RuntimeSelfTest (-not (Test-IsUnauthorizedStatusCode 200)) "HTTP 200 must fail the unauthenticated health gate."
	Assert-RuntimeSelfTest (-not (Test-IsUnauthorizedStatusCode $null)) "A missing HTTP status must fail the unauthenticated health gate."
	$tests.Add("unauthenticated_health_401")
	Assert-RuntimeSelfTest (Test-IsExactLoopbackAddress "127.0.0.1") "IPv4 loopback must satisfy the listener gate."
	Assert-RuntimeSelfTest (-not (Test-IsExactLoopbackAddress "::1")) "IPv6 loopback must fail the IPv4-only listener gate."
	Assert-RuntimeSelfTest (-not (Test-IsExactLoopbackAddress "0.0.0.0")) "Wildcard IPv4 must fail the listener gate."
	$tests.Add("strict_ipv4_loopback_listener")

	$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
	$tempRoot = Join-Path $tempBase ("metacompanion-runtime-smoke-selftest-" + [guid]::NewGuid().ToString("N"))
	$artifactRoot = Join-Path $tempRoot "artifacts\release-gate"
	try {
		$encoding = New-Object System.Text.UTF8Encoding($false)
		$runRoots = @{}
		foreach ($run in @("20260730-010101", "20260730-010102", "20260730-010103")) {
			$runDirectory = Join-Path $artifactRoot $run
			$packageRoot = Join-Path $runDirectory "package-root"
			$solverRoot = Join-Path $packageRoot "solver"
			[System.IO.Directory]::CreateDirectory($solverRoot) | Out-Null
			[System.IO.File]::WriteAllBytes((Join-Path $packageRoot "MetaCompanion.dll"), [byte[]]@(1, 2, 3))
			[System.IO.File]::WriteAllBytes((Join-Path $solverRoot "metacompanion-solver.exe"), [byte[]]@(4, 5, 6))
			$result = if ($run -eq "20260730-010103") { "FAIL" } else { "PASS" }
			[System.IO.File]::WriteAllText((Join-Path $runDirectory "release-gate.md"), "- Result: $result`r`n", $encoding)
			$runRoots[$run] = $packageRoot
		}

		$validRoot = $runRoots["20260730-010101"]
		$otherRoot = $runRoots["20260730-010102"]
		$failedRoot = $runRoots["20260730-010103"]
		$valid = Get-ReleaseArtifactReference `
			(Join-Path $validRoot "MetaCompanion.dll") `
			(Join-Path $validRoot "solver\metacompanion-solver.exe") `
			$artifactRoot
		$missing = Get-ReleaseArtifactReference "" "" $artifactRoot
		$mixed = Get-ReleaseArtifactReference `
			(Join-Path $validRoot "MetaCompanion.dll") `
			(Join-Path $otherRoot "solver\metacompanion-solver.exe") `
			$artifactRoot
		$notPass = Get-ReleaseArtifactReference `
			(Join-Path $failedRoot "MetaCompanion.dll") `
			(Join-Path $failedRoot "solver\metacompanion-solver.exe") `
			$artifactRoot

		Assert-RuntimeSelfTest $valid.passed "A same-package PASS release reference must pass."
		Assert-RuntimeSelfTest (-not $missing.passed) "Missing live references must fail."
		Assert-RuntimeSelfTest ($missing.check.evidence.failure_categories -contains "missing_expected_artifacts") "Missing-reference category was not reported."
		Assert-RuntimeSelfTest (-not $mixed.passed) "References from different package roots must fail."
		Assert-RuntimeSelfTest ($mixed.check.evidence.failure_categories -contains "different_package_roots") "Mixed-package category was not reported."
		Assert-RuntimeSelfTest (-not $notPass.passed) "A non-PASS release report must fail."
		Assert-RuntimeSelfTest ($notPass.check.evidence.failure_categories -contains "release_gate_result_not_pass") "Non-PASS report category was not reported."
		$tests.Add("release_artifact_reference_gate")

		$fakeWorkerRoot = Join-Path $tempRoot "AdvisorWorker"
		$fakeCompatibility = [pscustomobject]@{
			python = @([pscustomobject]@{
				ProcessId = 101
				CommandLine = "python -m metacompanion_solver serve"
				ExecutablePath = "python.exe"
			})
			legacy = @([pscustomobject]@{
				ProcessId = 102
				CommandLine = ""
				ExecutablePath = "MetaCompanion.Advisor.Worker.exe"
			})
			advisor_directory = @([pscustomobject]@{
				ProcessId = 103
				CommandLine = ""
				ExecutablePath = Join-Path $fakeWorkerRoot "advisor_worker.exe"
			})
		}
		$compatibilitySnapshot = Get-CompatibilityWorkerSnapshot $fakeCompatibility $fakeWorkerRoot
		Assert-RuntimeSelfTest ($compatibilitySnapshot.process_count -eq 3) "All compatibility worker forms must be detected."
		foreach ($expectedCategory in @("python_worker", "legacy_worker_executable", "advisor_worker_directory_executable")) {
			Assert-RuntimeSelfTest ($compatibilitySnapshot.categories -contains $expectedCategory) "Compatibility worker category was not reported."
		}
		$cleanCompatibility = [pscustomobject]@{
			python = @()
			legacy = @()
			advisor_directory = @([pscustomobject]@{
				ProcessId = 104
				CommandLine = "metacompanion-solver.exe serve"
				ExecutablePath = Join-Path $fakeWorkerRoot "metacompanion-solver.exe"
			})
		}
		$cleanSnapshot = Get-CompatibilityWorkerSnapshot $cleanCompatibility $fakeWorkerRoot
		Assert-RuntimeSelfTest ($cleanSnapshot.process_count -eq 0) "The expected Rust worker must not be classified as compatibility."
		$tests.Add("compatibility_worker_detection")

		$trainingOnlyRoot = Join-Path $tempRoot "training-only"
		$trainingOnlyWorkerRoot = Join-Path $trainingOnlyRoot "AdvisorWorker"
		[System.IO.Directory]::CreateDirectory($trainingOnlyWorkerRoot) | Out-Null
		$configPath = Join-Path $trainingOnlyRoot "config.xml"
		[System.IO.File]::WriteAllText($configPath, @'
<?xml version="1.0" encoding="utf-8"?>
<PluginConfig>
  <EnableLiveAdvisor>false</EnableLiveAdvisor>
  <EnableAdvisorTrainingLog>true</EnableAdvisorTrainingLog>
</PluginConfig>
'@, $encoding)
		$configPass = Get-TrainingOnlyConfigCheck $configPath
		Assert-RuntimeSelfTest ($configPass.status -eq "pass") "The explicit training-only config must pass."
		[System.IO.File]::WriteAllText($configPath, @'
<PluginConfig>
  <EnableLiveAdvisor>true</EnableLiveAdvisor>
  <EnableAdvisorTrainingLog>true</EnableAdvisorTrainingLog>
</PluginConfig>
'@, $encoding)
		$configMismatch = Get-TrainingOnlyConfigCheck $configPath
		Assert-RuntimeSelfTest ($configMismatch.status -eq "fail") "Live advice enabled must fail the training-only config gate."
		Assert-RuntimeSelfTest ($configMismatch.evidence.categories -contains "config_value_mismatch") "The config mismatch category was not reported."
		[System.IO.File]::WriteAllText($configPath, @'
<!DOCTYPE PluginConfig [<!ENTITY blocked "false">]>
<PluginConfig>
  <EnableLiveAdvisor>&blocked;</EnableLiveAdvisor>
  <EnableAdvisorTrainingLog>true</EnableAdvisorTrainingLog>
</PluginConfig>
'@, $encoding)
		$configUnsafe = Get-TrainingOnlyConfigCheck $configPath
		Assert-RuntimeSelfTest ($configUnsafe.status -eq "fail") "DTD-bearing config XML must be rejected."
		Assert-RuntimeSelfTest ($configUnsafe.evidence.categories -contains "config_parse_failed") "Unsafe config XML was not categorized as a parse failure."
		$tests.Add("training_only_config_gate")

		$trainingPath = Join-Path $trainingOnlyWorkerRoot "training-v2.jsonl"
		$missingTraining = Get-TrainingOnlySolveLogCheck $trainingPath
		Assert-RuntimeSelfTest ($missingTraining.status -eq "pass") "A not-yet-created training log must count as zero solve records."
		Assert-RuntimeSelfTest ($missingTraining.evidence.solve_record_count -eq 0) "Missing training log did not report zero solve records."
		[System.IO.File]::WriteAllLines(
			$trainingPath,
			[string[]]@(
				'{"kind":"observe","private_identity":"fixture-private-training"}',
				'{"kind":"transition"}'
			),
			$encoding)
		$zeroSolve = Get-TrainingOnlySolveLogCheck $trainingPath
		Assert-RuntimeSelfTest ($zeroSolve.status -eq "pass") "A training log without solve records must pass."
		$zeroSolveJson = $zeroSolve | ConvertTo-Json -Depth 5 -Compress
		Assert-RuntimeSelfTest (-not $zeroSolveJson.Contains("fixture-private-training")) "Training-only evidence emitted a private training value."
		Assert-RuntimeSelfTest (-not $zeroSolveJson.Contains($trainingPath)) "Training-only evidence emitted the training-log path."
		[System.IO.File]::AppendAllText($trainingPath, '{"kind":"solve"}' + [Environment]::NewLine, $encoding)
		$solvePresent = Get-TrainingOnlySolveLogCheck $trainingPath
		Assert-RuntimeSelfTest ($solvePresent.status -eq "fail") "A solve record must fail the training-only gate."
		Assert-RuntimeSelfTest ($solvePresent.evidence.solve_record_count -eq 1) "The solve-record count is incorrect."
		$tests.Add("training_only_solve_gate")

		$behaviorPath = Join-Path $trainingOnlyWorkerRoot "behavior-v1.jsonl"
		$missingBehavior = Get-TrainingOnlyBehaviorCheck $behaviorPath
		Assert-RuntimeSelfTest ($missingBehavior.status -eq "not_exercised") "A missing behavior log must not be reported as passing coverage."
		[System.IO.File]::WriteAllLines(
			$behaviorPath,
			[string[]]@('{"game_id":"fixture-private-game","actor_side":"local","rl_training_eligible":false}'),
			$encoding)
		$localOnly = Get-TrainingOnlyBehaviorCheck $behaviorPath
		Assert-RuntimeSelfTest ($localOnly.status -eq "not_exercised") "One-sided behavior must remain not_exercised."
		Assert-RuntimeSelfTest ($localOnly.evidence.local_count -eq 1 -and $localOnly.evidence.opponent_count -eq 0) "One-sided behavior counts are incorrect."
		[System.IO.File]::AppendAllText(
			$behaviorPath,
			'{"actor_side":"opponent","rl_training_eligible":false}' + [Environment]::NewLine,
			$encoding)
		$bothSides = Get-TrainingOnlyBehaviorCheck $behaviorPath
		Assert-RuntimeSelfTest ($bothSides.status -eq "pass") "Local and opponent behavior together must pass coverage."
		Assert-RuntimeSelfTest ($bothSides.evidence.local_count -eq 1 -and $bothSides.evidence.opponent_count -eq 1) "Two-sided behavior counts are incorrect."
		$bothSidesJson = $bothSides | ConvertTo-Json -Depth 5 -Compress
		Assert-RuntimeSelfTest (-not $bothSidesJson.Contains("fixture-private-game")) "Behavior evidence emitted a game identity."
		Assert-RuntimeSelfTest (-not $bothSidesJson.Contains($behaviorPath)) "Behavior evidence emitted the behavior-log path."
		[System.IO.File]::AppendAllText(
			$behaviorPath,
			'{"actor_side":"local","rl_training_eligible":true}' + [Environment]::NewLine,
			$encoding)
		$unsafeBehavior = Get-TrainingOnlyBehaviorCheck $behaviorPath
		Assert-RuntimeSelfTest ($unsafeBehavior.status -eq "fail") "RL-eligible behavior must fail the training-only behavior gate."
		Assert-RuntimeSelfTest ($unsafeBehavior.evidence.rl_training_eligible_violation_count -eq 1) "The RL-eligibility violation count is incorrect."
		$tests.Add("training_only_behavior_gate")

		$panelAbsent = Get-TrainingOnlyAdvisorPanelCheck 1 0
		$panelVisible = Get-TrainingOnlyAdvisorPanelCheck 1 1
		Assert-RuntimeSelfTest ($panelAbsent.status -eq "pass") "An absent advisor panel must pass in training-only mode."
		Assert-RuntimeSelfTest ($panelVisible.status -eq "fail") "A visible advisor panel must fail in training-only mode."
		$tests.Add("training_only_panel_absence_gate")
	} finally {
		$resolvedTemp = [System.IO.Path]::GetFullPath($tempRoot)
		$tempPrefix = $tempBase.TrimEnd([char[]]@([char]92, [char]47)) + [System.IO.Path]::DirectorySeparatorChar
		if ($resolvedTemp.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
			[System.IO.Path]::GetFileName($resolvedTemp).StartsWith("metacompanion-runtime-smoke-selftest-", [System.StringComparison]::Ordinal)) {
			if ([System.IO.Directory]::Exists($resolvedTemp)) {
				[System.IO.Directory]::Delete($resolvedTemp, $true)
			}
		}
	}

	$passChecks = @(
		(New-RuntimeCheck "required" "pass" "" ([ordered]@{}) $true),
		(New-RuntimeCheck "optional" "not_requested" "" ([ordered]@{}) $false)
	)
	$partialChecks = @(
		(New-RuntimeCheck "required" "pass" "" ([ordered]@{}) $true),
		(New-RuntimeCheck "ui" "not_exercised" "" ([ordered]@{}) $true)
	)
	$warningChecks = @(
		(New-RuntimeCheck "required" "warning" "" ([ordered]@{}) $true),
		(New-RuntimeCheck "ui" "not_exercised" "" ([ordered]@{}) $true)
	)
	$failChecks = @(
		(New-RuntimeCheck "required" "fail" "" ([ordered]@{}) $true),
		(New-RuntimeCheck "other" "warning" "" ([ordered]@{}) $true)
	)
	Assert-RuntimeSelfTest ((Resolve-RuntimeOverallStatus $passChecks) -eq "pass") "All required pass checks must resolve to pass."
	Assert-RuntimeSelfTest ((Resolve-RuntimeOverallStatus $partialChecks) -eq "partial") "A required not_exercised check must resolve to partial."
	Assert-RuntimeSelfTest ((Resolve-RuntimeOverallStatus $warningChecks) -eq "warning") "Warning must take precedence over partial."
	Assert-RuntimeSelfTest ((Resolve-RuntimeOverallStatus $failChecks) -eq "fail") "Failure must take precedence over warning."
	Assert-RuntimeSelfTest ((Get-RuntimeExitCode "pass") -eq 0) "Pass must use exit code 0."
	Assert-RuntimeSelfTest ((Get-RuntimeExitCode "fail") -eq 1) "Fail must use exit code 1."
	Assert-RuntimeSelfTest ((Get-RuntimeExitCode "partial") -eq 2) "Partial must use exit code 2."
	Assert-RuntimeSelfTest ((Get-RuntimeExitCode "warning") -eq 2) "Warning must use exit code 2."
	$tests.Add("report_status_and_exit_codes")

	return [pscustomobject][ordered]@{
		schema = $script:ReportSchema
		mode = "self_test"
		status = "pass"
		exit_code = 0
		test_count = $tests.Count
		tests = @($tests)
	}
}

function Test-PathWithinRoot([string]$Path, [string]$Root) {
	if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) {
		return $false
	}
	try {
		$fullPath = [System.IO.Path]::GetFullPath($Path)
		$fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]@([char]92, [char]47))
		$prefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
		return $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
	} catch {
		return $false
	}
}

function Convert-CimCreationDateToUtc([object]$Value) {
	if ($null -eq $Value) {
		return $null
	}
	try {
		if ($Value -is [datetime]) {
			return ([datetime]$Value).ToUniversalTime()
		}
		$date = [System.Management.ManagementDateTimeConverter]::ToDateTime([string]$Value)
		return $date.ToUniversalTime()
	} catch {
		return $null
	}
}

function Get-ProcessInventory {
	try {
		$hdt = @(Get-CimInstance Win32_Process -Filter "Name = 'HearthstoneDeckTracker.exe'" -ErrorAction Stop)
		$rust = @(Get-CimInstance Win32_Process -Filter "Name = 'metacompanion-solver.exe'" -ErrorAction Stop)
		$python = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'python%.exe' OR Name = 'py.exe'" -ErrorAction Stop)
		$legacy = @(Get-CimInstance Win32_Process -Filter "Name = 'MetaCompanion.Advisor.Worker.exe' OR Name = 'advisor_worker.exe'" -ErrorAction Stop)
		$advisorDirectory = @(Get-CimInstance Win32_Process -Filter "ExecutablePath LIKE '%AdvisorWorker%'" -ErrorAction Stop)
		return [pscustomobject]@{
			succeeded = $true
			hdt = $hdt
			rust = $rust
			python = $python
			legacy = $legacy
			advisor_directory = $advisorDirectory
		}
	} catch {
		return [pscustomobject]@{
			succeeded = $false
			hdt = @()
			rust = @()
			python = @()
			legacy = @()
			advisor_directory = @()
		}
	}
}

function Test-IsMetaCompanionPythonWorker([object]$Process) {
	if ($null -eq $Process -or [string]::IsNullOrWhiteSpace([string]$Process.CommandLine)) {
		return $false
	}
	$commandLine = [string]$Process.CommandLine
	return $commandLine -match '(?i)(?:-m\s+metacompanion_solver\b|launch_solver\.py\b|advisor_worker\.py\b|metacompanion_solver[\\/]__main__\.py\b|MetaCompanion[\\/]AdvisorWorker\b)'
}

function Get-CompatibilityWorkerSnapshot([object]$Inventory, [string]$WorkerRoot) {
	$pythonWorkers = @($Inventory.python | Where-Object { Test-IsMetaCompanionPythonWorker $_ })
	$legacyWorkers = @($Inventory.legacy)
	$directoryWorkers = @($Inventory.advisor_directory | Where-Object {
		-not [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) -and
		(Test-PathWithinRoot ([string]$_.ExecutablePath) $WorkerRoot) -and
		[System.IO.Path]::GetFileName([string]$_.ExecutablePath) -ine "metacompanion-solver.exe"
	})

	$categories = New-Object System.Collections.Generic.List[string]
	if ($pythonWorkers.Count -gt 0) { $categories.Add("python_worker") }
	if ($legacyWorkers.Count -gt 0) { $categories.Add("legacy_worker_executable") }
	if ($directoryWorkers.Count -gt 0) { $categories.Add("advisor_worker_directory_executable") }

	$seen = New-Object 'System.Collections.Generic.HashSet[int]'
	$allWorkers = New-Object System.Collections.Generic.List[object]
	foreach ($process in @($pythonWorkers) + @($legacyWorkers) + @($directoryWorkers)) {
		if ($seen.Add([int]$process.ProcessId)) {
			$allWorkers.Add($process)
		}
	}
	return [pscustomobject]@{
		process_count = $allWorkers.Count
		python_count = $pythonWorkers.Count
		legacy_executable_count = $legacyWorkers.Count
		advisor_directory_non_rust_count = $directoryWorkers.Count
		category_count = $categories.Count
		categories = @($categories | Sort-Object)
	}
}

function Get-WorkerListeners([int]$ProcessId) {
	$connections = @(Get-NetTCPConnection -State Listen -OwningProcess $ProcessId -ErrorAction Stop)
	$seen = @{}
	$rows = New-Object System.Collections.Generic.List[object]
	foreach ($connection in @($connections | Sort-Object LocalAddress, LocalPort)) {
		$key = ([string]$connection.LocalAddress) + ":" + ([int]$connection.LocalPort)
		if ($seen.ContainsKey($key)) {
			continue
		}
		$seen[$key] = $true
		$rows.Add([pscustomobject][ordered]@{
			address = [string]$connection.LocalAddress
			port = [int]$connection.LocalPort
		})
	}
	return $rows.ToArray()
}

function Test-IsExactLoopbackAddress([string]$Address) {
	return $Address -eq "127.0.0.1"
}

function Invoke-UnauthenticatedHealthProbe([string]$Address, [int]$Port) {
	if (-not (Test-IsExactLoopbackAddress $Address)) {
		return [pscustomobject][ordered]@{
			status_code = $null
			outcome = "rejected_non_ipv4_loopback"
		}
	}
	$uri = "http://127.0.0.1`:$Port/v1/health"
	$request = [System.Net.HttpWebRequest][System.Net.WebRequest]::Create($uri)
	$request.Method = "GET"
	$request.AllowAutoRedirect = $false
	$request.PreAuthenticate = $false
	$request.UseDefaultCredentials = $false
	$request.Credentials = $null
	$request.Proxy = $null
	$request.Timeout = 3000
	$request.ReadWriteTimeout = 3000
	$request.UserAgent = "MetaCompanion-RuntimeSmoke/1.0"

	$response = $null
	$statusCode = $null
	$transport = "response"
	try {
		$response = [System.Net.HttpWebResponse]$request.GetResponse()
		$statusCode = [int]$response.StatusCode
	} catch [System.Net.WebException] {
		if ($null -ne $_.Exception.Response) {
			$response = [System.Net.HttpWebResponse]$_.Exception.Response
			$statusCode = [int]$response.StatusCode
		} else {
			$transport = "unreachable"
		}
	} catch {
		$transport = "probe_error"
	} finally {
		if ($null -ne $response) {
			try { $response.Close() } catch { }
		}
	}

	return [pscustomobject][ordered]@{
		status_code = $statusCode
		outcome = if (Test-IsUnauthorizedStatusCode $statusCode) { "unauthorized" } else { $transport }
	}
}

function Get-FileSha256([string]$Path) {
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToUpperInvariant()
}

function Get-ReleaseArtifactReference(
	[string]$PluginPath,
	[string]$RustPath,
	[string]$ArtifactsRoot
) {
	$failures = New-Object System.Collections.Generic.List[string]
	$providedCount = 0
	if (-not [string]::IsNullOrWhiteSpace($PluginPath)) { $providedCount++ }
	if (-not [string]::IsNullOrWhiteSpace($RustPath)) { $providedCount++ }
	if ($providedCount -ne 2) {
		$failures.Add("missing_expected_artifacts")
	}

	$pluginFull = ""
	$rustFull = ""
	$artifactsRootFull = ""
	try {
		if ($providedCount -eq 2) {
			$pluginFull = [System.IO.Path]::GetFullPath($PluginPath)
			$rustFull = [System.IO.Path]::GetFullPath($RustPath)
		}
		$artifactsRootFull = [System.IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd([char[]]@([char]92, [char]47))
	} catch {
		if (-not $failures.Contains("artifact_path_invalid")) {
			$failures.Add("artifact_path_invalid")
		}
	}

	$referenceFileCount = 0
	if (-not [string]::IsNullOrWhiteSpace($pluginFull) -and (Test-Path -LiteralPath $pluginFull -PathType Leaf)) {
		$referenceFileCount++
	}
	if (-not [string]::IsNullOrWhiteSpace($rustFull) -and (Test-Path -LiteralPath $rustFull -PathType Leaf)) {
		$referenceFileCount++
	}
	if ($providedCount -eq 2 -and $referenceFileCount -ne 2) {
		$failures.Add("reference_file_missing")
	}

	$pluginPackageRoot = ""
	$rustPackageRoot = ""
	$layoutValid = $false
	$samePackageRoot = $false
	if (-not [string]::IsNullOrWhiteSpace($pluginFull) -and -not [string]::IsNullOrWhiteSpace($rustFull)) {
		try {
			$pluginPackageRoot = [System.IO.Path]::GetDirectoryName($pluginFull)
			$rustSolverRoot = [System.IO.Path]::GetDirectoryName($rustFull)
			$rustPackageRoot = [System.IO.Path]::GetDirectoryName($rustSolverRoot)
			$layoutValid =
				[System.IO.Path]::GetFileName($pluginFull) -ieq "MetaCompanion.dll" -and
				[System.IO.Path]::GetFileName($pluginPackageRoot) -ieq "package-root" -and
				[System.IO.Path]::GetFileName($rustFull) -ieq "metacompanion-solver.exe" -and
				[System.IO.Path]::GetFileName($rustSolverRoot) -ieq "solver" -and
				[System.IO.Path]::GetFileName($rustPackageRoot) -ieq "package-root"
			$samePackageRoot = $pluginPackageRoot.Equals($rustPackageRoot, [System.StringComparison]::OrdinalIgnoreCase)
		} catch {
			$layoutValid = $false
			$samePackageRoot = $false
		}
	}
	if ($providedCount -eq 2 -and -not $layoutValid) {
		$failures.Add("artifact_layout_invalid")
	}
	if ($providedCount -eq 2 -and $layoutValid -and -not $samePackageRoot) {
		$failures.Add("different_package_roots")
	}

	$releaseRootValid = $false
	$runNameValid = $false
	$reportPresent = $false
	$reportResultPass = $false
	$packageRoot = if ($samePackageRoot) { $pluginPackageRoot } else { "" }
	if (-not [string]::IsNullOrWhiteSpace($packageRoot) -and -not [string]::IsNullOrWhiteSpace($artifactsRootFull)) {
		try {
			$runDirectory = [System.IO.Path]::GetDirectoryName($packageRoot)
			$runParent = [System.IO.Path]::GetDirectoryName($runDirectory)
			$releaseRootValid = $runParent.Equals($artifactsRootFull, [System.StringComparison]::OrdinalIgnoreCase)
			$runNameValid = [System.IO.Path]::GetFileName($runDirectory) -match '^\d{8}-\d{6}$'
			if (-not $releaseRootValid) {
				$failures.Add("outside_release_gate_root")
			}
			if (-not $runNameValid) {
				$failures.Add("invalid_release_gate_run_name")
			}
			if ($releaseRootValid -and $runNameValid) {
				$reportPath = Join-Path $runDirectory "release-gate.md"
				$reportPresent = Test-Path -LiteralPath $reportPath -PathType Leaf
				if ($reportPresent) {
					try {
						$reportLines = @(Read-SharedUtf8Lines $reportPath)
						$resultLines = @($reportLines | Where-Object { $_ -match '^\s*(?:-\s*)?Result:\s*(?:PASS|FAIL)\s*$' })
						$reportResultPass = $resultLines.Count -eq 1 -and $resultLines[0] -match '^\s*(?:-\s*)?Result:\s*PASS\s*$'
					} catch {
						$failures.Add("release_gate_report_unreadable")
					}
				} else {
					$failures.Add("release_gate_report_missing")
				}
			}
		} catch {
			$failures.Add("artifact_path_invalid")
		}
	}
	if ($reportPresent -and -not $reportResultPass -and -not $failures.Contains("release_gate_report_unreadable")) {
		$failures.Add("release_gate_result_not_pass")
	}

	$failureCategories = @($failures | Sort-Object -Unique)
	$passed = $failureCategories.Count -eq 0 -and $providedCount -eq 2 -and
		$referenceFileCount -eq 2 -and $layoutValid -and $samePackageRoot -and
		$releaseRootValid -and $runNameValid -and $reportPresent -and $reportResultPass
	$check = New-RuntimeCheck "release_artifact_reference" $(if ($passed) { "pass" } else { "fail" }) $(if ($passed) {
		"预期 DLL 与 Rust 二进制来自同一个已通过门禁的发布包。"
	} else {
		"预期 DLL 与 Rust 二进制未通过同包发布门禁校验。"
	}) ([ordered]@{
		provided_count = $providedCount
		reference_file_count = $referenceFileCount
		layout_valid = [bool]$layoutValid
		same_package_root = [bool]$samePackageRoot
		release_root_valid = [bool]$releaseRootValid
		run_name_valid = [bool]$runNameValid
		release_gate_report_present = [bool]$reportPresent
		release_gate_result_pass = [bool]$reportResultPass
		failure_category_count = $failureCategories.Count
		failure_categories = $failureCategories
	}) $true

	return [pscustomobject]@{
		passed = [bool]$passed
		plugin_path = $pluginFull
		rust_path = $rustFull
		package_root = $packageRoot
		check = $check
	}
}

function Get-PluginHashCheck(
	[object]$ArtifactReference,
	[string]$InstalledPluginRoot,
	[object]$HdtProcess
) {
	if ($null -eq $ArtifactReference -or -not $ArtifactReference.passed) {
		return New-RuntimeCheck "plugin_dll_sha256" "fail" "发布包引用未通过，拒绝执行插件哈希验收。" ([ordered]@{
			artifact_reference_passed = $false
		}) $true
	}

	try {
		$expectedHash = Get-FileSha256 ([string]$ArtifactReference.plugin_path)
	} catch {
		return New-RuntimeCheck "plugin_dll_sha256" "fail" "无法读取发布包插件 DLL 哈希。" ([ordered]@{
			artifact_reference_passed = $true
			reference_hash_available = $false
		}) $true
	}

	$candidates = New-Object System.Collections.Generic.List[string]
	$candidates.Add((Join-Path $InstalledPluginRoot "MetaCompanion.dll"))
	if ($null -ne $HdtProcess -and -not [string]::IsNullOrWhiteSpace([string]$HdtProcess.ExecutablePath)) {
		try {
			$hdtDirectory = Split-Path -Parent ([string]$HdtProcess.ExecutablePath)
			$candidates.Add((Join-Path $hdtDirectory "Plugins\MetaCompanion\MetaCompanion.dll"))
		} catch { }
	}

	$unique = New-Object System.Collections.Generic.List[string]
	$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
	foreach ($candidate in $candidates) {
		if ($seen.Add([System.IO.Path]::GetFullPath($candidate))) {
			$unique.Add([System.IO.Path]::GetFullPath($candidate))
		}
	}

	$existingCount = 0
	$matchingCount = 0
	$installedHashes = New-Object System.Collections.Generic.List[string]
	foreach ($candidate in $unique) {
		if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
			continue
		}
		$existingCount++
		try {
			$hash = Get-FileSha256 $candidate
			if (-not $installedHashes.Contains($hash)) {
				$installedHashes.Add($hash)
			}
			if ($hash -eq $expectedHash) {
				$matchingCount++
			}
		} catch { }
	}

	$passed = $unique.Count -gt 0 -and $existingCount -eq $unique.Count -and $matchingCount -eq $unique.Count
	return New-RuntimeCheck "plugin_dll_sha256" $(if ($passed) { "pass" } else { "fail" }) $(if ($passed) {
		"当前安装目标均与预期插件 DLL 一致。"
	} else {
		"至少一个当前安装目标缺失或与预期插件 DLL 不一致。"
	}) ([ordered]@{
		artifact_reference_passed = $true
		expected_sha256 = $expectedHash
		target_count = $unique.Count
		existing_count = $existingCount
		matching_count = $matchingCount
		installed_sha256 = @($installedHashes | Sort-Object)
	}) $true
}

function Get-RustHashCheck([object]$ArtifactReference, [object]$WorkerProcess, [bool]$WorkerPathTrusted) {
	if ($null -eq $ArtifactReference -or -not $ArtifactReference.passed) {
		return New-RuntimeCheck "rust_binary_sha256" "fail" "发布包引用未通过，拒绝执行 Rust 哈希验收。" ([ordered]@{
			artifact_reference_passed = $false
		}) $true
	}
	if ($null -eq $WorkerProcess -or -not $WorkerPathTrusted -or
		[string]::IsNullOrWhiteSpace([string]$WorkerProcess.ExecutablePath) -or
		-not (Test-Path -LiteralPath ([string]$WorkerProcess.ExecutablePath) -PathType Leaf)) {
		return New-RuntimeCheck "rust_binary_sha256" "fail" "没有可信的当前 Rust worker 安装文件可供哈希比对。" ([ordered]@{
			artifact_reference_passed = $true
			runtime_target_available = $false
		}) $true
	}

	try {
		$expectedHash = Get-FileSha256 ([string]$ArtifactReference.rust_path)
		$installedHash = Get-FileSha256 ([string]$WorkerProcess.ExecutablePath)
		$passed = $expectedHash -eq $installedHash
		return New-RuntimeCheck "rust_binary_sha256" $(if ($passed) { "pass" } else { "fail" }) $(if ($passed) {
			"当前 Rust worker 与预期二进制一致。"
		} else {
			"当前 Rust worker 与预期二进制不一致。"
		}) ([ordered]@{
			artifact_reference_passed = $true
			expected_sha256 = $expectedHash
			installed_sha256 = $installedHash
		}) $true
	} catch {
		return New-RuntimeCheck "rust_binary_sha256" "fail" "无法完成 Rust worker 哈希比对。" ([ordered]@{
			artifact_reference_passed = $true
		}) $true
	}
}

function Read-SharedUtf8Lines([string]$Path) {
	$stream = [System.IO.File]::Open(
		$Path,
		[System.IO.FileMode]::Open,
		[System.IO.FileAccess]::Read,
		[System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
	$reader = $null
	try {
		$reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
		$text = $reader.ReadToEnd()
	} finally {
		if ($null -ne $reader) {
			$reader.Dispose()
		} else {
			$stream.Dispose()
		}
	}
	if ([string]::IsNullOrEmpty($text)) {
		return @()
	}
	return @($text -split "\r?\n")
}

function Add-RuntimeCategory(
	[System.Collections.Generic.List[string]]$Categories,
	[string]$Category
) {
	if (-not $Categories.Contains($Category)) {
		$Categories.Add($Category)
	}
}

function Get-TrainingOnlyConfigCheck([string]$ConfigPath) {
	$categories = New-Object System.Collections.Generic.List[string]
	$expectedSettingCount = 2
	$foundSettingCount = 0
	$parsedSettingCount = 0
	$mismatchCount = 0
	$document = $null

	if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
		Add-RuntimeCategory $categories "config_not_found"
		$mismatchCount = $expectedSettingCount
	} else {
		$stream = $null
		$reader = $null
		try {
			$item = Get-Item -LiteralPath $ConfigPath -ErrorAction Stop
			if ($item.Length -gt 1MB) {
				Add-RuntimeCategory $categories "config_size_limit_exceeded"
				$mismatchCount = $expectedSettingCount
			} else {
				$settings = New-Object System.Xml.XmlReaderSettings
				$settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
				$settings.XmlResolver = $null
				$settings.MaxCharactersInDocument = 1MB
				$settings.IgnoreComments = $true
				$settings.IgnoreProcessingInstructions = $true
				$settings.CloseInput = $true
				$stream = [System.IO.File]::Open(
					$ConfigPath,
					[System.IO.FileMode]::Open,
					[System.IO.FileAccess]::Read,
					[System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
				$reader = [System.Xml.XmlReader]::Create($stream, $settings)
				$document = New-Object System.Xml.XmlDocument
				$document.XmlResolver = $null
				$document.PreserveWhitespace = $false
				$document.Load($reader)
			}
		} catch {
			Add-RuntimeCategory $categories "config_parse_failed"
			$mismatchCount = $expectedSettingCount
			$document = $null
		} finally {
			if ($null -ne $reader) {
				$reader.Dispose()
			} elseif ($null -ne $stream) {
				$stream.Dispose()
			}
		}
	}

	if ($null -ne $document) {
		if ($null -eq $document.DocumentElement -or
			-not [string]::Equals(
				$document.DocumentElement.LocalName,
				"PluginConfig",
				[System.StringComparison]::Ordinal)) {
			Add-RuntimeCategory $categories "config_root_invalid"
			$mismatchCount = $expectedSettingCount
		} else {
			$expectations = [ordered]@{
				EnableLiveAdvisor = $false
				EnableAdvisorTrainingLog = $true
			}
			foreach ($expectation in $expectations.GetEnumerator()) {
				$nodes = @($document.DocumentElement.ChildNodes | Where-Object {
					$_.NodeType -eq [System.Xml.XmlNodeType]::Element -and
					[string]::Equals(
						$_.LocalName,
						[string]$expectation.Key,
						[System.StringComparison]::Ordinal)
				})
				$foundSettingCount += $nodes.Count
				if ($nodes.Count -eq 0) {
					Add-RuntimeCategory $categories "config_setting_missing"
					$mismatchCount++
					continue
				}
				if ($nodes.Count -ne 1) {
					Add-RuntimeCategory $categories "config_setting_duplicate"
					$mismatchCount++
					continue
				}

				$parsedValue = $false
				if (-not [bool]::TryParse(
					([string]$nodes[0].InnerText).Trim(),
					[ref]$parsedValue)) {
					Add-RuntimeCategory $categories "config_setting_invalid"
					$mismatchCount++
					continue
				}
				$parsedSettingCount++
				if ($parsedValue -ne [bool]$expectation.Value) {
					Add-RuntimeCategory $categories "config_value_mismatch"
					$mismatchCount++
				}
			}
		}
	}

	$passed = $categories.Count -eq 0 -and $mismatchCount -eq 0
	return New-RuntimeCheck "training_only_config" $(if ($passed) { "pass" } else { "fail" }) $(if ($passed) {
		"插件配置已明确关闭实时建议，并开启训练记录。"
	} else {
		"插件配置不符合仅训练模式要求，或无法安全读取。"
	}) ([ordered]@{
		expected_setting_count = $expectedSettingCount
		found_setting_count = $foundSettingCount
		parsed_setting_count = $parsedSettingCount
		mismatch_count = $mismatchCount
		category_count = $categories.Count
		categories = @($categories | Sort-Object)
	}) $true
}

function Get-TrainingOnlySolveLogCheck([string]$TrainingLogPath) {
	$categories = New-Object System.Collections.Generic.List[string]
	$recordCount = 0
	$solveRecordCount = 0
	$invalidRecordCount = 0
	$blankLineCount = 0
	$readFailed = $false

	if (-not (Test-Path -LiteralPath $TrainingLogPath -PathType Leaf)) {
		Add-RuntimeCategory $categories "training_log_not_created"
		return New-RuntimeCheck "training_only_solve_records" "pass" "当前严格轨迹文件尚未产生，solve 记录数为零。" ([ordered]@{
			record_count = 0
			solve_record_count = 0
			invalid_record_count = 0
			blank_line_count = 0
			category_count = $categories.Count
			categories = @($categories)
		}) $true
	}

	$stream = $null
	$reader = $null
	try {
		$stream = [System.IO.File]::Open(
			$TrainingLogPath,
			[System.IO.FileMode]::Open,
			[System.IO.FileAccess]::Read,
			[System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
		$reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
		while (-not $reader.EndOfStream) {
			$line = $reader.ReadLine()
			if ([string]::IsNullOrWhiteSpace($line)) {
				$blankLineCount++
				continue
			}

			try {
				$record = $line | ConvertFrom-Json -ErrorAction Stop
				$kindProperty = $record.PSObject.Properties["kind"]
				if ($null -eq $kindProperty -or -not ($kindProperty.Value -is [string]) -or
					[string]::IsNullOrWhiteSpace([string]$kindProperty.Value)) {
					$invalidRecordCount++
					continue
				}
				$recordCount++
				if ([string]::Equals(
					[string]$kindProperty.Value,
					"solve",
					[System.StringComparison]::Ordinal)) {
					$solveRecordCount++
				}
			} catch {
				$invalidRecordCount++
			}
		}
	} catch {
		$readFailed = $true
	} finally {
		if ($null -ne $reader) {
			$reader.Dispose()
		} elseif ($null -ne $stream) {
			$stream.Dispose()
		}
	}

	if ($readFailed) { Add-RuntimeCategory $categories "training_log_read_failed" }
	if ($invalidRecordCount -gt 0) { Add-RuntimeCategory $categories "training_log_invalid_record" }
	if ($solveRecordCount -gt 0) { Add-RuntimeCategory $categories "solve_record_present" }
	$passed = -not $readFailed -and $invalidRecordCount -eq 0 -and $solveRecordCount -eq 0
	return New-RuntimeCheck "training_only_solve_records" $(if ($passed) { "pass" } else { "fail" }) $(if ($passed) {
		"当前严格轨迹中的 solve 记录数为零。"
	} else {
		"当前严格轨迹包含 solve 记录，或无法可靠确认其数量。"
	}) ([ordered]@{
		record_count = $recordCount
		solve_record_count = $solveRecordCount
		invalid_record_count = $invalidRecordCount
		blank_line_count = $blankLineCount
		category_count = $categories.Count
		categories = @($categories | Sort-Object)
	}) $true
}

function Get-TrainingOnlyBehaviorCheck([string]$BehaviorLogPath) {
	$categories = New-Object System.Collections.Generic.List[string]
	$recordCount = 0
	$localCount = 0
	$opponentCount = 0
	$unknownCount = 0
	$invalidJsonCount = 0
	$schemaViolationCount = 0
	$rlEligibilityViolationCount = 0
	$blankLineCount = 0
	$readFailed = $false

	if (-not (Test-Path -LiteralPath $BehaviorLogPath -PathType Leaf)) {
		Add-RuntimeCategory $categories "behavior_log_not_created"
		Add-RuntimeCategory $categories "local_side_not_observed"
		Add-RuntimeCategory $categories "opponent_side_not_observed"
		return New-RuntimeCheck "training_only_behavior" "not_exercised" "双方行为文件尚未产生，本项未实际覆盖。" ([ordered]@{
			record_count = 0
			local_count = 0
			opponent_count = 0
			unknown_count = 0
			invalid_json_count = 0
			schema_violation_count = 0
			rl_training_eligible_violation_count = 0
			blank_line_count = 0
			category_count = $categories.Count
			categories = @($categories | Sort-Object)
		}) $true
	}

	$stream = $null
	$reader = $null
	try {
		$stream = [System.IO.File]::Open(
			$BehaviorLogPath,
			[System.IO.FileMode]::Open,
			[System.IO.FileAccess]::Read,
			[System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
		$reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
		while (-not $reader.EndOfStream) {
			$line = $reader.ReadLine()
			if ([string]::IsNullOrWhiteSpace($line)) {
				$blankLineCount++
				continue
			}

			try {
				$record = $line | ConvertFrom-Json -ErrorAction Stop
				$recordCount++
				$recordHasViolation = $false
				$sideProperty = $record.PSObject.Properties["actor_side"]
				if ($null -eq $sideProperty -or -not ($sideProperty.Value -is [string])) {
					$recordHasViolation = $true
				} else {
					switch -CaseSensitive ([string]$sideProperty.Value) {
						"local" { $localCount++ }
						"opponent" { $opponentCount++ }
						"unknown" { $unknownCount++ }
						default { $recordHasViolation = $true }
					}
				}

				$eligibilityProperty = $record.PSObject.Properties["rl_training_eligible"]
				if ($null -eq $eligibilityProperty -or
					-not ($eligibilityProperty.Value -is [bool]) -or
					[bool]$eligibilityProperty.Value) {
					$rlEligibilityViolationCount++
					$recordHasViolation = $true
				}
				if ($recordHasViolation) {
					$schemaViolationCount++
				}
			} catch {
				$invalidJsonCount++
			}
		}
	} catch {
		$readFailed = $true
	} finally {
		if ($null -ne $reader) {
			$reader.Dispose()
		} elseif ($null -ne $stream) {
			$stream.Dispose()
		}
	}

	if ($readFailed) { Add-RuntimeCategory $categories "behavior_log_read_failed" }
	if ($invalidJsonCount -gt 0) { Add-RuntimeCategory $categories "behavior_log_invalid_json" }
	if ($schemaViolationCount -gt 0) { Add-RuntimeCategory $categories "behavior_schema_violation" }
	if ($rlEligibilityViolationCount -gt 0) { Add-RuntimeCategory $categories "rl_training_eligible_not_false" }
	if ($recordCount -eq 0) { Add-RuntimeCategory $categories "no_behavior_records" }
	if ($localCount -eq 0) { Add-RuntimeCategory $categories "local_side_not_observed" }
	if ($opponentCount -eq 0) { Add-RuntimeCategory $categories "opponent_side_not_observed" }
	if ($unknownCount -gt 0) { Add-RuntimeCategory $categories "unknown_side_observed" }

	$failed = $readFailed -or $invalidJsonCount -gt 0 -or
		$schemaViolationCount -gt 0 -or $rlEligibilityViolationCount -gt 0
	$bothSidesObserved = $localCount -gt 0 -and $opponentCount -gt 0
	$status = if ($failed) { "fail" } elseif ($bothSidesObserved) { "pass" } else { "not_exercised" }
	$summary = if ($failed) {
		"双方行为文件无法可靠验证，或发现了训练资格违规记录。"
	} elseif ($bothSidesObserved) {
		"本方与对手行为均已实际记录，且没有记录可直接用于强化学习训练。"
	} else {
		"尚未同时观察到本方与对手行为，本项未实际覆盖。"
	}
	return New-RuntimeCheck "training_only_behavior" $status $summary ([ordered]@{
		record_count = $recordCount
		local_count = $localCount
		opponent_count = $opponentCount
		unknown_count = $unknownCount
		invalid_json_count = $invalidJsonCount
		schema_violation_count = $schemaViolationCount
		rl_training_eligible_violation_count = $rlEligibilityViolationCount
		blank_line_count = $blankLineCount
		category_count = $categories.Count
		categories = @($categories | Sort-Object)
	}) $true
}

function Get-SessionLogCheck([string]$LogPath, [object]$HdtStartUtc) {
	if ($null -eq $HdtStartUtc) {
		return New-RuntimeCheck "metacompanion_session_log" "not_exercised" "无法确定当前 HDT 启动时间，未统计本次会话日志。" ([ordered]@{
			scope = "current_hdt_start"
			warning_count = $null
			error_count = $null
		}) $true
	}
	if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
		return New-RuntimeCheck "metacompanion_session_log" "not_exercised" "当前 MetaCompanion 日志不存在，未统计本次会话日志。" ([ordered]@{
			scope = "current_hdt_start"
			warning_count = $null
			error_count = $null
		}) $true
	}

	try {
		$item = Get-Item -LiteralPath $LogPath -ErrorAction Stop
		if ($item.LastWriteTimeUtc -lt ([datetime]$HdtStartUtc).AddSeconds(-5)) {
			return New-RuntimeCheck "metacompanion_session_log" "not_exercised" "日志早于当前 HDT 会话，未将旧日志冒充为本次统计。" ([ordered]@{
				scope = "current_hdt_start"
				warning_count = $null
				error_count = $null
			}) $true
		}
		$lines = @(Read-SharedUtf8Lines $LogPath)
	} catch {
		return New-RuntimeCheck "metacompanion_session_log" "not_exercised" "无法只读打开当前 MetaCompanion 日志。" ([ordered]@{
			scope = "current_hdt_start"
			warning_count = $null
			error_count = $null
		}) $true
	}

	$startupIndex = -1
	for ($index = 0; $index -lt $lines.Count; $index++) {
		if ($lines[$index] -match '\|Info\|MetaCompanionPlugin\.OnLoad\s+>>\s+(?:插件已启动|Starting Meta Companion)\b') {
			$startupIndex = $index
			break
		}
	}
	if ($startupIndex -lt 0) {
		return New-RuntimeCheck "metacompanion_session_log" "not_exercised" "未找到当前插件启动标记，未对日志范围作推测。" ([ordered]@{
			scope = "current_hdt_start"
			warning_count = $null
			error_count = $null
		}) $true
	}

	$sessionLines = @($lines[$startupIndex..($lines.Count - 1)])
	$warnings = @($sessionLines | Where-Object { $_ -match '\|(?:Warning|Warn)\|' })
	$errors = @($sessionLines | Where-Object { $_ -match '\|Error\|' })
	$status = if ($errors.Count -gt 0) { "fail" } elseif ($warnings.Count -gt 0) { "warning" } else { "pass" }
	$summary = if ($errors.Count -gt 0) {
		"本次 HDT 启动后，MetaCompanion 日志出现错误。"
	} elseif ($warnings.Count -gt 0) {
		"本次 HDT 启动后，MetaCompanion 日志出现警告。"
	} else {
		"本次 HDT 启动后的 MetaCompanion 日志没有警告或错误。"
	}
	return New-RuntimeCheck "metacompanion_session_log" $status $summary ([ordered]@{
		scope = "from_current_plugin_startup_marker"
		scanned_line_count = $sessionLines.Count
		warning_count = $warnings.Count
		error_count = $errors.Count
	}) $true
}

function Get-VisibleAutomationNames([System.Windows.Automation.AutomationElement]$Element) {
	$names = New-Object System.Collections.Generic.List[string]
	$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
	$elements = $Element.FindAll(
		[System.Windows.Automation.TreeScope]::Subtree,
		[System.Windows.Automation.Condition]::TrueCondition)
	foreach ($candidate in $elements) {
		try {
			if ($candidate.Current.IsOffscreen) {
				continue
			}
			$name = ([string]$candidate.Current.Name).Trim()
			if (-not [string]::IsNullOrWhiteSpace($name) -and $seen.Add($name)) {
				$names.Add($name)
			}
		} catch { }
	}
	return $names.ToArray()
}

function Find-AdvisorPanelScope([System.Windows.Automation.AutomationElement]$TitleElement) {
	$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
	$current = $TitleElement
	for ($depth = 0; $depth -lt 12 -and $null -ne $current; $depth++) {
		try {
			$typeName = [string]$current.Current.ControlType.ProgrammaticName
			if ($typeName -eq "ControlType.Window") {
				return $null
			}
			$names = @(Get-VisibleAutomationNames $current)
			$chineseNames = @($names | Where-Object { $_ -match '[\u3400-\u9FFF]' })
			if ($names -contains "实战策略建议" -and $chineseNames.Count -ge 2 -and $names.Count -le 80) {
				return [pscustomobject]@{
				element = $current
				names = $names
			}
			}
			$current = $walker.GetParent($current)
		} catch {
			return $null
		}
	}
	return $null
}

function Get-TrainingOnlyAdvisorPanelCheck([int]$WindowCount, [int]$TitleMatchCount) {
	$visible = $TitleMatchCount -gt 0
	[string[]]$categories = @(if ($visible) { "advisor_panel_visible" } else { "advisor_panel_absent" })
	return New-RuntimeCheck "advisor_panel_ui" $(if ($visible) { "fail" } else { "pass" }) $(if ($visible) {
		"仅训练模式下仍发现可见建议面板。"
	} else {
		"仅训练模式下没有可见建议面板。"
	}) ([ordered]@{
		window_count = $WindowCount
		title_match_count = $TitleMatchCount
		category_count = $categories.Count
		categories = $categories
	}) $true
}

function Get-AdvisorPanelUiCheck([object]$HdtProcess, [bool]$ExpectAbsent = $false) {
	if ($null -eq $HdtProcess) {
		if ($ExpectAbsent) {
			return New-RuntimeCheck "advisor_panel_ui" "not_exercised" "HDT 未唯一运行，无法确认建议面板不存在。" ([ordered]@{
				window_count = 0
				title_match_count = 0
				category_count = 1
				categories = @("hdt_not_unique")
			}) $true
		}
		return New-RuntimeCheck "advisor_panel_ui" "not_exercised" "HDT 未唯一运行，无法枚举建议面板。" ([ordered]@{
			panel_visible = $false
			reason = "hdt_not_unique"
			chinese_text_count = 0
		}) $true
	}

	try {
		Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
		Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop
		$condition = New-Object System.Windows.Automation.PropertyCondition(
			[System.Windows.Automation.AutomationElement]::ProcessIdProperty,
			[int]$HdtProcess.ProcessId)
		$windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
			[System.Windows.Automation.TreeScope]::Children,
			$condition)
		$titleElements = New-Object System.Collections.Generic.List[System.Windows.Automation.AutomationElement]
		foreach ($window in $windows) {
			$descendants = $window.FindAll(
				[System.Windows.Automation.TreeScope]::Descendants,
				[System.Windows.Automation.Condition]::TrueCondition)
			foreach ($element in $descendants) {
				try {
					if (-not $element.Current.IsOffscreen -and ([string]$element.Current.Name).Trim() -eq "实战策略建议") {
						$titleElements.Add($element)
					}
				} catch { }
			}
		}
	} catch {
		if ($ExpectAbsent) {
			return New-RuntimeCheck "advisor_panel_ui" "fail" "无法只读确认建议面板是否存在。" ([ordered]@{
				window_count = 0
				title_match_count = 0
				category_count = 1
				categories = @("uia_enumeration_failed")
			}) $true
		}
		return New-RuntimeCheck "advisor_panel_ui" "fail" "UI Automation 无法只读枚举 HDT 控件。" ([ordered]@{
			panel_visible = $null
			reason = "uia_enumeration_failed"
			chinese_text_count = 0
		}) $true
	}

	if ($ExpectAbsent) {
		return Get-TrainingOnlyAdvisorPanelCheck $windows.Count $titleElements.Count
	}

	if ($titleElements.Count -eq 0) {
		return New-RuntimeCheck "advisor_panel_ui" "not_exercised" "当前没有可见的《实战策略建议》面板，本项未验收。" ([ordered]@{
			panel_visible = $false
			reason = "panel_not_visible"
			window_count = $windows.Count
			title_match_count = 0
			chinese_text_count = 0
		}) $true
	}

	$allPanelNames = New-Object System.Collections.Generic.List[string]
	$seenNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
	foreach ($titleElement in $titleElements) {
		$scope = Find-AdvisorPanelScope $titleElement
		if ($null -eq $scope) {
			return New-RuntimeCheck "advisor_panel_ui" "fail" "已发现建议面板，但无法将只读检查范围可靠限定在面板内。" ([ordered]@{
				panel_visible = $true
				reason = "panel_scope_unresolved"
				title_match_count = $titleElements.Count
				chinese_text_count = 1
			}) $true
		}
		foreach ($name in @($scope.names)) {
			if ($seenNames.Add([string]$name)) {
				$allPanelNames.Add([string]$name)
			}
		}
	}

	$categories = @(Get-ForbiddenUiTextCategories @($allPanelNames))
	$chineseTextCount = @($allPanelNames | Where-Object { $_ -match '[\u3400-\u9FFF]' }).Count
	$passed = $categories.Count -eq 0
	return New-RuntimeCheck "advisor_panel_ui" $(if ($passed) { "pass" } else { "fail" }) $(if ($passed) {
		"建议面板可见，且未出现 ASCII 英文或技术文本。"
	} else {
		"建议面板可见，但出现了不应展示给用户的技术文本。"
	}) ([ordered]@{
		panel_visible = $true
		title_match_count = $titleElements.Count
		visible_text_count = $allPanelNames.Count
		chinese_text_count = $chineseTextCount
		forbidden_category_count = $categories.Count
		forbidden_categories = $categories
	}) $true
}

function Invoke-HdtAdvisorRuntimeSmoke {
	$checks = New-Object System.Collections.Generic.List[object]
	$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
	$artifactsRoot = Join-Path $repositoryRoot "artifacts\release-gate"
	$artifactReference = Get-ReleaseArtifactReference $ExpectedPluginDll $ExpectedRustBinary $artifactsRoot
	$checks.Add($artifactReference.check)
	$inventory = Get-ProcessInventory

	$hdtProcesses = @($inventory.hdt)
	$hdtProcess = if ($hdtProcesses.Count -eq 1) { $hdtProcesses[0] } else { $null }
	$hdtStartUtc = if ($null -ne $hdtProcess) { Convert-CimCreationDateToUtc $hdtProcess.CreationDate } else { $null }
	$hdtPassed = $inventory.succeeded -and $hdtProcesses.Count -eq 1
	$checks.Add((New-RuntimeCheck "hdt_process" $(if ($hdtPassed) { "pass" } else { "fail" }) $(if ($hdtPassed) {
		"HDT 正好运行一个进程。"
	} elseif (-not $inventory.succeeded) {
		"无法读取本机进程清单。"
	} else {
		"HDT 进程数量不是一个。"
	}) ([ordered]@{
		inventory_available = [bool]$inventory.succeeded
		process_count = $hdtProcesses.Count
		start_time_available = $null -ne $hdtStartUtc
	}) $true))

	$workerProcesses = @($inventory.rust)
	$workerProcess = if ($workerProcesses.Count -eq 1) { $workerProcesses[0] } else { $null }
	$workerRoot = Join-Path $DataRoot "AdvisorWorker"
	$workerPathTrusted = $false
	$workerParentIsHdt = $false
	$workerServeMode = $false
	if ($null -ne $workerProcess) {
		$workerPathTrusted =
			-not [string]::IsNullOrWhiteSpace([string]$workerProcess.ExecutablePath) -and
			([System.IO.Path]::GetFileName([string]$workerProcess.ExecutablePath) -ieq "metacompanion-solver.exe") -and
			(Test-PathWithinRoot ([string]$workerProcess.ExecutablePath) $workerRoot)
		$workerParentIsHdt = $null -ne $hdtProcess -and [int]$workerProcess.ParentProcessId -eq [int]$hdtProcess.ProcessId
		$workerServeMode = -not [string]::IsNullOrWhiteSpace([string]$workerProcess.CommandLine) -and
			([string]$workerProcess.CommandLine -match '(?i)(?:^|\s)serve(?:\s|$)')
	}
	$workerPassed = $inventory.succeeded -and $workerProcesses.Count -eq 1 -and
		$workerPathTrusted -and $workerParentIsHdt -and $workerServeMode
	$checks.Add((New-RuntimeCheck "rust_worker" $(if ($workerPassed) { "pass" } else { "fail" }) $(if ($workerPassed) {
		"MetaCompanion Rust worker 正好一个，且由当前 HDT 从 AdvisorWorker 目录启动。"
	} else {
		"Rust worker 的数量、父进程、启动模式或安装目录不符合约束。"
	}) ([ordered]@{
		inventory_available = [bool]$inventory.succeeded
		process_count = $workerProcesses.Count
		within_advisor_worker = if ($null -ne $workerProcess) { [bool]$workerPathTrusted } else { $null }
		parent_is_current_hdt = if ($null -ne $workerProcess) { [bool]$workerParentIsHdt } else { $null }
		serve_mode = if ($null -ne $workerProcess) { [bool]$workerServeMode } else { $null }
	}) $true))

	$compatibility = Get-CompatibilityWorkerSnapshot $inventory $workerRoot
	$compatibilityPassed = $inventory.succeeded -and $compatibility.process_count -eq 0
	$checks.Add((New-RuntimeCheck "python_worker_absent" $(if ($compatibilityPassed) { "pass" } else { "fail" }) $(if ($compatibilityPassed) {
		"没有 Python 或其他兼容 worker 与 Rust worker 并存。"
	} else {
		"发现 Python 或其他兼容 worker，或无法读取进程清单。"
	}) ([ordered]@{
		inventory_available = [bool]$inventory.succeeded
		process_count = $compatibility.process_count
		python_count = $compatibility.python_count
		legacy_executable_count = $compatibility.legacy_executable_count
		advisor_directory_non_rust_count = $compatibility.advisor_directory_non_rust_count
		category_count = $compatibility.category_count
		categories = $compatibility.categories
	}) $true))

	$listeners = @()
	$listenerQuerySucceeded = $false
	if ($null -ne $workerProcess) {
		try {
			$listeners = @(Get-WorkerListeners ([int]$workerProcess.ProcessId))
			$listenerQuerySucceeded = $true
		} catch {
			$listenerQuerySucceeded = $false
		}
	}
	$nonLoopback = @($listeners | Where-Object { -not (Test-IsExactLoopbackAddress $_.address) })
	$ipv4Listeners = @($listeners | Where-Object { Test-IsExactLoopbackAddress $_.address })
	$uniquePorts = @($listeners.port | Sort-Object -Unique)
	$listenerPassed = $listenerQuerySucceeded -and $listeners.Count -gt 0 -and $nonLoopback.Count -eq 0
	$listenerStatus = if ($null -eq $workerProcess) { "not_exercised" } elseif ($listenerPassed) { "pass" } else { "fail" }
	$checks.Add((New-RuntimeCheck "rust_worker_listener" $listenerStatus $(if ($null -eq $workerProcess) {
		"没有唯一 Rust worker，监听范围未验收。"
	} elseif ($listenerPassed) {
		"Rust worker 的 TCP 监听仅位于 127.0.0.1。"
	} else {
		"Rust worker 没有可确认的监听，或监听不只位于 127.0.0.1。"
	}) ([ordered]@{
		query_succeeded = $listenerQuerySucceeded
		listener_count = $listeners.Count
		ipv4_loopback_count = $ipv4Listeners.Count
		non_ipv4_count = $nonLoopback.Count
		unique_port_count = $uniquePorts.Count
	}) $true))

	$probes = New-Object System.Collections.Generic.List[object]
	if ($listenerPassed) {
		foreach ($port in $uniquePorts) {
			$probes.Add((Invoke-UnauthenticatedHealthProbe "127.0.0.1" ([int]$port)))
		}
	}
	$healthPassed = $probes.Count -gt 0 -and @($probes | Where-Object { -not (Test-IsUnauthorizedStatusCode $_.status_code) }).Count -eq 0
	$healthStatus = if (-not $listenerPassed) { "not_exercised" } elseif ($healthPassed) { "pass" } else { "fail" }
	$checks.Add((New-RuntimeCheck "unauthenticated_health" $healthStatus $(if (-not $listenerPassed) {
		"没有可信的仅回环监听，无令牌健康检查未执行。"
	} elseif ($healthPassed) {
		"无令牌 GET /v1/health 均返回 401。"
	} else {
		"至少一个无令牌 GET /v1/health 没有返回 401。"
	}) ([ordered]@{
		probe_count = $probes.Count
		http_status_codes = @($probes | ForEach-Object { $_.status_code })
		outcome_categories = @($probes | ForEach-Object { $_.outcome } | Sort-Object -Unique)
		all_status_401 = if ($probes.Count -gt 0) { [bool]$healthPassed } else { $null }
	}) $true))

	$checks.Add((Get-PluginHashCheck $artifactReference $PluginRoot $hdtProcess))
	$checks.Add((Get-RustHashCheck $artifactReference $workerProcess $workerPathTrusted))
	if ($ExpectTrainingOnly) {
		$checks.Add((Get-TrainingOnlyConfigCheck (Join-Path $DataRoot "config.xml")))
		$checks.Add((Get-TrainingOnlySolveLogCheck (Join-Path $workerRoot "training-v2.jsonl")))
		$checks.Add((Get-TrainingOnlyBehaviorCheck (Join-Path $workerRoot "behavior-v1.jsonl")))
	}

	$logPath = Join-Path $DataRoot "Logs\log.txt"
	$checks.Add((Get-SessionLogCheck $logPath $hdtStartUtc))
	$checks.Add((Get-AdvisorPanelUiCheck $hdtProcess ([bool]$ExpectTrainingOnly)))

	$status = Resolve-RuntimeOverallStatus $checks.ToArray()
	$exitCode = Get-RuntimeExitCode $status
	return [pscustomobject][ordered]@{
		schema = $script:ReportSchema
		status = $status
		exit_code = $exitCode
		exit_code_contract = [ordered]@{
			pass = 0
			fail = 1
			partial_or_warning = 2
		}
		checks = $checks.ToArray()
	}
}

if ($SelfTest) {
	try {
		$selfTestReport = Invoke-RuntimeSelfTest
		$selfTestReport | ConvertTo-Json -Depth 8
		exit 0
	} catch {
		$selfTestFailure = [pscustomobject][ordered]@{
			schema = $script:ReportSchema
			mode = "self_test"
			status = "fail"
			exit_code = 1
			failure_category = "self_test_assertion_failed"
		}
		$selfTestFailure | ConvertTo-Json -Depth 8
		exit 1
	}
}

try {
	$report = Invoke-HdtAdvisorRuntimeSmoke
	$report | ConvertTo-Json -Depth 10
	exit ([int]$report.exit_code)
} catch {
	$fatalReport = [pscustomobject][ordered]@{
		schema = $script:ReportSchema
		status = "fail"
		exit_code = 1
		exit_code_contract = [ordered]@{
			pass = 0
			fail = 1
			partial_or_warning = 2
		}
		checks = @(
			(New-RuntimeCheck "runtime_internal" "fail" "运行时验收脚本发生内部错误。" ([ordered]@{
				failure_category = "runtime_internal_error"
			}) $true)
		)
	}
	$fatalReport | ConvertTo-Json -Depth 10
	exit 1
}
