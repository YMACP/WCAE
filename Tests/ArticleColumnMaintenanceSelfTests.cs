using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public static class ArticleColumnMaintenanceSelfTests
    {
        const string AlbumId="4113194063615115265";
        public static void Run()
        {
            RepairOnlyColumnsAndPersist();
            SaveProtectsNamesAndMembership();
            CancellationResumesCommittedRows();
            BadAndMissingBodiesDoNotRepeat();
            ClearAlsoRemovesOwnedBackups();
        }

        static void RepairOnlyColumnsAndPersist()
        {
            using var fixture=new Fixture(); var repo=fixture.Repository;
            var first=Article("columns-a",1,"码农小站");
            var second=Article("columns-b",2,"另一个公众号的专栏");
            repo.BeginCollectionRun(first.Biz);
            repo.SaveCollectionPage(first.Biz,new[]{first},0,20,false);
            repo.MarkArticleStarted(first.Biz,first.Id);
            repo.BeginNativeCollection(first.Biz,"native-test-account");
            repo.Save(second);
            using(var c=fixture.Open())using(var cmd=c.CreateCommand())
            {
                cmd.CommandText="UPDATE articles SET payload=json_set(payload,'$.UpdatedAt','2001-02-03T04:05:06','$.UnrelatedExtension',json('{\"nested\":[7,\"原数据\"]}'))";
                cmd.ExecuteNonQuery();
            }
            var before=fixture.Payloads(); string tables=fixture.OtherTables();
            var report=ArticleColumnMaintenance.Repair(repo);
            Check(report.Examined==2 && report.Changed==2 && report.Unresolved==0 && report.Unreadable==0,
                "两份缓存未按实际 JavaScript title 恢复合集名称");
            Check(File.Exists(report.BackupPath),"首次缓存修改前没有建立完整备份");
            var after=fixture.Payloads();
            foreach(var pair in before)
            {
                var a=(JObject)pair.Value.DeepClone(); var b=(JObject)after[pair.Key].DeepClone();
                a.Remove("Columns"); a.Remove("ColumnMetadataVersion"); b.Remove("Columns"); b.Remove("ColumnMetadataVersion");
                Check(JToken.DeepEquals(a,b),"合集维护改变了其他 payload 字段："+pair.Key);
                Check(after[pair.Key].Value<int>("ColumnMetadataVersion")==ArticleColumnParser.CurrentVersion,"合集修复版本没有持久化");
            }
            Check(tables==fixture.OtherTables(),"合集维护改动了正文 bytes、公众号、待处理队列或采集游标");
            repo=new ArticleRepository(fixture.Directory);
            var restored=repo.Load(first.Biz).Single();
            Check(restored.Columns.Single().Name=="码农小站" && restored.Columns.Single().Id==AlbumId,"重启后真实名称或长 ID 丢失");
            Check(repo.Load(second.Biz).Single().ColumnNames=="另一个公众号的专栏","相同合集 ID 的不同公众号发生串名");
            Check(ArticleQuery.Apply(repo.Load(first.Biz),new ArticleFilter { Column="码农小站" }).Single().Id==first.Id,
                "历史记录修复后无法使用中文专栏名称筛选");
            string repairedJson=fixture.AllPayloadText();
            Check(ArticleColumnMaintenance.Repair(repo).Examined==0 && repairedJson==fixture.AllPayloadText(),
                "第二次启动重复解压或再次改写已处理缓存");
            using(var backup=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=report.BackupPath,Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString()))
            {
                backup.Open(); using var cmd=backup.CreateCommand(); cmd.CommandText="SELECT payload FROM articles WHERE id=@id";
                cmd.Parameters.AddWithValue("@id",first.Id);
                Check(JToken.DeepEquals(before[first.Id],JObject.Parse((string)cmd.ExecuteScalar())),"备份不是修改前的原始 payload");
            }
        }

        static void SaveProtectsNamesAndMembership()
        {
            using var fixture=new Fixture(); var repo=fixture.Repository;
            var saved=Article("names",1,""); saved.Columns[0].Name="码农小站";
            saved.Columns.Add(new ColumnInfo { Id="2321406246047760386",Name="渗透测试" });
            saved.ColumnMetadataVersion=ArticleColumnParser.CurrentVersion; repo.Save(saved);
            var pending=Article("names",1,""); pending.Status=ArticleStatus.Pending;
            pending.Columns.Add(new ColumnInfo { Id="unrelated",Name="不属于本文" }); repo.Save(pending);
            var actual=repo.Load("names").Single();
            Check(actual.Columns.Count==2 && actual.Columns[0].Name=="码农小站" && actual.Columns[1].Name=="渗透测试"
                && actual.ColumnMetadataVersion==ArticleColumnParser.CurrentVersion,"Pending 占位覆盖真实名称、篡改归属或降级版本");
            Check(pending.Columns[0].Name=="合集 "+AlbumId && pending.ColumnMetadataVersion==0,"保存合并污染调用者快照");
            var fresh=Article("names",1,""); repo.Save(fresh);
            actual=repo.Load("names").Single();
            Check(actual.Columns.Count==1 && actual.Columns[0].Name=="码农小站","普通页面保存降级真实名，或盲目合并已经移除的合集");
            fresh.Columns[0].Name="新专栏名称"; repo.Save(fresh);
            Check(repo.Load("names").Single().Columns.Single().Name=="新专栏名称","新页面明确真实名称无法正常更新");
            var oldPlaceholder=Article("names",2,""); repo.Save(oldPlaceholder);
            var upgraded=Article("names",2,""); upgraded.Status=ArticleStatus.Pending; upgraded.Columns[0].Name="新识别名称"; repo.Save(upgraded);
            Check(repo.Load("names").Single(a=>a.Id==upgraded.Id).ColumnNames=="新识别名称","Pending 中同 ID 的真实名称未能升级旧占位");
            var foreign=Article("different-biz",1,""); repo.Save(foreign);
            Check(repo.Load(foreign.Biz).Single().ColumnNames=="合集 "+AlbumId,"保存名称保护跨公众号借用了同 ID 名称");
        }

        static void CancellationResumesCommittedRows()
        {
            using var fixture=new Fixture(); var repo=fixture.Repository;
            foreach(int i in new[]{1,2,3})repo.Save(Article("cancel",i,"专栏"+i));
            string before=fixture.AllPayloadText();
            using(var stop=new CancellationTokenSource())
            {
                stop.Cancel(); ExpectCanceled(()=>ArticleColumnMaintenance.Repair(repo,stop.Token));
                Check(before==fixture.AllPayloadText() && !System.IO.Directory.Exists(Path.Combine(fixture.Directory,"metadata-backups")),
                    "执行前取消仍然创建备份或改写缓存");
            }
            using(var stop=new CancellationTokenSource())
            {
                var progress=new InlineProgress(report=> { if(report.Examined==1)stop.Cancel(); });
                ExpectCanceled(()=>ArticleColumnMaintenance.Repair(repo,stop.Token,progress));
            }
            Check(fixture.Payloads().Values.Count(a=>a.Value<int>("ColumnMetadataVersion")==ArticleColumnParser.CurrentVersion)==1,
                "取消没有在单行提交边界停止");
            var result=ArticleColumnMaintenance.Repair(new ArticleRepository(fixture.Directory));
            Check(result.Examined==2 && result.Changed==2 && ArticleColumnMaintenance.Repair(repo).Examined==0,
                "恢复修复没有从未处理行继续，或重复处理已经提交的行");
            Check(System.IO.Directory.GetFiles(Path.Combine(fixture.Directory,"metadata-backups"),"articles-before-columns-v*.sqlite").Length==1,
                "取消恢复反复生成完整备份");
        }

        static void BadAndMissingBodiesDoNotRepeat()
        {
            using var fixture=new Fixture(); var repo=fixture.Repository;
            var bad=Article("broken",1,"损坏正文不可用"); repo.Save(bad);
            var missing=Article("broken",2,""); repo.Save(missing);
            var empty=Article("broken",3,""); empty.Columns.Clear(); repo.Save(empty);
            var valid=Article("broken",4,"可继续修复"); repo.Save(valid);
            using(var c=fixture.Open())using(var cmd=c.CreateCommand())
            {
                cmd.CommandText="UPDATE article_bodies SET body=X'00010203040506070809' WHERE id=@id";
                cmd.Parameters.AddWithValue("@id",bad.Id); cmd.ExecuteNonQuery();
            }
            string tables=fixture.OtherTables();
            var report=ArticleColumnMaintenance.Repair(repo);
            Check(report.Examined==3 && report.Changed==1 && report.Unreadable==1 && report.Unresolved==1,
                "坏 gzip/缺正文未分别报告，或阻断后续正常记录");
            Check(repo.Load("broken").Single(a=>a.Id==bad.Id).ColumnNames=="合集 "+AlbumId && tables==fixture.OtherTables(),
                "坏正文清空占位或被替换");
            Check(repo.Load("broken").Single(a=>a.Id==valid.Id).ColumnNames=="可继续修复"
                && ArticleColumnMaintenance.Repair(repo).Examined==0,"坏或缺失缓存导致每次重启重复维护");
            var refreshed=repo.Load("broken").Single(a=>a.Id==missing.Id);
            ArticleColumnParser.Apply(refreshed,Html("broken",2,"后来正常获取的名称")); repo.Save(refreshed);
            Check(repo.Load("broken").Single(a=>a.Id==missing.Id).ColumnNames=="后来正常获取的名称",
                "本地未解决版本阻止后续新页面修复名称");
        }

        static void ClearAlsoRemovesOwnedBackups()
        {
            using var fixture=new Fixture(); var repo=fixture.Repository;
            repo.Save(Article("clear",1,"清理测试")); var report=ArticleColumnMaintenance.Repair(repo);
            string unrelated=report.BackupPath+".user"; File.WriteAllText(unrelated,"keep");
            repo.ClearHistory();
            Check(!File.Exists(report.BackupPath) && File.Exists(unrelated) && repo.Accounts().Count==0,
                "清空缓存残留合集历史备份，或删除了非本工具备份");
        }

        static ArticleRecord Article(string biz,int mid,string name) => new ArticleRecord { Id=biz+":"+mid+":1",Biz=biz,Mid=mid.ToString(),
            AccountName="合集测试号",Title="原始标题"+mid,Author="原始作者",Status=ArticleStatus.Available,StatusDetail="保留原状态",
            PublishedAt=new DateTime(2024,1,2,3,4,5),ReadCount=17,LikeCount=3,FavoriteCount=2,ShareCount=1,
            MetadataVersion=ArticleMetadata.CurrentVersion,AudioMetadataVersion=1,TitleSource="heading",
            Url="https://mp.weixin.qq.com/s?__biz="+biz+"&mid="+mid+"&idx=1",
            Columns=new List<ColumnInfo> { new ColumnInfo { Id=AlbumId,Name="合集 "+AlbumId,
                Url="https://mp.weixin.qq.com/mp/appmsgalbum?__biz="+biz+"&album_id="+AlbumId } },
            Media=new List<MediaAsset> { new MediaAsset { Kind=MediaKind.Image,Url="https://image.invalid/original.png" } },
            Html=name.Length==0?"":Html(biz,mid,name) };
        static string Html(string biz,int mid,string name)=>"<html><script>var biz='"+biz+"';var mid='"+mid+"';var idx=1;var page={appmsgalbuminfo:{album_id:'"+AlbumId+"',title:'"+name+"',isupdating:'1' * 1,content_size:'3' * 1}};</script><div id='js_content'>原正文不变</div></html>";
        static void Check(bool value,string message) { if(!value)throw new InvalidOperationException("Article column maintenance self-test: "+message); }
        static void ExpectCanceled(Action action)
        { try { action(); throw new InvalidOperationException("维护取消未抛出取消信号"); } catch(OperationCanceledException) { } }

        sealed class InlineProgress : IProgress<ArticleColumnRepairReport>
        {
            readonly Action<ArticleColumnRepairReport> action;
            internal InlineProgress(Action<ArticleColumnRepairReport> action)=>this.action=action;
            public void Report(ArticleColumnRepairReport value)=>action(value);
        }
        sealed class Fixture : IDisposable
        {
            internal readonly string Directory=Path.Combine(Path.GetTempPath(),"WCAE-column-maintenance-"+Guid.NewGuid().ToString("N"));
            internal readonly ArticleRepository Repository;
            internal Fixture()=>Repository=new ArticleRepository(Directory);
            internal SqliteConnection Open()
            {
                var c=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(Directory,"articles.sqlite"),Pooling=false }.ToString());
                c.Open(); return c;
            }
            internal Dictionary<string,JObject> Payloads()
            {
                using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT id,payload FROM articles ORDER BY id";
                var rows=new Dictionary<string,JObject>(StringComparer.Ordinal);
                using var reader=cmd.ExecuteReader(); while(reader.Read())rows.Add(reader.GetString(0),JObject.Parse(reader.GetString(1)));
                return rows;
            }
            internal string AllPayloadText()
            {
                using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT payload FROM articles ORDER BY id";
                var values=new List<string>(); using var reader=cmd.ExecuteReader(); while(reader.Read())values.Add(reader.GetString(0));
                return string.Join("\n",values);
            }
            internal string OtherTables()
            {
                using var c=Open(); var tables=new List<string>();
                using(var cmd=c.CreateCommand())
                {
                    cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name<>'articles' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                    using var reader=cmd.ExecuteReader(); while(reader.Read())tables.Add(reader.GetString(0));
                }
                var values=new JObject();
                foreach(string table in tables)
                {
                    using var cmd=c.CreateCommand(); cmd.CommandText="SELECT * FROM \""+table.Replace("\"","\"\"")+"\" ORDER BY rowid";
                    var rows=new JArray(); using var reader=cmd.ExecuteReader();
                    while(reader.Read())
                    {
                        var row=new JArray();
                        for(int i=0;i<reader.FieldCount;i++)
                        {
                            object value=reader.GetValue(i);
                            row.Add(value is byte[] bytes?Convert.ToBase64String(bytes):value is DBNull?null:Convert.ToString(value,CultureInfo.InvariantCulture));
                        }
                        rows.Add(row);
                    }
                    values[table]=rows;
                }
                return values.ToString(Formatting.None);
            }
            public void Dispose()
            {
                string root=Path.GetFullPath(Directory),parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if(string.Equals(Path.GetDirectoryName(root),parent,StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(root).StartsWith("WCAE-column-maintenance-",StringComparison.Ordinal))
                    try { System.IO.Directory.Delete(root,true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
            }
        }
    }
}
