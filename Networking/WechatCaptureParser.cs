using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;

namespace WCAE
{
    public enum WechatCaptureReason
    {
        InvalidUrl, UnsupportedHost, UnsupportedPath, SpeculativeRequest, UnsupportedMethod,
        UnsupportedStatus, BodyTooLarge, MissingBiz, InvalidBiz, ConflictingBiz, AmbiguousKnownSession,
        EmptyDocumentBody, RefererOnlyUncertain, BackgroundSessionUpdate,
        DocumentNavigation, LegacyDocumentCandidate, CachedDocumentNavigation,
        CachedLegacyDocumentCandidate, VisibleDocumentObserved, ParseLimitExceeded
    }

    public enum WechatNavigationEvidence { None, Background, ExplicitDocument, LegacyDocumentCandidate, VisibleDocument }

    public sealed class WechatCaptureInput
    {
        public string RequestUrl { get; set; } = "";
        public string RequestMethod { get; set; } = "GET";
        public int StatusCode { get; set; } = 200;
        public string ResponseContentType { get; set; } = "";
        public string RequestBody { get; set; } = "";
        public string ResponseBody { get; set; } = "";
        public IDictionary<string, string> RequestHeaders { get; set; }
    }

    public sealed class WechatCaptureResult
    {
        public AccountSession Session { get; internal set; }
        public bool CanSelectAccount { get; internal set; }
        public WechatCaptureReason Reason { get; internal set; }
        public WechatNavigationEvidence NavigationEvidence { get; internal set; }
        // Diagnostics deliberately contain no URLs, titles, identifiers, cookies or session tokens.
        public string Diagnostic => WechatCaptureParser.Describe(Reason);
        public override string ToString() => Reason + ": " + Diagnostic;
    }

    /// <summary>
    /// Pure parser: no network, process, file or certificate access. CanSelectAccount means an
    /// eligible document observation, not proof of the foreground tab. Even Sec-Fetch-Mode:
    /// navigate cannot prove foreground focus; old clients without Fetch Metadata are explicitly
    /// reported as legacy candidates. Callers must not select an account for session-only results.
    /// </summary>
    public static class WechatCaptureParser
    {
        private const int MaximumBodyCharacters = 16 * 1024 * 1024;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
        private static readonly HashSet<string> SessionPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "/mp/getappmsgext", "/mp/appmsg_comment", "/mp/appmsg_reply", "/mp/getappmsgad"
        };

        public static bool IsWechatUri(Uri uri)
            => uri != null && uri.IsAbsoluteUri && (uri.Scheme == "https" || uri.Scheme == "http")
                && string.Equals(uri.Host, "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(uri.UserInfo);

        public static bool IsDocumentUri(Uri uri)
            => IsWechatUri(uri) && (uri.AbsolutePath == "/s" || uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal)
                || uri.AbsolutePath == "/mp/profile_ext" || uri.AbsolutePath == "/mp/profile"
                || uri.AbsolutePath == "/mp/appmsgalbum");

        public static WechatCaptureResult Parse(WechatCaptureInput input, IEnumerable<AccountSession> knownSessions = null)
        {
            if (input == null) return Result(WechatCaptureReason.InvalidUrl);
            try { return ParseCore(input, knownSessions); }
            catch (RegexMatchTimeoutException) { return Result(WechatCaptureReason.ParseLimitExceeded); }
            catch (ArgumentException) { return Result(WechatCaptureReason.InvalidUrl); }
        }

        public static WechatCaptureResult ParseVisibleDocument(string url, IEnumerable<AccountSession> knownSessions = null)
        {
            if (!TryUri(url, out var uri) || !IsDocumentUri(uri) || !uri.IsDefaultPort)
                return Result(WechatCaptureReason.UnsupportedPath);
            // Reuse the no-body URL/known-document association path. No HTTP response occurred;
            // the returned evidence explicitly identifies a visible accessibility document.
            var result = Parse(new WechatCaptureInput { RequestUrl = url, StatusCode = 304 }, knownSessions);
            if (result.CanSelectAccount)
            {
                result.Reason = WechatCaptureReason.VisibleDocumentObserved;
                result.NavigationEvidence = WechatNavigationEvidence.VisibleDocument;
            }
            return result;
        }

