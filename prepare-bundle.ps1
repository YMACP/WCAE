param(
    [Parameter(Mandatory=$true)][string]$DependencyRoot,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$toolsRoot = [IO.Path]::GetFullPath((Join-Path $DependencyRoot 'tools'))
$bundleRoot = [IO.Path]::GetFullPath($OutputDirectory)
$sourcePrefix = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
if (($bundleRoot.TrimEnd('\')+'\').StartsWith($sourcePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Generated bundles must remain outside the source directory.' }
New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null
function Write-AtomicUtf8([string]$Path, [string]$Text) {
    $temporary = $Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    [IO.File]::WriteAllText($temporary,$Text,(New-Object Text.UTF8Encoding($false)))
    if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary,$Path,[NullString]::Value) } else { [IO.File]::Move($temporary,$Path) }
}
$ingressFiles = @('ProxyBridgeCore.dll','WinDivert.dll','WinDivert64.sys')
$required = @('ffmpeg.exe','ffprobe.exe','yt-dlp.exe','wkhtmltopdf.exe','msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll') + $ingressFiles
foreach ($name in $required) { if (!(Test-Path -LiteralPath (Join-Path $toolsRoot $name))) { throw ('Missing packaged export component: '+$name) } }
$entries = @()
foreach ($file in (Get-ChildItem -LiteralPath $toolsRoot -File | Sort-Object Name)) {
    $group = if ($file.Name -in $ingressFiles) { 'ingress' } elseif ($file.Name -ieq 'wkhtmltopdf.exe') { 'pdf' } elseif ($file.Name -in @('ffmpeg.exe','ffprobe.exe','yt-dlp.exe')) { 'media' } else { 'runtime' }
    $entries += [ordered]@{ Name=$file.Name; Group=$group; Length=$file.Length; Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifestText = [ordered]@{ Format=1; Files=$entries } | ConvertTo-Json -Depth 5 -Compress
$sha = [Security.Cryptography.SHA256]::Create()
try { $bundleId = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifestText))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
foreach ($group in @('runtime','pdf','media','ingress')) {
    $members = @($entries | Where-Object { $_.Group -eq $group })
    $signature = $members | ConvertTo-Json -Depth 4 -Compress
    $archivePath = Join-Path $bundleRoot ($group+'.zip')
    $stampPath = $archivePath+'.inputs.json'
    if ([IO.File]::Exists($archivePath) -and [IO.File]::Exists($stampPath) -and [IO.File]::ReadAllText($stampPath) -ceq $signature) { continue }
    $archiveTemporary = $archivePath+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $archiveFile = [IO.File]::Create($archiveTemporary)
        $archive = New-Object IO.Compression.ZipArchive($archiveFile,[IO.Compression.ZipArchiveMode]::Create,$false)
        try {
            foreach ($member in $members) {
                $entry = $archive.CreateEntry($member.Name,[IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2020,1,1,0,0,0,[TimeSpan]::Zero)
                $inputStream = [IO.File]::OpenRead((Join-Path $toolsRoot $member.Name))
                $outputStream = $entry.Open()
                try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
            }
        } finally { $archive.Dispose(); $archiveFile.Dispose() }
        if ([IO.File]::Exists($archivePath)) { [IO.File]::Replace($archiveTemporary,$archivePath,[NullString]::Value) } else { [IO.File]::Move($archiveTemporary,$archivePath) }
        Write-AtomicUtf8 $stampPath $signature
    } finally { if ([IO.File]::Exists($archiveTemporary)) { [IO.File]::Delete($archiveTemporary) } }
}
$finalManifest = [ordered]@{ Format=1; BundleId=$bundleId; Files=$entries } | ConvertTo-Json -Depth 5 -Compress
$manifestPath = Join-Path $bundleRoot 'manifest.json'
if (![IO.File]::Exists($manifestPath) -or [IO.File]::ReadAllText($manifestPath) -cne $finalManifest) { Write-AtomicUtf8 $manifestPath $finalManifest }
Write-Output ('Export payload ready: '+$entries.Count+' files; PDF and media are extracted only when requested.')
