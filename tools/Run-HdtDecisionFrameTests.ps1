param(
	[Parameter(Mandatory = $true)]
	[string]$PythonPath,
	[Parameter(Mandatory = $true)]
	[string]$TestPath
)

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $PythonPath
$startInfo.Arguments = '"' + ($TestPath -replace '"', '\"') + '" -q'
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo
if (-not $process.Start()) {
	[Console]::Out.WriteLine("HDT decision-frame contract test process could not start.")
	exit 2
}
$standardOutput = $process.StandardOutput.ReadToEnd()
$standardError = $process.StandardError.ReadToEnd()
$process.WaitForExit()
$exitCode = $process.ExitCode
if (-not [string]::IsNullOrWhiteSpace($standardOutput)) {
	[Console]::Out.WriteLine($standardOutput.TrimEnd())
}
if (-not [string]::IsNullOrWhiteSpace($standardError)) {
	[Console]::Out.WriteLine($standardError.TrimEnd())
}

if ($exitCode -ne 0) {
	[Console]::Out.WriteLine("HDT decision-frame contract tests failed (exit code: $exitCode).")
	exit $exitCode
}

[Console]::Out.WriteLine("HDT decision-frame contract tests passed.")
exit 0
