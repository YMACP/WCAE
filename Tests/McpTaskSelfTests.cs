using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    public static class McpTaskSelfTests
    {
        public static async Task RunAsync()
        {
            await IdempotentAndConcurrentAcceptance();
            await CancellationWaitingAndShutdown();
            await PersistenceRestartAndPrivacy();
            await LargeResultsRetainSummaryAndDeclareTruncation();
            await BoundedHistoryAndClear();
            FailedAcceptanceLeavesNoTask();
        }
        static async Task IdempotentAndConcurrentAcceptance()
        {
            using var fixture=new Fixture(); var registry=new McpTaskRegistry(fixture.Path);
            var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls=0,events=0;
            registry.Changed+=info=> { _=registry.Get(info.Id); Interlocked.Increment(ref events); };
            registry.Changed+=_=>throw new InvalidOperationException("observer failure");
            Func<McpTaskContext,CancellationToken,Task<object>> worker=async(context,token)=>
            { Interlocked.Increment(ref calls); context.Report("准备中",0,3); await release.Task.WaitAsync(token); context.Report("完成",3,3); return new { processed=3 }; };
            var accepted=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Task.Run(()=>registry.Start("collection","biz","same-request","arguments",worker))));
            string id=accepted[0].Id;
            Check(accepted.All(info=>info.Id==id) && registry.List().Count==1,"并发重复提交未复用同一个任务");
            Expect("REQUEST_CONFLICT",()=>registry.Start("collection","biz","same-request","changed",worker));
            Expect("REQUEST_CONFLICT",()=>registry.Start("export","biz","same-request","arguments",worker));
            Expect("BUSY",()=>registry.Start("collection","biz","other-request","arguments",worker));
            var export=registry.Start("export","biz","export-request","export",async(c,t)=> { await release.Task.WaitAsync(t); return new { ok=true }; });
            Check(export.Kind=="export","独立类型无法并行接受");
            accepted[0].Message="mutated outside";
            Check(registry.Get(id).Message!="mutated outside","查询结果泄漏可变内部状态");
            release.TrySetResult(true); await Task.WhenAll(registry.Completion(id),registry.Completion(export.Id));
            Check(calls==1 && events>0 && registry.Get(id).State=="completed" && registry.Get(id).Processed==3,"任务重复运行、事件干扰业务或完成状态错误");
            Check(registry.Start("collection","biz","same-request","arguments",worker).Id==id && calls==1,"已完成请求被重复执行");
            await registry.ShutdownAsync();
        }
        static async Task CancellationWaitingAndShutdown()
        {
            using var fixture=new Fixture(); var registry=new McpTaskRegistry(fixture.Path);
            var waiting=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); bool cancelled=false;
            var task=registry.Start("export","biz","wait","export",async(context,token)=>
            {
                context.SetWaitingForDestination(); waiting.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite,token); } catch(OperationCanceledException) { cancelled=true; throw; }
                return null;
            });
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(registry.Get(task.Id).State=="waiting","选择位置期间没有waiting状态");
            Expect("TASK_NOT_FOUND",()=>registry.Cancel("not-a-task","export"));
            Expect("TASK_KIND_MISMATCH",()=>registry.Cancel(task.Id,"collection"));
            Check(registry.Get(task.Id).State=="waiting" && !cancelled,"错误任务取消影响了真实导出");
            registry.Cancel(task.Id,"export"); await registry.Completion(task.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Check(cancelled && registry.Get(task.Id).State=="cancelled","显式取消没有结束等待任务");
            Check(registry.Cancel(task.Id,"export").State=="cancelled","终态取消不幂等");
            McpTaskContext oldContext=null;
            var dismissed=registry.Start("export","biz","dismiss-picker","export",(context,token)=>
            { oldContext=context; context.SetWaitingForDestination(); throw new McpApplicationException("CANCELLED","用户取消保存位置选择。"); });
            await registry.Completion(dismissed.Id);
            Check(registry.Get(dismissed.Id).State=="cancelled","目录选择被用户取消后误记任务失败");
            registry.ClearCompleted(); oldContext.Report("迟到进度",1); oldContext.SetRunning();
            Check(registry.List().Count==0,"迟到上下文复活了清空的历史任务");
            var timeout=registry.Start("export","biz","unowned-cancellation","a",(c,t)=>throw new OperationCanceledException());
            await registry.Completion(timeout.Id);
            Check(registry.Get(timeout.Id).State=="failed","非用户取消的OCE被误报成已取消");
            var second=registry.Start("collection","biz","shutdown","collect",async(c,t)=> { await Task.Delay(Timeout.Infinite,t); return null; });
            await registry.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(registry.Get(second.Id).State=="cancelled","关闭没有等待业务退出");
            Expect("SHUTTING_DOWN",()=>registry.Start("export","biz","after-shutdown","a",(c,t)=>Task.FromResult<object>(null)));
        }
        static async Task PersistenceRestartAndPrivacy()
        {
            using var fixture=new Fixture(); var registry=new McpTaskRegistry(fixture.Path);
            var completed=registry.Start("export","biz","privacy","cookie=private-fingerprint",(context,token)=>
            {
                context.Report("cookie=private-progress",1,1);
                return Task.FromResult<object>(new { count=1,cookie="private-cookie",html="<p>private-html</p>",nested=new { access_token="private-token",ok="kept" } });
            });
            await registry.Completion(completed.Id);
            string data=File.ReadAllText(System.IO.Path.Combine(fixture.Path,"mcp-tasks.json"));
            Check(!data.Contains("private-") && data.Contains("kept"),"任务历史保存了cookie/正文/令牌或原始fingerprint");
            var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var interrupted=registry.Start("collection","biz","interrupt","a",async(c,t)=> { c.SetWaitingForDestination(); entered.TrySetResult(true); await Task.Delay(Timeout.Infinite,t); return null; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string activeSnapshot=File.ReadAllText(System.IO.Path.Combine(fixture.Path,"mcp-tasks.json"));
            await registry.ShutdownAsync();
            File.WriteAllText(System.IO.Path.Combine(fixture.Path,"mcp-tasks.json"),activeSnapshot);
            var resumed=new McpTaskRegistry(fixture.Path);
            Check(resumed.Get(interrupted.Id).State=="interrupted" && resumed.Completion(interrupted.Id).IsCompleted,"重启后把旧活动任务当作仍在运行");
            Check(resumed.Get(completed.Id).State=="completed" && resumed.Get(completed.Id).Result is JsonElement,"完成结果没有持久化");
            int reruns=0;
            Check(resumed.Start("collection","biz","interrupt","a",(c,t)=> { reruns++; return Task.FromResult<object>(null); }).Id==interrupted.Id && reruns==0,"重启后重复requestId丢失幂等性");
            var failure=resumed.Start("export","biz","failure","b",(c,t)=>throw new IOException("cookie=private-exception"));
            await resumed.Completion(failure.Id);
            Check(resumed.Get(failure.Id).State=="failed" && !resumed.Get(failure.Id).Message.Contains("private-exception"),"业务异常没有被记录或泄漏原异常内容");
            Check(!Directory.GetFiles(fixture.Path,"*.tmp").Any(),"原子写残留临时任务文件");
            await resumed.ShutdownAsync();
        }
        static async Task BoundedHistoryAndClear()
        {
            using var fixture=new Fixture(); var registry=new McpTaskRegistry(fixture.Path);
            for(int i=0;i<165;i++)
            {
                var task=registry.Start("collection","biz","bounded-"+i,"a",(c,t)=>Task.FromResult<object>(new { ok=true }));
                await registry.Completion(task.Id);
            }
            Check(registry.List().Count==160,"任务历史未按上限淘汰旧终态");
            var active=registry.Start("export","biz","keep-active","a",async(c,t)=> { await Task.Delay(Timeout.Infinite,t); return null; });
            registry.ClearCompleted();
            Check(registry.List().Count==1 && registry.Get(active.Id).Kind=="export","清理终态误删活动任务");
            await registry.ShutdownAsync(); registry.ClearCompleted();
            Check(new McpTaskRegistry(fixture.Path).List().Count==0,"清空任务历史未持久化");
        }
        static async Task LargeResultsRetainSummaryAndDeclareTruncation()
        {
            using var fixture=new Fixture(); var registry=new McpTaskRegistry(fixture.Path);
            const string destination=@"E:\用户导出\公众号文章";
            foreach(bool longText in new[]{false,true})
            {
                var details=Enumerable.Range(0,60).Select(i=>new { title=longText?new string('文',600)+i:"音频"+i,
                    output_path=destination+"\\"+(longText?new string('路',350):"音频")+i+".mp3",state="saved" }).ToArray();
                var task=registry.Start("export","biz","large-results-"+longText,"a",(c,t)=>Task.FromResult<object>(new
                { destination,format="audio",processed=60,succeeded=56,failed=4,skipped=0,results=details,results_truncated=false }));
                await registry.Completion(task.Id);
                var result=(JsonElement)registry.Get(task.Id).Result;
                Check(registry.Get(task.Id).State=="completed" && result.GetProperty("destination").GetString()==destination
                    && result.GetProperty("format").GetString()=="audio" && result.GetProperty("processed").GetInt32()==60
                    && result.GetProperty("succeeded").GetInt32()==56 && result.GetProperty("failed").GetInt32()==4
                    && result.GetProperty("skipped").GetInt32()==0,"大结果裁剪丢失目的地、格式或业务总数");
                Check(result.GetProperty("truncated").GetBoolean() && result.GetProperty("results_truncated").GetBoolean()
                    && result.GetProperty("results_total").GetInt32()==60 && result.GetProperty("results").GetArrayLength()<=30,
                    "结果数组被静默裁剪、总数丢失或被原false标志覆盖");
                Check(JsonSerializer.SerializeToUtf8Bytes(result).Length<=16384,"长标题/路径结果超过16KiB上限");
                using var persisted=JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(fixture.Path,"mcp-tasks.json")));
                var stored=persisted.RootElement.EnumerateArray().Single(e=>e.GetProperty("Info").GetProperty("Id").GetString()==task.Id).GetProperty("Info").GetProperty("Result");
                Check(JsonSerializer.SerializeToUtf8Bytes(stored).Length<=16384 && stored.GetProperty("results_truncated").GetBoolean(),"磁盘任务结果未按实际上限与裁剪标志保存");
            }
            var scalarResult=Enumerable.Range(0,35).ToDictionary(i=>"extra_"+i,i=>(object)new string('附',2048));
            scalarResult["destination"]=destination; scalarResult["format"]="full"; scalarResult["processed"]=60;
            scalarResult["succeeded"]=56; scalarResult["failed"]=4; scalarResult["skipped"]=0; scalarResult["total"]=60;
            var summaryTask=registry.Start("export","biz","scalar-heavy","b",(c,t)=>Task.FromResult<object>(scalarResult));
            await registry.Completion(summaryTask.Id);
            var summary=(JsonElement)registry.Get(summaryTask.Id).Result;
            Check(summary.GetProperty("destination").GetString()==destination && summary.GetProperty("processed").GetInt32()==60
                && summary.GetProperty("total").GetInt32()==60 && summary.GetProperty("truncated").GetBoolean()
                && JsonSerializer.SerializeToUtf8Bytes(summary).Length<=16384,"大量额外标量挤掉了业务摘要或超出预算");
            var arrayTask=registry.Start("export","biz","root-array","c",(c,t)=>Task.FromResult<object>(Enumerable.Range(0,60).ToArray()));
            await registry.Completion(arrayTask.Id);
            var array=(JsonElement)registry.Get(arrayTask.Id).Result;
            Check(array.GetProperty("truncated").GetBoolean() && array.GetProperty("items_total").GetInt32()==60
                && array.GetProperty("items").GetArrayLength()==30,"顶层数组裁剪缺少可见标志或原始总数");
            await registry.ShutdownAsync();
            var reloaded=new McpTaskRegistry(fixture.Path);
            var restored=(JsonElement)reloaded.Get(summaryTask.Id).Result;
            Check(restored.GetProperty("destination").GetString()==destination && restored.GetProperty("truncated").GetBoolean(),"持久化重载丢失裁剪摘要");
            await reloaded.ShutdownAsync();
        }
        static void FailedAcceptanceLeavesNoTask()
        {
            using var fixture=new Fixture(); var registry=new McpTaskRegistry(fixture.Path);
            Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path,"mcp-tasks.json")); int runs=0;
            Expect("PERSISTENCE_ERROR",()=>registry.Start("collection","biz","disk-error","a",(c,t)=> { runs++; return Task.FromResult<object>(null); }));
            Check(runs==0 && registry.List().Count==0,"接受失败后仍启动业务或保留幽灵任务");
        }
        static void Check(bool ok,string message) { if(!ok)throw new InvalidOperationException("MCP task self-test: "+message); }
        static void Expect(string code,Action action)
        { try { action(); } catch(McpApplicationException ex) when(ex.Code==code) { return; } throw new InvalidOperationException("Expected MCP error "+code); }
        sealed class Fixture : IDisposable
        {
            internal string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"WCAE-mcp-task-tests-"+Guid.NewGuid().ToString("N"));
            public void Dispose()
            {
                string root=System.IO.Path.GetFullPath(Path),parent=System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                if(Directory.Exists(root) && System.IO.Path.GetDirectoryName(root).Equals(parent,StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFileName(root).StartsWith("WCAE-mcp-task-tests-",StringComparison.Ordinal))Directory.Delete(root,true);
            }
        }
    }
}
