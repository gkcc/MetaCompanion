[CmdletBinding()]
param(
	[string]$DataDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\AdvisorWorker",
	[string]$HistoricalSourceDirectory = "",
	[string]$DecisionFramePath = "",
	[string]$SolverDirectory = "",
	[string]$RustSolverBinaryPath = "",
	[string]$PythonExecutable = "python",
	[string]$BehaviorPolicyPath = "",
	[string]$PriorPolicyPath = "",
	[string]$DecisionRankerPolicyPath = "",
	[string]$ObservedPolicyEvaluationPolicyPath = "",
	[string]$CandidateAlignmentPolicyPath = "",
	[string]$CardRulesPath = "",
	[ValidateRange(1, 100)]
	[int]$RankerEpochs = 20,
	[switch]$SelfTest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$script:BehaviorPriorModelName = "behavior-prior-v1.json"
$script:DecisionRankerModelName = "decision-ranker-v1.json"
$script:InstallManifestName = "advisor-ordering-models-v1.install.json"
$script:UpdateSchema = "metacompanion-advisor-ordering-model-update-v1"

function Test-PathWithinRoot([string]$Path, [string]$Root) {
	if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) {
		return $false
	}
	$resolvedPath = [System.IO.Path]::GetFullPath($Path)
	$resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
		[char[]]@([char]92, [char]47))
	if ($resolvedPath.Equals($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
		return $true
	}
	return $resolvedPath.StartsWith(
		$resolvedRoot + [System.IO.Path]::DirectorySeparatorChar,
		[System.StringComparison]::OrdinalIgnoreCase)
}

function Get-FileSha256([string]$Path) {
	$algorithm = [System.Security.Cryptography.SHA256]::Create()
	$stream = [System.IO.File]::Open(
		$Path,
		[System.IO.FileMode]::Open,
		[System.IO.FileAccess]::Read,
		[System.IO.FileShare]::ReadWrite)
	try {
		return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
	} finally {
		$stream.Dispose()
		$algorithm.Dispose()
	}
}

function Read-Utf8Json([string]$Path, [string]$Label) {
	if (-not [System.IO.File]::Exists($Path)) {
		throw "$Label 没有生成结果；现有双模型保持不变。"
	}
	try {
		return [System.IO.File]::ReadAllText(
			$Path,
			[System.Text.Encoding]::UTF8) | ConvertFrom-Json
	} catch {
		throw "$Label 不是有效的 JSON；现有双模型保持不变。"
	}
}

