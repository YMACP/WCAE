using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace WCAE
{
    public sealed class CollectionCheckpoint
    {
        public string Biz { get; set; } = "";
        public int CurrentOffset { get; set; }
        public int NextOffset { get; set; }
        public bool HistoryComplete { get; set; }
        public int PagesSaved { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? RefreshStartedAt { get; set; }
        public bool RunCompleted { get; set; }
        public string ActiveArticleId { get; set; } = "";
        public string NextArticleId { get; set; } = "";
        public string LastCompletedArticleId { get; set; } = "";
        public int DetailsCompleted { get; set; }
    }
    public sealed class PendingArticleWork
    {
        public string Id { get; set; } = "";
        public string Biz { get; set; } = "";
        public int Attempts { get; set; }
        public string LastError { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
    }
    public sealed partial class ArticleRepository
    {
        void InitializeCollectionStorage()
        {
            using(var c=Open())using(var cmd=c.CreateCommand())
            {
                cmd.CommandText="CREATE TABLE IF NOT EXISTS collection_pending(id TEXT PRIMARY KEY,biz TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,last_error TEXT NOT NULL DEFAULT '',updated_at TEXT NOT NULL); CREATE INDEX IF NOT EXISTS pending_account ON collection_pending(biz); CREATE TABLE IF NOT EXISTS collection_checkpoints(biz TEXT PRIMARY KEY,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS collection_details(id TEXT PRIMARY KEY,biz TEXT NOT NULL,completed_at TEXT NOT NULL);";
                cmd.ExecuteNonQuery();
            }
        }
        public CollectionCheckpoint BeginCollectionRun(string biz,CancellationToken token=default)
        {
            if(string.IsNullOrWhiteSpace(biz))throw new ArgumentException("缺少公众号标识",nameof(biz));
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                ImportLegacyPending(c,tx,biz);
                var checkpoint=ReadCheckpoint(c,tx,biz);
                if(checkpoint==null)
                    checkpoint=new CollectionCheckpoint { Biz=biz,RefreshStartedAt=NewRunTime(c,tx,biz),UpdatedAt=DateTime.UtcNow };
                else if(!checkpoint.RefreshStartedAt.HasValue)checkpoint.RefreshStartedAt=DateTime.UtcNow;
                if(CountPending(c,tx,biz)>0)checkpoint.RunCompleted=false;
                WriteCheckpoint(c,tx,checkpoint); token.ThrowIfCancellationRequested(); tx.Commit(); return checkpoint;
            }
        }
        public CollectionCheckpoint RestartCollectionRun(string biz,CancellationToken token=default)
        {
            if(string.IsNullOrWhiteSpace(biz))throw new ArgumentException("缺少公众号标识",nameof(biz));
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var previous=ReadCheckpoint(c,tx,biz);
                if(previous==null || !previous.RunCompleted || !previous.HistoryComplete || CountPending(c,tx,biz)!=0)
                    throw new InvalidOperationException("上一轮尚未完成，应从保存位置继续");
                var checkpoint=new CollectionCheckpoint { Biz=biz,RefreshStartedAt=NewRunTime(c,tx,biz),UpdatedAt=DateTime.UtcNow };
                WriteCheckpoint(c,tx,checkpoint); token.ThrowIfCancellationRequested(); tx.Commit(); return checkpoint;
            }
        }
        public CollectionCheckpoint FinishCollectionRun(string biz,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                // BeginTransaction may wait for another SQLite writer. A stop received during
                // that wait must still prevent the finished flag from being committed.
                token.ThrowIfCancellationRequested();
                var checkpoint=ReadCheckpoint(c,tx,biz)??throw new InvalidOperationException("尚未建立采集断点");
                checkpoint.RunCompleted=checkpoint.HistoryComplete && CountPending(c,tx,biz)==0;
                if(checkpoint.RunCompleted) { checkpoint.ActiveArticleId=""; checkpoint.NextArticleId=""; }
                checkpoint.UpdatedAt=DateTime.UtcNow; WriteCheckpoint(c,tx,checkpoint);
                token.ThrowIfCancellationRequested(); tx.Commit(); return checkpoint;
            }
        }
        public void MarkArticleStarted(string biz,string id,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                if(PendingPosition(c,tx,biz,id)==null)throw new InvalidOperationException("文章不在待处理队列中");
                var checkpoint=ReadCheckpoint(c,tx,biz)??throw new InvalidOperationException("尚未建立采集断点");
                checkpoint.ActiveArticleId=id; checkpoint.NextArticleId=id; checkpoint.RunCompleted=false;
                checkpoint.UpdatedAt=DateTime.UtcNow; WriteCheckpoint(c,tx,checkpoint); token.ThrowIfCancellationRequested(); tx.Commit();
            }
        }
        public void DeferArticle(string biz,string id,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                AdvanceArticleCursor(c,tx,biz,id,false);
                token.ThrowIfCancellationRequested(); tx.Commit();
            }
        }
        static void ImportLegacyPending(SqliteConnection c,SqliteTransaction tx,string biz)
        {
            // A previous version saved listing rows without a durable queue. Import before
            // deciding whether the previous run finished, including rows no longer returned
            // by the latest history list. Existing attempts/errors retain their original data.
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx;
                cmd.CommandText="INSERT INTO collection_pending(id,biz,updated_at) SELECT a.id,a.biz,@at FROM articles a LEFT JOIN article_bodies b ON b.id=a.id WHERE a.biz=@biz AND a.id NOT LIKE 'native-profile:%' AND CASE WHEN json_valid(a.payload) THEN (COALESCE(json_extract(a.payload,'$.NativeTextFingerprint'),'')='' AND COALESCE(json_extract(a.payload,'$.NativeTextContent'),'')='' AND (COALESCE(json_extract(a.payload,'$.Status'),0) IN (@pending,@failed,@restricted) OR (json_extract(a.payload,'$.Status')=@available AND (b.id IS NULL OR length(b.body)=0)))) ELSE 0 END ON CONFLICT(id) DO NOTHING";
                cmd.Parameters.AddWithValue("@biz",biz); cmd.Parameters.AddWithValue("@at",DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@pending",(int)ArticleStatus.Pending); cmd.Parameters.AddWithValue("@failed",(int)ArticleStatus.Failed);
                cmd.Parameters.AddWithValue("@restricted",(int)ArticleStatus.Restricted); cmd.Parameters.AddWithValue("@available",(int)ArticleStatus.Available);
                cmd.ExecuteNonQuery();
            }
        }
        public CollectionCheckpoint GetCollectionCheckpoint(string biz)
        { lock(gate)using(var c=Open())return ReadCheckpoint(c,null,biz); }
        static DateTime NewRunTime(SqliteConnection c,SqliteTransaction tx,string biz)
        {
            var now=DateTime.UtcNow;
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="SELECT MAX(completed_at) FROM collection_details WHERE biz=@biz"; cmd.Parameters.AddWithValue("@biz",biz);
                if(cmd.ExecuteScalar() is string value && DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var completed) && completed>=now && completed<DateTime.MaxValue)return completed.AddTicks(1);
            }
            return now;
        }
        static CollectionCheckpoint ReadCheckpoint(SqliteConnection c,SqliteTransaction tx,string biz)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="SELECT payload FROM collection_checkpoints WHERE biz=@biz";
                cmd.Parameters.AddWithValue("@biz",biz??"");
                return cmd.ExecuteScalar() is string value?JsonConvert.DeserializeObject<CollectionCheckpoint>(value):null;
            }
        }
        static void WriteCheckpoint(SqliteConnection c,SqliteTransaction tx,CollectionCheckpoint checkpoint)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="INSERT INTO collection_checkpoints(biz,payload) VALUES(@biz,@data) ON CONFLICT(biz) DO UPDATE SET payload=excluded.payload";
                cmd.Parameters.AddWithValue("@biz",checkpoint.Biz); cmd.Parameters.AddWithValue("@data",JsonConvert.SerializeObject(checkpoint)); cmd.ExecuteNonQuery();
            }
        }
        public List<ArticleRecord> SaveCollectionPage(string biz,IReadOnlyList<ArticleRecord> articles,int currentOffset,int nextOffset,bool historyComplete,CancellationToken token=default,bool skipCompleted=false)
        {
            if(string.IsNullOrWhiteSpace(biz))throw new ArgumentException("缺少公众号标识",nameof(biz));
            if(articles==null)throw new ArgumentNullException(nameof(articles));
            if(currentOffset<0 || nextOffset<0)throw new ArgumentOutOfRangeException(nameof(currentOffset));
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var checkpoint=ReadCheckpoint(c,tx,biz)??new CollectionCheckpoint { Biz=biz,RefreshStartedAt=DateTime.UtcNow };
                checkpoint.RefreshStartedAt??=DateTime.UtcNow;
                var repairs=new Dictionary<string,string>(StringComparer.Ordinal); var result=new List<ArticleRecord>();
                foreach(var incoming in articles)
                {
                    token.ThrowIfCancellationRequested();
                    if(incoming==null || incoming.Biz!=biz)throw new ArgumentException("列表中包含其他公众号的文章");
                    var saved=SaveCore(c,tx,incoming,repairs); result.Add(saved);
                    // Listing writes update UpdatedAt; detail completion has its own durable clock.
                    if(saved.Status==ArticleStatus.Deleted || (skipCompleted && HasCompletedBody(c,tx,saved)) || DetailCompleted(c,tx,saved,checkpoint.RefreshStartedAt.Value))
                        RemovePending(c,tx,biz,saved.Id);
                    else Enqueue(c,tx,biz,saved.Id);
                }
                checkpoint.CurrentOffset=currentOffset; checkpoint.NextOffset=nextOffset;
                checkpoint.HistoryComplete=historyComplete; checkpoint.PagesSaved=currentOffset==0?1:checkpoint.PagesSaved+1;
                checkpoint.RunCompleted=false;
                if(string.IsNullOrEmpty(checkpoint.ActiveArticleId))
                {
                    // A newly downloaded page follows all already attempted rows of the prior
                    // page; older bounded failures stay durable without jumping ahead of it.
                    var first=result.FirstOrDefault(a=>PendingPosition(c,tx,biz,a.Id).HasValue);
                    if(first!=null)checkpoint.NextArticleId=first.Id;
                }
                checkpoint.UpdatedAt=DateTime.UtcNow; WriteCheckpoint(c,tx,checkpoint);
                token.ThrowIfCancellationRequested(); tx.Commit(); CommitNameRepairs(repairs); return result;
            }
        }
        // A passive snapshot is a separate source. Its rows and work items are committed
        // together, without advancing or replacing any history/native navigation cursor.
        public List<ArticleRecord> SaveCollectionSupplement(string biz,IReadOnlyList<ArticleRecord> articles,CancellationToken token=default)
        {
            if(string.IsNullOrWhiteSpace(biz))throw new ArgumentException("缺少公众号标识",nameof(biz));
            if(articles==null)throw new ArgumentNullException(nameof(articles));
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var repairs=new Dictionary<string,string>(StringComparer.Ordinal);
                var result=new List<ArticleRecord>(); var seen=new HashSet<string>(StringComparer.Ordinal);
                foreach(var incoming in articles.OrderByDescending(a=>a?.PublishedAt)
                    .ThenByDescending(a=>(a?.Mid??"").TrimStart('0').Length).ThenByDescending(a=>a?.Mid,StringComparer.Ordinal)
                    .ThenBy(a=>a?.Idx).ThenBy(a=>a?.Id,StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    if(incoming==null || incoming.Biz!=biz)throw new ArgumentException("补充列表中包含其他公众号的文章");
                    string canonical=ArticleIdentityEvidence.Canonical(incoming);
                    if(canonical.Length==0 || incoming.Id!=canonical || incoming.Id.StartsWith("native-profile:",StringComparison.Ordinal)
                        || ArticleIdentity.Create(incoming.Biz,incoming.Mid,incoming.Idx,incoming.Url,incoming.SourceMessageId)!=canonical)
                        throw new ArgumentException("补充文章缺少一致的真实链接与身份");
                    var candidate=Newtonsoft.Json.Linq.JObject.FromObject(incoming).ToObject<ArticleRecord>();
                    candidate.Html="";
                    // Cache listings are discovery evidence, not successful body responses.
                    if(candidate.Status!=ArticleStatus.Deleted)candidate.Status=ArticleStatus.Pending;
                    var saved=SaveCore(c,tx,candidate,repairs);
                    if(!seen.Add(saved.Id))continue;
                    result.Add(saved);
                    // Preserve existing attempts, row order and active work even when the
                    // cache repeats it. Only genuinely new unfinished work is appended.
                    if(PendingPosition(c,tx,biz,saved.Id).HasValue)continue;
                    if(saved.Status!=ArticleStatus.Deleted && !HasCompletedBody(c,tx,saved))Enqueue(c,tx,biz,saved.Id);
                }
                token.ThrowIfCancellationRequested(); tx.Commit(); CommitNameRepairs(repairs); return result;
            }
        }
        static bool HasCompletedBody(SqliteConnection c,SqliteTransaction tx,ArticleRecord article)
            => ArticleIdentityEvidence.Canonical(article).Length>0 && article.Status==ArticleStatus.Available && HasUsableBody(c,tx,article.Biz,article.Id);
        public bool IsCollectionArticleCompleted(string biz,string id)
        {
            lock(gate)using(var c=Open())
            {
                var article=ReadArticle(c,null,biz,id);
                return article!=null && ArticleIdentityEvidence.Canonical(article).Length>0
                    && (article.Status==ArticleStatus.Deleted || HasCompletedBody(c,null,article));
            }
        }
        static bool HasUsableBody(SqliteConnection c,SqliteTransaction tx,string biz,string id)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="SELECT b.body FROM article_bodies b JOIN articles a ON a.id=b.id WHERE a.biz=@biz AND a.id=@id";
                cmd.Parameters.AddWithValue("@biz",biz??""); cmd.Parameters.AddWithValue("@id",id??"");
                if(!(cmd.ExecuteScalar() is byte[] bytes) || bytes.Length==0)return false;
                try
                {
                    using(var input=new MemoryStream(bytes))using(var gzip=new GZipStream(input,CompressionMode.Decompress))using(var reader=new StreamReader(gzip))
                    { int value; while((value=reader.Read())>=0)if(!char.IsWhiteSpace((char)value))return true; }
                }
                catch(InvalidDataException) { }
                return false;
            }
        }
        static bool DetailCompleted(SqliteConnection c,SqliteTransaction tx,ArticleRecord article,DateTime since)
        {
            if(article.Status!=ArticleStatus.Available)return false;
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="SELECT d.completed_at FROM collection_details d JOIN article_bodies b ON b.id=d.id WHERE d.id=@id AND d.biz=@biz";
                cmd.Parameters.AddWithValue("@id",article.Id); cmd.Parameters.AddWithValue("@biz",article.Biz);
                return cmd.ExecuteScalar() is string value && DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var completed) && completed>=since;
            }
        }
        static void Enqueue(SqliteConnection c,SqliteTransaction tx,string biz,string id)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="INSERT INTO collection_pending(id,biz,updated_at) VALUES(@id,@biz,@at) ON CONFLICT(id) DO NOTHING";
                cmd.Parameters.AddWithValue("@id",id); cmd.Parameters.AddWithValue("@biz",biz); cmd.Parameters.AddWithValue("@at",DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
            }
        }
        static void RemovePending(SqliteConnection c,SqliteTransaction tx,string biz,string id)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="DELETE FROM collection_pending WHERE biz=@biz AND id=@id";
                cmd.Parameters.AddWithValue("@biz",biz); cmd.Parameters.AddWithValue("@id",id); cmd.ExecuteNonQuery();
            }
        }
        static long? PendingPosition(SqliteConnection c,SqliteTransaction tx,string biz,string id)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="SELECT rowid FROM collection_pending WHERE biz=@biz AND id=@id";
                cmd.Parameters.AddWithValue("@biz",biz??""); cmd.Parameters.AddWithValue("@id",id??"");
                var value=cmd.ExecuteScalar(); return value==null || value is DBNull?null:Convert.ToInt64(value);
            }
        }
        static void AdvanceArticleCursor(SqliteConnection c,SqliteTransaction tx,string biz,string id,bool completed)
        {
            var checkpoint=ReadCheckpoint(c,tx,biz); if(checkpoint==null)return;
            long? position=PendingPosition(c,tx,biz,id);
            if(completed && position.HasValue)
            { checkpoint.LastCompletedArticleId=id; checkpoint.DetailsCompleted++; }
            if(string.IsNullOrEmpty(checkpoint.ActiveArticleId) || checkpoint.ActiveArticleId==id)
            {
                checkpoint.ActiveArticleId="";
                using(var cmd=c.CreateCommand())
                {
                    cmd.Transaction=tx;
                    cmd.CommandText="SELECT id FROM collection_pending WHERE biz=@biz AND id<>@id ORDER BY CASE WHEN rowid>@position THEN 0 ELSE 1 END,rowid LIMIT 1";
                    cmd.Parameters.AddWithValue("@biz",biz); cmd.Parameters.AddWithValue("@id",id); cmd.Parameters.AddWithValue("@position",position??0);
                    checkpoint.NextArticleId=cmd.ExecuteScalar() as string??"";
                }
            }
            checkpoint.UpdatedAt=DateTime.UtcNow; WriteCheckpoint(c,tx,checkpoint);
        }
        public List<ArticleRecord> GetPendingArticles(string biz)
        {
            var result=new List<ArticleRecord>();
            lock(gate)using(var c=Open())using(var cmd=c.CreateCommand())
            {
                cmd.CommandText="SELECT a.payload FROM collection_pending p JOIN articles a ON a.id=p.id AND a.biz=p.biz WHERE p.biz=@biz ORDER BY p.rowid";
                cmd.Parameters.AddWithValue("@biz",biz??"");
                using(var reader=cmd.ExecuteReader())while(reader.Read())
                { var article=JsonConvert.DeserializeObject<ArticleRecord>(reader.GetString(0)); if(article!=null)result.Add(article); }
                var checkpoint=ReadCheckpoint(c,null,biz);
                string cursor=!string.IsNullOrEmpty(checkpoint?.ActiveArticleId)?checkpoint.ActiveArticleId:checkpoint?.NextArticleId;
                int index=string.IsNullOrEmpty(cursor)?-1:result.FindIndex(a=>a.Id==cursor);
                if(index>0)result=result.Skip(index).Concat(result.Take(index)).ToList();
            }
            return result;
        }
        public List<PendingArticleWork> GetPendingWork(string biz)
        {
            var result=new List<PendingArticleWork>();
            lock(gate)using(var c=Open())using(var cmd=c.CreateCommand())
            {
                cmd.CommandText="SELECT id,biz,attempts,last_error,updated_at FROM collection_pending WHERE biz=@biz ORDER BY rowid";
                cmd.Parameters.AddWithValue("@biz",biz??"");
                using(var reader=cmd.ExecuteReader())while(reader.Read())result.Add(new PendingArticleWork {
                    Id=reader.GetString(0),Biz=reader.GetString(1),Attempts=reader.GetInt32(2),LastError=reader.GetString(3),UpdatedAt=DateTime.Parse(reader.GetString(4),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind) });
            }
            return result;
        }
        public int PendingCount(string biz) { lock(gate)using(var c=Open())return CountPending(c,null,biz); }
        static int CountPending(SqliteConnection c,SqliteTransaction tx,string biz)
        {
            using(var cmd=c.CreateCommand()) { cmd.Transaction=tx; cmd.CommandText="SELECT COUNT(*) FROM collection_pending WHERE biz=@biz"; cmd.Parameters.AddWithValue("@biz",biz??""); return Convert.ToInt32(cmd.ExecuteScalar()); }
        }
        public ArticleRecord CompleteArticle(ArticleRecord article,CancellationToken token=default)
        {
            if(article==null)throw new ArgumentNullException(nameof(article));
            token.ThrowIfCancellationRequested();
            if(article.Status!=ArticleStatus.Available && article.Status!=ArticleStatus.Deleted)return FailArticle(article,article.StatusDetail,token);
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var repairs=new Dictionary<string,string>(StringComparer.Ordinal);
                if(article.Status==ArticleStatus.Available && string.IsNullOrWhiteSpace(article.Html))
                {
                    bool hasBody=HasUsableBody(c,tx,article.Biz,article.Id);
                    if(!hasBody)
                    { var failure=FailCore(c,tx,article,"文章标记可访问，但尚未保存正文",repairs); token.ThrowIfCancellationRequested(); tx.Commit(); CommitNameRepairs(repairs); return failure; }
                }
                var saved=SaveCore(c,tx,article,repairs);
                using(var cmd=c.CreateCommand())
                {
                    cmd.Transaction=tx; cmd.CommandText="INSERT INTO collection_details(id,biz,completed_at) VALUES(@id,@biz,@at) ON CONFLICT(id) DO UPDATE SET completed_at=excluded.completed_at WHERE collection_details.biz=excluded.biz";
                    cmd.Parameters.AddWithValue("@id",saved.Id); cmd.Parameters.AddWithValue("@biz",saved.Biz); cmd.Parameters.AddWithValue("@at",DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
                }
                AdvanceArticleCursor(c,tx,saved.Biz,saved.Id,true);
                RemovePending(c,tx,saved.Biz,saved.Id); token.ThrowIfCancellationRequested(); tx.Commit(); CommitNameRepairs(repairs); return saved;
            }
        }
        public ArticleRecord FailArticle(string biz,string id,string error,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var article=ReadArticle(c,tx,biz,id)??throw new InvalidOperationException("待补全文章不存在");
                var repairs=new Dictionary<string,string>(StringComparer.Ordinal);
                var saved=FailCore(c,tx,article,error,repairs); token.ThrowIfCancellationRequested(); tx.Commit(); CommitNameRepairs(repairs); return saved;
            }
        }
        public ArticleRecord FailArticle(ArticleRecord article,string error,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var repairs=new Dictionary<string,string>(StringComparer.Ordinal);
                var saved=FailCore(c,tx,article,error,repairs); token.ThrowIfCancellationRequested(); tx.Commit(); CommitNameRepairs(repairs); return saved;
            }
        }
        ArticleRecord FailCore(SqliteConnection c,SqliteTransaction tx,ArticleRecord article,string error,Dictionary<string,string> repairs)
        {
            if(article==null)throw new ArgumentNullException(nameof(article));
            var failed=Newtonsoft.Json.Linq.JObject.FromObject(article).ToObject<ArticleRecord>();
            if(failed.Status!=ArticleStatus.Restricted)failed.Status=ArticleStatus.Failed;
            failed.StatusDetail=error??"";
            var saved=SaveCore(c,tx,failed,repairs); Enqueue(c,tx,saved.Biz,saved.Id);
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="UPDATE collection_pending SET attempts=attempts+1,last_error=@error,updated_at=@at WHERE biz=@biz AND id=@id";
                cmd.Parameters.AddWithValue("@error",error??""); cmd.Parameters.AddWithValue("@at",DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("@biz",saved.Biz); cmd.Parameters.AddWithValue("@id",saved.Id); cmd.ExecuteNonQuery();
            }
            return saved;
        }
    }
}
