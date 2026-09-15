using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    /// <summary>Offline tests only: no proxy listener, certificate store, registry writes, or WeChat requests.</summary>
    public static class NetworkingSelfTests
    {
        public static async Task RunAsync()
        {
            CaptureParsing();
            CaptureService.VerifyProxyRecoveryRules();
            ExistingProxyPortProtection();
            CookieUpdates();
            await MultiplePages().ConfigureAwait(false);
            await MultiplePages(false).ConfigureAwait(false);
            await InvalidResponses().ConfigureAwait(false);
            await OptionalTicketFailure().ConfigureAwait(false);
            await Cancellation().ConfigureAwait(false);
            OptionalTicketUrl();
        }

        private static void CaptureParsing()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["cookie"] = "test_cookie=local_fixture", ["user-agent"] = "SelfTest" };
            var old = CaptureService.ParseCapturedPage("var nickname = htmlDecode(\"甲&amp;乙\"); var user_name = 'gh_test';",
                "https://mp.weixin.qq.com/s?__biz=fixture-biz&uin=12&key=K%2B1&pass_ticket=P%2F1", headers);
            Check(old != null && old.Name == "甲&乙" && old.UserName == "gh_test" && old.Biz == "fixture-biz", "旧版文章识别");
            Check(old.Key == "K+1" && old.PassTicket == "P/1" && old.Cookie == "test_cookie=local_fixture", "会话参数 URL 解码");
            var modern = CaptureService.ParseCapturedPage("var biz = '' || 'short-biz'; window.cgiDataNew = { nick_name:'\\u65b0版\\'名称', user_name:'gh_new' };",
                "https://mp.weixin.qq.com/s/short-link", headers);
            Check(modern != null && modern.Biz == "short-biz" && modern.Name == "新版'名称", "新版短链接与 JavaScript 转义");
            var quoted = CaptureService.ParseCapturedPage("window.cgiDataNew = {\"nick_name\":\"JSON名称\",\"biz\":\"quoted-biz\"};", "https://mp.weixin.qq.com/s/quoted", headers);
            Check(quoted?.Name == "JSON名称" && quoted.Biz == "quoted-biz", "带引号的新页面属性");
            var history = CaptureService.ParseCapturedPage("var nickname='历史页';", "https://mp.weixin.qq.com/mp/profile_ext?action=home&__biz=profile-biz", headers);
            Check(history?.Biz == "profile-biz" && history.Name == "历史页", "历史主页识别");
            Check(CaptureService.ParseCapturedPage("var biz='fake';", "https://mp.weixin.qq.com.attacker.invalid/s", headers) == null, "Host 必须精确匹配");
            Check(CaptureService.ParseCapturedPage("var biz='fake';", "https://mp.weixin.qq.com/mp/getappmsgext", headers) == null, "忽略非识别页面");
            Check(CaptureService.ParseCapturedPage("var nickname='missing';", "https://mp.weixin.qq.com/s/empty", headers) == null, "无公众号标识不假报识别");
        }

        private static void CookieUpdates()
        {
            var session = new AccountSession { Cookie = "old=1; changed=before; removed=1" };
            HttpTransport.MergeCookies(session, new Uri("https://mp.weixin.qq.com/mp/profile_ext"), new[]
            {
                "changed=after; Path=/; Secure, fresh=2; Path=/; Expires=Wed, 21 Oct 2037 07:28:00 GMT",
                "removed=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT"
            });
            Check(session.Cookie.Contains("old=1") && session.Cookie.Contains("changed=after") && session.Cookie.Contains("fresh=2") && !session.Cookie.Contains("removed="), "Cookie 更新、逗号日期及过期删除");
            string before = session.Cookie;
            HttpTransport.MergeCookies(session, new Uri("https://mmbiz.qpic.cn/image"), new[] { "changed=cdn; Path=/" });
            HttpTransport.MergeCookies(session, new Uri("http://mp.weixin.qq.com/s"), new[] { "changed=insecure; Path=/" });
            HttpTransport.MergeCookies(session, new Uri("https://mp.weixin.qq.com/s"), new[] { "changed=foreign; Domain=attacker.invalid; Path=/" });
            Check(session.Cookie == before, "CDN、非 HTTPS 与外域 Cookie 隔离");
        }

        private static void ExistingProxyPortProtection()
        {
            var ports = CaptureService.GetConfiguredProxyPorts("http=127.0.0.1:8879;https=127.0.0.1:7897;socks=[::1]:1080");
            Check(ports.SetEquals(new[] { 8879, 7897, 1080 }), "识别已配置的 HTTP/HTTPS/SOCKS 代理端口");
            Check(CaptureService.ChooseInitialPort(8879, ports) == 0, "即使旧代理未运行，也不能监听其已配置端口");
            Check(CaptureService.ChooseInitialPort(8880, ports) == 8880, "未冲突的首选端口可正常使用");
            var bare = CaptureService.GetConfiguredProxyPorts("127.0.0.1:7897");
            Check(bare.SetEquals(new[] { 7897 }) && CaptureService.ChooseInitialPort(8879, bare) == 8879, "当前单地址代理格式保留且不与默认端口冲突");
        }

        private static async Task MultiplePages(bool withTicket = true)
        {
            var calls = new List<int>();
            var fake = new FakeTransport((url, session, token) =>
            {
                int offset = int.Parse(HttpUtility.ParseQueryString(new Uri(url).Query)["offset"]);
                calls.Add(offset);
                var query = HttpUtility.ParseQueryString(new Uri(url).Query);
                Check(query["count"] == "10", "分页大小为 10");
                Check(query.AllKeys.Contains("pass_ticket") == withTicket, "仅有票据时添加 pass_ticket 参数");
                if (offset == 0) return Task.FromResult(Page(true, 10, Message("101", true), Message("202")));
                if (offset == 10) return Task.FromResult(Page(false, 20, Message("202"), Message("303")));
                throw new Exception("读取了不应访问的分页。");
            });
            var rows = new List<ArticleRecord>();
            await Collector(fake).CollectAsync(Session(withTicket), (article, token) => { rows.Add(article); return Task.CompletedTask; }, null, CancellationToken.None).ConfigureAwait(false);
            Check(calls.SequenceEqual(new[] { 0, 10 }) && rows.Count == 4, "多页、跨页去重、多图文");
            Check(rows.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() == 4 && rows.Select(x => x.Title).Distinct().Count() == 1, "不同文章同标题完整保留");
            Check(rows.Any(x => x.Mid == "101" && x.Idx == 2), "多图文子文章位置");
        }

        private static async Task OptionalTicketFailure()
        {
            var rows = new List<ArticleRecord>();
            var updates = new List<string>();
            var fake = new FakeTransport((url, session, token) =>
            {
                var query = HttpUtility.ParseQueryString(new Uri(url).Query);
                Check(!query.AllKeys.Contains("pass_ticket"), "无票据错误路径也不发送空票据");
                return Task.FromResult(query["offset"] == "0" ? Page(true, 10, Message("101")) : "{\"ret\":-3}");
            });
            await ExpectFailure(() => Collector(fake).CollectAsync(Session(false), (article, token) =>
            {
                rows.Add(article); return Task.CompletedTask;
            }, new ImmediateProgress(p => updates.Add(p.Message)), CancellationToken.None), "ret=-3").ConfigureAwait(false);
            Check(fake.Requests == 2 && rows.Count == 1, "无票据仍读取分页，后页失败保留已读文章");
            Check(!updates.Any(message => message.Contains("已返回末页")), "接口错误不报告完成");

            foreach (string field in new[] { "Cookie", "Uin", "Key" })
            {
                var session = Session(false);
                if (field == "Cookie") session.Cookie = "";
                if (field == "Uin") session.Uin = "";
                if (field == "Key") session.Key = "";
                var notUsed = new FakeTransport((u, s, t) => throw new Exception("会话不足时不应请求网络。"));
                await ExpectFailure(() => Collector(notUsed).CollectAsync(session, Ignore, null, CancellationToken.None), "会话参数不完整").ConfigureAwait(false);
                Check(notUsed.Requests == 0, "其它会话门槛保留：" + field);
            }
        }

        private static void OptionalTicketUrl()
        {
            foreach (string value in new[] { null, "", "  " })
            {
                var session = Session(); session.PassTicket = value;
                Check(!HttpUtility.ParseQueryString(new Uri(HistoryCollector.BuildPageUrl(session, 0)).Query).AllKeys.Contains("pass_ticket"), "空白票据参数省略");
            }
            var encoded = Session(); encoded.PassTicket = "fixture+/=";
            Check(HttpUtility.ParseQueryString(new Uri(HistoryCollector.BuildPageUrl(encoded, 0)).Query)["pass_ticket"] == encoded.PassTicket, "已有票据转义后保留原值");
        }

        private static async Task InvalidResponses()
        {
            await ExpectFailure(() => Collector(new FakeTransport((u, s, t) => Task.FromResult(Page(true, 0, Message("101")))))
                .CollectAsync(Session(), Ignore, null, CancellationToken.None), "没有前进").ConfigureAwait(false);
            await ExpectFailure(() => Collector(new FakeTransport((u, s, t) => Task.FromResult("{\"ret\":-3}")))
                .CollectAsync(Session(), Ignore, null, CancellationToken.None), "ret=-3").ConfigureAwait(false);
            await ExpectFailure(() => Collector(new FakeTransport((u, s, t) => Task.FromResult(Page(false, 0))))
                .CollectAsync(Session(), Ignore, null, CancellationToken.None), "未返回").ConfigureAwait(false);
            await ExpectFailure(() => Collector(new FakeTransport((u, s, t) => Task.FromResult("<html>login</html>")))
                .CollectAsync(Session(), Ignore, null, CancellationToken.None), "登录状态").ConfigureAwait(false);
            var notUsed = new FakeTransport((u, s, t) => throw new Exception("会话不足时不应请求网络。"));
            await ExpectFailure(() => Collector(notUsed).CollectAsync(new AccountSession { Biz = "fixture-biz" }, Ignore, null, CancellationToken.None), "会话参数不完整").ConfigureAwait(false);
            Check(notUsed.Requests == 0, "缺少会话时不发请求");
        }

        private static async Task Cancellation()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var fake = new FakeTransport(async (url, session, token) =>
                {
                    entered.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    throw new Exception("不应完成被取消的请求。");
                });
                Task task = Collector(fake).CollectAsync(Session(false), Ignore, null, cancellation.Token);
                await entered.Task.ConfigureAwait(false);
                cancellation.Cancel();
                await ExpectCancelled(task).ConfigureAwait(false);
                Check(fake.Requests == 1, "取消传递到在途 transport");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                var fake = new FakeTransport((url, session, token) => Task.FromResult(Page(true, 10, Message("101"))));
                var collector = new HistoryCollector(fake) { MinimumDelay = TimeSpan.FromSeconds(15), MaximumDelay = TimeSpan.FromSeconds(15) };
                var progress = new ImmediateProgress(p => { if (p.Message.Contains("等待下一页")) cancellation.Cancel(); });
                Task task = collector.CollectAsync(Session(false), Ignore, progress, cancellation.Token);
                await ExpectCancelled(task).ConfigureAwait(false);
                Check(fake.Requests == 1, "节流等待可立即取消且不多读一页");
            }
        }

        private static HistoryCollector Collector(IHttpTransport transport) => new HistoryCollector(transport)
            { MinimumDelay = TimeSpan.Zero, MaximumDelay = TimeSpan.Zero };
        private static AccountSession Session(bool withTicket = true) => new AccountSession
            { Biz = "fixture-biz", Name = "测试公众号", Cookie = "test=fixture", Uin = "12", Key = "key", PassTicket = withTicket ? "ticket" : "" };
        private static Task Ignore(ArticleRecord article, CancellationToken token) => Task.CompletedTask;
        private static JObject Message(string mid, bool child = false)
        {
            var article = new JObject { ["title"] = "相同标题", ["content_url"] = "https://mp.weixin.qq.com/s?__biz=fixture-biz&amp;mid=" + mid + "&amp;idx=1" };
            if (child) article["multi_app_msg_item_list"] = new JArray(new JObject
                { ["title"] = "相同标题", ["content_url"] = "https://mp.weixin.qq.com/s?__biz=fixture-biz&amp;mid=" + mid + "&amp;idx=2" });
            return new JObject { ["comm_msg_info"] = new JObject { ["id"] = mid, ["datetime"] = 1700000000 }, ["app_msg_ext_info"] = article };
        }
        private static string Page(bool more, int next, params JObject[] messages) => new JObject
        {
            ["ret"] = 0, ["can_msg_continue"] = more ? 1 : 0, ["next_offset"] = next,
            ["general_msg_list"] = new JObject { ["list"] = new JArray(messages.Cast<object>().ToArray()) }.ToString(Formatting.None)
        }.ToString(Formatting.None);
        private static void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("Networking self-test failed: " + label); }
        private static async Task ExpectFailure(Func<Task> action, string message)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            { Check(ex.Message.Contains(message), "错误应说明原因：" + message); return; }
            throw new InvalidOperationException("Networking self-test failed: 应拒绝错误响应：" + message);
        }
        private static async Task ExpectCancelled(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(1500)).ConfigureAwait(false) != task) throw new InvalidOperationException("Networking self-test failed: 取消未及时结束。");
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("Networking self-test failed: 应传播取消状态。");
        }
        private sealed class ImmediateProgress : IProgress<CollectionProgress>
        {
            private readonly Action<CollectionProgress> callback;
            public ImmediateProgress(Action<CollectionProgress> callback) { this.callback = callback; }
            public void Report(CollectionProgress value) { callback(value); }
        }
        private sealed class FakeTransport : IHttpTransport
        {
            private readonly Func<string, AccountSession, CancellationToken, Task<string>> get;
            public int Requests { get; private set; }
            public FakeTransport(Func<string, AccountSession, CancellationToken, Task<string>> get) { this.get = get; }
            public Task<string> GetStringAsync(string url, AccountSession session, CancellationToken token) { Requests++; return get(url, session, token); }
            public Task<string> PostFormAsync(string url, IDictionary<string, string> values, AccountSession session, CancellationToken token) => throw new NotSupportedException();
            public Task DownloadAsync(string url, string destination, AccountSession session, CancellationToken token) => throw new NotSupportedException();
        }
    }
}
