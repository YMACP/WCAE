using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WCAE
{
    public sealed class ProxyDiscoveryResult
    {
        public bool Success { get; set; }
        public ProxyEndpoint Endpoint { get; set; }
        public string Source { get; set; } = "";
        public string Message { get; set; } = "";
        public bool IsDirect => Success && Endpoint == null;
    }

    /// <summary>Read-only environment discovery. Never writes registry, client configuration or proxy rules.</summary>
    public static class ProxyDiscovery
    {
        private const string ProbeHost = "mp.weixin.qq.com";
        private const int ProbePort = 443;
        private const int MaximumCandidates = 24;
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
        private static readonly SemaphoreSlim PacResolverGate = new SemaphoreSlim(1, 1);
        private static readonly HashSet<string> CoreNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "clash", "clash-win64", "clash-windows-amd64", "clash-meta", "clash-meta-alpha", "mihomo", "verge-mihomo", "verge-mihomo-alpha",
            "clash-verge", "clash-verge-service", "Clash for Windows", "v2rayN", "v2ray", "wv2ray", "xray", "wxray", "sing-box", "sing_box"
        };

        public static async Task<ProxyDiscoveryResult> ResolveAsync(ProxyConfiguration configuration, int capturePort, CancellationToken token)
        {
            configuration ??= new ProxyConfiguration();
            configuration.Validate(capturePort);
            token.ThrowIfCancellationRequested();
            if (configuration.Mode == ProxyMode.Direct)
                return new ProxyDiscoveryResult { Success = true, Source = "用户设置", Message = "已使用用户保存的直连设置。" };
            if (configuration.Mode == ProxyMode.Manual)
            {
                var result = await ProbeAsync(configuration.ManualEndpoint, capturePort, token).ConfigureAwait(false);
                result.Source = "用户设置";
                result.Message = result.Success ? "已使用用户保存的代理：" + result.Endpoint.DisplayName + "。" : "用户配置的代理不可用：" + result.Message;
                return result;
            }
            return await DiscoverAsync(capturePort, token).ConfigureAwait(false);
        }

        public static async Task<ProxyDiscoveryResult> DiscoverAsync(int capturePort, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Enumeration is small and read-only. Keep it off the UI thread even before the first network await.
            var environment = await Task.Run(() => ReadCandidates(capturePort), token).ConfigureAwait(false);
            if (environment.PacConfigured)
            {
                var pac = await ResolveSystemPacAsync(capturePort, token).ConfigureAwait(false);
                if (pac?.IsDirect == true)
                {
                    var reachable = await ProbeDirectAsync(token).ConfigureAwait(false);
                    if (reachable.Success) { reachable.Source = pac.Source; reachable.Message = pac.Message; }
                    return reachable;
                }
                if (pac?.Endpoint != null)
                {
                    // PAC's per-target decision takes priority over listeners of other running clients.
                    var verified = await ProbeAsync(pac.Endpoint, capturePort, token).ConfigureAwait(false);
                    if (verified.Success)
                    {
                        verified.Source = "系统 PAC";
                        verified.Message = "已自动识别并验证代理：" + verified.Endpoint.DisplayName + "（系统 PAC）。";
                        return verified;
                    }
                }
            }
            if (!environment.ProxyPresent)
            {
                var direct = await ProbeDirectAsync(token).ConfigureAwait(false);
                direct.Source = "自动检测";
                direct.Message = direct.Success ? "未检测到代理配置或活动代理客户端，已验证并使用直连。"
                    : "未检测到代理配置，直连验证也未成功，请检查网络或打开连接设置手动填写代理端口并保存。";
                return direct;
            }
            return await DiscoverCandidatesAsync(environment.Candidates, capturePort, token, ProbeTimeout, environment.PacConfigured).ConfigureAwait(false);
        }

        public static async Task<ProxyDiscoveryResult> ProbeDirectAsync(CancellationToken token, TimeSpan? timeout = null)
        {
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout ?? ProbeTimeout);
            using var socket = new TcpClient();
            try
            {
                await socket.ConnectAsync(ProbeHost, ProbePort, deadline.Token).ConfigureAwait(false);
                return new ProxyDiscoveryResult { Success = true, Source = "直连", Message = "微信 HTTPS 端口直连成功。" };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failure("微信 HTTPS 端口直连超时。"); }
            catch (SocketException) when (!token.IsCancellationRequested) { return Failure("微信 HTTPS 端口直连失败。"); }
            finally { token.ThrowIfCancellationRequested(); }
        }

        private static async Task<ProxyDiscoveryResult> ResolveSystemPacAsync(int capturePort, CancellationToken token)
        {
            // Windows' PAC resolver is synchronous. Never block UI/close, and allow at most one outstanding
            // resolver after a timeout; its worker owns the semaphore until the OS call actually returns.
            if (!await PacResolverGate.WaitAsync(0, token).ConfigureAwait(false)) return null;
            var resolution = Task.Run(() =>
            {
                try
                {
                    var destination = new Uri("https://" + ProbeHost + "/");
#pragma warning disable SYSLIB0014
                    var proxy = WebRequest.GetSystemWebProxy();
#pragma warning restore SYSLIB0014
                    var resolved = proxy.GetProxy(destination);
                    if (resolved == null || resolved == destination)
                        return new ProxyDiscoveryResult { Success = true, Source = "系统 PAC", Message = "系统 PAC 对微信地址选择直连，已采用该出口。" };
                    if (!string.IsNullOrEmpty(resolved.UserInfo) || (resolved.Scheme != "http" && resolved.Scheme != "socks" && resolved.Scheme != "socks5")) return null;
                    var endpoint = new ProxyEndpoint { Host = resolved.Host.Trim('[', ']'), Port = resolved.Port,
                        Protocol = resolved.Scheme.StartsWith("socks", StringComparison.Ordinal) ? ProxyProtocol.Socks5 : ProxyProtocol.Http };
                    endpoint.Validate(capturePort);
                    return new ProxyDiscoveryResult { Endpoint = endpoint, Source = "系统 PAC" };
                }
                catch (Exception ex) when (ex is WebException || ex is InvalidOperationException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception
                    || ex is IOException || ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                { return null; }
                finally { PacResolverGate.Release(); }
            });
            try { return await resolution.WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false); }
            catch (TimeoutException) { return null; }
        }

        internal static async Task<ProxyDiscoveryResult> DiscoverCandidatesAsync(IEnumerable<ProxyCandidate> candidates, int capturePort,
            CancellationToken token, TimeSpan timeout, bool pacConfigured = false)
        {
            var unique = candidates.Where(c => c?.Endpoint != null).GroupBy(c => c.Endpoint.Protocol + ":" + c.Endpoint.Host.ToLowerInvariant() + ":" + c.Endpoint.Port)
                .Select(g => g.First()).Take(MaximumCandidates).ToList();
            // Prefer a working active system proxy over another concurrently running core's listener.
            foreach (var priorityGroup in unique.GroupBy(c => c.SystemProxy ? c.Priority : 100).OrderBy(g => g.Key))
            {
                var group = priorityGroup.ToList();
                for (int offset = 0; offset < group.Count; offset += 4)
                {
                    token.ThrowIfCancellationRequested();
                    using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var pending = group.Skip(offset).Take(4).Select(async candidate =>
                    {
                        var result = await ProbeAsync(candidate.Endpoint, capturePort, batchCancellation.Token, timeout).ConfigureAwait(false);
                        result.Source = candidate.Source;
                        return result;
                    }).ToList();
                    while (pending.Count > 0)
                    {
                        var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                        pending.Remove(completed);
                        var result = await completed.ConfigureAwait(false);
                        if (result.Success)
                        {
                            batchCancellation.Cancel();
                            // Observe each cancelled sibling so no abandoned probe outlives discovery.
                            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (OperationCanceledException) { }
                            token.ThrowIfCancellationRequested();
                            result.Message = "已自动识别并验证代理：" + result.Endpoint.DisplayName + "（" + result.Source + "）。";
                            return result;
                        }
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            return new ProxyDiscoveryResult
            {
                Source = "自动检测",
                Message = "未自动识别到可用的代理端口，请打开连接设置手动填写并保存；未使用代理软件时请选择直连。"
                    + (pacConfigured ? "系统 PAC 解析或连接未成功，请填写其中实际使用的代理地址和端口。" : "")
            };
        }

        public static async Task<ProxyDiscoveryResult> ProbeAsync(ProxyEndpoint endpoint, int capturePort, CancellationToken token, TimeSpan? timeout = null)
        {
            token.ThrowIfCancellationRequested();
            if (endpoint == null) return Failure("请填写代理地址和端口。");
            endpoint = endpoint.Clone();
            try { endpoint.Validate(capturePort); }
            catch (ArgumentException ex) { return Failure(ex.Message); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout ?? ProbeTimeout);
            using var socket = new TcpClient();
            try
            {
                await socket.ConnectAsync(endpoint.Host, endpoint.Port, deadline.Token).ConfigureAwait(false);
                using var stream = socket.GetStream();
                string error = endpoint.Protocol == ProxyProtocol.Socks5
                    ? await ProbeSocksAsync(stream, endpoint, deadline.Token).ConfigureAwait(false)
                    : await ProbeHttpAsync(stream, endpoint, deadline.Token).ConfigureAwait(false);
                if (error != null) return Failure(error);
                return new ProxyDiscoveryResult { Success = true, Endpoint = endpoint, Message = "代理隧道连接成功：" + endpoint.DisplayName + "。" };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failure("连接或代理握手超时。"); }
            catch (IOException) when (!token.IsCancellationRequested) { return Failure("代理连接中断或返回了无效响应。"); }
            catch (SocketException) when (!token.IsCancellationRequested) { return Failure("代理地址或端口不可连接。"); }
            finally { token.ThrowIfCancellationRequested(); }
        }

        private static ProxyDiscoveryResult Failure(string message) => new ProxyDiscoveryResult { Message = message };

        private static async Task<string> ProbeHttpAsync(NetworkStream stream, ProxyEndpoint endpoint, CancellationToken token)
        {
            string authority = ProbeHost + ":" + ProbePort;
            var request = new StringBuilder("CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\nProxy-Connection: Keep-Alive\r\n");
            if (!string.IsNullOrEmpty(endpoint.Username))
                request.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(endpoint.Username + ":" + endpoint.Password))).Append("\r\n");
            request.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), token).ConfigureAwait(false);
            byte[] buffer = new byte[8192];
            int used = 0;
            while (used < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(used, 1), token).ConfigureAwait(false);
                if (read == 0) return "HTTP 代理提前关闭了连接。";
                used += read;
                if (used >= 4 && buffer[used - 4] == 13 && buffer[used - 3] == 10 && buffer[used - 2] == 13 && buffer[used - 1] == 10) break;
                // Wrong SOCKS/other protocol listeners must not consume the whole timeout.
                if (used <= 5 && buffer[used - 1] != "HTTP/"[used - 1]) return "该端口不是 HTTP 代理。";
            }
            if (used == buffer.Length) return "HTTP 代理响应头过大。";
            string header = Encoding.ASCII.GetString(buffer, 0, used);
            var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var status = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (status.Length < 2 || (status[0] != "HTTP/1.1" && status[0] != "HTTP/1.0") || !int.TryParse(status[1], out int code)) return "该端口未返回有效的 HTTP 代理响应。";
            if (code == 407) return "代理要求认证，请在连接设置中填写用户名和密码。";
            if (code < 200 || code >= 300) return "代理未能建立微信 HTTPS 隧道（HTTP " + code.ToString(CultureInfo.InvariantCulture) + "）。";
            // A web/control service that simply renders its normal HTML page for CONNECT is not a proxy.
            if (lines.Skip(1).Any(line => line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                || (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && line[(line.IndexOf(':') + 1)..].Trim() != "0")))
                return "该端口返回网页内容，未建立代理隧道。";
            return null;
        }

        private static async Task<string> ProbeSocksAsync(NetworkStream stream, ProxyEndpoint endpoint, CancellationToken token)
        {
            bool authenticate = !string.IsNullOrEmpty(endpoint.Username);
            await stream.WriteAsync(authenticate ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, token).ConfigureAwait(false);
            byte[] reply = new byte[2];
            await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
            if (reply[0] != 5) return "该端口不是 SOCKS5 代理。";
            if (reply[1] == 255 || (reply[1] == 2 && !authenticate)) return "SOCKS5 代理要求认证，请在连接设置中填写用户名和密码。";
            if (reply[1] == 2)
            {
                byte[] name = Encoding.UTF8.GetBytes(endpoint.Username), password = Encoding.UTF8.GetBytes(endpoint.Password ?? "");
                byte[] auth = new byte[3 + name.Length + password.Length];
                auth[0] = 1; auth[1] = (byte)name.Length; name.CopyTo(auth, 2); auth[2 + name.Length] = (byte)password.Length; password.CopyTo(auth, 3 + name.Length);
                await stream.WriteAsync(auth, token).ConfigureAwait(false);
                Array.Clear(auth); Array.Clear(password);
                await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
                if (reply[0] != 1 || reply[1] != 0) return "SOCKS5 代理用户名或密码不正确。";
            }
            else if (reply[1] != 0) return "SOCKS5 代理使用了不支持的认证方式。";
            byte[] host = Encoding.ASCII.GetBytes(ProbeHost);
            byte[] connect = new byte[7 + host.Length];
            connect[0] = 5; connect[1] = 1; connect[2] = 0; connect[3] = 3; connect[4] = (byte)host.Length;
            host.CopyTo(connect, 5); connect[^2] = (byte)(ProbePort >> 8); connect[^1] = (byte)(ProbePort & 255);
            await stream.WriteAsync(connect, token).ConfigureAwait(false);
            byte[] response = new byte[4];
            await stream.ReadExactlyAsync(response, token).ConfigureAwait(false);
            if (response[0] != 5 || response[2] != 0) return "SOCKS5 代理返回了无效响应。";
            if (response[1] != 0) return "SOCKS5 代理未能建立微信 HTTPS 隧道（代码 " + response[1] + "）。";
            int tail;
            if (response[3] == 1) tail = 6;
            else if (response[3] == 4) tail = 18;
            else if (response[3] == 3)
            {
                byte[] length = new byte[1]; await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
                if (length[0] == 0) return "SOCKS5 代理返回了无效地址。";
                tail = length[0] + 2;
            }
            else return "SOCKS5 代理返回了无效地址类型。";
            await stream.ReadExactlyAsync(new byte[tail], token).ConfigureAwait(false);
            return null;
        }

        internal sealed class ProxyCandidate
        {
            public ProxyEndpoint Endpoint { get; set; }
            public string Source { get; set; } = "代理进程监听";
            public bool SystemProxy { get; set; }
            public int Priority { get; set; }
        }

        private sealed class DiscoveryEnvironment
        {
            public List<ProxyCandidate> Candidates { get; } = new List<ProxyCandidate>();
            public bool PacConfigured { get; set; }
            public bool ProxyPresent { get; set; }
        }

        private static DiscoveryEnvironment ReadCandidates(int capturePort)
        {
            var result = new DiscoveryEnvironment();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                result.PacConfigured = !string.IsNullOrWhiteSpace(key?.GetValue("AutoConfigURL") as string);
                using var connections = key?.OpenSubKey("Connections");
                if (connections?.GetValue("DefaultConnectionSettings") is byte[] connectionFlags && connectionFlags.Length > 8)
                    result.PacConfigured |= (connectionFlags[8] & 12) != 0;
                result.ProxyPresent = result.PacConfigured;
                if (Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0, CultureInfo.InvariantCulture) != 0)
                {
                    result.ProxyPresent = true;
                    result.Candidates.AddRange(ParseWindowsProxy(key?.GetValue("ProxyServer") as string, capturePort));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException || ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            { result.ProxyPresent = true; }

            var processes = new Dictionary<int, string>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try { if (CoreNames.Contains(process.ProcessName)) processes[process.Id] = process.ProcessName; }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException) { }
                }
            }
            if (processes.Count > 0)
            {
                result.ProxyPresent = true;
                foreach (var listener in ReadOwnedListeners(processes.Keys.ToHashSet()).OrderBy(l => l.Port))
                {
                    if (listener.Port == capturePort) continue;
                    foreach (var protocol in new[] { ProxyProtocol.Http, ProxyProtocol.Socks5 })
                        result.Candidates.Add(new ProxyCandidate
                        {
                            Endpoint = new ProxyEndpoint { Host = listener.Host, Port = listener.Port, Protocol = protocol },
                            Source = processes[listener.ProcessId]
                        });
                }
            }
            return result;
        }

        internal static IEnumerable<ProxyCandidate> ParseWindowsProxy(string value, int capturePort)
        {
            var candidates = new List<ProxyCandidate>();
            foreach (string entry in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string address = entry.Trim(), type = "https";
                int separator = address.IndexOf('=');
                if (separator >= 0) { type = address[..separator].Trim().ToLowerInvariant(); address = address[(separator + 1)..].Trim(); }
                if (type != "http" && type != "https" && type != "socks" && type != "socks5") continue;
                string scheme = type.StartsWith("socks", StringComparison.Ordinal) ? "socks5" : "http";
                if (!address.Contains("://", StringComparison.Ordinal)) address = scheme + "://" + address;
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
                    || (uri.Scheme != "http" && uri.Scheme != "socks" && uri.Scheme != "socks5") || uri.AbsolutePath != "/"
                    || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) continue;
                var endpoint = new ProxyEndpoint { Host = uri.Host.Trim('[', ']'), Port = uri.Port, Protocol = uri.Scheme.StartsWith("socks", StringComparison.Ordinal) ? ProxyProtocol.Socks5 : ProxyProtocol.Http };
                try { endpoint.Validate(capturePort); } catch (ArgumentException) { continue; }
                candidates.Add(new ProxyCandidate { Endpoint = endpoint, Source = "系统代理", SystemProxy = true,
                    Priority = type == "https" ? 0 : type == "http" ? 1 : 2 });
            }
            return candidates;
        }

        private sealed class OwnedListener { public string Host; public int Port; public int ProcessId; }

        private static IEnumerable<OwnedListener> ReadOwnedListeners(HashSet<int> owners)
        {
            var results = new List<OwnedListener>();
            foreach (int family in new[] { 2, 23 })
            {
                int size = 0;
                uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
                if (status != 122 || size < 4 || size > 16 * 1024 * 1024) continue;
                IntPtr memory = Marshal.AllocHGlobal(size);
                try
                {
                    status = GetExtendedTcpTable(memory, ref size, false, family, 3, 0);
                    if (status != 0) continue;
                    int rows = Marshal.ReadInt32(memory), rowSize = family == 2 ? 24 : 56;
                    if (rows < 0 || rows > (size - 4) / rowSize) continue;
                    for (int index = 0; index < rows; index++)
                    {
                        IntPtr row = IntPtr.Add(memory, 4 + index * rowSize);
                        int pid = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                        if (!owners.Contains(pid)) continue;
                        int rawPort = Marshal.ReadInt32(row, family == 2 ? 8 : 20);
                        int port = ((rawPort & 255) << 8) | ((rawPort >> 8) & 255);
                        byte[] addressBytes = new byte[family == 2 ? 4 : 16];
                        Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), addressBytes, 0, addressBytes.Length);
                        var address = family == 2 ? new IPAddress(addressBytes) : new IPAddress(addressBytes, (uint)Marshal.ReadInt32(row, 16));
                        if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
                        if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
                        results.Add(new OwnedListener { Host = address.ToString(), Port = port, ProcessId = pid });
                    }
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            return results;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    }
}
