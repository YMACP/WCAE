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
    public static class HistoryPageSelfTests
    {
        public static async Task RunAsync()
        {
            await ExactCursorAndMultiArticleOrder();
            await RetryKeepsCursor();
            await EmptyFinalPageAndInvalidCursor();
            await CancellationStopsRequestAndDelay();
        }

        private static async Task ExactCursorAndMultiArticleOrder()
        {
            var offsets = new List<int>();
            var main = Item("501", 1, "普通文章完整标题", 0);
            main["multi_app_msg_item_list"] = new JArray(Item("501", 2, "同组第二篇", 0), Item("501", 3, "同组第三篇", 0));
            var transport = new FakeTransport((url, session, token) =>
            {
                offsets.Add(Offset(url));
                return Task.FromResult(Page(true, 37, Message("message-A", main), Message("message-B", Item("502", 1, "短内容", 10))));
            });
            var collector = Collector(transport);
            var page = await collector.ReadPageAtAsync(Session(), 24, null, CancellationToken.None);
            Check(offsets.SequenceEqual(new[] { 24 }) && page.Offset == 24 && page.NextOffset == 37 && page.CanContinue,
                "指定断点请求被重置为第一页，或单次调用自行请求了后页");
            Check(page.MessageCount == 2 && page.Articles.Count == 4 && page.Articles.Select(a => a.Idx).SequenceEqual(new[] { 1, 2, 3, 1 }),
                "多图文未按主项和子项原顺序完整展开");
            Check(page.Articles[0].Title == "普通文章完整标题" && page.Articles[0].HistoryItemShowType == 0 && page.Articles[3].HistoryItemShowType == 10,
                "普通文章或短内容被按类型过滤");
            Check(page.Articles[0].SourceMessageId == "message-A" && page.Articles[0].Mid == "501", "源消息ID冒充文章Mid");
        }

        private static async Task RetryKeepsCursor()
        {
            var offsets = new List<int>();
            var transport = new FakeTransport((url, session, token) =>
            {
                offsets.Add(Offset(url));
                if (offsets.Count < 3) throw new TimeoutException("local fixture");
                return Task.FromResult(Page(false, 25, Message("message-C", Item("503", 1, "重试后文章", 0))));
            });
            var page = await Collector(transport).ReadPageAtAsync(Session(), 11, null, CancellationToken.None);
            Check(offsets.SequenceEqual(new[] { 11, 11, 11 }) && page.Offset == 11 && !page.CanContinue,
                "重试更换了游标或从零重扫");
        }

        private static async Task EmptyFinalPageAndInvalidCursor()
        {
            var transport = new FakeTransport((url, session, token) => Task.FromResult(Page(false, 24)));
            var page = await Collector(transport).ReadPageAtAsync(Session(), 24, null, CancellationToken.None);
            Check(page.Articles.Count == 0 && page.MessageCount == 0 && !page.CanContinue, "续采空末页被误当成首次没有文章");
            var invalid = new FakeTransport((url, session, token) => Task.FromResult(Page(true, 24, Message("message-D", Item("504", 1, "无效游标", 0)))));
            try { await Collector(invalid).ReadPageAtAsync(Session(), 24, null, CancellationToken.None); }
            catch (IOException) { Check(invalid.Calls == 1, "无效分页响应被自动重试"); return; }
            throw new InvalidOperationException("History page self-test failed: 递增游标校验未执行");
        }

        private static async Task CancellationStopsRequestAndDelay()
        {
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new FakeTransport(async (url, session, token) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("被取消的模拟请求不应成功");
            });
            Task request = Collector(transport).ReadPageAtAsync(Session(), 37, null, cancellation.Token);
            Check(await Task.WhenAny(entered.Task, Task.Delay(3000)) == entered.Task, "模拟请求没有进入等待");
            cancellation.Cancel();
            await ExpectCancelled(request);
            Check(transport.Calls == 1, "取消后又发出了分页请求");
            using var delayCancellation = new CancellationTokenSource();
            var collector = Collector(transport); collector.MinimumDelay = collector.MaximumDelay = TimeSpan.FromSeconds(15);
            Task wait = collector.WaitForNextPageAsync(null, delayCancellation.Token);
            delayCancellation.Cancel();
            await ExpectCancelled(wait);
            Check(transport.Calls == 1, "等待下一页时取消仍发请求");
        }

        private static HistoryCollector Collector(IHttpTransport transport) => new HistoryCollector(transport)
        { MinimumDelay = TimeSpan.Zero, MaximumDelay = TimeSpan.Zero, RetryDelay = TimeSpan.Zero };
        private static AccountSession Session() => new AccountSession { Biz = "page-fixture", Name = "分页测试号", Cookie = "fixture=1", Uin = "1", Key = "fixture" };
        private static int Offset(string url) => int.Parse(HttpUtility.ParseQueryString(new Uri(url).Query)["offset"]);
        private static JObject Item(string mid, int idx, string title, int type) => new JObject
        {
            ["title"] = title, ["item_show_type"] = type,
            ["content_url"] = "https://mp.weixin.qq.com/s?__biz=page-fixture&mid=" + mid + "&idx=" + idx
        };
        private static JObject Message(string messageId, JObject item) => new JObject
        { ["comm_msg_info"] = new JObject { ["id"] = messageId, ["type"] = 49, ["datetime"] = 1700000000 }, ["app_msg_ext_info"] = item };
        private static string Page(bool more, int next, params JObject[] messages) => new JObject
        {
            ["ret"] = 0, ["can_msg_continue"] = more ? 1 : 0, ["next_offset"] = next,
            ["general_msg_list"] = new JObject { ["list"] = new JArray(messages) }.ToString(Formatting.None)
        }.ToString(Formatting.None);
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("History page self-test failed: " + message); }
        private static async Task ExpectCancelled(Task task)
        {
            Check(await Task.WhenAny(task, Task.Delay(3000)) == task, "取消未及时结束");
            try { await task; } catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("History page self-test failed: 预期取消");
        }
        private sealed class FakeTransport : IHttpTransport
        {
            private readonly Func<string, AccountSession, CancellationToken, Task<string>> get;
            public int Calls { get; private set; }
            public FakeTransport(Func<string, AccountSession, CancellationToken, Task<string>> get) { this.get = get; }
            public Task<string> GetStringAsync(string url, AccountSession session, CancellationToken token) { Calls++; return get(url, session, token); }
            public Task<string> PostFormAsync(string url, IDictionary<string, string> values, AccountSession session, CancellationToken token) => throw new NotSupportedException();
            public Task DownloadAsync(string url, string destination, AccountSession session, CancellationToken token) => throw new NotSupportedException();
        }
    }
}
