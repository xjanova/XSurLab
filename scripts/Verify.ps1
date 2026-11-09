param([switch]$Devices)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
& dotnet run --project tests/XSurLab.SmokeTests -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$deviceArgs = @()
if ($Devices) { $deviceArgs = @('--', '--devices') }
& dotnet run --project tests/XSurLab.AudioChecks -c Release --no-build @deviceArgs
exit $LASTEXITCODE
