using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WCAE
{
    internal static class McpHostSelfTests
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception("MCP: " + message); }

        public static async Task RunAsync()
        {
            string temporary = Path.Combine(Path.GetTempPath(), "WCAE-mcp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                var store = new McpConfigurationStore(temporary);
                var saved = store.LoadOrCreate();
                Check(saved.Port == 8878 && saved.Token.Length == 64, "首次配置默认端口或随机令牌错误");
                string persisted = File.ReadAllText(Path.Combine(temporary, "mcp-settings.json"));
                Check(!persisted.Contains(saved.Token), "令牌被明文保存");
                var reloaded = new McpConfigurationStore(temporary).LoadOrCreate();
                Check(reloaded.Token == saved.Token && reloaded.Port == saved.Port, "配置没有跨实例保留");
                reloaded.Port = 18878;
                store.Save(reloaded);
                Check(store.LoadOrCreate().Port == 18878 && store.LoadOrCreate().Token == saved.Token, "修改端口导致令牌改变");
                using (var copied = JsonDocument.Parse(reloaded.CopyConfigurationJson()))
                {
                    var target = copied.RootElement.GetProperty("mcpServers").GetProperty("WCAE");
                    Check(target.GetProperty("url").GetString() == "http://127.0.0.1:18878/mcp", "复制配置地址错误");
                    Check(target.GetProperty("headers").GetProperty("Authorization").GetString() == "Bearer " + saved.Token, "复制配置缺少认证");
                }
                bool invalidRejected = false;
                try { store.Save(new McpConfiguration { Port = 0 }); } catch (ArgumentOutOfRangeException) { invalidRejected = true; }
                Check(invalidRejected, "用户配置接受了不稳定端口零");
                File.WriteAllText(Path.Combine(temporary, "mcp-settings.json"), "{}", new UTF8Encoding(false));
                invalidRejected = false;
                try { store.LoadOrCreate(); } catch (InvalidDataException) { invalidRejected = true; }
                Check(invalidRejected && File.ReadAllText(Path.Combine(temporary, "mcp-settings.json")) == "{}", "损坏配置被悄悄重置");

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var config = new McpConfiguration { Port = 0 };
                var fake = new FakeApplication();
                await using var host = new McpHost(fake, config, true);
                var events = new List<string>();
                host.StatusChanged += message => events.Add(message);
                await host.StartAsync(timeout.Token);
                Check(host.IsRunning && host.Endpoint != null && host.LastCallUtc == null && !host.HasConnectedClient, "服务未启动或启动触发业务调用/虚假连接");
                using (var socket = new TcpClient())
                {
                    await socket.ConnectAsync(IPAddress.Loopback, new Uri(host.Endpoint).Port, timeout.Token);
                    Check(!host.HasConnectedClient, "未认证 TCP 连接被当作 Agent 已连接");
                }
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                Check(await RequestStatus(http, host.Endpoint, null, null, null) == HttpStatusCode.Unauthorized, "缺少令牌仍可访问");
                Check(await RequestStatus(http, host.Endpoint, new string('0', 64), null, null) == HttpStatusCode.Unauthorized, "错误令牌仍可访问");
                Check(await RequestStatus(http, host.Endpoint, config.Token, "https://untrusted.example", null) == HttpStatusCode.Forbidden, "外部 Origin 未拦截");
                Check(await RequestStatus(http, host.Endpoint, config.Token, "null", null) == HttpStatusCode.Forbidden, "null Origin 未拦截");
                Check(await RequestStatus(http, host.Endpoint, config.Token, null, "untrusted.example") == HttpStatusCode.Forbidden, "外部 Host 未拦截");
                Check(await RequestStatus(http, host.Endpoint, config.Token, "http://127.0.0.1:1", null) == HttpStatusCode.Forbidden, "跨端口 Origin 未拦截");
                string origin = new Uri(host.Endpoint).GetLeftPart(UriPartial.Authority);
                HttpStatusCode originStatus = await RequestStatus(http, host.Endpoint, config.Token, origin, null);
                Check(originStatus != HttpStatusCode.Forbidden && originStatus != HttpStatusCode.Unauthorized, "自身 Origin 被错误拒绝");
                Check(fake.LastTool == null && host.LastCallUtc == null && !host.HasConnectedClient, "被拒请求或 GET 请求触发业务调用/已连接状态");
                Check(await DiscoveryStatus(http, host.Endpoint, new string('0', 64), timeout.Token) == HttpStatusCode.Unauthorized && !host.HasConnectedClient,
                    "错误认证 MCP POST 被当作已连接");

                await CheckLiveConnections(host, config, timeout.Token);
                Check(host.LastCallUtc == null, "MCP 发现连接状态依赖业务工具调用");

                foreach (string version in new[] { "2026-07-28", "2025-11-25", "2025-06-18" })
                {
                    using (var clientHttp = NewHttpClient())
                    {
                    var transport = new HttpClientTransport(new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(host.Endpoint),
                        TransportMode = HttpTransportMode.StreamableHttp,
                        ConnectionTimeout = TimeSpan.FromSeconds(5),
                        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + config.Token }
                    }, clientHttp, ownsHttpClient: false);
                    await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
                    {
                        ClientInfo = new Implementation { Name = "WCAE-self-test", Version = "1" },
                        ProtocolVersion = version,
                        InitializationTimeout = TimeSpan.FromSeconds(5)
                    }, cancellationToken: timeout.Token);
                    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
                    Check(tools.Count == 13, "工具发现数量错误，协议 " + version);
                    await AwaitConnectionState(host, true, timeout.Token);
                    var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (string name in new[] { "get_status", "list_accounts", "list_columns", "list_articles", "search_articles", "get_article", "list_media", "start_collection", "stop_collection", "list_tasks", "get_task", "export_articles", "cancel_export" })
                        Check(names.Contains(name), "缺少工具 " + name);
                    var start = tools.Single(t => t.Name == "start_collection");
                    Check(start.JsonSchema.GetProperty("required").EnumerateArray().Any(p => p.GetString() == "request_id"), "收集去重 ID 不是必填参数");
                    var exported = tools.Single(t => t.Name == "export_articles");
                    Check(exported.JsonSchema.GetProperty("required").EnumerateArray().Any(p => p.GetString() == "request_id"), "导出去重 ID 不是必填参数");
                    var status = await client.CallToolAsync("get_status", cancellationToken: timeout.Token);
                    Check(status.IsError != true && status.StructuredContent?.GetProperty("ready").GetBoolean() == true, "官方客户端调用失败，协议 " + version);
                    Check(status.Content.OfType<TextContentBlock>().Any(t => t.Text.Contains("ready")), "兼容文本结果未携带业务数据");
                    Check(host.LastCallUtc.HasValue, "最近调用时间未更新");
                    await client.CallToolAsync("list_articles", new Dictionary<string, object> { ["account_id"] = "测试公众号" }, cancellationToken: timeout.Token);
                    Check(fake.LastTool == "list_articles" && fake.LastArguments.GetProperty("account_id").GetString() == "测试公众号", "参数或中文未到达后端");
                    Check(fake.LastArguments.GetProperty("page_size").GetInt32() == 50, "工具默认分页未传递");
                    var error = await client.CallToolAsync("get_task", new Dictionary<string, object> { ["task_id"] = "expected-error" }, cancellationToken: timeout.Token);
                    Check(error.IsError == true && error.StructuredContent?.GetProperty("error").GetProperty("code").GetString() == "task_not_found", "业务错误代码未返回");
                    var unexpected = await client.CallToolAsync("get_task", new Dictionary<string, object> { ["task_id"] = "unexpected-error" }, cancellationToken: timeout.Token);
                    string unexpectedJson = unexpected.StructuredContent?.GetRawText() ?? "";
                    Check(unexpected.IsError == true && unexpectedJson.Contains("internal_error") && !unexpectedJson.Contains("secret-cookie"), "未知异常泄露后端信息");
                    if (version == "2026-07-28")
                    {
                        using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                        Task<CallToolResult> pending = client.CallToolAsync("get_task", new Dictionary<string, object> { ["task_id"] = "slow-query" }, cancellationToken: callCancellation.Token).AsTask();
                        await fake.SlowQueryStarted.Task.WaitAsync(timeout.Token);
                        callCancellation.Cancel();
                        bool cancelled = false;
                        try { await pending; } catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled, "客户端取消未及时返回");
                        await fake.SlowQueryCancelled.Task.WaitAsync(timeout.Token);
                    }
                    }
                    await AwaitConnectionState(host, false, timeout.Token);
                }

                var conflictingConfig = config.Copy();
                conflictingConfig.Port = new Uri(host.Endpoint).Port;
                await using (var conflicting = new McpHost(fake, conflictingConfig))
                {
                    bool conflictReported = false;
                    try { await conflicting.StartAsync(timeout.Token); } catch (IOException) { conflictReported = true; }
                    Check(conflictReported && !conflicting.IsRunning && host.IsRunning, "端口冲突未被正确报告或影响已有服务");
                }
                using var oldConnection = NewHttpClient();
                Check(await DiscoveryStatus(oldConnection, host.Endpoint, config.Token, timeout.Token) == HttpStatusCode.OK, "停止前的认证请求失败");
                await AwaitConnectionState(host, true, timeout.Token);
                string stoppedEndpoint = host.Endpoint;
                await host.StopAsync(timeout.Token);
                Check(!host.IsRunning && !host.HasConnectedClient, "停止后仍然显示运行或连接");
                bool closed = false;
                try { await http.GetAsync(stoppedEndpoint, timeout.Token); } catch (HttpRequestException) { closed = true; }
                Check(closed, "停止后端口仍然开放");
                await host.StartAsync(timeout.Token);
                Check(host.IsRunning && !host.HasConnectedClient, "同一服务对象停止后不能重新启动或继承旧连接");
                using (var restartedConnection = NewHttpClient())
                {
                    Check(await DiscoveryStatus(restartedConnection, host.Endpoint, config.Token, timeout.Token) == HttpStatusCode.OK, "重启后的认证请求失败");
                    await AwaitConnectionState(host, true, timeout.Token);
                    oldConnection.Dispose();
                    Check(await DiscoveryStatus(restartedConnection, host.Endpoint, config.Token, timeout.Token) == HttpStatusCode.OK && host.HasConnectedClient,
                        "关闭旧代客户端错误清除了重启后的连接");
                }
                await AwaitConnectionState(host, false, timeout.Token);
                await CheckLiveConnections(host, config, timeout.Token);
                await host.StopAsync(timeout.Token);
                Check(events.All(e => !e.Contains(config.Token)), "状态消息暴露令牌");
            }
            finally { Directory.Delete(temporary, true); }
        }

        static HttpClient NewHttpClient() => new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            MaxConnectionsPerServer = 1,
            PooledConnectionIdleTimeout = System.Threading.Timeout.InfiniteTimeSpan
        }) { Timeout = TimeSpan.FromSeconds(5) };

        static async Task CheckLiveConnections(McpHost host, McpConfiguration config, CancellationToken cancellation)
        {
            using var first = NewHttpClient();
            using var second = NewHttpClient();
            Check(await DiscoveryStatus(first, host.Endpoint, config.Token, cancellation) == HttpStatusCode.OK, "首个认证发现请求失败");
            await AwaitConnectionState(host, true, cancellation);
            Check(await DiscoveryStatus(second, host.Endpoint, config.Token, cancellation) == HttpStatusCode.OK, "第二个认证发现请求失败");
            first.Dispose();
            // A real round trip after closing the first connection confirms the second
            // independently authenticated connection remains usable and counted.
            Check(await DiscoveryStatus(second, host.Endpoint, config.Token, cancellation) == HttpStatusCode.OK && host.HasConnectedClient,
                "一个客户端断开错误清除了其他活跃连接");
            second.Dispose();
            await AwaitConnectionState(host, false, cancellation);
        }

        static async Task<HttpStatusCode> DiscoveryStatus(HttpClient http, string endpoint, string token, CancellationToken cancellation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
            request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/list");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{},\"io.modelcontextprotocol/clientInfo\":{\"name\":\"WCAE-connection-test\",\"version\":\"1\"}}}}", Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellation);
            string body = await response.Content.ReadAsStringAsync(cancellation);
            if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Unauthorized)
                throw new Exception("MCP 测试发现请求失败：" + (int)response.StatusCode + " " + body.Substring(0, Math.Min(body.Length, 600)));
            return response.StatusCode;
        }

        static async Task AwaitConnectionState(McpHost host, bool connected, CancellationToken cancellation)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                while (host.HasConnectedClient != connected) await Task.Delay(10, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new Exception(connected ? "MCP: 成功认证的连接未被标记为已连接" : "MCP: 已关闭的连接仍被标记为已连接");
            }
        }

        static async Task<HttpStatusCode> RequestStatus(HttpClient http, string endpoint, string token, string origin, string host)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
            if (host != null) request.Headers.Host = host;
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }

        sealed class FakeApplication : IMcpApplication
        {
            internal string LastTool;
            internal JsonElement LastArguments;
            internal readonly TaskCompletionSource<bool> SlowQueryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> SlowQueryCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<object> InvokeAsync(string tool, JsonElement arguments, CancellationToken cancellation)
            {
                LastTool = tool;
                LastArguments = arguments.Clone();
                if (tool == "get_task")
                {
                    string id = arguments.GetProperty("task_id").GetString();
                    if (id == "expected-error") throw new McpApplicationException("task_not_found", "未找到任务。");
                    if (id == "unexpected-error") throw new IOException("secret-cookie=https://upstream.invalid/?key=hidden");
                    if (id == "slow-query")
                    {
                        SlowQueryStarted.TrySetResult(true);
                        try { await Task.Delay(TimeSpan.FromMinutes(1), cancellation); }
                        catch (OperationCanceledException) { SlowQueryCancelled.TrySetResult(true); throw; }
                    }
                }
                return new { ready = true, source = "isolated-test", tool };
            }
        }
    }
}
