using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace WCAE
{
    public sealed partial class ArticleRepository
    {
        readonly string connectionString;
        readonly string dataDirectory;
        readonly object gate = new object();
        readonly Dictionary<string,string> repairedNames = new Dictionary<string,string>(StringComparer.Ordinal);
        public ArticleRepository(string directory)
        {
            Directory.CreateDirectory(directory);
            dataDirectory=Path.GetFullPath(directory);
            connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "articles.sqlite"), DefaultTimeout = 10, Pooling = false }.ToString();
            using (var c = Open()) using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS accounts(biz TEXT PRIMARY KEY,name TEXT NOT NULL); CREATE TABLE IF NOT EXISTS articles(id TEXT PRIMARY KEY,biz TEXT NOT NULL,payload TEXT NOT NULL); CREATE INDEX IF NOT EXISTS article_account ON articles(biz); CREATE INDEX IF NOT EXISTS article_resolved_identity ON articles(biz,json_extract(payload,'$.ResolvedArticleId')) WHERE json_valid(payload); CREATE TABLE IF NOT EXISTS article_bodies(id TEXT PRIMARY KEY,body BLOB NOT NULL);";
                cmd.ExecuteNonQuery();
            }
            InitializeCollectionStorage();
            InitializeNativeCollectionStorage();
        }
        SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
        public string AccountName(string biz)
        {
            if(string.IsNullOrWhiteSpace(biz))return "";
            lock(gate)using(var connection=Open())
                return AccountNameResolver.Normalize(ReadAccountName(connection,null,biz),biz);
        }
        public void SaveAccount(string biz, string name)
        {
            if (string.IsNullOrEmpty(biz)) return;
            lock (gate) using (var c = Open()) using (var transaction = c.BeginTransaction())
            {
                string chosen = AccountNameResolver.Choose(biz, name, ReadAccountName(c,transaction,biz));
                WriteAccountName(c,transaction,biz,chosen);
                bool repair = NeedsNameRepair(biz,chosen);
                if(repair) RepairArticleNames(c,transaction,biz,chosen);
                transaction.Commit();
                if(repair) repairedNames[biz]=chosen;
            }
        }
        public void Save(ArticleRecord article)
            => Save(article,CancellationToken.None);
        public void Save(ArticleRecord article,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (gate) using (var c = Open()) using (var transaction = c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var repairs=new Dictionary<string,string>(StringComparer.Ordinal);
                var saved=SaveCore(c,transaction,article,repairs);
                token.ThrowIfCancellationRequested();
                transaction.Commit();
                CommitNameRepairs(repairs);
                article.Id=saved.Id;
            }
        }
        internal ArticleRecord ReadExportMetadata(string biz,string id)
        { lock(gate)using(var c=Open())return ReadArticle(c,null,biz,id); }
        internal ArticleRecord SaveAudioMetadata(ArticleRecord scanned,CancellationToken token)
        {
            if(scanned==null || scanned.Status!=ArticleStatus.Available || scanned.AudioMetadataVersion<1)
                throw new ArgumentException("音频扫描尚未成功完成。",nameof(scanned));
            token.ThrowIfCancellationRequested();
            lock(gate)using(var c=Open())using(var transaction=c.BeginTransaction())
            {
                token.ThrowIfCancellationRequested();
                var current=ReadArticle(c,transaction,scanned.Biz,scanned.Id)
                    ??throw new InvalidOperationException("原文章缓存已不存在，未写入音频更新。");
                // Concurrent collection may already have refreshed this row. Do not replace
                // that newer scan, a new status, any body or non-audio metadata with a snapshot.
                if(current.Status!=ArticleStatus.Available || current.AudioMetadataVersion>=scanned.AudioMetadataVersion)return current;
                ExportPreparation.ApplyAudioMetadata(current,scanned);
                using(var command=c.CreateCommand())
                {
                    command.Transaction=transaction; command.CommandText="UPDATE articles SET payload=@data WHERE biz=@biz AND id=@id";
                    current.Html="";
                    command.Parameters.AddWithValue("@data",JsonConvert.SerializeObject(current));
                    command.Parameters.AddWithValue("@biz",current.Biz); command.Parameters.AddWithValue("@id",current.Id);
                    if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("原文章缓存已变化，未写入音频更新。");
                }
                token.ThrowIfCancellationRequested(); transaction.Commit(); return current;
            }
        }
        ArticleRecord SaveCore(SqliteConnection c,SqliteTransaction transaction,ArticleRecord incoming,Dictionary<string,string> repairs)
        {
            if(incoming==null)throw new ArgumentNullException(nameof(incoming));
            if(string.IsNullOrWhiteSpace(incoming.Biz))throw new ArgumentException("文章缺少公众号标识");
            if(string.IsNullOrWhiteSpace(incoming.Id))incoming.Id=ArticleIdentity.Create(incoming.Biz,incoming.Mid,incoming.Idx,incoming.Url,incoming.SourceMessageId);
            // Keep queued/caller snapshots immutable; the merged result belongs to this transaction.
            var article=Newtonsoft.Json.Linq.JObject.FromObject(incoming).ToObject<ArticleRecord>();
            article.UpdatedAt=DateTime.Now;
            var saved=ReadArticle(c,transaction,article.Biz,article.Id);
            if(saved==null)
            {
                saved=FindCompatibleLegacyArticle(c,transaction,article);
                if(saved!=null)article.Id=saved.Id;
            }
            if(saved!=null)
            {
                ArticleMetadata.Merge(article,saved);
                article.ReadCount??=saved.ReadCount; article.LikeCount??=saved.LikeCount;
                article.FavoriteCount??=saved.FavoriteCount; article.ShareCount??=saved.ShareCount;
                if(string.IsNullOrWhiteSpace(article.Author))article.Author=saved.Author;
                if(string.IsNullOrWhiteSpace(article.Digest))article.Digest=saved.Digest;
                if(string.IsNullOrWhiteSpace(article.Url))article.Url=saved.Url;
                if(string.IsNullOrWhiteSpace(article.Mid) && !article.IdentityUnresolved)article.Mid=saved.Mid;
                if(string.IsNullOrWhiteSpace(article.SourceMessageId))article.SourceMessageId=saved.SourceMessageId;
                if(saved.IdentityUnresolved && string.IsNullOrWhiteSpace(article.Mid))article.IdentityUnresolved=true;
                article.Columns=MergeStoredColumns(article.Columns,saved.Columns,incoming.Status==ArticleStatus.Pending);
                article.ColumnMetadataVersion=Math.Max(article.ColumnMetadataVersion,saved.ColumnMetadataVersion);
                if(article.Media==null || article.Media.Count==0)
                    article.Media=article.AudioMetadataVersion>=1 ? (saved.Media??new List<MediaAsset>()).Where(m=>m.Kind!=MediaKind.Audio).ToList() : saved.Media;
                if(incoming.Status==ArticleStatus.Pending)
                {
                    article.Status=saved.Status; article.StatusDetail=saved.StatusDetail;
                    if(saved.Media?.Count>0)article.Media=saved.Media;
                }
                if(article.AudioMetadataVersion<saved.AudioMetadataVersion)
                {
                    article.Media=(article.Media??new List<MediaAsset>()).Where(m=>m.Kind!=MediaKind.Audio)
                        .Concat((saved.Media??new List<MediaAsset>()).Where(m=>m.Kind==MediaKind.Audio)).ToList();
                    article.AudioMetadataVersion=saved.AudioMetadataVersion;
                }
            }
            string bodyName=string.IsNullOrEmpty(article.Html)?"":AccountNameResolver.Extract(article.Html,article.Biz);
            string priorAccountName=ReadAccountName(c,transaction,article.Biz);
            article.AccountName=AccountNameResolver.Choose(article.Biz,bodyName,priorAccountName,article.AccountName,saved?.AccountName);
            string accountName=AccountNameResolver.Choose(article.Biz,bodyName,priorAccountName,article.AccountName);
            WriteAccountName(c,transaction,article.Biz,accountName);
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=transaction; cmd.CommandText="INSERT INTO articles(id,biz,payload) VALUES(@id,@biz,@data) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload WHERE articles.biz=excluded.biz";
                var metadata=Newtonsoft.Json.Linq.JObject.FromObject(article); metadata["Html"]="";
                cmd.Parameters.AddWithValue("@id",article.Id); cmd.Parameters.AddWithValue("@biz",article.Biz); cmd.Parameters.AddWithValue("@data",metadata.ToString(Formatting.None));
                if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("文章标识与公众号不匹配");
            }
            if(!string.IsNullOrEmpty(article.Html))using(var cmd=c.CreateCommand())
            {
                byte[] compressed;
                using(var output=new MemoryStream()) { using(var gzip=new GZipStream(output,CompressionMode.Compress,true)) { var bytes=Encoding.UTF8.GetBytes(article.Html); gzip.Write(bytes,0,bytes.Length); } compressed=output.ToArray(); }
                cmd.Transaction=transaction; cmd.CommandText="INSERT OR REPLACE INTO article_bodies(id,body) VALUES(@id,@body)";
                cmd.Parameters.AddWithValue("@id",article.Id); cmd.Parameters.AddWithValue("@body",compressed); cmd.ExecuteNonQuery();
            }
            if(NeedsNameRepair(article.Biz,accountName) && (!repairs.TryGetValue(article.Biz,out var name) || name!=accountName))
            { RepairArticleNames(c,transaction,article.Biz,accountName); repairs[article.Biz]=accountName; }
            article.Html=""; return article;
        }
        static List<ColumnInfo> MergeStoredColumns(List<ColumnInfo> incoming,List<ColumnInfo> saved,bool pending)
        {
            // A normal page supplies the current membership; a Pending history listing is
            // incomplete and retains the saved membership. Only names for those exact members
            // may be borrowed from the other snapshot, never unrelated old/new album IDs.
            bool retainSaved=(incoming?.Count??0)==0 || pending && (saved?.Count??0)>0;
            var membership=(retainSaved?saved:incoming)??new List<ColumnInfo>();
            var supplement=(retainSaved?incoming:saved)??new List<ColumnInfo>();
            var keys=new HashSet<string>(membership.Where(c=>c!=null).Select(StoredColumnKey),StringComparer.Ordinal);
            return ArticleColumnParser.Merge(membership,supplement.Where(c=>c!=null && keys.Contains(StoredColumnKey(c))));
        }
        static string StoredColumnKey(ColumnInfo column)
        {
            string id=(column.Id??"").Trim(),url=(column.Url??"").Trim();
            if(id.Length==0 && Uri.TryCreate(url,UriKind.Absolute,out var uri)
                && uri.Host.Equals("mp.weixin.qq.com",StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.Equals("/mp/appmsgalbum",StringComparison.OrdinalIgnoreCase))
                id=System.Web.HttpUtility.ParseQueryString(uri.Query)["album_id"]??"";
            return id.Length>0?"id:"+id:url.Length>0?"url:"+url:"name:"+(column.Name??"").Trim();
        }
        static ArticleRecord ReadArticle(SqliteConnection c,SqliteTransaction transaction,string biz,string id)
        {
            using(var cmd=c.CreateCommand())
            {
                cmd.Transaction=transaction; cmd.CommandText="SELECT payload FROM articles WHERE biz=@biz AND id=@id";
                cmd.Parameters.AddWithValue("@biz",biz); cmd.Parameters.AddWithValue("@id",id);
                return cmd.ExecuteScalar() is string json?JsonConvert.DeserializeObject<ArticleRecord>(json):null;
            }
        }
        static ArticleRecord FindCompatibleLegacyArticle(SqliteConnection c,SqliteTransaction transaction,ArticleRecord incoming)
        {
            string canonical;
            try
            {
                // Explicit/custom IDs are the caller's source identities, not migration targets.
                if(incoming.Id!=ArticleIdentity.Create(incoming.Biz,incoming.Mid,incoming.Idx,incoming.Url,incoming.SourceMessageId))return null;
                string proven=ArticleIdentityEvidence.Canonical(incoming);
                if(proven.Length>0)
                {
                    var matches=new List<ArticleRecord>(); int indexedMatches=0;
                    using(var cmd=c.CreateCommand())
                    {
                        cmd.Transaction=transaction;
                        cmd.CommandText="SELECT payload FROM articles WHERE biz=@biz AND json_valid(payload) AND json_extract(payload,'$.ResolvedArticleId')=@identity LIMIT 2";
                        cmd.Parameters.AddWithValue("@biz",incoming.Biz); cmd.Parameters.AddWithValue("@identity",proven);
                        using(var reader=cmd.ExecuteReader())while(reader.Read())
                        {
                            indexedMatches++;
                            var candidate=JsonConvert.DeserializeObject<ArticleRecord>(reader.GetString(0));
                            if(candidate!=null && ArticleIdentityEvidence.Canonical(candidate)==proven)matches.Add(candidate);
                        }
                    }
                    if(indexedMatches>1)return null;
                    // A canonical-key legacy row also needs a real long URL or page proof: an
                    // old common.id stored in Mid must never qualify solely by its primary key.
                    var keyed=ReadArticle(c,transaction,incoming.Biz,proven);
                    if(keyed!=null && ArticleIdentityEvidence.Canonical(keyed)==proven && !matches.Any(a=>a.Id==keyed.Id))matches.Add(keyed);
                    if(matches.Count==1)return matches[0];
                    if(matches.Count>1)return null;
                }
                canonical=ArticleIdentity.Create(incoming.Biz,"",incoming.Idx,incoming.Url,incoming.SourceMessageId);
            }
            catch(ArgumentException) { return null; }
            var keys=new List<string>();
            if(!string.IsNullOrWhiteSpace(incoming.SourceMessageId))keys.Add(incoming.Biz+":"+incoming.SourceMessageId+":"+incoming.Idx);
            if(Uri.TryCreate(incoming.Url,UriKind.Absolute,out var uri) && (uri.Scheme=="http" || uri.Scheme=="https"))
            {
                var query=System.Web.HttpUtility.ParseQueryString(uri.Query);
                string oldUrl=uri.GetLeftPart(UriPartial.Path)+(string.IsNullOrEmpty(query["sn"])?"":"?sn="+query["sn"]);
                using(var sha=SHA256.Create())keys.Add("url:"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(oldUrl))).Replace("-","").ToLowerInvariant());
            }
            foreach(string key in keys.Distinct(StringComparer.Ordinal))
            {
                if(key==incoming.Id)continue;
                var old=ReadArticle(c,transaction,incoming.Biz,key); if(old==null)continue;
                try
                {
                    // Old history called common.id 'Mid'. It may verify a message fallback only;
                    // it never supplies the actual article Mid or proves a URL match on its own.
                    string source=string.IsNullOrWhiteSpace(old.SourceMessageId)?old.Mid:old.SourceMessageId;
                    string oldCanonical=ArticleIdentity.Create(old.Biz,"",old.Idx,old.Url,source);
                    if(oldCanonical==canonical)return old;
                }
                catch(ArgumentException) { }
            }
            return null;
        }
        void CommitNameRepairs(Dictionary<string,string> repairs) { foreach(var pair in repairs)repairedNames[pair.Key]=pair.Value; }
        bool NeedsNameRepair(string biz,string name)
            => !string.IsNullOrEmpty(name) && (!repairedNames.TryGetValue(biz,out var prior) || prior!=name);

        static string ReadAccountName(SqliteConnection connection,SqliteTransaction transaction,string biz)
        {
            using(var cmd=connection.CreateCommand())
            {
                cmd.Transaction=transaction; cmd.CommandText="SELECT name FROM accounts WHERE biz=@biz";
                cmd.Parameters.AddWithValue("@biz",biz); return cmd.ExecuteScalar() as string ?? "";
            }
        }
        static void WriteAccountName(SqliteConnection connection,SqliteTransaction transaction,string biz,string name)
        {
            using(var cmd=connection.CreateCommand())
            {
                cmd.Transaction=transaction;
                cmd.CommandText="INSERT INTO accounts(biz,name) VALUES(@biz,@name) ON CONFLICT(biz) DO UPDATE SET name=excluded.name WHERE accounts.name<>excluded.name";
                cmd.Parameters.AddWithValue("@biz",biz); cmd.Parameters.AddWithValue("@name",name??""); cmd.ExecuteNonQuery();
            }
        }
        static void RepairArticleNames(SqliteConnection connection,SqliteTransaction transaction,string biz,string name,CancellationToken cancellation=default)
        {
            cancellation.ThrowIfCancellationRequested();
            var changes=new List<string>();
            using(var cmd=connection.CreateCommand())
            {
                // Healthy accounts read only the display field, without materializing full media
                // metadata or decompressing bodies. Malformed legacy JSON is left untouched.
                cmd.Transaction=transaction; cmd.CommandText="SELECT id,json_extract(payload,'$.AccountName') FROM articles WHERE biz=@biz AND json_valid(payload)";
                cmd.Parameters.AddWithValue("@biz",biz);
                using(var reader=cmd.ExecuteReader())while(reader.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    string existing=reader.IsDBNull(1)?"":reader.GetString(1);
                    if(AccountNameResolver.Normalize(existing,biz).Length>0 || existing==name)continue;
                    changes.Add(reader.GetString(0));
                }
            }
            foreach(var change in changes)using(var cmd=connection.CreateCommand())
            {
                cancellation.ThrowIfCancellationRequested();
                cmd.Transaction=transaction; cmd.CommandText="UPDATE articles SET payload=json_set(payload,'$.AccountName',@name) WHERE id=@id AND biz=@biz";
                cmd.Parameters.AddWithValue("@name",name); cmd.Parameters.AddWithValue("@id",change); cmd.Parameters.AddWithValue("@biz",biz); cmd.ExecuteNonQuery();
            }
        }

        public Dictionary<string,string> RepairAccountNames(CancellationToken cancellation=default)
        {
            cancellation.ThrowIfCancellationRequested();
            lock(gate)using(var connection=Open())using(var transaction=connection.BeginTransaction())
            {
                var names=new Dictionary<string,string>(StringComparer.Ordinal);
                using(var cmd=connection.CreateCommand())
                {
                    cmd.Transaction=transaction; cmd.CommandText="SELECT biz,name FROM accounts UNION ALL SELECT DISTINCT biz,'' FROM articles WHERE biz NOT IN (SELECT biz FROM accounts)";
                    using(var reader=cmd.ExecuteReader())while(reader.Read())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string biz=reader.GetString(0); if(!string.IsNullOrWhiteSpace(biz))names[biz]=reader.GetString(1);
                    }
                }
                foreach(string biz in names.Keys.ToArray())
                {
                    cancellation.ThrowIfCancellationRequested();
                    string stored=names[biz];
                    string name=AccountNameResolver.Normalize(stored,biz);
                    if(name.Length==0)
                    {
                        name=FindCachedBodyName(connection,transaction,biz,cancellation);
                        WriteAccountName(connection,transaction,biz,name);
                    }
                    RepairArticleNames(connection,transaction,biz,name,cancellation);
                    names[biz]=name;
                }
                cancellation.ThrowIfCancellationRequested(); transaction.Commit();
                repairedNames.Clear(); foreach(var pair in names)if(pair.Value.Length>0)repairedNames[pair.Key]=pair.Value;
                return names;
            }
        }
        static string FindCachedBodyName(SqliteConnection connection,SqliteTransaction transaction,string biz,CancellationToken cancellation)
        {
            using(var cmd=connection.CreateCommand())
            {
                cmd.Transaction=transaction;
                cmd.CommandText="SELECT b.body FROM articles a JOIN article_bodies b ON b.id=a.id WHERE a.biz=@biz ORDER BY a.rowid DESC";
                cmd.Parameters.AddWithValue("@biz",biz);
                using(var reader=cmd.ExecuteReader())while(reader.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        using(var input=new MemoryStream((byte[])reader[0]))
                        using(var gzip=new GZipStream(input,CompressionMode.Decompress))
                        using(var text=new StreamReader(gzip,Encoding.UTF8))
                        {
                            var html=new StringBuilder(); var buffer=new char[8192]; int count;
                            while((count=text.Read(buffer,0,buffer.Length))>0) { cancellation.ThrowIfCancellationRequested(); html.Append(buffer,0,count); }
                            cancellation.ThrowIfCancellationRequested();
                            string name=AccountNameResolver.Extract(html.ToString(),biz);
                            cancellation.ThrowIfCancellationRequested();
                            if(name.Length>0)return name;
                        }
                    }
                    catch(InvalidDataException) { /* A corrupt cached body must not erase metadata or stop other accounts repairing. */ }
                    catch(IOException) { }
                }
            }
            return "";
        }
        public List<ArticleRecord> Load(string biz)
        {
            var result = new List<ArticleRecord>();
            lock (gate) using (var c = Open()) using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT payload FROM articles WHERE biz=@b"; cmd.Parameters.AddWithValue("@b", biz ?? "");
                using (var reader = cmd.ExecuteReader()) while (reader.Read())
                {
                    var a = JsonConvert.DeserializeObject<ArticleRecord>(reader.GetString(0)); if (a != null) result.Add(a);
                }
            }
            return result;
        }
        public Dictionary<string,string> Accounts()
        {
            var result = new Dictionary<string,string>();
            lock (gate) using (var c = Open()) using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT biz,name FROM accounts ORDER BY name";
                using (var reader = cmd.ExecuteReader()) while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
            }
            return result;
        }
        public void ClearHistory()
        {
            lock (gate) using (var c = Open())
            {
                using (var transaction = c.BeginTransaction()) using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM native_collection_completed; DELETE FROM native_collection_states; DELETE FROM collection_pending; DELETE FROM collection_checkpoints; DELETE FROM collection_details; DELETE FROM article_bodies; DELETE FROM articles; DELETE FROM accounts;";
                    cmd.ExecuteNonQuery();
                    transaction.Commit();
                }
                repairedNames.Clear();
                ClearMetadataBackups();
                ClearColumnBackups();
                // The history is already cleared. A busy reader or a full disk may prevent
                // optional compaction, which must not turn a successful clear into a failure.
                try
                {
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (SqliteException ex)
                {
                    System.Diagnostics.Trace.WriteLine("WCAE history cleared; database compaction skipped: " + ex.SqliteErrorCode);
                }
            }
        }
        public string LoadBody(string id)
        {
            lock(gate) using(var c=Open()) using(var cmd=c.CreateCommand())
            {
                cmd.CommandText="SELECT body FROM article_bodies WHERE id=@id"; cmd.Parameters.AddWithValue("@id",id);
                var bytes=cmd.ExecuteScalar() as byte[]; if(bytes==null)return "";
                using(var input=new MemoryStream(bytes)) using(var gzip=new GZipStream(input,CompressionMode.Decompress)) using(var reader=new StreamReader(gzip,Encoding.UTF8))return reader.ReadToEnd();
            }
        }
    }
    public sealed class SessionVault
    {
        readonly string path;
        readonly object gate = new object();
        public SessionVault(string directory) { Directory.CreateDirectory(directory); path = Path.Combine(directory, "sessions.bin"); }
        public Dictionary<string,AccountSession> Load()
        {
            lock (gate)
            {
                if (!File.Exists(path)) return new Dictionary<string,AccountSession>();
                try
                {
                    var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                    return JsonConvert.DeserializeObject<Dictionary<string,AccountSession>>(Encoding.UTF8.GetString(clear)) ?? new Dictionary<string,AccountSession>();
                }
                catch (CryptographicException) { return new Dictionary<string,AccountSession>(); }
                catch (JsonException) { return new Dictionary<string,AccountSession>(); }
            }
        }
        public void Save(Dictionary<string,AccountSession> sessions)
        {
            lock (gate)
            {
                var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(sessions)), null, DataProtectionScope.CurrentUser);
                var temp = path + ".tmp"; File.WriteAllBytes(temp, data);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
        }
        public void Clear()
        {
            lock (gate) File.Delete(path);
        }
    }
    public sealed class ArticleFilter
    {
        public string Column { get; set; }
        public bool UnassignedColumn { get; set; }
        public ArticleStatus? Status { get; set; }
        public DateTime? From { get; set; }
        public DateTime? Through { get; set; }
        public string Search { get; set; }
        public string SortBy { get; set; } = "PublishedAt";
        public bool Descending { get; set; } = true;
    }
    internal static class ArticleIdentityEvidence
    {
        internal static string Canonical(ArticleRecord article)
        {
            if(article==null || string.IsNullOrWhiteSpace(article.Biz))return "";
            string resolved=Validate(article.Biz,article.ResolvedArticleId), url="";
            if(Uri.TryCreate(article.Url,UriKind.Absolute,out var uri) && (uri.Scheme=="http" || uri.Scheme=="https")
                && uri.Host.Equals("mp.weixin.qq.com",StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length==0 && uri.IsDefaultPort
                && (uri.AbsolutePath=="/s" || uri.AbsolutePath=="/mp/appmsg/show"))
            {
                var query=System.Web.HttpUtility.ParseQueryString(uri.Query);
                if(query["__biz"]==article.Biz)
                    url=Validate(article.Biz,article.Biz+":"+(query["mid"]??query["appmsgid"])+":"+query["idx"]);
            }
            if(resolved.Length>0 && url.Length>0 && resolved!=url)return "";
            return resolved.Length>0?resolved:url;
        }
        static string Validate(string biz,string identity)
        {
            string prefix=biz+":";
            if(string.IsNullOrEmpty(identity) || !identity.StartsWith(prefix,StringComparison.Ordinal))return "";
            string[] parts=identity.Substring(prefix.Length).Split(':');
            if(parts.Length!=2 || parts[0].Length==0 || !parts[0].All(c=>c>='0' && c<='9')
                || !int.TryParse(parts[1],out int idx) || idx<1 || parts[1]!=idx.ToString(System.Globalization.CultureInfo.InvariantCulture))return "";
            return identity;
        }
    }
    public static class ArticleQuery
    {
        public static List<ArticleRecord> ResolveReferences(IEnumerable<ArticleRecord> articles)
        {
            var source=(articles??Enumerable.Empty<ArticleRecord>()).Where(a=>a!=null).ToList();
            var lookup=BuildRepresentatives(source);
            return source.Where(a=>Representative(a,lookup).Id==a.Id).ToList();
        }
        public static string RepresentativeId(ArticleRecord article,IEnumerable<ArticleRecord> articles)
        {
            if(article==null)return "";
            var lookup=BuildRepresentatives((articles??Enumerable.Empty<ArticleRecord>()).Where(a=>a!=null).ToList());
            return Representative(article,lookup).Id;
        }
        public static Dictionary<(string,string),string> RepresentativeIds(IEnumerable<ArticleRecord> articles)
        {
            var source=(articles??Enumerable.Empty<ArticleRecord>()).Where(a=>a!=null).ToList();
            var lookup=BuildRepresentatives(source);
            return source.GroupBy(a=>(a.Biz??"",a.Id??"")).ToDictionary(g=>g.Key,g=>Representative(g.First(),lookup).Id);
        }
        sealed class RepresentationMap
        {
            internal Dictionary<(string,string),ArticleRecord> Rows;
            internal readonly Dictionary<(string,string),ArticleRecord> Proven=new Dictionary<(string,string),ArticleRecord>();
        }
        static RepresentationMap BuildRepresentatives(List<ArticleRecord> source)
        {
            var lookup=source.GroupBy(a=>(a.Biz??"",a.Id??"")).ToDictionary(g=>g.Key,g=>g.First());
            var result=new RepresentationMap { Rows=lookup };
            var groups=source.Select(a=>new { Article=a,Canonical=ArticleIdentityEvidence.Canonical(a) }).Where(x=>x.Canonical.Length>0)
                .GroupBy(x=>(x.Article.Biz,x.Canonical));
            foreach(var group in groups)
            {
                var representative=group.OrderByDescending(x=>x.Article.Id==x.Canonical).ThenBy(x=>x.Article.Id,StringComparer.Ordinal).First().Article;
                foreach(var item in group)lookup[(item.Article.Biz,item.Article.Id)]=representative;
                // Keep proof aliases separate from physical row keys: an old common.id may
                // coincidentally equal another article's canonical ID but cannot prove identity.
                result.Proven[group.Key]=representative;
            }
            return result;
        }
        static ArticleRecord Representative(ArticleRecord article,RepresentationMap lookup)
        {
            var current=lookup.Rows.TryGetValue((article.Biz??"",article.Id??""),out var stored)?stored:article;
            var starting=current;
            var seen=new HashSet<string>(StringComparer.Ordinal) { current.Id??"" };
            while(current.ContentKind==ArticleContentKind.ShareReference && !string.IsNullOrWhiteSpace(current.ReferenceSource)
                && !string.IsNullOrWhiteSpace(current.ReferencedArticleId))
            {
                if(!lookup.Proven.TryGetValue((article.Biz??"",current.ReferencedArticleId),out var target))return current;
                // Contradictory/cyclic evidence never removes every source article from display.
                if(!seen.Add(target.Id))return starting;
                current=target;
            }
            return current;
        }
        public static List<ArticleRecord> Apply(IEnumerable<ArticleRecord> articles, ArticleFilter filter)
        {
            filter??=new ArticleFilter();
            IEnumerable<ArticleRecord> q = ResolveReferences(articles);
            if (filter.UnassignedColumn) q = q.Where(x => x.Columns.Count == 0);
            else if (!string.IsNullOrEmpty(filter.Column)) q = q.Where(x => x.Columns.Any(c => c.Name == filter.Column));
            if (filter.Status.HasValue) q = q.Where(x => x.Status == filter.Status.Value);
            if (filter.From.HasValue) q = q.Where(x => x.PublishedAt.HasValue && x.PublishedAt.Value.Date >= filter.From.Value.Date);
            if (filter.Through.HasValue) q = q.Where(x => x.PublishedAt.HasValue && x.PublishedAt.Value.Date <= filter.Through.Value.Date);
            if (!string.IsNullOrWhiteSpace(filter.Search)) q = q.Where(x => (x.Title ?? "").IndexOf(filter.Search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            Func<ArticleRecord,long?> key = x => filter.SortBy == "ReadCount" ? x.ReadCount : filter.SortBy == "LikeCount" ? x.LikeCount : filter.SortBy == "FavoriteCount" ? x.FavoriteCount : filter.SortBy == "ShareCount" ? x.ShareCount : x.PublishedAt?.Ticks;
            var known = q.OrderBy(x => !key(x).HasValue);
            return (filter.Descending ? known.ThenByDescending(key) : known.ThenBy(key))
                .ThenByDescending(x=>string.IsNullOrWhiteSpace(x.SourceMessageId)?x.Mid:x.SourceMessageId,MessageIdComparer.Instance)
                .ThenBy(x=>x.Idx).ThenBy(x=>x.Id,StringComparer.Ordinal).ThenBy(x=>x.Biz,StringComparer.Ordinal).ToList();
        }
        sealed class MessageIdComparer : IComparer<string>
        {
            internal static readonly MessageIdComparer Instance=new MessageIdComparer();
            public int Compare(string a,string b)
            {
                a??=""; b??="";
                if(a.Length>0 && b.Length>0 && a.All(c=>c>='0' && c<='9') && b.All(c=>c>='0' && c<='9'))
                { a=a.TrimStart('0'); b=b.TrimStart('0'); int length=a.Length.CompareTo(b.Length); if(length!=0)return length; }
                return StringComparer.Ordinal.Compare(a,b);
            }
        }
    }
}
