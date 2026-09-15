using System;
using System.Collections.Generic;

namespace WCAE
{
    /// <summary>Synthetic parser fixtures only; never opens a listener or makes a WeChat request.</summary>
    public static class WechatCaptureParserSelfTests
    {
        public static void Run()
        {
            LegacyAndModernDocuments();
            TrustedAccountNames();
            CachedDocuments();
            BackgroundCannotSelect();
            SpeculationAndRefererBoundaries();
            DiagnosticsAndIsolation();
            var visible = WechatCaptureParser.ParseVisibleDocument("https://mp.weixin.qq.com/s?__biz=visible-biz&uin=12&key=fixture");
            Check(visible.CanSelectAccount && visible.Session.Biz == "visible-biz" && visible.Session.Key == "fixture"
                && visible.Reason == WechatCaptureReason.VisibleDocumentObserved && visible.NavigationEvidence == WechatNavigationEvidence.VisibleDocument,
                "visible cached document has its own evidence and URL credentials");
            Check(!visible.Diagnostic.Contains("304") && !visible.Diagnostic.Contains("fixture"), "visible document does not claim a network response or expose parameters");
            Check(!WechatCaptureParser.ParseVisibleDocument("https://mp.weixin.qq.com/mp/getappmsgext?__biz=visible-biz").CanSelectAccount,
                "background URL is not a visible account document");
            Check(!WechatCaptureParser.ParseVisibleDocument("https://mp.weixin.qq.com:8443/s?__biz=visible-biz").CanSelectAccount,
                "visible observation only trusts default ports");
        }

        private static void LegacyAndModernDocuments()
        {
            var legacy = Input("/s?__biz=fixture-biz&uin=12&key=K%2B1&pass_ticket=P%2F1", "var nickname=htmlDecode(\"甲&amp;乙\");var user_name='gh_test';");
            legacy.RequestHeaders = Headers("cookie", "fixture_cookie=local", "user-agent", "OfflineSelfTest");
            var parsed = WechatCaptureParser.Parse(legacy);
            Check(parsed.CanSelectAccount && parsed.Reason == WechatCaptureReason.LegacyDocumentCandidate && parsed.Session.Biz == "fixture-biz", "legacy document remains eligible with explicit uncertainty");
            Check(parsed.Session.Name == "甲&乙" && parsed.Session.UserName == "gh_test" && parsed.Session.Key == "K+1"
                && parsed.Session.PassTicket == "P/1" && parsed.Session.Cookie == "fixture_cookie=local", "legacy escapes and case-insensitive headers");

            var modern = Input("/s/short-link", "var biz='' || '\\x73hort-biz';window.cgiDataNew={nick_name:'\\u65b0版\\'名称',uin:123456,user_name:'gh_new'};");
            modern.RequestHeaders = Navigation();
            parsed = WechatCaptureParser.Parse(modern);
            Check(parsed.CanSelectAccount && parsed.Reason == WechatCaptureReason.DocumentNavigation && parsed.Session.Biz == "short-biz"
                && parsed.Session.Name == "新版'名称" && parsed.Session.Uin == "123456", "short links and numeric uin");
            var quoted = WechatCaptureParser.Parse(Input("/s/quoted", "window.cgiDataNew={\"biz\":\"quoted-biz\",\"nick_name\":\"JSON名称\"};"));
            Check(quoted.CanSelectAccount && quoted.Session.Name == "JSON名称", "quoted JavaScript object properties");
            foreach (string path in new[] { "/mp/profile_ext?action=home", "/mp/profile", "/mp/appmsgalbum?album_id=42&action=getalbum" })
            {
                var page = Input(path + (path.Contains("?") ? "&" : "?") + "__biz=profile-biz", "<html><script>var nickname='主页';</script></html>");
                page.RequestHeaders = Navigation();
                Check(WechatCaptureParser.Parse(page).CanSelectAccount, "supported account and album document: " + path);
            }
            var entityQuery = WechatCaptureParser.Parse(Input("/mp/profile?__biz=entity-biz&amp;nickname=%E5%90%8D%E7%A7%B0&amp;key=K%2B2", "<html></html>"));
            Check(entityQuery.Session.Name == "" && entityQuery.Session.Key == "K+2", "HTML-escaped query credentials remain usable but URL nickname is not a trusted account name");
            var conflict = WechatCaptureParser.Parse(Input("/s?__biz=account-A", "var biz='account-B';"));
            Check(conflict.Reason == WechatCaptureReason.ConflictingBiz && conflict.Session == null && !conflict.CanSelectAccount, "conflicting identities do not select or refresh");
        }

