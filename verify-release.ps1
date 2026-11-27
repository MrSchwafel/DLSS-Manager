param([Parameter(Mandatory)][string]$Package)
$ErrorActionPreference = 'Stop'
$zip = Get-Item -LiteralPath $Package
$expected = (Get-Content -LiteralPath ($zip.FullName + '.sha256') -Raw).Trim()
$hash = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -cne "$hash  $($zip.Name)") { throw 'ZIP checksum or filename mismatch.' }

$archive = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)
try {
    $required = @('DLSSManager.App.exe', 'dlss-manager-release.json', 'README.md',
        'IniDefaults/SOURCES.txt', 'IniDefaults/OptiScaler-LICENSE.txt')
    $entries = @($archive.Entries.FullName | ForEach-Object { $_.Replace([char]92, [char]47) })
    if ($entries.Count -ne $required.Count -or @($required | Where-Object { $_ -cnotin $entries }).Count) {
        throw 'Unexpected package contents.'
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('dlss-manager-release.json').Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.format -cne 'dlss-manager-csharp-win-x64-v1' -or $manifest.executable -cne 'DLSSManager.App.exe' -or
        $zip.Name -cne "DLSS-Manager-CSharp-win-x64-$($manifest.version).zip") { throw 'Invalid package manifest.' }

    $buffer = [IO.MemoryStream]::new()
    try {
        $exe = $archive.GetEntry($manifest.executable).Open()
        try { $exe.CopyTo($buffer) } finally { $exe.Dispose() }
        $reader = [IO.BinaryReader]::new($buffer)
        try {
            $buffer.Position = 0x3c
            $buffer.Position = $reader.ReadInt32()
            if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
                throw 'Invalid Windows x64 executable.'
            }
        } finally { $reader.Dispose() }
    } finally { $buffer.Dispose() }
} finally { $archive.Dispose() }
"Verified x64 package $($manifest.version) | SHA-256 $hash"
