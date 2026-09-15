using System;
using System.IO;
using System.IO.Pipes;
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

namespace WCAE
{
    public static class ProcessCaptureRouteSelfTests
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        public static async Task RunAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            CancellationToken token = deadline.Token;
            await VerifyHelperPipeDisposeAsync(token);
            Check(ProcessCaptureRoute.BuildCapturePorts(8879, 1080, "http=127.0.0.1:7897;https=[::1]:7898;socks=localhost:1080").SequenceEqual(new[] { 80, 443, 1080, 7897, 7898 }), "手动出口与系统代理端口没有分别保留");
            Check(ProcessCaptureRoute.BuildCapturePorts(8879, 0, "127.0.0.1:8879;http=bad:99999;socks=socks5://localhost:1081").SequenceEqual(new[] { 80, 443, 1081 }), "接入端口循环排除或代理端口校验失败");
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=mp.weixin.qq.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            using var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            var capture = new TcpListener(IPAddress.Loopback, 0); capture.Start();
            using var route = new ProcessCaptureRoute(message => System.Diagnostics.Trace.WriteLine(message));
            route.StartGateway(((IPEndPoint)capture.LocalEndpoint).Port);
            try
            {
                Check(ProcessCaptureRoute.IsMp("MP.WEIXIN.QQ.COM.") && !ProcessCaptureRoute.IsMp("mp.weixin.qq.com.example.org"), "接入域名边界错误");
                using (var rejected = new TcpClient())
                {
                    await rejected.ConnectAsync(IPAddress.Loopback, route.GatewayPort, token);
                    await rejected.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT 127.0.0.1:12345 HTTP/1.1\r\n\r\n"), token);
                    string response = Encoding.ASCII.GetString(await ProcessCaptureRoute.ReadHeaderAsync(rejected.GetStream(), token));
                    Check(response.Contains(" 407 "), "未认证进程可以使用接入网关");
                }
                // A real SslStream ClientHello is recognized and tunneled with the private auth.
                Task captured = CaptureTlsAsync(capture, route, certificate, token);
                using (var client = await GatewayAsync(route, 443, token))
                    await TlsPingAsync(client.GetStream(), "mp.weixin.qq.com", token);
                await captured;

                // Plain HTTP is converted to proxy form and receives the same authentication.
                Task plain = CaptureHttpAsync(capture, route, token);
                using (var client = await GatewayAsync(route, 80, token))
                {
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /s?example=1 HTTP/1.1\r\nHost: mp.weixin.qq.com\r\nConnection: close\r\n\r\n"), token);
                    string response = Encoding.ASCII.GetString(await ProcessCaptureRoute.ReadHeaderAsync(client.GetStream(), token));
                    Check(response.Contains(" 204 "), "明文HTTP没有进入公众号监听");
                }
                await plain;

                // A WeChat connection that already targets an HTTP proxy contains another CONNECT.
                captured = CaptureTlsAsync(capture, route, certificate, token);
                using (var client = await GatewayAsync(route, 12346, token))
                {
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT mp.weixin.qq.com:443 HTTP/1.1\r\nHost: mp.weixin.qq.com:443\r\n\r\n"), token);
                    string response = Encoding.ASCII.GetString(await ProcessCaptureRoute.ReadHeaderAsync(client.GetStream(), token));
                    Check(response.Contains(" 200 "), "原HTTP代理的CONNECT未正确拆封");
                    await TlsPingAsync(client.GetStream(), "mp.weixin.qq.com", token);
                }
                await captured;

                // Non-target HTTPS reaches its original endpoint, not the capture proxy.
                var original = new TcpListener(IPAddress.Loopback, 0); original.Start();
                try
                {
                    Task direct = ServeTlsAsync(original, certificate, token);
                    using var client = await GatewayAsync(route, ((IPEndPoint)original.LocalEndpoint).Port, token);
                    await TlsPingAsync(client.GetStream(), "other.example", token);
                    await direct;
                }
                finally { original.Stop(); }

                // Existing SOCKS5 username/password negotiation stays with the original proxy.
                var socks = new TcpListener(IPAddress.Loopback, 0); socks.Start();
                try
                {
                    var acceptedAuth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task originalSocks = SocksAuthOnlyAsync(socks, acceptedAuth, token);
                    captured = CaptureTlsAsync(capture, route, certificate, token);
                    using (var client = await GatewayAsync(route, ((IPEndPoint)socks.LocalEndpoint).Port, token))
                    {
                        Stream stream = client.GetStream();
                        await stream.WriteAsync(new byte[] { 5, 1, 2 }, token);
                        byte[] choice = new byte[2]; await stream.ReadExactlyAsync(choice, token);
                        Check(choice.SequenceEqual(new byte[] { 5, 2 }), "SOCKS认证方式未透传");
                        await stream.WriteAsync(new byte[] { 1, 1, (byte)'u', 1, (byte)'p' }, token);
                        await stream.ReadExactlyAsync(choice, token); Check(choice[1] == 0, "SOCKS认证结果未透传");
                        byte[] host = Encoding.ASCII.GetBytes("mp.weixin.qq.com");
                        await stream.WriteAsync(new byte[] { 5, 1, 0, 3, (byte)host.Length }.Concat(host).Concat(new byte[] { 1, 187 }).ToArray(), token);
                        byte[] result = new byte[10]; await stream.ReadExactlyAsync(result, token); Check(result[1] == 0, "SOCKS公众号连接未进入监听");
                        await TlsPingAsync(stream, "mp.weixin.qq.com", token);
                    }
                    Check(await acceptedAuth.Task.WaitAsync(token), "原SOCKS代理未验证用户名密码");
                    await captured; await originalSocks;
                }
                finally { socks.Stop(); }

                // Locally resolved SOCKS destinations still use TLS SNI after proxy negotiation.
                var ipSocks = new TcpListener(IPAddress.Loopback, 0); ipSocks.Start();
                try
                {
                    Task originalSocks = SocksIpAsync(ipSocks, token);
                    captured = CaptureTlsAsync(capture, route, certificate, token);
                    using (var client = await GatewayAsync(route, ((IPEndPoint)ipSocks.LocalEndpoint).Port, token))
                    {
                        Stream stream = client.GetStream();
                        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
                        byte[] choice = new byte[2]; await stream.ReadExactlyAsync(choice, token);
                        await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 192, 0, 2, 1, 1, 187 }, token);
                        byte[] result = new byte[10]; await stream.ReadExactlyAsync(result, token);
                        await TlsPingAsync(stream, "mp.weixin.qq.com", token);
                    }
                    await captured; await originalSocks;
                }
                finally { ipSocks.Stop(); }
            }
            finally { capture.Stop(); }
        }
        static async Task<TcpClient> GatewayAsync(ProcessCaptureRoute route, int port, CancellationToken token)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, route.GatewayPort, token);
            string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(route.CaptureUsername + ":" + route.CapturePassword));
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT 127.0.0.1:" + port + " HTTP/1.1\r\nProxy-Authorization: Basic " + auth + "\r\n\r\n"), token);
            string response = Encoding.ASCII.GetString(await ProcessCaptureRoute.ReadHeaderAsync(client.GetStream(), token));
            Check(response.Contains(" 200 "), "接入网关握手失败"); return client;
        }
        static async Task VerifyHelperPipeDisposeAsync(CancellationToken token)
        {
            string name = "WCAE-PipeDispose-Test-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Task accepting = server.WaitForConnectionAsync(token); await client.ConnectAsync(token); await accepting;
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, true);
            var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            Task<string> read = reader.ReadLineAsync(token).AsTask();
            await writer.WriteLineAsync("READY");
            Check(await read == "READY", "辅助进程pipe写入失败");
            server.Dispose();
            Check(await reader.ReadLineAsync(token) == null, "辅助进程pipe关闭没有产生EOF");
            // The pipe is the only native-resource owner, as in the actual EOF/helper path.
        }
        static async Task CaptureTlsAsync(TcpListener listener, ProcessCaptureRoute route, X509Certificate2 certificate, CancellationToken token)
        {
            using var accepted = await listener.AcceptTcpClientAsync(token);
            string header = Encoding.ASCII.GetString(await ProcessCaptureRoute.ReadHeaderAsync(accepted.GetStream(), token));
            Check(header.StartsWith("CONNECT mp.weixin.qq.com:443 HTTP/1.1"), "TLS SNI没有还原公众号域名");
            Check(header.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(route.CaptureUsername + ":" + route.CapturePassword))), "公众号接入缺少私有认证");
            await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), token);
            await ServeTlsStreamAsync(accepted.GetStream(), certificate, token);
        }
        static async Task CaptureHttpAsync(TcpListener listener, ProcessCaptureRoute route, CancellationToken token)
        {
            using var accepted = await listener.AcceptTcpClientAsync(token);
            string header = Encoding.ASCII.GetString(await ProcessCaptureRoute.ReadHeaderAsync(accepted.GetStream(), token));
            Check(header.StartsWith("GET http://mp.weixin.qq.com/s?example=1 HTTP/1.1"), "明文HTTP请求行未保留目标路径");
            Check(header.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(route.CaptureUsername + ":" + route.CapturePassword))), "明文HTTP接入未认证");
            await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\n\r\n"), token);
        }
        static async Task ServeTlsAsync(TcpListener listener, X509Certificate2 certificate, CancellationToken token)
        { using var accepted = await listener.AcceptTcpClientAsync(token); await ServeTlsStreamAsync(accepted.GetStream(), certificate, token); }
        static async Task ServeTlsStreamAsync(Stream stream, X509Certificate2 certificate, CancellationToken token)
        {
            using var tls = new SslStream(stream, true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 }, token);
            byte[] payload = new byte[4]; await tls.ReadExactlyAsync(payload, token);
            Check(Encoding.ASCII.GetString(payload) == "PING", "TLS应用数据丢失");
            await tls.WriteAsync(Encoding.ASCII.GetBytes("PONG"), token); await tls.FlushAsync(token);
        }
        static async Task TlsPingAsync(Stream stream, string host, CancellationToken token)
        {
            using var tls = new SslStream(stream, true, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = SslProtocols.Tls12 }, token);
            await tls.WriteAsync(Encoding.ASCII.GetBytes("PING"), token); await tls.FlushAsync(token);
            byte[] response = new byte[4]; await tls.ReadExactlyAsync(response, token);
            Check(Encoding.ASCII.GetString(response) == "PONG", "接入双向转发失败");
        }
        static async Task SocksAuthOnlyAsync(TcpListener listener, TaskCompletionSource<bool> authenticated, CancellationToken token)
        {
            using var accepted = await listener.AcceptTcpClientAsync(token);
            Stream stream = accepted.GetStream();
            byte[] greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, token);
            Check(greeting.SequenceEqual(new byte[] { 5, 1, 2 }), "原SOCKS握手被修改");
            await stream.WriteAsync(new byte[] { 5, 2 }, token);
            byte[] auth = new byte[5]; await stream.ReadExactlyAsync(auth, token);
            authenticated.SetResult(auth.SequenceEqual(new byte[] { 1, 1, (byte)'u', 1, (byte)'p' }));
            await stream.WriteAsync(new byte[] { 1, 0 }, token);
            byte[] next = new byte[1]; int received = await stream.ReadAsync(next, token);
            Check(received == 0, "公众号CONNECT仍发往原SOCKS而没有进入解密监听");
        }
        static async Task SocksIpAsync(TcpListener listener, CancellationToken token)
        {
            using var accepted = await listener.AcceptTcpClientAsync(token);
            Stream stream = accepted.GetStream();
            byte[] greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, token);
            await stream.WriteAsync(new byte[] { 5, 0 }, token);
            byte[] command = new byte[10]; await stream.ReadExactlyAsync(command, token);
            Check(command.SequenceEqual(new byte[] { 5, 1, 0, 1, 192, 0, 2, 1, 1, 187 }), "SOCKS原IP目标未保留");
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
            byte[] next = new byte[1]; int received = await stream.ReadAsync(next, token);
            Check(received == 0, "SOCKS按IP连接的公众号TLS未按SNI转入解密监听");
        }
    }
}
