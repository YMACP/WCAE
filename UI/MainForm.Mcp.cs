using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WCAE
{
    public sealed partial class MainForm
    {
        McpHost mcpHost;
        McpConfiguration mcpConfiguration;
        McpConfigurationStore mcpConfigurationStore;
        McpTaskRegistry mcpTasks;
        McpQueryService mcpQueries;
        Task mcpInitializationTask;
        bool mcpConfigurationRequiresSave;
        readonly SemaphoreSlim mcpHostGate=new SemaphoreSlim(1,1);
        string activeCollectionTaskId,activeExportTaskId;
        internal Func<CancellationToken,Task<string>> McpDirectorySelector;

        void InitializeMcpCore()
        {
            lifetime.Token.ThrowIfCancellationRequested();
            mcpTasks=new McpTaskRegistry(dataDirectory);
            mcpQueries=new McpQueryService(repository);
            mcpTasks.Changed+=info=>Ui(()=>
            {
                if(closing || clearingCache)return;
                if(info.State is "completed" or "failed" or "cancelled" or "interrupted")
                    AppendLog((info.Kind=="collection"?"收集任务":"导出任务")+" "+TaskStateText(info.State)+"："+info.Message);
                if(info.Kind=="collection")
                {
                    bool active=HasActiveMcpTask("collection");
                    start.Enabled=coreReady&&!preview&&!reconnecting&&!active;
                    stop.Enabled=active;
                }
                UpdateSummary();
            });
        }

        async Task InitializeMcpConfigurationAsync(CancellationToken token)
        {
            try
            {
                var store=new McpConfigurationStore(dataDirectory);
                var configuration=await Task.Run(()=>store.LoadOrCreate(),token);
                await mcpHostGate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    mcpConfigurationStore=store; mcpConfiguration=configuration;
                    mcpConfigurationRequiresSave=false;
                }
                finally { mcpHostGate.Release(); }
            }
            catch(OperationCanceledException) { }
            catch(Exception ex)
            {
                if(mcpConfiguration==null)
                {
                    // Keep the unreadable file intact. A fresh in-memory draft lets the
                    // user explicitly save working settings through the normal dialog.
                    mcpConfigurationStore=new McpConfigurationStore(dataDirectory);
                    mcpConfiguration=new McpConfiguration();
                    mcpConfigurationRequiresSave=true;
                    Ui(()=>AppendLog("MCP 配置读取失败（"+ex.GetType().Name+"）；请点击右上角 MCP 图标并保存新配置后重试。原配置文件已保留。"));
                    return;
                }
                Ui(()=>AppendLog("MCP 配置读取失败（"+ex.GetType().Name+"），请点击右上角 MCP 图标检查设置。"));
            }
        }

        McpHost CreateMcpHost(McpConfiguration configuration)
        {
            var host=new McpHost(this,configuration);
            host.StatusChanged+=message=>Ui(()=>AppendLog(message));
            return host;
        }

        // Call only while holding mcpHostGate. Finishing disposal releases the port
        // even when the dialog or application has cancelled the initiating action.
        async Task DisposeMcpHostAsync()
        {
            var host=mcpHost;
            try { if(host!=null)await host.DisposeAsync(); }
            finally { mcpHost=null; }
        }

        async Task SetMcpRunningAsync(bool running,CancellationToken token)
        {
            if(preview)throw new McpApplicationException("PREVIEW_MODE","离线预览不会开启 MCP 服务。");
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
            await mcpHostGate.WaitAsync(linked.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                if(!running)
                {
                    // Stop the API listener only; shared collection/export tasks
                    // retain their existing lifetime and remain controllable in WCAE.
                    await DisposeMcpHostAsync();
                    return;
                }
                if(mcpHost?.IsRunning==true)return;
                if(mcpConfiguration==null)
                    throw new McpApplicationException("INITIALIZING","MCP 配置仍在初始化，请稍后重试。");
                if(mcpConfigurationRequiresSave)
                    throw new McpApplicationException("SETTINGS_REQUIRED","请先保存 MCP 配置，再启动服务。");
                await DisposeMcpHostAsync();
                mcpHost=CreateMcpHost(mcpConfiguration);
                try
                {
                    await mcpHost.StartAsync(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                }
                catch { await DisposeMcpHostAsync(); throw; }
            }
            finally { mcpHostGate.Release(); }
        }

        async Task StopMcpAsync()
        {
            await mcpHostGate.WaitAsync();
            try { await DisposeMcpHostAsync(); }
            finally { mcpHostGate.Release(); }
        }

        async Task ApplyMcpConfigurationAsync(McpConfiguration configuration,CancellationToken token)
        {
            if(preview)throw new McpApplicationException("PREVIEW_MODE","离线预览不会开启 MCP 服务。");
            configuration=configuration.Copy();
            configuration.Validate();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
            await mcpHostGate.WaitAsync(linked.Token);
            var previous=mcpConfiguration;
            bool wasRunning=mcpHost?.IsRunning==true;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                // Saving while stopped must never start listening. A running
                // service validates the new binding before committing settings.
                if(wasRunning)
                {
                    await DisposeMcpHostAsync();
                    mcpHost=CreateMcpHost(configuration);
                    await mcpHost.StartAsync(linked.Token);
                }
                linked.Token.ThrowIfCancellationRequested();
                mcpConfigurationStore??=new McpConfigurationStore(dataDirectory);
                mcpConfigurationStore.Save(configuration);
                mcpConfiguration=configuration;
                mcpConfigurationRequiresSave=false;
                AppendLog("MCP 设置已保存："+configuration.Endpoint+"；请使用最新连接配置。");
            }
            catch
            {
                if(wasRunning)
                {
                    try { await DisposeMcpHostAsync(); } catch { }
                    if(previous!=null && !closing && !lifetime.IsCancellationRequested)
                    {
                        using var recovery=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        recovery.CancelAfter(TimeSpan.FromSeconds(5));
                        mcpHost=CreateMcpHost(previous);
                        try { await mcpHost.StartAsync(recovery.Token); }
                        catch
                        {
                            try { await DisposeMcpHostAsync(); } catch { }
                            Ui(()=>AppendLog("MCP 原服务恢复失败；原配置已保留，请检查端口后手动启动。"));
                        }
                    }
                }
                throw;
            }
            finally { mcpHostGate.Release(); }
        }

        void OpenMcpSettings(IWin32Window owner)
        {
            if(closing)return;
            if(mcpConfiguration==null)
            {
                if(preview)mcpConfiguration=new McpConfiguration { Port=8878,Token="preview-only-not-a-real-token" };
                else { MessageBox.Show(owner,"MCP 仍在初始化，请稍后打开设置。","MCP接入",MessageBoxButtons.OK,MessageBoxIcon.Information); return; }
            }
            using var dialog=new McpSettingsForm(()=>mcpConfiguration,()=>mcpHost?.IsRunning==true,
                ()=>mcpHost?.HasConnectedClient==true,ApplyMcpConfigurationAsync,SetMcpRunningAsync,preview);
            dialog.ShowDialog(owner);
        }

        async Task<object> IMcpApplication.InvokeAsync(string tool,JsonElement arguments,CancellationToken cancellation)
        {
            if(tool is "list_accounts" or "list_columns" or "list_articles" or "search_articles" or "get_article" or "list_media")
            {
                var lease=(McpReadLease)await OnMcpUiAsync(()=>
                {
                    EnsureMcpReady();
                    return Task.FromResult<object>(new McpReadLease(mcpQueries,Volatile.Read(ref historyEpoch)));
                },cancellation);
                var result=await lease.Queries.InvokeAsync(tool,arguments,cancellation);
                cancellation.ThrowIfCancellationRequested();
                if(closing || lease.Epoch!=Volatile.Read(ref historyEpoch))
                    throw new McpApplicationException("HISTORY_CHANGED","历史记录已变化，请重新查询。");
                return result;
            }
            return await OnMcpUiAsync(()=>Task.FromResult(InvokeMcpCommand(tool,arguments)),cancellation);
        }

        object InvokeMcpCommand(string tool,JsonElement args)
        {
            if(tool=="get_status")return new
            {
                app="WCAE",version=typeof(MainForm).Assembly.GetName().Version.ToString(3),
                runtime_state=closing?"stopping":coreReady?"ready":"initializing",
                mcp=new { running=mcpHost?.IsRunning==true, endpoint=mcpConfiguration?.Endpoint, last_call_utc=mcpHost?.LastCallUtc },
                capture=new { connected=coreReady && capture.RouteActive && capture.Diagnostics.Snapshot.Listening,
                    account_identified=!string.IsNullOrWhiteSpace(current?.Biz),account_id=current?.Biz,account_name=current?.Name,
                    session_ready=MissingSessionFields(current).Count==0,missing_session_fields=MissingSessionFields(current) },
                tasks=mcpTasks?.List().Where(t=>IsActiveState(t.State)).ToArray(),
                coverage="历史接口及本机已加载的公众号目录快照；不代表全部历史文章。"
            };
            EnsureMcpReady();
            if(tool=="list_tasks")return new { tasks=mcpTasks.List().Select(t=>new {
                id=t.Id,kind=t.Kind,account_id=t.AccountId,state=t.State,origin=t.Origin,
                processed=t.Processed,total=t.Total,failed=t.Failed,message=t.Message,error_code=t.ErrorCode,
                created_at=t.CreatedAt,updated_at=t.UpdatedAt }).ToArray() };
            if(tool=="get_task")return mcpTasks.Get(McpString(args,"task_id"));
            if(tool=="stop_collection")return mcpTasks.Cancel(McpString(args,"task_id"),"collection");
            if(tool=="cancel_export")return mcpTasks.Cancel(McpString(args,"task_id"),"export");
            if(tool=="start_collection")
            {
                if(preview)throw new McpApplicationException("PREVIEW_MODE","离线预览不发起公众号收集。");
                int limit=McpInt(args,"max_articles",50,0,10000);
                return StartSharedCollection(McpString(args,"account_id"),limit==0?null:limit,McpString(args,"request_id"),"agent");
            }
            if(tool=="export_articles")
            {
                if(!args.TryGetProperty("article_ids",out var ids) || ids.ValueKind!=JsonValueKind.Array)
                    throw new McpApplicationException("INVALID_ARGUMENT","article_ids 必须是文章 ID 数组。");
                var articles=ids.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.String?v.GetString():null).ToArray();
                if(articles.Length<1 || articles.Length>1000 || articles.Any(string.IsNullOrWhiteSpace))
                    throw new McpApplicationException("INVALID_ARGUMENT","每次导出需指定 1 至 1000 个有效文章 ID。");
                var options=new ExportOptions { Format=McpExportFormat(McpString(args,"format",false,"full")),
                    Destination=McpString(args,"destination",false),DownloadImages=McpBool(args,"download_images",false),Overwrite=McpBool(args,"overwrite",false) };
                return StartSharedExport(McpString(args,"account_id"),articles,options,McpString(args,"request_id"),"agent");
            }
            throw new McpApplicationException("UNKNOWN_TOOL","未找到该工具。");
        }

        void EnsureMcpReady()
        {
            if(closing)throw new McpApplicationException("SHUTTING_DOWN","WCAE 正在关闭。");
            if(!coreReady || mcpTasks==null || mcpQueries==null)throw new McpApplicationException("INITIALIZING","WCAE 正在初始化，请稍后重试。");
            if(clearingCache)throw new McpApplicationException("HISTORY_BUSY","正在清空缓存，请稍后重试。");
        }
        void EnsureStableOperations()
        {
            EnsureMcpReady();
            if(!preview && (reconnecting || initializationTask?.IsCompleted==false || listenerTask?.IsCompleted==false))
                throw new McpApplicationException("CONNECTION_BUSY","正在初始化或调整连接，请稍后再提交任务。");
        }

        McpTaskInfo StartSharedCollection(string accountId,int? limit,string requestId,string origin)
        {
            EnsureMcpReady();
            var fingerprint=JsonSerializer.Serialize(new { accountId,limit });
            var duplicate=mcpTasks.FindDuplicate("collection",accountId,requestId,fingerprint);
            if(duplicate!=null)return duplicate;
            EnsureStableOperations();
            if(!sessions.TryGetValue(accountId,out var saved))throw new McpApplicationException("SESSION_REQUIRED","尚未取得该公众号会话，请在微信中打开并刷新该公众号的一篇文章。");
            var session=saved.Clone();
            var missing=MissingSessionFields(session);
            if(missing.Count>0)throw new McpApplicationException("SESSION_REQUIRED","该公众号会话缺少："+string.Join("、",missing)+"。请在微信中刷新文章。");
            var task=mcpTasks.Start("collection",accountId,requestId,fingerprint,
                (context,token)=>OnMcpUiAsync(()=>RunSharedCollectionAsync(session,limit,context,token),token),origin);
            if(IsActiveState(task.State))
            {
                activeCollectionTaskId=task.Id; collectionTask=mcpTasks.Completion(task.Id);
                start.Enabled=false; stop.Enabled=true;
            }
            return task;
        }

        async Task<object> RunSharedCollectionAsync(AccountSession session,int? limit,McpTaskContext context,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LoadAccounts(session.Biz); start.Enabled=false; stop.Enabled=true;
            AppendLog("开始静默收集："+session.Name+(limit.HasValue?"；本次最多处理 "+limit.Value+" 篇。":""));
            controller.Recognize(session);
            var progress=new InlineMcpProgress<CollectionProgress>(p=>
            {
                context.Report(p.Message,controller.LastProcessedCount,limit,controller.LastFailedCount);
                Ui(()=> { if(!clearingCache && !closing && !string.IsNullOrEmpty(p.Message))AppendLog(p.Message); });
            });
            using var cancellation=token.Register(()=>controller.Stop());
            try
            {
                // Start synchronously takes its account snapshot before UI callbacks can switch it.
                var running=controller.StartAsync(progress,limit);
                if(token.IsCancellationRequested)controller.Stop();
                await running;
                token.ThrowIfCancellationRequested();
                context.Report(controller.LastSummary,controller.LastProcessedCount,limit,controller.LastFailedCount);
                return new { processed=controller.LastProcessedCount,succeeded=controller.LastSucceededCount,
                    deleted=controller.LastDeletedCount,failed=controller.LastFailedCount,summary=controller.LastSummary,
                    pending=repository.PendingCount(session.Biz),coverage="partial",resume_supported=true };
            }
            catch(OperationCanceledException) { throw; }
            catch(McpApplicationException) { throw; }
            catch(Exception ex) { throw new McpApplicationException("COLLECTION_FAILED","收集已暂停（"+ex.GetType().Name+"），断点已保留；请检查日志或刷新微信文章后继续。"); }
            finally
            {
                stop.Enabled=false; start.Enabled=!preview&&!closing&&!clearingCache;
                if(!clearingCache&&!closing&&!IsDisposed)
                { dirty=false; records.Clear(); foreach(var article in repository.Load(displayedBiz))records[article.Id]=article; RefreshTable(); }
            }
        }

        McpTaskInfo StartSharedExport(string accountId,string[] articleIds,ExportOptions options,string requestId,string origin)
        {
            EnsureMcpReady();
            var ids=articleIds.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var settings=new ExportOptions { Format=options.Format,Destination=NormalizeDestination(options.Destination),DownloadImages=options.DownloadImages,Overwrite=options.Overwrite };
            var fingerprint=JsonSerializer.Serialize(new { accountId,ids,settings.Format,settings.Destination,settings.DownloadImages,settings.Overwrite });
            var duplicate=mcpTasks.FindDuplicate("export",accountId,requestId,fingerprint);
            if(duplicate!=null)return duplicate;
            EnsureStableOperations();
            var all=repository.Load(accountId).ToDictionary(a=>a.Id,StringComparer.Ordinal);
            if(ids.Length==0 || ids.Any(id=>!all.ContainsKey(id)))throw new McpApplicationException("ARTICLE_NOT_FOUND","部分文章不存在或不属于指定公众号，请重新查询文章列表。");
            var articles=ids.Select(id=>all[id]).ToArray();
            var session=sessions.TryGetValue(accountId,out var saved)?saved.Clone():new AccountSession { Biz=accountId,Name=repository.AccountName(accountId) };
            var task=mcpTasks.Start("export",accountId,requestId,fingerprint,(context,token)=>RunSharedExportAsync(articles,session,settings,context,token),origin);
            if(IsActiveState(task.State))
            {
                activeExportTaskId=task.Id; exportTask=mcpTasks.Completion(task.Id); UpdateSummary();
            }
            return task;
        }

        async Task<object> RunSharedExportAsync(ArticleRecord[] articles,AccountSession session,ExportOptions options,McpTaskContext context,CancellationToken token)
        {
            using var cts=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
            try
            {
                await OnMcpUiAsync(()=> { exportCancellation=cts; UpdateSummary(); return Task.FromResult<object>(null); },token);
                if(string.IsNullOrWhiteSpace(options.Destination))
                {
                    context.SetWaitingForDestination();
                    string directory=McpDirectorySelector!=null ? await McpDirectorySelector(cts.Token)
                        : (string)await OnMcpUiAsync(async()=>await McpDestinationPicker.SelectAsync(this,cts.Token),cts.Token);
                    if(string.IsNullOrEmpty(directory))throw new McpApplicationException("CANCELLED","用户取消保存位置选择。");
                    options.Destination=NormalizeDestination(directory);
                    context.SetRunning();
                }
                cts.Token.ThrowIfCancellationRequested();
                var progress=new InlineMcpProgress<ExportProgress>(p=>
                {
                    context.Report(p.Message,p.Completed,p.Total,p.Failed);
                    Ui(()=> { if(!closing&&!clearingCache)AppendLog($"导出 {p.Completed}/{p.Total} · 失败 {p.Failed} · {p.Message}"); });
                });
                exporter.ClearPreparedHtml=true;
                var preparation=new ExportPreparation(repository,enricher.EnrichAsync);
                exporter.PrepareArticleAsync=(article,ct)=>preparation.PrepareAsync(article,session,options.Format,ct);
                await exporter.ExportAsync(articles,session,options,progress,cts.Token);
                var result=exporter.LastManifest;
                Ui(()=>AppendLog("导出目录："+options.Destination));
                return new { destination=options.Destination,format=options.Format.ToString().ToLowerInvariant(),
                    processed=result.ArticlesProcessed,succeeded=result.Succeeded,failed=result.Failed,skipped=result.Skipped,
                    results=result.Results.Take(30).Select(r=>new { article_id=r.ArticleId,title=r.Title,kind=r.Kind,
                        state=r.State,output_path=r.OutputPath,reason=McpResultText(r.Reason) }).ToArray(),
                    results_total=result.Results.Count,results_truncated=result.Results.Count>30 };
            }
            catch(OperationCanceledException) { throw; }
            catch(McpApplicationException) { throw; }
            catch(Exception ex) { throw new McpApplicationException("EXPORT_FAILED","导出未完成（"+ex.GetType().Name+"）；已完成文件保留，请检查保存位置或文章状态。"); }
            finally
            {
                exporter.PrepareArticleAsync=null;
                // The job stays active until its worker and UI cleanup have finished.
                await OnMcpUiCleanupAsync(()=> { if(ReferenceEquals(exportCancellation,cts))exportCancellation=null; if(!closing)UpdateSummary(); });
            }
        }

        void StopSharedCollection()
        {
            if(mcpTasks!=null && activeCollectionTaskId!=null)try { mcpTasks.Cancel(activeCollectionTaskId,"collection"); } catch(McpApplicationException) { }
            controller?.Stop();
        }
        void CancelSharedExport()
        {
            if(mcpTasks!=null && activeExportTaskId!=null)try { mcpTasks.Cancel(activeExportTaskId,"export"); } catch(McpApplicationException) { }
            exportCancellation?.Cancel();
        }
        bool HasActiveMcpTask(string kind)=>mcpTasks?.List().Any(t=>t.Kind==kind && IsActiveState(t.State))==true;
        static bool IsActiveState(string state)=>state is "queued" or "running" or "waiting" or "cancelling";
        static string TaskStateText(string state)=>state switch { "completed"=>"已完成","failed"=>"未完成","cancelled"=>"已取消","interrupted"=>"已中断",_=>state };

        async Task<object> OnMcpUiAsync(Func<Task<object>> action,CancellationToken token)
        {
            if(IsDisposed || !IsHandleCreated || closing)throw new McpApplicationException("SHUTTING_DOWN","WCAE 正在关闭或窗口尚未就绪。");
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            if(!InvokeRequired)return await action();
            var completion=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            int dispatchState=0;
            // Cancel before dispatch, but never abandon an accepted worker while it is
            // unwinding. Otherwise another job could start against the same controller.
            using var cancel=linked.Token.Register(()=>
            {
                if(Interlocked.CompareExchange(ref dispatchState,2,0)==0)completion.TrySetCanceled(linked.Token);
            });
            try
            {
                BeginInvoke(new Action(async()=>
                {
                    if(Interlocked.CompareExchange(ref dispatchState,1,0)!=0)return;
                    try
                    {
                        if(closing)throw new OperationCanceledException(lifetime.Token);
                        completion.TrySetResult(await action());
                    }
                    catch(OperationCanceledException) { completion.TrySetCanceled(); }
                    catch(Exception ex) { completion.TrySetException(ex); }
                }));
            }
            catch(InvalidOperationException) { throw new McpApplicationException("SHUTTING_DOWN","WCAE 正在关闭。"); }
            return await completion.Task;
        }

        Task OnMcpUiCleanupAsync(Action action)
        {
            if(IsDisposed || !IsHandleCreated)return Task.CompletedTask;
            if(!InvokeRequired) { action(); return Task.CompletedTask; }
            var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try { BeginInvoke(new Action(()=> { try { action(); completion.TrySetResult(); } catch(Exception ex) { completion.TrySetException(ex); } })); }
            catch(InvalidOperationException) { completion.TrySetResult(); }
            return completion.Task;
        }

        static string McpString(JsonElement args,string name,bool required=true,string fallback="")
        {
            if(!args.TryGetProperty(name,out var value) || value.ValueKind==JsonValueKind.Null)
            { if(required)throw new McpApplicationException("INVALID_ARGUMENT","缺少参数："+name); return fallback; }
            if(value.ValueKind!=JsonValueKind.String || value.GetString().Length>32768)throw new McpApplicationException("INVALID_ARGUMENT","参数必须是有效字符串："+name);
            string text=value.GetString().Trim();
            if(required && text.Length==0)throw new McpApplicationException("INVALID_ARGUMENT","参数不能为空："+name);
            return text;
        }
        static int McpInt(JsonElement args,string name,int fallback,int minimum,int maximum)
        {
            if(!args.TryGetProperty(name,out var value) || value.ValueKind==JsonValueKind.Null)return fallback;
            if(value.ValueKind!=JsonValueKind.Number || !value.TryGetInt32(out int result) || result<minimum || result>maximum)throw new McpApplicationException("INVALID_ARGUMENT",$"{name} 必须为 {minimum} 至 {maximum} 的整数。");
            return result;
        }
        static bool McpBool(JsonElement args,string name,bool fallback)
        {
            if(!args.TryGetProperty(name,out var value) || value.ValueKind==JsonValueKind.Null)return fallback;
            if(value.ValueKind!=JsonValueKind.True && value.ValueKind!=JsonValueKind.False)throw new McpApplicationException("INVALID_ARGUMENT","参数必须为布尔值："+name);
            return value.GetBoolean();
        }
        static ExportFormat McpExportFormat(string value)=>value.ToLowerInvariant() switch
        {
            "html"=>ExportFormat.Html,"markdown" or "md"=>ExportFormat.Markdown,"text" or "txt"=>ExportFormat.Text,"pdf"=>ExportFormat.Pdf,
            "images"=>ExportFormat.Images,"audio"=>ExportFormat.Audio,"video"=>ExportFormat.Video,"all_media" or "allmedia"=>ExportFormat.AllMedia,"full"=>ExportFormat.Full,
            _=>throw new McpApplicationException("INVALID_ARGUMENT","不支持的导出格式。")
        };
        static string NormalizeDestination(string value)
        {
            if(string.IsNullOrWhiteSpace(value))return "";
            if(!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\?",StringComparison.Ordinal) || value.StartsWith(@"\\.\",StringComparison.Ordinal))
                throw new McpApplicationException("INVALID_DESTINATION","请提供普通的绝对目录路径。");
            try { string path=Path.GetFullPath(value); if(path.Length>165)throw new ArgumentException(); return path; }
            catch(Exception ex) when(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            { throw new McpApplicationException("INVALID_DESTINATION","导出目录无效或过长。"); }
        }
        static string McpResultText(string value)
        {
            if(string.IsNullOrEmpty(value))return "";
            if(value.Contains("http",StringComparison.OrdinalIgnoreCase) || value.Contains("cookie",StringComparison.OrdinalIgnoreCase) || value.Contains("pass_ticket",StringComparison.OrdinalIgnoreCase))
                return "该项未完成，请查看 WCAE 日志或文章状态。";
            return value.Length>500?value.Substring(0,500):value;
        }
        sealed record McpReadLease(McpQueryService Queries,int Epoch);
        sealed class InlineMcpProgress<T> : IProgress<T>
        {
            readonly Action<T> report;
            internal InlineMcpProgress(Action<T> report) { this.report=report; }
            public void Report(T value)=>report(value);
        }
    }
}
