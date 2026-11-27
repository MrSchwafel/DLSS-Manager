param(
    [ValidatePattern('^[0-9][0-9A-Za-z.-]*$')][string]$Version,
    [string]$PackageSource = 'https://api.nuget.org/v3/index.json',
    [switch]$LocalOnly
)
$ErrorActionPreference = 'Stop'

$local = Join-Path $env:LOCALAPPDATA 'DLSSManagerDev/dotnet/dotnet.exe'
$cli = if (Test-Path -LiteralPath $local) { $local } else { 'dotnet' }
$appProject = Join-Path $PSScriptRoot 'src/DLSSManager.App/DLSSManager.App.csproj'
if (-not $PSBoundParameters.ContainsKey('Version')) {
    $project = [xml](Get-Content -LiteralPath $appProject -Raw)
    $Version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
}
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$packageName = "DLSS-Manager-CSharp-win-x64-$Version"
$output = Join-Path $artifacts 'win-x64'
$exe = Join-Path $output 'DLSSManager.App.exe'
$staging = Join-Path $artifacts ('.publish-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $staging 'package'
$stagedZip = Join-Path $staging ($packageName + '.zip')
$zip = Join-Path $artifacts ($packageName + '.zip')
$checksum = $zip + '.sha256'
if (-not $LocalOnly -and ((Test-Path -LiteralPath $zip) -or (Test-Path -LiteralPath $checksum))) {
    throw "A package for $Version already exists."
}

Push-Location $PSScriptRoot
try {
    $publishArgs = @('publish', $appProject, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '--source', $PackageSource, '-p:DebugType=None', "-p:Version=$Version", '-o', $publish)
    & $cli @publishArgs
    if ($LASTEXITCODE) { throw "Publish failed ($LASTEXITCODE)." }

    if (-not $LocalOnly) {
        @{
            format = 'dlss-manager-csharp-win-x64-v1'
            version = $Version
            executable = 'DLSSManager.App.exe'
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'dlss-manager-release.json') -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $publish
        Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $stagedZip
        $hash = (Get-FileHash -LiteralPath $stagedZip -Algorithm SHA256).Hash.ToLowerInvariant()
        $stagedChecksum = $stagedZip + '.sha256'
        "$hash  $packageName.zip" | Set-Content -LiteralPath $stagedChecksum -Encoding ascii
    }

    New-Item -ItemType Directory -Path (Join-Path $output 'IniDefaults') -Force | Out-Null
    Copy-Item -Path (Join-Path $publish 'IniDefaults/*.txt') -Destination (Join-Path $output 'IniDefaults')
    $newExe = Join-Path $publish 'DLSSManager.App.exe'
    try {
        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            [IO.File]::Move($newExe, $exe)
        } else {
            [IO.File]::Move($newExe, $exe, $true)
        }
    } catch {
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw }
        # Windows can keep a running image mapped after its file has been renamed.
        $previous = Join-Path $output ('.previous-' + [Guid]::NewGuid().ToString('N') + '.bin')
        [IO.File]::Move($exe, $previous)
        try {
            [IO.File]::Move($newExe, $exe)
        } catch {
            [IO.File]::Move($previous, $exe)
            throw
        }
    }
    Get-ChildItem -LiteralPath $output -File -Filter '.previous-*.bin' -Force |
        Where-Object { $_.Name -match '^\.previous-[a-f0-9]{32}\.bin$' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
    if ($LocalOnly) {
        Get-Item -LiteralPath $exe
    } else {
        [IO.File]::Move($stagedZip, $zip)
        [IO.File]::Move($stagedChecksum, $checksum)
        Get-Item -LiteralPath $exe, $zip, $checksum
    }
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = (Resolve-Path -LiteralPath $staging).ProviderPath
        $resolvedArtifacts = (Resolve-Path -LiteralPath $artifacts).ProviderPath
        if ((Split-Path -Parent $resolvedStaging) -eq $resolvedArtifacts -and
            (Split-Path -Leaf $resolvedStaging) -match '^\.publish-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
        }
    }
}
