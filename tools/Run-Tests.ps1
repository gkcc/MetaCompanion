param(
	[string]$AssemblyPath = "$PSScriptRoot\..\MetaCompanionTests\bin\x86\Release\MetaCompanionTests.dll",
	[switch]$KeepTestAppData
)

$ErrorActionPreference = "Stop"
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

$assemblyPath = (Resolve-Path $AssemblyPath).Path
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
	}) {
		$initializeMethods = @($type.GetMethods() | Where-Object {
			@($_.GetCustomAttributes($true) | ForEach-Object { $_.GetType().FullName }) -contains $initializeAttribute
		})
		$cleanupMethods = @($type.GetMethods() | Where-Object {
			@($_.GetCustomAttributes($true) | ForEach-Object { $_.GetType().FullName }) -contains $cleanupAttribute
		})
		$testMethods = @($type.GetMethods() | Where-Object {
			@($_.GetCustomAttributes($true) | ForEach-Object { $_.GetType().FullName }) -contains $testMethodAttribute
		})

		foreach ($method in $testMethods) {
			$instance = [Activator]::CreateInstance($type)
			$name = "$($type.Name).$($method.Name)"
			try {
				foreach ($initialize in $initializeMethods) {
					$initialize.Invoke($instance, @()) | Out-Null
				}
				$method.Invoke($instance, @()) | Out-Null
				Write-Host "PASS $name"
				$passed++
			} catch {
				$errorMessage = $_.Exception.Message
				if ($_.Exception.InnerException) {
					$errorMessage = $_.Exception.InnerException.Message
				}
				Write-Host "FAIL $name :: $errorMessage"
				$failed++
			} finally {
				foreach ($cleanup in $cleanupMethods) {
					$cleanup.Invoke($instance, @()) | Out-Null
				}
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
