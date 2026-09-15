# WCAE process ingress

Upstream: https://github.com/InterceptSuite/ProxyBridge
Pinned commit: 02703a0672a8b94011a4698368a392f7734c10dc
License: MIT (included).

WCAE changes are confined to the native filter configuration and short interruptible
shutdown waits. The helper creates rules only for WeChat.exe, Weixin.exe and
WeChatAppEx.exe at HTTP/HTTPS and the detected upstream port. No global proxy settings
or third-party proxy client configurations are modified. The signed WinDivert 2.2.2-A
driver is distributed unmodified under its included license.

The managed gateway recognizes TLS ClientHello SNI and nested HTTP/SOCKS5 CONNECT.
Only mp.weixin.qq.com enters Titanium HTTPS decryption. Other TCP connections continue
to their original destination. TLS with encrypted or missing SNI and QUIC/UDP are not
decrypted by this module; diagnostics must not claim those requests were captured.

Build the native library using `build-ingress.ps1` from the repository root, supplying
the official LLVM-MinGW compiler directory and WinDivert SDK directory. Output must
remain outside the source directory. Runtime files are bundled on demand.
