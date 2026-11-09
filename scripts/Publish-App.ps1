param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
$output = "artifacts/XSurLab-$Runtime"
# One compressed, self-contained XSurLab.exe: the target PC needs no .NET install.
& dotnet publish src/XSurLab.App/XSurLab.App.csproj -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Get-Item (Join-Path $output 'XSurLab.exe')
'{0}  {1:N1} MB  SHA-256 {2}' -f $exe.FullName, ($exe.Length / 1MB), (Get-FileHash -Algorithm SHA256 $exe.FullName).Hash