function Write-Utf8JsonFile([string]$Path, [object]$Value, [string]$Root) {
	$target = [System.IO.Path]::GetFullPath($Path)
	if (-not (Test-PathWithinRoot $target $Root)) {
		throw "JSON 写入目标超出了受控目录。"
	}
	$directory = [System.IO.Path]::GetDirectoryName($target)
	[System.IO.Directory]::CreateDirectory($directory) | Out-Null
	$encoding = New-Object System.Text.UTF8Encoding($false)
	$payload = $encoding.GetBytes(($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine)
	$stream = New-Object System.IO.FileStream(
		$target,
		[System.IO.FileMode]::Create,
		[System.IO.FileAccess]::Write,
		[System.IO.FileShare]::None)
	try {
		$stream.Write($payload, 0, $payload.Length)
		$stream.Flush($true)
	} finally {
		$stream.Dispose()
	}
}

function Copy-FileDurably(
	[string]$Source,
	[string]$Destination,
	[string]$DestinationRoot,
	[bool]$FailIfExists
) {
	$resolvedSource = [System.IO.Path]::GetFullPath($Source)
	$resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
	if (-not [System.IO.File]::Exists($resolvedSource)) {
		throw "需要复制的只读输入不存在。"
	}
	if (-not (Test-PathWithinRoot $resolvedDestination $DestinationRoot)) {
		throw "复制目标超出了受控目录。"
	}
	if ($FailIfExists -and [System.IO.File]::Exists($resolvedDestination)) {
		throw "受控复制目标已存在。"
	}
	$parent = [System.IO.Path]::GetDirectoryName($resolvedDestination)
	[System.IO.Directory]::CreateDirectory($parent) | Out-Null
	$mode = if ($FailIfExists) {
		[System.IO.FileMode]::CreateNew
	} else {
		[System.IO.FileMode]::Create
	}
	$share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
	$input = [System.IO.File]::Open(
		$resolvedSource,
		[System.IO.FileMode]::Open,
		[System.IO.FileAccess]::Read,
		$share)
	$output = New-Object System.IO.FileStream(
		$resolvedDestination,
		$mode,
		[System.IO.FileAccess]::Write,
		[System.IO.FileShare]::None)
	try {
		$input.CopyTo($output)
		$output.Flush($true)
	} finally {
		$output.Dispose()
		$input.Dispose()
	}
	return Get-FileSha256 $resolvedDestination
}

function Copy-SourceSnapshot(
	[string]$Source,
	[string]$SnapshotDirectory
) {
	$resolvedSource = [System.IO.Path]::GetFullPath($Source)
	$name = [System.IO.Path]::GetFileName($resolvedSource)
	if ([string]::IsNullOrWhiteSpace($name)) {
		throw "只读输入快照名称无效。"
	}
	$destination = Join-Path $SnapshotDirectory $name
	$sha256 = Copy-FileDurably $resolvedSource $destination $SnapshotDirectory $true
	return [pscustomobject]@{
		Path = [System.IO.Path]::GetFullPath($destination)
		Sha256 = $sha256
		Bytes = (Get-Item -LiteralPath $destination).Length
	}
}

function Invoke-SafeCommand(
	[string]$Label,
	[string]$Executable,
	[string[]]$Arguments,
	[int[]]$AllowedExitCodes
) {
	$captured = @(& $Executable @Arguments 2>&1)
	$exitCode = $LASTEXITCODE
	if ($exitCode -notin $AllowedExitCodes) {
		throw "$Label 失败（退出码 $exitCode）；现有双模型保持不变。"
	}
	return [pscustomobject]@{
		ExitCode = [int]$exitCode
		Output = @($captured)
	}
}

function Convert-CommandOutputToJson([object]$Result, [string]$Label) {
	try {
		return (@($Result.Output) -join [Environment]::NewLine) | ConvertFrom-Json
	} catch {
		throw "$Label 返回了无法识别的结果；现有双模型保持不变。"
	}
}

function Resolve-SolverDirectory([string]$Requested) {
	$candidates = New-Object System.Collections.Generic.List[string]
	if (-not [string]::IsNullOrWhiteSpace($Requested)) {
		$candidates.Add($Requested)
	}
	$candidates.Add((Join-Path $PSScriptRoot "..\AdvisorOfflineTools"))
	$candidates.Add((Join-Path $PSScriptRoot "..\AdvisorWorker"))
	$candidates.Add((Join-Path $PSScriptRoot "..\solver"))
	foreach ($candidate in $candidates) {
		try {
			$resolved = [System.IO.Path]::GetFullPath($candidate)
			if ([System.IO.File]::Exists((Join-Path $resolved "launch_solver.py"))) {
				return $resolved
			}
		} catch {
			continue
		}
	}
	throw "没有找到行为与决策帧离线工具。"
}

function Resolve-RustSolver([string]$Requested, [string]$ResolvedSolverDirectory) {
	if (-not [string]::IsNullOrWhiteSpace($Requested)) {
		try {
			$resolvedRequested = [System.IO.Path]::GetFullPath($Requested)
			if ([System.IO.File]::Exists($resolvedRequested)) {
				return $resolvedRequested
			}
		} catch {
			throw "指定的 Rust 求解器路径无效。"
		}
		throw "指定的 Rust 求解器不存在。"
	}
	$candidates = @(
		(Join-Path $PSScriptRoot "..\AdvisorWorker\metacompanion-solver.exe"),
		(Join-Path $ResolvedSolverDirectory "metacompanion-solver.exe"),
		(Join-Path $PSScriptRoot "..\solver-rust\target\release\metacompanion-solver.exe"),
		(Join-Path $PSScriptRoot "..\solver-rust\target\debug\metacompanion-solver.exe")
	)
	$available = New-Object System.Collections.Generic.List[System.IO.FileInfo]
	foreach ($candidate in ($candidates | Select-Object -Unique)) {
		try {
			$resolved = [System.IO.Path]::GetFullPath($candidate)
			if ([System.IO.File]::Exists($resolved)) {
				$available.Add((Get-Item -LiteralPath $resolved))
			}
		} catch {
			continue
		}
	}
	$selected = $available | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
	if ($null -ne $selected) {
		return $selected.FullName
	}
	throw "没有找到支持双模型门禁的 Rust 求解器。"
}

function Assert-PlainSnapshotName([string]$Name) {
	if ([string]::IsNullOrWhiteSpace($Name) -or
		-not [System.IO.Path]::GetFileName($Name).Equals(
			$Name,
			[System.StringComparison]::Ordinal)) {
		throw "行为语料快照名称不符合安全约束。"
	}
}

function Get-OneModelStatus([string]$Directory, [string]$Name) {
	$path = [System.IO.Path]::GetFullPath((Join-Path $Directory $Name))
	if (-not [System.IO.File]::Exists($path)) {
		return [pscustomobject]@{
			Name = $Name
			Path = $path
			Present = $false
			Sha256 = ""
		}
	}
	return [pscustomobject]@{
		Name = $Name
		Path = $path
		Present = $true
		Sha256 = Get-FileSha256 $path
	}
}

function Get-ExistingModelSet([string]$ResolvedDataDirectory) {
	return [pscustomobject]@{
		BehaviorPrior = Get-OneModelStatus $ResolvedDataDirectory $script:BehaviorPriorModelName
		DecisionRanker = Get-OneModelStatus $ResolvedDataDirectory $script:DecisionRankerModelName
	}
}

function New-PreservedResult(
	[string]$Status,
	[string]$Message,
	[object]$Existing,
	[string]$SourceKind
) {
	return [pscustomobject][ordered]@{
		schema = $script:UpdateSchema
		status = $Status
		message = $Message
		source_kind = $SourceKind
		behavior_prior_preserved = [bool]$Existing.BehaviorPrior.Present
		behavior_prior_sha256 = [string]$Existing.BehaviorPrior.Sha256
		decision_ranker_preserved = [bool]$Existing.DecisionRanker.Present
		decision_ranker_sha256 = [string]$Existing.DecisionRanker.Sha256
		search_ordering_only = $true
		candidate_generation_allowed = $false
		live_policy_eligible = $false
		rl_training_eligible = $false
		optimality_verified = $false
	}
}

function Resolve-InputSources(
	[string]$ResolvedDataDirectory,
	[string]$RequestedHistoricalSource,
	[string]$RequestedDecisionFrame
) {
	$historical = -not [string]::IsNullOrWhiteSpace($RequestedHistoricalSource)
	$sourceRoot = if ($historical) {
		[System.IO.Path]::GetFullPath($RequestedHistoricalSource)
	} else {
		$ResolvedDataDirectory
	}
	if (-not [System.IO.Directory]::Exists($sourceRoot)) {
		throw "指定的行为数据来源目录不存在。"
	}
	$decision = if ([string]::IsNullOrWhiteSpace($RequestedDecisionFrame)) {
		Join-Path $sourceRoot "advisor-decision-frame-v1.jsonl"
	} else {
		[System.IO.Path]::GetFullPath($RequestedDecisionFrame)
	}
	$trajectoryResults = Join-Path $sourceRoot "training-v2-results.jsonl"
	$trajectoryRuntime = Join-Path $sourceRoot "training-v2.jsonl"
	$trajectory = if ([System.IO.File]::Exists($trajectoryResults)) {
		$trajectoryResults
	} else {
		$trajectoryRuntime
	}
	$imitation = Join-Path $sourceRoot "behavior-imitation-v1.jsonl"
	$manifest = Join-Path $sourceRoot "behavior-imitation-v1.manifest.json"
	$imitationPresent = [System.IO.File]::Exists($imitation)
	$manifestPresent = [System.IO.File]::Exists($manifest)
	if ($imitationPresent -ne $manifestPresent) {
		throw "行为模仿语料与绑定清单不完整，已拒绝使用。"
	}
	return [pscustomobject]@{
		Kind = if ($historical) { "historical_import" } else { "runtime_logs" }
		Root = $sourceRoot
		Behavior = Join-Path $sourceRoot "behavior-v1.jsonl"
		DecisionFrames = $decision
		Trajectory = $trajectory
		Imitation = $imitation
		Manifest = $manifest
		UseBoundImitation = [bool]($historical -and $imitationPresent -and $manifestPresent)
	}
}

function Assert-BehaviorPriorArtifact([object]$Artifact, [string]$Path, [string]$Imitation, [string]$Manifest) {
	if ($Artifact.schema -cne "behavior-imitation-prior-v2" -or
		$Artifact.search_ordering_prior_ready -ne $true -or
		$Artifact.candidate_generation_allowed -ne $false -or
		$Artifact.live_policy_eligible -ne $false -or
		$Artifact.rl_training_eligible -ne $false -or
		$Artifact.optimality_verified -ne $false -or
		[string]$Artifact.source_dataset.sha256 -cne (Get-FileSha256 $Imitation) -or
		[string]$Artifact.source_manifest.sha256 -cne (Get-FileSha256 $Manifest)) {
		throw "行为模型训练产物不符合安全合同；现有双模型保持不变。"
	}
	if ((Get-FileSha256 $Path) -notmatch '^[0-9a-f]{64}$') {
		throw "行为模型训练产物哈希无效；现有双模型保持不变。"
	}
}

function Assert-DecisionRankerArtifact([object]$Artifact, [string]$Path, [string]$DecisionFrames, [string]$Behavior) {
	if ($Artifact.schema -cne "advisor-decision-ranker-v1" -or
		$Artifact.candidate_ranking_ready -ne $true -or
		$Artifact.user_visible_behavior_reference_eligible -ne $true -or
		$Artifact.candidate_generation_allowed -ne $false -or
		$Artifact.live_policy_eligible -ne $false -or
		$Artifact.rl_training_eligible -ne $false -or
		$Artifact.optimality_verified -ne $false -or
		[string]$Artifact.source_decision_frames.sha256 -cne (Get-FileSha256 $DecisionFrames) -or
		[string]$Artifact.source_behavior.sha256 -cne (Get-FileSha256 $Behavior)) {
		throw "本方决策排序产物不符合安全合同；现有双模型保持不变。"
	}
	if ((Get-FileSha256 $Path) -notmatch '^[0-9a-f]{64}$') {
		throw "本方决策排序产物哈希无效；现有双模型保持不变。"
	}
}

function Assert-ObservedPolicyEvaluation(
	[object]$Report,
	[string]$DecisionFrames,
	[string]$Behavior,
	[string]$Imitation,
	[string]$Manifest,
	[string]$Prior,
	[string]$Ranker
) {
	if ($Report.schema -cne "observed-policy-evaluation-v1" -or
		$Report.status -notin @("READY", "NOT_READY") -or
		$Report.source_binding_passed -ne $true -or
		$Report.decision_frame_contract_passed -ne $true -or
		$Report.game_level_split -ne $true -or
		$Report.training_split_only_updates_model -ne $true -or
		$Report.candidate_generation_allowed -ne $false -or
		$Report.live_policy_eligible -ne $false -or
		$Report.rl_training_eligible -ne $false -or
		$Report.optimality_verified -ne $false -or
		[string]$Report.source_decision_frames.sha256 -cne (Get-FileSha256 $DecisionFrames) -or
		[string]$Report.source_behavior.sha256 -cne (Get-FileSha256 $Behavior) -or
		[string]$Report.source_imitation_dataset.sha256 -cne (Get-FileSha256 $Imitation) -or
		[string]$Report.source_manifest.sha256 -cne (Get-FileSha256 $Manifest) -or
		[string]$Report.source_prior.sha256 -cne (Get-FileSha256 $Prior) -or
		[string]$Report.source_decision_ranker.sha256 -cne (Get-FileSha256 $Ranker)) {
		throw "双方观察策略联合评估不符合安全合同；现有双模型保持不变。"
	}
	$computedReady = (
		$Report.candidate_ranking_evaluation_ready -eq $true -and
		$Report.opponent_behavior_modeling_ready -eq $true -and
		$Report.search_ordering_prior_ready -eq $true)
	if (($Report.status -eq "READY") -ne $computedReady) {
		throw "双方观察策略联合评估的就绪状态相互矛盾；现有双模型保持不变。"
	}
}

function Assert-RustBehaviorPriorCheck([object]$Check, [string]$Candidate) {
	if ($Check.schema -cne "metacompanion-rust-behavior-prior-check-v1" -or
		$Check.status -cne "pass" -or
		$Check.search_ordering_only -ne $true -or
		$Check.candidate_generation_allowed -ne $false -or
		$Check.live_policy_eligible -ne $false -or
		$Check.rl_training_eligible -ne $false -or
		$Check.optimality_verified -ne $false -or
		[string]$Check.artifact_sha256 -cne (Get-FileSha256 $Candidate)) {
		throw "Rust 对手行为模型门禁结果无效；现有双模型保持不变。"
	}
}

function Assert-RustDecisionRankerCheck([object]$Check, [string]$Candidate) {
	if ($Check.schema -cne "metacompanion-rust-decision-ranker-check-v1" -or
		$Check.status -cne "pass" -or
		$Check.search_ordering_only -ne $true -or
		$Check.local_actions_only -ne $true -or
		$Check.candidate_generation_allowed -ne $false -or
		$Check.live_policy_eligible -ne $false -or
		$Check.rl_training_eligible -ne $false -or
		$Check.optimality_verified -ne $false -or
		[string]$Check.artifact_sha256 -cne (Get-FileSha256 $Candidate)) {
		throw "Rust 本方决策排序门禁结果无效；现有双模型保持不变。"
	}
}

function Copy-ArchiveIfNeeded(
	[object]$Status,
	[string]$ArchiveRoot,
	[string]$ResolvedDataDirectory
) {
	if (-not $Status.Present) {
		return
	}
	$modelDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArchiveRoot $Status.Name))
	if (-not (Test-PathWithinRoot $modelDirectory $ResolvedDataDirectory)) {
		throw "双模型归档目录超出了数据目录。"
	}
	[System.IO.Directory]::CreateDirectory($modelDirectory) | Out-Null
	$archive = Join-Path $modelDirectory ($Status.Sha256 + ".json")
	if (-not [System.IO.File]::Exists($archive)) {
		Copy-FileDurably $Status.Path $archive $ResolvedDataDirectory $true | Out-Null
	}
	if ((Get-FileSha256 $archive) -cne $Status.Sha256) {
		throw "旧双模型归档校验失败。"
	}
}