        private static void TrustedAccountNames()
        {
            const string mapped = "var component={nickname:\"data-miniprogram-nickname\"};";
            var actual = Input("/s?__biz=name-biz", "<html><body><a id='js_name'>船山信安</a><script>" + mapped + "var nickname=htmlDecode(\"船山信安\");</script></body></html>");
            Check(WechatCaptureParser.Parse(actual).Session.Name == "船山信安", "real component mapping before the real nickname must not pollute the visible account name");
            actual.ResponseBody = "<script>" + mapped + "var nickname=htmlDecode(\"船山信安\");</script>";
            Check(WechatCaptureParser.Parse(actual).Session.Name == "船山信安", "without js_name, an explicit account nickname declaration wins over component mappings");
            actual.ResponseBody = "<a id='js_name'>DOM名称</a><script>var nickname='脚本名称';</script>";
            Check(WechatCaptureParser.Parse(actual).Session.Name == "DOM名称", "visible account DOM has priority over script fallback");

            string decoys = "// var nickname='line-comment';\n/* window.nickname='block-comment'; */"
                + "const example=\"var nickname='quoted-example';\";const pattern=/var nickname='regex-example'/;"
                + "const template=`var nickname='template-example';${`window.nickname='nested-template';`}`;"
                + "var visitor={nickname:'comment-author'};function component(){var nickname='component-local';}"
                + "other.window.nickname='object-property';";
            Check(AccountNameResolver.Extract(decoys, "name-biz") == "", "comments, strings, regexes, templates, component scopes and unrelated objects cannot declare the account name");
            Check(AccountNameResolver.Extract(decoys + "window.nickname='OpenAI123';", "name-biz") == "OpenAI123", "a real window nickname assignment survives decoy text");
            Check(AccountNameResolver.Extract("let nickname='12345';", "name-biz") == "12345", "legitimate numeric display names are allowed");
            Check(AccountNameResolver.Extract("const nickname='Studio2026';", "name-biz") == "Studio2026", "legitimate English/alphanumeric display names are allowed");
            Check(AccountNameResolver.Extract("var biz='' || 'name-biz',nickname='甲&amp;乙';", "name-biz") == "甲&乙", "multiple explicit declarations and empty-string identifier fallback");
            Check(AccountNameResolver.Extract("window.cgiDataNew={biz:'name-biz',comments:[{nickname:'访客'}],nick_name:'资料名称'};", "name-biz") == "资料名称", "trusted page-data object uses direct account fields and ignores nested comments");
            Check(AccountNameResolver.Extract("<a id='js_name'>其他号</a><script>var biz='different-biz';var nickname='其他号';</script>", "name-biz") == "", "an explicit cross-Biz document cannot supply even a DOM account name");
            Check(AccountNameResolver.Extract("window.cgiData={biz:'different-biz',nickname:'其他号'};", "name-biz") == "", "trusted profile objects still require matching Biz");

            Check(AccountNameResolver.Normalize("data-miniprogram-nickname", "name-biz") == ""
                && AccountNameResolver.Normalize("name-biz", "name-biz") == "" && AccountNameResolver.Normalize("two\nlines", "name-biz") == "",
                "field markers, account identifiers and control characters are not display names");
            Check(AccountNameResolver.Choose("name-biz", "data-miniprogram-nickname", "name-biz", "English123") == "English123", "name selection skips only invalid candidates");
            var known = new AccountSession { Biz = "name-biz", Name = "已有名称", RequestUrl = "https://mp.weixin.qq.com/s?__biz=name-biz" };
            actual.ResponseBody = "<html><script>" + mapped + "</script></html>";
            actual.RequestUrl = "https://mp.weixin.qq.com/s?__biz=name-biz&nickname=QueryDecoy&nick_name=SecondDecoy";
            Check(WechatCaptureParser.Parse(actual, new[] { known }).Session.Name == "已有名称", "missing trustworthy name preserves a valid same-Biz name without taking URL candidates");
            Check(WechatCaptureParser.Parse(actual).Session.Name == "", "missing names stay empty rather than becoming a Biz or field marker");
            known.Name = "data-miniprogram-nickname";
            Check(WechatCaptureParser.Parse(actual, new[] { known }).Session.Name == "", "invalid historical name is not reused");
            known.Name = "已有名称";
            var background = Input("/mp/appmsg_comment?__biz=name-biz", "{\"comment\":[{\"nickname\":\"评论者\"}]}");
            Check(WechatCaptureParser.Parse(background, new[] { known }).Session.Name == "已有名称", "background comments cannot replace a trusted account name");
            actual.RequestUrl = "https://mp.weixin.qq.com/s?__biz=other-biz&nickname=QueryDecoy";
            Check(WechatCaptureParser.Parse(actual, new[] { known }).Session.Name == "", "a valid cached name cannot be borrowed across Biz");
        }

