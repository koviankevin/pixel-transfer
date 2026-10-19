# Read-only release gate. Run before creating tags or publishing assets.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [string]$RepositoryPath = (Join-Path $PSScriptRoot '..'),
    [string]$ZipPath
)
$ErrorActionPreference = 'Stop'
function Normalize-License([string]$Text) {
    # Git checkout can convert LF to CRLF on Windows. Keep all other text exact.
    return $Text.Replace("`r`n", "`n").TrimEnd([char[]]"`n")
}
$sourceLines = & git -C $RepositoryPath show "${SourceCommit}:LICENSE" 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Build source has no readable LICENSE; use a licensed build' }
$sourceLicense = Normalize-License ($sourceLines -join "`n")
if ([string]::IsNullOrWhiteSpace($sourceLicense)) { throw 'Build source LICENSE is empty' }
$required = @('PixelTransfer.exe', 'README.md', 'LICENSE')
foreach ($file in $required) {
    $path = Join-Path $PackagePath $file
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing or empty package file: $file"
    }
}
$packageLicense = Normalize-License ([System.IO.File]::ReadAllText((Join-Path $PackagePath 'LICENSE')))
if ($packageLicense -cne $sourceLicense) { throw 'Package LICENSE differs from build source' }
if ($ZipPath) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
    try {
        foreach ($file in $required) {
            $entries = @($zip.Entries | Where-Object { $_.FullName -ceq $file })
            if ($entries.Count -ne 1 -or $entries[0].Length -eq 0) {
                throw "Missing, empty or duplicate ZIP entry: $file"
            }
        }
        $reader = [System.IO.StreamReader]::new($zip.GetEntry('LICENSE').Open())
        try { $zipLicense = Normalize-License ($reader.ReadToEnd()) }
        finally { $reader.Dispose() }
        if ($zipLicense -cne $sourceLicense) { throw 'ZIP LICENSE differs from build source' }
    }
    finally { $zip.Dispose() }
}
