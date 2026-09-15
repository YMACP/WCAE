using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    internal sealed class McpTaskInfo
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string State { get; set; } = "queued";
        public string Origin { get; set; } = "agent";
        public int Processed { get; set; }
        public int? Total { get; set; }
        public int Failed { get; set; }
        public string Message { get; set; } = "";
        public string ErrorCode { get; set; } = "";
        public object Result { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal sealed class McpTaskContext
    {
        readonly McpTaskRegistry registry;
        readonly string id;
        internal McpTaskContext(McpTaskRegistry registry,string id) { this.registry=registry; this.id=id; }
        public string TaskId => id;
        public void Report(string message,int processed,int? total=null,int failed=0)
            => registry.Update(id,info=> { info.Message=McpTaskRegistry.SafeText(message); info.Processed=Math.Max(0,processed); info.Total=total.HasValue?Math.Max(0,total.Value):null; info.Failed=Math.Max(0,failed); });
        public void SetWaitingForDestination() => registry.Update(id,info=> { info.State="waiting"; info.Message="等待用户选择保存位置。"; });
        public void SetRunning() => registry.Update(id,info=>info.State="running");
    }

    // Business jobs own their cancellation lifetime. HTTP request tokens never enter this class.
    internal sealed class McpTaskRegistry
    {
        const int MaximumTasks=160;
        readonly object gate=new object();
        readonly string path;
        readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
        bool shuttingDown;
        public event Action<McpTaskInfo> Changed;
        static readonly JsonSerializerOptions JsonOptions=new JsonSerializerOptions { WriteIndented=false,MaxDepth=24 };

        sealed class Entry
        {
            internal McpTaskInfo Info;
            internal string RequestId;
            internal string Fingerprint;
            internal CancellationTokenSource Cancellation;
            internal readonly TaskCompletionSource<bool> Finished=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public sealed class SavedTask
        {
            public McpTaskInfo Info { get; set; }
            public string RequestId { get; set; }
            public string Fingerprint { get; set; }
        }

        internal McpTaskRegistry(string directory)
        {
            Directory.CreateDirectory(directory); path=Path.Combine(Path.GetFullPath(directory),"mcp-tasks.json");
            if(!File.Exists(path))return;
            try
            {
                if(new FileInfo(path).Length>8*1024*1024)throw new JsonException("Task history exceeds limit");
                var stored=JsonSerializer.Deserialize<List<SavedTask>>(File.ReadAllText(path,Encoding.UTF8),JsonOptions)??new List<SavedTask>();
                foreach(var saved in stored.Where(s=>s?.Info!=null && !string.IsNullOrWhiteSpace(s.Info.Id)).OrderByDescending(s=>s.Info.CreatedAt).Take(MaximumTasks))
                {
                    var info=Copy(saved.Info); info.Message=SafeText(info.Message); info.Result=SafeResult(info.Result);
                    if(Active(info.State)) { info.State="interrupted"; info.Message="上次运行已中断，可重新提交任务。"; info.ErrorCode="INTERRUPTED"; info.UpdatedAt=DateTimeOffset.UtcNow; }
                    var entry=new Entry { Info=info,RequestId=saved.RequestId??"",Fingerprint=saved.Fingerprint??"" };
                    entry.Finished.TrySetResult(true); entries.TryAdd(info.Id,entry);
                }
                Persist();
            }
            catch(McpApplicationException) { throw; }
            catch(Exception ex) when(ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            { throw new McpApplicationException("TASK_HISTORY_ERROR","任务记录读取失败（"+ex.GetType().Name+"），原文件已保留。"); }
        }

        internal McpTaskInfo Start(string kind,string accountId,string requestId,string fingerprint,Func<McpTaskContext,CancellationToken,Task<object>> worker,string origin="agent")
        {
            if(kind!="collection" && kind!="export")throw new McpApplicationException("INVALID_ARGUMENT","任务类型必须是 collection 或 export。");
            if(worker==null)throw new ArgumentNullException(nameof(worker));
            if(string.IsNullOrWhiteSpace(requestId) || requestId.Length>128 || requestId.Any(char.IsControl))throw new McpApplicationException("INVALID_ARGUMENT","requestId 必须为 1 至 128 个可见字符。");
            string requestKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
            string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind+"\n"+(accountId??"")+"\n"+(fingerprint??""))));
            Entry entry; McpTaskInfo accepted;
            lock(gate)
            {
                var duplicate=entries.Values.FirstOrDefault(e=>e.RequestId==requestKey);
                if(duplicate!=null)
                {
                    if(duplicate.Fingerprint!=digest)throw new McpApplicationException("REQUEST_CONFLICT","同一 requestId 对应的任务参数不一致。");
                    return Copy(duplicate.Info);
                }
                if(shuttingDown)throw new McpApplicationException("SHUTTING_DOWN","程序正在关闭，暂不接受新任务。");
                if(entries.Values.Any(e=>e.Info.Kind==kind && Active(e.Info.State)))throw new McpApplicationException("BUSY","已有同类型任务正在运行。");
                var now=DateTimeOffset.UtcNow;
                entry=new Entry { RequestId=requestKey,Fingerprint=digest,Cancellation=new CancellationTokenSource(),
                    Info=new McpTaskInfo { Id=Guid.NewGuid().ToString("N"),Kind=kind,AccountId=accountId??"",Origin=origin=="ui"?"ui":"agent",CreatedAt=now,UpdatedAt=now } };
                var removed=Prune(); entries.Add(entry.Info.Id,entry);
                try { Persist(); }
                catch { entries.Remove(entry.Info.Id); foreach(var old in removed)entries.Add(old.Info.Id,old); entry.Cancellation.Dispose(); throw; }
                accepted=Copy(entry.Info);
            }
            // Observers see acceptance before any progress from the background worker.
            Publish(accepted); _=Task.Run(()=>ExecuteAsync(entry,worker)); return accepted;
        }

        internal McpTaskInfo FindDuplicate(string kind,string accountId,string requestId,string fingerprint)
        {
            if(string.IsNullOrWhiteSpace(requestId) || requestId.Length>128 || requestId.Any(char.IsControl))throw new McpApplicationException("INVALID_ARGUMENT","requestId 必须为 1 至 128 个可见字符。");
            string requestKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
            string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind+"\n"+(accountId??"")+"\n"+(fingerprint??""))));
            lock(gate)
            {
                var duplicate=entries.Values.FirstOrDefault(e=>e.RequestId==requestKey);
                if(duplicate==null)return null;
                if(duplicate.Fingerprint!=digest)throw new McpApplicationException("REQUEST_CONFLICT","同一 requestId 对应的任务参数不一致。");
                return Copy(duplicate.Info);
            }
        }
        internal McpTaskInfo Get(string taskId) { lock(gate)return Copy(Require(taskId).Info); }
        internal IReadOnlyList<McpTaskInfo> List() { lock(gate)return entries.Values.OrderByDescending(e=>e.Info.CreatedAt).Select(e=>Copy(e.Info)).ToArray(); }
        internal Task Completion(string taskId) { lock(gate)return Require(taskId).Finished.Task; }
        internal McpTaskInfo Cancel(string taskId,string expectedKind)
        {
            Entry entry; McpTaskInfo snapshot;
            lock(gate)
            {
                entry=Require(taskId);
                if(entry.Info.Kind!=expectedKind)throw new McpApplicationException("TASK_KIND_MISMATCH","任务类型不匹配，未停止其他任务。");
                if(!Active(entry.Info.State))return Copy(entry.Info);
                var before=Copy(entry.Info); entry.Info.State="cancelling"; entry.Info.Message="正在停止任务。"; entry.Info.UpdatedAt=DateTimeOffset.UtcNow;
                try { Persist(); } catch { entry.Info=before; throw; }
                snapshot=Copy(entry.Info);
            }
            try { entry.Cancellation?.Cancel(); } catch(ObjectDisposedException) { } catch(AggregateException) { }
            Publish(snapshot); return snapshot;
        }
        internal async Task ShutdownAsync()
        {
            Entry[] active;
            lock(gate) { shuttingDown=true; active=entries.Values.Where(e=>Active(e.Info.State)).ToArray(); }
            // Cancel outside the registry lock: token callbacks may report final business progress.
            foreach(var entry in active)try { entry.Cancellation?.Cancel(); } catch(ObjectDisposedException) { } catch(AggregateException) { }
            await Task.WhenAll(active.Select(e=>e.Finished.Task)).ConfigureAwait(false);
        }
        internal void ClearCompleted()
        {
            lock(gate)
            {
                var removed=entries.Values.Where(e=>!Active(e.Info.State)).ToArray();
                foreach(var e in removed)entries.Remove(e.Info.Id);
                try { Persist(); } catch { foreach(var e in removed)entries.Add(e.Info.Id,e); throw; }
            }
        }
        internal void Update(string id,Action<McpTaskInfo> update)
        {
            McpTaskInfo snapshot;
            lock(gate)
            {
                // Posted UI progress can arrive after a completed job was cleared or pruned.
                // Its context must never revive history or throw on the UI message loop.
                if(!entries.TryGetValue(id,out var entry))return;
                if(!Active(entry.Info.State) || entry.Info.State=="cancelling")return;
                var before=Copy(entry.Info); update(entry.Info); entry.Info.UpdatedAt=DateTimeOffset.UtcNow;
                try { Persist(); } catch { entry.Info=before; throw; }
                snapshot=Copy(entry.Info);
            }
            Publish(snapshot);
        }
        async Task ExecuteAsync(Entry entry,Func<McpTaskContext,CancellationToken,Task<object>> worker)
        {
            string state="completed",code="",message="任务已完成。"; object result=null;
            try
            {
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                Update(entry.Info.Id,i=>i.State="running");
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                result=SafeResult(await worker(new McpTaskContext(this,entry.Info.Id),entry.Cancellation.Token).ConfigureAwait(false));
                entry.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch(OperationCanceledException) when(entry.Cancellation.IsCancellationRequested)
            { state="cancelled"; code="CANCELLED"; message="任务已停止，已完成的结果和断点保留。"; }
            catch(McpApplicationException ex) when(ex.Code=="CANCELLED")
            { state="cancelled"; code="CANCELLED"; message=SafeText(ex.Message); }
            catch(McpApplicationException ex) { state="failed"; code=ex.Code; message=SafeText(ex.Message); }
            catch(Exception ex) { state="failed"; code="TASK_FAILED"; message="任务未完成（"+ex.GetType().Name+"）。"; }
            McpTaskInfo snapshot;
            lock(gate)
            {
                // Cancellation can race with a successful worker return. Explicit stop wins.
                if(entry.Cancellation.IsCancellationRequested && state=="completed")
                { state="cancelled"; code="CANCELLED"; message="任务已停止，已完成的结果保留。"; }
                entry.Info.State=state; entry.Info.ErrorCode=code; entry.Info.Message=message; entry.Info.Result=result; entry.Info.UpdatedAt=DateTimeOffset.UtcNow;
                try { Persist(); }
                catch(McpApplicationException) { entry.Info.State="failed"; entry.Info.ErrorCode="PERSISTENCE_ERROR"; entry.Info.Message="任务已结束，但任务记录保存失败；业务结果保留。"; }
                snapshot=Copy(entry.Info);
                entry.Cancellation.Dispose();
            }
            entry.Finished.TrySetResult(true); Publish(snapshot);
        }
        Entry Require(string id) => id!=null && entries.TryGetValue(id,out var entry)?entry:throw new McpApplicationException("TASK_NOT_FOUND","未找到指定任务。");
        static bool Active(string state) => state=="queued" || state=="running" || state=="waiting" || state=="cancelling";
        List<Entry> Prune()
        {
            var removed=entries.Values.Where(e=>!Active(e.Info.State)).OrderBy(e=>e.Info.CreatedAt).Take(Math.Max(0,entries.Count-MaximumTasks+1)).ToList();
            foreach(var entry in removed)entries.Remove(entry.Info.Id); return removed;
        }
        void Persist()
        {
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                var records=entries.Values.Select(e=>new SavedTask { Info=e.Info,RequestId=e.RequestId,Fingerprint=e.Fingerprint }).ToArray();
                byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(records,JsonOptions);
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
                if(File.Exists(path))File.Replace(temporary,path,null); else File.Move(temporary,path);
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            { throw new McpApplicationException("PERSISTENCE_ERROR","任务记录保存失败（"+ex.GetType().Name+"）。"); }
            finally { try { if(File.Exists(temporary))File.Delete(temporary); } catch(IOException) { } catch(UnauthorizedAccessException) { } }
        }
        void Publish(McpTaskInfo info)
        {
            var handlers=Changed; if(handlers==null)return;
            foreach(Action<McpTaskInfo> callback in handlers.GetInvocationList())try { callback(Copy(info)); } catch { /* UI observers cannot terminate business jobs. */ }
        }
        static McpTaskInfo Copy(McpTaskInfo i) => new McpTaskInfo { Id=i.Id,Kind=i.Kind,AccountId=i.AccountId,State=i.State,Origin=i.Origin,
            Processed=i.Processed,Total=i.Total,Failed=i.Failed,Message=i.Message,ErrorCode=i.ErrorCode,Result=i.Result is JsonElement json?json.Clone():i.Result,CreatedAt=i.CreatedAt,UpdatedAt=i.UpdatedAt };
        internal static string SafeText(string value)
        {
            string text=value??"";
            if(text.IndexOf("<html",StringComparison.OrdinalIgnoreCase)>=0 || text.IndexOf("<script",StringComparison.OrdinalIgnoreCase)>=0)return "[页面内容已省略]";
            if(text.Length>2048)text=text.Substring(0,2048);
            text=Regex.Replace(text,@"(?i)(cookie|authorization|password|passwd|pass_ticket|appmsg_token|access_token|secret|uin|key)\s*[:=]\s*[^\s,;]+","$1=[已省略]",RegexOptions.None,TimeSpan.FromMilliseconds(200));
            return text;
        }
        static object SafeResult(object value)
        {
            if(value==null)return null;
            try
            {
                var element=value is JsonElement existing?existing:JsonSerializer.SerializeToElement(value,JsonOptions);
                object sanitized=null;
                // Reduce detail arrays before touching the business summary. Every shortened
                // array carries a visible flag; the source's false flag cannot overwrite it.
                foreach(int arrayLimit in new[]{30,20,10,5,2,1,0})
                {
                    var state=new ResultTruncation(); sanitized=Sanitize(element,0,arrayLimit,state);
                    if(state.Truncated)
                    {
                        if(sanitized is Dictionary<string,object> fields)fields["truncated"]=true;
                        else sanitized=new Dictionary<string,object> { ["items"]=sanitized,["truncated"]=true,
                            ["items_total"]=element.ValueKind==JsonValueKind.Array?element.GetArrayLength():null };
                    }
                    byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(sanitized,JsonOptions);
                    if(bytes.Length<=16384)return JsonSerializer.Deserialize<JsonElement>(bytes,JsonOptions).Clone();
                }
                // Pathological scalar-heavy results still retain destination, format and
                // counts. Extra detail is omitted explicitly instead of replacing everything.
                var summary=sanitized as Dictionary<string,object> ?? new Dictionary<string,object> { ["value"]=sanitized };
                foreach(string key in summary.Keys.ToArray())
                {
                    if(summary[key] is Dictionary<string,object>)summary.Remove(key);
                    else if(summary[key] is string text && text.Length>256)summary[key]=text.Substring(0,256);
                }
                summary["truncated"]=true;
                byte[] reduced=JsonSerializer.SerializeToUtf8Bytes(summary,JsonOptions);
                foreach(string key in summary.Keys.Where(k=>!SummaryKeys.Contains(k)).Reverse().ToArray())
                {
                    if(reduced.Length<=16384)break;
                    summary.Remove(key); reduced=JsonSerializer.SerializeToUtf8Bytes(summary,JsonOptions);
                }
                if(reduced.Length>16384)
                {
                    foreach(string key in summary.Keys.ToArray())if(summary[key] is string text && text.Length>128)summary[key]=text.Substring(0,128);
                    summary=summary.GroupBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First().Value,StringComparer.OrdinalIgnoreCase);
                    reduced=JsonSerializer.SerializeToUtf8Bytes(summary,JsonOptions);
                }
                return JsonSerializer.Deserialize<JsonElement>(reduced,JsonOptions).Clone();
            }
            catch(Exception ex) when(ex is JsonException || ex is NotSupportedException || ex is InvalidOperationException)
            { return JsonSerializer.SerializeToElement(new { message="结果摘要无法序列化，请查看程序界面。",truncated=true }); }
        }
        static readonly HashSet<string> SummaryKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "destination","format","processed","succeeded","failed","skipped","total","count","articles_processed","results_total","items_total","results_truncated","truncated" };
        sealed class ResultTruncation { internal bool Truncated; internal int Nodes; }
        static object Sanitize(JsonElement element,int depth,int arrayLimit,ResultTruncation state)
        {
            if(depth>6 || ++state.Nodes>4096) { state.Truncated=true; return null; }
            switch(element.ValueKind)
            {
                case JsonValueKind.Object:
                    var fields=new Dictionary<string,object>();
                    var properties=element.EnumerateObject().ToArray();
                    if(properties.Length>40)state.Truncated=true;
                    var shortenedArrays=new Dictionary<string,int>();
                    foreach(var p in properties.OrderByDescending(p=>depth==0 && SummaryKeys.Contains(p.Name)).Take(40))
                    {
                        string name=p.Name.ToLowerInvariant();
                        if(name.Contains("cookie") || name.Contains("token") || name.Contains("password") || name.Contains("secret") || name.Contains("authorization") || name.Contains("html") || name.Contains("header") || name=="body" || name=="session" || name=="key" || name=="uin" || name=="pass_ticket")continue;
                        string key=p.Name.Length>128?p.Name.Substring(0,128):p.Name;
                        if(p.Name.Length>128)state.Truncated=true;
                        fields[key]=Sanitize(p.Value,depth+1,arrayLimit,state);
                        if(p.Value.ValueKind==JsonValueKind.Array && p.Value.GetArrayLength()>arrayLimit)shortenedArrays[key]=p.Value.GetArrayLength();
                    }
                    foreach(var pair in shortenedArrays)
                    {
                        fields[pair.Key+"_truncated"]=true;
                        // A worker may already provide the pre-pagination total; preserve it.
                        if(!fields.ContainsKey(pair.Key+"_total"))fields[pair.Key+"_total"]=pair.Value;
                    }
                    return fields;
                case JsonValueKind.Array:
                    if(element.GetArrayLength()>arrayLimit)state.Truncated=true;
                    return element.EnumerateArray().Take(arrayLimit).Select(v=>Sanitize(v,depth+1,arrayLimit,state)).ToArray();
                case JsonValueKind.String:
                    if((element.GetString()?.Length??0)>2048)state.Truncated=true;
                    return SafeText(element.GetString());
                case JsonValueKind.Number:
                    if(element.GetRawText().Length>128) { state.Truncated=true; return null; }
                    return element.Clone();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }
}
