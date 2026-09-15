using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy;

namespace WCAE
{
    public static class CaptureCacheSelfTests
    {
        static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static FieldInfo Field(string name) => typeof(CaptureService).GetField(name, PrivateInstance);
        static long Generation(CaptureService capture) => (long)Field("sessionGeneration").GetValue(capture);
        static Dictionary<string, AccountSession> Sessions(CaptureService capture) => (Dictionary<string, AccountSession>)Field("sessions").GetValue(capture);
        static bool Publish(CaptureService capture, AccountSession session, long generation, bool select = true)
            => (bool)typeof(CaptureService).GetMethod("PublishObservedSession", PrivateInstance).Invoke(capture,
                new object[] { session, select, generation, CancellationToken.None, false });

        public static async Task RunAsync()
        {
            // Use an unstarted proxy object only to exercise passive callbacks. No listener,
            // route, certificate, WeChat process, or real user history is opened by this test.
            using (var capture = new CaptureService())
            using (var proxy = new ProxyServer(false, false, false))
            using (var cancellation = new CancellationTokenSource())
            {
                Field("proxy").SetValue(capture, proxy);
                Field("captureCancellation").SetValue(capture, cancellation);
                Field("routeActive").SetValue(capture, true);
                try
                {
                    int detected = 0, updated = 0;
                    capture.AccountDetected += _ => detected++;
                    capture.SessionUpdated += _ => updated++;
                    var account = new AccountSession { Biz = "cache-test", Name = "隔离缓存测试号", Cookie = "test-only=old" };
                    long oldGeneration = Generation(capture);
                    capture.SeedKnownSessions(new[] { account });
                    Check(Publish(capture, account, oldGeneration) && detected == 1, "缓存测试识别事件未建立");
                    var bodies = Field("requestBodies").GetValue(capture);
                    var requestKey = new object();
                    bodies.GetType().GetMethod("GetOrCreateValue").Invoke(bodies, new[] { requestKey });
                    capture.ClearKnownSessions();
                    Check(Sessions(capture).Count == 0 && Field("lastDetectedBiz").GetValue(capture) == null, "清空后内存公众号或当前标识仍保留");
                    var bodyArguments = new object[] { requestKey, null };
                    Check(!(bool)bodies.GetType().GetMethod("TryGetValue").Invoke(bodies, bodyArguments), "清空后旧请求仍可进入响应识别");
                    Check(ReferenceEquals(Field("proxy").GetValue(capture), proxy)
                        && ReferenceEquals(Field("captureCancellation").GetValue(capture), cancellation)
                        && !cancellation.IsCancellationRequested && capture.RouteActive, "清空历史影响了监听对象、取消令牌或接入状态");

                    Check(!Publish(capture, account, oldGeneration) && !Publish(capture, account, oldGeneration, false), "清空前的响应仍可发布识别或会话更新");
                    typeof(CaptureService).GetMethod("ObserveVisibleDocument", PrivateInstance).Invoke(capture, new object[]
                    {
                        new WechatObservedPage { Url = "https://mp.weixin.qq.com/s?__biz=cache-test&mid=1&idx=1&sn=test", AccountName = "清空前排队页面" }, oldGeneration
                    });
                    Check(Sessions(capture).Count == 0 && detected == 1 && updated == 0, "清空前排队的解析结果恢复了公众号记录");
                    Check(Publish(capture, account, Generation(capture)) && detected == 2 && Sessions(capture).Count == 1, "清空后新的识别请求不能继续使用");

                    // A clear waits for an already-publishing callback; after it returns that
                    // callback cannot restore sessions. UI consumers independently reject queued UI work.
                    using var entered = new ManualResetEventSlim();
                    using var release = new ManualResetEventSlim();
                    Action<AccountSession> block = _ =>
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("清空与发布并发测试超时");
                    };
                    capture.AccountDetected += block;
                    long raceGeneration = Generation(capture);
                    var publication = Task.Run(() => Publish(capture, new AccountSession { Biz = "race", Name = "并发识别测试" }, raceGeneration));
                    Check(entered.Wait(TimeSpan.FromSeconds(5)), "并发识别未进入发布边界");
                    using var clearStarted = new ManualResetEventSlim();
                    var clear = Task.Run(() => { clearStarted.Set(); capture.ClearKnownSessions(); });
                    try
                    {
                        Check(clearStarted.Wait(TimeSpan.FromSeconds(5)), "并发清空未开始");
                        Check(!clear.IsCompleted, "清空跨过了仍在发布的旧事件");
                    }
                    finally { release.Set(); }
                    await Task.WhenAll(publication, clear);
                    capture.AccountDetected -= block;
                    Check(Sessions(capture).Count == 0 && !Publish(capture, account, raceGeneration), "清空返回后旧识别复活");

                    // A status subscriber may synchronously clear while publication holds a
                    // reentrant Monitor. Rechecking before each account subscriber avoids revival.
                    int beforeReentrant = detected;
                    Action<string> clearFromStatus = _ => capture.ClearKnownSessions();
                    capture.StatusChanged += clearFromStatus;
                    Check(!Publish(capture, account, Generation(capture)), "重入清空后仍报告发布成功");
                    capture.StatusChanged -= clearFromStatus;
                    Check(detected == beforeReentrant && Sessions(capture).Count == 0, "状态回调中的清空没有拦住旧公众号事件");

                    var observer = (WechatPageObserver)Field("pageObserver").GetValue(capture);
                    long staleObservation = observer.ObservationGeneration;
                    capture.ClearKnownSessions();
                    typeof(CaptureService).GetMethod("ObserveVisibleDocument", PrivateInstance).Invoke(capture, new object[]
                    {
                        new WechatObservedPage
                        {
                            Url = "https://mp.weixin.qq.com/s?__biz=cache-test&mid=1&idx=1&sn=test",
                            AccountName = "清空前开始扫描", ObservationGeneration = staleObservation
                        }, Generation(capture)
                    });
                    Check(detected == beforeReentrant && Sessions(capture).Count == 0, "清空前开始的 UIA 扫描借新会话代次恢复了旧公众号");

                    var suspension = capture.SuspendHistory();
                    long pausedGeneration = Generation(capture);
                    long pausedObservation = observer.ObservationGeneration;
                    Check(!Publish(capture, account, pausedGeneration) && !Publish(capture, account, pausedGeneration, false), "清理期间仍发布公众号识别或会话更新");
                    capture.SeedKnownSessions(new[] { account });
                    Check(Sessions(capture).Count == 0, "清理期间重新填入了历史会话");
                    Check(ReferenceEquals(Field("proxy").GetValue(capture), proxy)
                        && !cancellation.IsCancellationRequested && capture.RouteActive, "暂停历史修改了网络接入");
                    var nestedSuspension = capture.SuspendHistory();
                    suspension.Dispose();
                    Check(!Publish(capture, account, Generation(capture)), "嵌套清理提前恢复了识别");
                    nestedSuspension.Dispose();
                    long resumedGeneration = Generation(capture);
                    suspension.Dispose(); nestedSuspension.Dispose();
                    Check(Generation(capture) == resumedGeneration && !Publish(capture, account, pausedGeneration), "重复释放暂停作用域或暂停期间旧结果改变了恢复状态");
                    typeof(CaptureService).GetMethod("ObserveVisibleDocument", PrivateInstance).Invoke(capture, new object[]
                    {
                        new WechatObservedPage
                        {
                            Url = "https://mp.weixin.qq.com/s?__biz=cache-test&mid=2&idx=1&sn=test",
                            AccountName = "暂停期间开始扫描", ObservationGeneration = pausedObservation
                        }, resumedGeneration
                    });
                    Check(Sessions(capture).Count == 0, "暂停期间开始的旧扫描在恢复后重新写入");
                    Check(Publish(capture, account, resumedGeneration) && Sessions(capture).Count == 1, "清理恢复后相同公众号的新识别被旧去重缓存吞掉");
                }
                finally
                {
                    // The test never started these objects; avoid pretending to stop a route.
                    Field("proxy").SetValue(capture, null);
                    Field("captureCancellation").SetValue(capture, null);
                    Field("routeActive").SetValue(capture, false);
                }
            }

