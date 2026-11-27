[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z]{2,3}$')]
    [string]$Language,

    [ValidateSet('en', 'de')]
    [string]$SourceLanguage = 'en'
)

$ErrorActionPreference = 'Stop'
$code = $Language.ToLowerInvariant()
$sourceCode = $SourceLanguage.ToLowerInvariant()
$repo = Join-Path $PSScriptRoot '../..'
$catalogs = Join-Path $repo 'src/DLSSManager.Core/Localization'
$source = Join-Path $catalogs "Strings.$sourceCode.json"
$target = Join-Path $catalogs "Strings.$code.json"

if (Test-Path -LiteralPath $target) {
    throw "The language catalog already exists: $target"
}

[IO.File]::Copy($source, $target, $false)
Get-Item -LiteralPath $target
