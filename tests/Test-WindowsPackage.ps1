# Synthetic fixtures only; never publishes a release or changes the working repository.
$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot '../scripts/Assert-WindowsPackage.ps1'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('pixel-package-' + [guid]::NewGuid())
$repo = Join-Path $root 'source'
$package = Join-Path $root 'package'
$archive = Join-Path $root 'package.zip'
$license = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot '../LICENSE')).Replace("`r`n", "`n")
$script:passed = 0
function Test-Gate([string]$Name, [bool]$ShouldPass, [scriptblock]$Action) {
    $failure = $null
    try { & $Action } catch { $failure = $_ }
    if ($ShouldPass -and $failure) { throw "${Name}: $failure" }
    if (!$ShouldPass -and !$failure) { throw "${Name}: invalid package was accepted" }
    $script:passed++
    Write-Output "PASS $Name"
}
function Write-License([string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path $package 'LICENSE'), $Text)
}
function New-Zip {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
}
try {
    New-Item -ItemType Directory -Force $repo, $package | Out-Null
    & git -C $repo init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Fixture git init failed' }
    & git -C $repo -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet --allow-empty -m 'Before license'
    if ($LASTEXITCODE -ne 0) { throw 'Fixture commit failed' }
    $unlicensed = & git -C $repo rev-parse HEAD
    [System.IO.File]::WriteAllText((Join-Path $repo 'LICENSE'), $license)
    & git -C $repo add LICENSE
    & git -C $repo -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Add license'
    if ($LASTEXITCODE -ne 0) { throw 'Fixture license commit failed' }
    $source = & git -C $repo rev-parse HEAD
    [System.IO.File]::WriteAllText((Join-Path $package 'PixelTransfer.exe'), 'synthetic executable placeholder')
    [System.IO.File]::WriteAllText((Join-Path $package 'README.md'), 'synthetic instructions')
    $argsForGate = @{ PackagePath=$package; SourceCommit=$source; RepositoryPath=$repo }
    Test-Gate 'Missing LICENSE' $false { & $validator @argsForGate }
    Write-License ''
    Test-Gate 'Empty LICENSE' $false { & $validator @argsForGate }
    Write-License '   '
    Test-Gate 'Whitespace LICENSE' $false { & $validator @argsForGate }
    Write-License ($license.Replace('MIT License', 'Altered License'))
    Test-Gate 'Changed LICENSE' $false { & $validator @argsForGate }
    Write-License ($license.Replace("`n", "`r`n"))
    Test-Gate 'Windows line endings' $true { & $validator @argsForGate }
    Test-Gate 'Source predates LICENSE' $false { & $validator -PackagePath $package -SourceCommit $unlicensed -RepositoryPath $repo }
    New-Zip
    Test-Gate 'Valid ZIP' $true { & $validator @argsForGate -ZipPath $archive }
    Remove-Item -LiteralPath (Join-Path $package 'LICENSE')
    New-Zip
    Write-License $license
    Test-Gate 'ZIP missing LICENSE' $false { & $validator @argsForGate -ZipPath $archive }
    Write-License ''
    New-Zip
    Write-License $license
    Test-Gate 'ZIP empty LICENSE' $false { & $validator @argsForGate -ZipPath $archive }
    Write-License 'wrong license in archive'
    New-Zip
    Write-License $license
    Test-Gate 'ZIP changed LICENSE' $false { & $validator @argsForGate -ZipPath $archive }
    New-Zip
    $zip = [System.IO.Compression.ZipFile]::Open($archive, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $writer = [System.IO.StreamWriter]::new($zip.CreateEntry('LICENSE').Open())
        try { $writer.Write($license) } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    Test-Gate 'ZIP duplicate LICENSE' $false { & $validator @argsForGate -ZipPath $archive }
    Write-Output "$script:passed release package checks passed."
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
