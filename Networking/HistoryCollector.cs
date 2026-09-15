using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public sealed class HistoryPage
    {
        public List<ArticleRecord> Articles { get; } = new List<ArticleRecord>();
        public int MessageCount { get; internal set; }
        public bool CanContinue { get; internal set; }
        public int NextOffset { get; internal set; }
        public int Offset { get; internal set; }
    }

    public sealed class CollectionPausedException : IOException
    {
        public CollectionPausedException(string message, Exception innerException = null) : base(message, innerException) { }
    }

    public sealed class HistoryCollector
    {
        private readonly IHttpTransport transport;
        private readonly Random random = new Random();
        public TimeSpan MinimumDelay { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan MaximumDelay { get; set; } = TimeSpan.FromSeconds(15);
        public int MaximumRequestAttempts { get; set; } = 3;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
        public HistoryCollector(IHttpTransport transport) { this.transport = transport ?? throw new ArgumentNullException(nameof(transport)); }

        // Read exactly the requested page. The controller owns durable cursor advancement and
        // must finish the pending articles from a saved page before asking for the next one.
        public async Task<HistoryPage> ReadPageAtAsync(AccountSession session, int offset,
            IProgress<CollectionProgress> progress, CancellationToken token)
        {
            ValidateRequestSettings(session);
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            token.ThrowIfCancellationRequested();
            var page = await ReadPageAsync(BuildPageUrl(session, offset), session, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            page.Offset = offset;
            if (page.CanContinue && page.NextOffset <= offset)
                throw new IOException("微信返回的下一页位置没有前进，采集已停止，不能确认已获取全部文章。");
            return page;
        }

        public async Task WaitForNextPageAsync(IProgress<CollectionProgress> progress, CancellationToken token)
        {
            if (MinimumDelay < TimeSpan.Zero || MaximumDelay < MinimumDelay) throw new InvalidOperationException("采集间隔设置无效。");
            token.ThrowIfCancellationRequested();
            double wait;
            lock (random) wait = MinimumDelay.TotalMilliseconds + random.NextDouble() * (MaximumDelay.TotalMilliseconds - MinimumDelay.TotalMilliseconds);
            progress?.Report(new CollectionProgress { Message = "等待下一页…" });
            await Task.Delay(TimeSpan.FromMilliseconds(wait), token).ConfigureAwait(false);
        }

        private void ValidateRequestSettings(AccountSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(session.Biz) || string.IsNullOrWhiteSpace(session.Cookie)
                || string.IsNullOrWhiteSpace(session.Uin) || string.IsNullOrWhiteSpace(session.Key))
                throw new InvalidOperationException("微信会话参数不完整。请开启监听，在电脑微信打开该公众号文章并刷新，再开始采集。");
            if (MaximumRequestAttempts < 1 || MaximumRequestAttempts > 5 || RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromMinutes(1))
                throw new InvalidOperationException("重试次数或间隔设置无效。");
        }

        public async Task CollectAsync(AccountSession session, Func<ArticleRecord, CancellationToken, Task> onArticle,
            IProgress<CollectionProgress> progress, CancellationToken token)
        {
            if (onArticle == null) throw new ArgumentNullException(nameof(onArticle));
            var identities = new HashSet<string>(StringComparer.Ordinal);
            await DiscoverAsync(session, async (page, cancellation) =>
            {
                foreach (var article in page.Articles)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (identities.Add(article.Id)) await onArticle(article, cancellation).ConfigureAwait(false);
                }
            }, progress, token).ConfigureAwait(false);
        }

        public async Task DiscoverAsync(AccountSession session, Func<HistoryPage, CancellationToken, Task> onPage,
            IProgress<CollectionProgress> progress, CancellationToken token)
        {
            ValidateRequestSettings(session);
            if (onPage == null) throw new ArgumentNullException(nameof(onPage));
            // The controller owns the per-run snapshot; keep this instance so Set-Cookie updates
            // from list requests are also available to enrichment callbacks for the same run.
            if (MinimumDelay < TimeSpan.Zero || MaximumDelay < MinimumDelay) throw new InvalidOperationException("采集间隔设置无效。");
            // A saved numeric offset is not a stable cursor when new messages arrive. Always
            // scan from the top and follow this run's server offsets; the store skips completed details.
            int offset = 0, pages = 0, count = 0;
            var offsets = new HashSet<int>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            string previousPage = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!offsets.Add(offset)) throw new IOException("微信返回了重复分页位置，采集已停止，不能确认已获取全部文章。");
                progress?.Report(new CollectionProgress { Pages = pages, Articles = count, Message = "正在读取第 " + (pages + 1) + " 页…" });
                var page = await ReadPageAtAsync(session, offset, progress, token).ConfigureAwait(false);
                string fingerprint = page.Articles.Count == 0 ? null : JsonConvert.SerializeObject(page.Articles.Select(article => article.Id));
                if (page.CanContinue && fingerprint != null && fingerprint == previousPage)
                    throw new CollectionPausedException("文章清单已暂停：分页位置虽在前进，但连续返回了相同的整页文章，尚未确认末页；已保存进度保留，请稍后刷新会话再试。");
                previousPage = fingerprint;
                pages++;
                foreach (var article in page.Articles)
                {
                    if (identities.Add(article.Id)) count++;
                }
                if (!page.CanContinue && count == 0)
                    throw new IOException("微信历史接口未返回可导出的文章，不能确认采集成功。请刷新公众号文章、更新会话后重试。");
                await onPage(page, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!page.CanContinue)
                {
                    progress?.Report(new CollectionProgress { Pages = pages, Articles = count, Message = "历史接口已返回末页，文章清单已保存，共 " + count + " 篇；正文补全进度另行统计。" });
                    return;
                }
                offset = page.NextOffset;
                progress?.Report(new CollectionProgress { Pages = pages, Articles = count, Message = "已读取 " + pages + " 页、" + count + " 篇，等待下一页…" });
                await WaitForNextPageAsync(null, token).ConfigureAwait(false);
            }
        }

        private async Task<HistoryPage> ReadPageAsync(string url, AccountSession session, IProgress<CollectionProgress> progress, CancellationToken token)
        {
            for (int attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                string json;
                try { json = await transport.GetStringAsync(url, session, token).ConfigureAwait(false); }
                catch (HttpRequestFailureException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden || (int)ex.StatusCode == 429)
                {
                    throw new CollectionPausedException("文章清单已暂停：微信拒绝请求或限制访问（HTTP " + (int)ex.StatusCode + "）。请稍后刷新公众号文章更新会话，再开始收集；已保存进度保留。", ex);
                }
                catch (Exception ex) when (IsTransientRequestFailure(ex))
                {
                    token.ThrowIfCancellationRequested();
                    if (attempt >= MaximumRequestAttempts)
                        throw new CollectionPausedException("文章清单已暂停：网络请求连续失败 " + attempt + " 次，尚未确认末页；已保存进度保留，请检查网络后重新开始。", ex);
                    progress?.Report(new CollectionProgress { Message = "文章清单请求失败，准备第 " + (attempt + 1) + "/" + MaximumRequestAttempts + " 次尝试。" });
                    await Task.Delay(RetryDelay, token).ConfigureAwait(false);
                    continue;
                }
                token.ThrowIfCancellationRequested();
                return ParsePage(json, session); // Invalid data and explicit server errors are not blindly retried.
            }
        }

        private static bool IsTransientRequestFailure(Exception exception)
        {
            if (exception is HttpRequestFailureException failure) return (int)failure.StatusCode >= 500 || (int)failure.StatusCode == 408;
            return exception is TimeoutException || exception is IOException || exception is WebException;
        }

        public static string BuildPageUrl(AccountSession session, int offset)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            return "https://mp.weixin.qq.com/mp/profile_ext?action=getmsg&__biz=" + Escape(session.Biz)
                + "&offset=" + offset.ToString(CultureInfo.InvariantCulture)
                + "&count=10&is_ok=1&scene=124&uin=" + Escape(session.Uin) + "&key=" + Escape(session.Key)
                + (string.IsNullOrWhiteSpace(session.PassTicket) ? "" : "&pass_ticket=" + Escape(session.PassTicket))
                + "&wxtoken=&x5=0&f=json";
        }

        public static HistoryPage ParsePage(string json, AccountSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(json)) throw new IOException("微信返回空响应，请刷新文章以更新会话。");
            if (json.TrimStart().StartsWith("<", StringComparison.Ordinal)) throw new CollectionPausedException("文章清单已暂停：微信返回了网页而非文章列表，登录状态可能已失效，请重新打开文章并刷新；已保存进度保留。");
            JObject root;
            try { root = JObject.Parse(json); }
            catch (JsonException ex) { throw new IOException("微信文章列表不是有效 JSON。", ex); }
            JToken returnCode = root["ret"] ?? (root["base_resp"] as JObject)?["ret"];
            int? code = ReadInt(returnCode);
            if (returnCode != null && !code.HasValue) throw new IOException("微信历史接口返回了无效的状态码，已停止采集。");
            if (code.HasValue && code.Value != 0)
                throw new CollectionPausedException("文章清单已暂停：微信历史接口返回错误（ret=" + code.Value + "），可能是会话失效或访问限制。请稍后重试或刷新文章以更新会话；尚未确认末页，已保存进度保留。");
            int? canContinue = ReadInt(root["can_msg_continue"]);
            if (canContinue != 0 && canContinue != 1) throw new IOException("微信响应缺少有效的分页结束标记，已停止采集。");
            int? next = ReadInt(root["next_offset"]);
            if (canContinue == 1 && (!next.HasValue || next.Value < 0)) throw new IOException("微信响应缺少下一页位置，已停止采集。");
            JToken messages = root["general_msg_list"];
            try
            {
                if (messages?.Type == JTokenType.String)
                {
                    var text = messages.Value<string>();
                    if (string.IsNullOrWhiteSpace(text)) throw new IOException("微信返回了空的历史列表字段，请更新会话后重试。");
                    messages = JObject.Parse(text);
                }
            }
            catch (JsonException ex) { throw new IOException("微信历史列表内容无法解析。", ex); }
            var list = (messages as JObject)?["list"] as JArray;
            if (list == null) throw new IOException("微信响应缺少文章列表字段，不能确认采集成功。");
            var result = new HistoryPage { CanContinue = canContinue == 1, NextOffset = next ?? 0, MessageCount = list.Count };
            foreach (var message in list)
            {
                if (!(message is JObject)) throw new IOException("微信文章列表包含无效记录，不能确认采集完整。");
                var info = message["comm_msg_info"] as JObject;
                var main = message["app_msg_ext_info"] as JObject;
                if (main == null) continue; // Non-article WeChat messages have no article body or URL.
                AddArticle(result, main, info, session, 1);
                var children = main["multi_app_msg_item_list"] as JArray;
                if (children != null)
                {
                    int index = 2;
                    foreach (var child in children)
                    {
                        if (!(child is JObject item)) throw new IOException("多图文列表包含无效记录。");
                        AddArticle(result, item, info, session, index++);
                    }
                }
            }
            return result;
        }

        private static void AddArticle(HistoryPage page, JObject item, JObject common, AccountSession session, int fallbackIndex)
        {
            string url = HttpUtility.HtmlDecode((string)item["content_url"] ?? "").Trim();
            Uri uri;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(new Uri("https://mp.weixin.qq.com/"), url, out uri)) url = uri.AbsoluteUri;
            else uri = null;
            var query = HttpUtility.ParseQueryString(uri?.Query ?? "");
            string biz = FirstIdentifier(query["__biz"], session.Biz);
            string mid = FirstIdentifier(query["mid"], query["appmsgid"], (string)item["mid"]);
            string sourceMessageId = ((string)common?["id"] ?? "").Trim();
            int idx;
            if (!int.TryParse(query["idx"], out idx) || idx <= 0) idx = ReadInt(item["idx"]) ?? fallbackIndex;
            if (idx <= 0) idx = fallbackIndex;
            bool deleted = ReadInt(item["is_deleted"]) == 1 || item["is_deleted"]?.Type == JTokenType.Boolean && item["is_deleted"].Value<bool>();
            if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(mid) && string.IsNullOrWhiteSpace(sourceMessageId))
                throw new IOException("一篇文章缺少链接和文章标识，不能保证无遗漏，已停止采集。");
            var record = new ArticleRecord
            {
                Biz = biz ?? "", Mid = mid, Idx = idx, Url = url,
                SourceMessageId = sourceMessageId, IdentityUnresolved = string.IsNullOrEmpty(mid),
                HistoryItemShowType = ReadInt(item["item_show_type"]), HistoryMessageType = ReadInt(common?["type"]),
                AccountName = session.Name, Title = HttpUtility.HtmlDecode((string)item["title"] ?? ""),
                Author = HttpUtility.HtmlDecode((string)item["author"] ?? ""), Digest = HttpUtility.HtmlDecode((string)item["digest"] ?? ""),
                Status = deleted ? ArticleStatus.Deleted : string.IsNullOrEmpty(url) ? ArticleStatus.Restricted : ArticleStatus.Pending,
                StatusDetail = deleted ? "历史接口标记为已删除。" : string.IsNullOrEmpty(url) ? "历史接口未提供文章链接。" : ""
            };
            long timestamp;
            if (long.TryParse((string)common?["datetime"] ?? (string)item["create_time"], out timestamp) && timestamp > 0)
                try { record.PublishedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToOffset(TimeSpan.FromHours(8)).DateTime; } catch (ArgumentOutOfRangeException) { }
            ArticleMetadata.ApplyHistory(record, (string)item["title"] ?? "", record.PublishedAt);
            record.Id = ArticleIdentity.Create(record.Biz, record.Mid, record.Idx, record.Url, sourceMessageId);
            page.Articles.Add(record);
        }

        private static int? ReadInt(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            if (value.Type == JTokenType.Boolean) return value.Value<bool>() ? 1 : 0;
            int number;
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : (int?)null;
        }
        private static string FirstIdentifier(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
        private static string Escape(string value) => Uri.EscapeDataString(value ?? "");
    }
}
