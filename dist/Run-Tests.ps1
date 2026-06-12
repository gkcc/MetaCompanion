param(
	[string]$AssemblyPath = "$PSScriptRoot\..\MetaCompanionTests\bin\x86\Release\MetaCompanionTests.dll"
)

$ErrorActionPreference = "Stop"
$assemblyPath = (Resolve-Path $AssemblyPath).Path
$assemblyDirectory = Split-Path -Parent $assemblyPath
Set-Location $assemblyDirectory

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
