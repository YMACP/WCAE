using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public sealed class ArticleMetadataRepairReport
    {
        public int Examined { get; set; }
        public int Changed { get; set; }
        public int Unchanged { get; set; }
        public int UnreadableBodies { get; set; }
        public int Remaining { get; set; }
        public string BackupPath { get; set; } = "";
    }
    public static class ArticleMetadataMaintenance
    {
        public static ArticleMetadataRepairReport Repair(ArticleRepository repository,CancellationToken token=default)
        {
            if(repository==null)throw new ArgumentNullException(nameof(repository));
            return repository.RepairArticleMetadata(token);
        }
    }
    public sealed partial class ArticleRepository
    {
        static readonly string[] MaintainedMetadataFields={ "Title","RawTitle","TitleSource","PageRawTitle","PageTitleSource","Mid","Idx","IdentityUnresolved","ResolvedArticleId","PublishedAt","PublishedAtSource","PublishedUnixSeconds","ContentKind","ReferencedArticleId","ReferencedArticleUrl","ReferenceSource" };
        internal ArticleMetadataRepairReport RepairArticleMetadata(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var report=new ArticleMetadataRepairReport();
            List<string> ids;
            lock(gate)using(var c=Open())
            {
                ids=OldMetadataIds(c);
                if(ids.Count==0)return report;
                report.BackupPath=EnsureMetadataBackup(c,token);
            }
            // Commit one source row at a time. Cancellation cannot produce a half-written row,
            // and the next startup resumes only rows not already marked with this parser version.
            foreach(string id in ids)
            {
                token.ThrowIfCancellationRequested();
                lock(gate)using(var c=Open())using(var tx=c.BeginTransaction())
                {
                    string json; byte[] bytes;
                    using(var cmd=c.CreateCommand())
                    {
                        cmd.Transaction=tx; cmd.CommandText="SELECT a.payload,b.body FROM articles a LEFT JOIN article_bodies b ON b.id=a.id WHERE a.id=@id AND json_valid(a.payload) AND COALESCE(json_extract(a.payload,'$.MetadataVersion'),0)<@version";
                        cmd.Parameters.AddWithValue("@id",id); cmd.Parameters.AddWithValue("@version",ArticleMetadata.CurrentVersion);
                        using(var reader=cmd.ExecuteReader())
                        { if(!reader.Read())continue; json=reader.GetString(0); bytes=reader.IsDBNull(1)?null:(byte[])reader[1]; }
                    }
                    var original=JObject.Parse(json); ArticleRecord article;
                    try { article=original.ToObject<ArticleRecord>(); }
                    catch(JsonException) { report.UnreadableBodies++; continue; }
                    if(article==null)continue;
                    // Normalize presentation without guessing that a short title represents another row.
                    string normalized=ArticleMetadata.NormalizeTitle(article.Title);
                    if(normalized.Length>0)article.Title=normalized;
                    if(bytes!=null)
                    {
                        try { ArticleMetadata.ApplyHtml(article,ReadMetadataBody(bytes,token)); }
                        catch(InvalidDataException) { report.UnreadableBodies++; }
                        catch(IOException) { report.UnreadableBodies++; }
                        catch(System.Text.RegularExpressions.RegexMatchTimeoutException) { report.UnreadableBodies++; }
                    }
                    token.ThrowIfCancellationRequested();
                    var updated=JObject.FromObject(article); bool changed=false;
                    foreach(string key in MaintainedMetadataFields)
                    {
                        var before=original[key]; var after=updated[key];
                        // Avoid adding absent default evidence fields to every old row unnecessarily.
                        if(before==null && (after==null || after.Type==JTokenType.Null || after.Type==JTokenType.String && after.Value<string>()=="" || after.Type==JTokenType.Integer && after.Value<long>()==0 || after.Type==JTokenType.Boolean && !after.Value<bool>()))continue;
                        if(JToken.DeepEquals(before,after))continue;
                        original[key]=after?.DeepClone(); changed=true;
                    }
                    original["MetadataVersion"]=ArticleMetadata.CurrentVersion;
                    using(var cmd=c.CreateCommand())
                    {
                        cmd.Transaction=tx; cmd.CommandText="UPDATE articles SET payload=@data WHERE id=@id";
                        cmd.Parameters.AddWithValue("@data",original.ToString(Formatting.None)); cmd.Parameters.AddWithValue("@id",id); cmd.ExecuteNonQuery();
                    }
                    token.ThrowIfCancellationRequested(); tx.Commit(); report.Examined++;
                    if(changed)report.Changed++;else report.Unchanged++;
                }
            }
            lock(gate)using(var c=Open())report.Remaining=OldMetadataIds(c).Count;
            return report;
        }
        static List<string> OldMetadataIds(SqliteConnection c)
        {
            var result=new List<string>();
            using(var cmd=c.CreateCommand())
            {
                cmd.CommandText="SELECT id FROM articles WHERE json_valid(payload) AND COALESCE(json_extract(payload,'$.MetadataVersion'),0)<@version ORDER BY rowid";
                cmd.Parameters.AddWithValue("@version",ArticleMetadata.CurrentVersion);
                using(var reader=cmd.ExecuteReader())while(reader.Read())result.Add(reader.GetString(0));
            }
            return result;
        }
        static string ReadMetadataBody(byte[] bytes,CancellationToken token)
        {
            using(var input=new MemoryStream(bytes))using(var gzip=new GZipStream(input,CompressionMode.Decompress))using(var reader=new StreamReader(gzip,Encoding.UTF8))
            {
                var result=new StringBuilder(); var buffer=new char[8192]; int count;
                while((count=reader.Read(buffer,0,buffer.Length))>0)
                { token.ThrowIfCancellationRequested(); if(result.Length+count>32*1024*1024)throw new InvalidDataException("缓存正文过大"); result.Append(buffer,0,count); }
                return result.ToString();
            }
        }
        string EnsureMetadataBackup(SqliteConnection source,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string directory=Path.Combine(dataDirectory,"metadata-backups"); Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"articles-before-metadata-v"+ArticleMetadata.CurrentVersion+".sqlite");
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
        void ClearMetadataBackups()
        {
            string directory=Path.GetFullPath(Path.Combine(dataDirectory,"metadata-backups"));
            if(!string.Equals(Path.GetDirectoryName(directory),dataDirectory.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase) || !Directory.Exists(directory))return;
            // Only our known top-level backup filenames; never recursively remove this directory.
            foreach(string file in Directory.EnumerateFiles(directory,"articles-before-metadata-v*",SearchOption.TopDirectoryOnly))
            {
                string name=Path.GetFileName(file);
                if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"^articles-before-metadata-v[0-9]+\.sqlite(?:\.tmp|-wal|-shm)?$"))continue;
                string absolute=Path.GetFullPath(file);
                if(string.Equals(Path.GetDirectoryName(absolute),directory,StringComparison.OrdinalIgnoreCase))File.Delete(absolute);
            }
        }
    }
}
