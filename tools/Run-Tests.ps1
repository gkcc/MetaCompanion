param(
	[string]$AssemblyPath = "$PSScriptRoot\..\MetaCompanionTests\bin\Release\MetaCompanionTests.dll",
	[switch]$KeepTestAppData,
	[switch]$SkipFreshnessCheck
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Desktop") {
	$windowsPowerShell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
	if (Test-Path -LiteralPath $windowsPowerShell) {
		$arguments = @(
			"-NoProfile",
			"-ExecutionPolicy",
			"Bypass",
			"-File",
			$MyInvocation.MyCommand.Path,
			"-AssemblyPath",
			$AssemblyPath
		)
		if ($KeepTestAppData) {
			$arguments += "-KeepTestAppData"
		}
		if ($SkipFreshnessCheck) {
			$arguments += "-SkipFreshnessCheck"
		}
		& $windowsPowerShell @arguments
		exit $LASTEXITCODE
	}
}

if (-not [Environment]::Is64BitProcess -and [Environment]::Is64BitOperatingSystem) {
	$nativePowerShell = Join-Path $env:WINDIR "sysnative\WindowsPowerShell\v1.0\powershell.exe"
	if (Test-Path -LiteralPath $nativePowerShell) {
		$arguments = @(
			"-NoProfile",
			"-ExecutionPolicy",
			"Bypass",
			"-File",
			$MyInvocation.MyCommand.Path,
			"-AssemblyPath",
			$AssemblyPath
		)
		if ($KeepTestAppData) {
			$arguments += "-KeepTestAppData"
		}
		if ($SkipFreshnessCheck) {
			$arguments += "-SkipFreshnessCheck"
		}
		& $nativePowerShell @arguments
		exit $LASTEXITCODE
	}
}

