using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using HtmlAgilityPack;
using Microsoft.Data.Sqlite;

namespace WCAE
{
    // Only local, read-only operations. Never prepares exports, refreshes a session or downloads a body.
    internal sealed class McpQueryService
    {
        const int MaximumRows = 25000, MaximumSnapshots = 8, MaximumCachedRows = 50000;
        const long MaximumCachedBytes = 32 * 1024 * 1024;
        static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(10);
        static readonly string CoverageNote = "结果来自本地已保存的数据，不代表已收齐公众号全部历史文章；未知指标为 null。";
        readonly ArticleRepository repository;
        readonly Func<DateTimeOffset> clock;
        readonly SemaphoreSlim querySlots = new SemaphoreSlim(2, 2);
        readonly object gate = new object();
        readonly Dictionary<string, Snapshot> snapshots = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        readonly Dictionary<string, Cursor> cursors = new Dictionary<string, Cursor>(StringComparer.Ordinal);
        long generation;

        sealed class Snapshot
        {
            internal string Id, Signature, Account;
            internal DateTimeOffset Created, Expires;
            internal List<JsonElement> Items;
            internal long Bytes;
            internal int SkippedBodies;
            internal Dictionary<int, string> Tokens = new Dictionary<int, string>();
        }
        sealed class Cursor { internal Snapshot Snapshot; internal int Offset; }

        internal McpQueryService(ArticleRepository repository) : this(repository, () => DateTimeOffset.UtcNow) { }
        internal McpQueryService(ArticleRepository repository, Func<DateTimeOffset> clock)
        { this.repository = repository ?? throw new ArgumentNullException(nameof(repository)); this.clock = clock; }

        internal void InvalidateSnapshots()
        { lock (gate) { generation++; snapshots.Clear(); cursors.Clear(); } }

        internal async Task<object> InvokeAsync(string tool, JsonElement args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (args.ValueKind != JsonValueKind.Object) throw Error("invalid_arguments", "参数必须是 JSON 对象。");
            await querySlots.WaitAsync(token).ConfigureAwait(false);
            try { return await Task.Run(() => Invoke(tool, args, token), token).ConfigureAwait(false); }
            finally { querySlots.Release(); }
        }

        object Invoke(string tool, JsonElement args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            switch (tool)
            {
                case "list_accounts":
                    EnsureFields(args);
                    return new { Accounts = repository.McpAccounts(token).Select(a => new
                    { AccountId = a.Id, Name = SafeText(a.Name, 300), StoredArticleCount = a.Count }).ToArray(), Coverage = "local_cache", CoverageNote };
                case "list_columns": return Columns(args, token);
                case "list_articles": return Page(args, false, token);
                case "search_articles": return Page(args, true, token);
                case "get_article": return Article(args, token);
                case "list_media": return Media(args, token);
                default: throw Error("unknown_tool", "未知的本地查询工具。");
            }
        }

