using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    /// <summary>Loopback fixtures only: no process discovery, registry writes, proxy software or external network required.</summary>
    public static class ProxyDiscoverySelfTests
    {
        public static async Task RunAsync()
        {
            SettingsRoundTrip();
            ParseAndLoopProtection();
            await HttpProxy().ConfigureAwait(false);
            await WrongService().ConfigureAwait(false);
            await AuthenticationRequired().ConfigureAwait(false);
            await SocksProxy(false).ConfigureAwait(false);
            await SocksProxy(true).ConfigureAwait(false);
            await SocksRejectsDestination().ConfigureAwait(false);
            await TimeoutAndCancellation().ConfigureAwait(false);
            await CandidateSelection().ConfigureAwait(false);
            await ExplicitModePrecedence().ConfigureAwait(false);
        }

        private static void SettingsRoundTrip()
        {
            string root = Path.Combine(Path.GetTempPath(), "WCAE-proxy-settings-" + Guid.NewGuid().ToString("N"));
            var store = new ProxyConfigurationStore(root);
            try
            {
                Check(store.Load().Mode == ProxyMode.Auto, "首次使用必须默认自动检测");
                var config = new ProxyConfiguration
                {
                    Mode = ProxyMode.Manual,
                    IngressMode = CaptureIngressMode.Process,
                    ManualEndpoint = new ProxyEndpoint { Host = "127.0.0.1", Port = 29123, Protocol = ProxyProtocol.Socks5, Username = "proxy-user-fixture", Password = "proxy-secret-fixture" }
                };
                store.Save(config);
                string serialized = File.ReadAllText(store.FilePath);
                Check(!serialized.Contains("proxy-user-fixture") && !serialized.Contains("proxy-secret-fixture"), "代理用户名和密码不得明文落盘");
                var loaded = store.Load();
                Check(loaded.Mode == ProxyMode.Manual && loaded.IngressMode == CaptureIngressMode.Process && loaded.ManualEndpoint.Port == 29123 && loaded.ManualEndpoint.Protocol == ProxyProtocol.Socks5
                    && loaded.ManualEndpoint.Username == config.ManualEndpoint.Username && loaded.ManualEndpoint.Password == config.ManualEndpoint.Password, "手动设置和受保护认证信息往返");
                loaded.Mode = ProxyMode.Auto;
                store.Save(loaded);
                Check(store.Load().Mode == ProxyMode.Auto && store.Load().ManualEndpoint.Port == 29123, "重新选择自动后保留手填内容但不自动使用");
                File.WriteAllText(store.FilePath, "{ broken fixture }");
                bool rejected = false;
                try { store.Load(); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "损坏设置应提示重新保存，不能默默换出口");
            }
            finally
            {
                // This is the isolated GUID directory created by this test, never the real user data directory.
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void ParseAndLoopProtection()
        {
            var endpoints = ProxyDiscovery.ParseWindowsProxy("http=127.0.0.1:3128;socks=[::1]:10808;https=127.0.0.1:8879;ftp=127.0.0.1:21", 8879).Select(c => c.Endpoint).ToList();
            Check(endpoints.Count == 2 && endpoints.Any(e => e.Port == 3128 && e.Protocol == ProxyProtocol.Http)
                && endpoints.Any(e => e.Port == 10808 && e.Protocol == ProxyProtocol.Socks5 && e.Host == "::1"), "读取系统代理的实际端口与 IPv6 SOCKS，排除监听端口和其他协议");
            foreach (string host in new[] { "127.0.0.1", "127.0.0.2", "localhost", "localhost.", "::1", "::ffff:127.0.0.1" })
            {
                bool rejected = false;
                try { new ProxyEndpoint { Host = host, Port = 8879 }.Validate(); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "必须阻止代理回环：" + host);
            }
            Check(!ProxyDiscovery.ParseWindowsProxy("http://user:secret@127.0.0.1:3128", 8879).Any(), "不从系统代理 URL 中导入裸认证信息");
            var copied = new ProxyConfiguration { Mode = ProxyMode.Manual, ManualEndpoint = new ProxyEndpoint { Port = 3128 } };
            var clone = copied.Clone(); clone.ManualEndpoint.Port = 10080;
            Check(copied.ManualEndpoint.Port == 3128, "设置预览必须使用独立副本");
            Check(new ProxyEndpoint { Protocol = ProxyProtocol.Socks5, Host = "::1", Port = 10808 }.ToWebProxy().Address.Scheme == "socks5", "统一出口必须保留 SOCKS5 协议");
        }

        private static async Task HttpProxy()
        {
            await WithServer(async (stream, token) =>
            {
                string request = await ReadHeaders(stream, token).ConfigureAwait(false);
                Check(request.StartsWith("CONNECT mp.weixin.qq.com:443 HTTP/1.1\r\n", StringComparison.Ordinal), "验证真实 HTTPS CONNECT 而不是仅检查端口");
                Check(!request.Contains("Cookie:", StringComparison.OrdinalIgnoreCase), "代理检测不能发送公众号会话");
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), token).ConfigureAwait(false);
            }, async endpoint =>
            {
                var result = await ProxyDiscovery.ProbeAsync(endpoint, 8879, CancellationToken.None).ConfigureAwait(false);
                Check(result.Success && result.Endpoint.Port == endpoint.Port, "HTTP CONNECT 代理可用");
            }).ConfigureAwait(false);
        }

        private static async Task WrongService()
        {
            await WithServer(async (stream, token) =>
            {
                await ReadHeaders(stream, token).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 5\r\n\r\nhello"), token).ConfigureAwait(false);
            }, async endpoint =>
            {
                var result = await ProxyDiscovery.ProbeAsync(endpoint, 8879, CancellationToken.None).ConfigureAwait(false);
                Check(!result.Success && result.Message.Contains("网页"), "普通网页监听不能假报代理接入成功");
            }).ConfigureAwait(false);
        }

        private static async Task AuthenticationRequired()
        {
            await WithServer(async (stream, token) =>
            {
                await ReadHeaders(stream, token).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=fixture\r\n\r\n"), token).ConfigureAwait(false);
            }, async endpoint =>
            {
                var result = await ProxyDiscovery.ResolveAsync(new ProxyConfiguration { Mode = ProxyMode.Manual, ManualEndpoint = endpoint }, 8879, CancellationToken.None).ConfigureAwait(false);
                Check(!result.Success && result.Message.Contains("认证") && result.Source == "用户设置", "保存的手动代理认证失败应提示配置，不能自动改用其他出口");
            }).ConfigureAwait(false);
        }

        private static async Task SocksProxy(bool authenticated)
        {
            await WithServer(async (stream, token) =>
            {
                var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, token).ConfigureAwait(false);
                Check(greeting[0] == 5, "SOCKS5 握手版本");
                var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, token).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 5, (byte)(authenticated ? 2 : 0) }, token).ConfigureAwait(false);
                if (authenticated)
                {
                    var prefix = new byte[2]; await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
                    var username = new byte[prefix[1]]; await stream.ReadExactlyAsync(username, token).ConfigureAwait(false);
                    var length = new byte[1]; await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
                    var password = new byte[length[0]]; await stream.ReadExactlyAsync(password, token).ConfigureAwait(false);
                    Check(prefix[0] == 1 && Encoding.UTF8.GetString(username) == "fixture" && Encoding.UTF8.GetString(password) == "fixture-password", "SOCKS5 用户认证传输");
                    await stream.WriteAsync(new byte[] { 1, 0 }, token).ConfigureAwait(false);
                }
                await ReadSocksConnect(stream, token).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 1, 187 }, token).ConfigureAwait(false);
            }, async endpoint =>
            {
                endpoint.Protocol = ProxyProtocol.Socks5;
                if (authenticated) { endpoint.Username = "fixture"; endpoint.Password = "fixture-password"; }
                var result = await ProxyDiscovery.ProbeAsync(endpoint, 8879, CancellationToken.None).ConfigureAwait(false);
                Check(result.Success, "SOCKS5 完整 CONNECT 可用，认证=" + authenticated);
            }).ConfigureAwait(false);
        }

        private static async Task SocksRejectsDestination()
        {
            await WithServer(async (stream, token) =>
            {
                var greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, token).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 5, 0 }, token).ConfigureAwait(false);
                await ReadSocksConnect(stream, token).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }, token).ConfigureAwait(false);
            }, async endpoint =>
            {
                endpoint.Protocol = ProxyProtocol.Socks5;
                var result = await ProxyDiscovery.ProbeAsync(endpoint, 8879, CancellationToken.None).ConfigureAwait(false);
                Check(!result.Success && result.Message.Contains("隧道"), "SOCKS5 方法握手成功但目标失败时不能假报可用");
            }).ConfigureAwait(false);
        }

        private static async Task TimeoutAndCancellation()
        {
            await WithServer(async (stream, token) =>
            {
                await ReadHeaders(stream, token).ConfigureAwait(false);
                await Task.Delay(500, token).ConfigureAwait(false);
            }, async endpoint =>
            {
                var timer = Stopwatch.StartNew();
                var result = await ProxyDiscovery.ProbeAsync(endpoint, 8879, CancellationToken.None, TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
                Check(!result.Success && result.Message.Contains("超时") && timer.Elapsed < TimeSpan.FromSeconds(2), "不响应的监听必须限时失败");
            }).ConfigureAwait(false);
            await WithServer(async (stream, token) =>
            {
                await ReadHeaders(stream, token).ConfigureAwait(false);
                await Task.Delay(500, token).ConfigureAwait(false);
            }, async endpoint =>
            {
                using var cancellation = new CancellationTokenSource(150);
                bool cancelled = false;
                try { await ProxyDiscovery.ProbeAsync(endpoint, 8879, cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "关闭/重连的取消请求必须中断代理检测");
            }).ConfigureAwait(false);
        }

        private static async Task CandidateSelection()
        {
            await WithServer(async (stream, token) =>
            {
                await ReadHeaders(stream, token).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), token).ConfigureAwait(false);
            }, async endpoint =>
            {
                var result = await ProxyDiscovery.DiscoverCandidatesAsync(new[]
                {
                    new ProxyDiscovery.ProxyCandidate { Endpoint = new ProxyEndpoint { Port = 8879 }, Source = "禁止回环" },
                    new ProxyDiscovery.ProxyCandidate { Endpoint = endpoint, Source = "本地测试代理", SystemProxy = true }
                }, 8879, CancellationToken.None, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                Check(result.Success && result.Endpoint.Port == endpoint.Port && result.Source == "本地测试代理", "自动检测直接使用验证成功的动态端口");
            }).ConfigureAwait(false);
            var empty = await ProxyDiscovery.DiscoverCandidatesAsync(Array.Empty<ProxyDiscovery.ProxyCandidate>(), 8879, CancellationToken.None, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            Check(!empty.Success && empty.Message.Contains("手动") && empty.Message.Contains("直连"), "自动失败明确提示手动配置，不默默选择另一出口");
        }

        private static async Task ExplicitModePrecedence()
        {
            var direct = await ProxyDiscovery.ResolveAsync(new ProxyConfiguration { Mode = ProxyMode.Direct }, 8879, CancellationToken.None).ConfigureAwait(false);
            Check(direct.IsDirect && direct.Endpoint == null, "用户明确选择直连后不再自动发现代理");
            await WithServer(async (stream, token) =>
            {
                await ReadHeaders(stream, token).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), token).ConfigureAwait(false);
            }, async endpoint =>
            {
                var result = await ProxyDiscovery.ResolveAsync(new ProxyConfiguration { Mode = ProxyMode.Manual, ManualEndpoint = endpoint }, 8879, CancellationToken.None).ConfigureAwait(false);
                Check(result.Success && result.Endpoint.Port == endpoint.Port && result.Source == "用户设置", "显式保存的手动端口生效，不被系统自动检测覆盖");
            }).ConfigureAwait(false);
        }

        private static async Task ReadSocksConnect(NetworkStream stream, CancellationToken token)
        {
            var header = new byte[5]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            Check(header[0] == 5 && header[1] == 1 && header[2] == 0 && header[3] == 3, "SOCKS5 使用域名建立 CONNECT");
            var destination = new byte[header[4] + 2]; await stream.ReadExactlyAsync(destination, token).ConfigureAwait(false);
            Check(Encoding.ASCII.GetString(destination, 0, destination.Length - 2) == "mp.weixin.qq.com" && destination[^2] == 1 && destination[^1] == 187, "SOCKS5 只验证微信 HTTPS 端口");
        }

        private static async Task<string> ReadHeaders(NetworkStream stream, CancellationToken token)
        {
            var bytes = new byte[8192]; int count = 0;
            while (count < bytes.Length)
            {
                await stream.ReadExactlyAsync(bytes.AsMemory(count, 1), token).ConfigureAwait(false); count++;
                if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 && bytes[count - 2] == 13 && bytes[count - 1] == 10)
                    return Encoding.ASCII.GetString(bytes, 0, count);
            }
            throw new Exception("代理测试请求头过大");
        }

        private static async Task WithServer(Func<NetworkStream, CancellationToken, Task> server, Func<ProxyEndpoint, Task> test)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                var endpoint = new ProxyEndpoint { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
                var served = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token).ConfigureAwait(false);
                    using var stream = client.GetStream();
                    await server(stream, cancellation.Token).ConfigureAwait(false);
                });
                try { await test(endpoint).ConfigureAwait(false); }
                finally
                {
                    cancellation.Cancel();
                    try { await served.ConfigureAwait(false); } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                }
            }
            finally { listener.Stop(); }
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception("ProxyDiscovery: " + message); }
    }
}
