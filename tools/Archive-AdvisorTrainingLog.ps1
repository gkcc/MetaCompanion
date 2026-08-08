[CmdletBinding()]
param(
	[string]$DataDirectory = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\AdvisorWorker",
	[string]$FileName = "training-v2.jsonl",
	[string]$Reason = "pre-power-contract-rotation",
	[switch]$SkipWorkerCheck,
	[switch]$SelfTest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$script:ManifestSchema = "metacompanion-training-log-archive-v1"

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
	$prefix = $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar
	return $resolvedPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-AdvisorWorkerSnapshot([string]$ResolvedDataDirectory) {
	$matches = New-Object System.Collections.Generic.List[object]
	foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
		$name = [string]$process.ProcessName
		if ($name -in @("metacompanion-solver", "MetaCompanion.Advisor.Worker", "advisor_worker")) {
			$matches.Add([pscustomobject][ordered]@{
				process_name = $name
				process_id = [int]$process.Id
			})
		}
	}

	# A compatibility Python worker cannot be identified by its process name alone.
	# Inspect its command line only for matching; never return or persist the command line,
	# because it can contain the short-lived local session token.
	try {
		foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name = 'python.exe' OR Name = 'pythonw.exe'" -ErrorAction Stop)) {
			$commandLine = [string]$process.CommandLine
			if ($commandLine -match '(?i)metacompanion_solver|launch_solver\.py' -and
				($commandLine -match [regex]::Escape($ResolvedDataDirectory) -or
				 $commandLine -match '(?i)metacompanion')) {
				$matches.Add([pscustomobject][ordered]@{
					process_name = [System.IO.Path]::GetFileNameWithoutExtension([string]$process.Name)
					process_id = [int]$process.ProcessId
				})
			}
		}
	} catch {
		# Native worker detection remains authoritative for the default Rust deployment.
	}
	return @($matches | Sort-Object process_name, process_id -Unique)
}

function Get-TrainingLogSummary([string]$Path) {
	$kindCounts = [ordered]@{}
	$lineCount = 0L
	$blankLineCount = 0L
	$invalidKindLineCount = 0L
	$reader = New-Object System.IO.StreamReader(
		[System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
			[System.IO.FileAccess]::Read, [System.IO.FileShare]::Read),
		[System.Text.Encoding]::UTF8,
		$true,
		65536)
	try {
		while (($line = $reader.ReadLine()) -ne $null) {
			$lineCount++
			if ([string]::IsNullOrWhiteSpace($line)) {
				$blankLineCount++
				continue
			}
		$match = [regex]::Match($line, '"kind"\s*:\s*"(?<kind>[A-Za-z0-9_.:-]+)"')
			if (-not $match.Success) {
				$invalidKindLineCount++
				continue
			}
			$kind = $match.Groups["kind"].Value
			if (-not $kindCounts.Contains($kind)) {
				$kindCounts[$kind] = 0L
			}
			$kindCounts[$kind] = [long]$kindCounts[$kind] + 1L
		}
	} finally {
		$reader.Dispose()
	}
	return [pscustomobject][ordered]@{
		line_count = $lineCount
		blank_line_count = $blankLineCount
		unclassified_line_count = $invalidKindLineCount
		kind_counts = $kindCounts
	}
}

function Get-FileSha256([string]$Path) {
	$algorithm = [System.Security.Cryptography.SHA256]::Create()
	$stream = [System.IO.File]::Open(
		$Path,
		[System.IO.FileMode]::Open,
		[System.IO.FileAccess]::Read,
		[System.IO.FileShare]::Read)
	try {
		$bytes = $algorithm.ComputeHash($stream)
		return ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
	} finally {
		$stream.Dispose()
		$algorithm.Dispose()
	}
}