        private static void CachedDocuments()
        {
            var known = new AccountSession { Biz = "cached-biz", Name = "缓存名称", RequestUrl = "https://mp.weixin.qq.com/s/cached-short",
                Cookie = "old=fixture", Uin = "123", CapturedAt = new DateTime(2026, 1, 1) };
            var cached = Input("/s/cached-short#new-fragment", "ignored invalid 304 body biz='other';");
            cached.StatusCode = 304;
            cached.RequestHeaders = Navigation();
            cached.RequestHeaders["cookie"] = "new=fixture";
            var parsed = WechatCaptureParser.Parse(cached, new[] { known });
            Check(parsed.CanSelectAccount && parsed.Reason == WechatCaptureReason.CachedDocumentNavigation && parsed.Session.Biz == "cached-biz"
                && parsed.Session.Name == "缓存名称" && parsed.Session.Cookie == "new=fixture", "304 exact short-link association and request cookie refresh");
            Check(known.Cookie == "old=fixture" && known.RequestUrl.EndsWith("cached-short", StringComparison.Ordinal), "known session is not mutated");
            var queryCached = Input("/mp/profile?__biz=query-biz&nickname=Query", "");
            queryCached.StatusCode = 304;
            var direct = WechatCaptureParser.Parse(queryCached);
            Check(direct.CanSelectAccount && direct.Reason == WechatCaptureReason.CachedLegacyDocumentCandidate && direct.Session.Biz == "query-biz" && direct.Session.Name == "", "304 query identifies the account without trusting a URL nickname");
            var unknown = Input("/s/unrelated", ""); unknown.StatusCode = 304;
            var unknownResult = WechatCaptureParser.Parse(unknown, new[] { known });
            Check(!unknownResult.CanSelectAccount && unknownResult.Reason == WechatCaptureReason.MissingBiz, "single known account is not assigned to an unrelated cached URL");
            var duplicate = known.Clone(); duplicate.Biz = "second-biz";
            Check(WechatCaptureParser.Parse(cached, new[] { known, duplicate }).Reason == WechatCaptureReason.AmbiguousKnownSession, "ambiguous cached URL is not guessed");
            var empty200 = WechatCaptureParser.Parse(Input("/s?__biz=empty", ""));
            Check(!empty200.CanSelectAccount && empty200.Reason == WechatCaptureReason.EmptyDocumentBody, "empty successful response does not impersonate cached navigation");
        }

