# Public synthetic fixture only. No source file, credentials or desktop access.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [ValidateRange(1,64)][int]$SizeMiB = 8
)
$ErrorActionPreference = 'Stop'
# Specify a small deterministic PRNG explicitly so Windows PowerShell and pwsh agree.
[uint32]$state = 20261014
$stream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $buffer = New-Object byte[] 65536
    for ($block = 0; $block -lt $SizeMiB * 16; $block++) {
        for ($i = 0; $i -lt $buffer.Length; $i++) {
            $state = [uint32](([uint64]1664525 * $state + 1013904223) -band 0xffffffffL)
            $buffer[$i] = [byte]($state -shr 24)
        }
        $stream.Write($buffer, 0, $buffer.Length)
    }
}
finally { $stream.Dispose() }
Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256 | Select-Object Algorithm, Hash