function Set-TargetFromStagedFile(
	[string]$StagedPath,
	[string]$TargetPath,
	[string]$ResolvedDataDirectory
) {
	$target = [System.IO.Path]::GetFullPath($TargetPath)
	if (-not (Test-PathWithinRoot $target $ResolvedDataDirectory)) {
		throw "双模型替换目标超出了数据目录。"
	}
	$temporary = Join-Path (
		[System.IO.Path]::GetDirectoryName($target)) (
		"." + [guid]::NewGuid().ToString("N") + ".model.tmp")
	$replaceBackup = Join-Path (
		[System.IO.Path]::GetDirectoryName($target)) (
		"." + [guid]::NewGuid().ToString("N") + ".replace.backup")
	Copy-FileDurably $StagedPath $temporary $ResolvedDataDirectory $true | Out-Null
	try {
		if ([System.IO.File]::Exists($target)) {
			[System.IO.File]::Replace($temporary, $target, $replaceBackup, $true)
		} else {
			[System.IO.File]::Move($temporary, $target)
		}
	} finally {
		if ([System.IO.File]::Exists($temporary)) {
			[System.IO.File]::Delete($temporary)
		}
		if ([System.IO.File]::Exists($replaceBackup)) {
			[System.IO.File]::Delete($replaceBackup)
		}
	}
}

