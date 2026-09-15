using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Microsoft.Win32;

namespace WCAE
{
    /// <summary>Temporary per-process TCP ingress. The elevated helper owns its WinDivert
    /// handle; EOF/parent exit closes it. Neither system proxy settings nor client config changes.</summary>
    public sealed class ProcessCaptureRoute : IDisposable
    {
        static readonly string[] WechatProcesses = { "WeChatAppEx.exe", "Weixin.exe", "WeChat.exe" };
        readonly Action<string> status;
        readonly SemaphoreSlim lifecycle = new SemaphoreSlim(1, 1);
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly ConcurrentDictionary<int, TcpClient> clients = new ConcurrentDictionary<int, TcpClient>();
        TcpListener gateway;
        NamedPipeServerStream pipe;
        StreamWriter writer;
        Task monitor;
        Process helper;
        int capturePort, serial;
        bool disposed;
        public string CaptureUsername { get; } = "wcae_" + Guid.NewGuid().ToString("N");
        public string CapturePassword { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public bool IsActive { get; private set; }
        internal int GatewayPort => (gateway?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

        public ProcessCaptureRoute(Action<string> status = null) { this.status = status; }

        public async Task ActivateAsync(int capturePort, int upstreamPort, CancellationToken token)
        {
            await lifecycle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (IsActive) return;
                if (capturePort < 1 || capturePort > 65535 || capturePort == upstreamPort)
                    throw new IOException("公众号接入端口无效或与网络出口冲突。");
                await BundledTools.EnsureIngressAsync(token).ConfigureAwait(false);
                StartGateway(capturePort);
                string name = "WCAE-Capture-" + Guid.NewGuid().ToString("N");
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var owner = Process.GetCurrentProcess();
                var launch = new ProcessStartInfo(Environment.ProcessPath)
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                    Arguments = "--process-capture-helper " + name + " " + owner.Id + " " + owner.StartTime.ToUniversalTime().Ticks
                };
                Emit("正在启用独立微信接入；首次启用时请允许 Windows 管理员权限提示。");
                try { helper = Process.Start(launch) ?? throw new IOException("接入辅助进程未启动。"); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                { throw new IOException("已取消管理员权限，微信流量接入尚未启用。请点击重新接入并允许权限提示。", ex); }
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                startup.CancelAfter(TimeSpan.FromSeconds(90));
                await pipe.WaitForConnectionAsync(startup.Token).ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) || pid != helper.Id)
                    throw new IOException("接入辅助进程身份校验失败。");
                writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                var config = new HelperConfig
                {
                    NativeDirectory = AppPaths.ToolsDirectory, GatewayPort = GatewayPort,
                    Username = CaptureUsername, Password = CapturePassword,
                    Ports = BuildCapturePorts(capturePort, upstreamPort, ReadSystemProxyServer()),
                    Processes = WechatProcesses
                };
                await writer.WriteLineAsync(JsonConvert.SerializeObject(config)).ConfigureAwait(false);
                var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                while (true)
                {
                    string line = await reader.ReadLineAsync(startup.Token).ConfigureAwait(false);
                    if (line == null) throw new IOException("接入辅助进程提前结束。");
                    if (line == "READY") break;
                    if (line.StartsWith("ERROR ", StringComparison.Ordinal)) throw new IOException(line.Substring(6));
                    if (line.StartsWith("LOG ", StringComparison.Ordinal)) Emit(line.Substring(4));
                }
                IsActive = true;
                monitor = MonitorAsync(reader);
                Emit("独立微信接入已开启，原系统代理和其他应用连接保持原配置；请刷新当前公众号文章。");
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
            finally { lifecycle.Release(); }
        }

