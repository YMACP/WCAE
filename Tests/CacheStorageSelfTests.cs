using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace WCAE
{
    public static class CacheStorageSelfTests
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Run()
        {
            // All data belongs to this test. Never change AppPaths or open the user's store.
            var root = Path.Combine(Path.GetTempPath(), "WCAE-cache-clear-tests-" + Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(root, "data");
            Directory.CreateDirectory(directory);
            var neighbor = Path.Combine(root, "neighbor.txt");
            var settings = Path.Combine(directory, "settings.json");
            var exported = Path.Combine(directory, "exported-article.html");
            File.WriteAllText(neighbor, "neighbor must survive");
            File.WriteAllText(settings, "{\"proxyPort\":8879}");
            File.WriteAllText(exported, "<p>user export must survive</p>");
            var repo = new ArticleRepository(directory);
            var first = new ArticleRecord { Id = "article-a", Biz = "account-a", Title = "第一篇", Html = "<p>第一篇正文</p>", Status = ArticleStatus.Available };
            var second = new ArticleRecord { Id = "article-b", Biz = "account-b", Title = "第二篇", Html = "<p>第二篇正文</p>", Status = ArticleStatus.Available };
            repo.SaveAccount(first.Biz, "测试号 A"); repo.SaveAccount(second.Biz, "测试号 B");
            repo.Save(first); repo.Save(second);
            var vault = new SessionVault(directory);
            var sessions = new Dictionary<string, AccountSession>
            {
                { first.Biz, new AccountSession { Biz = first.Biz, Name = "测试号 A", Cookie = "test=a" } },
                { second.Biz, new AccountSession { Biz = second.Biz, Name = "测试号 B", Cookie = "test=b" } }
            };
            vault.Save(sessions);
            Check(repo.Accounts().Count == 2 && repo.LoadBody(first.Id) == first.Html && repo.LoadBody(second.Id) == second.Html, "清空测试种子未完整保存");
            Check(vault.Load().Count == 2, "清空测试会话未完整保存");

            using (var connection = Open(directory)) using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE preferences(value TEXT); INSERT INTO preferences VALUES('preserve'); CREATE TRIGGER reject_clear BEFORE DELETE ON accounts BEGIN SELECT RAISE(ABORT,'test rollback'); END;";
                cmd.ExecuteNonQuery();
            }
            var failed = false;
            try { repo.ClearHistory(); } catch (SqliteException) { failed = true; }
            Check(failed, "清空失败未向调用者报告");
            Check(repo.Accounts().Count == 2 && repo.Load(first.Biz).Count == 1 && repo.Load(second.Biz).Count == 1 && repo.LoadBody(first.Id) == first.Html && repo.LoadBody(second.Id) == second.Html, "清空失败没有完整回滚公众号、文章和正文");
            using (var connection = Open(directory)) using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DROP TRIGGER reject_clear;"; cmd.ExecuteNonQuery();
            }

            repo.ClearHistory();
            vault.Clear();
            var reopened = new ArticleRepository(directory);
            Check(reopened.Accounts().Count == 0 && reopened.Load(first.Biz).Count == 0 && reopened.Load(second.Biz).Count == 0, "重开数据库仍包含历史公众号或文章");
            Check(reopened.LoadBody(first.Id) == "" && reopened.LoadBody(second.Id) == "", "清空后文章正文仍可读取");
            using (var connection = Open(directory)) using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT (SELECT COUNT(*) FROM accounts) + (SELECT COUNT(*) FROM articles) + (SELECT COUNT(*) FROM article_bodies);";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 0, "历史表或孤立正文没有全部清空");
                cmd.CommandText = "SELECT value FROM preferences;";
                Check((string)cmd.ExecuteScalar() == "preserve", "清空历史误删了其他数据表");
            }
            Check(!File.Exists(Path.Combine(directory, "sessions.bin")) && new SessionVault(directory).Load().Count == 0, "会话缓存没有清空");
            Check(File.ReadAllText(neighbor) == "neighbor must survive" && File.ReadAllText(settings) == "{\"proxyPort\":8879}" && File.ReadAllText(exported) == "<p>user export must survive</p>", "清空历史影响了其他文件或用户导出");

            // Clearing an empty cache remains harmless, and both stores remain writable.
            reopened.ClearHistory(); vault.Clear();
            reopened.SaveAccount(first.Biz, "重新识别"); reopened.Save(first);
            vault.Save(new Dictionary<string, AccountSession> { { first.Biz, sessions[first.Biz] } });
            var afterSave = new ArticleRepository(directory);
            Check(afterSave.Accounts().Count == 1 && afterSave.Load(first.Biz).Count == 1 && afterSave.LoadBody(first.Id) == first.Html, "清空后无法重新保存公众号或文章正文");
            Check(new SessionVault(directory).Load().Count == 1, "清空后会话存储不可继续使用");
            AccountNameStorageSelfTests.Run();
        }

        static SqliteConnection Open(string directory)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "articles.sqlite"), Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }
    }
}
