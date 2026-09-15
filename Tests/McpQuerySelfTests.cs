using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace WCAE
{
    internal static class McpQuerySelfTests
    {
        internal static void Run() => RunAsync().GetAwaiter().GetResult();
        static async Task RunAsync()
        {
            await AccountIsolationAndFilters();
            await ChineseBodySearchAndLocalOnly();
            await ImmutablePaginationAndCursorLifetime();
            await SafeBodyAndPublicMetadata();
            await ValidationCancellationAndReadOnly();
        }

        static async Task AccountIsolationAndFilters()
        {
            using var f = new Fixture();
            var first = Article("a", "1", "渗透测试基础", 15); first.ReadCount = 10; first.LikeCount = 3;
            first.Columns.Add(new ColumnInfo { Id = "4113194063615115265", Name = "码农小站" });
            var second = Article("a", "2", "失败记录", 14); second.Status = ArticleStatus.Failed; second.ReadCount = null;
            second.Columns.Add(new ColumnInfo { Id = "4113194063615115266", Name = "码农小站" });
            var third = Article("a", "3", "删除记录", 13); third.Status = ArticleStatus.Deleted; third.ReadCount = 0;
            var fourth = Article("a", "4", "受限记录", 12); fourth.Status = ArticleStatus.Restricted; fourth.ReadCount = 20;
            var undated = Article("a", "5", "未知日期", 11); undated.PublishedAt = null;
            f.Save(first, second, third, fourth, undated, Article("b", "6", "另一个公众号", 15));
            var accounts = await f.Call("list_accounts", new { });
            Check(accounts.GetProperty("accounts").GetArrayLength() == 2, "公众号列表未完整返回");
            var page = await f.Call("list_articles", new { account_id = "a", sort_by = "read_count", descending = false });
            var rows = Rows(page);
            Check(rows.Length == 5 && rows.All(x => Str(x, "account_id") == "a"), "公众号查询越界");
            Check(Str(rows[0], "title") == "删除记录" && Str(rows[1], "title") == "渗透测试基础" && rows[4].GetProperty("read_count").ValueKind == JsonValueKind.Null,
                "排序没有将未知指标置后，或把未知指标转换为零");
            Check(Str(rows[1], "author") == "甲公众号", "缺失作者没有回退到公众号名称");
            var defaults = await f.Call("list_articles", new { account_id = "a", column_id = (string)null, status = (string)null, from = (string)null,
                through = (string)null, sort_by = (string)null, cursor = (string)null });
            Check(Rows(defaults).Length == 5, "HTTP MCP 工具序列化的可选 null 参数未采用默认值");
            page = await f.Call("list_articles", new { account_id = "a", column_id = "4113194063615115265", from = "2026-09-15", through = "2026-09-15" });
            Check(Rows(page).Length == 1 && Str(Rows(page)[0], "article_id") == first.Id, "长合集 ID 精度、同名合集匹配或日期边界错误");
            page = await f.Call("list_articles", new { account_id = "a", status = "deleted" });
            Check(Rows(page).Length == 1 && Str(Rows(page)[0], "article_id") == third.Id, "失败或受限文章被误判为已删除");
            page = await f.Call("list_articles", new { account_id = "a", column_id = "__unclassified__" });
            Check(Rows(page).Length == 3, "未分类筛选错误");
            var columns = await f.Call("list_columns", new { account_id = "a" });
            Check(columns.GetProperty("columns").GetArrayLength() == 2 && columns.GetProperty("unclassified_count").GetInt32() == 3,
                "不同 ID 的同名合集被合并");
            await f.Error("article_not_found", "get_article", new { account_id = "b", article_id = first.Id });
            await f.Error("article_not_found", "list_media", new { account_id = "b", article_id = first.Id });
        }

        static async Task ChineseBodySearchAndLocalOnly()
        {
            using var f = new Fixture();
            var a = Article("a", "1", "普通标题", 15);
            a.Html = "<script>只在脚本中出现的词</script><div id='js_content'><p>微信公众号<span>静默</span><strong>收集</strong>平台</p><script>隐藏口令词</script></div>";
            var b = Article("a", "2", "静默收集指南", 14); b.Html = "";
            var c = Article("a", "3", "无正文容器", 13); c.Html = "<html><script>var pass_ticket='fake-secret';</script><body>静默收集</body></html>";
            f.Save(a, b, c, Article("b", "4", "静默收集其他号", 15));
            string before = f.LogicalFingerprint();
            var result = await f.Call("search_articles", new { account_id = "a", query = "静默收集", scope = "body" });
            Check(Rows(result).Length == 1 && Str(Rows(result)[0], "article_id") == a.Id, "中文正文跨 inline 标签检索失败或读取了正文容器以外内容");
            Check(result.GetProperty("skipped_uncached_or_unreadable_bodies").GetInt32() == 2, "缺失缓存正文未明确报告");
            result = await f.Call("search_articles", new { account_id = "a", query = "静默收集", scope = "all" });
            Check(Rows(result).Length == 2, "标题与正文合并检索错误");
            result = await f.Call("search_articles", new { account_id = "a", query = "隐藏口令词", scope = "body" });
            Check(Rows(result).Length == 0, "脚本内容进入了正文搜索");
            var body = await f.Call("get_article", new { account_id = "a", article_id = b.Id });
            Check(Str(body, "body_state") == "body_not_cached" && Str(body, "content") == "", "无缓存正文没有结构化返回");
            body = await f.Call("get_article", new { account_id = "a", article_id = c.Id });
            Check(Str(body, "body_state") == "body_not_cached", "整页脚本被当作正文返回");
            Check(before == f.LogicalFingerprint(), "只读正文搜索或查询修改了本地数据库");
        }

        static async Task ImmutablePaginationAndCursorLifetime()
        {
            using var f = new Fixture();
            f.Save(Article("a", "1", "第一页甲", 15), Article("a", "2", "第一页乙", 14), Article("a", "3", "旧尾页", 13), Article("b", "4", "乙号文章", 12));
            var initial = await f.Call("list_articles", new { account_id = "a", page_size = 2 });
            string cursor = Str(initial, "next_cursor");
            Check(cursor.Length > 30 && !cursor.Contains("a:"), "游标不是不透明随机值");
            // The repository deliberately rejects unrelated heading replacements. A genuine
            // completion of the original heading supplies valid update evidence for this test.
            f.Save(Article("a", "3", "旧尾页补全后的标题", 16), Article("a", "5", "新插入文章", 17));
            Check(f.Repository.Load("a").Any(a => a.Id == "a:3:1" && a.Title == "旧尾页补全后的标题") && f.Repository.Load("a").Count == 4,
                "分页更新测试未建立已变化的数据库记录");
            var next = await f.Call("list_articles", new { account_id = "a", page_size = 2, cursor });
            Check(Rows(next).Length == 1 && Str(Rows(next)[0], "title") == "旧尾页" && next.GetProperty("total").GetInt32() == 3,
                "查询中途插入/更新导致已有快照遗漏、重复或内容变化");
            var retry = await f.Call("list_articles", new { account_id = "a", page_size = 2, cursor });
            Check(next.GetRawText() == retry.GetRawText(), "相同游标重试没有返回相同结果");
            await f.Error("cursor_mismatch", "list_articles", new { account_id = "b", page_size = 2, cursor });
            await f.Error("cursor_mismatch", "list_articles", new { account_id = "a", page_size = 2, cursor, status = "available" });
            await f.Error("cursor_mismatch", "search_articles", new { account_id = "a", page_size = 2, cursor, query = "标题" });
            f.Now = f.Now.AddMinutes(11);
            await f.Error("cursor_expired", "list_articles", new { account_id = "a", page_size = 2, cursor });
            initial = await f.Call("list_articles", new { account_id = "a", page_size = 2 }); cursor = Str(initial, "next_cursor");
            f.Service.InvalidateSnapshots();
            await f.Error("cursor_expired", "list_articles", new { account_id = "a", page_size = 2, cursor });
            initial = await f.Call("list_articles", new { account_id = "a", page_size = 2 }); cursor = Str(initial, "next_cursor");
            for (int i = 0; i < 8; i++)
            {
                f.Now = f.Now.AddSeconds(1);
                await f.Call("search_articles", new { account_id = "a", query = "独立查询" + i });
            }
            await f.Error("cursor_expired", "list_articles", new { account_id = "a", page_size = 2, cursor });
        }

        static async Task SafeBodyAndPublicMetadata()
        {
            using var f = new Fixture();
            var a = Article("a", "1", "媒体安全", 15);
            a.Url += "&uin=fixture-uin&key=fixture-key&pass_ticket=fixture-ticket&appmsg_token=fixture-token#secret";
            a.StatusDetail = "key=fixture-detail";
            a.Html = "<script>outside-secret</script><div id='js_content' data-uin='fixture-root'><p>甲😀乙</p>"
                + "<!-- comment-secret --><script>script-secret</script><style>style-secret</style><svg><text>svg-secret</text></svg>"
                + "<img data-src='https://cdn.example.test/image.png?uin=fixture-img&amp;mid=123' onerror='event-secret' data-ticket='attribute-secret'>"
                + "<a href='https://mp.weixin.qq.com/s?__biz=a&amp;mid=2&amp;idx=1&amp;key=fixture-link'>文章</a>"
                + "<iframe src='https://example.test/secret'>iframe-secret</iframe><a href='javascript:alert(1)'>安全文本</a>"
                + "<span>pass_ticket=fixture-visible</span></div>";
            a.Media.Add(new MediaAsset { Kind = MediaKind.Image, Id = "img-1", Name = "图片", Url = "https://cdn.example.test/image.png?key=fixture-media&image=1" });
            a.Media.Add(new MediaAsset { Kind = MediaKind.Audio, Id = "voice-1", Name = "音频", RequiresExtractor = true });
            f.Save(a);
            var result = await f.Call("get_article", new { account_id = "a", article_id = a.Id, format = "html" });
            string json = result.GetRawText(), html = Str(result, "content");
            foreach (string forbidden in new[] { "fixture-", "outside-secret", "comment-secret", "script-secret", "style-secret", "svg-secret", "event-secret", "attribute-secret", "iframe-secret", "javascript:" })
                Check(!json.Contains(forbidden, StringComparison.Ordinal), "MCP 正文/元数据泄露敏感字段或活动内容：" + forbidden);
            Check(html.Contains("image.png") && html.Contains("mid=123"), "净化丢失有效图片或文章标识");
            var media = await f.Call("list_media", new { account_id = "a", article_id = a.Id });
            Check(!media.GetRawText().Contains("fixture-media") && media.GetProperty("items").GetArrayLength() == 2
                && media.GetProperty("items")[1].GetProperty("requires_extractor").GetBoolean(), "媒体清单敏感字段/解析器状态错误");
            var shortBody = Article("a", "2", "分段", 14); shortBody.Html = "<div id='wcae_body'>甲😀乙</div>"; f.Save(shortBody);
            result = await f.Call("get_article", new { account_id = "a", article_id = shortBody.Id, format = "text", max_chars = 2 });
            Check(Str(result, "content") == "甲" && result.GetProperty("next_offset").GetInt32() == 1 && result.GetProperty("total_chars").GetInt32() == 4,
                "正文分段切断 Unicode 字符");
            result = await f.Call("get_article", new { account_id = "a", article_id = shortBody.Id, format = "text", offset = 1, max_chars = 2 });
            Check(Str(result, "content") == "😀" && result.GetProperty("next_offset").GetInt32() == 3, "Unicode 分段偏移错误");
            result = await f.Call("get_article", new { account_id = "a", article_id = shortBody.Id, format = "text", offset = 3, max_chars = 2 });
            Check(Str(result, "content") == "乙" && !result.GetProperty("truncated").GetBoolean() && result.GetProperty("next_offset").ValueKind == JsonValueKind.Null,
                "正文尾部分段状态错误");
            result = await f.Call("get_article", new { account_id = "a", article_id = shortBody.Id });
            Check(Str(result, "format") == "markdown" && Str(result, "content").Contains("甲😀乙"), "默认 Markdown 正文读取失败");
            await f.Error("invalid_arguments", "get_article", new { account_id = "a", article_id = shortBody.Id, offset = 2 });
            string nested = McpQueryService.SafePublicUrl("https://example.test/redirect?url=https%3A%2F%2Fmp.weixin.qq.com%2Fs%3Fkey%3Dnested-secret&safe=1");
            Check(!nested.Contains("nested-secret") && nested.Contains("safe=1"), "嵌套链接泄露会话参数");
            Check(McpQueryService.SafePublicUrl("https://user:password@example.test/a") == "" && McpQueryService.SafePublicUrl("file:///C:/secret") == "", "允许了非公开 URL");
        }

        static async Task ValidationCancellationAndReadOnly()
        {
            using var f = new Fixture(); f.Save(Article("a", "1", "验证", 15));
            using (var connection = f.Open()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE query_sentinel(id TEXT PRIMARY KEY,value BLOB); INSERT INTO query_sentinel VALUES('protected',X'0001020304'); UPDATE article_bodies SET body=X'00010203'";
                command.ExecuteNonQuery();
            }
            string before = f.LogicalFingerprint();
            await f.Error("invalid_arguments", "list_articles", new { account_id = "a", page_size = 201 });
            await f.Error("invalid_arguments", "list_articles", new { account_id = "a", page_size = "20" });
            await f.Error("invalid_arguments", "list_articles", new { account_id = "a", descending = "true" });
            await f.Error("invalid_arguments", "list_articles", new { account_id = "a", from = "2026/09/15" });
            await f.Error("invalid_arguments", "list_articles", new { account_id = "a", from = "2026-09-16", through = "2026-09-15" });
            await f.Error("invalid_arguments", "list_articles", new { account_id = "a", status = "missing" });
            await f.Error("invalid_arguments", "get_article", new { account_id = "a", article_id = "a:1:1", max_chars = 50001 });
            await f.Error("invalid_arguments", "get_article", new { account_id = "a", article_id = "a:1:1", format = "raw_page" });
            await f.Error("invalid_arguments", "search_articles", new { account_id = "a", query = " " });
            await f.Error("invalid_arguments", "list_accounts", new { session = true });
            await f.Error("account_not_found", "list_columns", new { account_id = "missing-account" });
            await f.Error("unknown_tool", "not_a_tool", new { });
            var result = await f.Call("get_article", new { account_id = "a", article_id = "a:1:1" });
            Check(Str(result, "body_state") == "body_unreadable", "损坏正文没有结构化返回");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await f.Service.InvokeAsync("list_accounts", JsonSerializer.SerializeToElement(new { }), cancelled.Token); throw new InvalidOperationException("取消被忽略"); }
            catch (OperationCanceledException) { }
            Check(before == f.LogicalFingerprint(), "MCP 查询修改了文章、正文、断点或其他表");
        }

        static ArticleRecord Article(string account, string mid, string title, int day)
            => new ArticleRecord { Id = account + ":" + mid + ":1", Biz = account, Mid = mid, Idx = 1, AccountName = account == "a" ? "甲公众号" : "乙公众号",
                Url = "https://mp.weixin.qq.com/s?__biz=" + account + "&mid=" + mid + "&idx=1", Title = title, TitleSource = "heading", RawTitle = title,
                Status = ArticleStatus.Available, PublishedAt = new DateTime(2026, 9, day, 12, 30, 0), PublishedAtSource = "page",
                Html = "<div id='js_content'><p>本地测试正文</p></div>" };
        static JsonElement[] Rows(JsonElement result) => result.GetProperty("items").EnumerateArray().ToArray();
        static string Str(JsonElement value, string name) => value.GetProperty(name).GetString();
        static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException("MCP query: " + message); }

        sealed class Fixture : IDisposable
        {
            internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "WCAE-mcp-query-" + Guid.NewGuid().ToString("N"));
            internal readonly ArticleRepository Repository;
            internal readonly McpQueryService Service;
            internal DateTimeOffset Now = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);
            internal Fixture() { Repository = new ArticleRepository(DirectoryPath); Service = new McpQueryService(Repository, () => Now); }
            internal void Save(params ArticleRecord[] articles) { foreach (var article in articles) Repository.Save(article); }
            internal async Task<JsonElement> Call(string tool, object args)
                => JsonSerializer.SerializeToElement(await Service.InvokeAsync(tool, JsonSerializer.SerializeToElement(args), CancellationToken.None),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            internal async Task Error(string code, string tool, object args)
            {
                try { await Call(tool, args); throw new InvalidOperationException("Expected MCP error: " + code); }
                catch (McpApplicationException ex) { Check(ex.Code == code, "错误代码不匹配：" + ex.Code + " / " + code); }
            }
            internal SqliteConnection Open()
            {
                var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "articles.sqlite"), Pooling = false }.ToString());
                connection.Open(); return connection;
            }
            internal string LogicalFingerprint()
            {
                using var connection = Open(); var names = new List<string>(); var output = new StringBuilder();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='table' ORDER BY name";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) { names.Add(reader.GetString(0)); output.Append(reader.GetString(0)).Append('|').Append(reader.GetString(1)).Append('\n'); }
                }
                foreach (var name in names)
                {
                    using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\" ORDER BY rowid";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            object value = reader.GetValue(i); string text = value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value, CultureInfo.InvariantCulture);
                            output.Append(text?.Length ?? 0).Append(':').Append(text).Append('|');
                        }
                        output.Append('\n');
                    }
                }
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output.ToString())));
            }
            public void Dispose()
            {
                string root = Path.GetFullPath(DirectoryPath), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("WCAE-mcp-query-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected MCP fixture directory.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
