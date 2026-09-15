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
    internal static class AccountNameStorageSelfTests
    {
        private static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
        internal static void Run()
        {
            string root=Path.Combine(Path.GetTempPath(),"WCAE-name-storage-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            TestAccountProtection(Path.Combine(root,"protect"));
            TestRepairAndVault(Path.Combine(root,"repair"));
            TestSaveSynchronization(Path.Combine(root,"save"));
            string resolved=Path.GetFullPath(root), temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if(string.Equals(Path.GetDirectoryName(resolved),temporary,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("WCAE-name-storage-",StringComparison.Ordinal))Directory.Delete(resolved,true);
        }
        private static void TestAccountProtection(string root)
        {
            var repository=new ArticleRepository(root);
            repository.SaveAccount("account-protect","船山信安");
            foreach(string invalid in new[] { "data-miniprogram-nickname","data-nickname","account-protect","",null })
                repository.SaveAccount("account-protect",invalid);
            Check(repository.Accounts()["account-protect"]=="船山信安","无效账号名称覆盖了已知正确名称");
            repository.SaveAccount("account-unknown","data-miniprogram-nickname");
            Check(repository.Accounts()["account-unknown"]=="","未知账号又将Biz或字段标记保存成显示名");
            Check(repository.AccountName("account-protect")=="船山信安" && repository.AccountName("account-unknown")=="" && repository.AccountName("missing")=="","单账号名称查询没有返回规范有效名或空值");
            repository.SaveAccount("english-account","OpenAI News"); repository.SaveAccount("numeric-account","123456");
            repository.RepairAccountNames();
            Check(repository.Accounts()["english-account"]=="OpenAI News" && repository.Accounts()["numeric-account"]=="123456","可信英文或数字名称被当成污染数据清理");
        }
        private static void TestRepairAndVault(string root)
        {
            var repository=new ArticleRepository(root);
            SeedAccount(root,"biz-a","data-miniprogram-nickname");
            SeedAccount(root,"biz-b","biz-b");
            SeedAccount(root,"biz-c","123456");
            SeedAccount(root,"biz-d","data-nick-name");
            SeedAccount(root,"biz-e","data-nickname");
            SeedArticle(root,"a-body","biz-a","data-miniprogram-nickname",Body("biz-a","船山信安"));
            SeedArticle(root,"a-old","biz-a","Trusted Historical Name",Body("biz-a","Trusted Historical Name"));
            // The newest body determines the account display, while an already trusted historical name remains unchanged.
            SeedArticle(root,"a-new","biz-a","data-miniprogram-nickname",Body("biz-a","船山信安"));
            SeedArticle(root,"b-cross","biz-b","data-miniprogram-nickname",Body("biz-a","不应串入另一个公众号"));
            SeedArticle(root,"c-empty","biz-c","",null);
            SeedArticle(root,"d-no-body","biz-d","data-miniprogram-nickname",null);
            SeedArticle(root,"d-trusted","biz-d","可信旧文章名",null);
            SeedArticle(root,"e-vault","biz-e","data-miniprogram-nickname",null);
            SeedArticle(root,"a-corrupt-body","biz-a","data-miniprogram-nickname",null);
            Execute(root,"INSERT INTO article_bodies(id,body) VALUES('a-corrupt-body',@body)",("@body",new byte[] { 1,2,3,4 }));
            var beforeBodies=Bodies(root); var beforePayloads=Payloads(root);
            using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel(); bool observed=false;
                try { repository.RepairAccountNames(canceled.Token); } catch(OperationCanceledException) { observed=true; }
                Check(observed && repository.Accounts()["biz-a"]=="data-miniprogram-nickname" && Payloads(root).All(pair=>pair.Value==beforePayloads[pair.Key]),"已取消的名称修复仍修改了缓存");
            }
            Execute(root,"CREATE TABLE preferences(value TEXT); INSERT INTO preferences VALUES('preserved'); CREATE TRIGGER stop_name_repair BEFORE UPDATE ON articles WHEN OLD.id='b-cross' BEGIN SELECT RAISE(ABORT,'fixture rollback'); END;");
            bool failed=false;
            try { repository.RepairAccountNames(); } catch(SqliteException) { failed=true; }
            Check(failed,"名称修复事务失败没有向调用者报告");
            Check(repository.Accounts()["biz-a"]=="data-miniprogram-nickname" && Payloads(root).All(pair=>pair.Value==beforePayloads[pair.Key]),"名称修复失败没有整体回滚账号与文章修改");
            Execute(root,"DROP TRIGGER stop_name_repair;");
            var fixedNames=repository.RepairAccountNames();
            Check(fixedNames["biz-a"]=="船山信安" && fixedNames["biz-b"]=="" && fixedNames["biz-c"]=="123456" && fixedNames["biz-d"]=="","正文恢复、跨Biz限制或没有可信正文时清空错名的行为错误");
            var after=Payloads(root);
            Check(Name(after["a-body"])=="船山信安" && Name(after["a-new"])=="船山信安" && Name(after["a-old"])=="Trusted Historical Name","同Biz错误文章没有修正，或可信旧文章名被覆盖");
            Check(Name(after["b-cross"])=="" && Name(after["c-empty"])=="123456" && Name(after["d-no-body"])=="" && Name(after["d-trusted"])=="可信旧文章名","无正文或错误Biz文章名称处理错误");
            Check(after.Count==beforePayloads.Count && Bodies(root).Count==beforeBodies.Count,"名称修复删除了文章或正文");
            foreach(var pair in beforePayloads)
            {
                var expected=JObject.Parse(pair.Value); var actual=JObject.Parse(after[pair.Key]);
                expected.Remove("AccountName"); actual.Remove("AccountName");
                Check(JToken.DeepEquals(expected,actual),"名称修复更改了正文以外的文章数据："+pair.Key);
            }
            Check(Bodies(root).All(pair=>pair.Value.SequenceEqual(beforeBodies[pair.Key])),"名称修复重写或损坏了压缩正文");
            Check((string)Scalar(root,"SELECT value FROM preferences")=="preserved","名称修复影响了其他表");
            Execute(root,"CREATE TABLE writes(kind TEXT); CREATE TRIGGER name_account_writes AFTER UPDATE ON accounts BEGIN INSERT INTO writes VALUES('account'); END; CREATE TRIGGER name_article_writes AFTER UPDATE ON articles BEGIN INSERT INTO writes VALUES('article'); END;");
            repository.RepairAccountNames();
            Check(Convert.ToInt64(Scalar(root,"SELECT COUNT(*) FROM writes"))==0,"重复名称修复仍在重写已经正确的缓存");

            var vault=new SessionVault(root);
            var sessions=new Dictionary<string,AccountSession>
            {
                ["biz-a"]=Session("biz-a","data-miniprogram-nickname"),
                ["biz-b"]=Session("biz-b","biz-b"),
                ["biz-c"]=Session("biz-c","123456"),
                ["biz-e"]=Session("biz-e","Vault Trusted Name"),
                ["separate-session"]=Session("separate-session","Separate Valid Name")
            };
            vault.Save(sessions);
            var sessionBefore=sessions.ToDictionary(pair=>pair.Key,pair=>JObject.FromObject(pair.Value));
            AccountNameMaintenance.Repair(repository,vault,sessions);
            var restored=vault.Load();
            Check(restored["biz-a"].Name=="船山信安" && restored["biz-b"].Name=="" && restored["biz-c"].Name=="123456" && restored["separate-session"].Name=="Separate Valid Name","会话名称维护没有正确按Biz同步");
            Check(restored["biz-e"].Name=="Vault Trusted Name" && repository.AccountName("biz-e")=="Vault Trusted Name" && Name(Payloads(root)["e-vault"])=="Vault Trusted Name","已有可信会话名没有补回无正文的错误账号和文章名称");
            foreach(var pair in restored)
            {
                var expected=sessionBefore[pair.Key]; var actual=JObject.FromObject(pair.Value);
                expected.Remove("Name"); actual.Remove("Name");
                Check(JToken.DeepEquals(expected,actual),"修正会话名称改变了Cookie、凭据或时间戳");
            }
            byte[] savedVault=File.ReadAllBytes(Path.Combine(root,"sessions.bin"));
            AccountNameMaintenance.Repair(repository,vault,sessions);
            Check(File.ReadAllBytes(Path.Combine(root,"sessions.bin")).SequenceEqual(savedVault),"名称已正确时仍重写了会话保险库");
        }
        private static void TestSaveSynchronization(string root)
        {
            var repository=new ArticleRepository(root);
            SeedAccount(root,"biz-save","data-miniprogram-nickname");
            SeedArticle(root,"existing-bad","biz-save","data-miniprogram-nickname",null);
            SeedArticle(root,"existing-good","biz-save","可信历史昵称",null);
            SeedAccount(root,"other-biz","Other Account");
            SeedArticle(root,"other-article","other-biz","data-miniprogram-nickname",null);
            var article=new ArticleRecord { Id="fresh-body", Biz="biz-save", AccountName="data-miniprogram-nickname", Title="新正文", Html=Body("biz-save","正确公众号"), Status=ArticleStatus.Available };
            Execute(root,"CREATE TRIGGER reject_article_name_sync BEFORE UPDATE ON articles WHEN OLD.id='existing-bad' BEGIN SELECT RAISE(ABORT,'fixture rollback'); END;");
            bool failed=false; try { repository.Save(article); } catch(SqliteException) { failed=true; }
            Check(failed && repository.Accounts()["biz-save"]=="data-miniprogram-nickname" && !Payloads(root).ContainsKey(article.Id) && !Bodies(root).ContainsKey(article.Id),"保存文章时名称同步失败没有回滚新文章、正文及账号名");
            Execute(root,"DROP TRIGGER reject_article_name_sync;"); repository.Save(article);
            var saved=Payloads(root);
            Check(repository.Accounts()["biz-save"]=="正确公众号" && Name(saved["existing-bad"])=="正确公众号" && Name(saved[article.Id])=="正确公众号","可信正文名称没有同步账号与同Biz错误文章");
            Check(Name(saved["existing-good"])=="可信历史昵称" && Name(saved["other-article"])=="data-miniprogram-nickname","文章保存覆盖了可信旧名或串改其他公众号");
            repository.Save(new ArticleRecord { Id=article.Id, Biz=article.Biz, AccountName="data-miniprogram-nickname", Title=article.Title, Status=ArticleStatus.Pending });
            Check(repository.Load(article.Biz).Single(item=>item.Id==article.Id).AccountName=="正确公众号" && repository.LoadBody(article.Id)==article.Html,"待采集记录重新覆盖了正确名称或已下载正文");
        }
        private static AccountSession Session(string biz,string name) => new AccountSession { Biz=biz, Name=name, UserName="user-"+biz, Cookie="fixture-cookie", Key="fixture-key", Uin="fixture-uin", PassTicket="fixture-ticket", AppMsgToken="fixture-token", CapturedAt=new DateTime(2026,1,2,3,4,5), Headers=new Dictionary<string,string> { ["fixture-header"]="preserve" } };
        private static string Body(string biz,string name) => "<html><script>var biz = '"+biz+"';</script><strong id='js_name' data-miniprogram-nickname='"+name+"'>"+name+"</strong><div id='js_content'>保留正文</div></html>";
        private static string Name(string payload) => (string)JObject.Parse(payload)["AccountName"]??"";
        private static void SeedAccount(string root,string biz,string name) => Execute(root,"INSERT OR REPLACE INTO accounts(biz,name) VALUES(@biz,@name)",("@biz",biz),("@name",name));
        private static void SeedArticle(string root,string id,string biz,string name,string html)
        {
            var payload=JObject.FromObject(new ArticleRecord { Id=id, Biz=biz, AccountName=name, Title="保留标题 "+id, ReadCount=42, Status=ArticleStatus.Available, Columns=new List<ColumnInfo> { new ColumnInfo { Name="保留专栏" } } });
            payload["legacy-extra-field"]="must survive";
            Execute(root,"INSERT INTO articles(id,biz,payload) VALUES(@id,@biz,@payload)",("@id",id),("@biz",biz),("@payload",payload.ToString(Formatting.None)));
            if(html==null)return;
            using(var output=new MemoryStream())
            {
                using(var gzip=new GZipStream(output,CompressionMode.Compress,true)) { byte[] bytes=Encoding.UTF8.GetBytes(html); gzip.Write(bytes,0,bytes.Length); }
                Execute(root,"INSERT INTO article_bodies(id,body) VALUES(@id,@body)",("@id",id),("@body",output.ToArray()));
            }
        }
        private static Dictionary<string,string> Payloads(string root)
        {
            var result=new Dictionary<string,string>(); using(var connection=Open(root))using(var command=connection.CreateCommand())
            { command.CommandText="SELECT id,payload FROM articles"; using(var reader=command.ExecuteReader())while(reader.Read())result[reader.GetString(0)]=reader.GetString(1); }
            return result;
        }
        private static Dictionary<string,byte[]> Bodies(string root)
        {
            var result=new Dictionary<string,byte[]>(); using(var connection=Open(root))using(var command=connection.CreateCommand())
            { command.CommandText="SELECT id,body FROM article_bodies"; using(var reader=command.ExecuteReader())while(reader.Read())result[reader.GetString(0)]=(byte[])reader[1]; }
            return result;
        }
        private static SqliteConnection Open(string root)
        {
            var connection=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(root,"articles.sqlite"), Pooling=false }.ToString()); connection.Open(); return connection;
        }
        private static void Execute(string root,string sql,params (string Name,object Value)[] values)
        {
            using(var connection=Open(root))using(var command=connection.CreateCommand())
            { command.CommandText=sql; foreach(var value in values)command.Parameters.AddWithValue(value.Name,value.Value); command.ExecuteNonQuery(); }
        }
        private static object Scalar(string root,string sql)
        {
            using(var connection=Open(root))using(var command=connection.CreateCommand()) { command.CommandText=sql; return command.ExecuteScalar(); }
        }
    }
}
