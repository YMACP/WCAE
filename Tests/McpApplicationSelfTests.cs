using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WCAE
{
    internal static class McpApplicationSelfTests
    {
        internal static async Task RunAsync()
        {
            string original = AppPaths.DataDirectory;
            string fixture = Path.Combine(Path.GetTempPath(), "WCAE-mcp-application-" + Guid.NewGuid().ToString("N"));
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                Exception failure = null;
                try
                {
                    AppPaths.DataDirectory = fixture;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    using var form = new MainForm(true) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
                    form.Show();
                    Task test = form.ValidateMcpIntegrationAsync(fixture, timeout.Token);
                    var elapsed = Stopwatch.StartNew();
                    while (!test.IsCompleted)
                    {
                        if (elapsed.Elapsed > TimeSpan.FromSeconds(27)) throw new TimeoutException("MCP 主界面集成检查或清理超时。");
                        Application.DoEvents(); Thread.Sleep(1);
                    }
                    test.GetAwaiter().GetResult();
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    AppPaths.DataDirectory = original;
                    string resolved = Path.GetFullPath(fixture), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                    if (Directory.Exists(resolved) && string.Equals(Path.GetDirectoryName(resolved), temp, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(resolved).StartsWith("WCAE-mcp-application-", StringComparison.Ordinal))
                    {
                        try { Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                if (failure == null) result.TrySetResult(true); else result.TrySetException(failure);
            }) { IsBackground = true, Name = "WCAE MCP isolated UI integration" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            try { await result.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
            finally
            {
                // The test worker normally restores this before its thread exits; also restore if an outer timeout fires.
                AppPaths.DataDirectory = original;
            }
        }
        internal static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("MCP application: " + message); }
    }

    public sealed partial class MainForm
    {
        internal async Task ValidateMcpIntegrationAsync(string fixture, CancellationToken timeout)
        {
            if (!preview || !IsHandleCreated || InvokeRequired) throw new InvalidOperationException("MCP 集成检查只允许隔离预览 UI。");
            var releases = new List<TaskCompletionSource<bool>>();
            TaskCompletionSource<bool> NewSignal() { var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); releases.Add(signal); return signal; }
            var transport = new McpIntegrationNoNetwork();
            exporter = new ExportService(transport); enricher = new ArticleEnricher(transport);
            repository.ClearHistory(); mcpQueries.InvalidateSnapshots(); sessions.Clear(); records.Clear();
            const string account = "mcp-integration", articleId = "mcp-integration:123456:1";
            var article = new ArticleRecord { Id = articleId, Biz = account, Mid = "123456", Idx = 1, AccountName = "MCP 集成测试公众号",
                Title = "中文正文集成验证", TitleSource = "heading", RawTitle = "中文正文集成验证", Status = ArticleStatus.Available,
                Url = "https://mp.weixin.qq.com/s?__biz=mcp-integration&mid=123456&idx=1&key=fixture-session-secret",
                PublishedAt = new DateTime(2026, 9, 15, 12, 0, 0), Html = "<div id='js_content'><p>这是一篇完全保存在本地的中文文章。</p></div>",
                AudioMetadataVersion = 1 };
            repository.Save(article);
            sessions[account] = new AccountSession { Biz = account, Name = article.AccountName, Cookie = "fixture-cookie", Uin = "fixture-uin", Key = "fixture-key" };
            LoadAccounts(account);
            using var registryScope = ExportFileSystem.UseDirectoryRegistry(new ExportDirectoryRegistry(fixture));
            try
            {
                var status = await CallMcpIntegrationAsync("get_status", new { }, timeout);
                McpApplicationSelfTests.Check(status.GetProperty("runtime_state").GetString() == "ready" && !status.GetProperty("mcp").GetProperty("running").GetBoolean(), "预览后端状态不正确或意外开启监听");
                var accountsResult = await CallMcpIntegrationAsync("list_accounts", new { }, timeout);
                McpApplicationSelfTests.Check(accountsResult.GetProperty("accounts").GetArrayLength() == 1, "真实后端没有返回隔离账号");
                var body = await CallMcpIntegrationAsync("get_article", new { account_id = account, article_id = articleId, format = "text" }, timeout);
                McpApplicationSelfTests.Check(body.GetProperty("content").GetString().Contains("完全保存在本地") && !body.GetRawText().Contains("fixture-session-secret"), "真实后端正文内容或凭据净化错误");
                await ExpectMcpIntegrationError("article_not_found", "get_article", new { account_id = "wrong-account", article_id = articleId }, timeout);
                await ExpectMcpIntegrationError("ARTICLE_NOT_FOUND", "export_articles", new { account_id = "wrong-account", article_ids = new[] { articleId }, request_id = "wrong-account", format = "html" }, timeout);

                // Exercise accepted UI work from a background caller. Cancellation cannot release ownership before the action unwinds.
                var actionStarted = NewSignal(); var actionRelease = NewSignal();
                using (var cancelAction = new CancellationTokenSource())
                {
                    var action = Task.Run(() => OnMcpUiAsync(async () => { actionStarted.TrySetResult(true); await actionRelease.Task; return (object)"unwound"; }, cancelAction.Token));
                    await actionStarted.Task.WaitAsync(timeout); cancelAction.Cancel();
                    await Task.Delay(30, timeout);
                    McpApplicationSelfTests.Check(!action.IsCompleted, "已开始的 UI 异步动作在取消时被提前放弃");
                    actionRelease.TrySetResult(true);
                    McpApplicationSelfTests.Check((string)await action.WaitAsync(timeout) == "unwound", "取消后的 UI 动作未等待清理完成");
                }

                // Official MCP SDK -> HTTP service -> real MainForm implementation -> cached HTML export.
                var config = new McpConfiguration { Port = 0 };
                await using (var host = new McpHost(this, config, true))
                {
                    await host.StartAsync(timeout);
                    using var httpClient = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                    var clientTransport = new HttpClientTransport(new HttpClientTransportOptions
                    { Endpoint = new Uri(host.Endpoint), TransportMode = HttpTransportMode.StreamableHttp, ConnectionTimeout = TimeSpan.FromSeconds(5),
                        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + config.Token } }, httpClient, ownsHttpClient: false);
                    await using var client = await McpClient.CreateAsync(clientTransport, new McpClientOptions
                    { ClientInfo = new Implementation { Name = "WCAE-real-application-self-test", Version = "1" }, InitializationTimeout = TimeSpan.FromSeconds(5) }, cancellationToken: timeout);
                    var sdkAccounts = await client.CallToolAsync("list_accounts", cancellationToken: timeout);
                    McpApplicationSelfTests.Check(sdkAccounts.IsError != true && sdkAccounts.StructuredContent?.GetProperty("accounts").GetArrayLength() == 1, "官方客户端未连通真实公众号后端");
                    var sdkBody = await client.CallToolAsync("get_article", new Dictionary<string, object> { ["account_id"] = account, ["article_id"] = articleId, ["format"] = "text" }, cancellationToken: timeout);
                    McpApplicationSelfTests.Check(sdkBody.IsError != true && sdkBody.StructuredContent?.GetProperty("content").GetString().Contains("完全保存在本地") == true, "官方客户端未取得真实缓存正文");
                    var sdkArticles = await client.CallToolAsync("list_articles", new Dictionary<string, object> { ["account_id"] = account }, cancellationToken: timeout);
                    McpApplicationSelfTests.Check(sdkArticles.IsError != true && sdkArticles.StructuredContent?.GetProperty("items").GetArrayLength() == 1, "官方客户端默认可选参数未兼容真实文章查询");
                    string destination = Path.Combine(fixture, "exports");
                    var exportArgs = new Dictionary<string, object> { ["account_id"] = account, ["article_ids"] = new[] { articleId }, ["format"] = "html", ["destination"] = destination, ["request_id"] = "http-export-once" };
                    var exported = await client.CallToolAsync("export_articles", exportArgs, cancellationToken: timeout);
                    McpApplicationSelfTests.Check(exported.IsError != true, "官方客户端提交真实导出失败");
                    string exportedId = exported.StructuredContent.Value.GetProperty("id").GetString();
                    await mcpTasks.Completion(exportedId).WaitAsync(timeout);
                    var result = await client.CallToolAsync("get_task", new Dictionary<string, object> { ["task_id"] = exportedId }, cancellationToken: timeout);
                    McpApplicationSelfTests.Check(result.IsError != true && result.StructuredContent?.GetProperty("state").GetString() == "completed", "真实正文导出任务未完成");
                    var duplicate = await client.CallToolAsync("export_articles", exportArgs, cancellationToken: timeout);
                    McpApplicationSelfTests.Check(duplicate.StructuredContent?.GetProperty("id").GetString() == exportedId && Directory.GetDirectories(destination).Length == 1
                        && Directory.GetFiles(destination, "*.html", SearchOption.AllDirectories).Length == 1, "重试导出重复执行或创建重复目录");
                    McpApplicationSelfTests.Check(!Directory.EnumerateFiles(destination, "export-results-*", SearchOption.AllDirectories).Any(), "MCP 导出重新生成了已移除的结果清单");
                    await host.StopAsync(timeout);
                }

                // Waiting destination remains an active export, and both MCP and UI share the same busy/cancel path.
                var waiting = NewSignal(); var cancelled = NewSignal(); var cleanup = NewSignal();
                McpDirectorySelector = async token =>
                {
                    waiting.TrySetResult(true);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; }
                    finally { cancelled.TrySetResult(true); await cleanup.Task; }
                };
                var waitingArgs = new { account_id = account, article_ids = new[] { articleId }, format = "text", request_id = "waiting-export" };
                var pending = await CallMcpIntegrationAsync("export_articles", waitingArgs, timeout); string pendingId = pending.GetProperty("id").GetString();
                await waiting.Task.WaitAsync(timeout);
                var duplicateWaiting = await CallMcpIntegrationAsync("export_articles", waitingArgs, timeout);
                McpApplicationSelfTests.Check(duplicateWaiting.GetProperty("id").GetString() == pendingId && mcpTasks.Get(pendingId).State == "waiting", "等待目录任务去重或状态错误");
                await ExpectMcpIntegrationError("BUSY", "export_articles", new { account_id = account, article_ids = new[] { articleId }, format = "text", request_id = "another-export" }, timeout);
                bool uiBusy = false;
                try { StartSharedExport(account, new[] { articleId }, new ExportOptions { Format = ExportFormat.Text }, "ui-busy", "ui"); }
                catch (McpApplicationException ex) when (ex.Code == "BUSY") { uiBusy = true; }
                McpApplicationSelfTests.Check(uiBusy, "UI 导出没有共享 MCP 任务互斥");
                await CallMcpIntegrationAsync("cancel_export", new { task_id = pendingId }, timeout);
                await cancelled.Task.WaitAsync(timeout);
                McpApplicationSelfTests.Check(!mcpTasks.Completion(pendingId).IsCompleted && mcpTasks.Get(pendingId).State == "cancelling", "取消等待目录时提前释放了导出所有权");
                cleanup.TrySetResult(true); await mcpTasks.Completion(pendingId).WaitAsync(timeout);
                McpApplicationSelfTests.Check(mcpTasks.Get(pendingId).State == "cancelled" && exportCancellation == null, "取消导出没有清理共享取消源");
                McpDirectorySelector = _ => Task.FromResult<string>(null);
                var nullChoice = await CallMcpIntegrationAsync("export_articles", new { account_id = account, article_ids = new[] { articleId }, format = "text", request_id = "null-destination" }, timeout);
                string nullId = nullChoice.GetProperty("id").GetString(); await mcpTasks.Completion(nullId).WaitAsync(timeout);
                McpApplicationSelfTests.Check(mcpTasks.Get(nullId).State == "cancelled", "用户取消目录选择未标记为 cancelled");

                // The collection adapter also honors the UI stop control; its fake source never contacts WeChat.
                var collecting = NewSignal(); var collectionCancelled = NewSignal(); var collectionCleanup = NewSignal();
                controller.Dispose();
                controller = new CollectionController(async (_, __, ___, token) =>
                {
                    collecting.TrySetResult(true);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    finally { collectionCancelled.TrySetResult(true); await collectionCleanup.Task; }
                }, (record, _, __) => Task.FromResult(record), repository.Save);
                var collection = StartSharedCollection(account, null, "ui-collection-stop", "ui");
                await collecting.Task.WaitAsync(timeout); StopSharedCollection(); await collectionCancelled.Task.WaitAsync(timeout);
                McpApplicationSelfTests.Check(!mcpTasks.Completion(collection.Id).IsCompleted && controller.IsRunning, "停止采集未等待采集器实际退出");
                collectionCleanup.TrySetResult(true); await mcpTasks.Completion(collection.Id).WaitAsync(timeout);
                McpApplicationSelfTests.Check(mcpTasks.Get(collection.Id).State == "cancelled" && !controller.IsRunning, "UI 停止采集未与 MCP 任务同步");

                // Clear must wait for a cancelled writer to finish before deleting history or invalidating its queries.
                waiting = NewSignal(); cancelled = NewSignal(); cleanup = NewSignal();
                var clearWait = waiting; var clearCancelled = cancelled; var clearCleanup = cleanup;
                McpDirectorySelector = async token =>
                {
                    clearWait.TrySetResult(true);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; }
                    finally { clearCancelled.TrySetResult(true); await clearCleanup.Task; }
                };
                pending = await CallMcpIntegrationAsync("export_articles", new { account_id = account, article_ids = new[] { articleId }, format = "text", request_id = "clear-cache-writer" }, timeout);
                pendingId = pending.GetProperty("id").GetString(); await clearWait.Task.WaitAsync(timeout);
                Task clearing = ClearCacheAsync(); await clearCancelled.Task.WaitAsync(timeout);
                McpApplicationSelfTests.Check(!clearing.IsCompleted && repository.Load(account).Count == 1, "清空缓存未等待旧写入任务结束");
                clearCleanup.TrySetResult(true); await clearing.WaitAsync(timeout);
                accountsResult = await CallMcpIntegrationAsync("list_accounts", new { }, timeout);
                McpApplicationSelfTests.Check(accountsResult.GetProperty("accounts").GetArrayLength() == 0 && repository.Load(account).Count == 0
                    && sessions.Count == 0 && mcpTasks.List().Count == 0, "清空后仍返回历史账号、文章或任务");

                // Exercise the actual shutdown helper with a pending chooser; no proxy/capture service was ever started.
                article.Html = "<div id='js_content'>关闭前的缓存正文</div>"; repository.Save(article);
                var closeWait = NewSignal(); var closeCancelled = NewSignal(); var closeCleanup = NewSignal();
                McpDirectorySelector = async token =>
                {
                    closeWait.TrySetResult(true);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; }
                    finally { closeCancelled.TrySetResult(true); await closeCleanup.Task; }
                };
                pending = await CallMcpIntegrationAsync("export_articles", new { account_id = account, article_ids = new[] { articleId }, format = "text", request_id = "shutdown-writer" }, timeout);
                pendingId = pending.GetProperty("id").GetString(); await closeWait.Task.WaitAsync(timeout);
                closing = true; lifetime.Cancel();
                Task shutdown = ShutdownAsync(); await closeCancelled.Task.WaitAsync(timeout);
                McpApplicationSelfTests.Check(!shutdown.IsCompleted, "关闭未等待目录选择/导出任务清理");
                closeCleanup.TrySetResult(true); await shutdown.WaitAsync(timeout);
                McpApplicationSelfTests.Check(coreDisposed && exportCancellation == null && mcpTasks.Get(pendingId).State == "cancelled"
                    && !mcpTasks.List().Any(t => IsActiveState(t.State)), "关闭后服务、取消源或活动任务未清理");
                McpApplicationSelfTests.Check(transport.Calls == 0 && !Directory.Exists(Path.Combine(fixture, "components"))
                    && !File.Exists(Path.Combine(fixture, "network-settings.json")) && !File.Exists(Path.Combine(fixture, "system-proxy-lease.bin")), "隔离集成检查发生了网络调用、组件释放或代理变更");
                allowClose = true;
            }
            finally
            {
                foreach (var signal in releases) signal.TrySetResult(true);
                if (mcpTasks != null) await mcpTasks.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
                McpDirectorySelector = null;
                if (!coreDisposed) DisposeCore();
            }
        }

        async Task<JsonElement> CallMcpIntegrationAsync(string tool, object args, CancellationToken timeout)
        {
            object result = await Task.Run(() => ((IMcpApplication)this).InvokeAsync(tool, JsonSerializer.SerializeToElement(args), timeout), timeout).WaitAsync(timeout);
            return JsonSerializer.SerializeToElement(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        }
        async Task ExpectMcpIntegrationError(string code, string tool, object args, CancellationToken timeout)
        {
            try { await CallMcpIntegrationAsync(tool, args, timeout); throw new InvalidOperationException("未返回预期错误：" + code); }
            catch (McpApplicationException ex) { McpApplicationSelfTests.Check(ex.Code == code, "错误代码不匹配：" + ex.Code + " / " + code); }
        }
        sealed class McpIntegrationNoNetwork : IHttpTransport
        {
            internal int Calls;
            Task<T> Fail<T>() { Interlocked.Increment(ref Calls); return Task.FromException<T>(new InvalidOperationException("MCP 集成测试禁止上游网络调用。")); }
            public Task<string> GetStringAsync(string url, AccountSession session, CancellationToken token) => Fail<string>();
            public Task<string> PostFormAsync(string url, IDictionary<string, string> values, AccountSession session, CancellationToken token) => Fail<string>();
            public Task DownloadAsync(string url, string destination, AccountSession session, CancellationToken token) => Fail<object>();
        }
    }
}
