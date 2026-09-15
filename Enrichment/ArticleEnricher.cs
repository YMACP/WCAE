using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public sealed class ArticleEnricher
    {
        private readonly IHttpTransport transport;
        public ArticleEnricher(IHttpTransport transport)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        // No I/O and no script execution. Standard articles retain their original HTML;
        // recognized share posts receive a small document with #wcae_body.
        public static ArticleRecord ParseHtml(ArticleRecord article, string html)
        {
            if (article == null) throw new ArgumentNullException(nameof(article));
            return ArticleHtmlParser.Parse(article, html ?? "");
        }

        public async Task<ArticleRecord> EnrichAsync(ArticleRecord article, AccountSession session, CancellationToken token)
        {
            if (article == null) throw new ArgumentNullException(nameof(article));
            token.ThrowIfCancellationRequested();
            AccountSession requestSession;
            if (session == null) requestSession = new AccountSession();
            else lock (session) requestSession = session.Clone();
            string originalCookie = requestSession.Cookie;
            if (requestSession.Headers == null) requestSession.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var url = ArticleHtmlParser.NormalizeUrl(article.Url, "https://mp.weixin.qq.com/");
            if (url.Length == 0)
            {
                Fail(article, ArticleStatus.Failed, "文章链接无效");
                return article;
            }
            requestSession.Headers["Referer"] = url;
            try
            {
                var html = await transport.GetStringAsync(url, requestSession, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ParseHtml(article, html);
                if (article.Status == ArticleStatus.Failed && new Uri(url).AbsolutePath.StartsWith("/s", StringComparison.Ordinal))
                {
                    var builder = new UriBuilder(url); var query = HttpUtility.ParseQueryString(builder.Query); query["f"] = "json"; builder.Query = query.ToString();
                    var json = await transport.GetStringAsync(builder.Uri.AbsoluteUri, requestSession, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (ArticleHtmlParser.ParseJson(json) is JObject) { ParseHtml(article, json); html = json; }
                }
                if (article.Status != ArticleStatus.Available) return article;
                token.ThrowIfCancellationRequested();
                await ResolveWeChatVideosAsync(article, html, requestSession, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await EnrichMetricsAsync(article, html, requestSession, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ArticleHtmlParser.UpdateMetricNote(article);
                return article;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Fail(article, ArticleStatus.Failed, "文章请求超时"); return article;
            }
            catch (OperationCanceledException) { throw; }
            catch (CollectionPausedException) { throw; }
            catch (HttpRequestFailureException ex)
            {
                token.ThrowIfCancellationRequested();
                FailHttp(article, ex.StatusCode); return article;
            }
            catch (WebException ex)
            {
                token.ThrowIfCancellationRequested();
                var response = ex.Response as HttpWebResponse;
                if (response != null) FailHttp(article, response.StatusCode);
                else Fail(article, ArticleStatus.Failed, "文章网络请求失败（" + ex.Status + "）");
                return article;
            }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                // Exception messages can contain credential-bearing URLs. Never persist them.
                Fail(article, ArticleStatus.Failed, "文章获取或解析失败（" + ex.GetType().Name + "）");
                return article;
            }
            finally
            {
                article.UpdatedAt = DateTime.Now;
                // Keep response cookie rotations for the next article, without overwriting
                // a session that another request has refreshed since our snapshot.
                if (session != null && requestSession.Cookie != originalCookie)
                    lock (session)
                        if (session.Cookie == originalCookie)
                        {
                            session.Cookie = requestSession.Cookie;
                            session.CapturedAt = DateTime.Now;
                        }
            }
        }

        private async Task ResolveWeChatVideosAsync(ArticleRecord article, string html, AccountSession session, CancellationToken token)
        {
            var pending = article.Media.Where(m => m.Kind == MediaKind.Video && m.Url.Length == 0 &&
                System.Text.RegularExpressions.Regex.IsMatch(m.Id ?? "", @"^(?:wxv_)?\d{8,30}$")).ToList();
            foreach (var media in pending)
            {
                token.ThrowIfCancellationRequested();
                var cookie = ReadCookies(session.Cookie); var captured = Query(session.RequestUrl);
                var query = HttpUtility.ParseQueryString("");
                query["action"] = "get_mp_video_play_url"; query["vid"] = media.Id; query["preview"] = "0"; query["f"] = "json"; query["x5"] = "0";
                query["__biz"] = First(article.Biz, session.Biz); query["mid"] = article.Mid; query["idx"] = Math.Max(1, article.Idx).ToString(CultureInfo.InvariantCulture);
                query["uin"] = First(session.Uin, captured["uin"], Cookie(cookie, "wxuin")); query["key"] = First(session.Key, captured["key"]);
                query["pass_ticket"] = First(session.PassTicket, captured["pass_ticket"], Cookie(cookie, "pass_ticket"));
                query["appmsg_token"] = First(ArticleHtmlParser.JsValue(html, "appmsg_token"), session.AppMsgToken, Cookie(cookie, "appmsg_token"));
                query["wxtoken"] = First(captured["wxtoken"], Cookie(cookie, "wxtokenkey"), "777");
                try
                {
                    // Observed player endpoint, not a promise of access. Only response URLs are accepted;
                    // signed query strings are retained without inventing a media download URL.
                    string response = await transport.GetStringAsync("https://mp.weixin.qq.com/mp/videoplayer?" + query, session, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var json = ArticleHtmlParser.ParseJson(response) as JObject;
                    var urls = json == null ? null : json["url_info"] as JArray;
                    var best = urls == null ? null : urls.OfType<JObject>().Where(x => x["url"] != null)
                        .OrderByDescending(x => (ArticleHtmlParser.NonnegativeInteger(x["width"]) ?? 0) * (ArticleHtmlParser.NonnegativeInteger(x["height"]) ?? 0)).FirstOrDefault();
                    string actual = best == null ? "" : ArticleHtmlParser.NormalizeUrl(best["url"].ToString(), article.Url);
                    if (actual.Length > 0)
                    {
                        media.Url = actual; media.UnavailableReason = ""; media.RequiresExtractor = ArticleHtmlParser.IsManifest(actual);
                    }
                    else media.UnavailableReason = "微信视频播放接口未返回可用直链，可能需要有效会话或该视频不可访问";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { media.UnavailableReason = "微信视频地址解析超时"; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { token.ThrowIfCancellationRequested(); media.UnavailableReason = "微信视频地址解析失败（" + ex.GetType().Name + "）"; }
            }
        }

        private async Task EnrichMetricsAsync(ArticleRecord article, string html, AccountSession session, CancellationToken token)
        {
            if (article.ReadCount.HasValue && article.LikeCount.HasValue && article.FavoriteCount.HasValue && article.ShareCount.HasValue) return;
            var cookie = ReadCookies(session.Cookie);
            var capturedQuery = Query(session.RequestUrl);
            var articleQuery = Query(article.Url);
            string biz = First(article.Biz, articleQuery["__biz"], session.Biz, capturedQuery["__biz"]);
            string mid = First(article.Mid, articleQuery["mid"], articleQuery["appmsgid"]);
            string sn = First(articleQuery["sn"], ArticleHtmlParser.JsValue(html, "sn"));
            string uin = First(session.Uin, capturedQuery["uin"], Cookie(cookie, "wxuin"));
            string key = First(session.Key, capturedQuery["key"]);
            if (new[] { biz, mid, sn, uin, key }.Any(string.IsNullOrWhiteSpace))
            {
                ArticleHtmlParser.AddNote(article, "统计接口所需的文章或会话参数不完整");
                return;
            }
            string appToken = First(ArticleHtmlParser.JsValue(html, "appmsg_token"), session.AppMsgToken, capturedQuery["appmsg_token"], Cookie(cookie, "appmsg_token"));
            var form = new Dictionary<string, string>
            {
                ["__biz"] = biz, ["mid"] = mid, ["sn"] = sn,
                ["idx"] = (article.Idx > 0 ? article.Idx : 1).ToString(CultureInfo.InvariantCulture),
                ["uin"] = uin, ["key"] = key, ["appmsg_type"] = "9", ["is_only_read"] = "1", ["f"] = "json",
                ["wxtoken"] = First(capturedQuery["wxtoken"], Cookie(cookie, "wxtokenkey"), "777")
            };
            AddIfPresent(form, "pass_ticket", First(session.PassTicket, capturedQuery["pass_ticket"], Cookie(cookie, "pass_ticket")));
            AddIfPresent(form, "appmsg_token", appToken);
            AddIfPresent(form, "comment_id", ArticleHtmlParser.JsValue(html, "comment_id"));
            try
            {
                token.ThrowIfCancellationRequested();
                var body = await transport.PostFormAsync("https://mp.weixin.qq.com/mp/getappmsgext", form, session, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var json = ArticleHtmlParser.ParseJson(body) as JObject;
                var ret = json == null ? null : ArticleHtmlParser.Integer(json.SelectToken("base_resp.ret") ?? json["ret"]);
                if (json == null || (ret.HasValue && ret.Value != 0))
                {
                    ArticleHtmlParser.AddNote(article, "统计接口未返回有效数据" + (ret.HasValue ? "（ret=" + ret.Value.ToString(CultureInfo.InvariantCulture) + "）" : ""));
                    return;
                }
                var stats = json["appmsgstat"] as JObject;
                if (stats == null)
                {
                    ArticleHtmlParser.AddNote(article, "统计接口响应缺少 appmsgstat");
                    return;
                }
                ApplyStats(article, stats);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { ArticleHtmlParser.AddNote(article, "正文已获取，统计接口请求超时"); }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestFailureException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || (int)ex.StatusCode == 429)
            {
                token.ThrowIfCancellationRequested();
                throw new CollectionPausedException("正文已获取，统计接口要求更新会话或降低请求频率（HTTP " + (int)ex.StatusCode + "）；已暂停后续请求，请稍后刷新文章后继续。", ex);
            }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                // A metrics failure must not hide a successfully retrieved article.
                ArticleHtmlParser.AddNote(article, "正文已获取，统计补充失败（" + ex.GetType().Name + "）");
            }
        }

        internal static void ApplyStats(ArticleRecord article, JObject stats)
        {
            article.ReadCount = article.ReadCount ?? ArticleHtmlParser.NonnegativeInteger(stats["read_num"]);
            article.LikeCount = article.LikeCount ?? ArticleHtmlParser.NonnegativeInteger(stats["old_like_num"]);
            article.FavoriteCount = article.FavoriteCount ?? ArticleHtmlParser.NonnegativeInteger(stats["like_num"]);
            article.ShareCount = article.ShareCount ?? ArticleHtmlParser.NonnegativeInteger(stats["share_num"])
                ?? ArticleHtmlParser.NonnegativeInteger(stats["share_count"]) ?? ArticleHtmlParser.NonnegativeInteger(stats["rt_num"]);
        }

        private static void Fail(ArticleRecord article, ArticleStatus status, string detail)
        {
            article.Status = status; article.StatusDetail = detail; article.Html = "";
            article.Media = new List<MediaAsset>();
        }
        private static void FailHttp(ArticleRecord article, HttpStatusCode status)
        {
            if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden || (int)status == 429)
                Fail(article, ArticleStatus.Restricted, "文章请求被拒绝或受限（HTTP " + (int)status + "）");
            else if (status == HttpStatusCode.NotFound || status == HttpStatusCode.Gone)
                Fail(article, ArticleStatus.Deleted, "文章不存在或已移除（HTTP " + (int)status + "）");
            else Fail(article, ArticleStatus.Failed, "文章请求失败（HTTP " + (int)status + "）");
        }
        private static void AddIfPresent(IDictionary<string, string> form, string name, string value) { if (!string.IsNullOrEmpty(value)) form[name] = value; }
        private static string First(params string[] values) { return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? ""; }
        private static string Cookie(IDictionary<string, string> cookies, string name) { string value; return cookies.TryGetValue(name, out value) ? value : ""; }
        private static Dictionary<string, string> ReadCookies(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (text ?? "").Split(';')) { int at = part.IndexOf('='); if (at > 0) result[part.Substring(0, at).Trim()] = part.Substring(at + 1).Trim(); }
            return result;
        }
        private static System.Collections.Specialized.NameValueCollection Query(string url)
        {
            Uri uri;
            return HttpUtility.ParseQueryString(Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Query : "");
        }
    }
}
