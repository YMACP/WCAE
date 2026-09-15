using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WCAE
{
    public sealed partial class MainForm
    {
        // Called from the existing startup test's STA thread. The ordinary non-preview
        // window uses its isolated connection initializer, so only our loopback MCP
        // listener is allowed to run; no capture/WeChat/network setup is started.
        internal async Task<McpConfiguration> ValidateManualMcpLifecycleAsync(CancellationToken timeout)
        {
            RequireManualMcpFixture();
            if (mcpInitializationTask != null) await mcpInitializationTask.WaitAsync(timeout);
            CheckManualMcp(mcpConfiguration != null && mcpHost?.IsRunning != true, "初始化未加载配置或自动开启了 MCP");
            AssertManualMcpPortClosed(mcpConfiguration.Port);
            string initialToken = mcpConfiguration.Token;
            await InitializeMcpConfigurationAsync(timeout);
            CheckManualMcp(mcpConfiguration.Token == initialToken && mcpHost?.IsRunning != true, "重新加载配置改变了令牌或自动开启监听");

            var savedOff = mcpConfiguration.Copy();
            savedOff.Port = FindManualMcpPort();
            savedOff.Token = McpConfiguration.CreateToken();
            await ApplyMcpConfigurationAsync(savedOff, timeout);
            CheckManualMcp(mcpHost?.IsRunning != true, "保存关闭状态的设置自动开启了服务");
            AssertManualMcpPortClosed(savedOff.Port);
            CheckManualMcpStored(savedOff);

            // A rejected OFF-state save must not start a listener or replace disk settings.
            var invalid = savedOff.Copy(); invalid.Port = 0;
            bool rejected = false;
            try { await ApplyMcpConfigurationAsync(invalid, timeout); } catch (ArgumentException) { rejected = true; }
            CheckManualMcp(rejected && mcpHost?.IsRunning != true, "关闭状态的无效保存开启了监听或未被拒绝");
            CheckManualMcpStored(savedOff);

            // Cancel a real queued start before it acquires lifecycle ownership.
            await mcpHostGate.WaitAsync(timeout);
            try
            {
                using var canceledStart = CancellationTokenSource.CreateLinkedTokenSource(timeout);
                Task queued = SetMcpRunningAsync(true, canceledStart.Token);
                canceledStart.Cancel();
                bool canceled = false;
                try { await queued; } catch (OperationCanceledException) { canceled = true; }
                CheckManualMcp(canceled, "等待中的启动未响应取消");
            }
            finally { mcpHostGate.Release(); }
            CheckManualMcp(mcpHost?.IsRunning != true, "取消启动后遗留了服务");
            AssertManualMcpPortClosed(savedOff.Port);

            // Binding failure while OFF must stay OFF with the existing saved token.
            using (var occupied = new TcpListener(IPAddress.Loopback, savedOff.Port))
            {
                occupied.Server.ExclusiveAddressUse = true;
                occupied.Start();
                rejected = false;
                try { await SetMcpRunningAsync(true, timeout); }
                catch (IOException) { rejected = true; }
                catch (SocketException) { rejected = true; }
                CheckManualMcp(rejected && mcpHost?.IsRunning != true, "端口被占用后错误显示运行");
                CheckManualMcpStored(savedOff);
            }

            await SetMcpRunningAsync(true, timeout);
            CheckManualMcp(mcpHost?.IsRunning == true, "显式启动未开启服务");
            await CheckManualMcpHttpAsync(savedOff, timeout);

            // A real occupied-port reconfiguration must roll back both runtime and disk.
            using (var occupied = new TcpListener(IPAddress.Loopback, 0))
            {
                occupied.Server.ExclusiveAddressUse = true;
                occupied.Start();
                var bad = savedOff.Copy();
                bad.Port = ((IPEndPoint)occupied.LocalEndpoint).Port;
                bad.Token = McpConfiguration.CreateToken();
                rejected = false;
                try { await ApplyMcpConfigurationAsync(bad, timeout); }
                catch (IOException) { rejected = true; }
                catch (SocketException) { rejected = true; }
                CheckManualMcp(rejected && mcpHost?.IsRunning == true && mcpConfiguration.Port == savedOff.Port && mcpConfiguration.Token == savedOff.Token,
                    "运行中修改端口失败未恢复原服务和配置");
                CheckManualMcpStored(savedOff);
                await CheckManualMcpHttpAsync(savedOff, timeout);
            }

            var changedRunning = savedOff.Copy();
            changedRunning.Port = FindManualMcpPort();
            changedRunning.Token = McpConfiguration.CreateToken();
            await ApplyMcpConfigurationAsync(changedRunning, timeout);
            CheckManualMcp(mcpHost?.IsRunning == true, "运行中保存成功却关闭了服务");
            CheckManualMcpStored(changedRunning);
            AssertManualMcpPortClosed(savedOff.Port);
            await CheckManualMcpHttpAsync(changedRunning, timeout);

            await SetMcpRunningAsync(false, timeout);
            CheckManualMcp(mcpHost?.IsRunning != true && mcpHost?.HasConnectedClient != true, "显式停止后仍然运行或已连接");
            AssertManualMcpPortClosed(changedRunning.Port);
            await SetMcpRunningAsync(true, timeout);
            await CheckManualMcpHttpAsync(changedRunning, timeout);
            await SetMcpRunningAsync(false, timeout);
            AssertManualMcpPortClosed(changedRunning.Port);
            CheckManualMcpStored(changedRunning);
            return changedRunning.Copy();
        }

        internal async Task ValidateManualMcpReloadAsync(McpConfiguration expected, CancellationToken timeout)
        {
            RequireManualMcpFixture();
            if (mcpInitializationTask != null) await mcpInitializationTask.WaitAsync(timeout);
            CheckManualMcp(mcpHost?.IsRunning != true, "再次启动 WCAE 自动开启了 MCP");
            CheckManualMcp(mcpConfiguration?.Port == expected.Port && mcpConfiguration?.Token == expected.Token, "跨窗口初始化丢失了已保存的端口或令牌");
            AssertManualMcpPortClosed(expected.Port);
            await SetMcpRunningAsync(true, timeout);
            await CheckManualMcpHttpAsync(expected, timeout);
            // The caller closes the window with the MCP listener active, then verifies
            // shutdown actually released the endpoint.
        }

        void RequireManualMcpFixture()
        {
            if (preview || isolatedConnectionInitializer == null || !coreReady || InvokeRequired || !IsHandleCreated)
                throw new InvalidOperationException("手动 MCP 生命周期检查必须使用非预览隔离 UI。");
        }

        void CheckManualMcpStored(McpConfiguration expected)
        {
            var actual = new McpConfigurationStore(dataDirectory).LoadOrCreate();
            CheckManualMcp(actual.Port == expected.Port && actual.Token == expected.Token, "磁盘配置与预期运行配置不一致");
            CheckManualMcp(mcpConfiguration.Port == expected.Port && mcpConfiguration.Token == expected.Token, "内存配置与磁盘不一致");
        }

        async Task CheckManualMcpHttpAsync(McpConfiguration configuration, CancellationToken timeout)
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(configuration.Endpoint), TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(5),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + configuration.Token }
            }, http, ownsHttpClient: false);
            await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "WCAE-manual-lifecycle-test", Version = "1" },
                InitializationTimeout = TimeSpan.FromSeconds(5)
            }, cancellationToken: timeout);
            var result = await client.CallToolAsync("get_status", cancellationToken: timeout);
            CheckManualMcp(result.IsError != true && result.StructuredContent?.GetProperty("mcp").GetProperty("running").GetBoolean() == true,
                "真实 MCP HTTP 调用未返回运行状态");
            CheckManualMcp(result.StructuredContent?.GetProperty("mcp").GetProperty("endpoint").GetString() == configuration.Endpoint,
                "真实 MCP HTTP 返回的地址与已保存配置不一致");
        }

        internal static int FindManualMcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        internal static void AssertManualMcpPortClosed(int port)
        {
            // On Windows a refused loopback ConnectAsync can take over two seconds.
            // Binding exclusively checks port ownership directly without a SYN timeout.
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(); }
            catch (SocketException error) { throw new InvalidOperationException("关闭状态尚未释放 MCP 监听端口。", error); }
        }

        static void CheckManualMcp(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("MCP lifecycle: " + message); }
    }
}
