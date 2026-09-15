using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WCAE
{
    public enum CaptureFailureKind
    {
        None, NoRequests, UpstreamUnreachable, TlsHandshakeFailed, FilteredUrl,
        FilteredStatus, MissingBiz, EmptyBody, BodyTooLarge, ParseFailed,
        ListenFailed, ProxyRestoreFailed, Unknown
    }

    public enum CaptureDiagnosticKind
    {
        ListeningStarted, ListeningStopped, UpstreamStatus, ConnectionObserved,
        MpTunnelObserved, ResponseObserved, AccountRecognized, Failure
    }

    public sealed class CaptureDiagnosticEntry
    {
        public long Sequence { get; }
        public DateTimeOffset TimestampUtc { get; }
        public CaptureDiagnosticKind Kind { get; }
        public CaptureFailureKind FailureKind { get; }
        public string Endpoint { get; }
        public int? StatusCode { get; }
        public string ExceptionType { get; }
        public string Message { get; }

        internal CaptureDiagnosticEntry(long sequence, DateTimeOffset time, CaptureDiagnosticKind kind,
            CaptureFailureKind failure, string endpoint, int? status, string exceptionType, string message)
        {
            Sequence = sequence; TimestampUtc = time; Kind = kind; FailureKind = failure;
            Endpoint = endpoint; StatusCode = status; ExceptionType = exceptionType; Message = message;
        }
    }

    public sealed class CaptureDiagnosticsSnapshot
    {
        public long Version { get; }
        public bool Listening { get; }
        public int ListeningPort { get; }
        public bool? UpstreamReachable { get; }
        public long TotalConnections { get; }
        public long WechatConnections { get; }
        public long MpTunnels { get; }
        public long DecryptedResponses { get; }
        public long RecognizedAccounts { get; }
        public long FilteredResponses { get; }
        public DateTimeOffset? StartedUtc { get; }
        public DateTimeOffset UpdatedUtc { get; }
        public CaptureFailureKind LastFailure => LastFailureEntry?.FailureKind ?? CaptureFailureKind.None;
        public CaptureDiagnosticEntry LastFailureEntry { get; }
        public IReadOnlyList<CaptureDiagnosticEntry> RecentEntries { get; }
        public bool HasObservedTraffic => TotalConnections != 0 || MpTunnels != 0 || DecryptedResponses != 0;

        internal CaptureDiagnosticsSnapshot(long version, bool listening, int port, bool? upstream,
            long connections, long wechat, long tunnels, long responses, long accounts, long filtered,
            DateTimeOffset? started, DateTimeOffset updated, CaptureDiagnosticEntry lastFailure, CaptureDiagnosticEntry[] entries)
        {
            Version = version; Listening = listening; ListeningPort = port; UpstreamReachable = upstream;
            TotalConnections = connections; WechatConnections = wechat; MpTunnels = tunnels;
            DecryptedResponses = responses; RecognizedAccounts = accounts; FilteredResponses = filtered;
            StartedUtc = started; UpdatedUtc = updated; LastFailureEntry = lastFailure;
            RecentEntries = new ReadOnlyCollection<CaptureDiagnosticEntry>(entries);
        }
    }

    /// <summary>
    /// In-memory, bounded diagnostics. No request headers, response bodies, query strings,
    /// account identifiers or exception messages are retained. Events run outside the state lock;
    /// concurrent changes are coalesced and delivered in increasing snapshot-version order.
    /// </summary>
    public sealed class CaptureDiagnostics
    {
        private readonly object gate = new object();
        private readonly int capacity;
        private readonly Func<DateTimeOffset> utcNow;
        private readonly Queue<CaptureDiagnosticEntry> entries = new Queue<CaptureDiagnosticEntry>();
        private readonly Queue<CaptureDiagnosticEntry> pendingEntries = new Queue<CaptureDiagnosticEntry>();
        private readonly HashSet<string> accountHashes = new HashSet<string>(StringComparer.Ordinal);
        private bool listening, notifying, pendingNotification, noRequestsReported;
        private int listeningPort;
        private bool? upstreamReachable;
        private long version, sequence, connections, wechat, tunnels, responses, filtered;
        private DateTimeOffset? started;
        private DateTimeOffset updated;
        private CaptureDiagnosticEntry lastFailure;

        public CaptureDiagnostics(int logCapacity = 100, Func<DateTimeOffset> utcNow = null)
        {
            if (logCapacity < 1 || logCapacity > 1000) throw new ArgumentOutOfRangeException(nameof(logCapacity));
            capacity = logCapacity;
            this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            updated = this.utcNow().ToUniversalTime();
        }

        public event Action<CaptureDiagnosticsSnapshot> Changed;
        public event Action<CaptureDiagnosticEntry> EntryAdded;
        public CaptureDiagnosticsSnapshot Snapshot { get { lock (gate) return SnapshotLocked(); } }

        public void Start(int port)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            Change(() =>
            {
                if (listening) return false;
                listening = true; listeningPort = port; upstreamReachable = null;
                connections = wechat = tunnels = responses = filtered = 0;
                accountHashes.Clear(); entries.Clear(); pendingEntries.Clear(); lastFailure = null;
                noRequestsReported = false; started = Now();
                Add(CaptureDiagnosticKind.ListeningStarted, message: "监听已开启，等待请求。");
                return true;
            });
        }

        public void Stop()
        {
            Change(() =>
            {
                if (!listening) return false;
                listening = false; listeningPort = 0; upstreamReachable = null;
                Add(CaptureDiagnosticKind.ListeningStopped, message: "监听已停止。");
                return true;
            });
        }

        public void SetUpstreamReachable(bool? reachable)
        {
            Change(() =>
            {
                if (upstreamReachable == reachable) return false;
                upstreamReachable = reachable;
                if (reachable == false) AddFailure(CaptureFailureKind.UpstreamUnreachable);
                else Add(CaptureDiagnosticKind.UpstreamStatus, message: reachable == true ? "上游连接可达。" : "上游连接尚未检测。");
                return true;
            });
        }

        // RecordConnection and RecordMpTunnel are separate: a single MP CONNECT increments both.
        public void RecordConnection(bool isWechat)
        {
            Change(() =>
            {
                if (!listening) return false;
                connections++;
                if (isWechat) wechat++;
                if (connections == 1 || isWechat && wechat == 1)
                    Add(CaptureDiagnosticKind.ConnectionObserved, message: isWechat ? "已观察到微信连接。" : "已观察到代理连接。");
                return true;
            });
        }

        public void RecordMpTunnel()
        {
            Change(() =>
            {
                if (!listening) return false;
                tunnels++;
                if (tunnels == 1) Add(CaptureDiagnosticKind.MpTunnelObserved, endpoint: "mp.weixin.qq.com", message: "已观察到公众号 HTTPS 隧道。");
                return true;
            });
        }

        public void RecordDecryptedResponse(Uri uri, int statusCode)
        {
            string endpoint = DescribeEndpoint(uri);
            int? status = ValidStatus(statusCode);
            Change(() =>
            {
                if (!listening) return false;
                responses++;
                if (responses == 1) Add(CaptureDiagnosticKind.ResponseObserved, endpoint: endpoint, status: status, message: "已收到解密后的公众号响应。");
                return true;
            });
        }

        public void RecordFilteredResponse(Uri uri, int statusCode, CaptureFailureKind reason)
        {
            if (reason != CaptureFailureKind.FilteredUrl && reason != CaptureFailureKind.FilteredStatus)
                throw new ArgumentOutOfRangeException(nameof(reason));
            string endpoint = DescribeEndpoint(uri);
            int? status = ValidStatus(statusCode);
            Change(() =>
            {
                if (!listening) return false;
                filtered++;
                AddFailure(reason, endpoint, status);
                return true;
            });
        }

        public bool RecordRecognizedAccount(string biz)
        {
            if (string.IsNullOrWhiteSpace(biz)) { RecordFailure(CaptureFailureKind.MissingBiz); return false; }
            string digest;
            using (var hash = SHA256.Create()) digest = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(biz)));
            bool added = false;
            Change(() =>
            {
                if (!listening || !accountHashes.Add(digest)) return false;
                added = true;
                Add(CaptureDiagnosticKind.AccountRecognized, message: "已识别公众号，账户计数已更新。");
                return true;
            });
            return added;
        }

        public void RecordFailure(CaptureFailureKind kind, Uri uri = null, int? statusCode = null, Exception exception = null)
        {
            if (kind == CaptureFailureKind.None || !Enum.IsDefined(typeof(CaptureFailureKind), kind)) kind = CaptureFailureKind.Unknown;
            string endpoint = DescribeEndpoint(uri), exceptionType = DescribeException(exception);
            int? status = ValidStatus(statusCode);
            Change(() => { AddFailure(kind, endpoint, status, exceptionType); return true; });
        }

        // The caller supplies the timer; this object never opens sockets or starts background work.
        public void CheckNoRequests(TimeSpan waitingPeriod)
        {
            if (waitingPeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(waitingPeriod));
            Change(() =>
            {
                if (!listening || noRequestsReported || !started.HasValue || Now() - started.Value < waitingPeriod ||
                    connections != 0 || tunnels != 0 || responses != 0) return false;
                noRequestsReported = true;
                AddFailure(CaptureFailureKind.NoRequests);
                return true;
            });
        }

        public static string DescribeEndpoint(Uri uri)
        {
            if (uri == null) return "";
            if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return "[无效地址]";
            if (!string.Equals(uri.Host, "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase))
                return uri.Host.EndsWith(".weixin.qq.com", StringComparison.OrdinalIgnoreCase) ? "*.weixin.qq.com/[路径已省略]" : "[其他主机]/[路径已省略]";
            if (!uri.IsDefaultPort) return "mp.weixin.qq.com/[路径已省略]";
            string path = uri.AbsolutePath;
            if (path == "/s") return "mp.weixin.qq.com/s";
            if (path.StartsWith("/s/", StringComparison.Ordinal)) return "mp.weixin.qq.com/s/{article}";
            // Only simple interface names are useful here; query strings, fragments,
            // article identifiers and arbitrary token-bearing paths stay excluded.
            if (path == "/" || Regex.IsMatch(path, @"\A/mp/[a-z_]{1,48}\z", RegexOptions.CultureInvariant))
                return "mp.weixin.qq.com" + path;
            return "mp.weixin.qq.com/[路径已省略]";
        }

        private static int? ValidStatus(int? status) => status >= 100 && status <= 599 ? status : null;
        private static string DescribeException(Exception error)
        {
            if (error == null) return "";
            // Fixed labels for types verified in Titanium.Web.Proxy 3.1.1397.
            // Do not expose arbitrary class names, exception messages or session data.
            switch (error.GetType().FullName)
            {
                case "Titanium.Web.Proxy.Exceptions.ProxyHttpException": return "ProxyHttpException";
                case "Titanium.Web.Proxy.Exceptions.ProxyConnectException": return "ProxyConnectException";
                case "Titanium.Web.Proxy.Exceptions.ProxyAuthorizationException": return "ProxyAuthorizationException";
                case "Titanium.Web.Proxy.Exceptions.BodyNotFoundException": return "BodyNotFoundException";
                case "Titanium.Web.Proxy.Exceptions.RetryableServerConnectionException": return "RetryableServerConnectionException";
                case "Titanium.Web.Proxy.Exceptions.ProxyException": return "ProxyException";
                case "Titanium.Web.Proxy.ProxySocket.ProxyException": return "ProxySocket.ProxyException";
            }
            if (error is AuthenticationException) return "AuthenticationException";
            if (error is SocketException socket) return "SocketException/" + socket.SocketErrorCode;
            if (error is WebException web) return "WebException/" + web.Status;
            if (error is HttpRequestException) return "HttpRequestException";
            if (error is OperationCanceledException) return "OperationCanceledException";
            if (error is CryptographicException) return "CryptographicException";
            if (error is IOException) return "IOException";
            if (error is InvalidOperationException) return "InvalidOperationException";
            return "Exception";
        }

        private DateTimeOffset Now() => utcNow().ToUniversalTime();
        private void AddFailure(CaptureFailureKind kind, string endpoint = "", int? status = null, string exceptionType = "")
        {
            string message;
            switch (kind)
            {
                case CaptureFailureKind.NoRequests: message = "监听已开启，但尚未收到请求。"; break;
                case CaptureFailureKind.UpstreamUnreachable: message = "上游连接检测失败。"; break;
                case CaptureFailureKind.TlsHandshakeFailed: message = "TLS 握手失败，尚未取得可解析的页面响应。"; break;
                case CaptureFailureKind.FilteredUrl: message = "该响应地址不属于公众号识别页面，已忽略。"; break;
                case CaptureFailureKind.FilteredStatus: message = "该响应 HTTP 状态不符合识别条件，已忽略。"; break;
                case CaptureFailureKind.MissingBiz: message = "已读取页面，但没有识别到公众号标识。"; break;
                case CaptureFailureKind.EmptyBody: message = "响应正文为空。"; break;
                case CaptureFailureKind.BodyTooLarge: message = "响应正文超过识别大小限制。"; break;
                case CaptureFailureKind.ParseFailed: message = "公众号页面解析失败。"; break;
                case CaptureFailureKind.ListenFailed: message = "监听启动失败。"; break;
                case CaptureFailureKind.ProxyRestoreFailed: message = "系统代理恢复失败。"; break;
                default: message = "监听处理发生异常。"; break;
            }
            lastFailure = Add(CaptureDiagnosticKind.Failure, kind, endpoint, status, exceptionType, message);
        }

        private CaptureDiagnosticEntry Add(CaptureDiagnosticKind kind, CaptureFailureKind failure = CaptureFailureKind.None,
            string endpoint = "", int? status = null, string exceptionType = "", string message = "")
        {
            var entry = new CaptureDiagnosticEntry(++sequence, Now(), kind, failure, endpoint, status, exceptionType, message);
            entries.Enqueue(entry); pendingEntries.Enqueue(entry);
            while (entries.Count > capacity) entries.Dequeue();
            while (pendingEntries.Count > capacity) pendingEntries.Dequeue();
            return entry;
        }

        private CaptureDiagnosticsSnapshot SnapshotLocked() => new CaptureDiagnosticsSnapshot(version, listening, listeningPort,
            upstreamReachable, connections, wechat, tunnels, responses, accountHashes.Count, filtered, started, updated, lastFailure, entries.ToArray());

        private void Change(Func<bool> mutation)
        {
            bool dispatch;
            lock (gate)
            {
                if (!mutation()) return;
                version++; updated = Now(); pendingNotification = true;
                dispatch = !notifying;
                if (dispatch) notifying = true;
            }
            if (dispatch) Dispatch();
        }

        private void Dispatch()
        {
            while (true)
            {
                CaptureDiagnosticsSnapshot snapshot;
                CaptureDiagnosticEntry[] batch;
                lock (gate)
                {
                    if (!pendingNotification) { notifying = false; return; }
                    pendingNotification = false;
                    snapshot = SnapshotLocked(); batch = pendingEntries.ToArray(); pendingEntries.Clear();
                }
                Notify(Changed, snapshot);
                foreach (var entry in batch) Notify(EntryAdded, entry);
            }
        }

        private static void Notify<T>(Action<T> listeners, T value)
        {
            if (listeners == null) return;
            foreach (Action<T> listener in listeners.GetInvocationList())
                try { listener(value); } catch { /* Diagnostic subscribers must not interrupt capture. */ }
        }
    }
}
