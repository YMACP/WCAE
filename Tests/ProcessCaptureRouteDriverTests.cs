using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace WCAE
{
    public static class ProcessCaptureRouteDriverTests
    {
        public static async Task<int> RunHelperTestAsync(string reportPath, string probePath)
        {
            var report = new StringBuilder();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            CancellationToken token = deadline.Token;
            var original = new TcpListener(IPAddress.Loopback, 0); original.Start();
            var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
            int port = ((IPEndPoint)original.LocalEndpoint).Port;
            try
            {
                if (Path.GetFileName(probePath) != "WCAE-RouteProbe.exe" || !File.Exists(probePath)) throw new IOException("隔离辅助进程测试客户端无效。");
                await BundledTools.EnsureIngressAsync(token);
                using var owner = Process.GetCurrentProcess();
                for (int round = 0; round < 2; round++)
                {
                    string name = "WCAE-Capture-" + Guid.NewGuid().ToString("N");
                    using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath)
                    {
                        UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                        Arguments = "--process-capture-helper " + name + " " + owner.Id + " " + owner.StartTime.ToUniversalTime().Ticks
                    }) ?? throw new IOException("隔离接入辅助进程未启动。");
                    try
                    {
                        await pipe.WaitForConnectionAsync(token);
                        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint clientPid) || clientPid != helper.Id)
                            throw new IOException("隔离接入辅助进程身份不符。");
                        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                        var config = new ProcessCaptureRoute.HelperConfig
                        {
                            NativeDirectory = AppPaths.ToolsDirectory, GatewayPort = ((IPEndPoint)proxy.LocalEndpoint).Port,
                            Username = "helper-test", Password = Guid.NewGuid().ToString("N"),
                            Ports = new[] { port }, Processes = new[] { "WCAE-RouteProbe.exe" }
                        };
                        await writer.WriteLineAsync(JsonConvert.SerializeObject(config));
                        await ReadHelperStateAsync(reader, "READY", report, token);
                        string expectedAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Username + ":" + config.Password));
                        Task throughProxy = RespondAsync(proxy, true, token, expectedAuth);
                        string routed = await ProbeAsync(probePath, port, token); await throughProxy;
                        if (routed != "ROUTED") throw new IOException("隔离辅助进程未路由目标连接。");
                        report.AppendLine("PASS helper READY / authenticated named-pipe peer and parent path / authenticated synthetic route, round " + (round + 1));
                        if (round == 0)
                        {
                            await writer.WriteLineAsync("STOP");
                            await ReadHelperStateAsync(reader, "STOPPED", report, token);
                        }
                        else pipe.Dispose();
                        await helper.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(8), token);
                        if (helper.ExitCode != 0) throw new IOException("隔离接入辅助进程退出码：" + helper.ExitCode);
                        Task direct = RespondAsync(original, false, token);
                        string restored = await ProbeAsync(probePath, port, token); await direct;
                        if (restored != "DIRECT") throw new IOException("辅助进程退出后流量未恢复。");
                        report.AppendLine(round == 0 ? "PASS explicit STOP / helper exits / probe returns to direct" : "PASS pipe EOF / helper exits / probe returns to direct");
                    }
                    finally
                    {
                        pipe.Dispose();
                        if (!helper.HasExited)
                        { try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
                    }
                }
                report.AppendLine("No WeChat rules, system proxy settings or proxy-client configuration were changed.");
                await File.WriteAllTextAsync(reportPath, report.ToString()); return 0;
            }
            catch (Exception ex)
            { report.AppendLine("FAIL " + ex); await File.WriteAllTextAsync(reportPath, report.ToString()); return 1; }
            finally { deadline.Cancel(); original.Stop(); proxy.Stop(); }
        }
        static async Task ReadHelperStateAsync(StreamReader reader, string expected, StringBuilder report, CancellationToken token)
        {
            while (true)
            {
                string line = await reader.ReadLineAsync(token);
                if (line == expected) return;
                if (line == null) throw new IOException("辅助进程在 " + expected + " 前关闭连接。");
                if (line.StartsWith("ERROR ", StringComparison.Ordinal)) throw new IOException(line);
                if (line.StartsWith("LOG ", StringComparison.Ordinal)) report.AppendLine(line);
            }
        }
        public static async Task<int> RunAsync(string reportPath, string probePath)
        {
            var report = new StringBuilder();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            CancellationToken token = deadline.Token;
            var original = new TcpListener(IPAddress.Loopback, 0); original.Start();
            var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
            int port = ((IPEndPoint)original.LocalEndpoint).Port;
            try
            {
                if (Path.GetFileName(probePath) != "WCAE-RouteProbe.exe" || !File.Exists(probePath)) throw new IOException("隔离驱动测试客户端无效。");
                await BundledTools.EnsureIngressAsync(token);
                var config = new ProcessCaptureRoute.HelperConfig
                {
                    NativeDirectory = AppPaths.ToolsDirectory, GatewayPort = ((IPEndPoint)proxy.LocalEndpoint).Port,
                    Username = "route-test", Password = Guid.NewGuid().ToString("N"),
                    Ports = new[] { port }, Processes = new[] { "WCAE-RouteProbe.exe" }
                };
                using var route = new NativeProcessRoute(config, m => report.AppendLine(m));
                route.Start();
                Task throughProxy = RespondAsync(proxy, true, token);
                string redirected = await ProbeAsync(probePath, port, token);
                await throughProxy;
                if (redirected != "ROUTED") throw new IOException("目标测试进程未进入独立接入。");
                report.AppendLine("PASS signed WinDivert / target synthetic process redirected through authenticated HTTP CONNECT");

                Task directServer = RespondAsync(original, false, token);
                using (var ordinary = new TcpClient())
                {
                    await ordinary.ConnectAsync(IPAddress.Loopback, port, token);
                    await ordinary.GetStream().WriteAsync(Encoding.ASCII.GetBytes("PING"), token);
                    byte[] reply = new byte[6]; await ordinary.GetStream().ReadExactlyAsync(reply, token);
                    if (Encoding.ASCII.GetString(reply) != "DIRECT") throw new IOException("非目标进程的连接被修改。");
                }
                await directServer;
                report.AppendLine("PASS ordinary process bypasses redirect and retains original destination");

                route.Stop();
                directServer = RespondAsync(original, false, token);
                string restored = await ProbeAsync(probePath, port, token); await directServer;
                if (restored != "DIRECT") throw new IOException("停止独立接入后连接未恢复。");
                report.AppendLine("PASS driver handle closed / same synthetic process immediately direct again");
                report.AppendLine("No WeChat rules, system proxy settings or proxy-client configuration were changed.");
                await File.WriteAllTextAsync(reportPath, report.ToString()); return 0;
            }
            catch (Exception ex)
            { report.AppendLine("FAIL " + ex); await File.WriteAllTextAsync(reportPath, report.ToString()); return 1; }
            finally { deadline.Cancel(); original.Stop(); proxy.Stop(); }
        }
        static async Task<string> ProbeAsync(string path, int port, CancellationToken token)
        {
            using var process = Process.Start(new ProcessStartInfo(path, port.ToString())
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true });
            try
            {
                string result = await process.StandardOutput.ReadToEndAsync(token); await process.WaitForExitAsync(token);
                if (process.ExitCode != 0) throw new IOException("隔离测试客户端退出码：" + process.ExitCode); return result;
            }
            finally { if (!process.HasExited) process.Kill(); }
        }
        static async Task RespondAsync(TcpListener listener, bool connect, CancellationToken token, string expectedAuth = null)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            Stream stream = client.GetStream();
            if (connect)
            {
                string header = Encoding.ASCII.GetString(await ProcessCaptureRoute.ReadHeaderAsync(stream, token));
                if (!header.StartsWith("CONNECT 127.0.0.1:") || !header.Contains("Proxy-Authorization: Basic ", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("驱动TCP原目的地址或认证未正确传给网关。");
                if (expectedAuth != null && !header.Contains("Proxy-Authorization: Basic " + expectedAuth, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("辅助进程接入凭证不匹配。");
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), token);
            }
            byte[] request = new byte[4]; await stream.ReadExactlyAsync(request, token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(connect ? "ROUTED" : "DIRECT"), token);
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
    }
}
