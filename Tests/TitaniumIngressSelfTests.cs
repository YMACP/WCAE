using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace WCAE
{
    /// <summary>Real Titanium CONNECT/TLS tests, routed exclusively to a loopback fake upstream. No trusted certificates or system proxy changes.</summary>
    public static class TitaniumIngressSelfTests
    {
        public static string LastReport { get; private set; } = "";

        public static async Task RunAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var rootCertificate = CreateCertificate("WCAE isolated ingress root", true);
            using var originCertificate = CreateCertificate("mp.weixin.qq.com", false);
            await using var origin = new LoopbackUpstream(originCertificate, deadline.Token);
            var events = new ConcurrentQueue<Observation>();
            var faults = new ConcurrentQueue<Exception>();
            var marker = new object();
            int recognized = 0;
            using var proxy = new ProxyServer(false, false, false)
            {
                ForwardToUpstreamGateway = false,
                EnableConnectionPool = false,
                ConnectionTimeOutSeconds = 5,
                ConnectTimeOutSeconds = 5
            };
            // Assign an ephemeral root directly. EnsureRootCertificate therefore never creates/trusts a user root.
            proxy.CertificateManager.RootCertificate = rootCertificate;
            proxy.CertificateManager.SaveFakeCertificates = false;
            proxy.CertificateManager.CertificateEngine = Titanium.Web.Proxy.Network.CertificateEngine.BouncyCastle;
            proxy.CertificateManager.StorageFlag = X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable;
            var upstream = new ExternalProxy("127.0.0.1", origin.Port) { ProxyType = ExternalProxyType.Http, BypassLocalhost = false, ProxyDnsRequests = true };
            proxy.UpStreamHttpProxy = upstream; proxy.UpStreamHttpsProxy = upstream;
            proxy.GetCustomUpStreamProxyFunc = e => Task.FromResult<IExternalProxy>(upstream);
            proxy.CustomUpStreamProxyFailureFunc = e => throw new IOException("隔离测试上游不可用；禁止回退到外网。");
            proxy.ServerCertificateValidationCallback += (sender, args) =>
            {
                // This callback belongs only to this test instance and only accepts our local origin certificate.
                args.IsValid = args.Certificate?.GetCertHashString() == originCertificate.Thumbprint;
                return Task.CompletedTask;
            };
            proxy.ExceptionFunc = ex => faults.Enqueue(ex);
            proxy.ProxyBasicAuthenticateFunc = (args, username, password) =>
            {
                bool valid = username == "fixture-route" && password == "fixture-password";
                events.Enqueue(new Observation(args.ClientConnectionId, valid ? "Authenticate:accepted" : "Authenticate:rejected", ReferenceEquals(args.ClientUserData, marker)));
                if (valid) args.ClientUserData = marker;
                return Task.FromResult(valid);
            };
            var endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, true);
            endpoint.BeforeTunnelConnectRequest += (sender, args) =>
            {
                string host = args.HttpClient.Request.RequestUri.Host;
                if (host != "mp.weixin.qq.com" && host != "cdn.fixture.invalid") throw new IOException("隔离测试出现未预期目标。");
                events.Enqueue(new Observation(args.ClientConnectionId, "BeforeTunnel:" + host, ReferenceEquals(args.ClientUserData, marker)));
                // Mirrors secured CaptureService routes: select interception by host here; authentication occurs later.
                args.DecryptSsl = host == "mp.weixin.qq.com";
                return Task.CompletedTask;
            };
            endpoint.BeforeTunnelConnectResponse += (sender, args) =>
            {
                events.Enqueue(new Observation(args.ClientConnectionId, "TunnelResponse", ReferenceEquals(args.ClientUserData, marker)));
                return Task.CompletedTask;
            };
            proxy.BeforeRequest += (sender, args) =>
            {
                events.Enqueue(new Observation(args.ClientConnectionId, "BeforeRequest:" + args.HttpClient.Request.RequestUri.Host, ReferenceEquals(args.ClientUserData, marker)));
                return Task.CompletedTask;
            };
            proxy.BeforeResponse += async (sender, args) =>
            {
                events.Enqueue(new Observation(args.ClientConnectionId, "BeforeResponse:" + args.HttpClient.Request.RequestUri.Host, ReferenceEquals(args.ClientUserData, marker)));
                if (!ReferenceEquals(args.ClientUserData, marker)) return;
                string body = await args.GetResponseBodyAsString(deadline.Token).ConfigureAwait(false);
                var session = CaptureService.ParseCapturedPage(body, args.HttpClient.Request.RequestUri.AbsoluteUri, new Dictionary<string, string>());
                if (session?.Biz == "local-proxy-biz" && session.Name == "本地代理测试") Interlocked.Increment(ref recognized);
            };
            proxy.AddEndPoint(endpoint);
            proxy.Start();
            try
            {
                string refused = await ConnectOnly(endpoint.Port, null, deadline.Token).ConfigureAwait(false);
                Check(refused.StartsWith("HTTP/1.1 407", StringComparison.Ordinal), "未认证的 CONNECT 必须拒绝");
                Check(recognized == 0, "未认证连接不能进入文章识别");
                string invalid = await ConnectOnly(endpoint.Port, "fixture-route:wrong-password", deadline.Token).ConfigureAwait(false);
                Check(invalid.StartsWith("HTTP/1.1 407", StringComparison.Ordinal), "错误认证的 CONNECT 必须拒绝");

                string article = await FetchThroughTunnel(endpoint.Port, "mp.weixin.qq.com", deadline.Token).ConfigureAwait(false);
                Check(article.Contains("local-proxy-biz", StringComparison.Ordinal) && recognized == 1, "认证后的TLS文章内容必须能被真实解析器识别");
                var authenticated = events.ToArray().Where(e => e.Stage == "Authenticate:accepted").Single();
                var trace = events.ToArray().Where(e => e.Connection == authenticated.Connection).ToList();
                int before = trace.FindIndex(e => e.Stage.StartsWith("BeforeTunnel:", StringComparison.Ordinal));
                int auth = trace.FindIndex(e => e.Stage == "Authenticate:accepted");
                int request = trace.FindIndex(e => e.Stage == "BeforeRequest:mp.weixin.qq.com");
                int response = trace.FindIndex(e => e.Stage == "BeforeResponse:mp.weixin.qq.com");
                Check(before >= 0 && auth > before && request > auth && response > request, "真实事件顺序必须为 CONNECT钩子 → 认证 → TLS请求 → TLS响应");
                Check(!trace[before].Marked && trace[request].Marked && trace[response].Marked,
                    "CONNECT钩子尚无认证标记，而TLS请求/响应必须继承同一ClientUserData标记");

                int decryptedEvents = events.Count(e => e.Stage.StartsWith("BeforeRequest:", StringComparison.Ordinal) || e.Stage.StartsWith("BeforeResponse:", StringComparison.Ordinal));
                string passthrough = await FetchThroughTunnel(endpoint.Port, "cdn.fixture.invalid", deadline.Token).ConfigureAwait(false);
                Check(passthrough.Contains("local-proxy-biz", StringComparison.Ordinal), "非微信主域 CONNECT 必须能透明转发TLS");
                Check(events.Count(e => e.Stage.StartsWith("BeforeRequest:", StringComparison.Ordinal) || e.Stage.StartsWith("BeforeResponse:", StringComparison.Ordinal)) == decryptedEvents && recognized == 1,
                    "非微信主域不解密、不触发文章解析");
                Check(origin.Requests == 2 && origin.Targets.All(t => t == "mp.weixin.qq.com:443" || t == "cdn.fixture.invalid:443"), "全部成功请求必须仅到本地模拟上游");
                LastReport = "Titanium 3.1.1397: " + string.Join(" → ", trace.Select(e => e.Stage + "[marker=" + e.Marked + "]"))
                    + "; unauthenticated/wrong-password=407; article recognized=1; non-mp TLS passthrough=yes; local origin requests=" + origin.Requests;
            }
            catch (Exception ex)
            {
                throw new Exception("Titanium入口验证失败：" + ex.Message + "；事件=" + string.Join(", ", events.Select(e => e.Stage + ":" + e.Marked))
                    + "；代理异常=" + string.Join(" | ", faults.Select(e => e.GetBaseException().Message)), ex);
            }
            finally { proxy.Stop(); }
        }

        private static async Task<string> ConnectOnly(int port, string credentials, CancellationToken token)
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            using var stream = client.GetStream();
            await SendConnect(stream, "mp.weixin.qq.com", credentials, token).ConfigureAwait(false);
            return await ReadHeaders(stream, token).ConfigureAwait(false);
        }
        private static async Task<string> FetchThroughTunnel(int port, string host, CancellationToken token)
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            using var stream = client.GetStream();
            await SendConnect(stream, host, "fixture-route:fixture-password", token).ConfigureAwait(false);
            string accepted = await ReadHeaders(stream, token).ConfigureAwait(false);
            Check(accepted.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "有效代理认证必须建立CONNECT隧道");
            using var tls = new SslStream(stream, true, (sender, certificate, chain, errors) => true);
            // Test-client-only certificate acceptance, never a ServicePointManager or process-global callback.
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = SslProtocols.Tls12 }, token).ConfigureAwait(false);
            string request = "GET /s?__biz=local-proxy-biz HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n";
            await tls.WriteAsync(Encoding.ASCII.GetBytes(request), token).ConfigureAwait(false);
            string header = await ReadHeaders(tls, token).ConfigureAwait(false);
            Check(header.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "本地TLS源站应返回200");
            string length = header.Split("\r\n").First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim();
            byte[] body = new byte[int.Parse(length)]; await tls.ReadExactlyAsync(body, token).ConfigureAwait(false); return Encoding.UTF8.GetString(body);
        }
        private static Task SendConnect(Stream stream, string host, string credentials, CancellationToken token)
        {
            string auth = credentials == null ? "" : "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials)) + "\r\n";
            return stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT " + host + ":443 HTTP/1.1\r\nHost: " + host + ":443\r\n" + auth + "\r\n"), token).AsTask();
        }
        private static async Task<string> ReadHeaders(Stream stream, CancellationToken token)
        {
            byte[] bytes = new byte[8192]; int used = 0;
            while (used < bytes.Length)
            {
                await stream.ReadExactlyAsync(bytes.AsMemory(used, 1), token).ConfigureAwait(false); used++;
                if (used >= 4 && bytes[used - 4] == 13 && bytes[used - 3] == 10 && bytes[used - 2] == 13 && bytes[used - 1] == 10)
                    return Encoding.ASCII.GetString(bytes, 0, used);
            }
            throw new IOException("隔离测试HTTP头过大。");
        }
        private static X509Certificate2 CreateCertificate(string name, bool root)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(root, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(root ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            if (!root)
            {
                var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("mp.weixin.qq.com"); san.AddDnsName("cdn.fixture.invalid"); request.CertificateExtensions.Add(san.Build());
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            }
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            // Schannel server authentication needs a user-key handle on Windows. Without PersistKeySet,
            // disposing this test certificate also removes its temporary key; no certificate store is used.
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        private sealed record Observation(Guid Connection, string Stage, bool Marked);

        private sealed class LoopbackUpstream : IAsyncDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource lifetime;
            private readonly X509Certificate2 certificate;
            private readonly Task accepting;
            private readonly ConcurrentBag<Task> connections = new ConcurrentBag<Task>();
            private int requests;
            public int Requests => Volatile.Read(ref requests);
            public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
            public ConcurrentBag<string> Targets { get; } = new ConcurrentBag<string>();
            public LoopbackUpstream(X509Certificate2 certificate, CancellationToken token)
            {
                this.certificate = certificate; lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); listener.Start();
                accepting = Task.Run(async () =>
                {
                    try
                    {
                        while (!lifetime.IsCancellationRequested)
                        {
                            var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                            connections.Add(Serve(client));
                        }
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                    catch (SocketException) when (lifetime.IsCancellationRequested) { }
                });
            }
            private async Task Serve(TcpClient client)
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    try
                    {
                        string connect = await ReadHeaders(stream, lifetime.Token).ConfigureAwait(false);
                        string target = connect.Split(' ')[1];
                        Check(target == "mp.weixin.qq.com:443" || target == "cdn.fixture.invalid:443", "模拟上游拒绝非预期目标"); Targets.Add(target);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), lifetime.Token).ConfigureAwait(false);
                        using var tls = new SslStream(stream, true);
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 }, lifetime.Token).ConfigureAwait(false);
                        string request = await ReadHeaders(tls, lifetime.Token).ConfigureAwait(false);
                        Check(request.StartsWith("GET /s?__biz=local-proxy-biz ", StringComparison.Ordinal), "模拟源站仅接收测试文章请求");
                        Interlocked.Increment(ref requests);
                        byte[] body = Encoding.UTF8.GetBytes("<html><script>var nickname='本地代理测试';var biz='local-proxy-biz';</script><div id='js_content'>local fixture</div></html>");
                        await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n"), lifetime.Token).ConfigureAwait(false);
                        await tls.WriteAsync(body, lifetime.Token).ConfigureAwait(false);
                    }
                    catch (IOException) { } // A prefetched tunnel can close when proxy authentication rejects the client.
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                }
            }
            public async ValueTask DisposeAsync()
            {
                lifetime.Cancel(); listener.Stop();
                await accepting.ConfigureAwait(false);
                await Task.WhenAll(connections).ConfigureAwait(false);
                lifetime.Dispose();
            }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
