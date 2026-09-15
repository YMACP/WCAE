using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public sealed class ArticleColumnRepairReport
    {
        public int Examined { get; set; }
        public int Changed { get; set; }
        public int Unresolved { get; set; }
        public int Unreadable { get; set; }
        public string BackupPath { get; set; } = "";
    }

    public static class ArticleColumnMaintenance
    {
        public static ArticleColumnRepairReport Repair(ArticleRepository repository,CancellationToken token=default,
            IProgress<ArticleColumnRepairReport> progress=null)
        {
            if(repository==null)throw new ArgumentNullException(nameof(repository));
            return repository.RepairArticleColumns(token,progress);
        }
    }

    public sealed partial class ArticleRepository
    {
        internal ArticleColumnRepairReport RepairArticleColumns(CancellationToken token,IProgress<ArticleColumnRepairReport> progress)
        {
            token.ThrowIfCancellationRequested();
            var report=new ArticleColumnRepairReport(); List<string> ids;
            lock(gate)using(var c=Open())
            {
                ids=OldColumnMetadataIds(c,token);
                if(ids.Count==0)return report;
                report.BackupPath=EnsureColumnBackup(c,token);
            }
            foreach(string id in ids)
            {
                token.ThrowIfCancellationRequested();
                lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
                {
                    string json; byte[] bytes;
                    using(var cmd=c.CreateCommand())
                    {
                        cmd.Transaction=tx;
                        cmd.CommandText="SELECT a.payload,b.body FROM articles a LEFT JOIN article_bodies b ON b.id=a.id WHERE a.id=@id AND json_valid(a.payload) AND COALESCE(json_extract(a.payload,'$.ColumnMetadataVersion'),0)<@version";
                        cmd.Parameters.AddWithValue("@id",id); cmd.Parameters.AddWithValue("@version",ArticleColumnParser.CurrentVersion);
                        using(var reader=cmd.ExecuteReader())
                        { if(!reader.Read())continue; json=reader.GetString(0); bytes=reader.IsDBNull(1)?null:(byte[])reader[1]; }
                    }
                    var original=JObject.Parse(json); bool unreadable=false,unresolved=false,changed=false;
                    JToken columns=original["Columns"];
                    try
                    {
                        // Deserialize a working copy, then write ONLY the two maintained fields.
                        // A normal Save/ParseHtml here would also change status, timestamps and body.
                        var article=original.ToObject<ArticleRecord>();
                        if(article==null)throw new JsonSerializationException("文章缓存为空");
                        if(bytes!=null)ArticleColumnParser.Apply(article,ReadMetadataBody(bytes,token));
                        columns=JToken.FromObject(article.Columns??new List<ColumnInfo>());
                        changed=!JToken.DeepEquals(original["Columns"],columns);
                        unresolved=(article.Columns??new List<ColumnInfo>()).Any(ArticleColumnParser.IsPlaceholder);
                    }
                    catch(InvalidDataException) { unreadable=true; }
                    catch(IOException) { unreadable=true; }
                    catch(JsonException) { unreadable=true; }
                    catch(System.Text.RegularExpressions.RegexMatchTimeoutException) { unreadable=true; }
                    // A failed local attempt is versioned too. Future live page parsing does not
                    // use this migration marker as a reason to skip newly received HTML.
                    token.ThrowIfCancellationRequested();
                    using(var cmd=c.CreateCommand())
                    {
                        cmd.Transaction=tx;
                        cmd.CommandText=changed
                            ? "UPDATE articles SET payload=json_set(payload,'$.Columns',json(@columns),'$.ColumnMetadataVersion',@version) WHERE id=@id"
                            : "UPDATE articles SET payload=json_set(payload,'$.ColumnMetadataVersion',@version) WHERE id=@id";
                        if(changed)cmd.Parameters.AddWithValue("@columns",columns.ToString(Formatting.None));
                        cmd.Parameters.AddWithValue("@version",ArticleColumnParser.CurrentVersion); cmd.Parameters.AddWithValue("@id",id);
                        cmd.ExecuteNonQuery();
                    }
                    token.ThrowIfCancellationRequested(); tx.Commit(); report.Examined++;
                    if(changed)report.Changed++;
                    if(unreadable)report.Unreadable++;
                    else if(unresolved)report.Unresolved++;
                }
                // Observers run after committing and releasing the repository lock. Cancellation
                // at this boundary resumes at the next unmarked row when called again.
                progress?.Report(new ArticleColumnRepairReport { Examined=report.Examined,Changed=report.Changed,
                    Unresolved=report.Unresolved,Unreadable=report.Unreadable,BackupPath=report.BackupPath });
            }
            return report;
        }

        static List<string> OldColumnMetadataIds(SqliteConnection c,CancellationToken token)
        {
            var result=new List<string>();
            using(var cmd=c.CreateCommand())
            {
                // Do not decompress a cache body twice for the same parser version. Rows without
                // either a body or membership contain no local evidence and need no migration.
                cmd.CommandText="SELECT a.id FROM articles a WHERE json_valid(a.payload) AND COALESCE(json_extract(a.payload,'$.ColumnMetadataVersion'),0)<@version AND (EXISTS(SELECT 1 FROM article_bodies b WHERE b.id=a.id) OR COALESCE(json_array_length(a.payload,'$.Columns'),0)>0) ORDER BY a.rowid";
                cmd.Parameters.AddWithValue("@version",ArticleColumnParser.CurrentVersion);
                using(var reader=cmd.ExecuteReader())while(reader.Read())
                { token.ThrowIfCancellationRequested(); result.Add(reader.GetString(0)); }
            }
            return result;
        }

        string EnsureColumnBackup(SqliteConnection source,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string directory=Path.Combine(dataDirectory,"metadata-backups"); Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"articles-before-columns-v"+ArticleColumnParser.CurrentVersion+".sqlite");
            if(File.Exists(path))return path;
            string temporary=path+".tmp";
            try
            {
                using(var target=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=temporary,Pooling=false }.ToString()))
                { target.Open(); source.BackupDatabase(target); }
                token.ThrowIfCancellationRequested(); File.Move(temporary,path); return path;
            }
            finally { if(File.Exists(temporary))File.Delete(temporary); }
        }

        void ClearColumnBackups()
        {
            string directory=Path.GetFullPath(Path.Combine(dataDirectory,"metadata-backups"));
            if(!string.Equals(Path.GetDirectoryName(directory),dataDirectory.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase) || !Directory.Exists(directory))return;
            foreach(string file in Directory.EnumerateFiles(directory,"articles-before-columns-v*",SearchOption.TopDirectoryOnly))
            {
                string name=Path.GetFileName(file);
                if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"^articles-before-columns-v[0-9]+\.sqlite(?:\.tmp|-wal|-shm)?$"))continue;
                string absolute=Path.GetFullPath(file);
                if(string.Equals(Path.GetDirectoryName(absolute),directory,StringComparison.OrdinalIgnoreCase))File.Delete(absolute);
            }
        }
    }
}
