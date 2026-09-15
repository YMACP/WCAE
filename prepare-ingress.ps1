param([Parameter(Mandatory=$true)][string]$BuildCache)
$ErrorActionPreference = 'Stop'
$cache = [IO.Path]::GetFullPath($BuildCache)
$sourcePrefix = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
if (($cache.TrimEnd('\')+'\').StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'BuildCache must be outside the source folder.' }
$tools = Join-Path $cache 'dependencies\tools'
$sdk = Join-Path $cache 'native-sdk'
$receipt = Join-Path $cache 'ingress-build-receipt.json'
$nativeFiles = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Native\ProxyBridge\src') -File | Sort-Object Name
$fingerprintText = (@($nativeFiles | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) + (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'build-ingress.ps1') -Algorithm SHA256).Hash) -join '|'
$algorithm = [Security.Cryptography.SHA256]::Create()
try { $fingerprint = ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprintText)))).Replace('-', '') }
finally { $algorithm.Dispose() }
$names = @('ProxyBridgeCore.dll','WinDivert.dll','WinDivert64.sys')
if (Test-Path -LiteralPath $receipt) {
    $previous = Get-Content -LiteralPath $receipt -Raw -Encoding UTF8 | ConvertFrom-Json
    $valid = $previous.SourceFingerprint -eq $fingerprint
    foreach ($name in $names) {
        $path = Join-Path $tools $name
        if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $previous.Files.$name) { $valid = $false }
    }
    if ($valid) { Write-Output 'Native ingress build is current.'; return }
}
New-Item -ItemType Directory -Path $sdk,$tools -Force | Out-Null
function Get-VerifiedArchive([string]$Url, [string]$Name, [string]$Sha256) {
    $archive = Join-Path $sdk $Name
    if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Sha256) {
        $pending = $archive + '.download'
        & curl.exe --fail --location --retry 2 --silent --show-error --output $pending $Url
        if ($LASTEXITCODE -ne 0) { throw ('Download failed: '+$Name) }
        if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $Sha256) { throw ('Checksum mismatch: '+$Name) }
        Move-Item -LiteralPath $pending -Destination $archive -Force
    }
    return $archive
}
$compilerArchive = Get-VerifiedArchive 'https://github.com/mstorsjo/llvm-mingw/releases/download/20260908/llvm-mingw-20260908-ucrt-x86_64.zip' 'llvm-mingw-20260908-ucrt-x86_64.zip' '1BCF74D06B724AEECAA6412CA85F5B26FB1DA770E7CDCEFA9263C9C5C3AD34B6'
$driverArchive = Get-VerifiedArchive 'https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip' 'WinDivert-2.2.2-A.zip' '63CB41763BB4B20F600B6DE04E991A9C2BE73279E317D4D82F237B150C5F3F15'
$compilerDirectory = Join-Path $sdk 'llvm-mingw-20260908-ucrt-x86_64'
$driverDirectory = Join-Path $sdk 'WinDivert-2.2.2-A'
if (!(Test-Path -LiteralPath (Join-Path $compilerDirectory 'bin\x86_64-w64-mingw32-clang.exe'))) {
    & tar.exe -xf $compilerArchive -C $sdk
    if ($LASTEXITCODE -ne 0) { throw 'Compiler extraction failed.' }
}
if (!(Test-Path -LiteralPath (Join-Path $driverDirectory 'x64\WinDivert.lib'))) {
    & tar.exe -xf $driverArchive -C $sdk
    if ($LASTEXITCODE -ne 0) { throw 'WinDivert SDK extraction failed.' }
}
& (Join-Path $PSScriptRoot 'build-ingress.ps1') -CompilerDirectory $compilerDirectory -WinDivertDirectory $driverDirectory -OutputDirectory $tools
$hashes = @{}
foreach ($name in $names) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $tools $name) -Algorithm SHA256).Hash }
$record = @{ SourceFingerprint=$fingerprint; Files=$hashes; Compiler='llvm-mingw-20260908-ucrt-x86_64'; ProxyBridgeCommit='02703a0672a8b94011a4698368a392f7734c10dc'; WinDivert='2.2.2-A' } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($receipt+'.tmp', $record, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath ($receipt+'.tmp') -Destination $receipt -Force
