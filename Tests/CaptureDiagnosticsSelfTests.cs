using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    public static class CaptureDiagnosticsSelfTests
    {
        public static async Task RunAsync()
        {
            CheckStateTransitions();
            CheckPrivacy();
            CheckInterfacePathsAndProxyExceptions();
            await CheckConcurrentCountsAsync();
            await CheckSubscriberIsolationAsync();
        }

        private static void CheckStateTransitions()
        {
            var time = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
            var diagnostics = new CaptureDiagnostics(20, () => time);
            var initial = diagnostics.Snapshot;
            Check(!initial.Listening && initial.UpstreamReachable == null && initial.LastFailure == CaptureFailureKind.None, "initial state");
            diagnostics.Start(8879);
            var started = diagnostics.Snapshot;
            Check(started.Listening && started.ListeningPort == 8879, "listening state");
            diagnostics.CheckNoRequests(TimeSpan.FromSeconds(10));
            Check(diagnostics.Snapshot.LastFailure == CaptureFailureKind.None, "premature no-requests failure");
            time += TimeSpan.FromSeconds(11);
            diagnostics.CheckNoRequests(TimeSpan.FromSeconds(10));
            diagnostics.CheckNoRequests(TimeSpan.FromSeconds(10));
            Check(diagnostics.Snapshot.LastFailure == CaptureFailureKind.NoRequests &&
                diagnostics.Snapshot.RecentEntries.Count(x => x.FailureKind == CaptureFailureKind.NoRequests) == 1, "missing or repeated no-requests diagnosis");
            diagnostics.SetUpstreamReachable(false);
            Check(diagnostics.Snapshot.UpstreamReachable == false && diagnostics.Snapshot.LastFailure == CaptureFailureKind.UpstreamUnreachable, "upstream failure state");
            diagnostics.SetUpstreamReachable(true);
            diagnostics.RecordConnection(false);
            diagnostics.RecordConnection(true);
            diagnostics.RecordMpTunnel();
            diagnostics.RecordDecryptedResponse(new Uri("https://mp.weixin.qq.com/s?__biz=fixture"), 200);
            Check(diagnostics.RecordRecognizedAccount("same-account"), "first account recognition");
            Check(!diagnostics.RecordRecognizedAccount("same-account"), "duplicate account recognition");
            Check(diagnostics.RecordRecognizedAccount("second-account"), "second account recognition");
            diagnostics.RecordFilteredResponse(new Uri("https://mp.weixin.qq.com/mp/getappmsgext"), 200, CaptureFailureKind.FilteredUrl);
            diagnostics.RecordFilteredResponse(new Uri("https://mp.weixin.qq.com/s"), 403, CaptureFailureKind.FilteredStatus);
            var active = diagnostics.Snapshot;
            Check(active.TotalConnections == 2 && active.WechatConnections == 1 && active.MpTunnels == 1 &&
                active.DecryptedResponses == 1 && active.RecognizedAccounts == 2 && active.FilteredResponses == 2, "capture pipeline counters");
            Check(active.LastFailure == CaptureFailureKind.FilteredStatus && active.LastFailureEntry.StatusCode == 403, "filtered status diagnosis");
            Check(!diagnostics.RecordRecognizedAccount(" ") && diagnostics.Snapshot.LastFailure == CaptureFailureKind.MissingBiz, "missing biz diagnosis");
            diagnostics.Stop();
            diagnostics.RecordConnection(true); diagnostics.RecordMpTunnel();
            diagnostics.RecordDecryptedResponse(new Uri("https://mp.weixin.qq.com/s"), 200);
            Check(!diagnostics.Snapshot.Listening && diagnostics.Snapshot.TotalConnections == 2 && diagnostics.Snapshot.MpTunnels == 1, "late callbacks changed stopped counters");
            Check(!initial.Listening && started.TotalConnections == 0 && started.RecentEntries.Count == 1, "snapshot changed after publication");
            var readonlyEntries = (IList<CaptureDiagnosticEntry>)active.RecentEntries;
            try { readonlyEntries.Clear(); throw new InvalidOperationException("snapshot log collection is mutable"); }
            catch (NotSupportedException) { }
            diagnostics.Start(8880);
            Check(diagnostics.Snapshot.ListeningPort == 8880 && diagnostics.Snapshot.TotalConnections == 0 &&
                diagnostics.Snapshot.RecognizedAccounts == 0 && diagnostics.Snapshot.LastFailure == CaptureFailureKind.None &&
                diagnostics.RecordRecognizedAccount("same-account"), "new session did not reset counters or account identity");
            diagnostics.RecordConnection(false);
            time += TimeSpan.FromMinutes(1);
            diagnostics.CheckNoRequests(TimeSpan.FromSeconds(10));
            Check(diagnostics.Snapshot.LastFailure == CaptureFailureKind.None, "no-requests diagnosis despite observed traffic");
        }

        private static void CheckPrivacy()
        {
            var diagnostics = new CaptureDiagnostics(8);
            var publicEvents = new List<CaptureDiagnosticEntry>();
            diagnostics.EntryAdded += publicEvents.Add;
            diagnostics.Start(8879);
            string query = "Cookie=cookie-secret&key=key-secret&uin=uin-secret&pass_ticket=ticket-secret&__biz=biz-secret&appmsg_token=token-secret";
            var uri = new Uri("https://user-secret:password-secret@mp.weixin.qq.com/s/article-secret?" + query + "#body-secret");
            diagnostics.RecordDecryptedResponse(uri, 200);
            diagnostics.RecordFailure(CaptureFailureKind.TlsHandshakeFailed, uri, 502,
                new AuthenticationException("Cookie: cookie-secret; " + query + " <body>body-secret</body>"));
            diagnostics.RecordFailure(CaptureFailureKind.ParseFailed, uri, 200, new HostileException());
            diagnostics.RecordRecognizedAccount("biz-secret");
            diagnostics.RecordFailure(CaptureFailureKind.FilteredUrl, new Uri("https://host-secret.example.invalid/path-secret?" + query));
            diagnostics.RecordFailure(CaptureFailureKind.FilteredUrl, new Uri("https://mp.weixin.qq.com/path-secret?" + query));
            string serialized = JsonSerializer.Serialize(diagnostics.Snapshot) + JsonSerializer.Serialize(publicEvents);
            foreach (string secret in new[] { "cookie-secret", "key-secret", "uin-secret", "ticket-secret", "biz-secret", "token-secret", "user-secret", "password-secret", "body-secret", "article-secret", "host-secret", "path-secret", query })
                Check(!serialized.Contains(secret, StringComparison.Ordinal), "sensitive value retained: " + secret.Split('-')[0]);
            Check(publicEvents.Any(x => x.ExceptionType == "AuthenticationException") && publicEvents.Any(x => x.ExceptionType == "Exception"), "exception classification missing");
            Check(CaptureDiagnostics.DescribeEndpoint(uri) == "mp.weixin.qq.com/s/{article}", "short article path was not templated");
            Check(CaptureDiagnostics.DescribeEndpoint(new Uri("https://mp.weixin.qq.com.evil.invalid/s?key=secret")).StartsWith("[其他主机]", StringComparison.Ordinal), "lookalike hostname accepted");
            Check(CaptureDiagnostics.DescribeEndpoint(new Uri("/s?key=secret", UriKind.Relative)) == "[无效地址]", "relative URL retention");
            diagnostics.RecordFailure(CaptureFailureKind.Unknown, statusCode: -123);
            Check(diagnostics.Snapshot.LastFailureEntry.StatusCode == null, "invalid status retained");
            for (int i = 0; i < 30; i++) diagnostics.RecordFailure(CaptureFailureKind.FilteredUrl, uri);
            Check(diagnostics.Snapshot.RecentEntries.Count == 8, "diagnostic history is not bounded");
        }

        private static void CheckInterfacePathsAndProxyExceptions()
        {
            const string query = "Cookie=cookie-secret&key=key-secret&uin=uin-secret&pass_ticket=ticket-secret&__biz=biz-secret";
            var diagnostics = new CaptureDiagnostics();
            diagnostics.Start(8879);
            foreach (string path in new[] { "/mp/appmsgcaptcha", "/mp/getappmsgext", "/mp/new_interface", "/mp/" + new string('a', 48) })
            {
                var uri = new Uri("https://mp.weixin.qq.com:443" + path + "?" + query + "#fragment-secret");
                Check(CaptureDiagnostics.DescribeEndpoint(uri) == "mp.weixin.qq.com" + path, "standard interface name was hidden");
                diagnostics.RecordFilteredResponse(uri, 200, CaptureFailureKind.FilteredUrl);
            }
            Check(CaptureDiagnostics.DescribeEndpoint(new Uri("http://mp.weixin.qq.com:80/mp/appmsgcaptcha")) == "mp.weixin.qq.com/mp/appmsgcaptcha", "default HTTP port rejected");
            foreach (string path in new[] { "/mp/", "/mp/" + new string('a', 49), "/mp/AppMsg", "/mp/appmsg123", "/mp/appmsg-token", "/mp/appmsg/token-secret", "/mp/appmsg%0A", "/token-secret" })
                Check(CaptureDiagnostics.DescribeEndpoint(new Uri("https://mp.weixin.qq.com" + path + "?" + query)) == "mp.weixin.qq.com/[路径已省略]", "sensitive or nonstandard path retained");
            Check(CaptureDiagnostics.DescribeEndpoint(new Uri("https://mp.weixin.qq.com:8443/mp/appmsgcaptcha?" + query)) == "mp.weixin.qq.com/[路径已省略]", "nondefault port accepted");
            string randomArticle = Guid.NewGuid().ToString("N");
            Check(CaptureDiagnostics.DescribeEndpoint(new Uri("https://mp.weixin.qq.com/s/" + randomArticle + "?" + query)) == "mp.weixin.qq.com/s/{article}", "random article identifier retained");

            var assembly = typeof(Titanium.Web.Proxy.ProxyServer).Assembly;
            foreach (string name in new[] { "ProxyHttpException", "ProxyConnectException", "ProxyAuthorizationException", "BodyNotFoundException", "RetryableServerConnectionException", "ProxyException" })
            {
                // Internal constructors require live proxy sessions. Allocating only the exception
                // shell tests its actual type without constructing sessions or opening connections.
                var type = assembly.GetType("Titanium.Web.Proxy.Exceptions." + name, true);
                if (type.IsAbstract) { Check(name == "ProxyException", "unexpected abstract proxy exception"); continue; }
                var error = (Exception)RuntimeHelpers.GetUninitializedObject(type);
                diagnostics.RecordFailure(CaptureFailureKind.Unknown, exception: error);
                Check(diagnostics.Snapshot.LastFailureEntry.ExceptionType == name, "known proxy exception collapsed");
            }
            var socketError = (Exception)Activator.CreateInstance(assembly.GetType("Titanium.Web.Proxy.ProxySocket.ProxyException", true), "key-secret; " + query);
            diagnostics.RecordFailure(CaptureFailureKind.Unknown, exception: socketError);
            Check(diagnostics.Snapshot.LastFailureEntry.ExceptionType == "ProxySocket.ProxyException", "socket proxy exception collapsed");
            diagnostics.RecordFailure(CaptureFailureKind.Unknown, exception: new ProxyHttpException());
            Check(diagnostics.Snapshot.LastFailureEntry.ExceptionType == "Exception", "arbitrary exception matched by short name");
            string serialized = JsonSerializer.Serialize(diagnostics.Snapshot);
            foreach (string secret in new[] { "cookie-secret", "key-secret", "uin-secret", "ticket-secret", "biz-secret", "fragment-secret", randomArticle, query })
                Check(!serialized.Contains(secret, StringComparison.Ordinal), "interface diagnostics retained sensitive value");
        }

        private static async Task CheckConcurrentCountsAsync()
        {
            var diagnostics = new CaptureDiagnostics(17);
            diagnostics.Start(8879);
            var errors = new ConcurrentQueue<string>();
            long lastVersion = 0;
            int simultaneousCallbacks = 0;
            diagnostics.Changed += snapshot =>
            {
                if (Interlocked.Increment(ref simultaneousCallbacks) != 1) errors.Enqueue("overlapping callbacks");
                if (snapshot.Version <= Interlocked.Exchange(ref lastVersion, snapshot.Version)) errors.Enqueue("out-of-order snapshot");
                if (diagnostics.Snapshot.Version < snapshot.Version) errors.Enqueue("snapshot is not current");
                if (snapshot.WechatConnections > snapshot.TotalConnections || snapshot.MpTunnels > snapshot.WechatConnections || snapshot.DecryptedResponses > snapshot.MpTunnels)
                    errors.Enqueue("inconsistent pipeline snapshot");
                Interlocked.Decrement(ref simultaneousCallbacks);
            };
            var uri = new Uri("https://mp.weixin.qq.com/s?key=never-log");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (int i = 0; i < 500; i++)
                {
                    diagnostics.RecordConnection(true); diagnostics.RecordMpTunnel(); diagnostics.RecordDecryptedResponse(uri, 200);
                    diagnostics.RecordRecognizedAccount("account-" + (i % 37));
                    if (i % 25 == 0) diagnostics.RecordFailure(CaptureFailureKind.MissingBiz, uri);
                }
            })));
            var final = diagnostics.Snapshot;
            Check(final.TotalConnections == 4000 && final.WechatConnections == 4000 && final.MpTunnels == 4000 &&
                final.DecryptedResponses == 4000 && final.RecognizedAccounts == 37, "concurrent updates lost or duplicate accounts counted");
            Check(final.RecentEntries.Count == 17 && errors.IsEmpty, "concurrent snapshot/event consistency: " + string.Join(",", errors));
        }

        private static async Task CheckSubscriberIsolationAsync()
        {
            var diagnostics = new CaptureDiagnostics();
            diagnostics.Start(8879);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new ManualResetEventSlim())
            {
                bool waited = false;
                int received = 0;
                diagnostics.Changed += _ => throw new InvalidOperationException("subscriber failure");
                diagnostics.Changed += snapshot =>
                {
                    received++;
                    var current = diagnostics.Snapshot;
                    if (!waited)
                    {
                        waited = true; entered.TrySetResult(true);
                        release.Wait(TimeSpan.FromSeconds(5));
                        diagnostics.RecordMpTunnel(); // Reentrant updates are queued, never recursively dispatched.
                    }
                };
                var first = Task.Run(() => diagnostics.RecordConnection(true));
                Check(await Task.WhenAny(entered.Task, Task.Delay(2000)) == entered.Task, "event subscriber did not run");
                try
                {
                    var other = Task.Run(() =>
                    {
                        for (int i = 0; i < 100; i++) diagnostics.RecordConnection(true);
                    });
                    Check(await Task.WhenAny(other, Task.Delay(2000)) == other, "slow event subscriber held the state lock");
                    await other;
                    Check(diagnostics.Snapshot.TotalConnections == 101, "state unavailable while subscriber waited");
                }
                finally { release.Set(); }
                await first;
                Check(received >= 2 && diagnostics.Snapshot.MpTunnels == 1, "throwing or reentrant subscriber interrupted updates");
            }
        }

        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Capture diagnostics: " + message); }
        private sealed class HostileException : Exception
        {
            public override string Message => throw new InvalidOperationException("Diagnostics must not read exception messages.");
        }
        private sealed class ProxyHttpException : Exception { }
    }
}
