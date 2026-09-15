# WCAE 第三方组件与来源

MCP 入口图标采用官方文档的 [favicon.svg](https://github.com/modelcontextprotocol/docs/blob/573dc60c2e7aab2605b29d0bf27194aa7b02e4fb/favicon.svg) 路径，以 Windows 向量绘图方式呈现。原作者为 Anthropic, PBC and contributors，使用 [MIT 许可原文](licenses/MCP-logo-LICENSE.txt)，本次仅调整显示尺寸与颜色。

WCAE 的应用依赖通过 NuGet PackageReference 恢复，版本由 [packages.lock.json](../packages.lock.json) 锁定。下表已与实际 project.assets.json 及已恢复包的 .nuspec 元数据核对；元数据摘录见 [licenses/NUGET_PACKAGES.json](licenses/NUGET_PACKAGES.json)。源码目录保留源码、构建输入和许可证文本；工具、SDK、恢复缓存及构建输出位于本机的 WCAE-build 构建目录。

各组件版权归其作者所有。许可证原文见 [licenses/](licenses/)，每份原文的上游或包内来源见 [licenses/SOURCES.json](licenses/SOURCES.json)。MCP 及 ASP.NET 运行时依赖的包内元数据、精确源码提交、许可声明和本地来源校验另见 [licenses/MCP_DEPENDENCIES.json](licenses/MCP_DEPENDENCIES.json)，保留原有来源记录。

## NuGet 应用依赖

| 包 | 锁定版本 | 来源与版权 | 许可证 |
| --- | --- | --- | --- |
| Newtonsoft.Json | 13.0.4 | [NuGet](https://www.nuget.org/packages/Newtonsoft.Json/13.0.4) / [源码](https://github.com/JamesNK/Newtonsoft.Json/tree/4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a)；James Newton-King | [MIT，取自恢复包](licenses/Newtonsoft.Json-13.0.4-LICENSE.txt) |
| HtmlAgilityPack | 1.12.4 | [NuGet](https://www.nuget.org/packages/HtmlAgilityPack/1.12.4) / [源码](https://github.com/zzzprojects/html-agility-pack)；Copyright © ZZZ Projects Inc. | [MIT](licenses/HtmlAgilityPack-LICENSE.txt) |
| Titanium.Web.Proxy | 3.1.1397 | [NuGet](https://www.nuget.org/packages/Titanium.Web.Proxy/3.1.1397) / [源码](https://github.com/justcoding121/Titanium-Web-Proxy)；Titanium Web Proxy GitHub Contributors | [MIT，包元数据指向此上游许可](licenses/Titanium.Web.Proxy-LICENSE.txt) |
| Portable.BouncyCastle | 1.9.0 | [NuGet](https://www.nuget.org/packages/Portable.BouncyCastle/1.9.0) / [包元数据中的源码提交](https://github.com/novotnyllc/bc-csharp/tree/0f827cc3e74a8f45669fba5c525adacd6b897503)；© 2000–2021 Legion of the Bouncy Castle Inc. | [Bouncy Castle License，MIT 形式](licenses/Portable.BouncyCastle-1.9.0-LICENSE.html) |
| ReverseMarkdown | 2.1.0 | [NuGet](https://www.nuget.org/packages/ReverseMarkdown/2.1.0) / [源码](https://github.com/mysticmind/reversemarkdown-net)；Babu Annamalai | [MIT](licenses/ReverseMarkdown-LICENSE.txt) |
| YamlDotNet | 16.3.0 | [NuGet](https://www.nuget.org/packages/YamlDotNet/16.3.0) / [包元数据中的源码提交](https://github.com/aaubry/YamlDotNet/tree/ae480660f4fb26f3eb0b41c1d1fcf21c0e9d9e73)；Antoine Aubry and contributors | [MIT 原文，取自该精确源码提交](licenses/YamlDotNet-16.3.0-LICENSE.txt)；恢复包声明 MIT，但未附许可正文。 |
| BrotliSharpLib（传递依赖） | 0.3.3 | [NuGet](https://www.nuget.org/packages/BrotliSharpLib/0.3.3) / [源码](https://github.com/master131/BrotliSharpLib)；Copyright 2017–2019 master131 | [MIT](licenses/BrotliSharpLib-LICENSE.txt) |
| Microsoft.Data.Sqlite | 10.0.12 | [NuGet](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12) / [官方源码](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701)；© Microsoft Corporation | [MIT](licenses/dotnet-source-10.0.12-LICENSE.txt) |
| Microsoft.Data.Sqlite.Core（传递依赖） | 10.0.12 | [NuGet](https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/10.0.12)；© Microsoft Corporation | [MIT](licenses/dotnet-source-10.0.12-LICENSE.txt) |
| SQLitePCLRaw.bundle_e_sqlite3（传递依赖） | 2.1.12 | [NuGet](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/2.1.12) / [源码](https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.12)；Copyright 2014–2024 SourceGear, LLC | [Apache-2.0](licenses/SQLitePCLRaw-2.1.12-LICENSE.txt) |
| SQLitePCLRaw.core（传递依赖） | 2.1.12 | [NuGet](https://www.nuget.org/packages/SQLitePCLRaw.core/2.1.12)；Copyright 2014–2024 SourceGear, LLC | [Apache-2.0](licenses/SQLitePCLRaw-2.1.12-LICENSE.txt) |
| SQLitePCLRaw.provider.e_sqlite3（传递依赖） | 2.1.12 | [NuGet](https://www.nuget.org/packages/SQLitePCLRaw.provider.e_sqlite3/2.1.12)；Copyright 2014–2024 SourceGear, LLC | [Apache-2.0](licenses/SQLitePCLRaw-2.1.12-LICENSE.txt) |
| SQLitePCLRaw.lib.e_sqlite3（原生库包） | 2.1.12 | [NuGet](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/2.1.12)；Copyright 2014–2024 SourceGear, LLC | 包元数据为 [Apache-2.0](licenses/SQLitePCLRaw-2.1.12-LICENSE.txt)；SQLite 引擎本身为 [Public Domain](licenses/SQLite-copyright.html) |
| ModelContextProtocol.AspNetCore | 2.2.0 | [NuGet](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore/2.2.0) / [包标注源码提交](https://github.com/modelcontextprotocol/csharp-sdk/tree/6fa3825973949a9c4f0cd8af344e15a8db09dc35)；© Model Context Protocol a Series of LF Projects, LLC. | 包声明 Apache-2.0；复用已随附的 [Apache-2.0 完整正文](licenses/SQLitePCLRaw-2.1.12-LICENSE.txt)。 |
| ModelContextProtocol（传递依赖） | 2.2.0 | [NuGet](https://www.nuget.org/packages/ModelContextProtocol/2.2.0)；© Model Context Protocol a Series of LF Projects, LLC.；与上项为同一源码提交 | [Apache-2.0](licenses/SQLitePCLRaw-2.1.12-LICENSE.txt) |
| ModelContextProtocol.Core（传递依赖） | 2.2.0 | [NuGet](https://www.nuget.org/packages/ModelContextProtocol.Core/2.2.0)；© Model Context Protocol a Series of LF Projects, LLC.；与上项为同一源码提交 | [Apache-2.0](licenses/SQLitePCLRaw-2.1.12-LICENSE.txt) |
| Microsoft.Extensions.AI.Abstractions（传递依赖） | 10.8.3 | [NuGet](https://www.nuget.org/packages/Microsoft.Extensions.AI.Abstractions/10.8.3) / [包标注源码提交](https://github.com/dotnet/extensions/tree/ccb356f31db9d894807c4fd0c97c2f41553d1524)；© Microsoft Corporation. All rights reserved. | 包声明 MIT；复用已随附的 [MIT 完整正文](licenses/dotnet-source-10.0.12-LICENSE.txt)，本行保留该包的版权声明。 |

BrotliSharpLib 上游说明其移植自 Google Brotli v0.6.0；[Brotli Authors 的 MIT 许可](licenses/Google-Brotli-0.6.0-LICENSE.txt)一并保留。

YamlDotNet 用于解析和生成临时 Clash 运行配置。Clash Verge / Mihomo 是用户已安装的外部服务，通过本机控制器通信；本 WCAE 发布包不包含这两个程序，其许可不由上述 YamlDotNet MIT 声明替代。

MCP 三个包使用官方 C# SDK，通过 ASP.NET Core 提供本机 Streamable HTTP 服务。上述三个 2.2.0 包和 Microsoft.Extensions.AI.Abstractions 10.8.3 的本地恢复包仅带许可证表达式，未附独立 LICENSE/NOTICE 文件；其实际声明、包版权及包文件校验保存在新增来源记录中。引用现有 Apache/MIT 标准许可正文不表示这些包由 SQLitePCLRaw 或其他被引用组件的作者拥有。

## 独立微信流量接入组件

| 组件 | 精确来源 | 许可证与构建 |
| --- | --- | --- |
| ProxyBridgeCore | [InterceptSuite/ProxyBridge，提交 02703a0672a8b94011a4698368a392f7734c10dc](https://github.com/InterceptSuite/ProxyBridge/tree/02703a0672a8b94011a4698368a392f7734c10dc)；Copyright (c) 2025 Anof-cyber/InterceptSuite | [MIT 原文](../Native/ProxyBridge/LICENSE)。使用源码目录 [Native/ProxyBridge](../Native/ProxyBridge) 编译；WCAE 添加端口过滤、动态中继端口与快速退出等待，并兼容 LLVM-MinGW。 |
| WinDivert.dll / WinDivert64.sys | [basil00/WinDivert 2.2.2-A 官方二进制](https://github.com/basil00/WinDivert/releases/tag/v2.2.2) / [对应源码](https://github.com/basil00/WinDivert/tree/v2.2.2) | [上游双许可原文](licenses/WinDivert-2.2.2-LICENSE.txt)，WCAE 采用 LGPLv3 分发。驱动与 DLL 均保持官方字节不变；驱动包含有效 Authenticode 签名。 |

独立接入组件按需释放到用户组件目录后动态加载；安装包不包含新的永久系统代理或 Clash 配置。进程退出关闭 WinDivert 句柄。`prepare-ingress.ps1` 从官方来源获取固定 SHA-256 的 LLVM-MinGW 与 WinDivert SDK，`build-ingress.ps1` 从随附源码构建原生库。编译器只供开发使用，不随 EXE 分发。原生库依赖 Windows UCRT 和 Windows 系统 DLL，不依赖另装 LLVM 或 Visual Studio。

## 内置 .NET 运行环境

WCAE 面向 net10.0-windows / win-x64，以自包含单文件方式发布。应用内置下列运行时包，无需依靠机器上另装的 .NET 运行环境。版本和许可来自实际恢复的包元数据。

| 组件 | 版本与来源 | 本地许可与声明 |
| --- | --- | --- |
| Microsoft.NETCore.App.Runtime.win-x64 | [10.0.12 NuGet 包](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.12) / [包标注源码提交](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701) | [MIT 原文](licenses/dotnet-runtime-10.0.12-LICENSE.txt)；[包内第三方声明](licenses/dotnet-runtime-10.0.12-THIRD-PARTY-NOTICES.txt) |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 | [10.0.12 NuGet 包](https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.12)；用于 Windows Forms 桌面运行环境 | [包内 MIT 原文](licenses/dotnet-windowsdesktop-10.0.12-LICENSE.txt)；固定 10.0.12 源码中的 [Windows Forms 声明](licenses/dotnet-winforms-10.0.12-THIRD-PARTY-NOTICES.txt)与 [WPF 声明](licenses/dotnet-wpf-10.0.12-THIRD-PARTY-NOTICES.txt) |
| Microsoft.AspNetCore.App.Runtime.win-x64 | [10.0.12 NuGet 包](https://www.nuget.org/packages/Microsoft.AspNetCore.App.Runtime.win-x64/10.0.12) / [包标注源码提交](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701)；© Microsoft Corporation. All rights reserved.；提供 MCP HTTP 运行环境 | [包内 MIT 原文](licenses/dotnet-runtime-10.0.12-LICENSE.txt)及 [包内第三方声明](licenses/dotnet-aspnetcore-10.0.12-THIRD-PARTY-NOTICES.txt)。MIT 原文与 .NET 运行时许可逐字节相同，合并保留一份；第三方声明仍按原字节保留，独立来源记录不变。 |

构建使用 Microsoft .NET SDK 10.0.401，SDK 为构建工具，不包含在 WCAE 的应用运行环境中。本机 .NET 构建工具目录附带的 [Microsoft .NET Library 许可条款](licenses/dotnet-sdk-10.0.401-LICENSE.txt)与 [第三方声明](licenses/dotnet-sdk-10.0.401-THIRD-PARTY-NOTICES.txt)另行保留；该目录由 [Microsoft .NET 10 官方下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)构建，不能用其根目录条款替代上表各运行时 NuGet 包自带的许可。SDK 和运行时下载已由构建流程按微软发布的 SHA-512 值校验。

锁定文件中自动引用的 [Microsoft.NET.ILLink.Tasks 10.0.12](https://www.nuget.org/packages/Microsoft.NET.ILLink.Tasks/10.0.12)属于构建任务，包元数据为 [MIT](licenses/dotnet-source-10.0.12-LICENSE.txt)，不作为应用业务库分发。

## 独立媒体与 PDF 工具

媒体工具作为构建输入缓存，并随单文件发布打包，运行时作为独立进程调用。固定发行地址与校验记录保留在 [licenses/MEDIA_TOOL_UPSTREAM.json](licenses/MEDIA_TOOL_UPSTREAM.json)。

| 工具 | 版本与来源 | 许可证与源码 |
| --- | --- | --- |
| yt-dlp.exe | 2026.08.19；[官方固定发行](https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19)，已按发行 SHA-256 校验 | 源码为 [Unlicense](licenses/yt-dlp-2026.08.19-LICENSE.txt)；Windows 打包程序整体为 GPLv3+，见该版本的 [Licensing 说明](https://github.com/yt-dlp/yt-dlp/blob/2026.08.19/README.md#licensing)。附 [完整第三方许可](licenses/yt-dlp-2026.08.19-THIRD_PARTY_LICENSES.txt)和 [GPL v3 原文](licenses/GPL-3.0.txt)。 |
| ffmpeg.exe、ffprobe.exe | Gyan 9.0.1 essentials 同一发行包；本地版本查询已核实。[固定发行](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.1) / [构建项目](https://www.gyan.dev/ffmpeg/builds/) | 当前构建启用 --enable-gpl 和 --enable-version3，ffmpeg -L 明确为 GPL v3 或以后版本。附 [GPL v3 原文](licenses/GPL-3.0.txt)；[官方源码入口](https://ffmpeg.org/download.html)与 [许可说明](https://ffmpeg.org/legal.html)。 |
| wkhtmltopdf.exe | 0.12.6 (with patched qt)，从此前已有工具二进制保留，已运行 --version 核实 | [0.12.6 上游源码](https://github.com/wkhtmltopdf/wkhtmltopdf/tree/0.12.6)的 [LGPL v3 原文](licenses/wkhtmltopdf-0.12.6-LICENSE.txt)；[打包工程](https://github.com/wkhtmltopdf/packaging)。该已有二进制精确原始发行包及 Qt/WebKit 构建的完整对应声明尚待核实。 |
| Microsoft Visual C++ v14 Runtime | msvcp140.dll、vcruntime140.dll、vcruntime140_1.dll；文件版本均 14.50.35719.0。来自本机 Windows System32，均已核实 Microsoft 有效签名，供 wkhtmltopdf 本地加载；[来源与校验记录](licenses/VC_RUNTIME_SOURCES.json) | 微软专有软件条款，见 [Microsoft Visual C++ V14 Redistributable and Runtime 2026 原文](https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/)；[本地部署说明](https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method?view=msvc-170)及 [再分发文件说明](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution#visual-c-runtime-files)。这些 DLL 不属于上述 MIT/Apache 开源许可。 |

运行时和媒体工具可能带有不同许可证的第三方组件，应保留随附声明。上述来源链接用于组件与许可追溯，不代替对应源码本身；第三方许可也不由 WCAE 自身的版权声明替代。