        private static WechatCaptureResult ParseCore(WechatCaptureInput input, IEnumerable<AccountSession> knownSessions)
        {
            Uri uri;
            if (!TryUri(input.RequestUrl, out uri)) return Result(WechatCaptureReason.InvalidUrl);
            if (!IsWechatUri(uri)) return Result(WechatCaptureReason.UnsupportedHost);
            var headers = CopyHeaders(input.RequestHeaders);
            if (IsSpeculative(headers)) return Result(WechatCaptureReason.SpeculativeRequest);
            bool documentPath = IsDocumentUri(uri);
            if (!documentPath && !SessionPaths.Contains(uri.AbsolutePath)) return Result(WechatCaptureReason.UnsupportedPath);
            string method = (input.RequestMethod ?? "GET").Trim().ToUpperInvariant();
            if (method != "GET" && method != "POST") return Result(WechatCaptureReason.UnsupportedMethod);
            if (input.StatusCode != 200 && input.StatusCode != 304) return Result(WechatCaptureReason.UnsupportedStatus);
            if ((input.ResponseBody?.Length ?? 0) > MaximumBodyCharacters || (input.RequestBody?.Length ?? 0) > MaximumBodyCharacters)
                return Result(WechatCaptureReason.BodyTooLarge);

            var query = HttpUtility.ParseQueryString(uri.Query);
            string responseBody = input.StatusCode == 304 ? "" : input.ResponseBody ?? "";
            var form = ReadForm(input.RequestBody, Header(headers, "Content-Type"));
            string requestJson = Header(headers, "Content-Type").IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 ? input.RequestBody ?? "" : "";
            bool background = !documentPath || IsBackground(input, headers, query, method);
            WechatNavigationEvidence evidence = background ? WechatNavigationEvidence.Background
                : HasNavigationMetadata(headers) ? WechatNavigationEvidence.ExplicitDocument : WechatNavigationEvidence.LegacyDocumentCandidate;
            var known = (knownSessions ?? Enumerable.Empty<AccountSession>()).Where(s => s != null && ValidBiz(s.Biz)).Take(1000).ToArray();

            string queryBiz = First(query["__biz"], query["biz"]);
            string formBiz = First(form["__biz"], form["biz"], ReadValue(requestJson, "__biz", "biz"));
            string bodyBiz = ReadValue(responseBody, "biz", "__biz");
            if (Disagree(queryBiz, formBiz) || Disagree(First(queryBiz, formBiz), bodyBiz))
                return Result(WechatCaptureReason.ConflictingBiz, evidence);
            string biz = First(queryBiz, formBiz, bodyBiz);
            bool refererOnly = false;

            Uri referer;
            bool trustedReferer = TryUri(Header(headers, "Referer"), out referer) && IsDocumentUri(referer) && referer.IsDefaultPort;
            var refererQuery = trustedReferer ? HttpUtility.ParseQueryString(referer.Query) : new NameValueCollection();
            var urlMatches = known.Where(s => SameDocument(s.RequestUrl, uri)).ToArray();
            if (string.IsNullOrEmpty(biz) && urlMatches.Length > 0)
            {
                var matchedBiz = urlMatches.Select(s => s.Biz).Distinct(StringComparer.Ordinal).ToArray();
                if (matchedBiz.Length != 1) return Result(WechatCaptureReason.AmbiguousKnownSession, evidence);
                biz = matchedBiz[0];
            }
            string refererBiz = First(refererQuery["__biz"], refererQuery["biz"]);
            if (string.IsNullOrEmpty(refererBiz) && trustedReferer)
            {
                var refBiz = known.Where(s => SameDocument(s.RequestUrl, referer)).Select(s => s.Biz).Distinct(StringComparer.Ordinal).ToArray();
                if (refBiz.Length == 1) refererBiz = refBiz[0];
            }
            if (string.IsNullOrEmpty(biz) && !string.IsNullOrEmpty(refererBiz))
            {
                biz = refererBiz;
                // A referer names the source page, not necessarily the destination account.
                refererOnly = !background && !SameDocument(referer.AbsoluteUri, uri);
            }
            if (string.IsNullOrEmpty(biz)) return Result(WechatCaptureReason.MissingBiz, evidence);
            if (!ValidBiz(biz)) return Result(WechatCaptureReason.InvalidBiz, evidence);
            // Do not attribute destination credentials to the account of a different source page.
            if (refererOnly) return Result(WechatCaptureReason.RefererOnlyUncertain, evidence);
            var previous = known.Where(s => s.Biz == biz).OrderByDescending(s => s.CapturedAt).FirstOrDefault();
            bool sameRefererAccount = trustedReferer && refererBiz == biz;
            if (!sameRefererAccount) refererQuery = new NameValueCollection();

            var session = previous?.Clone() ?? new AccountSession();
            session.Biz = biz;
            // A nickname-shaped key in a component, comment, URL or request is not the account's display name.
            // Use the document's visible name / explicit page declarations, or a validated name for this same Biz.
            session.Name = AccountNameResolver.Choose(biz, background ? "" : AccountNameResolver.Extract(responseBody, biz), previous?.Name);
            session.UserName = First(ReadValue(responseBody, "user_name", "username"), query["user_name"], form["user_name"],
                ReadValue(requestJson, "user_name", "username"), refererQuery["user_name"], session.UserName);
            // Comment/reply JSON can contain the commenter's nickname and username. These
            // fields are not the public account's identity and must never rename the account.
            if (background) { session.Name = AccountNameResolver.Normalize(previous?.Name, biz); session.UserName = previous?.UserName ?? ""; }
            session.Uin = Field("uin", query, form, requestJson, responseBody, refererQuery, session.Uin);
            session.Key = Field("key", query, form, requestJson, responseBody, refererQuery, session.Key);
            session.PassTicket = Field("pass_ticket", query, form, requestJson, responseBody, refererQuery, session.PassTicket);
            session.AppMsgToken = Field("appmsg_token", query, form, requestJson, responseBody, refererQuery, session.AppMsgToken);
            session.Cookie = First(Header(headers, "Cookie"), session.Cookie);
            session.UserAgent = First(Header(headers, "User-Agent"), session.UserAgent, "Mozilla/5.0");
            // Session refreshes must preserve the known document URL used for history collection.
            if (!background && !refererOnly) session.RequestUrl = uri.AbsoluteUri;
            else if (string.IsNullOrEmpty(session.RequestUrl)) session.RequestUrl = sameRefererAccount ? referer.AbsoluteUri : uri.AbsoluteUri;
            session.Headers = CopyHeaders(session.Headers);
            foreach (var pair in headers) session.Headers[pair.Key] = pair.Value;
            session.CapturedAt = DateTime.Now;

            if (background) return Result(WechatCaptureReason.BackgroundSessionUpdate, evidence, session);
            if (refererOnly) return Result(WechatCaptureReason.RefererOnlyUncertain, evidence, session);
            if (input.StatusCode != 304 && string.IsNullOrWhiteSpace(responseBody))
                return Result(WechatCaptureReason.EmptyDocumentBody, evidence, session);
            var reason = input.StatusCode == 304
                ? evidence == WechatNavigationEvidence.ExplicitDocument ? WechatCaptureReason.CachedDocumentNavigation : WechatCaptureReason.CachedLegacyDocumentCandidate
                : evidence == WechatNavigationEvidence.ExplicitDocument ? WechatCaptureReason.DocumentNavigation : WechatCaptureReason.LegacyDocumentCandidate;
            return Result(reason, evidence, session, true);
        }