function Restore-TransactionTarget(
	[string]$Target,
	[bool]$WasPresent,
	[string]$RollbackSnapshot,
	[string]$ExpectedSha256,
	[string]$ResolvedDataDirectory
) {
	if ($WasPresent) {
		Set-TargetFromStagedFile $RollbackSnapshot $Target $ResolvedDataDirectory
		if ((Get-FileSha256 $Target) -cne $ExpectedSha256) {
			throw "回滚后的文件哈希不一致。"
		}
	} elseif ([System.IO.File]::Exists($Target)) {
		if (-not (Test-PathWithinRoot $Target $ResolvedDataDirectory)) {
			throw "回滚删除目标超出了数据目录。"
		}
		[System.IO.File]::Delete($Target)
	}
}

function Install-AdvisorOrderingModels(
	[string]$BehaviorPriorCandidate,
	[string]$DecisionRankerCandidate,
	[string]$EvaluationPath,
	[object]$EvaluationReport,
	[string]$ResolvedDataDirectory,
	[string]$FailureInjection = ""
) {
	$priorCandidate = [System.IO.Path]::GetFullPath($BehaviorPriorCandidate)
	$rankerCandidate = [System.IO.Path]::GetFullPath($DecisionRankerCandidate)
	$evaluation = [System.IO.Path]::GetFullPath($EvaluationPath)
	foreach ($required in @($priorCandidate, $rankerCandidate, $evaluation)) {
		if (-not [System.IO.File]::Exists($required)) {
			throw "双模型事务安装缺少候选文件。"
		}
	}
	$priorSha256 = Get-FileSha256 $priorCandidate
	$rankerSha256 = Get-FileSha256 $rankerCandidate
	$evaluationSha256 = Get-FileSha256 $evaluation
	if ([string]$EvaluationReport.source_prior.sha256 -cne $priorSha256 -or
		[string]$EvaluationReport.source_decision_ranker.sha256 -cne $rankerSha256 -or
		$EvaluationReport.status -cne "READY" -or
		$EvaluationReport.search_ordering_prior_ready -ne $true) {
		throw "联合评估没有绑定待安装的双模型，已拒绝替换。"
	}

	$existing = Get-ExistingModelSet $ResolvedDataDirectory
	$priorChanged = (-not $existing.BehaviorPrior.Present -or
		$existing.BehaviorPrior.Sha256 -cne $priorSha256)
	$rankerChanged = (-not $existing.DecisionRanker.Present -or
		$existing.DecisionRanker.Sha256 -cne $rankerSha256)
	$status = if ($priorChanged -or $rankerChanged) { "activated" } else { "unchanged" }
	$manifestTarget = [System.IO.Path]::GetFullPath((Join-Path $ResolvedDataDirectory $script:InstallManifestName))
	$manifestWasPresent = [System.IO.File]::Exists($manifestTarget)
	$manifestPreviousSha256 = if ($manifestWasPresent) { Get-FileSha256 $manifestTarget } else { "" }
	$staging = [System.IO.Path]::GetFullPath((Join-Path $ResolvedDataDirectory (
		"advisor-model-staging-" + [guid]::NewGuid().ToString("N"))))
	if (-not (Test-PathWithinRoot $staging $ResolvedDataDirectory)) {
		throw "双模型 staging 目录超出了数据目录。"
	}
	[System.IO.Directory]::CreateDirectory($staging) | Out-Null
	$stagedPrior = Join-Path $staging $script:BehaviorPriorModelName
	$stagedRanker = Join-Path $staging $script:DecisionRankerModelName
	$stagedManifest = Join-Path $staging $script:InstallManifestName
	$rollbackPrior = Join-Path $staging "rollback-behavior-prior.json"
	$rollbackRanker = Join-Path $staging "rollback-decision-ranker.json"
	$rollbackManifest = Join-Path $staging "rollback-install-manifest.json"

	try {
		Copy-FileDurably $priorCandidate $stagedPrior $staging $true | Out-Null
		Copy-FileDurably $rankerCandidate $stagedRanker $staging $true | Out-Null
		if ((Get-FileSha256 $stagedPrior) -cne $priorSha256 -or
			(Get-FileSha256 $stagedRanker) -cne $rankerSha256) {
			throw "双模型 staging 哈希校验失败。"
		}
		if ($existing.BehaviorPrior.Present) {
			Copy-FileDurably $existing.BehaviorPrior.Path $rollbackPrior $staging $true | Out-Null
		}
		if ($existing.DecisionRanker.Present) {
			Copy-FileDurably $existing.DecisionRanker.Path $rollbackRanker $staging $true | Out-Null
		}
		if ($manifestWasPresent) {
			Copy-FileDurably $manifestTarget $rollbackManifest $staging $true | Out-Null
		}

		$archiveRoot = [System.IO.Path]::GetFullPath((Join-Path $ResolvedDataDirectory "advisor-model-archive"))
		Copy-ArchiveIfNeeded $existing.BehaviorPrior $archiveRoot $ResolvedDataDirectory
		Copy-ArchiveIfNeeded $existing.DecisionRanker $archiveRoot $ResolvedDataDirectory

		$installManifest = [pscustomobject][ordered]@{
			schema = "metacompanion-advisor-ordering-model-install-v1"
			installed_at_utc = [DateTime]::UtcNow.ToString(
				"o",
				[System.Globalization.CultureInfo]::InvariantCulture)
			status = $status
			models = [pscustomobject][ordered]@{
				behavior_prior = [pscustomobject][ordered]@{
					name = $script:BehaviorPriorModelName
					sha256 = $priorSha256
					previous_sha256 = [string]$existing.BehaviorPrior.Sha256
					scope = "opponent_observed_behavior"
				}
				decision_ranker = [pscustomobject][ordered]@{
					name = $script:DecisionRankerModelName
					sha256 = $rankerSha256
					previous_sha256 = [string]$existing.DecisionRanker.Sha256
					scope = "local_complete_hdt_candidates"
				}
			}
			evaluation = [pscustomobject][ordered]@{
				schema = "observed-policy-evaluation-v1"
				sha256 = $evaluationSha256
				policy_sha256 = [string]$EvaluationReport.policy_sha256
				status = "READY"
			}
			source_bindings = [pscustomobject][ordered]@{
				decision_frames_sha256 = [string]$EvaluationReport.source_decision_frames.sha256
				behavior_sha256 = [string]$EvaluationReport.source_behavior.sha256
				imitation_sha256 = [string]$EvaluationReport.source_imitation_dataset.sha256
				imitation_manifest_sha256 = [string]$EvaluationReport.source_manifest.sha256
			}
			search_ordering_only = $true
			candidate_generation_allowed = $false
			live_policy_eligible = $false
			rl_training_eligible = $false
			optimality_verified = $false
			hot_reload_supported = $true
			transactional_pair = $true
		}
		Write-Utf8JsonFile $stagedManifest $installManifest $staging

		if ($priorChanged) {
			Set-TargetFromStagedFile $stagedPrior $existing.BehaviorPrior.Path $ResolvedDataDirectory
		}
		if ($FailureInjection -ceq "after_prior") {
			throw "事务回滚自检注入：第一份模型替换后失败。"
		}
		if ($rankerChanged) {
			Set-TargetFromStagedFile $stagedRanker $existing.DecisionRanker.Path $ResolvedDataDirectory
		}
		if ($FailureInjection -ceq "after_ranker") {
			throw "事务回滚自检注入：第二份模型替换后失败。"
		}
		Set-TargetFromStagedFile $stagedManifest $manifestTarget $ResolvedDataDirectory
		if ($FailureInjection -ceq "after_manifest") {
			throw "事务回滚自检注入：联合清单替换后失败。"
		}
		if ((Get-FileSha256 $existing.BehaviorPrior.Path) -cne $priorSha256 -or
			(Get-FileSha256 $existing.DecisionRanker.Path) -cne $rankerSha256 -or
			(Get-FileSha256 $manifestTarget) -cne (Get-FileSha256 $stagedManifest)) {
			throw "双模型安装后的联合哈希校验失败。"
		}
		return [pscustomobject][ordered]@{
			status = $status
			behavior_prior_sha256 = $priorSha256
			previous_behavior_prior_sha256 = [string]$existing.BehaviorPrior.Sha256
			decision_ranker_sha256 = $rankerSha256
			previous_decision_ranker_sha256 = [string]$existing.DecisionRanker.Sha256
			evaluation_sha256 = $evaluationSha256
			manifest_sha256 = Get-FileSha256 $manifestTarget
		}
	} catch {
		$originalMessage = [string]$_.Exception.Message
		$rollbackFailure = ""
		try {
			Restore-TransactionTarget `
				$manifestTarget `
				$manifestWasPresent `
				$rollbackManifest `
				$manifestPreviousSha256 `
				$ResolvedDataDirectory
			Restore-TransactionTarget `
				$existing.DecisionRanker.Path `
				([bool]$existing.DecisionRanker.Present) `
				$rollbackRanker `
				([string]$existing.DecisionRanker.Sha256) `
				$ResolvedDataDirectory
			Restore-TransactionTarget `
				$existing.BehaviorPrior.Path `
				([bool]$existing.BehaviorPrior.Present) `
				$rollbackPrior `
				([string]$existing.BehaviorPrior.Sha256) `
				$ResolvedDataDirectory
		} catch {
			$rollbackFailure = [string]$_.Exception.Message
		}
		if (-not [string]::IsNullOrWhiteSpace($rollbackFailure)) {
			throw "双模型安装失败，且自动回滚未能完成：$rollbackFailure"
		}
		throw "双模型安装失败，已验证恢复旧版本：$originalMessage"
	} finally {
		if ([System.IO.Directory]::Exists($staging) -and
			(Test-PathWithinRoot $staging $ResolvedDataDirectory)) {
			[System.IO.Directory]::Delete($staging, $true)
		}
	}
}

function Invoke-AdvisorOrderingModelUpdate(
	[string]$RequestedDataDirectory,
	[string]$RequestedHistoricalSource,
	[string]$RequestedDecisionFrame,
	[string]$RequestedSolverDirectory,
	[string]$RequestedRustSolver,
	[string]$RequestedPython,
	[string]$RequestedBehaviorPolicy,
	[string]$RequestedPriorPolicy,
	[string]$RequestedRankerPolicy,
	[string]$RequestedEvaluationPolicy,
	[string]$RequestedCandidateAlignmentPolicy,
	[string]$RequestedCardRules,
	[int]$RequestedRankerEpochs
) {
	if ([string]::IsNullOrWhiteSpace($RequestedDataDirectory)) {
		throw "模型安装目录不能为空。"
	}
	$resolvedData = [System.IO.Path]::GetFullPath($RequestedDataDirectory)
	[System.IO.Directory]::CreateDirectory($resolvedData) | Out-Null
	$existing = Get-ExistingModelSet $resolvedData
	$sources = Resolve-InputSources $resolvedData $RequestedHistoricalSource $RequestedDecisionFrame
	if (-not [System.IO.File]::Exists($sources.Behavior)) {
		return New-PreservedResult `
			"no_data" `
			"尚无可用的双方公开行为记录；现有模型保持不变（两份均不替换）。" `
			$existing `
			$sources.Kind
	}
	if (-not [System.IO.File]::Exists($sources.DecisionFrames)) {
		return New-PreservedResult `
			"not_ready" `
			"已找到双方行为，但尚无 HDT 完整本方决策帧；现有双模型保持不变。" `
			$existing `
			$sources.Kind
	}

	$solver = Resolve-SolverDirectory $RequestedSolverDirectory
	$rust = Resolve-RustSolver $RequestedRustSolver $solver
	$launcher = Join-Path $solver "launch_solver.py"
	$buildRoot = [System.IO.Path]::GetFullPath((Join-Path $resolvedData "advisor-model-builds"))
	if (-not (Test-PathWithinRoot $buildRoot $resolvedData)) {
		throw "双模型构建目录超出了数据目录。"
	}
	[System.IO.Directory]::CreateDirectory($buildRoot) | Out-Null
	$runName = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
		[guid]::NewGuid().ToString("N").Substring(0, 8)
	$run = Join-Path $buildRoot $runName
	$sourceSnapshots = Join-Path $run "source-snapshots"
	[System.IO.Directory]::CreateDirectory($sourceSnapshots) | Out-Null

	$behaviorForTraining = ""
	$decisionForTraining = ""
	$imitationForTraining = ""
	$manifestForTraining = ""
	$readinessReport = $null
	if ($sources.UseBoundImitation) {
		$behaviorForTraining = (Copy-SourceSnapshot $sources.Behavior $sourceSnapshots).Path
		$decisionForTraining = (Copy-SourceSnapshot $sources.DecisionFrames $sourceSnapshots).Path
		$imitationForTraining = (Copy-SourceSnapshot $sources.Imitation $sourceSnapshots).Path
		$manifestForTraining = (Copy-SourceSnapshot $sources.Manifest $sourceSnapshots).Path
	} else {
		if (-not [System.IO.File]::Exists($sources.Trajectory)) {
			return New-PreservedResult `
				"not_ready" `
				"已有行为记录，但还没有可关联的终局记录；现有双模型保持不变。" `
				$existing `
				$sources.Kind
		}
		$snapshots = Join-Path $run "behavior-snapshots"
		[System.IO.Directory]::CreateDirectory($snapshots) | Out-Null
		$readinessPath = Join-Path $run "behavior-readiness.json"
		$auditArgs = @(
			$launcher,
			"audit-runtime-behavior-learning",
			"--behavior", $sources.Behavior,
			"--trajectory", $sources.Trajectory,
			"--snapshot-dir", $snapshots,
			"--output", $readinessPath
		)
		if (-not [string]::IsNullOrWhiteSpace($RequestedBehaviorPolicy)) {
			$auditArgs += @("--policy", [System.IO.Path]::GetFullPath($RequestedBehaviorPolicy))
		}
		$auditResult = Invoke-SafeCommand "双方行为与终局联审" $RequestedPython $auditArgs @(0, 3, 4)
		$readinessReport = Read-Utf8Json $readinessPath "双方行为与终局联审"
		if ($readinessReport.status -notin @("READY", "NOT_READY", "NO_DATA") -or
			$readinessReport.rl_training_ready -ne $false) {
			throw "双方行为与终局联审不符合安全合同；现有双模型保持不变。"
		}
		if ($readinessReport.status -ne "READY" -or $auditResult.ExitCode -ne 0) {
			$status = if ($readinessReport.status -eq "NO_DATA") { "no_data" } else { "not_ready" }
			$message = if ($status -eq "no_data") {
				"尚无可联审的双方行为与终局；现有双模型保持不变。"
			} else {
				"双方行为已保存，但数量或质量尚未达到模仿训练门槛；现有双模型保持不变。"
			}
			return New-PreservedResult $status $message $existing $sources.Kind
		}
		Assert-PlainSnapshotName ([string]$readinessReport.behavior_snapshot)
		Assert-PlainSnapshotName ([string]$readinessReport.trajectory_snapshot)
		$behaviorForTraining = [System.IO.Path]::GetFullPath((Join-Path $snapshots ([string]$readinessReport.behavior_snapshot)))
		$trajectorySnapshot = [System.IO.Path]::GetFullPath((Join-Path $snapshots ([string]$readinessReport.trajectory_snapshot)))
		if (-not (Test-PathWithinRoot $behaviorForTraining $snapshots) -or
			-not (Test-PathWithinRoot $trajectorySnapshot $snapshots) -or
			(Get-FileSha256 $behaviorForTraining) -cne [string]$readinessReport.behavior_input_sha256 -or
			(Get-FileSha256 $trajectorySnapshot) -cne [string]$readinessReport.trajectory_input_sha256) {
			throw "双方行为与终局快照校验失败；现有双模型保持不变。"
		}
		$decisionForTraining = (Copy-SourceSnapshot $sources.DecisionFrames $sourceSnapshots).Path
		$imitationForTraining = Join-Path $run "behavior-imitation-v1.jsonl"
		$manifestForTraining = Join-Path $run "behavior-imitation-v1.manifest.json"
		$promoteArgs = @(
			$launcher,
			"promote-behavior-imitation",
			"--behavior", $behaviorForTraining,
			"--trajectory", $trajectorySnapshot,
			"--output", $imitationForTraining,
			"--manifest", $manifestForTraining
		)
		if (-not [string]::IsNullOrWhiteSpace($RequestedBehaviorPolicy)) {
			$promoteArgs += @("--policy", [System.IO.Path]::GetFullPath($RequestedBehaviorPolicy))
		}
		Invoke-SafeCommand "双方行为模仿语料晋级" $RequestedPython $promoteArgs @(0) | Out-Null
	}

	if (-not [string]::IsNullOrWhiteSpace($RequestedCandidateAlignmentPolicy) -or
		-not [string]::IsNullOrWhiteSpace($RequestedCardRules)) {
		$alignmentPath = Join-Path $run "behavior-candidate-alignment-v1.json"
		$alignmentArgs = @(
			$launcher,
			"audit-behavior-candidates",
			"--input", $imitationForTraining,
			"--manifest", $manifestForTraining,
			"--output", $alignmentPath
		)
		if (-not [string]::IsNullOrWhiteSpace($RequestedCandidateAlignmentPolicy)) {
			$alignmentArgs += @("--policy", [System.IO.Path]::GetFullPath($RequestedCandidateAlignmentPolicy))
		}
		if (-not [string]::IsNullOrWhiteSpace($RequestedCardRules)) {
			$alignmentArgs += @("--rules", [System.IO.Path]::GetFullPath($RequestedCardRules))
		}
		$alignmentResult = Invoke-SafeCommand "可选的规则重建候选审计" $RequestedPython $alignmentArgs @(0, 3)
		$alignment = Read-Utf8Json $alignmentPath "可选的规则重建候选审计"
		if ($alignment.schema -cne "behavior-candidate-alignment-report-v1" -or
			$alignment.contract_passed -ne $true -or
			$alignment.candidate_generation_allowed -ne $false -or
			$alignment.rl_training_eligible -ne $false -or
			$alignment.optimality_verified -ne $false) {
			throw "可选的规则重建候选审计不符合安全合同；现有双模型保持不变。"
		}
		if ($alignmentResult.ExitCode -ne 0 -or
			$alignment.candidate_ranking_training_ready -ne $true) {
			return New-PreservedResult `
				"not_ready" `
				"规则重建候选审计未通过；已保留数据与现有双模型。" `
				$existing `
				$sources.Kind
		}
	}

	$priorCandidate = Join-Path $run $script:BehaviorPriorModelName
	$priorArgs = @(
		$launcher,
		"train-behavior-prior",
		"--input", $imitationForTraining,
		"--manifest", $manifestForTraining,
		"--output", $priorCandidate
	)
	if (-not [string]::IsNullOrWhiteSpace($RequestedPriorPolicy)) {
		$priorArgs += @("--policy", [System.IO.Path]::GetFullPath($RequestedPriorPolicy))
	}
	$priorResult = Invoke-SafeCommand "对手行为排序模型训练" $RequestedPython $priorArgs @(0, 3)
	$priorArtifact = Read-Utf8Json $priorCandidate "对手行为排序模型训练"
	if ($priorResult.ExitCode -ne 0 -or $priorArtifact.search_ordering_prior_ready -ne $true) {
		return New-PreservedResult `
			"not_ready" `
			"对手行为模型的独立验证尚未通过；现有双模型保持不变。" `
			$existing `
			$sources.Kind
	}
	Assert-BehaviorPriorArtifact `
		$priorArtifact `
		$priorCandidate `
		$imitationForTraining `
		$manifestForTraining

	$rankerCandidate = Join-Path $run $script:DecisionRankerModelName
	$rankerArgs = @(
		$launcher,
		"train-decision-ranker",
		"--decision-frames", $decisionForTraining,
		"--behavior", $behaviorForTraining,
		"--output", $rankerCandidate,
		"--epochs", [string]$RequestedRankerEpochs
	)
	if (-not [string]::IsNullOrWhiteSpace($RequestedRankerPolicy)) {
		$rankerArgs += @("--policy", [System.IO.Path]::GetFullPath($RequestedRankerPolicy))
	}
	$rankerResult = Invoke-SafeCommand "本方决策排序模型训练" $RequestedPython $rankerArgs @(0, 3)
	$rankerArtifact = Read-Utf8Json $rankerCandidate "本方决策排序模型训练"
	if ($rankerResult.ExitCode -ne 0 -or
		$rankerArtifact.candidate_ranking_ready -ne $true -or
		$rankerArtifact.user_visible_behavior_reference_eligible -ne $true) {
		return New-PreservedResult `
			"not_ready" `
			"本方决策排序模型的独立验证尚未通过；现有双模型保持不变。" `
			$existing `
			$sources.Kind
	}
	Assert-DecisionRankerArtifact `
		$rankerArtifact `
		$rankerCandidate `
		$decisionForTraining `
		$behaviorForTraining

	$evaluationPath = Join-Path $run "observed-policy-evaluation-v1.json"
	$evaluationArgs = @(
		$launcher,
		"evaluate-observed-policy",
		"--decision-frames", $decisionForTraining,
		"--behavior", $behaviorForTraining,
		"--imitation", $imitationForTraining,
		"--manifest", $manifestForTraining,
		"--prior", $priorCandidate,
		"--ranker", $rankerCandidate,
		"--output", $evaluationPath
	)
	if (-not [string]::IsNullOrWhiteSpace($RequestedEvaluationPolicy)) {
		$evaluationArgs += @("--policy", [System.IO.Path]::GetFullPath($RequestedEvaluationPolicy))
	}
	$evaluationResult = Invoke-SafeCommand "双方观察策略联合评估" $RequestedPython $evaluationArgs @(0, 3)
	$evaluationReport = Read-Utf8Json $evaluationPath "双方观察策略联合评估"
	Assert-ObservedPolicyEvaluation `
		$evaluationReport `
		$decisionForTraining `
		$behaviorForTraining `
		$imitationForTraining `
		$manifestForTraining `
		$priorCandidate `
		$rankerCandidate
	if ($evaluationResult.ExitCode -ne 0 -or $evaluationReport.status -ne "READY") {
		return New-PreservedResult `
			"not_ready" `
			"本方排序与对手行为模型的联合评估尚未通过；现有双模型保持不变。" `
			$existing `
			$sources.Kind
	}

	$rustPriorResult = Invoke-SafeCommand "Rust 对手行为模型门禁" $rust @(
		"behavior-prior-check", "--path", $priorCandidate
	) @(0)
	$rustPriorCheck = Convert-CommandOutputToJson $rustPriorResult "Rust 对手行为模型门禁"
	Assert-RustBehaviorPriorCheck $rustPriorCheck $priorCandidate
	$rustRankerResult = Invoke-SafeCommand "Rust 本方决策排序门禁" $rust @(
		"decision-ranker-check", "--path", $rankerCandidate
	) @(0)
	$rustRankerCheck = Convert-CommandOutputToJson $rustRankerResult "Rust 本方决策排序门禁"
	Assert-RustDecisionRankerCheck $rustRankerCheck $rankerCandidate

	$installed = Install-AdvisorOrderingModels `
		$priorCandidate `
		$rankerCandidate `
		$evaluationPath `
		$evaluationReport `
		$resolvedData
	$candidateTest = $evaluationReport.candidate_ranking.test
	$opponentTest = $evaluationReport.opponent_behavior.test
	return [pscustomobject][ordered]@{
		schema = $script:UpdateSchema
		status = [string]$installed.status
		message = if ($installed.status -eq "activated") {
			"本方决策排序与对手行为模型已通过联合门禁并成对启用；Rust worker 会自动热加载。"
		} else {
			"双模型内容未变化，继续使用当前已验证版本。"
		}
		source_kind = [string]$sources.Kind
		behavior_prior_sha256 = [string]$installed.behavior_prior_sha256
		decision_ranker_sha256 = [string]$installed.decision_ranker_sha256
		evaluation_sha256 = [string]$installed.evaluation_sha256
		decision_frame_records = [long]$evaluationReport.source_decision_frames.record_count
		behavior_records = [long]$evaluationReport.source_imitation_dataset.record_count
		local_test_top1_accuracy = [double]$candidateTest.top1_accuracy
		local_test_top3_accuracy = [double]$candidateTest.top3_accuracy
		local_test_log_loss = [double]$candidateTest.log_loss
		opponent_test_kind_log_loss = [double]$opponentTest.kind_log_loss
		search_ordering_only = $true
		candidate_generation_allowed = $false
		live_policy_eligible = $false
		rl_training_eligible = $false
		optimality_verified = $false
		hot_reload_supported = $true
		transactional_pair = $true
	}
}

function Invoke-AdvisorOrderingModelUpdateSelfTest {
	$temp = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) (
		"metacompanion-advisor-model-update-" + [guid]::NewGuid().ToString("N"))))
	[System.IO.Directory]::CreateDirectory($temp) | Out-Null
	try {
		$solver = Resolve-SolverDirectory $SolverDirectory
		$rust = Resolve-RustSolver $RustSolverBinaryPath $solver
		$launcher = Join-Path $solver "launch_solver.py"
		$fixtureBuilder = Join-Path $solver "tools\observed_policy_fixture.py"
		if (-not [System.IO.File]::Exists($fixtureBuilder)) {
			throw "双模型合成夹具生成器不存在。"
		}
		$fixture = Join-Path $temp "fixture"
		Invoke-SafeCommand "自检双模型合成夹具" $PythonExecutable @(
			$fixtureBuilder, "--output-dir", $fixture
		) @(0) | Out-Null

		$alignment = Join-Path $temp "candidate-alignment-negative.json"
		Invoke-SafeCommand "自检规则重建候选负向门禁" $PythonExecutable @(
			$launcher,
			"audit-behavior-candidates",
			"--input", (Join-Path $solver "fixtures\behavior-prior-readiness-v1.jsonl"),
			"--manifest", (Join-Path $solver "fixtures\behavior-prior-readiness-v1.manifest.json"),
			"--policy", (Join-Path $solver "fixtures\behavior-candidate-alignment-policy-v1.json"),
			"--rules", (Join-Path $solver "metacompanion_solver\rules_data\hdt-visible-point-effects-v1.json"),
			"--output", $alignment
		) @(3) | Out-Null
		$alignmentReport = Read-Utf8Json $alignment "自检规则重建候选负向门禁"
		if ($alignmentReport.status -cne "NOT_READY" -or
			$alignmentReport.candidate_ranking_training_ready -ne $false -or
			$alignmentReport.candidate_generation_allowed -ne $false) {
			throw "规则重建候选负向门禁自检失败。"
		}

		$prior = Join-Path $temp $script:BehaviorPriorModelName
		Invoke-SafeCommand "自检对手行为模型训练" $PythonExecutable @(
			$launcher,
			"train-behavior-prior",
			"--input", (Join-Path $fixture "behavior-imitation-v1.jsonl"),
			"--manifest", (Join-Path $fixture "behavior-imitation-v1.manifest.json"),
			"--policy", (Join-Path $fixture "behavior-prior-policy-v1.json"),
			"--output", $prior
		) @(0) | Out-Null
		$ranker = Join-Path $temp $script:DecisionRankerModelName
		Invoke-SafeCommand "自检本方决策排序训练" $PythonExecutable @(
			$launcher,
			"train-decision-ranker",
			"--decision-frames", (Join-Path $fixture "advisor-decision-frame-v1.jsonl"),
			"--behavior", (Join-Path $fixture "behavior-v1.jsonl"),
			"--policy", (Join-Path $fixture "decision-ranker-policy-v1.json"),
			"--epochs", "1",
			"--output", $ranker
		) @(0) | Out-Null
		$evaluation = Join-Path $temp "observed-policy-evaluation-v1.json"
		Invoke-SafeCommand "自检双方观察策略联合评估" $PythonExecutable @(
			$launcher,
			"evaluate-observed-policy",
			"--decision-frames", (Join-Path $fixture "advisor-decision-frame-v1.jsonl"),
			"--behavior", (Join-Path $fixture "behavior-v1.jsonl"),
			"--imitation", (Join-Path $fixture "behavior-imitation-v1.jsonl"),
			"--manifest", (Join-Path $fixture "behavior-imitation-v1.manifest.json"),
			"--prior", $prior,
			"--ranker", $ranker,
			"--policy", (Join-Path $fixture "observed-policy-evaluation-policy-v1.json"),
			"--output", $evaluation
		) @(0) | Out-Null
		$evaluationReport = Read-Utf8Json $evaluation "自检双方观察策略联合评估"
		if ($evaluationReport.status -cne "READY" -or
			$evaluationReport.search_ordering_prior_ready -ne $true -or
			$evaluationReport.rl_training_eligible -ne $false -or
			$evaluationReport.optimality_verified -ne $false) {
			throw "双方观察策略联合评估自检失败。"
		}

		$priorRustResult = Invoke-SafeCommand "自检 Rust 对手行为门禁" $rust @(
			"behavior-prior-check", "--path", $prior
		) @(0)
		Assert-RustBehaviorPriorCheck `
			(Convert-CommandOutputToJson $priorRustResult "自检 Rust 对手行为门禁") `
			$prior
		$rankerRustResult = Invoke-SafeCommand "自检 Rust 本方排序门禁" $rust @(
			"decision-ranker-check", "--path", $ranker
		) @(0)
		Assert-RustDecisionRankerCheck `
			(Convert-CommandOutputToJson $rankerRustResult "自检 Rust 本方排序门禁") `
			$ranker

		$installRoot = Join-Path $temp "install"
		[System.IO.Directory]::CreateDirectory($installRoot) | Out-Null
		$first = Install-AdvisorOrderingModels `
			$prior $ranker $evaluation $evaluationReport $installRoot
		$second = Install-AdvisorOrderingModels `
			$prior $ranker $evaluation $evaluationReport $installRoot
		if ($first.status -cne "activated" -or $second.status -cne "unchanged" -or
			$first.behavior_prior_sha256 -cne $second.behavior_prior_sha256 -or
			$first.decision_ranker_sha256 -cne $second.decision_ranker_sha256) {
			throw "双模型更新与不变分支自检失败。"
		}

		$beforePrior = Get-FileSha256 (Join-Path $installRoot $script:BehaviorPriorModelName)
		$beforeRanker = Get-FileSha256 (Join-Path $installRoot $script:DecisionRankerModelName)
		$beforeManifest = Get-FileSha256 (Join-Path $installRoot $script:InstallManifestName)
		$changedPrior = Join-Path $temp "changed-prior.json"
		$changedRanker = Join-Path $temp "changed-ranker.json"
		[System.IO.File]::Copy($prior, $changedPrior, $false)
		[System.IO.File]::Copy($ranker, $changedRanker, $false)
		[System.IO.File]::AppendAllText($changedPrior, " ", [System.Text.Encoding]::UTF8)
		[System.IO.File]::AppendAllText($changedRanker, " ", [System.Text.Encoding]::UTF8)
		$changedEvaluation = $evaluationReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
		$changedEvaluation.source_prior.sha256 = Get-FileSha256 $changedPrior
		$changedEvaluation.source_prior.bytes = (Get-Item -LiteralPath $changedPrior).Length
		$changedEvaluation.source_decision_ranker.sha256 = Get-FileSha256 $changedRanker
		$changedEvaluation.source_decision_ranker.bytes = (Get-Item -LiteralPath $changedRanker).Length
		$changedEvaluationPath = Join-Path $temp "changed-evaluation.json"
		Write-Utf8JsonFile $changedEvaluationPath $changedEvaluation $temp
		$rollbackObserved = $false
		try {
			Install-AdvisorOrderingModels `
				$changedPrior `
				$changedRanker `
				$changedEvaluationPath `
				$changedEvaluation `
				$installRoot `
				"after_prior" | Out-Null
		} catch {
			$rollbackObserved = ([string]$_.Exception.Message -like "*已验证恢复旧版本*")
		}
		if (-not $rollbackObserved -or
			(Get-FileSha256 (Join-Path $installRoot $script:BehaviorPriorModelName)) -cne $beforePrior -or
			(Get-FileSha256 (Join-Path $installRoot $script:DecisionRankerModelName)) -cne $beforeRanker -or
			(Get-FileSha256 (Join-Path $installRoot $script:InstallManifestName)) -cne $beforeManifest) {
			throw "双模型事务回滚自检失败。"
		}
		Write-Output "SELFTEST_OK=1"
		Write-Output "SELFTEST OK：本方决策排序、对手行为模型、联合评估、双 Rust 门禁、成对更新、不变与回滚均通过。"
	} finally {
		if ([System.IO.Directory]::Exists($temp) -and
			(Test-PathWithinRoot $temp ([System.IO.Path]::GetTempPath()))) {
			[System.IO.Directory]::Delete($temp, $true)
		}
	}
}

if ($SelfTest) {
	Invoke-AdvisorOrderingModelUpdateSelfTest
	return
}

try {
	$result = Invoke-AdvisorOrderingModelUpdate `
		$DataDirectory `
		$HistoricalSourceDirectory `
		$DecisionFramePath `
		$SolverDirectory `
		$RustSolverBinaryPath `
		$PythonExecutable `
		$BehaviorPolicyPath `
		$PriorPolicyPath `
		$DecisionRankerPolicyPath `
		$ObservedPolicyEvaluationPolicyPath `
		$CandidateAlignmentPolicyPath `
		$CardRulesPath `
		$RankerEpochs
	$result | ConvertTo-Json -Depth 12
	if ($result.status -eq "no_data") {
		exit 4
	}
	if ($result.status -eq "not_ready") {
		exit 3
	}
	exit 0
} catch {
	[pscustomobject][ordered]@{
		schema = $script:UpdateSchema
		status = "error"
		message = [string]$_.Exception.Message
		current_models_preserved = $true
		candidate_generation_allowed = $false
		rl_training_eligible = $false
		optimality_verified = $false
	} | ConvertTo-Json -Depth 6
	exit 1
}
