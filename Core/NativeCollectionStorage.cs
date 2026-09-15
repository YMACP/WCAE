using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace WCAE
{
    public sealed partial class ArticleRepository
    {
        // These tables belong exclusively to the visible native-profile source. Article
        // metadata/bodies are shared, but legacy pending rows and offsets are never changed.
        void InitializeNativeCollectionStorage()
        {
            using(var c=Open())using(var cmd=c.CreateCommand())
            {
                cmd.CommandText="CREATE TABLE IF NOT EXISTS native_collection_states(biz TEXT PRIMARY KEY,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS native_collection_completed(biz TEXT NOT NULL,run_id TEXT NOT NULL,article_id TEXT NOT NULL,canonical_id TEXT NOT NULL DEFAULT '',completed_at TEXT NOT NULL,PRIMARY KEY(biz,run_id,article_id)); CREATE INDEX IF NOT EXISTS native_completed_identity ON native_collection_completed(biz,run_id,canonical_id);";
                cmd.ExecuteNonQuery();
            }
        }

        public NativeCollectionState BeginNativeCollection(string biz,string userName,CancellationToken token=default)
        {
            ValidateNativeBiz(biz); token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var state=ReadNativeState(c,tx,biz);
                string supplied=(userName??"").Trim();
                if(state!=null && state.UserName.Length>0 && supplied.Length>0 && state.UserName!=supplied)
                    throw new InvalidOperationException("公众号主页与已保存的采集账号不一致。");
                if(state==null || state.Phase=="Complete")
                {
                    state=new NativeCollectionState { Biz=biz,UserName=supplied.Length>0?supplied:state?.UserName??"",
                        RunId=Guid.NewGuid().ToString("N"),UpdatedAt=DateTime.UtcNow };
                    WriteNativeState(c,tx,state);
                }
                else if(state.UserName.Length==0 && supplied.Length>0)
                {
                    state.UserName=supplied; state.UpdatedAt=DateTime.UtcNow;
                    WriteNativeState(c,tx,state);
                }
                token.ThrowIfCancellationRequested(); tx.Commit(); return state;
            }
        }

        public NativeCollectionState GetNativeCollectionState(string biz)
        { ValidateNativeBiz(biz); lock(gate)using(var c=Open())return ReadNativeState(c,null,biz); }

        public ArticleRecord GetNativeCurrentArticle(string biz)
        {
            ValidateNativeBiz(biz);
            lock(gate)using(var c=Open())
            {
                var state=ReadNativeState(c,null,biz);
                if(string.IsNullOrEmpty(state?.CurrentArticleId))return null;
                return ReadArticle(c,null,biz,state.CurrentArticleId)
                    ??throw new InvalidOperationException("当前文章记录缺失，已保留断点，不能跳过继续。");
            }
        }

        public ArticleRecord SaveNativeDiscovered(string biz,ArticleRecord article,NativeArticleAnchor anchor,CancellationToken token=default)
        {
            ValidateNativeBiz(biz);
            if(article==null)throw new ArgumentNullException(nameof(article));
            if(anchor==null)throw new ArgumentNullException(nameof(anchor));
            if(article.Biz!=biz)throw new ArgumentException("文章不属于当前公众号。",nameof(article));
            bool snapshot=NativeProfileTextSnapshot.IsSnapshot(article);
            if(snapshot)ValidateNativeSnapshotAnchor(article,anchor);
            else if(!string.IsNullOrEmpty(anchor.NativeTextFingerprint))
                throw new InvalidOperationException("普通文章不能使用主页正文副本的断点。");
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var state=RequireNativeState(c,tx,biz);
                if(state.Phase=="Complete")throw new InvalidOperationException("请先开始新的主页采集轮次。");
                if(snapshot)
                {
                    var prior=ReadArticle(c,tx,biz,article.Id);
                    if(prior!=null && (!NativeProfileTextSnapshot.IsSnapshot(prior)
                        || prior.NativeTextFingerprint!=article.NativeTextFingerprint || prior.NativeDateLabel!=article.NativeDateLabel))
                        throw new InvalidOperationException("不同的主页正文出现位置不能复用同一本地标识。");
                }
                var repairs=new Dictionary<string,string>(StringComparer.Ordinal);
                var saved=SaveCore(c,tx,article,repairs);
                if(state.CurrentArticleId.Length>0 && state.CurrentArticleId!=saved.Id)
                    throw new InvalidOperationException("当前文章尚未返回主页，不能用下一篇覆盖断点。");
                state.CurrentArticleId=saved.Id;
                state.CurrentAnchor=CloneNativeAnchor(anchor); state.CurrentAnchor.ArticleId=saved.Id;
                state.Phase=IsNativeCompleted(c,tx,state,saved.Id,saved)?"Returning":"ArticleReady";
                state.EndConfirmed=false; state.LastError=""; state.UpdatedAt=DateTime.UtcNow;
                WriteNativeState(c,tx,state);
                token.ThrowIfCancellationRequested(); tx.Commit(); CommitNameRepairs(repairs); return saved;
            }
        }

        public ArticleRecord CompleteNativeArticle(ArticleRecord article,CancellationToken token=default)
        {
            if(article==null)throw new ArgumentNullException(nameof(article));
            if(NativeProfileTextSnapshot.IsSnapshot(article))
                throw new InvalidOperationException("主页正文副本必须使用独立保存分支，不能当作已访问原文。");
            ValidateNativeBiz(article.Biz); token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var state=RequireNativeState(c,tx,article.Biz);
                if(!string.IsNullOrEmpty(state.CurrentAnchor?.NativeTextFingerprint))
                    throw new InvalidOperationException("当前断点是主页正文副本，不能保存普通文章处理结果。");
                if(state.Phase=="Returning")
                {
                    var current=ReadArticle(c,tx,state.Biz,state.CurrentArticleId)
                        ??throw new InvalidOperationException("已完成的当前文章记录缺失。");
                    string proof=ArticleIdentityEvidence.Canonical(article);
                    if(article.Id!=current.Id && (proof.Length==0 || proof!=ArticleIdentityEvidence.Canonical(current)))
                        throw new InvalidOperationException("处理结果与当前文章断点不一致。");
                    // A completion callback delivered twice must neither count twice nor
                    // overwrite the body/status already committed before returning to UI.
                    token.ThrowIfCancellationRequested(); tx.Commit(); return current;
                }
                var repairs=new Dictionary<string,string>(StringComparer.Ordinal);
                var candidate=CloneNativeArticle(article);
                // Whitespace from a failed request is not a body, and must not replace an
                // already cached valid body while checking whether completion is permitted.
                if(string.IsNullOrWhiteSpace(candidate.Html))candidate.Html="";
                var saved=SaveCore(c,tx,candidate,repairs);
                RequireNativeCurrent(state,saved.Id);
                bool final=candidate.Status==ArticleStatus.Deleted
                    || candidate.Status==ArticleStatus.Available && HasNativeBody(c,tx,saved.Id);
                if(!final)
                {
                    string error=saved.Status==ArticleStatus.Available?"文章尚未保存有效正文，已保留当前篇供续采。":saved.StatusDetail;
                    saved=SaveNativeFailure(c,tx,state,candidate,error,repairs);
                }
                else
                {
                    if(!IsNativeCompleted(c,tx,state,saved.Id,saved))
                    {
                        using(var cmd=c.CreateCommand())
                        {
                            cmd.Transaction=tx;
                            cmd.CommandText="INSERT INTO native_collection_completed(biz,run_id,article_id,canonical_id,completed_at) VALUES(@biz,@run,@id,@canonical,@at)";
                            cmd.Parameters.AddWithValue("@biz",state.Biz); cmd.Parameters.AddWithValue("@run",state.RunId);
                            cmd.Parameters.AddWithValue("@id",saved.Id); cmd.Parameters.AddWithValue("@canonical",ArticleIdentityEvidence.Canonical(saved));
                            cmd.Parameters.AddWithValue("@at",DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
                        }
                        state.CompletedCount++;
                    }
                    state.Phase="Returning"; state.LastError=""; state.UpdatedAt=DateTime.UtcNow;
                    WriteNativeState(c,tx,state);
                }
                token.ThrowIfCancellationRequested(); tx.Commit(); CommitNameRepairs(repairs); return saved;
            }
        }

        public ArticleRecord CompleteNativeSnapshot(ArticleRecord article,CancellationToken token=default)
        {
            NativeProfileTextSnapshot.Validate(article); token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var state=RequireNativeState(c,tx,article.Biz);
                RequireNativeCurrent(state,article.Id);
                ValidateNativeSnapshotAnchor(article,state.CurrentAnchor);
                var prior=ReadArticle(c,tx,article.Biz,article.Id)
                    ??throw new InvalidOperationException("当前主页正文副本不存在，已保留断点。");
                NativeProfileTextSnapshot.Validate(prior);
                if(prior.NativeTextFingerprint!=article.NativeTextFingerprint || prior.NativeDateLabel!=article.NativeDateLabel)
                    throw new InvalidOperationException("当前主页正文证据与保存时不一致，未推进断点。");
                if(state.Phase=="Returning")
                {
                    if(prior.Status!=ArticleStatus.LocalSnapshot || !HasNativeBody(c,tx,prior.Id)
                        || !IsNativeCompleted(c,tx,state,prior.Id,prior))
                        throw new InvalidOperationException("主页正文副本的完成记录不完整，未推进断点。");
                    token.ThrowIfCancellationRequested(); tx.Commit(); return prior;
                }
                // Rebuild from the persisted observation, not a potentially mutated
                // caller copy. The complete text and current occurrence were committed
                // together before entering ArticleReady.
                var completed=NativeProfileTextSnapshot.Finalize(prior);
                var repairs=new Dictionary<string,string>(StringComparer.Ordinal);
                var saved=SaveCore(c,tx,completed,repairs);
                ValidateNativeSnapshotAnchor(saved,state.CurrentAnchor);
                if(!HasNativeBody(c,tx,saved.Id))
                    throw new InvalidOperationException("主页正文副本没有有效正文，已保留当前项。");
                if(!IsNativeCompleted(c,tx,state,saved.Id,saved))
                {
                    using(var cmd=c.CreateCommand())
                    {
                        cmd.Transaction=tx;
                        cmd.CommandText="INSERT INTO native_collection_completed(biz,run_id,article_id,canonical_id,completed_at) VALUES(@biz,@run,@id,'',@at)";
                        cmd.Parameters.AddWithValue("@biz",state.Biz); cmd.Parameters.AddWithValue("@run",state.RunId);
                        cmd.Parameters.AddWithValue("@id",saved.Id); cmd.Parameters.AddWithValue("@at",DateTime.UtcNow.ToString("O"));
                        cmd.ExecuteNonQuery();
                    }
                    state.CompletedCount++;
                }
                state.Phase="Returning"; state.LastError=""; state.UpdatedAt=DateTime.UtcNow;
                WriteNativeState(c,tx,state);
                token.ThrowIfCancellationRequested(); tx.Commit(); CommitNameRepairs(repairs); return saved;
            }
        }

        static void ValidateNativeSnapshotAnchor(ArticleRecord article,NativeArticleAnchor anchor)
        {
            NativeProfileTextSnapshot.Validate(article);
            if(anchor==null || anchor.ArticleId!=article.Id || !string.IsNullOrEmpty(anchor.CanonicalId)
                || anchor.RequiresArticleNavigation || anchor.VerifyDeletionOnly
                || anchor.NativeTextFingerprint!=article.NativeTextFingerprint || anchor.NativeDateLabel!=article.NativeDateLabel)
                throw new InvalidOperationException("主页正文副本与当前出现位置证据不一致，已保留断点。");
        }

        public ArticleRecord FailNativeArticle(ArticleRecord article,string error,CancellationToken token=default)
        {
            if(article==null)throw new ArgumentNullException(nameof(article));
            ValidateNativeBiz(article.Biz); token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var state=RequireNativeState(c,tx,article.Biz);
                if(state.Phase!="ArticleReady")throw new InvalidOperationException("当前篇不在等待正文状态。");
                var repairs=new Dictionary<string,string>(StringComparer.Ordinal);
                var saved=SaveNativeFailure(c,tx,state,article,error,repairs);
                token.ThrowIfCancellationRequested(); tx.Commit(); CommitNameRepairs(repairs); return saved;
            }
        }

        ArticleRecord SaveNativeFailure(SqliteConnection c,SqliteTransaction tx,NativeCollectionState state,ArticleRecord article,string error,Dictionary<string,string> repairs)
        {
            var failed=CloneNativeArticle(article);
            if(failed.Status!=ArticleStatus.Restricted)failed.Status=ArticleStatus.Failed;
            failed.StatusDetail=string.IsNullOrWhiteSpace(error)?"文章尚未处理完成，已保留当前篇供续采。":error;
            if(string.IsNullOrWhiteSpace(failed.Html))failed.Html="";
            var saved=SaveCore(c,tx,failed,repairs); RequireNativeCurrent(state,saved.Id);
            state.Phase="ArticleReady"; state.LastError=saved.StatusDetail; state.UpdatedAt=DateTime.UtcNow;
            WriteNativeState(c,tx,state); return saved;
        }

        public NativeCollectionState AdvanceNativeNavigation(string biz,CancellationToken token=default)
        {
            ValidateNativeBiz(biz); token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var state=RequireNativeState(c,tx,biz);
                if(state.Phase!="Returning" || state.CurrentArticleId.Length==0 || state.CurrentAnchor==null
                    || state.CurrentAnchor.ArticleId!=state.CurrentArticleId || !IsNativeCompleted(c,tx,state,state.CurrentArticleId))
                    throw new InvalidOperationException("当前篇尚未完成，不能推进主页导航断点。");
                state.LastAnchor=CloneNativeAnchor(state.CurrentAnchor); state.CurrentAnchor=null;
                state.CurrentArticleId=""; state.Phase="Discovering"; state.LastError=""; state.UpdatedAt=DateTime.UtcNow;
                WriteNativeState(c,tx,state);
                token.ThrowIfCancellationRequested(); tx.Commit(); return state;
            }
        }

        public NativeCollectionState FinishNativeCollection(string biz,CancellationToken token=default)
        {
            ValidateNativeBiz(biz); token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var state=RequireNativeState(c,tx,biz);
                if(state.Phase!="Discovering" || state.CurrentArticleId.Length>0 || state.CurrentAnchor!=null || state.CompletedCount<=0)
                    throw new InvalidOperationException("主页尚未完成非空文章列表的逐篇处理，不能标记采集结束。");
                state.Phase="Complete"; state.EndConfirmed=true; state.LastError=""; state.UpdatedAt=DateTime.UtcNow;
                WriteNativeState(c,tx,state);
                token.ThrowIfCancellationRequested(); tx.Commit(); return state;
            }
        }

        public bool IsNativeArticleCompleted(string biz,string id)
        {
            ValidateNativeBiz(biz); if(string.IsNullOrWhiteSpace(id))return false;
            lock(gate)using(var c=Open())
            { var state=ReadNativeState(c,null,biz); return state!=null && IsNativeCompleted(c,null,state,id); }
        }

        static bool IsNativeCompleted(SqliteConnection c,SqliteTransaction tx,NativeCollectionState state,string id,ArticleRecord article=null)
        {
            string canonical=ArticleIdentityEvidence.Canonical(article??ReadArticle(c,tx,state.Biz,id));
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx;
                cmd.CommandText="SELECT EXISTS(SELECT 1 FROM native_collection_completed WHERE biz=@biz AND run_id=@run AND (article_id=@id OR canonical_id=@id OR (@canonical<>'' AND canonical_id=@canonical)))";
                cmd.Parameters.AddWithValue("@biz",state.Biz); cmd.Parameters.AddWithValue("@run",state.RunId);
                cmd.Parameters.AddWithValue("@id",id); cmd.Parameters.AddWithValue("@canonical",canonical);
                return Convert.ToInt32(cmd.ExecuteScalar())!=0;
            }
        }

        static bool HasNativeBody(SqliteConnection c,SqliteTransaction tx,string id)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="SELECT body FROM article_bodies WHERE id=@id"; cmd.Parameters.AddWithValue("@id",id);
                if(!(cmd.ExecuteScalar() is byte[] bytes) || bytes.Length==0)return false;
                try
                {
                    using(var stream=new MemoryStream(bytes))using(var gzip=new GZipStream(stream,CompressionMode.Decompress))using(var reader=new StreamReader(gzip))
                    {
                        var buffer=new char[1024]; int count;
                        while((count=reader.Read(buffer,0,buffer.Length))>0)
                            for(int i=0;i<count;i++)if(!char.IsWhiteSpace(buffer[i]))return true;
                        return false;
                    }
                }
                catch(InvalidDataException) { return false; }
                catch(IOException) { return false; }
            }
        }

        static NativeCollectionState ReadNativeState(SqliteConnection c,SqliteTransaction tx,string biz)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="SELECT payload FROM native_collection_states WHERE biz=@biz"; cmd.Parameters.AddWithValue("@biz",biz);
                if(!(cmd.ExecuteScalar() is string json))return null;
                var state=JsonConvert.DeserializeObject<NativeCollectionState>(json);
                if(state==null || state.Biz!=biz || string.IsNullOrWhiteSpace(state.RunId)
                    || (state.Phase!="Discovering" && state.Phase!="ArticleReady" && state.Phase!="Returning" && state.Phase!="Complete"))
                    throw new InvalidOperationException("主页采集断点无效，未自动重置或跳过文章。");
                state.UserName??=""; state.CurrentArticleId??=""; return state;
            }
        }
        static NativeCollectionState RequireNativeState(SqliteConnection c,SqliteTransaction tx,string biz)
            => ReadNativeState(c,tx,biz)??throw new InvalidOperationException("尚未开始此公众号的主页采集。");
        static void RequireNativeCurrent(NativeCollectionState state,string id)
        {
            if((state.Phase!="ArticleReady" && state.Phase!="Returning") || state.CurrentArticleId.Length==0 || state.CurrentArticleId!=id)
                throw new InvalidOperationException("处理结果与当前文章断点不一致。");
        }
        static void WriteNativeState(SqliteConnection c,SqliteTransaction tx,NativeCollectionState state)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=tx; cmd.CommandText="INSERT INTO native_collection_states(biz,payload) VALUES(@biz,@data) ON CONFLICT(biz) DO UPDATE SET payload=excluded.payload";
                cmd.Parameters.AddWithValue("@biz",state.Biz); cmd.Parameters.AddWithValue("@data",JsonConvert.SerializeObject(state)); cmd.ExecuteNonQuery();
            }
        }
        static void ValidateNativeBiz(string biz)
        { if(string.IsNullOrWhiteSpace(biz))throw new ArgumentException("缺少公众号标识。",nameof(biz)); }
        static NativeArticleAnchor CloneNativeAnchor(NativeArticleAnchor value)
            => value==null?null:JsonConvert.DeserializeObject<NativeArticleAnchor>(JsonConvert.SerializeObject(value));
        static ArticleRecord CloneNativeArticle(ArticleRecord value)
            => Newtonsoft.Json.Linq.JObject.FromObject(value).ToObject<ArticleRecord>();
    }
}