function Invoke-AdvisorTrainingLogArchive(
	[string]$Root,
	[string]$ActiveFileName,
	[string]$ArchiveReason,
	[bool]$AllowRunningWorker
) {
	if ([string]::IsNullOrWhiteSpace($Root)) {
		throw "训练日志目录不能为空。"
	}
	if ([string]::IsNullOrWhiteSpace($ActiveFileName) -or
		-not [System.IO.Path]::GetFileName($ActiveFileName).Equals(
			$ActiveFileName, [System.StringComparison]::Ordinal)) {
		throw "训练日志文件名必须是单独的文件名，不能包含目录。"
	}
	$resolvedRoot = [System.IO.Path]::GetFullPath($Root)
	$activePath = [System.IO.Path]::GetFullPath((Join-Path $resolvedRoot $ActiveFileName))
	if (-not (Test-PathWithinRoot $activePath $resolvedRoot)) {
		throw "训练日志路径超出了指定的数据目录。"
	}
	if (-not [System.IO.File]::Exists($activePath)) {
		throw "没有找到待归档的训练日志：$activePath"
	}

	if (-not $AllowRunningWorker) {
		$workers = @(Get-AdvisorWorkerSnapshot $resolvedRoot)
		if ($workers.Count -gt 0) {
			$identities = @($workers | ForEach-Object {
				$_.process_name + "(" + $_.process_id.ToString([System.Globalization.CultureInfo]::InvariantCulture) + ")"
			}) -join ", "
			throw "检测到本机建议 worker 仍在运行（$identities）。请先停止 worker，再归档训练日志。"
		}
	}

	$before = New-Object System.IO.FileInfo($activePath)
	$beforeLength = [long]$before.Length
	$beforeWriteUtc = $before.LastWriteTimeUtc
	$sha256 = Get-FileSha256 $activePath
	$summary = Get-TrainingLogSummary $activePath
	$after = New-Object System.IO.FileInfo($activePath)
	if ([long]$after.Length -ne $beforeLength -or $after.LastWriteTimeUtc -ne $beforeWriteUtc) {
		throw "读取期间训练日志发生了变化，已取消归档；请确认 worker 完全停止后重试。"
	}

	$archiveDirectory = [System.IO.Path]::GetFullPath((Join-Path $resolvedRoot "archive"))
	if (-not (Test-PathWithinRoot $archiveDirectory $resolvedRoot)) {
		throw "归档目录超出了指定的数据目录。"
	}
	if (-not [System.IO.Path]::GetPathRoot($archiveDirectory).Equals(
		[System.IO.Path]::GetPathRoot($activePath),
		[System.StringComparison]::OrdinalIgnoreCase)) {
		throw "归档目录必须和活动日志位于同一个磁盘卷。"
	}
	[System.IO.Directory]::CreateDirectory($archiveDirectory) | Out-Null

	$shortHash = $sha256.Substring(0, 12)
	$archiveName = [System.IO.Path]::GetFileNameWithoutExtension($ActiveFileName) +
		".pre-power." + $shortHash + ".jsonl"
	$archivePath = [System.IO.Path]::GetFullPath((Join-Path $archiveDirectory $archiveName))
	$manifestPath = $archivePath + ".manifest.json"
	if (-not (Test-PathWithinRoot $archivePath $archiveDirectory)) {
		throw "归档目标超出了归档目录。"
	}
	if ([System.IO.File]::Exists($archivePath) -or [System.IO.File]::Exists($manifestPath)) {
		throw "相同内容的归档已经存在，未覆盖任何文件：$archivePath"
	}

	$manifest = [pscustomobject][ordered]@{
		schema = $script:ManifestSchema
		archived_at_utc = [DateTime]::UtcNow.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
		reason = $ArchiveReason
		original_file_name = $ActiveFileName
		archive_file_name = $archiveName
		sha256 = $sha256
		byte_count = $beforeLength
		last_write_utc = $beforeWriteUtc.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
		summary = $summary
		read_only = $true
	}
	$manifestJson = $manifest | ConvertTo-Json -Depth 8
	$tempManifest = Join-Path $archiveDirectory ("." + [guid]::NewGuid().ToString("N") + ".manifest.tmp")
	$encoding = New-Object System.Text.UTF8Encoding($false)
	[System.IO.File]::WriteAllText($tempManifest, $manifestJson + [Environment]::NewLine, $encoding)
	$archiveMoved = $false
	$manifestMoved = $false
	try {
		# File.Move is an atomic rename here because source and target are on the same volume.
		[System.IO.File]::Move($activePath, $archivePath)
		$archiveMoved = $true
		[System.IO.File]::Move($tempManifest, $manifestPath)
		$manifestMoved = $true
		if ((Get-FileSha256 $archivePath) -ne $sha256) {
			throw "归档完成后的哈希校验失败。"
		}
		$archived = New-Object System.IO.FileInfo($archivePath)
		$archived.IsReadOnly = $true
		$manifestFile = New-Object System.IO.FileInfo($manifestPath)
		$manifestFile.IsReadOnly = $true
	} catch {
		# Restore the active file whenever the two-file handoff did not finish cleanly.
		# This avoids leaving a half-rotated corpus after a manifest or attribute failure.
		if ($manifestMoved -and [System.IO.File]::Exists($manifestPath)) {
			$manifestInfo = New-Object System.IO.FileInfo($manifestPath)
			$manifestInfo.IsReadOnly = $false
			[System.IO.File]::Delete($manifestPath)
		}
		if ($archiveMoved -and [System.IO.File]::Exists($archivePath) -and
			-not [System.IO.File]::Exists($activePath)) {
			$archiveInfo = New-Object System.IO.FileInfo($archivePath)
			$archiveInfo.IsReadOnly = $false
			[System.IO.File]::Move($archivePath, $activePath)
		}
		if ([System.IO.File]::Exists($tempManifest)) {
			[System.IO.File]::Delete($tempManifest)
		}
		throw
	}

	return [pscustomobject][ordered]@{
		status = "archived"
		archive_path = $archivePath
		manifest_path = $manifestPath
		sha256 = $sha256
		byte_count = $beforeLength
		line_count = [long]$summary.line_count
	}
}