        async Task MonitorAsync(StreamReader reader)
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    string line = await reader.ReadLineAsync(lifetime.Token).ConfigureAwait(false);
                    if (line == null || line == "STOPPED") break;
                    if (line.StartsWith("LOG ", StringComparison.Ordinal)) Emit(line.Substring(4));
                    if (line.StartsWith("ERROR ", StringComparison.Ordinal)) Emit(line.Substring(6));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException) { }
            finally
            {
                if (IsActive) Emit("独立微信接入已结束；需要继续识别时请点击重新接入。");
                IsActive = false;
            }
        }

        public Task<bool> CheckActiveAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(IsActive && pipe?.IsConnected == true); }
        public Task<int> RefreshWechatConnectionsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Emit("接入已就绪，请重新打开或刷新公众号文章以建立新连接。");
            return Task.FromResult(0);
        }
        public async Task<bool> RestoreAsync(CancellationToken token)
        {
            await lifecycle.WaitAsync(token).ConfigureAwait(false);
            try { await StopCoreAsync().ConfigureAwait(false); return true; }
            finally { lifecycle.Release(); }
        }
        async Task StopCoreAsync()
        {
            IsActive = false;
            if (writer != null) { try { await writer.WriteLineAsync("STOP").ConfigureAwait(false); } catch { } }
            Task stopped = monitor; monitor = null;
            if (stopped != null) { try { await stopped.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { } }
            // The helper observes EOF even if its regular STOP response was lost.
            pipe?.Dispose(); pipe = null; writer = null;
            gateway?.Stop(); gateway = null;
            foreach (var client in clients.Values) client.Dispose();
            helper?.Dispose(); helper = null;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            StopCoreAsync().GetAwaiter().GetResult();
            lifetime.Cancel(); lifetime.Dispose();
        }
        void Emit(string message) { try { status?.Invoke(message); } catch { } }

        internal void StartGateway(int targetCapturePort)
        {
            capturePort = targetCapturePort;
            gateway = new TcpListener(IPAddress.Loopback, 0); gateway.Start();
            _ = AcceptAsync(gateway, lifetime.Token);
        }
        async Task AcceptAsync(TcpListener listener, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    client.NoDelay = true;
                    int id = Interlocked.Increment(ref serial); clients[id] = client;
                    _ = HandleAsync(id, client, token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is ObjectDisposedException || ex is SocketException) { }
        }
        async Task HandleAsync(int id, TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(15));
                    Stream incoming = client.GetStream();
                    byte[] outer = await ReadHeaderAsync(incoming, handshake.Token).ConfigureAwait(false);
                    string text = Encoding.ASCII.GetString(outer);
                    string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(CaptureUsername + ":" + CapturePassword));
                    if (!string.Equals(HeaderValue(text, "Proxy-Authorization"), expected, StringComparison.Ordinal))
                    { await incoming.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nConnection: close\r\n\r\n"), token); return; }
                    var first = text.Split('\n')[0].Trim().Split(' ');
                    if (first.Length < 3 || first[0] != "CONNECT" || !TryAuthority(first[1], out string originalHost, out int originalPort))
                        throw new IOException("接入网关请求无效。");
                    if (IPAddress.TryParse(originalHost, out var ip) && IPAddress.IsLoopback(ip)
                        && (originalPort == GatewayPort || originalPort == capturePort)) throw new IOException("已阻止接入流量循环。");
                    await incoming.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), token);
                    byte[] leading = new byte[1]; await incoming.ReadExactlyAsync(leading, handshake.Token);
                    if (leading[0] == 5)
                    {
                        await ForwardNestedSocksAsync(incoming, originalHost, originalPort, leading, handshake.Token, token);
                        return;
                    }
                    if (leading[0] != 22 && !"GPHCDOT".Contains((char)leading[0]))
                    {
                        using var original = await ConnectAsync(originalHost, originalPort, handshake.Token);
                        await original.GetStream().WriteAsync(leading, token);
                        await RelayAsync(incoming, original.GetStream(), token); return;
                    }
                    byte[] prefix;
                    if (leading[0] == 22)
                    {
                        prefix = await ReadClientHelloAsync(incoming, leading[0], handshake.Token).ConfigureAwait(false);
                        string sni = TlsServerName(prefix);
                        using TcpClient outbound = IsMp(sni)
                            ? await ConnectCaptureAsync("mp.weixin.qq.com", originalPort, handshake.Token)
                            : await ConnectAsync(originalHost, originalPort, handshake.Token);
                        await outbound.GetStream().WriteAsync(prefix, token);
                        await RelayAsync(incoming, outbound.GetStream(), token);
                    }
                    else
                    {
                        prefix = await ReadHeaderAsync(incoming, handshake.Token, leading).ConfigureAwait(false);
                        string request = Encoding.ASCII.GetString(prefix);
                        string[] line = request.Split('\n')[0].Trim().Split(' ');
                        if (line.Length >= 3 && line[0] == "CONNECT" && TryAuthority(line[1], out string host, out int port) && IsMp(host))
                        {
                            using TcpClient outbound = await ConnectCaptureAsync(host, port, handshake.Token);
                            await incoming.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), token);
                            await RelayAsync(incoming, outbound.GetStream(), token);
                        }
                        else if (line.Length >= 3 && line[0] == "CONNECT")
                        {
                            using TcpClient outbound = await ConnectAsync(originalHost, originalPort, handshake.Token);
                            Stream original = outbound.GetStream();
                            await original.WriteAsync(prefix, handshake.Token);
                            byte[] response = await ReadHeaderAsync(original, handshake.Token);
                            await incoming.WriteAsync(response, handshake.Token);
                            if (Encoding.ASCII.GetString(response).Split('\n')[0].Contains(" 200 ", StringComparison.Ordinal))
                                await ForwardEstablishedTunnelAsync(incoming, original, handshake.Token, token);
                            else await RelayAsync(incoming, original, token);
                        }
                        else
                        {
                            bool mp = IsMp(HeaderValue(request, "Host").Split(':')[0]);
                            if (line.Length >= 2 && Uri.TryCreate(line[1], UriKind.Absolute, out Uri url)) mp |= IsMp(url.Host);
                            using TcpClient outbound = mp
                                ? await ConnectAsync("127.0.0.1", capturePort, handshake.Token)
                                : await ConnectAsync(originalHost, originalPort, handshake.Token);
                            if (mp) prefix = AuthenticateHttpRequest(request, expected);
                            await outbound.GetStream().WriteAsync(prefix, token);
                            await RelayAsync(incoming, outbound.GetStream(), token);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is OperationCanceledException || ex is ObjectDisposedException)
            { if (!token.IsCancellationRequested && ex is not OperationCanceledException) Emit("微信接入连接结束：" + ex.Message); }
            finally { clients.TryRemove(id, out _); }
        }

        async Task ForwardNestedSocksAsync(Stream incoming, string originalHost, int originalPort, byte[] prefix, CancellationToken handshake, CancellationToken token)
        {
            // Preserve the original proxy's negotiation/authentication, then inspect CONNECT.
            using var original = await ConnectAsync(originalHost, originalPort, handshake);
            Stream upstream = original.GetStream();
            byte[] count = new byte[1]; await incoming.ReadExactlyAsync(count, handshake);
            byte[] methods = new byte[count[0]]; await incoming.ReadExactlyAsync(methods, handshake);
            await upstream.WriteAsync(prefix.Concat(count).Concat(methods).ToArray(), handshake);
            byte[] selection = new byte[2]; await upstream.ReadExactlyAsync(selection, handshake); await incoming.WriteAsync(selection, handshake);
            if (selection[0] != 5 || selection[1] == 255) return;
            if (selection[1] == 2)
            {
                byte[] auth = new byte[2]; await incoming.ReadExactlyAsync(auth, handshake);
                byte[] user = new byte[auth[1] + 1]; await incoming.ReadExactlyAsync(user, handshake);
                byte[] password = new byte[user[^1]]; await incoming.ReadExactlyAsync(password, handshake);
                await upstream.WriteAsync(auth.Concat(user).Concat(password).ToArray(), handshake);
                byte[] result = new byte[2]; await upstream.ReadExactlyAsync(result, handshake); await incoming.WriteAsync(result, handshake);
                if (result[1] != 0) return;
            }
            else if (selection[1] != 0) { await RelayAsync(incoming, upstream, token); return; }
            byte[] command = new byte[4]; await incoming.ReadExactlyAsync(command, handshake);
            byte[] address;
            string host;
            if (command[3] == 3)
            {
                byte[] size = new byte[1]; await incoming.ReadExactlyAsync(size, handshake);
                byte[] domain = new byte[size[0]]; await incoming.ReadExactlyAsync(domain, handshake);
                address = size.Concat(domain).ToArray(); host = Encoding.ASCII.GetString(domain);
            }
            else if (command[3] == 1 || command[3] == 4)
            { address = new byte[command[3] == 1 ? 4 : 16]; await incoming.ReadExactlyAsync(address, handshake); host = new IPAddress(address).ToString(); }
            else throw new IOException("SOCKS 目标地址无效。");
            byte[] portBytes = new byte[2]; await incoming.ReadExactlyAsync(portBytes, handshake);
            int port = portBytes[0] * 256 + portBytes[1];
            if (command[0] == 5 && command[1] == 1 && IsMp(host))
            {
                using var capture = await ConnectCaptureAsync(host, port, handshake);
                await incoming.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, handshake);
                await RelayAsync(incoming, capture.GetStream(), token);
            }
            else
            {
                await upstream.WriteAsync(command.Concat(address).Concat(portBytes).ToArray(), handshake);
                if (command[0] == 5 && command[1] == 1 && port == 443)
                {
                    byte[] reply = new byte[4]; await upstream.ReadExactlyAsync(reply, handshake);
                    int addressSize;
                    byte[] size = Array.Empty<byte>();
                    if (reply[3] == 3) { size = new byte[1]; await upstream.ReadExactlyAsync(size, handshake); addressSize = size[0]; }
                    else if (reply[3] == 1 || reply[3] == 4) addressSize = reply[3] == 1 ? 4 : 16;
                    else throw new IOException("SOCKS响应地址无效。");
                    byte[] bound = new byte[addressSize + 2]; await upstream.ReadExactlyAsync(bound, handshake);
                    await incoming.WriteAsync(reply.Concat(size).Concat(bound).ToArray(), handshake);
                    if (reply[1] == 0) await ForwardEstablishedTunnelAsync(incoming, upstream, handshake, token);
                }
                else await RelayAsync(incoming, upstream, token);
            }
        }
        async Task ForwardEstablishedTunnelAsync(Stream incoming, Stream original, CancellationToken handshake, CancellationToken token)
        {
            byte[] first = new byte[1]; await incoming.ReadExactlyAsync(first, handshake);
            if (first[0] == 22)
            {
                byte[] hello = await ReadClientHelloAsync(incoming, first[0], handshake);
                if (IsMp(TlsServerName(hello)))
                {
                    using var capture = await ConnectCaptureAsync("mp.weixin.qq.com", 443, handshake);
                    await capture.GetStream().WriteAsync(hello, token);
                    await RelayAsync(incoming, capture.GetStream(), token); return;
                }
                await original.WriteAsync(hello, token);
            }
            else await original.WriteAsync(first, token);
            await RelayAsync(incoming, original, token);
        }
        async Task<TcpClient> ConnectCaptureAsync(string host, int port, CancellationToken token)
        {
            TcpClient client = await ConnectAsync("127.0.0.1", capturePort, token);
            try
            {
                string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(CaptureUsername + ":" + CapturePassword));
                string authority = host + ":" + port;
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\nProxy-Authorization: Basic " + auth + "\r\n\r\n"), token);
                string result = Encoding.ASCII.GetString(await ReadHeaderAsync(client.GetStream(), token));
                if (!result.Split('\n')[0].Contains(" 200 ", StringComparison.Ordinal)) throw new IOException("公众号解密监听拒绝了接入连接。");
                return client;
            }
            catch { client.Dispose(); throw; }
        }
        static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken token)
        {
            var client = new TcpClient { NoDelay = true };
            try { await client.ConnectAsync(host, port, token); return client; }
            catch { client.Dispose(); throw; }
        }
        static async Task RelayAsync(Stream a, Stream b, CancellationToken token)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task left = a.CopyToAsync(b, cancel.Token), right = b.CopyToAsync(a, cancel.Token);
            await Task.WhenAny(left, right); cancel.Cancel();
            try { await Task.WhenAll(left, right); } catch (OperationCanceledException) { }
        }
        internal static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken token, byte[] leading = null)
        {
            using var buffer = new MemoryStream();
            if (leading != null) buffer.Write(leading);
            var one = new byte[1];
            while (buffer.Length < 32768)
            {
                await stream.ReadExactlyAsync(one, token); buffer.WriteByte(one[0]);
                if (buffer.Length >= 4)
                {
                    byte[] data = buffer.GetBuffer(); int n = (int)buffer.Length;
                    if (data[n - 4] == 13 && data[n - 3] == 10 && data[n - 2] == 13 && data[n - 1] == 10) return buffer.ToArray();
                }
            }
            throw new IOException("接入请求头过长。");
        }
        static async Task<byte[]> ReadClientHelloAsync(Stream stream, byte leading, CancellationToken token)
        {
            using var records = new MemoryStream();
            byte[] header = new byte[5]; header[0] = leading;
            await stream.ReadExactlyAsync(header.AsMemory(1), token);
            int totalHandshake = 0, expectedHandshake = 0;
            do
            {
                if (header[0] != 22 || header[1] != 3) throw new IOException("TLS ClientHello 格式无效。");
                int length = header[3] * 256 + header[4];
                if (length == 0 || length > 18432 || records.Length + length > 65536) throw new IOException("TLS ClientHello 长度无效。");
                byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, token);
                records.Write(header); records.Write(body);
                if (totalHandshake == 0 && body.Length >= 4) expectedHandshake = 4 + (body[1] << 16) + (body[2] << 8) + body[3];
                totalHandshake += length;
                if (expectedHandshake == 0 || totalHandshake >= expectedHandshake) break;
                await stream.ReadExactlyAsync(header, token);
            } while (true);
            return records.ToArray();
        }
        internal static string TlsServerName(byte[] records)
        {
            try
            {
                using var handshake = new MemoryStream();
                for (int cursor = 0; cursor + 5 <= records.Length;)
                { int len = (records[cursor + 3] << 8) + records[cursor + 4]; if (cursor + 5 + len > records.Length) return ""; handshake.Write(records, cursor + 5, len); cursor += 5 + len; }
                byte[] b = handshake.ToArray(); if (b.Length < 44 || b[0] != 1) return "";
                int p = 38; p += 1 + b[p]; p += 2 + U16(b, p); p += 1 + b[p];
                int end = p + 2 + U16(b, p); p += 2;
                while (p + 4 <= end && end <= b.Length)
                {
                    int kind = U16(b, p), len = U16(b, p + 2); p += 4;
                    if (p + len > end) return "";
                    if (kind == 0)
                    {
                        int q = p + 2, listEnd = p + 2 + U16(b, p);
                        while (q + 3 <= listEnd && listEnd <= p + len)
                        { int type = b[q], size = U16(b, q + 1); q += 3; if (q + size > listEnd) return ""; if (type == 0) return Encoding.ASCII.GetString(b, q, size); q += size; }
                    }
                    p += len;
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException) { }
            return "";
        }
        static int U16(byte[] b, int p) => (b[p] << 8) + b[p + 1];
        internal static bool IsMp(string host) => string.Equals(host?.TrimEnd('.'), "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase);
        static bool TryAuthority(string text, out string host, out int port)
        {
            host = ""; port = 0;
            if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out Uri uri) || uri.Port < 1 || uri.Port > 65535 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/") return false;
            host = uri.Host.Trim('[', ']'); port = uri.Port; return true;
        }
        static string HeaderValue(string header, string name)
        {
            foreach (string line in header.Split('\n'))
            { int colon = line.IndexOf(':'); if (colon > 0 && string.Equals(line.Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase)) return line.Substring(colon + 1).Trim(); }
            return "";
        }
        static byte[] AuthenticateHttpRequest(string request, string authorization)
        {
            var lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None).Where(l => !l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)).ToList();
            string[] first = lines[0].Split(' ');
            if (first.Length >= 3 && first[1].StartsWith('/')) lines[0] = first[0] + " http://mp.weixin.qq.com" + first[1] + " " + first[2];
            lines.Insert(1, "Proxy-Authorization: " + authorization);
            return Encoding.ASCII.GetBytes(string.Join("\r\n", lines));
        }

        internal sealed class HelperConfig
        {
            public string NativeDirectory { get; set; }
            public int GatewayPort { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public int[] Ports { get; set; }
            public string[] Processes { get; set; }
        }
        static string ReadSystemProxyServer()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);
                return key?.GetValue("ProxyServer") as string ?? "";
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { return ""; }
        }
        internal static int[] BuildCapturePorts(int capturePort, int upstreamPort, string systemProxyServer)
        {
            var ports = new List<int> { 80, 443, upstreamPort };
            foreach (string item in (systemProxyServer ?? "").Split(';'))
            {
                string value = item.Trim(); int separator = value.IndexOf('=');
                if (separator >= 0) value = value.Substring(separator + 1).Trim();
                if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)) ports.Add(uri.Port);
            }
            return ports.Where(p => p > 0 && p <= 65535 && p != capturePort).Distinct().Take(10).ToArray();
        }
        public static async Task<int> RunHelperAsync(string[] args)
        {
            if (args.Length != 4 || args[0] != "--process-capture-helper" || !args[1].StartsWith("WCAE-Capture-", StringComparison.Ordinal)
                || !int.TryParse(args[2], out int ownerId) || !long.TryParse(args[3], out long ownerTicks)) return 2;
            using var lifetime = new CancellationTokenSource();
            try
            {
                using var owner = Process.GetProcessById(ownerId);
                if (owner.StartTime.ToUniversalTime().Ticks != ownerTicks) return 3;
                if (!string.Equals(Path.GetFullPath(owner.MainModule.FileName), Path.GetFullPath(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase)) return 3;
                using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(15000, lifetime.Token);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint serverPid) || serverPid != ownerId) return 3;
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                // The pipe is the sole owner of the native handle. Do not dispose this
                // AutoFlush wrapper after a peer EOF: StreamWriter would flush a broken pipe.
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                string line = await reader.ReadLineAsync(lifetime.Token);
                var config = JsonConvert.DeserializeObject<HelperConfig>(line ?? "");
                if (config == null || config.GatewayPort < 1 || config.GatewayPort > 65535 || config.Ports?.Length is not (>= 1 and <= 10)
                    || config.Ports.Any(p => p < 1 || p > 65535) || config.Processes?.Length is not (>= 1 and <= 10)
                    || config.Processes.Any(p => Path.GetFileName(p) != p || p.Contains('*') || p.Contains(';')))
                    throw new IOException("接入辅助进程配置无效。");
                await BundledTools.EnsureIngressAsync(lifetime.Token);
                if (!string.Equals(Path.GetFullPath(config.NativeDirectory), Path.GetFullPath(AppPaths.ToolsDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("接入组件路径不属于当前 WCAE 安装。");
                object outputGate = new object();
                using var native = new NativeProcessRoute(config, message =>
                { try { lock (outputGate) writer.WriteLine("LOG " + message.Replace('\r', ' ').Replace('\n', ' ')); } catch { } });
                try
                {
                    native.Start();
                    writer.WriteLine("READY");
                    Task<string> command = reader.ReadLineAsync();
                    Task parentExited = owner.WaitForExitAsync(lifetime.Token);
                    await Task.WhenAny(command, parentExited);
                }
                catch (Exception ex) { try { writer.WriteLine("ERROR " + ex.Message); } catch { } return 4; }
                finally { native.Stop(); }
                try { writer.WriteLine("STOPPED"); } catch { }
                return 0;
            }
            catch { return 5; }
            finally { lifetime.Cancel(); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
    }
}
