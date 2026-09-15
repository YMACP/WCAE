param(
    [Parameter(Mandatory=$true)][string]$CompilerDirectory,
    [Parameter(Mandatory=$true)][string]$WinDivertDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $CompilerDirectory 'bin\x86_64-w64-mingw32-clang.exe'
$nativeSource = Join-Path $PSScriptRoot 'Native\ProxyBridge\src'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.StartsWith([IO.Path]::GetFullPath($PSScriptRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native build output must stay outside the source folder.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$nativeFiles = @('ProxyBridge.c','pb_util.c','pb_process.c','pb_rules.c','pb_proxy.c','pb_dns.c','pb_socks5.c','pb_http.c','pb_conntrack.c','pb_relay.c') | ForEach-Object { Join-Path $nativeSource $_ }
$arguments = @('-O2','-Wall','-Wno-unused-parameter','-Wno-unused-variable','-Wno-unknown-pragmas','-D_WIN32_WINNT=0x0601','-DPROXYBRIDGE_EXPORTS','-shared','-static-libgcc',('-I'+(Join-Path $WinDivertDirectory 'include'))) + $nativeFiles + @((Join-Path $WinDivertDirectory 'x64\WinDivert.lib'),'-lws2_32','-liphlpapi','-lpsapi','-ldnsapi','-Wl,--dynamicbase,--nxcompat','-o',(Join-Path $output 'ProxyBridgeCore.dll'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw ('Native compiler failed: ' + $LASTEXITCODE) }
foreach ($name in @('WinDivert.dll','WinDivert64.sys')) {
    Copy-Item -LiteralPath (Join-Path $WinDivertDirectory ('x64\'+$name)) -Destination (Join-Path $output $name) -Force
}
Get-ChildItem -LiteralPath $output -File | Select-Object Name,Length