        private static bool IsBackground(WechatCaptureInput input, IDictionary<string, string> headers, NameValueCollection query, string method)
        {
            if (method != "GET") return true;
            string mode = Header(headers, "Sec-Fetch-Mode"), dest = Header(headers, "Sec-Fetch-Dest");
            if (mode.Length > 0 && !mode.Equals("navigate", StringComparison.OrdinalIgnoreCase)) return true;
            if (dest.Length > 0 && !dest.Equals("document", StringComparison.OrdinalIgnoreCase)) return true;
            if (Header(headers, "X-Requested-With").Equals("XMLHttpRequest", StringComparison.OrdinalIgnoreCase)) return true;
            string action = query["action"] ?? "";
            if (action.Equals("getmsg", StringComparison.OrdinalIgnoreCase) || string.Equals(query["f"], "json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(query["format"], "json", StringComparison.OrdinalIgnoreCase) || query["ajax"] == "1") return true;
            string contentType = input.ResponseContentType ?? "";
            if (contentType.Length > 0 && contentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) < 0
                && contentType.IndexOf("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) < 0) return true;
            string body = (input.ResponseBody ?? "").TrimStart();
            return body.StartsWith("{", StringComparison.Ordinal) || body.StartsWith("[", StringComparison.Ordinal);
        }

        private static bool HasNavigationMetadata(IDictionary<string, string> headers)
            => Header(headers, "Sec-Fetch-Mode").Equals("navigate", StringComparison.OrdinalIgnoreCase)
                || Header(headers, "Sec-Fetch-Dest").Equals("document", StringComparison.OrdinalIgnoreCase);

        private static bool IsSpeculative(IDictionary<string, string> headers)
        {
            foreach (string key in new[] { "Purpose", "Sec-Purpose", "X-Purpose", "X-Moz", "Sec-Fetch-Purpose" })
            {
                string value = Header(headers, key);
                if (Regex.IsMatch(value, @"(?:^|[^a-z])(?:prefetch|prerender)(?:$|[^a-z])", RegexOptions.IgnoreCase, RegexTimeout)) return true;
            }
            return false;
        }

        private static bool SameDocument(string previousUrl, Uri current)
        {
            Uri previous;
            if (!TryUri(previousUrl, out previous) || !IsDocumentUri(previous) || !IsDocumentUri(current)
                || !string.Equals(previous.Host, current.Host, StringComparison.OrdinalIgnoreCase)
                || previous.AbsolutePath != current.AbsolutePath) return false;
            var oldQuery = HttpUtility.ParseQueryString(previous.Query);
            var newQuery = HttpUtility.ParseQueryString(current.Query);
            if (current.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal) && current.AbsolutePath.Length > 3)
            {
                // Short-link path identifies an article, but an explicit differing biz still wins.
                return !Disagree(First(oldQuery["__biz"], oldQuery["biz"]), First(newQuery["__biz"], newQuery["biz"]));
            }
            string oldBiz = First(oldQuery["__biz"], oldQuery["biz"]), newBiz = First(newQuery["__biz"], newQuery["biz"]);
            if (oldBiz.Length == 0 || newBiz.Length == 0 || oldBiz != newBiz) return false;
            if (current.AbsolutePath == "/s") return First(oldQuery["mid"], oldQuery["appmsgid"]) == First(newQuery["mid"], newQuery["appmsgid"])
                && First(oldQuery["idx"], "1") == First(newQuery["idx"], "1");
            return current.AbsolutePath != "/mp/appmsgalbum" || oldQuery["album_id"] == newQuery["album_id"];
        }

        private static NameValueCollection ReadForm(string body, string contentType)
        {
            if (string.IsNullOrWhiteSpace(body)) return new NameValueCollection();
            if (contentType.IndexOf("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0)
                return HttpUtility.ParseQueryString(body);
            return new NameValueCollection();
        }

        private static string Field(string name, NameValueCollection query, NameValueCollection form, string requestJson,
            string body, NameValueCollection referer, string previous)
            => First(query[name], form[name], ReadValue(requestJson, name), ReadValue(body, name), referer[name], previous);

        private static string ReadValue(string text, params string[] names)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (string name in names)
            {
                string pattern = @"(?<![\w$])['""]?" + Regex.Escape(name) + @"['""]?\s*(?::|=)\s*(?:['""]['""]\s*\|\|\s*)?(?:htmlDecode\s*\(\s*)?(?<q>['""])(?<v>(?:\\.|(?!\k<q>).)*?)\k<q>";
                foreach (Match match in Regex.Matches(text, pattern, RegexOptions.Singleline, RegexTimeout))
                {
                    string value = DecodeScript(match.Groups["v"].Value);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                // uin is often emitted as a numeric JSON/JavaScript value.
                if (name == "uin")
                {
                    var numeric = Regex.Match(text, @"(?<![\w$])['""]?uin['""]?\s*(?::|=)\s*(?<v>[0-9]+)(?![\w.])", RegexOptions.None, RegexTimeout);
                    if (numeric.Success) return numeric.Groups["v"].Value;
                }
            }
            return "";
        }

        private static string DecodeScript(string value)
        {
            value = Regex.Replace(value, @"\\(?:u(?<u>[0-9a-fA-F]{4})|x(?<x>[0-9a-fA-F]{2})|(?<c>[\\/""'bfnrt]))", m =>
            {
                if (m.Groups["u"].Success) return ((char)Convert.ToInt32(m.Groups["u"].Value, 16)).ToString();
                if (m.Groups["x"].Success) return ((char)Convert.ToInt32(m.Groups["x"].Value, 16)).ToString();
                switch (m.Groups["c"].Value)
                {
                    case "b": return "\b"; case "f": return "\f"; case "n": return "\n"; case "r": return "\r"; case "t": return "\t";
                    default: return m.Groups["c"].Value;
                }
            }, RegexOptions.None, RegexTimeout);
            return HttpUtility.HtmlDecode(value);
        }

        private static bool TryUri(string value, out Uri uri) => Uri.TryCreate(HttpUtility.HtmlDecode(value ?? ""), UriKind.Absolute, out uri);
        private static bool Disagree(string a, string b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && a != b;
        private static bool ValidBiz(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
        private static string First(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        private static string Header(IDictionary<string, string> headers, string key)
        {
            string value;
            return headers != null && headers.TryGetValue(key, out value) ? value ?? "" : "";
        }
        private static Dictionary<string, string> CopyHeaders(IDictionary<string, string> headers)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers != null) foreach (var entry in headers) if (!string.IsNullOrEmpty(entry.Key)) result[entry.Key] = entry.Value ?? "";
            return result;
        }
        private static WechatCaptureResult Result(WechatCaptureReason reason, WechatNavigationEvidence evidence = WechatNavigationEvidence.None,
            AccountSession session = null, bool canSelect = false)
            => new WechatCaptureResult { Reason = reason, NavigationEvidence = evidence, Session = session, CanSelectAccount = canSelect };

        internal static string Describe(WechatCaptureReason reason)
        {
            switch (reason)
            {
                case WechatCaptureReason.DocumentNavigation: return "已识别文档导航；网络信息不能证明当前前台标签页。";
                case WechatCaptureReason.LegacyDocumentCandidate: return "已识别兼容页面；缺少导航头，不能证明前台状态。";
                case WechatCaptureReason.CachedDocumentNavigation: return "304 缓存文档已关联公众号；不能证明前台状态。";
                case WechatCaptureReason.CachedLegacyDocumentCandidate: return "304 缓存页面已关联公众号；缺少导航头。";
                case WechatCaptureReason.VisibleDocumentObserved: return "已从微信显示的文档地址识别公众号；本次没有收到新的正文响应。";
                case WechatCaptureReason.BackgroundSessionUpdate: return "后台请求只补充会话，不切换当前公众号。";
                case WechatCaptureReason.RefererOnlyUncertain: return "仅能识别来源页公众号，不能确认目标页面归属。";
                case WechatCaptureReason.SpeculativeRequest: return "已忽略明确的预取或预渲染请求。";
                case WechatCaptureReason.UnsupportedHost: return "请求主机不是受支持的微信网页主机。";
                case WechatCaptureReason.UnsupportedPath: return "路径不属于受支持的文档或会话接口。";
                case WechatCaptureReason.UnsupportedStatus: return "响应状态不是可识别的成功或缓存响应。";
                case WechatCaptureReason.UnsupportedMethod: return "请求方法不参与页面识别。";
                case WechatCaptureReason.MissingBiz: return "页面、请求和可靠关联均未提供公众号标识。";
                case WechatCaptureReason.InvalidBiz: return "公众号标识格式无效。";
                case WechatCaptureReason.ConflictingBiz: return "请求和正文的公众号标识冲突，未切换公众号。";
                case WechatCaptureReason.AmbiguousKnownSession: return "同一页面存在多个会话归属，无法可靠关联。";
                case WechatCaptureReason.EmptyDocumentBody: return "成功响应没有正文，未确认页面完成加载。";
                case WechatCaptureReason.BodyTooLarge: return "正文超过解析上限。";
                case WechatCaptureReason.ParseLimitExceeded: return "页面解析超出时间限制。";
                default: return "请求地址无效。";
            }
        }
    }
}
