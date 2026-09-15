using System;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace WCAE
{
    /// <summary>Pure synthetic filtering only: does not start UIA or inspect any real window.</summary>
    public static class WechatPageObserverSelfTests
    {
        public static void Run()
        {
            string url;
            Check(WechatPageObserver.TryDocumentUrl("https://mp.weixin.qq.com/s?__biz=A&amp;key=private%2Btoken#fragment", out url)
                && url == "https://mp.weixin.qq.com/s?__biz=A&key=private%2Btoken", "query preserved only in memory; entities decoded and fragment ignored");
            foreach (string path in new[] { "/s", "/s/short", "/mp/profile_ext?action=home", "/mp/profile", "/mp/appmsgalbum?album_id=1" })
                Check(WechatPageObserver.TryDocumentUrl("https://mp.weixin.qq.com" + path, out url), "supported document path");
            foreach (string rejected in new[] { "https://mp.weixin.qq.com.attacker.invalid/s?__biz=A", "https://user@mp.weixin.qq.com/s?__biz=A",
                "https://mp.weixin.qq.com:9443/s?__biz=A", "file:///mp.weixin.qq.com/s", "javascript:alert(1)", "聊天文字 fixture",
                "https://mp.weixin.qq.com/mp/getappmsgext?__biz=A", "https://mp.weixin.qq.com/mp/profile_ext?action=getmsg&__biz=A",
                "https://mp.weixin.qq.com/mp/appmsgalbum?f=json&__biz=A", "https://mp.weixin.qq.com/s?ajax=1&__biz=A", "https://mp.weixin.qq.com/s\r\nCookie: private" })
                Check(!WechatPageObserver.TryDocumentUrl(rejected, out url), "reject non-document, non-default-port and untrusted values");
            var a = Document("https://mp.weixin.qq.com/s?__biz=A&mid=1");
            a.AccountName = "公开公众号";
            var selected = WechatPageObserver.SelectVisibleDocuments(new[] { a });
            Check(selected.Page?.AccountName == "公开公众号" && selected.Reason == WechatPageObserver.SelectionReason.Selected, "one visible article and optional scoped account name");
            Check(!selected.Page.ToString().Contains("__biz") && !selected.Page.ToString().Contains(a.AccountName), "model string representation is redacted");
            var b = Document("https://mp.weixin.qq.com/s?__biz=B&mid=2");
            Check(WechatPageObserver.SelectVisibleDocuments(new[] { a, b }).Reason == WechatPageObserver.SelectionReason.ConflictingAccounts, "different visible accounts are ambiguous");
            b.IsVisible = false;
            Check(WechatPageObserver.SelectVisibleDocuments(new[] { a, b }).Page?.Url == a.Url, "offscreen document does not compete");
            b.IsVisible = true; b.IsNestedDocument = true;
            Check(WechatPageObserver.SelectVisibleDocuments(new[] { a, b }).Page?.Url == a.Url, "nested document or iframe does not compete");
            var secondArticle = Document("https://mp.weixin.qq.com/s?__biz=A&mid=3");
            Check(WechatPageObserver.SelectVisibleDocuments(new[] { a, secondArticle }).Reason == WechatPageObserver.SelectionReason.AmbiguousDocuments, "same account in multiple different documents is not assigned arbitrary tokens");
            Check(WechatPageObserver.SelectVisibleDocuments(new[] { a, Document(a.Url + "#same") }).Page?.Url == a.Url, "duplicate accessible elements for one URL are deduplicated");
            Check(WechatPageObserver.SelectVisibleDocuments(new[] { a, Document("https://mp.weixin.qq.com/s/unknown") }).Page == null, "short link with unknown identity cannot be assumed to match known account");
            a.IsVisible = false;
            Check(WechatPageObserver.SelectVisibleDocuments(new[] { a }).Reason == WechatPageObserver.SelectionReason.NoDocument, "no visible eligible document");
            Check(WechatPageObserver.SelectVisibleDocuments(null).Page == null, "empty scan");
            Check(WechatPageObserver.SelectVisibleDocuments(Enumerable.Range(0, 65).Select(i => Document("https://mp.weixin.qq.com/s/" + i))).Page == null, "bounded candidate set");
            var invalidName = Document("https://mp.weixin.qq.com/s/one"); invalidName.AccountName = "name\r\nprivate";
            Check(WechatPageObserver.SelectVisibleDocuments(new[] { invalidName }).Page.AccountName == "", "invalid name is omitted instead of inferred");
            TestPendingObservationInvalidation();
        }

        private static void TestPendingObservationInvalidation()
        {
            // Synthetic ObserverRun only: no thread, UIA provider, real window, or process scan.
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            using var observer = new WechatPageObserver();
            var runType = typeof(WechatPageObserver).GetNestedType("ObserverRun", BindingFlags.NonPublic);
            var observation = Activator.CreateInstance(runType, true);
            var runField = typeof(WechatPageObserver).GetField("run", hidden);
            var lastUrl = runType.GetField("LastUrl");
            var cancellation = (CancellationTokenSource)runType.GetField("Cancellation").GetValue(observation);
            var publish = typeof(WechatPageObserver).GetMethod("Publish", hidden);
            runField.SetValue(observer, observation);
            try
            {
                int count = 0;
                long publishedGeneration = -1;
                observer.DocumentObserved += page => { count++; publishedGeneration = page.ObservationGeneration; };
                lastUrl.SetValue(observation, "previous-document");
                var first = WechatPageObserver.SelectVisibleDocuments(new[] { Document("https://mp.weixin.qq.com/s?__biz=A&mid=1") });
                long staleScan = observer.ObservationGeneration;
                observer.InvalidatePendingObservations();
                publish.Invoke(observer, new object[] { observation, first, "test scan", staleScan });
                Check(count == 0 && (string)lastUrl.GetValue(observation) == "previous-document", "scan begun before invalidation cannot publish or alter URL dedupe");
                publish.Invoke(observer, new object[] { observation, first, "test scan", observer.ObservationGeneration });
                Check(count == 1 && publishedGeneration == observer.ObservationGeneration, "fresh scan publishes its original generation");
                observer.InvalidatePendingObservations();
                publish.Invoke(observer, new object[] { observation, first, "test scan", observer.ObservationGeneration });
                Check(count == 1 && (string)lastUrl.GetValue(observation) == first.Page.Url, "invalidation preserves the same visible URL dedupe");

                var second = WechatPageObserver.SelectVisibleDocuments(new[] { Document("https://mp.weixin.qq.com/s?__biz=B&mid=2") });
                observer.SetHistorySuspended(true);
                long pausedScan = observer.ObservationGeneration;
                publish.Invoke(observer, new object[] { observation, second, "paused scan", pausedScan });
                Check(count == 1 && (string)lastUrl.GetValue(observation) == first.Page.Url, "suspension does not consume a newly observed URL");
                observer.InvalidatePendingObservations();
                observer.SetHistorySuspended(false);
                publish.Invoke(observer, new object[] { observation, second, "paused scan", pausedScan });
                Check(count == 1, "scan from suspension cannot publish after resume");
                publish.Invoke(observer, new object[] { observation, second, "fresh scan", observer.ObservationGeneration });
                Check(count == 2 && (string)lastUrl.GetValue(observation) == second.Page.Url, "fresh observation remains usable after resume");

                Action<string> invalidate = _ => observer.InvalidatePendingObservations();
                observer.StatusChanged += invalidate;
                var third = WechatPageObserver.SelectVisibleDocuments(new[] { Document("https://mp.weixin.qq.com/s?__biz=C&mid=3") });
                publish.Invoke(observer, new object[] { observation, third, "reentrant invalidation", observer.ObservationGeneration });
                observer.StatusChanged -= invalidate;
                Check(count == 2 && (string)lastUrl.GetValue(observation) == second.Page.Url, "invalidation inside status callback suppresses pending page");
            }
            finally
            {
                runField.SetValue(observer, null);
                cancellation.Dispose();
            }
        }
        private static WechatPageObserver.VisibleDocument Document(string url) => new WechatPageObserver.VisibleDocument { Url = url };
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("WechatPageObserver filtering test failed: " + message);
        }
    }
}
