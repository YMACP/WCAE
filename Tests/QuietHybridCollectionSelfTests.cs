using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public static class QuietHybridCollectionSelfTests
    {
        const string Biz="quiet-fixture";
        public static async Task RunAsync()
        {
            await SupplementsDeduplicateAndDoNotRefreshCompletedBodies();
            await ExistingQueueAndCanonicalAliasesSurviveSupplementImport();
            SameMessageChildrenKeepNumericOrder();
            await NativeCompletionReusesBodyFromLegacyPending();
            await SourceFailureAndForeignSnapshotLeaveHistoryUsable();
            await StopAndResumeKeepSupplementAndHistoryCursorsIndependent();
            await OnlySameAccountObservationUpdatesRunningSession();
            await StopAfterDetailReturnsPreventsSave();
            await SupplementCancellationDoesNotBecomeFallback();
            await CancellationAfterWriterWaitRollsBackEveryMutation();
        }

        static void SameMessageChildrenKeepNumericOrder()
        {
            using var fixture=new Fixture(); var second=Article("250"); var tenth=Article("250");
            second.Idx=2; second.Id=Biz+":250:2"; second.Url=second.Url.Replace("idx=1","idx=2");
            tenth.Idx=10; tenth.Id=Biz+":250:10"; tenth.Url=tenth.Url.Replace("idx=1","idx=10");
            fixture.Repository.BeginCollectionRun(Biz);
            fixture.Repository.SaveCollectionSupplement(Biz,new[]{tenth,second});
            Check(fixture.Repository.GetPendingArticles(Biz).Select(a=>a.Idx).SequenceEqual(new[]{2,10}),"同消息的多图文子序号被字典序打乱");
        }

        static async Task SupplementsDeduplicateAndDoNotRefreshCompletedBodies()
        {
            using var fixture=new Fixture();
            var complete=Available(Article("101")); fixture.Repository.Save(complete);
            var deleted=Article("102"); deleted.Status=ArticleStatus.Deleted; fixture.Repository.Save(deleted);
            var source=new Source((session,token)=>Task.FromResult(Snapshot(Article("104"),Article("101"),Article("103"),Article("103"),Article("102"))));
            var requests=new List<int>(); var details=new List<string>();
            var transport=new Transport((url,session,token)=>
            { requests.Add(Offset(url)); return Task.FromResult(Page(false,10,"101","102","103","105")); });
            using var controller=Controller(fixture.Repository,transport,source,(article,session,token)=>
            { details.Add(article.Mid); return Task.FromResult(Available(article)); });
            var events=new List<(string Mid,ArticleStatus Status)>();
            controller.ArticleSaved+=a=>events.Add((a.Mid,a.Status));
            Check(source.Calls==0,"构造或识别自动读取了缓存");
            await controller.StartAsync(null);
            Check(details.SequenceEqual(new[]{"104","103","105"}),"补充列表排序、两来源去重或现有终态复用失败");
            Check(events.Where(e=>e.Status==ArticleStatus.Pending).Select(e=>e.Mid).SequenceEqual(details),"缓存待办在进入当前篇之前被批量通知为Pending");
            foreach(string mid in details)
            {
                int pendingIndex=events.FindIndex(e=>e.Mid==mid && e.Status==ArticleStatus.Pending);
                Check(pendingIndex>=0 && pendingIndex+1<events.Count && events[pendingIndex+1]==(mid,ArticleStatus.Available),
                    "当前篇Pending后没有先完成当前正文就通知下一篇");
            }
            Check(fixture.Repository.Load(Biz).Count==5 && fixture.Repository.PendingCount(Biz)==0,"补漏没有正确保存/去重/完成");
            await controller.StartAsync(null);
            Check(source.Calls==2 && requests.SequenceEqual(new[]{0,0}) && details.Count==3,"新一轮重新下载了已有正文或已删除记录");
            Check(controller.LastSummary.Contains("接口可访问范围") && controller.LastSummary.Contains("不代表公众号全部"),"汇总错误宣称全公众号收齐");
        }

        static async Task ExistingQueueAndCanonicalAliasesSurviveSupplementImport()
        {
            using var fixture=new Fixture(); var repo=fixture.Repository;
            var first=Article("201"); var current=Article("202"); var next=Article("203");
            repo.BeginCollectionRun(Biz); repo.SaveCollectionPage(Biz,new[]{first,current,next},0,19,false);
            repo.MarkArticleStarted(Biz,first.Id); repo.FailArticle(first,"fixture bounded failure"); repo.DeferArticle(Biz,first.Id);
            repo.MarkArticleStarted(Biz,current.Id);
            string before=JsonConvert.SerializeObject(repo.GetCollectionCheckpoint(Biz));
            repo.SaveCollectionSupplement(Biz,new[]{Article("204"),Article("202")});
            Check(JsonConvert.SerializeObject(repo.GetCollectionCheckpoint(Biz))==before,"补充缓存改写了历史游标或当前文章");
            Check(repo.GetPendingWork(Biz).Single(a=>a.Id==first.Id).Attempts==1,"补充缓存清掉了旧失败次数");
            var original=new ArticleRecord { Biz=Biz,Url="https://mp.weixin.qq.com/s/quiet-alias",Mid="",Idx=1,
                ResolvedArticleId=Article("205").Id,Title="alias",Status=ArticleStatus.Available,Html="<p>existing alias body</p>" };
            original.Id=ArticleIdentity.Create(Biz,"",1,original.Url); repo.Save(original);
            repo.SaveCollectionSupplement(Biz,new[]{Article("205")});
            Check(repo.Load(Biz).Count==5 && repo.Load(Biz).Any(a=>a.Id==original.Id) && !repo.GetPendingWork(Biz).Any(a=>a.Id==original.Id),
                "补充 canonical 未归并已有显式证明短链，或重复排入已有正文");
            var order=new List<string>();
            using var controller=Controller(repo,new Transport((url,session,token)=>
            { Check(Offset(url)==19,"补充缓存重置了原接口offset"); return Task.FromResult(Page(false,24)); }),
                new Source((s,t)=>Task.FromResult(Snapshot(Article("206")))),(article,session,token)=>
            { order.Add(article.Mid); return Task.FromResult(Available(article)); });
            await controller.StartAsync(null);
            Check(order.SequenceEqual(new[]{"202","203","204","201","206"}),"旧待办恢复顺序被新缓存插队或重排");
        }

        static async Task NativeCompletionReusesBodyFromLegacyPending()
        {
            using var fixture=new Fixture(); var repo=fixture.Repository; var article=Article("301");
            repo.BeginCollectionRun(Biz); repo.SaveCollectionPage(Biz,new[]{article},0,11,false); repo.MarkArticleStarted(Biz,article.Id);
            repo.BeginNativeCollection(Biz,"gh_fixture");
            repo.SaveNativeDiscovered(Biz,article,new NativeArticleAnchor { ArticleId=article.Id,CanonicalId=article.Id });
            repo.CompleteNativeArticle(Available(article));
            Check(repo.PendingCount(Biz)==1,"测试未复现原生完成而旧待办仍存在的场景");
            int details=0;
            using var controller=Controller(repo,new Transport((url,session,token)=>
            { Check(Offset(url)==11 && repo.PendingCount(Biz)==0,"未先复用原生已完成正文，或沿用了错误来源offset"); return Task.FromResult(Page(false,20)); }),
                new Source((s,t)=>Task.FromResult(Snapshot())),(a,s,t)=> { details++; return Task.FromResult(Available(a)); });
            await controller.StartAsync(null);
            Check(details==0 && repo.GetCollectionCheckpoint(Biz).LastCompletedArticleId==article.Id
                && repo.GetNativeCollectionState(Biz).Phase=="Returning","旧待办重复下载原生已完成文章或破坏原生断点");
        }

        static async Task SourceFailureAndForeignSnapshotLeaveHistoryUsable()
        {
            foreach(bool foreign in new[]{false,true})
            {
                using var fixture=new Fixture(); var log=new List<string>();
                var source=new Source((session,token)=>foreign
                    ? Task.FromResult(Snapshot(Article("404"),Article("402","other-account")))
                    : Task.FromException<ArticleSupplementSnapshot>(new IOException("fixture details must not enter public logs")));
                using var controller=Controller(fixture.Repository,new Transport((u,s,t)=>Task.FromResult(Page(false,10,"403"))),source,(a,s,t)=>Task.FromResult(Available(a)));
                await controller.StartAsync(new ProgressSink(log.Add));
                Check(fixture.Repository.Load(Biz).Select(a=>a.Mid).SequenceEqual(new[]{"403"}) && fixture.Repository.Load("other-account").Count==0,
                    "补充失败没有回滚整批，或阻挡了旧接口/串入其他账号");
                Check(log.Any(s=>s.Contains("继续历史接口")) && !log.Any(s=>s.Contains("fixture details")),"补充异常缺少降级提示或泄露原异常内容");
            }
        }

        static async Task StopAndResumeKeepSupplementAndHistoryCursorsIndependent()
        {
            using var fixture=new Fixture(); var requests=new List<int>(); var order=new List<string>();
            var source=new Source((s,t)=>Task.FromResult(Snapshot(Article("501"),Article("502"))));
            var transport=new Transport((url,s,t)=> { requests.Add(Offset(url)); return Task.FromResult(Page(false,12,"501","503")); });
            using(var first=Controller(fixture.Repository,transport,source,(a,s,t)=> { order.Add(a.Mid); return Task.FromResult(Available(a)); }))
            {
                first.ArticleSaved+=a=> { if(a.Status==ArticleStatus.Available)first.Stop(); };
                await ExpectCanceled(first.StartAsync(null));
                var checkpoint=fixture.Repository.GetCollectionCheckpoint(Biz);
                Check(requests.Count==0 && !checkpoint.HistoryComplete && checkpoint.PagesSaved==0 && checkpoint.NextOffset==0
                    && fixture.Repository.PendingCount(Biz)==1,"缓存完成错误确认历史末页，或停止时丢失补充待办");
            }
            using(var second=Controller(new ArticleRepository(fixture.DirectoryPath),transport,source,(a,s,t)=> { order.Add(a.Mid); return Task.FromResult(Available(a)); }))
            { await second.StartAsync(null); }
            Check(order.SequenceEqual(new[]{"502","501","503"}) && requests.SequenceEqual(new[]{0}),"停止后重抓第一篇、丢失补漏项或错误处理历史重复项");
            // A finished history page can still have new cache work, but the cache must
            // never manufacture a new history cursor or reset an unfinished one.
            var repo=new ArticleRepository(fixture.DirectoryPath); repo.SaveCollectionSupplement(Biz,new[]{Article("504")});
            int extra=0;
            using var third=Controller(repo,transport,new Source((s,t)=>Task.FromResult(Snapshot())),(a,s,t)=> { extra++; return Task.FromResult(Available(a)); });
            await third.StartAsync(null);
            Check(extra==1 && requests.Count==1 && repo.GetCollectionCheckpoint(Biz).NextOffset==12,"末页后的独立补漏重置或伪造旧接口进度");
        }

        static async Task OnlySameAccountObservationUpdatesRunningSession()
        {
            using var fixture=new Fixture(); CollectionController controller=null; int details=0; AccountSession updated=null;
            var initial=Session(); var newer=initial.Clone(); newer.Cookie="fixture=new-observation"; newer.CapturedAt=initial.CapturedAt.AddSeconds(1);
            var foreign=newer.Clone(); foreign.Biz="other-account"; foreign.Cookie="fixture=foreign"; foreign.CapturedAt=newer.CapturedAt.AddSeconds(1);
            var transport=new Transport((url,session,token)=>
            { session.Cookie="fixture=page-response"; return Task.FromResult(Page(false,10,"601","602","603")); });
            using(controller=Controller(fixture.Repository,transport,new Source((s,t)=>Task.FromResult(Snapshot())),(article,session,token)=>
            {
                details++;
                Check(session.Biz==Biz && session.Cookie==(details==1?"fixture=page-response":"fixture=new-observation"),"会话更新覆盖了列表响应或混入其他账号");
                if(details==1)controller.Recognize(newer); else if(details==2)controller.Recognize(foreign);
                return Task.FromResult(Available(article));
            },initial))
            {
                controller.SessionUpdated+=s=>updated=s;
                await controller.StartAsync(null);
            }
            Check(updated.Biz==Biz && updated.Cookie==newer.Cookie && fixture.Repository.Load("other-account").Count==0,"最终会话或文章保存串入其他公众号");
        }

        static async Task StopAfterDetailReturnsPreventsSave()
        {
            using var fixture=new Fixture(); CollectionController controller=null;
            using(controller=Controller(fixture.Repository,new Transport((u,s,t)=>Task.FromResult(Page(false,10,"701","702"))),
                new Source((s,t)=>Task.FromResult(Snapshot())),(article,session,token)=>
                { controller.Stop(); return Task.FromResult(Available(article)); }))
            { await ExpectCanceled(controller.StartAsync(null)); }
            Check(fixture.Repository.PendingCount(Biz)==2 && fixture.Repository.Load(Biz).All(a=>a.Status==ArticleStatus.Pending)
                && fixture.Repository.LoadBody(Article("701").Id)=="","网络返回后收到停止仍写入正文或推进待办");
        }

        static async Task SupplementCancellationDoesNotBecomeFallback()
        {
            using var fixture=new Fixture(); var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport=new Transport((u,s,t)=>Task.FromResult(Page(false,1,"801")));
            var source=new Source(async(s,t)=> { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite,t); return Snapshot(); });
            using var controller=Controller(fixture.Repository,transport,source,(a,s,t)=>Task.FromResult(Available(a)));
            var running=controller.StartAsync(null); await entered.Task; controller.Stop(); await ExpectCanceled(running);
            Check(transport.Requests==0 && fixture.Repository.Load(Biz).Count==0,"用户取消缓存读取后仍回退请求旧接口");
            using var independentlyCanceled=Controller(fixture.Repository,transport,new Source((s,t)=>Task.FromException<ArticleSupplementSnapshot>(new OperationCanceledException())),(a,s,t)=>Task.FromResult(Available(a)));
            await ExpectCanceled(independentlyCanceled.StartAsync(null));
            Check(transport.Requests==0,"来源OCE被吞掉并继续请求");
        }

        static async Task CancellationAfterWriterWaitRollsBackEveryMutation()
        {
            foreach(string operation in new[]{"mark","complete","fail-record","fail-id","supplement","page"})
            {
                using var fixture=new Fixture(); var repo=fixture.Repository; var current=Article("901"); var next=Article("902");
                repo.BeginCollectionRun(Biz); repo.SaveCollectionPage(Biz,new[]{current,next},0,17,false); repo.MarkArticleStarted(Biz,current.Id);
                string before=JsonConvert.SerializeObject(repo.GetCollectionCheckpoint(Biz));
                using var stop=new CancellationTokenSource(); using var entered=new ManualResetEventSlim(); Task pending;
                using(var writer=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(fixture.DirectoryPath,"articles.sqlite"),Pooling=false }.ToString()))
                {
                    writer.Open(); using var transaction=writer.BeginTransaction();
                    using(var command=writer.CreateCommand()) { command.Transaction=transaction; command.CommandText="UPDATE collection_checkpoints SET payload=payload"; command.ExecuteNonQuery(); }
                    pending=Task.Run(()=>
                    {
                        entered.Set();
                        switch(operation)
                        {
                            case "mark": repo.MarkArticleStarted(Biz,next.Id,stop.Token); break;
                            case "complete": repo.CompleteArticle(Available(current),stop.Token); break;
                            case "fail-record": repo.FailArticle(current,"fixture failure",stop.Token); break;
                            case "fail-id": repo.FailArticle(Biz,current.Id,"fixture failure",stop.Token); break;
                            case "supplement": repo.SaveCollectionSupplement(Biz,new[]{Article("903")},stop.Token); break;
                            case "page": repo.SaveCollectionPage(Biz,Array.Empty<ArticleRecord>(),17,30,true,stop.Token); break;
                        }
                    });
                    Check(entered.Wait(TimeSpan.FromSeconds(5)),"写锁测试任务没有开始");
                    await Task.Delay(100);
                    Check(!pending.IsCompleted,"待测写入没有被现有SQLite事务阻塞");
                    stop.Cancel(); transaction.Rollback();
                }
                await ExpectCanceled(pending);
                Check(JsonConvert.SerializeObject(repo.GetCollectionCheckpoint(Biz))==before && repo.PendingCount(Biz)==2
                    && repo.GetPendingWork(Biz).All(a=>a.Attempts==0) && repo.Load(Biz).Count==2
                    && repo.Load(Biz).All(a=>a.Status==ArticleStatus.Pending) && repo.LoadBody(current.Id)=="",
                    "写锁等待中停止后仍提交变更："+operation);
            }
        }

        static AccountSession Session()=>new AccountSession { Biz=Biz,Name="quiet fixture",UserName="gh_fixture",Uin="fixture-uin",Key="fixture-key",Cookie="fixture=initial" };
        static ArticleRecord Article(string mid,string biz=Biz)=>new ArticleRecord { Biz=biz,Mid=mid,Idx=1,Id=biz+":"+mid+":1",
            Url="https://mp.weixin.qq.com/s?__biz="+Uri.EscapeDataString(biz)+"&mid="+mid+"&idx=1",Title="fixture "+mid,
            PublishedAt=new DateTime(2026,1,1).AddMinutes(int.Parse(mid)),Status=ArticleStatus.Pending };
        static ArticleRecord Available(ArticleRecord article) { article.Status=ArticleStatus.Available; article.Html="<html><div id='js_content'>fixture body "+article.Mid+"</div></html>"; return article; }
        static ArticleSupplementSnapshot Snapshot(params ArticleRecord[] articles)=>new ArticleSupplementSnapshot { Articles=articles,Message="fixture cache snapshot",CapturedAtUtc=DateTime.UtcNow };
        static int Offset(string url)=>int.Parse(HttpUtility.ParseQueryString(new Uri(url).Query)["offset"]??"0");
        static string Page(bool more,int next,params string[] mids)=>new JObject { ["ret"]=0,["can_msg_continue"]=more?1:0,["next_offset"]=next,
            ["general_msg_list"]=new JObject { ["list"]=new JArray(mids.Select(mid=>new JObject { ["comm_msg_info"]=new JObject { ["id"]=mid,["datetime"]=1700000000 },
                ["app_msg_ext_info"]=new JObject { ["title"]="fixture "+mid,["content_url"]=Article(mid).Url } })) }.ToString(Formatting.None) }.ToString(Formatting.None);
        static CollectionController Controller(ArticleRepository repo,Transport transport,IArticleSupplementSource source,
            Func<ArticleRecord,AccountSession,CancellationToken,Task<ArticleRecord>> enrich,AccountSession session=null)
        {
            var history=new HistoryCollector(transport) { MinimumDelay=TimeSpan.Zero,MaximumDelay=TimeSpan.Zero,RetryDelay=TimeSpan.Zero };
            var controller=new CollectionController(history,enrich,repo,supplementSource:source) { DetailRetryDelay=TimeSpan.Zero };
            controller.Recognize(session??Session()); return controller;
        }
        static async Task ExpectCanceled(Task task)
        {
            Check(await Task.WhenAny(task,Task.Delay(5000))==task,"停止未结束");
            try { await task; } catch(OperationCanceledException) { return; }
            throw new InvalidOperationException("Quiet hybrid test expected cancellation.");
        }
        static void Check(bool value,string message) { if(!value)throw new InvalidOperationException("Quiet hybrid self-test: "+message); }
        sealed class Source : IArticleSupplementSource
        {
            readonly Func<AccountSession,CancellationToken,Task<ArticleSupplementSnapshot>> read;
            public int Calls { get; private set; }
            public Source(Func<AccountSession,CancellationToken,Task<ArticleSupplementSnapshot>> read) { this.read=read; }
            public Task<ArticleSupplementSnapshot> ReadAsync(AccountSession session,CancellationToken token) { Calls++; return read(session,token); }
        }
        sealed class Transport : IHttpTransport
        {
            readonly Func<string,AccountSession,CancellationToken,Task<string>> get;
            public int Requests { get; private set; }
            public Transport(Func<string,AccountSession,CancellationToken,Task<string>> get) { this.get=get; }
            public Task<string> GetStringAsync(string url,AccountSession session,CancellationToken token) { Requests++; return get(url,session,token); }
            public Task<string> PostFormAsync(string url,IDictionary<string,string> values,AccountSession session,CancellationToken token)=>throw new NotSupportedException();
            public Task DownloadAsync(string url,string destination,AccountSession session,CancellationToken token)=>throw new NotSupportedException();
        }
        sealed class ProgressSink : IProgress<CollectionProgress>
        {
            readonly Action<string> log; public ProgressSink(Action<string> log) { this.log=log; }
            public void Report(CollectionProgress value)=>log(value.Message);
        }
        sealed class Fixture : IDisposable
        {
            public string DirectoryPath { get; }=Path.Combine(Path.GetTempPath(),"WCAE-quiet-tests-"+Guid.NewGuid().ToString("N"));
            public ArticleRepository Repository { get; }
            public Fixture() { Repository=new ArticleRepository(DirectoryPath); }
            public void Dispose() { if(Directory.Exists(DirectoryPath))Directory.Delete(DirectoryPath,true); }
        }
    }
}