        private static void BackgroundCannotSelect()
        {
            var known = new AccountSession { Biz = "background-biz", Name = "已知公众号", RequestUrl = "https://mp.weixin.qq.com/s/background-article", Key = "old-key" };
            var stats = Input("/mp/getappmsgext", "{\"appmsg_token\":\"new-token\"}");
            stats.RequestMethod = "POST";
            stats.ResponseContentType = "application/json";
            stats.RequestHeaders = Headers("Content-Type", "application/x-www-form-urlencoded", "Cookie", "fresh=fixture");
            stats.RequestBody = "__biz=background-biz&key=K%2B3&uin=123";
            var parsed = WechatCaptureParser.Parse(stats, new[] { known });
            Check(!parsed.CanSelectAccount && parsed.Reason == WechatCaptureReason.BackgroundSessionUpdate && parsed.Session.Key == "K+3"
                && parsed.Session.AppMsgToken == "new-token" && parsed.Session.RequestUrl == known.RequestUrl, "stats refresh session without selecting or replacing document URL");
            Check(known.Key == "old-key", "stats refresh does not mutate caller session");
            known.Name = "公众号名称"; known.UserName = "gh_account";
            var comments = Input("/mp/appmsg_comment?__biz=background-biz", "{\"comment\":[{\"nick_name\":\"评论者\",\"username\":\"visitor\"}]}");
            var refreshed = WechatCaptureParser.Parse(comments, new[] { known });
            Check(refreshed.Session.Name == "公众号名称" && refreshed.Session.UserName == "gh_account" && !refreshed.CanSelectAccount,
                "comment author cannot replace the public account identity");
            Check(WechatCaptureParser.Parse(comments).Session.Name == "", "unknown account is not named after a commenter");
            foreach (string path in new[] { "/mp/profile_ext?action=getmsg&__biz=background-biz", "/mp/appmsgalbum?__biz=background-biz&f=json", "/mp/appmsg_comment?__biz=background-biz" })
            {
                var api = Input(path, "{}"); api.RequestHeaders = Navigation();
                var result = WechatCaptureParser.Parse(api);
                Check(!result.CanSelectAccount && result.Reason == WechatCaptureReason.BackgroundSessionUpdate, "known backend paths override navigation headers: " + path);
            }
            var xhr = Input("/s?__biz=xhr-biz", "<html></html>"); xhr.RequestHeaders = Headers("Sec-Fetch-Mode", "cors", "Sec-Fetch-Dest", "empty");
            Check(!WechatCaptureParser.Parse(xhr).CanSelectAccount, "XHR to document-shaped URL is not navigation");
            xhr.RequestHeaders = Headers("X-Requested-With", "XMLHttpRequest");
            Check(!WechatCaptureParser.Parse(xhr).CanSelectAccount, "legacy XMLHttpRequest does not select");
            xhr.RequestHeaders = Headers("Sec-Fetch-Mode", "navigate", "Sec-Fetch-Dest", "iframe");
            Check(!WechatCaptureParser.Parse(xhr).CanSelectAccount, "subframe is not a top-level document");
            var jsonPost = Input("/mp/getappmsgext", "{}"); jsonPost.RequestMethod = "POST";
            jsonPost.RequestBody = "{\"__biz\":\"json-biz\",\"key\":\"json-key\",\"uin\":987}";
            jsonPost.RequestHeaders = Headers("Content-Type", "application/json");
            var json = WechatCaptureParser.Parse(jsonPost);
            Check(!json.CanSelectAccount && json.Session.Biz == "json-biz" && json.Session.Key == "json-key" && json.Session.Uin == "987", "JSON POST session fields");
            var unlisted = WechatCaptureParser.Parse(Input("/mp/unrecognised?__biz=should-not-select", "var biz='should-not-select';"));
            Check(unlisted.Reason == WechatCaptureReason.UnsupportedPath && unlisted.Session == null, "arbitrary biz-bearing APIs are not navigation or session sources");
        }

