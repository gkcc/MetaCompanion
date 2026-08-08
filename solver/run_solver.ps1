param(
    [string]$SessionToken = $env:METACOMPANION_SOLVER_TOKEN,
    [string]$Config = (Join-Path $PSScriptRoot "config.default.json")
)

$arguments = @((Join-Path $PSScriptRoot "launch_solver.py"), "serve", "--config", $Config)
if ($SessionToken) {
    $arguments += @("--session-token", $SessionToken)
}

& python @arguments
exit $LASTEXITCODE
