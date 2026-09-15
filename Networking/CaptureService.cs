using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace WCAE
{
    /// <summary>Only observes article traffic; starting capture never starts collection.</summary>
    public sealed class CaptureService : IDisposable
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, AccountSession> sessions = new Dictionary<string, AccountSession>(StringComparer.Ordinal);
        private readonly EventHandler processExit;
        private ProxyServer proxy;
        private ExplicitProxyEndPoint endpoint;
        private CancellationTokenSource captureCancellation;
        private CaptureRoute route;
        private ProxyEndpoint upstream;
        private CaptureIngressMode ingressMode;
        private readonly SemaphoreSlim routeGate = new SemaphoreSlim(1, 1);
        private FileStream leaseLock;
        private bool disposed;
        private string leasePath;
        private string lastDetectedBiz;
        private long sessionGeneration;
        private int historySuspensions;
        private bool routeActive;
        private readonly object logGate = new object();
        private readonly ConditionalWeakTable<object, CapturedRequest> requestBodies = new ConditionalWeakTable<object, CapturedRequest>();
        private readonly object authenticatedWechatRoute = new object();
        private readonly WechatPageObserver pageObserver = new WechatPageObserver();
        private readonly HashSet<Guid> authenticatedConnections = new HashSet<Guid>();
        private volatile bool cancelPending;
        private sealed class CapturedRequest { public string Body = ""; public long Generation; }
        public int PreferredPort { get; set; } = 8879;
        public int UpstreamPort => upstream?.Port ?? 0;
        public int ListeningPort { get; private set; }
        public bool RouteActive => routeActive;
        public CaptureDiagnostics Diagnostics { get; } = new CaptureDiagnostics();
        public bool IsRunning { get { lock (gate) return proxy?.ProxyRunning == true; } }
        public event Action<AccountSession> AccountDetected;
        public event Action<AccountSession> SessionUpdated;
        public event Action<string> StatusChanged;

        public void Configure(ProxyEndpoint endpoint, CaptureIngressMode mode)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(CaptureService));
                if (proxy != null) throw new InvalidOperationException("请先停止监听再修改接入设置。");
                var selected = endpoint?.Clone(); selected?.Validate(PreferredPort);
                if (!Enum.IsDefined(mode)) throw new ArgumentException("接入方式无效。");
                upstream = selected; ingressMode = mode;
            }
        }
        public Task ReconfigureAsync(ProxyEndpoint endpoint, CaptureIngressMode mode, CancellationToken token)
        {
            captureCancellation?.Cancel();
            return Task.Run(() =>
            {
                Stop(); token.ThrowIfCancellationRequested(); Configure(endpoint, mode);
                using var cancellation = token.Register(() => captureCancellation?.Cancel());
                Start();
            }, token);
        }

        public void SeedKnownSessions(IEnumerable<AccountSession> known)
        {
            lock (gate)
            {
                if (historySuspensions != 0) return;
                foreach (var session in known ?? Enumerable.Empty<AccountSession>())
                    if (session != null && !string.IsNullOrWhiteSpace(session.Biz)) sessions[session.Biz] = session.Clone();
            }
        }

        public void ClearKnownSessions()
        {
            lock (gate)
            {
                // Invalidate queued page observations and responses from requests made before
                // the clear, without changing the listener, route, or observer's URL dedupe.
                Interlocked.Increment(ref sessionGeneration);
                pageObserver.InvalidatePendingObservations();
                sessions.Clear();
                lastDetectedBiz = null;
                requestBodies.Clear();
            }
        }

        public void UpdateKnownAccountName(string biz, string name)
        {
            lock (gate)
            {
                if (historySuspensions != 0 || string.IsNullOrEmpty(biz)) return;
                if (sessions.TryGetValue(biz, out var known))
                    known.Name = AccountNameResolver.Choose(biz, name, known.Name);
            }
        }

        public IDisposable SuspendHistory()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(CaptureService));
                historySuspensions++;
                pageObserver.SetHistorySuspended(true);
                ClearKnownSessions();
                return new HistorySuspension(this);
            }
        }

        private void ResumeHistory()
        {
            lock (gate)
            {
                if (historySuspensions == 0) return;
                ClearKnownSessions();
                // Clear while still paused. New callbacks can enter only after the whole
                // persistence/UI clear and this final generation change have completed.
                historySuspensions--;
                if (historySuspensions == 0) pageObserver.SetHistorySuspended(false);
            }
        }

        private sealed class HistorySuspension : IDisposable
        {
            private CaptureService owner;
            public HistorySuspension(CaptureService owner) { this.owner = owner; }
            public void Dispose() => Interlocked.Exchange(ref owner, null)?.ResumeHistory();
        }

        public CaptureService()
        {
            processExit = (s, e) => { try { Stop(); } catch { /* Stop reports a recovery failure through StatusChanged. */ } };
            AppDomain.CurrentDomain.ProcessExit += processExit;
            Diagnostics.EntryAdded += WriteDiagnostic;
            pageObserver.StatusChanged += Notify;
            pageObserver.DocumentObserved += page =>
            {
                // This callback runs under the observer's gate. Do not acquire our gate here:
                // shutdown takes our gate before stopping the observer.
                long generation = Interlocked.Read(ref sessionGeneration);
                _ = Task.Run(() => ObserveVisibleDocument(page, generation));
            };
        }

        public void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(CaptureService));
                if (cancelPending) throw new OperationCanceledException();
                if (proxy?.ProxyRunning == true) return;
                Directory.CreateDirectory(AppPaths.DataDirectory);
                leasePath = Path.Combine(AppPaths.DataDirectory, "capture-proxy-lease.json");
                try
                {
                    try { leaseLock = new FileStream(Path.Combine(AppPaths.DataDirectory, "capture-proxy.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException ex) { throw new InvalidOperationException("已有一个工具实例正在使用系统代理监听，请先停止该实例。", ex); }
                    RecoverPreviousLease();
                    lastDetectedBiz = null;
                    captureCancellation = new CancellationTokenSource();
                    if (cancelPending) captureCancellation.Cancel();
                    captureCancellation.Token.ThrowIfCancellationRequested();
                    authenticatedConnections.Clear();
                    proxy = new ProxyServer(false, false, false);
                    // Ingress and outbound are independent; never inherit our own capture proxy.
                    proxy.ForwardToUpstreamGateway = false;
                    proxy.UpStreamHttpProxy = CreateExternalProxy(upstream);
                    proxy.UpStreamHttpsProxy = CreateExternalProxy(upstream);
                    proxy.CustomUpStreamProxyFailureFunc = args => throw new IOException("所选代理出口连接失败，请检查网络或手动配置；未切换到其他出口。");
                    proxy.ConnectionTimeOutSeconds = 40;
                    proxy.ConnectTimeOutSeconds = 35;
                    ConfigureCertificate(proxy);
                    // Titanium.Start can remove an existing system proxy if its port matches a
                    // listener. Snapshot first and never bind a configured proxy port, even if idle.
                    var originalProxy = ProxyLease.CaptureSnapshot();
                    var reservedPorts = GetConfiguredProxyPorts(originalProxy["ProxyServer"].Text);
                    reservedPorts.Add(UpstreamPort);
                    ListeningPort = FindPort(PreferredPort, reservedPorts);
                    route = CaptureRoute.CreateAsync(upstream, ingressMode, Notify, captureCancellation.Token).GetAwaiter().GetResult();
                    if (route.RequiresAuthentication) proxy.ProxyBasicAuthenticateFunc = (args, user, password) =>
                    {
                        bool valid = user == route?.CaptureUsername && password == route?.CapturePassword;
                        if (valid)
                        {
                            MarkWechatConnection(args);
                        }
                        return Task.FromResult(valid);
                    };
                    proxy.ExceptionFunc = ex => Diagnostics.RecordFailure(IsTlsError(ex)
                        ? CaptureFailureKind.TlsHandshakeFailed : CaptureFailureKind.Unknown, exception: ex);
                    endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, ListeningPort, true);
                    endpoint.BeforeTunnelConnectRequest += BeforeTunnelConnect;
                    proxy.BeforeRequest += BeforeRequest;
                    proxy.BeforeResponse += BeforeResponse;
                    proxy.AddEndPoint(endpoint);
                    proxy.Start();
                    Diagnostics.Start(ListeningPort);
                    CheckUpstreamAsync(captureCancellation.Token).GetAwaiter().GetResult();
                    route.ActivateAsync(ListeningPort, UpstreamPort, captureCancellation.Token).GetAwaiter().GetResult();
                    routeActive = true;
                    int reset = route.RefreshWechatConnectionsAsync(captureCancellation.Token).GetAwaiter().GetResult();
                    pageObserver.Start();
                    Notify("微信公众号接入已开启。请在电脑微信中刷新当前文章；识别后点击「开始收集」。" +
                        (reset > 0 ? "已刷新公众号的旧连接。" : ""));
                }
                catch (Exception startError)
                {
                    Exception cleanupError = Cleanup();
                    Diagnostics.RecordFailure(CaptureFailureKind.ListenFailed, exception: startError);
                    Notify("监听启动失败：" + startError.Message);
                    if (cleanupError != null) throw new AggregateException("监听启动失败，且系统代理恢复失败，请检查系统代理设置。", startError, cleanupError);
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                bool wasRunning = proxy != null || route != null;
                Exception error = Cleanup();
                if (error != null)
                {
                    Diagnostics.RecordFailure(CaptureFailureKind.ProxyRestoreFailed, exception: error);
                    throw new InvalidOperationException("微信公众号临时接入未能完全撤销，恢复记录已保留。", error);
                }
                if (wasRunning) Notify("监听已停止，微信公众号临时接入已撤销。");
            }
        }

        private Exception Cleanup()
        {
            Exception failure = null;
            captureCancellation?.Cancel();
            pageObserver.Stop();
            // Remove the route before its destination stops listening. A separate watcher also
            // restores an encrypted lease if this process is terminated unexpectedly.
            routeGate.Wait();
            try
            {
                if (route != null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    try { route.RestoreAsync(timeout.Token).GetAwaiter().GetResult(); }
                    catch (Exception ex) { failure = ex; }
                    try { route.Dispose(); }
                    catch (Exception ex) { failure = failure ?? ex; }
                    finally { route = null; }
                }
                routeActive = false;
            }
            finally { routeGate.Release(); }
            var server = proxy;
            proxy = null;
            if (server != null)
            {
                try
                {
                    server.BeforeResponse -= BeforeResponse;
                    server.BeforeRequest -= BeforeRequest;
                    if (endpoint != null) endpoint.BeforeTunnelConnectRequest -= BeforeTunnelConnect;
                    if (server.ProxyRunning) server.Stop();
                }
                catch (Exception ex) { failure = ex; }
                try { server.Dispose(); } catch (Exception ex) { failure = failure ?? ex; }
            }
            endpoint = null;
            captureCancellation = null; // In-flight passive callbacks retain their cancellation token safely.
            leaseLock?.Dispose();
            leaseLock = null;
            ListeningPort = 0;
            Diagnostics.Stop();
            return failure;
        }

        public async Task CheckHealthAsync()
        {
            if (!await routeGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                var active = route; var cancellation = captureCancellation;
                if (active == null || cancellation == null || cancellation.IsCancellationRequested) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await CheckUpstreamAsync(timeout.Token).ConfigureAwait(false);
                bool healthy = await active.CheckActiveAsync(timeout.Token).ConfigureAwait(false);
                if (routeActive && !healthy) Notify("公众号接入规则已改变，请点击「重新接入」后刷新微信文章。");
                routeActive = healthy;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { routeActive = false; Notify("无法确认公众号接入状态，请检查网络出口并点击「重新接入」。"); }
            finally { routeGate.Release(); }
        }

        public void CancelPendingOperations() { cancelPending = true; captureCancellation?.Cancel(); }

        public async Task RefreshWechatAsync()
        {
            if (!IsRunning) { await Task.Run(Start).ConfigureAwait(false); return; }
            await routeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var active = route; var cancellation = captureCancellation;
                if (active == null || cancellation == null) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                await CheckUpstreamAsync(timeout.Token).ConfigureAwait(false);
                if (!await active.CheckActiveAsync(timeout.Token).ConfigureAwait(false))
                {
                    routeActive = false;
                    await active.RestoreAsync(timeout.Token).ConfigureAwait(false);
                    await active.ActivateAsync(ListeningPort, UpstreamPort, timeout.Token).ConfigureAwait(false);
                }
                routeActive = true;
                await active.RefreshWechatConnectionsAsync(timeout.Token).ConfigureAwait(false);
                pageObserver.Start();
                Notify("公众号接入已就绪，请在电脑微信中刷新当前文章。");
            }
            finally { routeGate.Release(); }
        }

        private async Task CheckUpstreamAsync(CancellationToken token)
        {
            if (upstream == null) { Diagnostics.SetUpstreamReachable(true); return; }
            using var client = new TcpClient();
            try { await client.ConnectAsync(upstream.Host, upstream.Port, token).ConfigureAwait(false); Diagnostics.SetUpstreamReachable(true); }
            catch (OperationCanceledException) { throw; }
            catch { Diagnostics.SetUpstreamReachable(false); throw new IOException("所选代理出口不可达，请检查代理软件或在设置中手动配置。"); }
        }

        internal static ExternalProxy CreateExternalProxy(ProxyEndpoint selected)
        {
            if (selected == null) return null;
            return new ExternalProxy(selected.Host, selected.Port, selected.Username ?? "", selected.Password ?? "")
            {
                ProxyType = selected.Protocol == ProxyProtocol.Socks5 ? ExternalProxyType.Socks5 : ExternalProxyType.Http,
                ProxyDnsRequests = true, BypassLocalhost = false
            };
        }

        private static bool IsTlsError(Exception error)
        {
            for (int i = 0; error != null && i < 8; i++, error = error.InnerException)
                if (error is System.Security.Authentication.AuthenticationException || error.GetType().Name.Contains("Ssl", StringComparison.OrdinalIgnoreCase)
                    || error.GetType().Name.Contains("Tls", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void ObserveVisibleDocument(WechatObservedPage page, long generation)
        {
            try
            {
                lock (gate)
                {
                    if (historySuspensions != 0 || generation != sessionGeneration || !pageObserver.IsCurrentObservation(page.ObservationGeneration)
                        || captureCancellation == null || captureCancellation.IsCancellationRequested || proxy == null) return;
                    var parsed = WechatCaptureParser.ParseVisibleDocument(page.Url, sessions.Values);
                    if (!parsed.CanSelectAccount || parsed.Session == null) return;
                    var account = parsed.Session;
                    if (!string.IsNullOrWhiteSpace(page.AccountName)) account.Name = page.AccountName;
                    PublishObservedSession(account, true, generation, captureCancellation.Token, true);
                }
            }
            catch (Exception ex) { if (!cancelPending) Diagnostics.RecordFailure(CaptureFailureKind.ParseFailed, exception: ex); }
        }

        private bool PublishObservedSession(AccountSession account, bool canSelectAccount, long generation, CancellationToken cancellation, bool visibleDocument)
        {
            lock (gate)
            {
                if (historySuspensions != 0 || generation != sessionGeneration || cancellation.IsCancellationRequested || proxy == null) return false;
                sessions.TryGetValue(account.Biz, out var previous);
                account.Name = AccountNameResolver.Choose(account.Biz, account.Name, previous?.Name);
                bool changed = previous == null || !SameSession(account, previous) || lastDetectedBiz != account.Biz;
                sessions[account.Biz] = account.Clone();
                if (canSelectAccount) lastDetectedBiz = account.Biz;
                if (canSelectAccount)
                {
                    Diagnostics.RecordRecognizedAccount(account.Biz);
                    if (changed) Notify(visibleDocument
                        ? "已从微信显示的文章页面识别公众号；会话完整后可点击「开始收集」。"
                        : "已识别微信访问的公众号。点击「开始收集」后才会采集。");
                }
                if (!changed) return historySuspensions == 0 && generation == sessionGeneration;
                var listeners = canSelectAccount ? AccountDetected : SessionUpdated;
                if (listeners == null) return historySuspensions == 0 && generation == sessionGeneration;
                // Checking and publishing share the clear's gate. Consumers must enqueue UI
                // work instead of waiting for the UI thread. Re-check after reentrant callbacks.
                foreach (Action<AccountSession> listener in listeners.GetInvocationList())
                {
                    if (historySuspensions != 0 || generation != sessionGeneration || cancellation.IsCancellationRequested || proxy == null) return false;
                    listener(account.Clone());
                }
                return generation == sessionGeneration;
            }
        }

        private void WriteDiagnostic(CaptureDiagnosticEntry entry)
        {
            // Only the diagnostics module's allow-listed fields are persisted, never raw sessions.
            lock (logGate)
            {
                try
                {
                    string path = Path.Combine(AppPaths.DataDirectory, "capture-diagnostics.jsonl");
                    Directory.CreateDirectory(AppPaths.DataDirectory);
                    if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                        File.Move(path, path + ".previous", true);
                    File.AppendAllText(path, JsonConvert.SerializeObject(entry, Formatting.None) + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private void RecoverPreviousLease()
        {
            if (!File.Exists(leasePath)) return;
            ProxyLease previous;
            try { previous = JsonConvert.DeserializeObject<ProxyLease>(File.ReadAllText(leasePath, Encoding.UTF8)); }
            catch (JsonException ex) { throw new IOException("上次代理恢复记录损坏，请检查 Windows 代理设置。", ex); }
            if (previous == null || previous.Before == null || previous.Applied == null) throw new IOException("上次代理恢复记录无效。");
            bool restored = previous.RestoreIfOwned();
            File.Delete(leasePath);
            Notify(restored ? "已恢复上次异常退出遗留的系统代理。" : "上次监听后系统代理已被修改，已保留当前设置。");
        }

        private static void ConfigureCertificate(ProxyServer server)
        {
            var manager = server.CertificateManager;
            string certificatePath = Path.Combine(AppPaths.DataDirectory, "capture-root.pfx");
            string secretPath = Path.Combine(AppPaths.DataDirectory, "capture-root.secret");
            string password;
            if (File.Exists(secretPath))
            {
                try { password = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(secretPath), null, DataProtectionScope.CurrentUser)); }
                catch (CryptographicException ex) { throw new IOException("监听证书密钥无法由当前 Windows 用户读取。", ex); }
            }
            else
            {
                if (File.Exists(certificatePath)) throw new IOException("监听根证书的密钥文件缺失，无法安全加载已有证书。");
                var bytes = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                password = Convert.ToBase64String(bytes);
                File.WriteAllBytes(secretPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
            }
            manager.RootCertificateName = "WCAE Capture Root";
            manager.RootCertificateIssuerName = "WCAE Capture Root";
            manager.PfxFilePath = certificatePath;
            manager.PfxPassword = password;
            manager.OverwritePfxFile = false;
            manager.StorageFlag = X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable;
            if (File.Exists(certificatePath))
            {
                if (!manager.LoadRootCertificate(certificatePath, password, false, manager.StorageFlag))
                    throw new IOException("无法加载已保存的监听根证书。");
            }
            else if (!manager.CreateRootCertificate(true)) throw new IOException("无法创建监听根证书。");
            if (!manager.IsRootCertificateUserTrusted()) manager.TrustRootCertificate(false);
            if (!manager.IsRootCertificateUserTrusted()) throw new InvalidOperationException("未完成当前用户的监听证书信任，请允许证书提示后重试。");
        }

        internal static HashSet<int> GetConfiguredProxyPorts(string proxyServer)
        {
            var result = new HashSet<int>();
            foreach (string setting in (proxyServer ?? "").Split(new[] { ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string address = setting;
                int equals = address.IndexOf('=');
                if (equals >= 0) address = address.Substring(equals + 1);
                if (address.IndexOf("://", StringComparison.Ordinal) < 0) address = "http://" + address;
                Uri endpointUri;
                if (Uri.TryCreate(address, UriKind.Absolute, out endpointUri) && endpointUri.Port > 0 && endpointUri.Port <= 65535)
                    result.Add(endpointUri.Port);
            }
            return result;
        }

        internal static int ChooseInitialPort(int preferred, ISet<int> reservedPorts)
        {
            if (preferred < 0 || preferred > 65535) throw new ArgumentOutOfRangeException(nameof(preferred));
            return reservedPorts != null && reservedPorts.Contains(preferred) ? 0 : preferred;
        }

        private static int FindPort(int preferred, ISet<int> reservedPorts)
        {
            int candidate = ChooseInitialPort(preferred, reservedPorts);
            TcpListener listener = null;
            try
            {
                for (int attempt = 0; attempt < 32; attempt++)
                {
                    listener = new TcpListener(IPAddress.Loopback, candidate);
                    try { listener.Start(); }
                    catch (SocketException) { listener.Stop(); candidate = 0; continue; }
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    if (!reservedPorts.Contains(port)) return port;
                    listener.Stop();
                    candidate = 0;
                }
                throw new IOException("找不到可用的本机监听端口，请检查网络状态后重试。");
            }
            finally { listener?.Stop(); }
        }

        private Task BeforeTunnelConnect(object sender, TunnelConnectSessionEventArgs args)
        {
            var uri = args.HttpClient.Request.RequestUri;
            if (route?.RequiresAuthentication == false && OwnsWechatConnection(args)) MarkWechatConnection(args);
            // Titanium may raise the CONNECT hook before its proxy-auth callback. Secured
            // routes still require authentication before processing the tunnel itself.
            args.DecryptSsl = (route?.RequiresAuthentication == true || ReferenceEquals(args.ClientUserData, authenticatedWechatRoute))
                && uri != null && string.Equals(uri.Host, "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase);
            return Task.CompletedTask;
        }

        private static bool OwnsWechatConnection(SessionEventArgsBase args)
        {
            var remote = args.ClientRemoteEndPoint;
            return remote != null && WechatConnectionOwner.IsWechat(new JObject
                { ["sourceIP"] = remote.Address.ToString(), ["sourcePort"] = remote.Port });
        }

        private void MarkWechatConnection(SessionEventArgsBase args)
        {
            args.ClientUserData = authenticatedWechatRoute;
            bool first;
            lock (authenticatedConnections) first = authenticatedConnections.Add(args.ClientConnectionId);
            if (!first) return;
            Diagnostics.RecordConnection(true);
            if (args.HttpClient.Request.Method == "CONNECT" && string.Equals(args.HttpClient.Request.RequestUri?.Host, "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase)) Diagnostics.RecordMpTunnel();
        }

        private async Task BeforeRequest(object sender, SessionEventArgs args)
        {
            if (route?.RequiresAuthentication == false && OwnsWechatConnection(args)) MarkWechatConnection(args);
            if (!ReferenceEquals(args.ClientUserData, authenticatedWechatRoute)) return;
            var request = args.HttpClient.Request;
            if (!WechatCaptureParser.IsWechatUri(request.RequestUri)) return;
            CancellationToken cancellation;
            CapturedRequest captured;
            lock (gate)
            {
                if (historySuspensions != 0 || captureCancellation == null || captureCancellation.IsCancellationRequested) return;
                cancellation = captureCancellation.Token;
                captured = requestBodies.GetOrCreateValue(args);
                captured.Generation = sessionGeneration;
            }
            if (request.Method != "POST") return;
            try
            {
                if (request.ContentLength > 1024 * 1024) return;
                byte[] bytes = await args.GetRequestBody(cancellation).ConfigureAwait(false);
                if (bytes != null && bytes.Length <= 1024 * 1024)
                    captured.Body = Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.RecordFailure(CaptureFailureKind.ParseFailed, request.RequestUri, exception: ex); }
        }

        private async Task BeforeResponse(object sender, SessionEventArgs args)
        {
            if (!ReferenceEquals(args.ClientUserData, authenticatedWechatRoute)) return;
            CancellationToken cancellation;
            CapturedRequest captured;
            long generation;
            lock (gate)
            {
                if (historySuspensions != 0 || captureCancellation == null || !requestBodies.TryGetValue(args, out captured)) return;
                cancellation = captureCancellation.Token;
                generation = captured.Generation;
                if (generation != sessionGeneration) return;
            }
            try
            {
                var request = args.HttpClient.Request;
                var uri = request.RequestUri;
                if (!WechatCaptureParser.IsWechatUri(uri)) return;
                int statusCode = args.HttpClient.Response.StatusCode;
                Diagnostics.RecordDecryptedResponse(uri, statusCode);
                if (statusCode != 200 && statusCode != 304)
                {
                    Diagnostics.RecordFilteredResponse(uri, statusCode, CaptureFailureKind.FilteredStatus); return;
                }
                if (args.HttpClient.Response.ContentLength > 16 * 1024 * 1024)
                {
                    Diagnostics.RecordFailure(CaptureFailureKind.BodyTooLarge, uri, statusCode); return;
                }
                var bytes = statusCode == 304 ? Array.Empty<byte>() : await args.GetResponseBody(cancellation).ConfigureAwait(false);
                if (bytes?.Length > 16 * 1024 * 1024) { Diagnostics.RecordFailure(CaptureFailureKind.BodyTooLarge, uri, statusCode); return; }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in request.Headers.Headers)
                    if (!header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) headers[header.Key] = header.Value.Value;
                AccountSession[] known;
                lock (gate)
                {
                    if (generation != sessionGeneration || cancellation.IsCancellationRequested) return;
                    known = sessions.Values.Select(s => s.Clone()).ToArray();
                }
                var parsed = WechatCaptureParser.Parse(new WechatCaptureInput
                {
                    RequestUrl = uri.AbsoluteUri, RequestMethod = request.Method, StatusCode = statusCode,
                    ResponseContentType = args.HttpClient.Response.ContentType ?? "",
                    ResponseBody = bytes == null ? "" : Encoding.UTF8.GetString(bytes), RequestHeaders = headers,
                    RequestBody = captured.Body
                }, known);
                var account = parsed.Session;
                if (account == null)
                {
                    if (parsed.Reason == WechatCaptureReason.UnsupportedPath)
                        Diagnostics.RecordFilteredResponse(uri, statusCode, CaptureFailureKind.FilteredUrl);
                    else if (parsed.Reason != WechatCaptureReason.SpeculativeRequest)
                        Diagnostics.RecordFailure(parsed.Reason == WechatCaptureReason.MissingBiz ? CaptureFailureKind.MissingBiz : CaptureFailureKind.ParseFailed, uri, statusCode);
                    return;
                }
                if (!PublishObservedSession(account, parsed.CanSelectAccount, generation, cancellation, false)) return;
                if (parsed.Reason == WechatCaptureReason.EmptyDocumentBody)
                    Diagnostics.RecordFailure(CaptureFailureKind.EmptyBody, uri, statusCode);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested) Diagnostics.RecordFailure(CaptureFailureKind.ParseFailed, exception: ex); }
            finally { requestBodies.Remove(args); }
        }

        public static AccountSession ParseCapturedPage(string html, string requestUrl, IDictionary<string, string> headers)
        {
            Uri uri;
            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out uri) || !IsCaptureUri(uri) || string.IsNullOrWhiteSpace(html)) return null;
            var query = HttpUtility.ParseQueryString(uri.Query);
            string biz = query["__biz"];
            if (string.IsNullOrWhiteSpace(biz)) biz = ReadScriptValue(html, "biz", "__biz");
            if (string.IsNullOrWhiteSpace(biz)) return null;
            var collectedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers != null) foreach (var pair in headers) collectedHeaders[pair.Key] = pair.Value ?? "";
            string cookie, userAgent;
            collectedHeaders.TryGetValue("Cookie", out cookie);
            collectedHeaders.TryGetValue("User-Agent", out userAgent);
            return new AccountSession
            {
                Biz = DecodeScript(biz), Name = AccountNameResolver.Extract(html, DecodeScript(biz)),
                UserName = ReadScriptValue(html, "user_name", "username"),
                Uin = query["uin"] ?? ReadScriptValue(html, "uin"), Key = query["key"] ?? ReadScriptValue(html, "key"),
                PassTicket = query["pass_ticket"] ?? ReadScriptValue(html, "pass_ticket"),
                AppMsgToken = query["appmsg_token"] ?? ReadScriptValue(html, "appmsg_token"),
                Cookie = cookie ?? "", UserAgent = string.IsNullOrWhiteSpace(userAgent) ? "Mozilla/5.0" : userAgent,
                RequestUrl = uri.AbsoluteUri, CapturedAt = DateTime.Now, Headers = collectedHeaders
            };
        }

        private static bool IsCaptureUri(Uri uri)
        {
            if (uri == null || (uri.Scheme != "http" && uri.Scheme != "https") || !string.Equals(uri.Host, "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase)) return false;
            return uri.AbsolutePath == "/s" || uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal)
                || uri.AbsolutePath.Equals("/mp/profile_ext", StringComparison.Ordinal);
        }

        private static string ReadScriptValue(string html, params string[] names)
        {
            foreach (string name in names)
            {
                string pattern = @"(?<![\w$])['""]?" + Regex.Escape(name) + @"['""]?\s*(?::|=)\s*(?:['""]['""]\s*\|\|\s*)?(?:htmlDecode\s*\(\s*)?(?<q>['""])(?<v>(?:\\.|(?!\k<q>).)*?)\k<q>";
                foreach (Match match in Regex.Matches(html, pattern, RegexOptions.Singleline, TimeSpan.FromMilliseconds(500)))
                {
                    string value = DecodeScript(match.Groups["v"].Value);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            return "";
        }

        private static string DecodeScript(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = Regex.Replace(value, @"\\(?:u(?<u>[0-9a-fA-F]{4})|x(?<x>[0-9a-fA-F]{2}))", m =>
                ((char)Convert.ToInt32(m.Groups["u"].Success ? m.Groups["u"].Value : m.Groups["x"].Value, 16)).ToString());
            return HttpUtility.HtmlDecode(value.Replace("\\/", "/").Replace("\\\"", "\"").Replace("\\'", "'").Replace("\\\\", "\\"));
        }

        private static void MergeMissing(AccountSession target, AccountSession previous)
        {
            if (string.IsNullOrEmpty(target.Name)) target.Name = previous.Name;
            if (string.IsNullOrEmpty(target.UserName)) target.UserName = previous.UserName;
            if (string.IsNullOrEmpty(target.Cookie)) target.Cookie = previous.Cookie;
            if (string.IsNullOrEmpty(target.Uin)) target.Uin = previous.Uin;
            if (string.IsNullOrEmpty(target.Key)) target.Key = previous.Key;
            if (string.IsNullOrEmpty(target.PassTicket)) target.PassTicket = previous.PassTicket;
            if (string.IsNullOrEmpty(target.AppMsgToken)) target.AppMsgToken = previous.AppMsgToken;
        }
        private static bool SameSession(AccountSession a, AccountSession b)
            => a.Biz == b.Biz && a.Name == b.Name && a.UserName == b.UserName && a.Cookie == b.Cookie && a.Uin == b.Uin && a.Key == b.Key && a.PassTicket == b.PassTicket && a.AppMsgToken == b.AppMsgToken;

        private void Notify(string message) { try { StatusChanged?.Invoke(message); } catch { } }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                try { Stop(); }
                finally { disposed = true; pageObserver.Dispose(); AppDomain.CurrentDomain.ProcessExit -= processExit; }
            }
        }

        private static void SaveLease(string path, ProxyLease value)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        internal static void VerifyProxyRecoveryRules()
        {
            Func<string, ProxyValue> text = value => new ProxyValue { Exists = true, Kind = RegistryValueKind.String, Text = value };
            var before = new Dictionary<string, ProxyValue>
            {
                ["ProxyEnable"] = new ProxyValue { Exists = true, Kind = RegistryValueKind.DWord, Number = 0 },
                ["ProxyServer"] = text("prior-proxy:8080"), ["ProxyOverride"] = text("localhost"), ["AutoConfigURL"] = new ProxyValue { Exists = false }
            };
            var applied = new Dictionary<string, ProxyValue>(before)
            {
                ["ProxyEnable"] = new ProxyValue { Exists = true, Kind = RegistryValueKind.DWord, Number = 1 },
                ["ProxyServer"] = text("http=127.0.0.1:8879;https=127.0.0.1:8879"), ["ProxyOverride"] = text("<local>")
            };
            var fixture = new ProxyLease { Before = before, Applied = applied, AppliedSuccessfully = true, Port = 8879 };
            // Persisted recovery records must survive JSON serialization without accessing the registry.
            fixture = JsonConvert.DeserializeObject<ProxyLease>(JsonConvert.SerializeObject(fixture));
            bool complete;
            var planned = fixture.PlanRestore(new Dictionary<string, ProxyValue>(applied), out complete);
            if (!complete || planned == null || !planned["ProxyServer"].Same(before["ProxyServer"])) throw new InvalidOperationException("代理恢复自测：正常退出恢复失败。");
            var changed = new Dictionary<string, ProxyValue>(applied) { ["ProxyServer"] = text("user-proxy:9000") };
            if (fixture.PlanRestore(changed, out complete) != null) throw new InvalidOperationException("代理恢复自测：覆盖了用户的新代理。");
            changed = new Dictionary<string, ProxyValue>(applied) { ["ProxyOverride"] = text("user-bypass") };
            planned = fixture.PlanRestore(changed, out complete);
            if (complete || planned == null || planned["ProxyOverride"].Text != "user-bypass" || !planned["ProxyServer"].Same(before["ProxyServer"]))
                throw new InvalidOperationException("代理恢复自测：未保留用户的绕过列表或留下了失效端口。");
            changed = new Dictionary<string, ProxyValue>(applied) { ["ProxyEnable"] = before["ProxyEnable"] };
            planned = fixture.PlanRestore(changed, out complete);
            if (planned == null || planned["ProxyEnable"].Number != 0) throw new InvalidOperationException("代理恢复自测：未保留用户关闭代理的设置。");
            fixture.AppliedSuccessfully = false;
            changed = new Dictionary<string, ProxyValue>(before) { ["ProxyEnable"] = applied["ProxyEnable"] };
            planned = fixture.PlanRestore(changed, out complete);
            if (!complete || planned == null || planned["ProxyEnable"].Number != 0) throw new InvalidOperationException("代理恢复自测：启动中断未回滚。");
        }

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);

        private sealed class ProxyValue
        {
            public ProxyValue() { }
            public bool Exists { get; set; }
            public RegistryValueKind Kind { get; set; }
            public string Text { get; set; }
            public int Number { get; set; }
            public bool Same(ProxyValue other) => other != null && Exists == other.Exists && (!Exists || Kind == other.Kind && Text == other.Text && Number == other.Number);
        }

        private sealed class ProxyLease
        {
            public ProxyLease() { }
            private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
            private static readonly string[] Names = { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL" };
            public Dictionary<string, ProxyValue> Before { get; set; }
            public Dictionary<string, ProxyValue> Applied { get; set; }
            public int Port { get; set; }
            public bool AppliedSuccessfully { get; set; }
            public static Dictionary<string, ProxyValue> CaptureSnapshot() => Read();
            public static ProxyLease Create(int port, Dictionary<string, ProxyValue> before)
            {
                var expected = new Dictionary<string, ProxyValue>(before, StringComparer.Ordinal);
                expected["ProxyEnable"] = new ProxyValue { Exists = true, Kind = RegistryValueKind.DWord, Number = 1 };
                expected["ProxyServer"] = new ProxyValue { Exists = true, Kind = RegistryValueKind.String, Text = "http=127.0.0.1:" + port + ";https=127.0.0.1:" + port };
                expected["ProxyOverride"] = new ProxyValue { Exists = true, Kind = RegistryValueKind.String, Text = "<local>" };
                expected["AutoConfigURL"] = new ProxyValue { Exists = false };
                return new ProxyLease { Before = before, Applied = expected, Port = port };
            }
            public void Apply() { Write(Applied); }
            public bool RestoreIfOwned()
            {
                bool complete;
                var planned = PlanRestore(Read(), out complete);
                if (planned == null) return false;
                Write(planned);
                return complete;
            }
            public Dictionary<string, ProxyValue> PlanRestore(Dictionary<string, ProxyValue> current, out bool complete)
            {
                complete = false;
                if (Before == null || Applied == null) throw new IOException("系统代理恢复记录无效。");
                current = new Dictionary<string, ProxyValue>(current, StringComparer.Ordinal);
                bool preservedUserChange = false;
                foreach (var name in Names)
                {
                    if (!Before.ContainsKey(name) || !Applied.ContainsKey(name) || Before[name] == null || Applied[name] == null)
                        throw new IOException("系统代理恢复记录不完整。");
                }
                // A different proxy target belongs to the user or another application.
                if (AppliedSuccessfully && !current["ProxyServer"].Same(Applied["ProxyServer"])) return null;
                if (!AppliedSuccessfully)
                {
                    // A persisted pre-apply lease also covers a crash half-way through writing settings.
                    foreach (var name in Names)
                        if (!current[name].Same(Applied[name]) && !current[name].Same(Before[name])) return null;
                }
                foreach (var name in Names)
                {
                    if (current[name].Same(Applied[name])) current[name] = Before[name];
                    else if (!current[name].Same(Before[name])) preservedUserChange = true;
                }
                complete = !preservedUserChange;
                return current;
            }
            private static Dictionary<string, ProxyValue> Read()
            {
                var result = new Dictionary<string, ProxyValue>(StringComparer.Ordinal);
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
                {
                    foreach (var name in Names)
                    {
                        object value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (value == null) result[name] = new ProxyValue { Exists = false };
                        else
                        {
                            var kind = key.GetValueKind(name);
                            if (kind != RegistryValueKind.DWord && kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
                                throw new IOException("系统代理设置的注册表类型异常：" + name);
                            result[name] = new ProxyValue { Exists = true, Kind = kind, Text = value as string, Number = value is int number ? number : 0 };
                        }
                    }
                }
                return result;
            }
            private static void Write(Dictionary<string, ProxyValue> snapshot)
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                {
                    if (key == null) throw new IOException("无法访问当前用户的系统代理设置。");
                    foreach (var name in Names)
                    {
                        var value = snapshot[name];
                        if (!value.Exists) key.DeleteValue(name, false);
                        else key.SetValue(name, value.Kind == RegistryValueKind.DWord ? (object)value.Number : value.Text ?? "", value.Kind);
                    }
                }
                if (!InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0) || !InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0))
                    throw new IOException("Windows 未能刷新系统代理设置（" + Marshal.GetLastWin32Error() + "）。");
            }
        }
    }
}
