using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace WCAE
{
    public static class IncrementalStorageSelfTests
    {
        public static void Run()
        {
            ResumeCurrentArticleAndNextPage();
            PreserveOldFullListingQueue();
            CursorRollsBackWithArticle();
            FailuresDoNotJumpAheadOfCurrentWork();
            StopWhileFinishingWaitsForWriter();
            StopWhileDeferringWaitsForWriter();
        }
        static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
        static string TestDirectory()
        {
            string path=Path.Combine(Path.GetTempPath(),"WCAE-incremental-storage-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }
        static ArticleRecord Article(int mid,string biz="incremental") => new ArticleRecord
        {
            Id=biz+":"+mid+":1",Biz=biz,Mid=mid.ToString(),Title="文章 "+mid,
            Url="https://mp.weixin.qq.com/s?__biz="+biz+"&mid="+mid+"&idx=1"
        };
        static void Complete(ArticleRepository repository,ArticleRecord article)
        {
            article.Status=ArticleStatus.Available; article.Html="<div id='js_content'>完整正文</div>";
            repository.CompleteArticle(article);
        }
        static string[] Ids(IEnumerable<ArticleRecord> articles) => articles.Select(a=>a.Id).ToArray();

        static void ResumeCurrentArticleAndNextPage()
        {
            string directory=TestDirectory(); var repository=new ArticleRepository(directory);
            var initial=repository.BeginCollectionRun("incremental");
            var first=Article(1); var current=Article(2); var following=Article(3);
            repository.SaveCollectionPage("incremental",new[]{first,current,following},0,17,false);
            repository.MarkArticleStarted("incremental",first.Id); Complete(repository,first);
            repository.MarkArticleStarted("incremental",current.Id);
            // Cancellation happens between attempts. The failed attempt must not erase the
            // active article, so a new process retries it before advancing to article three.
            repository.FailArticle(current,"temporary connection failure");
            repository=new ArticleRepository(directory);
            var resumed=repository.BeginCollectionRun("incremental");
            Check(resumed.RefreshStartedAt==initial.RefreshStartedAt && resumed.NextOffset==17 && resumed.PagesSaved==1
                && resumed.ActiveArticleId==current.Id && resumed.LastCompletedArticleId==first.Id && resumed.DetailsCompleted==1,
                "停止后恢复重置了分页位置、当前篇或已完成游标");
            Check(Ids(repository.GetPendingArticles("incremental")).SequenceEqual(new[]{current.Id,following.Id}),
                "停止后的当前篇不是第一个恢复任务，或已完成篇再次进入队列");
            Complete(repository,current);
            var betweenArticles=repository.BeginCollectionRun("incremental");
            Check(betweenArticles.ActiveArticleId=="" && betweenArticles.NextArticleId==following.Id
                && repository.GetPendingArticles("incremental").First().Id==following.Id,
                "两篇之间停止后没有从最后完成文章的下一篇继续");
            repository.MarkArticleStarted("incremental",following.Id); Complete(repository,following);
            var betweenPages=new ArticleRepository(directory).BeginCollectionRun("incremental");
            Check(repository.PendingCount("incremental")==0 && betweenPages.NextOffset==17 && !betweenPages.HistoryComplete
                && !betweenPages.RunCompleted && betweenPages.DetailsCompleted==3,
                "当前页正文处理完后没有保留下一页真实offset");
            var nextPageArticle=Article(4);
            repository.SaveCollectionPage("incremental",new[]{nextPageArticle},17,29,true);
            Check(repository.GetPendingArticles("incremental").Single().Id==nextPageArticle.Id
                && repository.GetCollectionCheckpoint("incremental").PagesSaved==2,
                "后续页保存重排了已完成正文或丢失原分页进度");
        }

        static void PreserveOldFullListingQueue()
        {
            string directory=TestDirectory(); var repository=new ArticleRepository(directory);
            repository.BeginCollectionRun("incremental");
            var articles=new[]{Article(11),Article(12),Article(13)};
            repository.SaveCollectionPage("incremental",articles,40,60,true);
            // Emulate 1.1.5 JSON: its full-list checkpoint had no per-article cursor fields.
            using(var connection=Open(directory))using(var command=connection.CreateCommand())
            {
                command.CommandText="UPDATE collection_checkpoints SET payload=json_remove(payload,'$.RunCompleted','$.ActiveArticleId','$.NextArticleId','$.LastCompletedArticleId','$.DetailsCompleted')";
                command.ExecuteNonQuery();
            }
            repository=new ArticleRepository(directory);
            var legacy=repository.BeginCollectionRun("incremental");
            Check(legacy.HistoryComplete && legacy.CurrentOffset==40 && legacy.NextOffset==60 && !legacy.RunCompleted
                && Ids(repository.GetPendingArticles("incremental")).SequenceEqual(Ids(articles)),
                "旧版全量列表待处理队列未按保存顺序直接恢复");
            bool denied=false;
            try { repository.RestartCollectionRun("incremental"); } catch(InvalidOperationException) { denied=true; }
            Check(denied,"仍有正文待处理时错误允许重新从顶部开始");
            Check(!repository.FinishCollectionRun("incremental").RunCompleted,"有待处理正文却被标记整轮完成");
            foreach(var article in articles)
            { repository.MarkArticleStarted("incremental",article.Id); Complete(repository,article); }
            // A stop immediately after the final body remains a resumable checkpoint until
            // the controller explicitly confirms normal completion with FinishCollectionRun.
            var stoppedAtEnd=repository.BeginCollectionRun("incremental");
            Check(!stoppedAtEnd.RunCompleted && stoppedAtEnd.HistoryComplete && stoppedAtEnd.NextOffset==60,
                "最后一篇后停止被自动当成新一轮开始");
            var finished=repository.FinishCollectionRun("incremental");
            Check(finished.RunCompleted && finished.DetailsCompleted==3 && finished.ActiveArticleId=="" && finished.NextArticleId=="",
                "正常结束未保存整轮完成状态");
            var repeatedBegin=new ArticleRepository(directory).BeginCollectionRun("incremental");
            Check(repeatedBegin.RunCompleted && repeatedBegin.NextOffset==60 && repeatedBegin.RefreshStartedAt==finished.RefreshStartedAt,
                "Begin擅自把已完成轮次重置为从顶部采集");
            var restarted=repository.RestartCollectionRun("incremental");
            Check(!restarted.HistoryComplete && !restarted.RunCompleted && restarted.NextOffset==0 && restarted.PagesSaved==0
                && restarted.DetailsCompleted==0 && restarted.RefreshStartedAt>finished.RefreshStartedAt,
                "明确新一轮操作没有重置分页并建立新轮次");
            repository.SaveCollectionPage("incremental",articles,0,60,true);
            Check(repository.PendingCount("incremental")==3,"明确新一轮无法重新补全已有文章指标");
        }

        static void CursorRollsBackWithArticle()
        {
            string directory=TestDirectory(); var repository=new ArticleRepository(directory);
            var article=Article(21); repository.BeginCollectionRun("incremental");
            repository.SaveCollectionPage("incremental",new[]{article},0,7,true);
            repository.MarkArticleStarted("incremental",article.Id);
            using(var connection=Open(directory))using(var command=connection.CreateCommand())
            {
                command.CommandText="CREATE TRIGGER reject_completion_cursor BEFORE UPDATE ON collection_checkpoints BEGIN SELECT RAISE(ABORT,'fixture checkpoint failure'); END;";
                command.ExecuteNonQuery();
            }
            bool failed=false;
            try { Complete(repository,article); } catch(SqliteException) { failed=true; }
            var checkpoint=repository.GetCollectionCheckpoint("incremental");
            Check(failed && checkpoint.ActiveArticleId==article.Id && checkpoint.LastCompletedArticleId=="" && checkpoint.DetailsCompleted==0
                && repository.PendingCount("incremental")==1 && repository.LoadBody(article.Id)==""
                && repository.Load("incremental").Single().Status==ArticleStatus.Pending,
                "完成游标写失败时正文、元数据、队列没有整体回滚");
        }

        static void FailuresDoNotJumpAheadOfCurrentWork()
        {
            string directory=TestDirectory(); var repository=new ArticleRepository(directory);
            repository.BeginCollectionRun("incremental");
            var failed=Article(31); var current=Article(32); var following=Article(33);
            repository.SaveCollectionPage("incremental",new[]{failed,current,following},0,19,false);
            repository.MarkArticleStarted("incremental",failed.Id); repository.FailArticle(failed,"attempts exhausted");
            repository.DeferArticle("incremental",failed.Id);
            repository.MarkArticleStarted("incremental",current.Id);
            var other=Article(41,"other"); repository.BeginCollectionRun("other");
            repository.SaveCollectionPage("other",new[]{other},0,5,true); repository.MarkArticleStarted("other",other.Id);
            repository=new ArticleRepository(directory); repository.BeginCollectionRun("incremental");
            Check(Ids(repository.GetPendingArticles("incremental")).SequenceEqual(new[]{current.Id,following.Id,failed.Id}),
                "之前有界失败的文章抢在停止中的当前篇之前重试");
            Check(repository.GetPendingWork("incremental").Single(a=>a.Id==failed.Id).Attempts==1
                && repository.GetCollectionCheckpoint("other").ActiveArticleId==other.Id && repository.GetPendingArticles("other").Single().Id==other.Id,
                "恢复游标丢失失败信息或影响了另一个公众号");
            Complete(repository,current); Complete(repository,following);
            var nextPage=Article(34);
            repository.SaveCollectionPage("incremental",new[]{nextPage},19,23,true);
            Check(Ids(repository.GetPendingArticles("incremental")).SequenceEqual(new[]{nextPage.Id,failed.Id}),
                "保存下一页后旧失败任务抢在新页恢复位置之前");
        }

        static void StopWhileFinishingWaitsForWriter()
        {
            string directory=TestDirectory(); var repository=new ArticleRepository(directory);
            var article=Article(51); repository.BeginCollectionRun("incremental");
            repository.SaveCollectionPage("incremental",new[]{article},0,24,true);
            repository.MarkArticleStarted("incremental",article.Id); Complete(repository,article);
            using var stop=new CancellationTokenSource();
            using var entered=new ManualResetEventSlim();
            Task<CollectionCheckpoint> finishing;
            using(var writer=Open(directory))using(var transaction=writer.BeginTransaction())
            {
                // This independent writer holds the database while Finish begins. Cancel
                // before releasing it: Finish may then acquire its transaction, but must not
                // mark the run complete and make the next Start restart at offset zero.
                using(var command=writer.CreateCommand())
                { command.Transaction=transaction; command.CommandText="UPDATE collection_checkpoints SET payload=payload"; command.ExecuteNonQuery(); }
                finishing=Task.Run(()=> { entered.Set(); return repository.FinishCollectionRun("incremental",stop.Token); });
                Check(entered.Wait(TimeSpan.FromSeconds(3)),"完成取消测试工作线程没有启动");
                Check(!finishing.Wait(TimeSpan.FromMilliseconds(100)),"完成标记绕过了另一个SQLite写事务");
                stop.Cancel(); transaction.Rollback();
            }
            Check(Task.WhenAny(finishing,Task.Delay(5000)).GetAwaiter().GetResult()==finishing,"SQLite锁释放后完成取消未及时结束");
            bool canceled=false;
            try { finishing.GetAwaiter().GetResult(); } catch(OperationCanceledException) { canceled=true; }
            var saved=new ArticleRepository(directory).BeginCollectionRun("incremental");
            Check(canceled && !saved.RunCompleted && saved.HistoryComplete && saved.NextOffset==24
                && saved.LastCompletedArticleId==article.Id && repository.PendingCount("incremental")==0,
                "等待写锁期间停止仍提交了完成标志，或破坏了已完成文章断点");
            Check(repository.FinishCollectionRun("incremental").RunCompleted,"取消完成标记后无法正常确认结束");
        }

        static void StopWhileDeferringWaitsForWriter()
        {
            string directory=TestDirectory(); var repository=new ArticleRepository(directory);
            var current=Article(61); var next=Article(62);
            repository.BeginCollectionRun("incremental");
            repository.SaveCollectionPage("incremental",new[]{current,next},0,24,false);
            repository.MarkArticleStarted("incremental",current.Id);
            repository.FailArticle(current,"attempts exhausted");
            using var stop=new CancellationTokenSource();
            using var entered=new ManualResetEventSlim();
            Task deferring;
            using(var writer=Open(directory))using(var transaction=writer.BeginTransaction())
            {
                using(var command=writer.CreateCommand())
                { command.Transaction=transaction; command.CommandText="UPDATE collection_checkpoints SET payload=payload"; command.ExecuteNonQuery(); }
                deferring=Task.Run(()=> { entered.Set(); repository.DeferArticle("incremental",current.Id,stop.Token); });
                Check(entered.Wait(TimeSpan.FromSeconds(3)),"跳过取消测试工作线程没有启动");
                Check(!deferring.Wait(TimeSpan.FromMilliseconds(100)),"跳过游标绕过了另一个SQLite写事务");
                stop.Cancel(); transaction.Rollback();
            }
            Check(Task.WhenAny(deferring,Task.Delay(5000)).GetAwaiter().GetResult()==deferring,"SQLite锁释放后跳过取消未及时结束");
            bool canceled=false;
            try { deferring.GetAwaiter().GetResult(); } catch(OperationCanceledException) { canceled=true; }
            repository=new ArticleRepository(directory);
            var resumed=repository.BeginCollectionRun("incremental");
            Check(canceled && resumed.ActiveArticleId==current.Id && resumed.NextArticleId==current.Id && resumed.NextOffset==24
                && Ids(repository.GetPendingArticles("incremental")).SequenceEqual(new[]{current.Id,next.Id})
                && repository.GetPendingWork("incremental").Single(a=>a.Id==current.Id).Attempts==1,
                "等待写锁期间停止仍跳过了当前失败文章，或丢失原失败信息");
            repository.DeferArticle("incremental",current.Id);
            Check(repository.GetPendingArticles("incremental").First().Id==next.Id,"取消跳过后无法正常推进到下一篇");
        }

        static SqliteConnection Open(string directory)
        {
            var connection=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(directory,"articles.sqlite"),Pooling=false }.ToString());
            connection.Open(); return connection;
        }
    }
}
