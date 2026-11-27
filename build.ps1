param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$local = Join-Path $env:LOCALAPPDATA 'DLSSManagerDev/dotnet/dotnet.exe'
$cli = if (Test-Path -LiteralPath $local) { $local } else { 'dotnet' }
Push-Location $PSScriptRoot
try {
    & $cli build DLSSManager.slnx -c $Configuration
    if ($LASTEXITCODE) { throw "Build failed ($LASTEXITCODE)." }
} finally { Pop-Location }