function Invoke-ArchiveSelfTest {
	$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
	$tempRoot = Join-Path $tempBase ("metacompanion-training-archive-selftest-" + [guid]::NewGuid().ToString("N"))
	[System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
	try {
		$active = Join-Path $tempRoot "training-v2.jsonl"
		$encoding = New-Object System.Text.UTF8Encoding($false)
		[System.IO.File]::WriteAllText(
			$active,
			'{"kind":"solve"}' + [Environment]::NewLine +
			'{"kind":"action"}' + [Environment]::NewLine,
			$encoding)
		$result = Invoke-AdvisorTrainingLogArchive $tempRoot "training-v2.jsonl" "self-test" $true
		if ([System.IO.File]::Exists($active)) {
			throw "自检失败：活动日志仍然存在。"
		}
		if (-not [System.IO.File]::Exists($result.archive_path) -or
			-not [System.IO.File]::Exists($result.manifest_path)) {
			throw "自检失败：归档或清单没有生成。"
		}
		if ((Get-FileSha256 $result.archive_path) -ne $result.sha256) {
			throw "自检失败：归档哈希不一致。"
		}
		if (-not (New-Object System.IO.FileInfo($result.archive_path)).IsReadOnly) {
			throw "自检失败：归档没有设为只读。"
		}
		return [pscustomobject][ordered]@{
			schema = $script:ManifestSchema
			status = "pass"
			test_count = 5
		}
	} finally {
		$resolvedTemp = [System.IO.Path]::GetFullPath($tempRoot)
		$tempPrefix = $tempBase.TrimEnd([char[]]@([char]92, [char]47)) +
			[System.IO.Path]::DirectorySeparatorChar
		if ($resolvedTemp.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
			[System.IO.Path]::GetFileName($resolvedTemp).StartsWith(
				"metacompanion-training-archive-selftest-",
				[System.StringComparison]::Ordinal)) {
			foreach ($file in @(Get-ChildItem -LiteralPath $resolvedTemp -File -Recurse -ErrorAction SilentlyContinue)) {
				$file.IsReadOnly = $false
			}
			[System.IO.Directory]::Delete($resolvedTemp, $true)
		}
	}
}

if ($SelfTest) {
	Invoke-ArchiveSelfTest
} else {
	$result = Invoke-AdvisorTrainingLogArchive $DataDirectory $FileName $Reason ([bool]$SkipWorkerCheck)
	Write-Host ("旧训练日志已安全归档：" + $result.archive_path)
	Write-Host ("SHA256：" + $result.sha256 + "；行数：" + $result.line_count)
	$result
}