function Assert-TestAssemblyFresh {
	param(
		[string]$AssemblyPath,
		[string]$RepoRoot
	)

	$assembly = Get-Item -LiteralPath $AssemblyPath
	$sourceFiles = New-Object System.Collections.Generic.List[object]
	$sourceExtensions = @(".cs", ".xaml", ".csproj", ".png")
	$generatedDirectoryNames = @("bin", "obj")
	foreach ($sourceRootName in @("MetaCompanion", "MetaCompanionTests")) {
		$sourceRoot = Join-Path $RepoRoot $sourceRootName
		if (-not (Test-Path -LiteralPath $sourceRoot)) {
			continue
		}
		foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
			$relativePath = $sourceFile.FullName.Substring($sourceRoot.Length).TrimStart("\", "/")
			$relativeParts = $relativePath -split "[\\/]"
			if ($sourceExtensions -notcontains $sourceFile.Extension) {
				continue
			}
			if (@($relativeParts | Where-Object { $generatedDirectoryNames -contains $_ }).Count -gt 0) {
				continue
			}
			$sourceFiles.Add($sourceFile)
		}
	}
	if ($sourceFiles.Count -eq 0) {
		return
	}

	$newestSource = $sourceFiles |
		Sort-Object LastWriteTimeUtc -Descending |
		Select-Object -First 1
	if ($newestSource.LastWriteTimeUtc -le $assembly.LastWriteTimeUtc) {
		return
	}

	$relativeSource = $newestSource.FullName
	if ($relativeSource.StartsWith($RepoRoot, [StringComparison]::OrdinalIgnoreCase)) {
		$relativeSource = $relativeSource.Substring($RepoRoot.Length).TrimStart("\", "/")
	}

	throw "Test assembly is older than source file '$relativeSource'. Run tools\Build-MetaCompanion.ps1 before tools\Run-Tests.ps1, or pass -SkipFreshnessCheck to use the existing assembly."
}

function Resolve-TestAssemblyPath {
	param([string]$Path)

	if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		throw "Test assembly not found: $Path. Run tools\Build-MetaCompanion.ps1 before tools\Run-Tests.ps1."
	}
	return (Resolve-Path -LiteralPath $Path).Path
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$assemblyPath = Resolve-TestAssemblyPath $AssemblyPath
if (-not $SkipFreshnessCheck) {
	Assert-TestAssemblyFresh $assemblyPath $repoRoot
}

$originalAppData = $env:APPDATA
$originalLocalAppData = $env:LOCALAPPDATA
$realHdtConfigPath = if ($originalAppData) {
	Join-Path $originalAppData "HearthstoneDeckTracker\config.xml"
} else {
	$null
}
$realHdtConfigHash = if ($realHdtConfigPath -and (Test-Path -LiteralPath $realHdtConfigPath)) {
	(Get-FileHash -LiteralPath $realHdtConfigPath -Algorithm SHA256).Hash
} else {
	$null
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("MetaCompanionTests-" + [Guid]::NewGuid().ToString("N"))
$testAppData = Join-Path $testRoot "Roaming"
$testLocalAppData = Join-Path $testRoot "Local"
New-Item -ItemType Directory -Force -Path $testAppData, $testLocalAppData | Out-Null

$assemblyDirectory = Split-Path -Parent $assemblyPath
Set-Location $assemblyDirectory

function Set-HdtTestAppDataPath {
	param(
		[string]$AssemblyDirectory,
		[string]$AppDataPath
	)

	$hdtAssemblyPath = Join-Path $AssemblyDirectory "HearthstoneDeckTracker.exe"
	if (-not (Test-Path -LiteralPath $hdtAssemblyPath)) {
		throw "HearthstoneDeckTracker.exe not found next to test assembly: $hdtAssemblyPath"
	}

	$hdtAssembly = [Reflection.Assembly]::LoadFrom($hdtAssemblyPath)
	$configType = $hdtAssembly.GetType("Hearthstone_Deck_Tracker.Config", $true)
	$field = $configType.GetField(
		"AppDataPath",
		[Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static)
	if ($field -eq $null) {
		throw "HDT Config.AppDataPath field was not found."
	}

	# Force HDT's static constructor, then redirect the readonly path before tests touch Config.Load/Save.
	[Runtime.CompilerServices.RuntimeHelpers]::RunClassConstructor($configType.TypeHandle)
	$field.SetValue($null, (Join-Path $AppDataPath "HearthstoneDeckTracker"))
}

function Assert-RealHdtConfigUnchanged {
	if (-not $realHdtConfigPath -or -not $realHdtConfigHash) {
		return
	}
	if (-not (Test-Path -LiteralPath $realHdtConfigPath)) {
		throw "Real HDT config disappeared during tests: $realHdtConfigPath"
	}
	$currentHash = (Get-FileHash -LiteralPath $realHdtConfigPath -Algorithm SHA256).Hash
	if ($currentHash -ne $realHdtConfigHash) {
		throw "Real HDT config changed during tests: $realHdtConfigPath. Tests must run only against sandboxed AppData."
	}
}

function Get-TestFailureMessage {
	param([object]$ErrorValue)

	$exception = if ($ErrorValue -is [System.Management.Automation.ErrorRecord]) {
		$ErrorValue.Exception
	} elseif ($ErrorValue -is [Exception]) {
		$ErrorValue
	} else {
		$null
	}
	if ($exception) {
		while ($exception.InnerException) {
			$exception = $exception.InnerException
		}
		return $exception.Message
	}
	return [string]$ErrorValue
}

try {
	$env:APPDATA = $testAppData
	$env:LOCALAPPDATA = $testLocalAppData
	Set-HdtTestAppDataPath $assemblyDirectory $testAppData

	$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
	$testClassAttribute = "Microsoft.VisualStudio.TestTools.UnitTesting.TestClassAttribute"
	$testMethodAttribute = "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute"
	$initializeAttribute = "Microsoft.VisualStudio.TestTools.UnitTesting.TestInitializeAttribute"
	$cleanupAttribute = "Microsoft.VisualStudio.TestTools.UnitTesting.TestCleanupAttribute"

	$passed = 0
	$failed = 0
	foreach ($type in $assembly.GetTypes() | Where-Object {
		@($_.GetCustomAttributes($true) | ForEach-Object { $_.GetType().FullName }) -contains $testClassAttribute
	} | Sort-Object FullName) {
		$initializeMethods = @($type.GetMethods() | Where-Object {
			@($_.GetCustomAttributes($true) | ForEach-Object { $_.GetType().FullName }) -contains $initializeAttribute
		} | Sort-Object Name)
		$cleanupMethods = @($type.GetMethods() | Where-Object {
			@($_.GetCustomAttributes($true) | ForEach-Object { $_.GetType().FullName }) -contains $cleanupAttribute
		} | Sort-Object Name)
		$testMethods = @($type.GetMethods() | Where-Object {
			@($_.GetCustomAttributes($true) | ForEach-Object { $_.GetType().FullName }) -contains $testMethodAttribute
		} | Sort-Object Name)

		foreach ($method in $testMethods) {
			$name = "$($type.Name).$($method.Name)"
			$failureMessages = New-Object System.Collections.Generic.List[string]
			$instance = $null
			try {
				$instance = [Activator]::CreateInstance($type)
				foreach ($initialize in $initializeMethods) {
					$initialize.Invoke($instance, @()) | Out-Null
				}
				$method.Invoke($instance, @()) | Out-Null
			} catch {
				if ($instance -eq $null) {
					$failureMessages.Add("Constructor failed: " + (Get-TestFailureMessage $_))
				} else {
					$failureMessages.Add((Get-TestFailureMessage $_))
				}
			}
			if ($instance -ne $null) {
				foreach ($cleanup in $cleanupMethods) {
					try {
						$cleanup.Invoke($instance, @()) | Out-Null
					} catch {
						$failureMessages.Add("Cleanup failed: " + (Get-TestFailureMessage $_))
					}
				}
			}
			if ($failureMessages.Count -gt 0) {
				Write-Host "FAIL $name :: $([string]::Join('; ', $failureMessages))"
				$failed++
			} else {
				Write-Host "PASS $name"
				$passed++
			}
		}
	}

	Write-Host "RESULT passed=$passed failed=$failed"
	if ($failed -gt 0) {
		exit 1
	}
}
finally {
	$env:APPDATA = $originalAppData
	$env:LOCALAPPDATA = $originalLocalAppData
	if (-not $KeepTestAppData -and (Test-Path -LiteralPath $testRoot)) {
		Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
	}
	Assert-RealHdtConfigUnchanged
}
