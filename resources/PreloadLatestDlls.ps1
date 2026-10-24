param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$ManifestUrl,
    [string]$LogRoot = ""
)

$ErrorActionPreference = 'Stop'
$statusFile = Join-Path $RepoRoot 'preload.status'
if (-not $LogRoot) { $LogRoot = $RepoRoot }
New-Item -ItemType Directory -Path $LogRoot -Force | Out-Null
$logFile = Join-Path $LogRoot 'preload.log'
$manifestFile = Join-Path $RepoRoot 'manifest.json'
$downloadDir = Join-Path $RepoRoot 'downloads'

function Add-Status([string]$State, [string]$Message) {
    $safeMessage = $Message -replace '[\r\n|]', ' '
    $line = $State + '|' + $safeMessage
    Add-Content -LiteralPath $statusFile -Value $line -Encoding UTF8
    Add-Content -LiteralPath $logFile -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' | ' + $line) -Encoding UTF8
}

function Get-Md5([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm MD5).Hash.ToUpperInvariant()
}

function Get-LatestValidEntry($Entries) {
    return $Entries |
        Where-Object { $_.is_signature_valid -eq $true -and $_.is_dev_file -ne $true -and $_.version -and $_.download_url } |
        Sort-Object { [version]$_.version } -Descending |
        Select-Object -First 1
}

function Install-LatestManifestEntry([string]$Type, [string]$Category, [string]$DllName, $Entry) {
    $version = [string]$Entry.version
    $targetDir = Join-Path (Join-Path $RepoRoot $Type) $version
    $targetDll = Join-Path $targetDir $DllName
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null

    $expectedDllMd5 = ([string]$Entry.md5_hash).ToUpperInvariant()
    if ((Test-Path -LiteralPath $targetDll) -and $expectedDllMd5 -and (Get-Md5 $targetDll) -eq $expectedDllMd5) {
        Add-Status 'READY' "$Category $version bereits lokal und geprüft"
        return
    }

    $zipPath = Join-Path $downloadDir ($Type + '_' + $version + '.zip')
    $expectedZipMd5 = ([string]$Entry.zip_md5_hash).ToUpperInvariant()
    if ((Test-Path -LiteralPath $zipPath) -and $expectedZipMd5 -and (Get-Md5 $zipPath) -ne $expectedZipMd5) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    if (-not (Test-Path -LiteralPath $zipPath)) {
        Add-Status 'DOWNLOAD' "$Category $version wird heruntergeladen"
        Invoke-WebRequest -Uri ([string]$Entry.download_url) -OutFile $zipPath -UseBasicParsing
    }
    if ($expectedZipMd5 -and (Get-Md5 $zipPath) -ne $expectedZipMd5) {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
        throw "${Category} ${version}: ZIP-Prüfsumme stimmt nicht."
    }

    $extractDir = Join-Path $targetDir '_extract'
    if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force }
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
    try {
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force
        $candidate = Get-ChildItem -LiteralPath $extractDir -Filter $DllName -File -Recurse |
            Where-Object { -not $expectedDllMd5 -or (Get-Md5 $_.FullName) -eq $expectedDllMd5 } |
            Select-Object -First 1
        if (-not $candidate) { throw "${Category} ${version}: keine passende $DllName im Archiv gefunden." }
        Copy-Item -LiteralPath $candidate.FullName -Destination ($targetDll + '.new') -Force
        if ($expectedDllMd5 -and (Get-Md5 ($targetDll + '.new')) -ne $expectedDllMd5) {
            Remove-Item -LiteralPath ($targetDll + '.new') -Force -ErrorAction SilentlyContinue
            throw "${Category} ${version}: DLL-Prüfsumme stimmt nach dem Entpacken nicht."
        }
        Move-Item -LiteralPath ($targetDll + '.new') -Destination $targetDll -Force
        Add-Status 'READY' "$Category $version heruntergeladen und geprüft"
    }
    finally {
        if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    New-Item -ItemType Directory -Path $RepoRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
    Add-Status 'START' 'Prüfe öffentliches DLL-Manifest'
    $manifest = Invoke-RestMethod -Uri $ManifestUrl -TimeoutSec 45
    $manifestJson = $manifest | ConvertTo-Json -Depth 100
    $manifestTemp = $manifestFile + '.new'
    Set-Content -LiteralPath $manifestTemp -Value $manifestJson -Encoding UTF8
    Move-Item -LiteralPath $manifestTemp -Destination $manifestFile -Force
    Add-Status 'MANIFEST_READY' 'Manifest aktualisiert; Versionslisten werden geladen'

    $jobs = @(
        @{ Type = 'dlss'; Category = 'DLSS SR'; Dll = 'nvngx_dlss.dll'; Entries = $manifest.dlss },
        @{ Type = 'dlssg'; Category = 'FrameGen'; Dll = 'nvngx_dlssg.dll'; Entries = $manifest.dlss_g },
        @{ Type = 'dlssd'; Category = 'Ray Reconstruction'; Dll = 'nvngx_dlssd.dll'; Entries = $manifest.dlss_d }
    )
    $failures = @()
    foreach ($job in $jobs) {
        try {
            $latest = Get-LatestValidEntry $job.Entries
            if (-not $latest) { throw 'Manifest enthält keine passende signierte Version.' }
            Install-LatestManifestEntry $job.Type $job.Category $job.Dll $latest
        }
        catch {
            $failures += ($job.Category + ': ' + $_.Exception.Message)
            Add-Status 'ERROR' ($job.Category + ': ' + $_.Exception.Message)
        }
    }
    if ($failures.Count) { Add-Status 'DONE' 'Abgeschlossen; mindestens ein Download fehlgeschlagen (Details im Status/log).' }
    else { Add-Status 'DONE' 'Alle neuesten DLL-Versionen sind lokal bereit.' }
}
catch {
    Add-Status 'FAILED' $_.Exception.Message
    exit 1
}
