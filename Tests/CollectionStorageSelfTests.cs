using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public static class CollectionStorageSelfTests
    {
        public static void Run()
        {
            AtomicPagesAndRecovery();
            LegacyPendingRecovery();
            LegacyIdentityCompatibility();
            ResolvedShortAndLongLinks();
            OfflineMigration();
        }
        static void Check(bool value,string message) { if(!value)throw new Exception(message); }
        static string DirectoryFor(string name)
        {
            string path=Path.Combine(Path.GetTempPath(),"WCAE-collection-storage-"+name+"-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }
        static ArticleRecord Article(int mid) => new ArticleRecord { Id="storage:"+mid+":1",Biz="storage",Mid=mid.ToString(),AccountName="存储测试号",
            Title="文章"+mid,Url="https://mp.weixin.qq.com/s?__biz=storage&mid="+mid+"&idx=1" };

        static void AtomicPagesAndRecovery()
        {
            string directory=DirectoryFor("pages"); var repo=new ArticleRepository(directory);
            repo.BeginCollectionRun("storage");
            var a=Article(1); a.Title="保存过的完整标题"; a.TitleSource="history"; var b=Article(2);
            using(var cancellation=new CancellationTokenSource())
            {
                try
                {
                    repo.SaveCollectionPage("storage",new InterruptedPage(a,b,cancellation),0,10,false,cancellation.Token);
                    throw new Exception("页中途取消没有中断事务");
                }
                catch(OperationCanceledException) { }
            }
            Check(repo.Load("storage").Count==0 && repo.PendingCount("storage")==0 && repo.GetCollectionCheckpoint("storage").PagesSaved==0,
                "整页取消未同时回滚文章、待处理任务和分页位置");
            Check(repo.Accounts().Count==0,"页事务回滚后残留了公众号记录");
            repo.SaveCollectionPage("storage",new[]{a,b},0,10,false);
            repo.SaveCollectionPage("storage",new[]{a,b},0,10,false);
            Check(repo.Load("storage").Count==2 && repo.PendingCount("storage")==2,"重复页产生重复文章或任务");
            var complete=Article(1); complete.Title="保存过的完整标题含中间文字"; complete.TitleSource="heading";
            complete.PublishedAt=new DateTime(2026,8,30,7,23,6); complete.PublishedAtSource="page:ct";
            complete.ReadCount=42; complete.Status=ArticleStatus.Available; complete.Html="<div id='js_content'>已保存的正文</div>";
            repo.CompleteArticle(complete);
            repo.FailArticle("storage",b.Id,"temporary failure");
            var oldRound=repo.GetCollectionCheckpoint("storage").RefreshStartedAt;
            repo=new ArticleRepository(directory); repo.BeginCollectionRun("storage");
            Check(repo.GetCollectionCheckpoint("storage").RefreshStartedAt==oldRound,"重新打开未完成的采集时意外重置轮次");
            var shortListing=Article(1); shortListing.Title="保存过的标题"; shortListing.TitleSource="history";
            shortListing.PublishedAt=new DateTime(2026,8,30,7,23,0); shortListing.PublishedAtSource="history";
            var fresh=Article(3);
            var stored=repo.SaveCollectionPage("storage",new[]{fresh,shortListing,b},0,0,true);
            Check(repo.PendingCount("storage")==2 && repo.GetPendingArticles("storage").All(x=>x.Id!=a.Id),"顶部新增文章后的恢复漏任务或重排已成功正文");
            var retained=stored.Single(x=>x.Id==a.Id);
            Check(retained.Title==complete.Title && retained.PublishedAt==complete.PublishedAt && retained.ReadCount==42,
                "列表刷新覆盖了完整标题、秒级时间或历史统计");
            Check(shortListing.Status==ArticleStatus.Pending && shortListing.ReadCount==null,"存储合并污染了网络采集输入");
            Check(repo.GetPendingWork("storage").Single(x=>x.Id==b.Id).Attempts==1,"重启后丢失失败尝试记录");
            foreach(var pending in repo.GetPendingArticles("storage"))
            { pending.Status=ArticleStatus.Available; pending.Html="<div id='js_content'>正文</div>"; repo.CompleteArticle(pending); }
            Check(repo.PendingCount("storage")==0,"成功补全后任务仍待处理");
            repo.FinishCollectionRun("storage");
            repo.RestartCollectionRun("storage");
            repo.SaveCollectionPage("storage",new[]{a,b,fresh},0,0,true);
            Check(repo.PendingCount("storage")==3,"完整采集之后显式新一轮无法刷新已有文章统计");
            repo.ClearHistory();
            Check(repo.Load("storage").Count==0 && repo.PendingCount("storage")==0 && repo.GetCollectionCheckpoint("storage")==null,
                "清空历史没有清理持久化采集任务");
        }

        static void LegacyPendingRecovery()
        {
            var repo=new ArticleRepository(DirectoryFor("legacy-pending"));
            foreach(var item in new[]{(10,ArticleStatus.Pending),(11,ArticleStatus.Failed),(12,ArticleStatus.Restricted),(13,ArticleStatus.Available),(14,ArticleStatus.Deleted)})
            { var old=Article(item.Item1); old.Status=item.Item2; repo.Save(old); }
            var cached=Article(15); cached.Status=ArticleStatus.Available; cached.Html="<div id='js_content'>历史正文</div>"; repo.Save(cached);
            repo.BeginCollectionRun("storage");
            Check(repo.PendingCount("storage")==4,"旧版未补全记录未导入队列，或已删除/有缓存正文的旧文章被误导入");
            repo.SaveCollectionPage("storage",new[]{Article(16)},0,0,true);
            Check(repo.PendingCount("storage")==5 && repo.GetPendingArticles("storage").Any(a=>a.Mid=="11"),"新列表没有返回旧失败文章时旧任务丢失");
        }

        static void LegacyIdentityCompatibility()
        {
            var repo=new ArticleRepository(DirectoryFor("legacy-identity"));
            var old=Article(700); old.Url="https://mp.weixin.qq.com/s/SHORT_TOKEN";
            repo.Save(old); repo.BeginCollectionRun("storage");
            var incoming=new ArticleRecord { Biz="storage",SourceMessageId="700",IdentityUnresolved=true,Url=old.Url,Title=old.Title };
            incoming.Id=ArticleIdentity.Create(incoming.Biz,"",1,incoming.Url,incoming.SourceMessageId);
            var stored=repo.SaveCollectionPage("storage",new[]{incoming},0,0,true).Single();
            Check(repo.Load("storage").Count==1 && stored.Id==old.Id && stored.Mid=="" && stored.SourceMessageId=="700" && repo.PendingCount("storage")==1,
                "旧版源消息ID短链接重采产生重复，或把源消息ID恢复成文章mid");
            string legacyHash;
            using(var sha=SHA256.Create())legacyHash="url:"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("https://mp.weixin.qq.com/s"))).Replace("-","").ToLowerInvariant();
            var oldHash=new ArticleRecord { Id=legacyHash,Biz="storage",Url="https://mp.weixin.qq.com/s?article_token=first",Title="旧版哈希文章" };
            repo.Save(oldHash);
            var newHash=new ArticleRecord { Biz="storage",IdentityUnresolved=true,Url=oldHash.Url,Title=oldHash.Title };
            newHash.Id=ArticleIdentity.Create(newHash.Biz,"",1,newHash.Url);
            Check(repo.SaveCollectionPage("storage",new[]{newHash},0,0,true).Single().Id==legacyHash && repo.Load("storage").Count==2,
                "旧版URL哈希在升级重采后产生重复");
            var distinct=new ArticleRecord { Biz="storage",IdentityUnresolved=true,Url="https://mp.weixin.qq.com/s?article_token=second",Title="不同文章" };
            distinct.Id=ArticleIdentity.Create(distinct.Biz,"",1,distinct.Url);
            repo.SaveCollectionPage("storage",new[]{distinct},0,0,true);
            Check(repo.Load("storage").Count==3,"相同旧path哈希但不同新URL身份被错误合并");
        }

        static void ResolvedShortAndLongLinks()
        {
            string shortUrl="https://mp.weixin.qq.com/s/RESOLVED_TOKEN";
            string longUrl="https://mp.weixin.qq.com/s?__biz=storage&mid=800&idx=1";
            string html="<script>var biz='storage';var mid='800';var idx='1';</script><h1 id='activity-name'>同篇标题</h1><div id='js_content'>同一正文</div>";
            ArticleRecord Short()=>new ArticleRecord { Id=ArticleIdentity.Create("storage","",1,shortUrl),Biz="storage",SourceMessageId="8000",IdentityUnresolved=true,Url=shortUrl,Title="同篇标题" };
            ArticleRecord Long()=>new ArticleRecord { Id="storage:800:1",Biz="storage",Mid="800",Url=longUrl,Title="同篇标题",SourceMessageId="8000" };
            var repo=new ArticleRepository(DirectoryFor("short-long")); repo.BeginCollectionRun("storage");
            var shortRecord=repo.SaveCollectionPage("storage",new[]{Short()},0,0,true).Single();
            ArticleEnricher.ParseHtml(shortRecord,html); repo.CompleteArticle(shortRecord);
            var refreshed=repo.SaveCollectionPage("storage",new[]{Short()},0,0,true).Single();
            Check(refreshed.Mid=="800" && refreshed.ResolvedArticleId=="storage:800:1" && !refreshed.IdentityUnresolved,
                "短链接列表刷新丢失了正文确认的真实身份");
            var seenLong=repo.SaveCollectionPage("storage",new[]{Long()},0,0,true).Single();
            Check(seenLong.Id==shortRecord.Id && repo.Load("storage").Count==1,"短链接补全后切换长链接产生重复记录");
            var reference=Article(900);
            Check(ArticleMetadata.SetReference(reference,longUrl,"page:share-original-card"),"测试原文引用无效");
            repo.Save(reference);
            Check(ArticleQuery.ResolveReferences(repo.Load("storage")).Count==1 && ArticleQuery.RepresentativeId(reference,repo.Load("storage"))==shortRecord.Id,
                "原文保留短链接存储键后，明确引用未解析到该原文");

            var reversed=new ArticleRepository(DirectoryFor("long-short")); reversed.BeginCollectionRun("storage");
            var longRecord=reversed.SaveCollectionPage("storage",new[]{Long()},0,0,true).Single();
            ArticleEnricher.ParseHtml(longRecord,html); reversed.CompleteArticle(longRecord);
            var laterShort=reversed.SaveCollectionPage("storage",new[]{Short()},0,0,true).Single();
            Check(reversed.Load("storage").Count==2,"未验证的短链接被提前合并");
            ArticleEnricher.ParseHtml(laterShort,html); reversed.CompleteArticle(laterShort);
            var rows=reversed.Load("storage");
            Check(rows.Count==2 && ArticleQuery.ResolveReferences(rows).Single().Id==longRecord.Id
                && ArticleQuery.RepresentativeId(laterShort,rows)==longRecord.Id,"长链接先入库后短链接补全没有按明确身份合并展示并保留来源");
        }

        static void OfflineMigration()
        {
            string directory=DirectoryFor("migration"); var repo=new ArticleRepository(directory);
            var article=Article(10); article.Title="可恢复的文章标题"; article.Status=ArticleStatus.Available;
            article.PublishedAt=new DateTime(2026,8,30,7,23,0);
            long ct=new DateTimeOffset(2026,8,30,7,23,6,TimeSpan.FromHours(8)).ToUnixTimeSeconds();
            article.Html="<html><script>function library(){var ct='123';} var ct='"+ct+"'; var biz='storage'; var mid='10'; var idx='1';</script>"
                +"<h1 id='activity-name'>可恢复的文章标题</h1><div id='js_content'>原始正文</div></html>";
            repo.Save(article);
            using(var connection=Open(directory))using(var command=connection.CreateCommand())
            { command.CommandText="UPDATE articles SET payload=json_set(payload,'$.FutureMetadata','retained')"; command.ExecuteNonQuery(); }
            var before=ReadBody(directory,article.Id);
            var report=ArticleMetadataMaintenance.Repair(repo);
            Check(report.Examined==1 && report.Remaining==0 && File.Exists(report.BackupPath),"旧数据升级缺少备份或未完成");
            Check(repo.Load("storage").Single().PublishedAt==new DateTime(2026,8,30,7,23,6),"旧正文的秒级发布时间未恢复");
            Check(before.SequenceEqual(ReadBody(directory,article.Id)),"元数据升级重写了原始压缩正文");
            using(var connection=Open(directory))using(var command=connection.CreateCommand())
            { command.CommandText="SELECT payload FROM articles"; Check((string)JObject.Parse((string)command.ExecuteScalar())["FutureMetadata"]=="retained","升级丢失未知字段"); }
            using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=report.BackupPath,Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString()))
            {
                connection.Open(); using(var command=connection.CreateCommand())
                { command.CommandText="SELECT payload FROM articles"; Check(JObject.Parse((string)command.ExecuteScalar())["PublishedAt"].Value<DateTime>().Second==0,"升级备份未保存原始数据"); }
            }
            Check(ArticleMetadataMaintenance.Repair(repo).Examined==0,"第二次启动重复扫描已升级正文");
            var unrelated=Path.Combine(directory,"metadata-backups","keep-user-file.txt"); File.WriteAllText(unrelated,"preserve");
            repo.ClearHistory();
            Check(!File.Exists(report.BackupPath) && File.Exists(unrelated),"清空历史遗漏自动备份或删除了无关文件");
        }
        static SqliteConnection Open(string directory)
        {
            var connection=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(directory,"articles.sqlite"),Pooling=false }.ToString());
            connection.Open(); return connection;
        }
        static byte[] ReadBody(string directory,string id)
        {
            using(var connection=Open(directory))using(var command=connection.CreateCommand())
            { command.CommandText="SELECT body FROM article_bodies WHERE id=@id"; command.Parameters.AddWithValue("@id",id); return (byte[])command.ExecuteScalar(); }
        }
        sealed class InterruptedPage : IReadOnlyList<ArticleRecord>
        {
            readonly ArticleRecord first,second; readonly CancellationTokenSource cancellation;
            public InterruptedPage(ArticleRecord first,ArticleRecord second,CancellationTokenSource cancellation)
            { this.first=first; this.second=second; this.cancellation=cancellation; }
            public int Count=>2;
            public ArticleRecord this[int index]=>index==0?first:second;
            public IEnumerator<ArticleRecord> GetEnumerator() { yield return first; cancellation.Cancel(); yield return second; }
            IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
        }
    }
}