        private static void SpeculationAndRefererBoundaries()
        {
            foreach (var pair in new[] { Tuple.Create("Purpose", "prefetch"), Tuple.Create("Sec-Purpose", "prefetch;prerender"),
                Tuple.Create("X-Moz", "prefetch"), Tuple.Create("X-Purpose", "Prerender") })
            {
                var speculative = Input("/s?__biz=speculative", "<html></html>"); speculative.RequestHeaders = Headers(pair.Item1, pair.Item2);
                var result = WechatCaptureParser.Parse(speculative);
                Check(result.Reason == WechatCaptureReason.SpeculativeRequest && result.Session == null && !result.CanSelectAccount, "explicit speculation is ignored: " + pair.Item1);
            }
            var stat = Input("/mp/getappmsgext", "{}");
            stat.RequestHeaders = Headers("Referer", "https://mp.weixin.qq.com/s?__biz=referer-biz&nickname=Ref&key=K%2B4");
            var fromReferer = WechatCaptureParser.Parse(stat);
            Check(!fromReferer.CanSelectAccount && fromReferer.Session.Biz == "referer-biz" && fromReferer.Session.Key == "K+4", "trusted article referer can associate a backend session");
            foreach (string badReferer in new[] { "https://mp.weixin.qq.com.attacker.invalid/s?__biz=bad", "https://mp.weixin.qq.com@attacker.invalid/s?__biz=bad",
                "https://user@mp.weixin.qq.com/s?__biz=bad", "https://mp.weixin.qq.com:9443/s?__biz=bad", "https://mp.weixin.qq.com/mp/other?__biz=bad" })
            {
                stat.RequestHeaders["Referer"] = badReferer;
                Check(WechatCaptureParser.Parse(stat).Session == null, "untrusted referer is not an account source");
            }
            var destination = Input("/s/new-unknown", ""); destination.StatusCode = 304;
            destination.RequestHeaders = Headers("Referer", "https://mp.weixin.qq.com/s?__biz=source-A&key=A-key");
            var uncertain = WechatCaptureParser.Parse(destination);
            Check(!uncertain.CanSelectAccount && uncertain.Session == null && uncertain.Reason == WechatCaptureReason.RefererOnlyUncertain, "cross-page referer cannot attribute unknown destination");
            destination.RequestUrl = "https://mp.weixin.qq.com/s?__biz=destination-B";
            var direct = WechatCaptureParser.Parse(destination);
            Check(direct.CanSelectAccount && direct.Session.Biz == "destination-B" && direct.Session.Key == "", "cross-account navigation does not import source tokens");
        }

        private static void DiagnosticsAndIsolation()
        {
            const string secret = "UNIQUE_PRIVATE_TOKEN_123";
            var input = Input("/s?__biz=" + secret + "&key=" + secret + "&pass_ticket=" + secret, "var nickname='" + secret + "';");
            input.RequestHeaders = Headers("Cookie", secret, "Authorization", secret);
            var result = WechatCaptureParser.Parse(input);
            Check(!result.Diagnostic.Contains(secret) && !result.ToString().Contains(secret), "diagnostics omit credentials, URL, account identifier and name");
            result.Session.Headers["Cookie"] = "changed";
            Check(input.RequestHeaders["Cookie"] == secret, "parser output headers are detached from input");
            input.RequestUrl = "https://mp.weixin.qq.com.attacker.invalid/s?__biz=" + secret;
            Check(WechatCaptureParser.Parse(input).Reason == WechatCaptureReason.UnsupportedHost, "exact request host check");
            input.RequestUrl = "https://mp.weixin.qq.com/s?__biz=fixture"; input.StatusCode = 403;
            Check(WechatCaptureParser.Parse(input).Reason == WechatCaptureReason.UnsupportedStatus, "restricted response cannot select");
            input.StatusCode = 200; input.RequestMethod = "OPTIONS";
            Check(WechatCaptureParser.Parse(input).Reason == WechatCaptureReason.UnsupportedMethod, "preflight cannot select");
            Check(WechatCaptureParser.Parse(null).Reason == WechatCaptureReason.InvalidUrl, "null input has a diagnostic");
        }

        private static WechatCaptureInput Input(string path, string body)
            => new WechatCaptureInput { RequestUrl = "https://mp.weixin.qq.com" + path, ResponseBody = body };
        private static Dictionary<string, string> Navigation() => Headers("Sec-Fetch-Mode", "navigate", "Sec-Fetch-Dest", "document");
        private static Dictionary<string, string> Headers(params string[] pairs)
        {
            // A case-sensitive input exercises normalization inside the parser.
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) headers[pairs[i]] = pairs[i + 1];
            return headers;
        }
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("WechatCaptureParser test failed: " + message);
        }
    }
}