        object Columns(JsonElement args, CancellationToken token)
        {
            EnsureFields(args, "account_id");
            string account = Required(args, "account_id");
            var articles = Load(account, token);
            var columns = new Dictionary<string, (string Name, string Url, int Count)>(StringComparer.Ordinal);
            int unclassified = 0;
            foreach (var article in articles)
            {
                token.ThrowIfCancellationRequested();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var column in article.Columns ?? new List<ColumnInfo>())
                {
                    if (column == null) continue;
                    string id = column.Id ?? "";
                    if (id.Length == 0 || !seen.Add(id)) continue;
                    columns.TryGetValue(id, out var previous);
                    string name = SafeText(column.Name, 300);
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("合集 ", StringComparison.Ordinal)) name = previous.Name ?? name;
                    columns[id] = (name, SafePublicUrl(column.Url), previous.Count + 1);
                }
                if ((article.Columns?.Count ?? 0) == 0) unclassified++;
            }
            return new { AccountId = account, Columns = columns.OrderBy(x => x.Value.Name, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new { ColumnId = x.Key, Name = x.Value.Name, Url = x.Value.Url, ArticleCount = x.Value.Count }).ToArray(),
                UnclassifiedCount = unclassified, UnclassifiedColumnId = "__unclassified__", Coverage = "local_cache", CoverageNote };
        }

        object Page(JsonElement args, bool search, CancellationToken token)
        {
            if (search) EnsureFields(args, "account_id", "query", "scope", "page_size", "cursor");
            else EnsureFields(args, "account_id", "column_id", "status", "from", "through", "sort_by", "descending", "page_size", "cursor");
            string account = Required(args, "account_id"), cursor = Optional(args, "cursor", "", 256);
            int size = Integer(args, "page_size", 50, 1, 200);
            string query = search ? Required(args, "query", 1000).Trim() : "";
            string scope = search ? Choice(args, "scope", "title", "title", "body", "all") : "";
            string column = search ? "" : Optional(args, "column_id", "", 1000);
            string status = search ? "all" : Choice(args, "status", "all", "all", "pending", "available", "deleted", "restricted", "failed", "local_snapshot");
            string sort = search ? "published_at" : Choice(args, "sort_by", "published_at", "published_at", "read_count", "like_count", "favorite_count", "share_count");
            bool descending = search || Boolean(args, "descending", true);
            DateTime? from = search ? null : Date(args, "from"), through = search ? null : Date(args, "through");
            if (from > through) throw Error("invalid_arguments", "from 不得晚于 through。");
            string signature = JsonSerializer.Serialize(new { search, account, query, scope, column, status, sort, descending, from, through, size });
            long version;
            lock (gate)
            {
                Prune(); version = generation;
                if (cursor.Length > 0)
                {
                    if (!cursors.TryGetValue(cursor, out var found)) throw Error("cursor_expired", "分页游标无效或已过期，请重新查询第一页。");
                    if (found.Snapshot.Account != account || found.Snapshot.Signature != signature)
                        throw Error("cursor_mismatch", "分页游标与公众号或查询参数不匹配；请保留第一页的查询参数。");
                    token.ThrowIfCancellationRequested();
                    return Slice(found.Snapshot, found.Offset, size);
                }
            }
            var articles = Load(account, token);
            IEnumerable<ArticleRecord> filtered = articles;
            if (column == "__unclassified__") filtered = filtered.Where(a => (a.Columns?.Count ?? 0) == 0);
            else if (column.Length > 0) filtered = filtered.Where(a => a.Columns?.Any(c => c != null && c.Id == column) == true);
            if (status != "all") filtered = filtered.Where(a => StatusCode(a.Status) == status);
            if (from.HasValue) filtered = filtered.Where(a => a.PublishedAt.HasValue && a.PublishedAt.Value.Date >= from.Value);
            if (through.HasValue) filtered = filtered.Where(a => a.PublishedAt.HasValue && a.PublishedAt.Value.Date <= through.Value);
            int skippedBodies = 0;
            var matched = new List<ArticleRecord>();
            foreach (var article in filtered)
            {
                token.ThrowIfCancellationRequested();
                if (search)
                {
                    bool matches = scope != "body" && Contains(article.Title, query);
                    if (!matches && scope != "title")
                    {
                        try
                        {
                            var body = Body(article.Biz, article.Id, token);
                            matches = body != null && Contains(PlainBody(body, token), query);
                            if (body == null) skippedBodies++;
                        }
                        catch (InvalidDataException) { skippedBodies++; }
                    }
                    if (!matches) continue;
                }
                matched.Add(article);
            }
            Func<ArticleRecord, long?> key = sort == "read_count" ? a => a.ReadCount : sort == "like_count" ? a => a.LikeCount
                : sort == "favorite_count" ? a => a.FavoriteCount : sort == "share_count" ? a => a.ShareCount : a => a.PublishedAt?.Ticks;
            var known = matched.OrderBy(a => !key(a).HasValue);
            var ordered = (descending ? known.ThenByDescending(key) : known.ThenBy(key)).ThenBy(a => a.Id, StringComparer.Ordinal);
            var items = new List<JsonElement>(); long bytes = 0;
            foreach (var article in ordered)
            {
                token.ThrowIfCancellationRequested();
                var item = JsonSerializer.SerializeToElement(Metadata(article), SnakeJson);
                bytes += Encoding.UTF8.GetByteCount(item.GetRawText());
                if (bytes > MaximumCachedBytes) throw Error("query_too_large", "查询快照超过 32 MiB，请缩小日期、合集或关键词范围。");
                items.Add(item);
            }
            var now = clock();
            var snapshot = new Snapshot { Id = RandomToken(), Account = account, Signature = signature, Created = now,
                Expires = now + SnapshotLifetime, Items = items, Bytes = bytes, SkippedBodies = skippedBodies };
            lock (gate)
            {
                token.ThrowIfCancellationRequested(); Prune();
                if (version != generation) throw Error("cache_changed", "历史缓存已清空，请重新查询。");
                while (snapshots.Count > 0 && (snapshots.Count >= MaximumSnapshots || snapshots.Values.Sum(s => s.Items.Count) + items.Count > MaximumCachedRows
                    || snapshots.Values.Sum(s => s.Bytes) + bytes > MaximumCachedBytes)) Remove(snapshots.Values.OrderBy(s => s.Created).First());
                snapshots[snapshot.Id] = snapshot;
                return Slice(snapshot, 0, size);
            }
        }

        object Slice(Snapshot snapshot, int offset, int size)
        {
            int next = Math.Min(offset + size, snapshot.Items.Count); string nextCursor = null;
            if (next < snapshot.Items.Count)
            {
                if (!snapshot.Tokens.TryGetValue(next, out nextCursor))
                {
                    nextCursor = RandomToken(); snapshot.Tokens[next] = nextCursor;
                    cursors[nextCursor] = new Cursor { Snapshot = snapshot, Offset = next };
                }
            }
            return new { AccountId = snapshot.Account, Items = snapshot.Items.Skip(offset).Take(size).ToArray(), Total = snapshot.Items.Count,
                NextCursor = nextCursor, SnapshotCreatedAt = snapshot.Created, ExpiresAt = snapshot.Expires,
                SkippedUncachedOrUnreadableBodies = snapshot.SkippedBodies, Coverage = "local_cache", CoverageNote };
        }
        void Prune() { foreach (var snapshot in snapshots.Values.Where(s => s.Expires <= clock()).ToArray()) Remove(snapshot); }
        void Remove(Snapshot snapshot) { snapshots.Remove(snapshot.Id); foreach (string token in snapshot.Tokens.Values) cursors.Remove(token); }
        static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        static readonly JsonSerializerOptions SnakeJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        object Article(JsonElement args, CancellationToken token)
        {
            EnsureFields(args, "account_id", "article_id", "format", "offset", "max_chars");
            string account = Required(args, "account_id"), id = Required(args, "article_id");
            string format = Choice(args, "format", "markdown", "markdown", "text", "html");
            int offset = Integer(args, "offset", 0, 0, 32000000), length = Integer(args, "max_chars", 12000, 1, 50000);
            var article = Find(account, id, token); HtmlNode body;
            try { body = Body(account, id, token); }
            catch (InvalidDataException) { return EmptyArticle(article, format, "body_unreadable", "本地正文缓存损坏或过大，未发起网络获取。"); }
            if (body == null) return EmptyArticle(article, format, "body_not_cached", "本地未保存可读取的正文容器，未发起网络获取。");
            token.ThrowIfCancellationRequested();
            string content = format == "html" ? body.InnerHtml : format == "text" ? PlainBody(body, token) : new ReverseMarkdown.Converter().Convert(body.InnerHtml);
            content = RedactSecrets(content); token.ThrowIfCancellationRequested();
            if (offset > content.Length || offset > 0 && offset < content.Length && char.IsLowSurrogate(content[offset]) && char.IsHighSurrogate(content[offset - 1]))
                throw Error("invalid_arguments", "offset 超出正文范围或位于 Unicode 字符中间。");
            int count = Math.Min(length, content.Length - offset);
            if (count > 0 && offset + count < content.Length && char.IsHighSurrogate(content[offset + count - 1]) && char.IsLowSurrogate(content[offset + count])) count--;
            if (count == 0 && offset < content.Length) throw Error("invalid_arguments", "max_chars 太小，无法容纳当前位置的完整 Unicode 字符。");
            bool truncated = offset + count < content.Length;
            return new { Article = Metadata(article), BodyState = "cached", Format = format, Content = content.Substring(offset, count), Offset = offset,
                NextOffset = truncated ? (int?)(offset + count) : null, Truncated = truncated, TotalChars = content.Length,
                OffsetUnit = "utf16_code_units", Coverage = "local_cache", CoverageNote };
        }
        static object EmptyArticle(ArticleRecord article, string format, string code, string reason)
            => new { Article = Metadata(article), BodyState = code, Reason = reason, Format = format, Content = "", Offset = 0,
                NextOffset = (int?)null, Truncated = false, TotalChars = 0, OffsetUnit = "utf16_code_units", Coverage = "local_cache", CoverageNote };

        object Media(JsonElement args, CancellationToken token)
        {
            EnsureFields(args, "account_id", "article_id");
            var article = Find(Required(args, "account_id"), Required(args, "article_id"), token);
            var media = (article.Media ?? new List<MediaAsset>()).Where(m => m != null).Select(m =>
            {
                token.ThrowIfCancellationRequested();
                string url = SafePublicUrl(m.Url);
                string reason = SafeText(m.UnavailableReason, 1000);
                if (url.Length == 0 && reason.Length == 0) reason = m.RequiresExtractor ? "需由导出服务解析，查询未触发下载。" : "本地未保存可公开返回的媒体地址。";
                return new { Kind = m.Kind.ToString().ToLowerInvariant(), Name = SafeText(m.Name, 500), Id = SafeText(m.Id, 1000), Url = url,
                    RequiresExtractor = m.RequiresExtractor, UnavailableReason = reason };
            }).ToArray();
            return new { AccountId = article.Biz, ArticleId = article.Id, Items = media, Total = media.Length, Downloaded = false,
                AudioMetadataComplete = article.AudioMetadataVersion >= 1, Coverage = "local_cache", CoverageNote = "仅返回已保存的媒体清单；空清单不证明原文没有媒体。" };
        }

        List<ArticleRecord> Load(string account, CancellationToken token)
        {
            var rows = repository.McpLoad(account, MaximumRows, token);
            token.ThrowIfCancellationRequested();
            return ArticleQuery.ResolveReferences(rows);
        }
        ArticleRecord Find(string account, string id, CancellationToken token)
            => repository.McpFind(account, id, token) ?? throw Error("article_not_found", "指定公众号下没有这篇本地文章。");
        HtmlNode Body(string account, string id, CancellationToken token)
        {
            string html = repository.McpLoadBody(account, id, token);
            if (string.IsNullOrWhiteSpace(html)) return null;
            var document = new HtmlDocument(); document.LoadHtml(html); token.ThrowIfCancellationRequested();
            var source = document.GetElementbyId("js_content") ?? document.GetElementbyId("wcae_body") ?? document.GetElementbyId("wechatlist_body");
            if (source == null) return null;
            var clean = new HtmlDocument(); clean.LoadHtml("<div>" + source.InnerHtml + "</div>");
            var root = clean.DocumentNode.SelectSingleNode("/div");
            var forbidden = new HashSet<string>(new[] { "script", "style", "iframe", "object", "embed", "base", "link", "meta", "form", "input", "button", "textarea", "select", "noscript", "svg", "math", "template", "foreignobject" }, StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(new[] { "href", "src", "poster", "alt", "title", "colspan", "rowspan", "width", "height", "controls" }, StringComparer.OrdinalIgnoreCase);
            foreach (var node in root.Descendants().ToArray())
            {
                token.ThrowIfCancellationRequested();
                if (node.NodeType == HtmlNodeType.Comment || forbidden.Contains(node.Name)) { node.Remove(); continue; }
                if (node.NodeType == HtmlNodeType.Text) { ((HtmlTextNode)node).Text = RedactSecrets(((HtmlTextNode)node).Text); continue; }
                // WeChat commonly lazy-loads images using data-src. Keep only the sanitized public URL.
                if (node.Name.Equals("img", StringComparison.OrdinalIgnoreCase) && node.GetAttributeValue("data-src", "").Length > 0)
                    node.SetAttributeValue("src", node.GetAttributeValue("data-src", ""));
                foreach (var attr in node.Attributes.ToArray())
                {
                    if (!allowed.Contains(attr.Name)) { node.Attributes.Remove(attr); continue; }
                    if (attr.Name == "href" || attr.Name == "src" || attr.Name == "poster")
                    {
                        string url = SafePublicUrl(attr.Value);
                        if (url.Length == 0) node.Attributes.Remove(attr); else attr.Value = WebUtility.HtmlEncode(url);
                    }
                    else attr.Value = WebUtility.HtmlEncode(SafeText(WebUtility.HtmlDecode(attr.Value), 1000));
                }
            }
            return root;
        }

        static string PlainBody(HtmlNode body, CancellationToken token)
        {
            var output = new StringBuilder();
            var blocks = new HashSet<string>(new[] { "p", "div", "section", "article", "h1", "h2", "h3", "h4", "h5", "h6", "li", "tr", "blockquote", "pre", "br" }, StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<(HtmlNode Node, bool End)>(); pending.Push((body, false));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested(); var current = pending.Pop(); var node = current.Node;
                if (node.NodeType == HtmlNodeType.Text) { output.Append(WebUtility.HtmlDecode(node.InnerText)); continue; }
                bool block = blocks.Contains(node.Name);
                if (block && output.Length > 0 && output[output.Length - 1] != '\n') output.Append('\n');
                if (current.End) continue;
                pending.Push((node, true));
                for (int i = node.ChildNodes.Count - 1; i >= 0; i--) pending.Push((node.ChildNodes[i], false));
            }
            return output.ToString().Replace("\r\n", "\n").Trim();
        }
        static bool Contains(string text, string query) => (text ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        internal static string StatusCode(ArticleStatus status) => status == ArticleStatus.Pending ? "pending" : status == ArticleStatus.Available ? "available"
            : status == ArticleStatus.Deleted ? "deleted" : status == ArticleStatus.Restricted ? "restricted" : status == ArticleStatus.Failed ? "failed"
            : status == ArticleStatus.LocalSnapshot ? "local_snapshot" : "unknown";
        static object Metadata(ArticleRecord article)
            => new { ArticleId = article.Id, AccountId = article.Biz, AccountName = SafeText(article.AccountName, 300), Title = SafeText(article.Title, 2000),
                Digest = SafeText(article.Digest, 500), Author = SafeText(string.IsNullOrWhiteSpace(article.Author) ? article.AccountName : article.Author, 300),
                PublishedAt = article.PublishedAt?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), PublishedTimezone = "Asia/Shanghai",
                article.PublishedUnixSeconds, article.ReadCount, article.LikeCount, article.FavoriteCount, article.ShareCount,
                Status = StatusCode(article.Status), StatusText = article.StatusText, StatusDetail = SafeText(article.StatusDetail, 1000),
                Columns = (article.Columns ?? new List<ColumnInfo>()).Where(c => c != null).Select(c => new
                { ColumnId = c.Id, Name = SafeText(c.Name, 300), Url = SafePublicUrl(c.Url) }).ToArray(),
                Url = SafePublicUrl(article.Url), ContentKind = article.ContentKind.ToString(),
                Source = article.Status == ArticleStatus.LocalSnapshot || (article.TitleSource ?? "").StartsWith("native_", StringComparison.Ordinal) ? "native_local_snapshot" : "saved_article_metadata",
                article.UpdatedAt };

        static readonly HashSet<string> SecretKeys = new HashSet<string>(new[] { "uin", "key", "pass_ticket", "appmsg_token", "wxtoken", "wxtokenkey", "sessionid", "session_id", "token", "access_token", "auth", "authorization", "cookie", "cookies", "ticket", "jwt", "password", "passwd", "credential", "credentials" }, StringComparer.OrdinalIgnoreCase);
        internal static string SafePublicUrl(string value)
        {
            value = WebUtility.HtmlDecode(value ?? "").Trim();
            if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
            if (value.StartsWith("/", StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal)) value = "https://mp.weixin.qq.com" + value;
            if (value.Length > 16384 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length > 0) return "";
            try
            {
                var query = HttpUtility.ParseQueryString(uri.Query);
                foreach (string key in query.AllKeys.Where(k => k != null).ToArray())
                {
                    string normalized = key.Replace("-", "_").ToLowerInvariant();
                    string queryValue = query[key] ?? "";
                    if (SecretKeys.Contains(normalized) || normalized.EndsWith("_token", StringComparison.Ordinal) || normalized.EndsWith("_ticket", StringComparison.Ordinal)
                        || Regex.IsMatch(queryValue, @"(?i)(?:uin|key|pass_ticket|appmsg_token|access_token|cookie|sessionid)\s*=")) query.Remove(key);
                }
                // Bare query values and fragments can carry nested links or session state.
                query.Remove(null);
                return new UriBuilder(uri) { Query = query.ToString(), Fragment = "" }.Uri.AbsoluteUri;
            }
            catch (ArgumentException) { return ""; }
        }
        static readonly Regex SecretAssignment = new Regex(@"(?i)\b(uin|key|pass_ticket|appmsg_token|wxtoken(?:key)?|session_?id|access_token|authorization|cookie|password)\b(\s*(?:=|:)\s*)(?:""[^""]*""|'[^']*'|[^\s&<>""']+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        static string RedactSecrets(string text) => SecretAssignment.Replace(text ?? "", "$1$2[redacted]");
        static string SafeText(string text, int maximum)
        {
            text = RedactSecrets(text ?? "");
            if (text.Length <= maximum) return text;
            if (char.IsHighSurrogate(text[maximum - 1])) maximum--;
            return text.Substring(0, maximum);
        }
        static McpApplicationException Error(string code, string message) => new McpApplicationException(code, message);
        static string Required(JsonElement args, string name, int maximum = 1000)
        { string value = Optional(args, name, "", maximum); if (string.IsNullOrWhiteSpace(value)) throw Error("invalid_arguments", name + " 不能为空。"); return value; }
        static string Optional(JsonElement args, string name, string fallback, int maximum = 1000)
        {
            if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
            if (value.ValueKind != JsonValueKind.String) throw Error("invalid_arguments", name + " 必须是字符串。");
            string result = value.GetString(); if (result.Length > maximum) throw Error("invalid_arguments", name + " 过长。"); return result;
        }
        static string Choice(JsonElement args, string name, string fallback, params string[] allowed)
        { string value = Optional(args, name, fallback); if (!allowed.Contains(value, StringComparer.Ordinal)) throw Error("invalid_arguments", name + " 可选值：" + string.Join(", ", allowed)); return value; }
        static int Integer(JsonElement args, string name, int fallback, int minimum, int maximum)
        {
            if (!args.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result) || result < minimum || result > maximum)
                throw Error("invalid_arguments", name + " 必须是 " + minimum + " 至 " + maximum + " 的整数。");
            return result;
        }
        static bool Boolean(JsonElement args, string name, bool fallback)
        {
            if (!args.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False) throw Error("invalid_arguments", name + " 必须是布尔值。");
            return value.GetBoolean();
        }
        static DateTime? Date(JsonElement args, string name)
        {
            string text = Optional(args, name, ""); if (text.Length == 0) return null;
            if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw Error("invalid_arguments", name + " 必须使用 yyyy-MM-dd 格式，日期边界包含当天。");
            return date;
        }
        static void EnsureFields(JsonElement args, params string[] names)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in args.EnumerateObject())
                if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw Error("invalid_arguments", "未知或重复参数：" + property.Name);
        }
    }

    // SELECT-only helpers share the repository lock but do not run repairs, migrations or session reads.
    public sealed partial class ArticleRepository
    {
        internal List<(string Id, string Name, int Count)> McpAccounts(CancellationToken token)
        {
            var result = new List<(string, string, int)>();
            lock (gate) using (var connection = Open()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT a.biz,a.name,(SELECT COUNT(*) FROM articles r WHERE r.biz=a.biz) FROM accounts a ORDER BY a.name,a.biz LIMIT 5001";
                using var cancel = token.Register(command.Cancel);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (result.Count == 5000) throw new McpApplicationException("query_too_large", "本地公众号超过 5000 个，暂不支持一次列出。");
                    result.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
                }
            }
            return result;
        }
        internal List<ArticleRecord> McpLoad(string account, int limit, CancellationToken token)
        {
            var result = new List<ArticleRecord>(); long payloadBytes = 0; string accountName;
            lock (gate) using (var connection = Open()) using (var command = connection.CreateCommand())
            {
                token.ThrowIfCancellationRequested();
                using (var exists = connection.CreateCommand())
                {
                    exists.CommandText = "SELECT name FROM accounts WHERE biz=@account"; exists.Parameters.AddWithValue("@account", account);
                    accountName = exists.ExecuteScalar() as string;
                    if (accountName == null) throw new McpApplicationException("account_not_found", "本地没有此公众号。");
                }
                command.CommandText = "SELECT payload FROM articles WHERE biz=@account ORDER BY id LIMIT @limit";
                command.Parameters.AddWithValue("@account", account); command.Parameters.AddWithValue("@limit", limit + 1);
                using var cancel = token.Register(command.Cancel); using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (result.Count == limit) throw new McpApplicationException("query_too_large", "本地公众号文章超过 25000 条，当前快照查询上限已达到。");
                    string json = reader.GetString(0); payloadBytes += json.Length * 2L;
                    if (payloadBytes > 128 * 1024 * 1024) throw new McpApplicationException("query_too_large", "本地元数据过大，当前查询内存上限已达到。");
                    var article = Newtonsoft.Json.JsonConvert.DeserializeObject<ArticleRecord>(json);
                    if (article != null && article.Biz == account)
                    {
                        if (string.IsNullOrWhiteSpace(article.AccountName)) article.AccountName = accountName;
                        result.Add(article);
                    }
                }
            }
            return result;
        }
        internal ArticleRecord McpFind(string account, string id, CancellationToken token)
        {
            lock (gate) using (var connection = Open()) using (var command = connection.CreateCommand())
            {
                token.ThrowIfCancellationRequested(); command.CommandText = "SELECT a.payload,COALESCE(b.name,'') FROM articles a LEFT JOIN accounts b ON b.biz=a.biz WHERE a.biz=@account AND a.id=@id";
                command.Parameters.AddWithValue("@account", account); command.Parameters.AddWithValue("@id", id);
                using var cancel = token.Register(command.Cancel);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;
                var article = Newtonsoft.Json.JsonConvert.DeserializeObject<ArticleRecord>(reader.GetString(0));
                if (article == null || article.Biz != account) return null;
                if (string.IsNullOrWhiteSpace(article.AccountName)) article.AccountName = reader.GetString(1);
                token.ThrowIfCancellationRequested(); return article;
            }
        }
        internal string McpLoadBody(string account, string id, CancellationToken token)
        {
            byte[] bytes;
            lock (gate) using (var connection = Open()) using (var command = connection.CreateCommand())
            {
                token.ThrowIfCancellationRequested();
                command.CommandText = "SELECT b.body FROM article_bodies b JOIN articles a ON a.id=b.id WHERE a.biz=@account AND a.id=@id";
                command.Parameters.AddWithValue("@account", account); command.Parameters.AddWithValue("@id", id);
                using var cancel = token.Register(command.Cancel); bytes = command.ExecuteScalar() as byte[];
            }
            if (bytes == null) return "";
            using var input = new MemoryStream(bytes); using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var text = new StreamReader(gzip, Encoding.UTF8); var output = new StringBuilder(); var buffer = new char[8192]; int count;
            while ((count = text.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (output.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("Local body exceeds query limit.");
                output.Append(buffer, 0, count);
            }
            return output.ToString();
        }
    }
}
