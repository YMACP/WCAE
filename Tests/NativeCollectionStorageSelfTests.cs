using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace WCAE
{
    public static class NativeCollectionStorageSelfTests
    {
        public static void Run()
        {
            LegacySourceRemainsIndependent();
            ResumeCurrentAndReturnWithoutRepeatingBody();
            InlineMessageNavigationSurvivesResume();
            LocalSnapshotsAreSeparateAndResumable();
            LocalSnapshotCompletionIsAtomic();
            AliasMembershipUsesRealStoredIdentity();
            IncompleteBodiesCannotAdvance();
            AtomicDiscoveryAndCompletion();
            CancellationWhileWaitingForWriter();
            ClearRemovesNativeHistory();
        }
        static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
        static string DirectoryFor(string name)
        {
            string path=Path.Combine(Path.GetTempPath(),"WCAE-native-storage-"+name+"-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }
        static ArticleRecord Article(int mid,string biz="native-storage") => new ArticleRecord
        {
            Id=biz+":"+mid+":1",Biz=biz,Mid=mid.ToString(),AccountName="主页存储测试号",Title="文章 "+mid,
            Url="https://mp.weixin.qq.com/s?__biz="+biz+"&mid="+mid+"&idx=1",Status=ArticleStatus.Pending
        };
        static NativeArticleAnchor Anchor(ArticleRecord article) => new NativeArticleAnchor
        { ArticleId=article.Id,CanonicalId=ArticleIdentityEvidence.Canonical(article),Title=article.Title,PreviousTitle="上一项提示",NextTitle="下一项提示",OrdinalHint=3 };
        static ArticleRecord Complete(ArticleRepository repo,ArticleRecord article)
        {
            article.Status=ArticleStatus.Available; article.Html="<div id='js_content'>已取得完整正文</div>";
            return repo.CompleteNativeArticle(article);
        }
        static void MustReject(Action action,string message)
        {
            bool rejected=false; try { action(); } catch(InvalidOperationException) { rejected=true; }
            Check(rejected,message);
        }

        static void LegacySourceRemainsIndependent()
        {
            string directory=DirectoryFor("isolation"); var repo=new ArticleRepository(directory);
            var legacy=Enumerable.Range(1,50).Select(i=>Article(i)).ToArray();
            repo.BeginCollectionRun("native-storage"); repo.SaveCollectionPage("native-storage",legacy,0,24,true);
            string checkpoint=JsonConvert.SerializeObject(repo.GetCollectionCheckpoint("native-storage"));
            string pending=JsonConvert.SerializeObject(repo.GetPendingWork("native-storage"));
            var initial=repo.BeginNativeCollection("native-storage","gh_native");
            Check(initial.Phase=="Discovering" && initial.CompletedCount==0 && initial.CurrentArticleId=="" && !initial.EndConfirmed
                && repo.GetNativeCurrentArticle("native-storage")==null,"旧50篇队列或旧末页错误迁入主页源");
            var discovered=repo.SaveNativeDiscovered("native-storage",legacy[4],Anchor(legacy[4]));
            Complete(repo,discovered); repo.AdvanceNativeNavigation("native-storage"); repo.FinishNativeCollection("native-storage");
            Check(checkpoint==JsonConvert.SerializeObject(repo.GetCollectionCheckpoint("native-storage"))
                && pending==JsonConvert.SerializeObject(repo.GetPendingWork("native-storage")) && repo.PendingCount("native-storage")==50,
                "主页源改动了旧队列或分页状态");
            using(var c=Open(directory))using(var cmd=c.CreateCommand())
            { cmd.CommandText="SELECT COUNT(*) FROM collection_details"; Check(Convert.ToInt32(cmd.ExecuteScalar())==0,"主页完成写入旧来源完成时钟"); }
            Check(repo.Load("native-storage").Count==50 && repo.LoadBody(discovered.Id).Length>0,"共享文章正文时产生重复行或丢失正文");
        }

        static void ResumeCurrentAndReturnWithoutRepeatingBody()
        {
            string directory=DirectoryFor("resume"); var repo=new ArticleRepository(directory);
            var initial=repo.BeginNativeCollection("native-storage","");
            var bound=repo.BeginNativeCollection("native-storage","gh_native");
            Check(bound.RunId==initial.RunId && bound.UserName=="gh_native","补全主页用户名错误重置轮次");
            MustReject(()=>repo.BeginNativeCollection("native-storage","gh_other"),"允许把断点换绑到其他主页");
            MustReject(()=>repo.FinishNativeCollection("native-storage"),"空列表被错误标记完成");
            var article=Article(100); var anchor=Anchor(article);
            article=repo.SaveNativeDiscovered("native-storage",article,anchor); anchor.Title="调用方后来修改";
            var current=repo.GetNativeCollectionState("native-storage");
            Check(current.CurrentAnchor.Title!="调用方后来修改" && current.CurrentAnchor.CanonicalId==article.Id && current.Phase=="ArticleReady",
                "导航锚点未复制或丢失真实页面身份");
            article=repo.FailNativeArticle(article,"temporary failure"); repo=new ArticleRepository(directory);
            var resumed=repo.BeginNativeCollection("native-storage","gh_native");
            Check(resumed.RunId==initial.RunId && resumed.CurrentArticleId==article.Id && resumed.Phase=="ArticleReady"
                && resumed.LastError=="temporary failure" && repo.GetNativeCurrentArticle("native-storage").Status==ArticleStatus.Failed,
                "重启未优先恢复失败中的当前篇");
            MustReject(()=>repo.SaveNativeDiscovered("native-storage",Article(101),Anchor(Article(101))),"下一篇覆盖了尚未完成的当前篇");
            Complete(repo,article); repo=new ArticleRepository(directory); resumed=repo.BeginNativeCollection("native-storage","gh_native");
            Check(resumed.Phase=="Returning" && resumed.CompletedCount==1 && resumed.CurrentArticleId==article.Id
                && repo.IsNativeArticleCompleted("native-storage",article.Id),"正文完成后重启丢失返回主页阶段");
            MustReject(()=>repo.FinishNativeCollection("native-storage"),"尚未返回主页就提前结束");
            var returned=repo.AdvanceNativeNavigation("native-storage");
            Check(returned.CurrentArticleId=="" && returned.CurrentAnchor==null && returned.LastAnchor.ArticleId==article.Id
                && returned.Phase=="Discovering","返回主页后没有推进导航锚点");
            var pinned=Article(100); repo.SaveNativeDiscovered("native-storage",pinned,Anchor(pinned));
            Check(repo.GetNativeCollectionState("native-storage").Phase=="Returning" && repo.GetNativeCollectionState("native-storage").CompletedCount==1,
                "置顶重复文章重新进入正文处理或重复计数");
            repo.AdvanceNativeNavigation("native-storage"); var finished=repo.FinishNativeCollection("native-storage");
            Check(finished.Phase=="Complete" && finished.EndConfirmed,"正常末页确认未保存");
            var next=repo.BeginNativeCollection("native-storage","gh_native");
            Check(next.RunId!=initial.RunId && next.CompletedCount==0 && !next.EndConfirmed && !repo.IsNativeArticleCompleted("native-storage",article.Id),
                "明确完成后的新轮次沿用了旧完成成员");
        }

        static void InlineMessageNavigationSurvivesResume()
        {
            string directory=DirectoryFor("inline-navigation"); var repo=new ArticleRepository(directory);
            var initial=repo.BeginNativeCollection("native-storage","gh_native");
            var article=Article(120); var anchor=Anchor(article); anchor.RequiresArticleNavigation=false;
            article=repo.SaveNativeDiscovered(article.Biz,article,anchor);
            anchor.RequiresArticleNavigation=true;
            var state=repo.GetNativeCollectionState(article.Biz);
            Check(state.Phase=="ArticleReady" && state.CurrentAnchor!=null && !state.CurrentAnchor.RequiresArticleNavigation,
                "保存内联短消息后丢失无需文章导航标记，或调用方修改污染持久断点");

            repo=new ArticleRepository(directory); state=repo.BeginNativeCollection(article.Biz,"gh_native");
            Check(state.RunId==initial.RunId && state.Phase=="ArticleReady" && state.CurrentArticleId==article.Id
                && state.CurrentAnchor!=null && !state.CurrentAnchor.RequiresArticleNavigation,
                "正文处理前重启将内联短消息误恢复为需要文章导航的卡片");
            article=repo.GetNativeCurrentArticle(article.Biz); Complete(repo,article);
            state=repo.GetNativeCollectionState(article.Biz);
            Check(state.Phase=="Returning" && state.CompletedCount==1 && state.CurrentAnchor!=null
                && !state.CurrentAnchor.RequiresArticleNavigation,
                "完成内联短消息正文后丢失无需关闭文章窗口的返回标记");

            repo=new ArticleRepository(directory); state=repo.BeginNativeCollection(article.Biz,"gh_native");
            Check(state.RunId==initial.RunId && state.Phase=="Returning" && state.CurrentArticleId==article.Id
                && state.CurrentAnchor!=null && !state.CurrentAnchor.RequiresArticleNavigation,
                "返回阶段重启把内联短消息误恢复为应关闭其他文章窗口");
            state=repo.AdvanceNativeNavigation(article.Biz);
            Check(state.Phase=="Discovering" && state.CurrentAnchor==null && state.LastAnchor!=null
                && state.LastAnchor.ArticleId==article.Id && !state.LastAnchor.RequiresArticleNavigation,
                "推进后 LastAnchor 丢失内联短消息的导航方式");
            repo=new ArticleRepository(directory); state=repo.GetNativeCollectionState(article.Biz);
            Check(state.LastAnchor!=null && state.LastAnchor.CanonicalId==article.Id
                && !state.LastAnchor.RequiresArticleNavigation && state.CompletedCount==1,
                "重新打开数据库后 LastAnchor 的真实文章身份或内联导航标记丢失");

            var normal=Article(121); repo.SaveNativeDiscovered(normal.Biz,normal,Anchor(normal));
            state=repo.GetNativeCollectionState(normal.Biz);
            Check(state.CurrentAnchor!=null && state.CurrentAnchor.RequiresArticleNavigation && state.LastAnchor!=null
                && !state.LastAnchor.RequiresArticleNavigation,
                "正常图文默认导航行为被上一条内联短消息污染");
        }

        static ArticleRecord LocalText(string text="相同短消息\n完整可见正文 <script>alert(1)</script> & O'Reilly")
            =>NativeProfileTextSnapshot.Create(new AccountSession { Biz="native-storage",Name="主页存储测试号" },
                "相同短消息",text,"今天",123,4);
        static NativeArticleAnchor LocalAnchor(ArticleRecord article)=>new NativeArticleAnchor
        {
            ArticleId=article.Id,Title=article.Title,NativeTextFingerprint=article.NativeTextFingerprint,
            NativeDateLabel=article.NativeDateLabel,RequiresArticleNavigation=false,Section="timeline",SectionOrdinal=1,
            PreviousTitle="上一篇",NextTitle="下一篇",OrdinalHint=3
        };
        static void LocalSnapshotsAreSeparateAndResumable()
        {
            string directory=DirectoryFor("local-text"); var repo=new ArticleRepository(directory);
            var first=LocalText(); var sameText=LocalText();
            Check(first.Id!=sameText.Id && first.NativeTextFingerprint==sameText.NativeTextFingerprint,
                "把相同文案的不同发布位置错误当成同一本地记录");
            Check(NativeProfileTextSnapshot.MatchesFingerprint(first.Biz,"相同短消息 完整可见正文 <script>alert(1)</script> &amp; O'Reilly",first.NativeTextFingerprint)
                && !NativeProfileTextSnapshot.MatchesFingerprint("other-account",first.NativeTextContent,first.NativeTextFingerprint),
                "全文指纹没有按一次实体解码/空白归一或公众号隔离");
            Check(first.Status==ArticleStatus.Pending && first.Url=="" && first.Mid=="" && first.ResolvedArticleId==""
                && !first.PublishedAt.HasValue && !first.PublishedUnixSeconds.HasValue && ArticleIdentityEvidence.Canonical(first)==""
                && first.Html.Contains("&lt;script&gt;") && !first.Html.Contains("<script>") && first.Html.Contains("原生主页正文"),
                "本地正文伪造了原文身份、时间或未编码可见内容");
            var malformed=JsonConvert.DeserializeObject<ArticleRecord>(JsonConvert.SerializeObject(first));
            malformed.NativeTextContent+="多出来的内容";
            MustReject(()=>NativeProfileTextSnapshot.Validate(malformed),"被改写全文仍通过指纹验证");
            malformed=JsonConvert.DeserializeObject<ArticleRecord>(JsonConvert.SerializeObject(first)); malformed.Url=Article(700).Url;
            MustReject(()=>NativeProfileTextSnapshot.Validate(malformed),"本地正文混入真实原文身份");

            repo.BeginNativeCollection(first.Biz,"gh_native");
            var anchor=LocalAnchor(first); first=repo.SaveNativeDiscovered(first.Biz,first,anchor);
            var legacy=Article(699); repo.Save(legacy); repo.BeginCollectionRun(first.Biz);
            Check(repo.GetPendingArticles(first.Biz).Select(a=>a.Id).SequenceEqual(new[]{legacy.Id}),
                "停止在 Pending 的主页正文被旧来源导入 HTTP 待办");
            string legacyState=JsonConvert.SerializeObject(repo.GetCollectionCheckpoint(first.Biz));
            var badAnchor=LocalAnchor(first); badAnchor.CanonicalId=first.Id;
            MustReject(()=>repo.SaveNativeDiscovered(first.Biz,first,badAnchor),"本地ID被填进 canonical 仍被保存");
            MustReject(()=>repo.CompleteNativeArticle(first),"主页正文绕过专用分支变成可访问原文");
            repo=new ArticleRepository(directory);
            var resumed=repo.BeginNativeCollection(first.Biz,"gh_native");
            Check(resumed.Phase=="ArticleReady" && resumed.CurrentAnchor.NativeTextFingerprint==first.NativeTextFingerprint
                && resumed.CurrentAnchor.CanonicalId=="" && resumed.CurrentAnchor.NativeDateLabel=="今天", "重启丢失本地正文断点证据");
            first=repo.GetNativeCurrentArticle(first.Biz);
            Check(first.Html=="" && first.NativeTextContent.Length>0,"分离大块HTML时丢失供续采的完整可见文本");
            first.Html="<script>untrusted caller HTML</script>";
            var completed=repo.CompleteNativeSnapshot(first);
            Check(completed.Status==ArticleStatus.LocalSnapshot && completed.StatusText=="主页正文"
                && repo.GetNativeCollectionState(first.Biz).Phase=="Returning" && repo.GetNativeCollectionState(first.Biz).CompletedCount==1
                && repo.LoadBody(first.Id).Contains("&lt;script&gt;") && !repo.LoadBody(first.Id).Contains("untrusted caller"),
                "主页正文未从持久文本安全完成，或当前文章/计数未原子推进");
            repo.CompleteNativeSnapshot(completed);
            Check(repo.GetNativeCollectionState(first.Biz).CompletedCount==1,"重复本地完成回调重复计数");
            repo.AdvanceNativeNavigation(first.Biz);
            Check(repo.GetNativeCollectionState(first.Biz).LastAnchor.NativeTextFingerprint==first.NativeTextFingerprint,
                "推进时丢失本地正文恢复指纹");
            repo.SaveNativeDiscovered(sameText.Biz,sameText,LocalAnchor(sameText)); repo.CompleteNativeSnapshot(sameText);
            repo.AdvanceNativeNavigation(first.Biz);
            var canonical=Article(700); canonical.Title=first.Title; canonical.HistoryTitle=first.NativeTextContent;
            repo.SaveNativeDiscovered(canonical.Biz,canonical,Anchor(canonical)); Complete(repo,canonical);
            Check(repo.GetNativeCollectionState(first.Biz).CompletedCount==3 && repo.Load(first.Biz).Count==4
                && ArticleQuery.ResolveReferences(repo.Load(first.Biz)).Count==4,
                "相同文案本地副本和 canonical 文章被错误合并或没有单独计数");
            using(var c=Open(directory))using(var cmd=c.CreateCommand())
            { cmd.CommandText="SELECT COUNT(*) FROM native_collection_completed WHERE article_id LIKE 'native-profile:%' AND canonical_id<>''";
                Check(Convert.ToInt32(cmd.ExecuteScalar())==0,"本地完成成员伪造了 canonical 标识"); }
            Check(legacyState==JsonConvert.SerializeObject(repo.GetCollectionCheckpoint(first.Biz)) && repo.PendingCount(first.Biz)==1,
                "本地正文完成改变了旧来源分页或待办");
        }
        static void LocalSnapshotCompletionIsAtomic()
        {
            string directory=DirectoryFor("snapshot-atomic"); var repo=new ArticleRepository(directory);
            var article=LocalText(); repo.BeginNativeCollection(article.Biz,"gh_native");
            repo.SaveNativeDiscovered(article.Biz,article,LocalAnchor(article));
            string before=Snapshot(directory); SetRejectTrigger(directory,true); bool rejected=false;
            try { repo.CompleteNativeSnapshot(article); } catch(SqliteException) { rejected=true; }
            Check(rejected && Snapshot(directory)==before,"本地正文完成断点失败时正文、状态或完成计数未整体回滚");
            SetRejectTrigger(directory,false);
            using var stop=new CancellationTokenSource(); stop.Cancel(); bool canceled=false;
            try { repo.CompleteNativeSnapshot(article,stop.Token); } catch(OperationCanceledException) { canceled=true; }
            Check(canceled && Snapshot(directory)==before,"已停止后仍写入本地正文完成结果");
        }

        static void AliasMembershipUsesRealStoredIdentity()
        {
            string directory=DirectoryFor("aliases"); var repo=new ArticleRepository(directory);
            var shortRecord=Article(200); shortRecord.Url="https://mp.weixin.qq.com/s/native-short-link";
            shortRecord.Mid=""; shortRecord.IdentityUnresolved=true; shortRecord.Id=ArticleIdentity.Create(shortRecord.Biz,"",1,shortRecord.Url);
            string storageId=shortRecord.Id; repo.Save(shortRecord);
            // A real page later proved the identity; the old short URL storage key stays.
            shortRecord.Mid="200"; shortRecord.ResolvedArticleId="native-storage:200:1"; shortRecord.IdentityUnresolved=false;
            repo.Save(shortRecord); repo.BeginNativeCollection("native-storage","gh_native");
            var incoming=Article(200); var saved=repo.SaveNativeDiscovered("native-storage",incoming,Anchor(incoming));
            var state=repo.GetNativeCollectionState("native-storage");
            Check(saved.Id==storageId && state.CurrentArticleId==storageId && state.CurrentAnchor.ArticleId==storageId
                && state.CurrentAnchor.CanonicalId=="native-storage:200:1","归并短链接后存储ID与页面身份错位");
            Complete(repo,saved); repo.AdvanceNativeNavigation("native-storage");
            Check(repo.IsNativeArticleCompleted("native-storage",storageId) && repo.IsNativeArticleCompleted("native-storage","native-storage:200:1"),
                "已完成文章的长短链接身份没有归到同一轮成员");
            saved=repo.SaveNativeDiscovered("native-storage",Article(200),Anchor(Article(200)));
            Check(saved.Id==storageId && repo.GetNativeCollectionState("native-storage").Phase=="Returning"
                && repo.GetNativeCollectionState("native-storage").CompletedCount==1 && repo.Load("native-storage").Count==1,
                "同一文章以canonical再次出现时重复补全或新增源行");
            var other=Article(200,"other-native"); repo.BeginNativeCollection(other.Biz,"gh_other");
            Check(!repo.IsNativeArticleCompleted(other.Biz,other.Id),"另一个公众号借用了本号完成成员");
        }

        static void IncompleteBodiesCannotAdvance()
        {
            var repo=new ArticleRepository(DirectoryFor("body")); repo.BeginNativeCollection("native-storage","gh_native");
            var article=repo.SaveNativeDiscovered("native-storage",Article(300),Anchor(Article(300)));
            article.Status=ArticleStatus.Available; article.Html="   "; article=repo.CompleteNativeArticle(article);
            Check(article.Status==ArticleStatus.Failed && repo.GetNativeCollectionState(article.Biz).Phase=="ArticleReady"
                && repo.GetNativeCollectionState(article.Biz).CompletedCount==0 && !repo.IsNativeArticleCompleted(article.Biz,article.Id),
                "可访问但没有正文的文章错误推进断点");
            MustReject(()=>repo.AdvanceNativeNavigation(article.Biz),"没有正文仍允许推进下一篇");
            article.Status=ArticleStatus.Restricted; article.Html="<div>限流前取得的部分正文</div>"; article=repo.FailNativeArticle(article,"rate limit");
            Check(article.Status==ArticleStatus.Restricted && repo.LoadBody(article.Id).Contains("部分正文")
                && repo.GetNativeCollectionState(article.Biz).Phase=="ArticleReady","限流未保留部分正文和当前篇");
            article.Status=ArticleStatus.Pending;
            Check(repo.CompleteNativeArticle(article).Status==ArticleStatus.Failed,"非最终状态借用旧正文被标记成功");
            article.Status=ArticleStatus.Deleted; article.Html=""; repo.CompleteNativeArticle(article);
            Check(repo.GetNativeCollectionState(article.Biz).Phase=="Returning" && repo.GetNativeCollectionState(article.Biz).CompletedCount==1,
                "已删除状态不能正常完成并返回主页");
        }

        static void AtomicDiscoveryAndCompletion()
        {
            string directory=DirectoryFor("atomic"); var repo=new ArticleRepository(directory); repo.BeginNativeCollection("native-storage","gh_native");
            string before=Snapshot(directory); SetRejectTrigger(directory,true); bool rejected=false;
            try { repo.SaveNativeDiscovered("native-storage",Article(400),Anchor(Article(400))); } catch(SqliteException) { rejected=true; }
            Check(rejected && Snapshot(directory)==before,"发现断点写失败时没有回滚文章和账号");
            SetRejectTrigger(directory,false); var article=repo.SaveNativeDiscovered("native-storage",Article(400),Anchor(Article(400)));
            before=Snapshot(directory); SetRejectTrigger(directory,true); rejected=false;
            try { Complete(repo,article); } catch(SqliteException) { rejected=true; }
            Check(rejected && Snapshot(directory)==before && repo.LoadBody(article.Id)=="","完成断点写失败时正文、完成成员或计数没有整体回滚");
        }

        static void CancellationWhileWaitingForWriter()
        {
            foreach(string operation in new[]{"begin","discover","complete","snapshot","fail","advance","finish"})
            {
                string directory=DirectoryFor("cancel-"+operation); var repo=new ArticleRepository(directory);
                repo.BeginNativeCollection("native-storage","gh_native"); var article=Article(500);
                if(operation=="snapshot")
                { article=LocalText(); article=repo.SaveNativeDiscovered(article.Biz,article,LocalAnchor(article)); }
                if(operation=="complete" || operation=="fail" || operation=="advance" || operation=="finish")
                    article=repo.SaveNativeDiscovered(article.Biz,article,Anchor(article));
                if(operation=="advance" || operation=="finish")Complete(repo,article);
                if(operation=="finish")repo.AdvanceNativeNavigation(article.Biz);
                string before=Snapshot(directory);
                using var stop=new CancellationTokenSource(); using var entered=new ManualResetEventSlim(); Task work;
                using(var writer=Open(directory))using(var tx=writer.BeginTransaction())
                {
                    using(var cmd=writer.CreateCommand())
                    { cmd.Transaction=tx; cmd.CommandText="UPDATE native_collection_states SET payload=payload"; cmd.ExecuteNonQuery(); }
                    work=Task.Run(()=>
                    {
                        entered.Set();
                        switch(operation)
                        {
                            case "begin": repo.BeginNativeCollection("other-native","gh_other",stop.Token); break;
                            case "discover": repo.SaveNativeDiscovered(article.Biz,article,Anchor(article),stop.Token); break;
                            case "complete": article.Status=ArticleStatus.Available; article.Html="<div>完整正文</div>"; repo.CompleteNativeArticle(article,stop.Token); break;
                            case "snapshot": repo.CompleteNativeSnapshot(article,stop.Token); break;
                            case "fail": repo.FailNativeArticle(article,"temporary failure",stop.Token); break;
                            case "advance": repo.AdvanceNativeNavigation(article.Biz,stop.Token); break;
                            case "finish": repo.FinishNativeCollection(article.Biz,stop.Token); break;
                        }
                    });
                    Check(entered.Wait(TimeSpan.FromSeconds(3)),"取消测试线程没有启动："+operation);
                    Check(!work.Wait(TimeSpan.FromMilliseconds(100)),"写操作绕过独立SQLite写锁："+operation);
                    stop.Cancel(); tx.Rollback();
                }
                bool canceled=false; try { work.GetAwaiter().GetResult(); } catch(OperationCanceledException) { canceled=true; }
                Check(canceled && Snapshot(directory)==before,"停止发生在等待写锁期间仍修改数据库："+operation);
            }
        }

        static void ClearRemovesNativeHistory()
        {
            string directory=DirectoryFor("clear"); var repo=new ArticleRepository(directory); repo.BeginNativeCollection("native-storage","gh_native");
            var article=repo.SaveNativeDiscovered("native-storage",Article(600),Anchor(Article(600))); Complete(repo,article); repo.ClearHistory();
            Check(repo.GetNativeCollectionState("native-storage")==null && !repo.IsNativeArticleCompleted("native-storage",article.Id)
                && repo.Load("native-storage").Count==0 && repo.LoadBody(article.Id)=="","清空缓存后残留主页断点、成员或正文");
            using(var c=Open(directory))using(var cmd=c.CreateCommand())
            { cmd.CommandText="SELECT COUNT(*) FROM native_collection_completed"; Check(Convert.ToInt32(cmd.ExecuteScalar())==0,"清空后残留独立完成表"); }
        }
        static SqliteConnection Open(string directory)
        {
            var c=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(directory,"articles.sqlite"),Pooling=false,DefaultTimeout=10 }.ToString());
            c.Open(); return c;
        }
        static void SetRejectTrigger(string directory,bool enabled)
        {
            using(var c=Open(directory))using(var cmd=c.CreateCommand())
            {
                cmd.CommandText=enabled?"CREATE TRIGGER reject_native_state BEFORE UPDATE ON native_collection_states BEGIN SELECT RAISE(ABORT,'fixture native state failure'); END;":"DROP TRIGGER reject_native_state";
                cmd.ExecuteNonQuery();
            }
        }
        static string Snapshot(string directory)
        {
            var all=new List<object>();
            using(var c=Open(directory))
                foreach(string query in new[]{"SELECT * FROM accounts ORDER BY biz","SELECT * FROM articles ORDER BY id","SELECT * FROM article_bodies ORDER BY id",
                    "SELECT * FROM collection_pending ORDER BY id","SELECT * FROM collection_checkpoints ORDER BY biz","SELECT * FROM collection_details ORDER BY id",
                    "SELECT * FROM native_collection_states ORDER BY biz","SELECT * FROM native_collection_completed ORDER BY biz,run_id,article_id"})
                    using(var cmd=c.CreateCommand())
                    {
                        cmd.CommandText=query; var rows=new List<object>();
                        using(var reader=cmd.ExecuteReader())while(reader.Read())
                        {
                            var cells=new object[reader.FieldCount];
                            for(int i=0;i<cells.Length;i++)cells[i]=reader.GetValue(i) is byte[] bytes?Convert.ToBase64String(bytes):reader.GetValue(i);
                            rows.Add(cells);
                        }
                        all.Add(rows);
                    }
            return JsonConvert.SerializeObject(all);
        }
    }
}