            int runs = 0;
            using (var controller = new CollectionController(async (session, save, progress, token) =>
            {
                runs++;
                await Task.Delay(Timeout.Infinite, token);
            }, (article, session, token) => Task.FromResult(article), _ => { }))
            {
                controller.Recognize(new AccountSession { Biz = "old", Name = "旧公众号" });
                var collection = controller.StartAsync(null);
                bool rejectedWhileRunning = false;
                try { controller.ClearRecognition(); } catch (InvalidOperationException) { rejectedWhileRunning = true; }
                Check(rejectedWhileRunning, "运行中清空识别没有要求等待任务退出");
                controller.Stop();
                try { await collection; } catch (OperationCanceledException) { }
                controller.ClearRecognition();
                Check(!controller.IsRunning && controller.CollectingAccount == "", "清空后仍显示旧采集账号");
                bool rejectedOldStart = false;
                try { await controller.StartAsync(null); } catch (InvalidOperationException) { rejectedOldStart = true; }
                Check(rejectedOldStart && runs == 1, "清空后仍能使用旧识别会话开始采集");
                controller.Recognize(new AccountSession { Biz = "new", Name = "新公众号" });
                var next = controller.StartAsync(null);
                Check(controller.IsRunning && controller.CollectingAccount == "新公众号" && runs == 2, "清空后新识别不能正常采集");
                controller.Stop();
                try { await next; } catch (OperationCanceledException) { }
            }
        }
    }
}
