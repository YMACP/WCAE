using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public static class CollectionLimitSelfTests
    {
        const string Biz="collection-limit-test";
        public static async Task RunAsync()
        {
            await LimitPreservesQueueAndPageCursor();
            await FailedAndDeletedCountOncePerArticle();
            await LegacyAndInvalidLimits();
            await ExplicitStopAtLimitStillCancels();
        }
        static async Task LimitPreservesQueueAndPageCursor()
        {
            using var fixture=new Fixture(); var repo=fixture.Repository;
            repo.BeginCollectionRun(Biz); repo.SaveCollectionPage(Biz,new[]{Article(1),Article(2),Article(3)},0,7,false);
            var transport=new Transport((url,token)=>
            {
                Check(HttpUtility.ParseQueryString(new Uri(url).Query)["offset"]=="7","续收重置到了第一页");
                return Task.FromResult(Page(20,4));
            });
            var details=new List<string>();
            using(var first=Controller(repo,transport,(a,s,t)=> { details.Add(a.Mid); return Task.FromResult(Available(a)); }))
            {
                await first.StartAsync(null,maxArticles:2);
                var checkpoint=repo.GetCollectionCheckpoint(Biz);
                Check(details.SequenceEqual(new[]{"1","2"}) && transport.Calls==0 && repo.PendingCount(Biz)==1,"限额后继续请求下一篇或下一页");
                Check(checkpoint.NextOffset==7 && !checkpoint.HistoryComplete && !checkpoint.RunCompleted && checkpoint.LastCompletedArticleId==Article(2).Id,"限额伪造完成或破坏分页/逐篇断点");
                Check(first.LastProcessedCount==2 && first.LastSucceededCount==2 && first.LastFailedCount==0 && first.LastSummary.Contains("数量上限"),"完成计数或限额结果不明确");
            }
            repo=new ArticleRepository(fixture.Path);
            using(var resumed=Controller(repo,transport,(a,s,t)=> { details.Add(a.Mid); return Task.FromResult(Available(a)); }))
            {
                await resumed.StartAsync(null,1);
                Check(details.SequenceEqual(new[]{"1","2","3"}) && transport.Calls==0 && resumed.LastProcessedCount==1,"重启后重收已完成文章或计数未按轮重置");
                await resumed.StartAsync(null);
                Check(details.SequenceEqual(new[]{"1","2","3","4"}) && transport.Calls==1 && repo.PendingCount(Biz)==0,"不限量续收未从剩余页完成");
                Check(repo.GetCollectionCheckpoint(Biz).RunCompleted && resumed.LastProcessedCount==1,"真实末页未完成或计数累计到上一轮");
            }
        }
        static async Task FailedAndDeletedCountOncePerArticle()
        {
            using var fixture=new Fixture(); var repo=fixture.Repository;
            repo.BeginCollectionRun(Biz); repo.SaveCollectionPage(Biz,new[]{Article(1),Article(2),Article(3)},0,9,false);
            var details=new List<string>();
            using var controller=Controller(repo,new Transport((u,t)=>throw new InvalidOperationException("限量不应翻页")),(a,s,t)=>
            {
                details.Add(a.Mid);
                if(a.Mid=="1") { a.Status=ArticleStatus.Failed; a.StatusDetail="fixture failed"; }
                else a.Status=ArticleStatus.Deleted;
                return Task.FromResult(a);
            });
            await controller.StartAsync(null,2);
            Check(details.SequenceEqual(new[]{"1","1","2"}),"失败重试被重复计入限额或限额后仍读取第三篇");
            Check(controller.LastProcessedCount==2 && controller.LastSucceededCount==0 && controller.LastDeletedCount==1 && controller.LastFailedCount==1,"失败/删除终态计数不可靠");
            Check(repo.PendingCount(Biz)==2 && !repo.GetCollectionCheckpoint(Biz).RunCompleted,"限额清除了失败或未处理待办");
        }
        static async Task LegacyAndInvalidLimits()
        {
            int visited=0; var saved=new Dictionary<string,ArticleRecord>();
            using var controller=new CollectionController(async(s,accept,p,t)=>
            {
                for(int i=1;i<=3;i++) { visited++; await accept(Article(i),t); }
            },(a,s,t)=>Task.FromResult(Available(a)),a=>saved[a.Id]=a);
            controller.Recognize(Session());
            try { await controller.StartAsync(null,0); throw new InvalidOperationException("零限额未拒绝"); }
            catch(ArgumentOutOfRangeException) { }
            Check(visited==0 && !controller.IsRunning,"非法限额仍启动收集");
            await controller.StartAsync(null,1);
            Check(visited==1 && saved.Count==1 && saved.Values.Single().Status==ArticleStatus.Available && controller.LastProcessedCount==1,"旧委托构造不支持数量上限或未先保存终态");
        }
        static async Task ExplicitStopAtLimitStillCancels()
        {
            using var fixture=new Fixture(); var repo=fixture.Repository;
            repo.BeginCollectionRun(Biz); repo.SaveCollectionPage(Biz,new[]{Article(1),Article(2)},0,9,false);
            using var controller=Controller(repo,new Transport((u,t)=>throw new InvalidOperationException("不应翻页")),(a,s,t)=>Task.FromResult(Available(a)));
            controller.ArticleSaved+=a=> { if(a.Status==ArticleStatus.Available)controller.Stop(); };
            try { await controller.StartAsync(null,1); throw new InvalidOperationException("显式停止被限额吞掉"); }
            catch(OperationCanceledException) { }
            Check(controller.LastProcessedCount==1 && repo.PendingCount(Biz)==1,"完成后显式停止的计数或待办错误");
        }
        static ArticleRecord Article(int mid)=>new ArticleRecord { Id=Biz+":"+mid+":1",Biz=Biz,Mid=mid.ToString(),Idx=1,Title="文章"+mid,
            Url="https://mp.weixin.qq.com/s?__biz="+Biz+"&mid="+mid+"&idx=1",Status=ArticleStatus.Pending };
        static ArticleRecord Available(ArticleRecord a) { a.Status=ArticleStatus.Available; a.Html="<div id='js_content'>完整正文</div>"; return a; }
        static AccountSession Session()=>new AccountSession { Biz=Biz,Name="限额测试号",Uin="test",Key="test",Cookie="test=fixture" };
        static string Page(int next,int mid)=>new JObject { ["ret"]=0,["can_msg_continue"]=0,["next_offset"]=next,
            ["general_msg_list"]=new JObject { ["list"]=new JArray(new JObject { ["comm_msg_info"]=new JObject { ["id"]=mid,["datetime"]=1700000000 },
                ["app_msg_ext_info"]=new JObject { ["title"]="文章"+mid,["content_url"]=Article(mid).Url } }) }.ToString(Formatting.None) }.ToString(Formatting.None);
        static CollectionController Controller(ArticleRepository repo,Transport transport,Func<ArticleRecord,AccountSession,CancellationToken,Task<ArticleRecord>> enrich)
        {
            var history=new HistoryCollector(transport) { MinimumDelay=TimeSpan.Zero,MaximumDelay=TimeSpan.Zero,RetryDelay=TimeSpan.Zero };
            var controller=new CollectionController(history,enrich,repo) { DetailRetryDelay=TimeSpan.Zero,MaximumDetailAttempts=2 };
            controller.Recognize(Session()); return controller;
        }
        static void Check(bool value,string message) { if(!value)throw new InvalidOperationException("Collection limit self-test: "+message); }
        sealed class Transport : IHttpTransport
        {
            readonly Func<string,CancellationToken,Task<string>> get;
            internal int Calls;
            internal Transport(Func<string,CancellationToken,Task<string>> get)=>this.get=get;
            public Task<string> GetStringAsync(string url,AccountSession session,CancellationToken token) { Calls++; return get(url,token); }
            public Task<string> PostFormAsync(string url,IDictionary<string,string> values,AccountSession session,CancellationToken token)=>throw new NotSupportedException();
            public Task DownloadAsync(string url,string destination,AccountSession session,CancellationToken token)=>throw new NotSupportedException();
        }
        sealed class Fixture : IDisposable
        {
            internal string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"WCAE-collection-limit-tests-"+Guid.NewGuid().ToString("N"));
            internal ArticleRepository Repository { get; }
            internal Fixture()=>Repository=new ArticleRepository(Path);
            public void Dispose()
            {
                string root=System.IO.Path.GetFullPath(Path),parent=System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                if(Directory.Exists(root) && System.IO.Path.GetDirectoryName(root).Equals(parent,StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFileName(root).StartsWith("WCAE-collection-limit-tests-",StringComparison.Ordinal))Directory.Delete(root,true);
            }
        }
    }
}
