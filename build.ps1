param(
    [switch]$Test,
    [string]$Output = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'WCAE.exe'),
    [string]$BuildCache = (Join-Path $env:LOCALAPPDATA 'WCAE-build')
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$BuildCache = [IO.Path]::GetFullPath($BuildCache)
$sourcePrefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
if (($BuildCache.TrimEnd('\')+'\').StartsWith($sourcePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'BuildCache must be outside the source directory.' }
$Output = [IO.Path]::GetFullPath($Output)
if ($Output.StartsWith($sourcePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be outside the source directory.' }
New-Item -ItemType Directory -Path $BuildCache -Force | Out-Null
$sdkMetadata = Get-Content -LiteralPath (Join-Path $projectRoot 'sdk-download.json') -Encoding UTF8 | ConvertFrom-Json
$sdkDirectory = Join-Path $BuildCache 'dotnet'
$dotnet = Join-Path $sdkDirectory 'dotnet.exe'
if (!(Test-Path -LiteralPath (Join-Path $sdkDirectory ('sdk\'+$sdkMetadata.SdkVersion+'\dotnet.dll')))) {
    $sdkArchive = Join-Path $BuildCache ('dotnet-sdk-'+$sdkMetadata.SdkVersion+'-win-x64.zip')
    if (!(Test-Path -LiteralPath $sdkArchive) -or (Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA512).Hash -ne $sdkMetadata.Sha512) {
        & curl.exe --fail --location --retry 2 --silent --show-error --output ($sdkArchive+'.download') $sdkMetadata.Url
        if ($LASTEXITCODE -ne 0) { throw 'SDK download failed.' }
        if ((Get-FileHash -LiteralPath ($sdkArchive+'.download') -Algorithm SHA512).Hash -ne $sdkMetadata.Sha512) { throw 'SDK checksum mismatch.' }
        Move-Item -LiteralPath ($sdkArchive+'.download') -Destination $sdkArchive -Force
    }
    New-Item -ItemType Directory -Path $sdkDirectory -Force | Out-Null
    & tar.exe -xf $sdkArchive -C $sdkDirectory
    if ($LASTEXITCODE -ne 0) { throw 'SDK extraction failed.' }
}
$toolsDirectory = Join-Path $BuildCache 'dependencies\tools'
& (Join-Path $projectRoot 'prepare-ingress.ps1') -BuildCache $BuildCache
foreach ($tool in @('wkhtmltopdf.exe','yt-dlp.exe','ffmpeg.exe','ffprobe.exe','MSVCP140.dll','VCRUNTIME140.dll','VCRUNTIME140_1.dll','ProxyBridgeCore.dll','WinDivert.dll','WinDivert64.sys')) {
    if (!(Test-Path -LiteralPath (Join-Path $toolsDirectory $tool))) { throw ('Missing build input '+$tool+'. See README.md / prepare-tools.py. End users only need the published WCAE.exe.') }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$publishDirectory = Join-Path $BuildCache ('publish\'+[Guid]::NewGuid().ToString('N'))
& $dotnet publish (Join-Path $projectRoot 'WCAE.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $publishDirectory "-p:WcaeBuildRoot=$BuildCache" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$files = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
if ($files.Count -ne 1 -or $files[0].Name -ne 'WCAE.exe') { throw 'Publish must produce exactly one WCAE.exe.' }
$publishedExe = $files[0].FullName
if ($Test) {
    $testReport = Join-Path $BuildCache 'release-tests.txt'
    $testProcess = Start-Process -FilePath $publishedExe -ArgumentList ('--self-test "'+$testReport+'"') -WindowStyle Hidden -PassThru
    if (!$testProcess.WaitForExit(180000)) { Stop-Process -Id $testProcess.Id; throw 'Self tests timed out.' }
    Get-Content -LiteralPath $testReport -Encoding UTF8
    if ($testProcess.ExitCode -ne 0) { throw 'Self tests failed.' }
}
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Output)) -Force | Out-Null
$outputTemporary = $Output+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
Copy-Item -LiteralPath $publishedExe -Destination $outputTemporary
if (Test-Path -LiteralPath $Output) { [IO.File]::Replace($outputTemporary,$Output,[NullString]::Value) } else { [IO.File]::Move($outputTemporary,$Output) }
Write-Output ('Built: '+$Output)
