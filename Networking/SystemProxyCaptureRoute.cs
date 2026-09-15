using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace WCAE
{
    /// <summary>Explicit compatibility mode. All registry writes run as the ordinary current user.</summary>
    public sealed class SystemProxyCaptureRoute : IDisposable
    {
        private readonly Action<string> status;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly SystemProxyLeaseStore store = new SystemProxyLeaseStore(DefaultLeasePath);
        private SystemProxyLease lease;
        private NamedPipeServerStream watchdogPipe;
        private Process watchdog;
        private bool disposed;
        public string CaptureUsername => "";
        public string CapturePassword => "";
        public bool IsActive { get; private set; }
        internal static string DefaultLeasePath => Path.Combine(AppPaths.DataDirectory, "system-proxy-lease.bin");

        public SystemProxyCaptureRoute(Action<string> status = null) { this.status = status; }

        public static async Task<bool> RecoverOrphanedAsync(Action<string> status, CancellationToken token)
        {
            var storage = new SystemProxyLeaseStore(DefaultLeasePath);
            if (!File.Exists(storage.Path)) return false;
            using var held = await storage.AcquireAsync(token).ConfigureAwait(false);
            var record = storage.Read();
            if (record == null || OwnerAlive(record.OwnerPid, record.OwnerStartTicks)) return false;
            RestoreOwned(storage, record, status); return true;
        }

        public async Task ActivateAsync(int capturePort, int upstreamPort, CancellationToken token)
        {
            if (capturePort < 1 || capturePort > 65535 || capturePort == upstreamPort) throw new ArgumentException("系统代理接入端口无效或与上游冲突。");
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (IsActive) return;
                using (var held = await store.AcquireAsync(token).ConfigureAwait(false))
                {
                    var old = store.Read();
                    if (old != null)
                    {
                        old.Validate();
                        if (OwnerAlive(old.OwnerPid, old.OwnerStartTicks)) throw new IOException("已有 WCAE 正在使用系统代理兼容接入。");
                        RestoreOwned(store, old, status);
                    }
                    using var owner = Process.GetCurrentProcess();
                    lease = SystemProxyLease.Create(capturePort, SystemProxyRegistry.Read(), owner.Id, owner.StartTime.ToUniversalTime().Ticks, CurrentSid());
                    store.Write(lease);
                }
                try
                {
                    // Never switch Windows to a listener that has not actually started.
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    using (var client = new TcpClient())
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(2));
                        await client.ConnectAsync(IPAddress.Loopback, capturePort, deadline.Token).ConfigureAwait(false);
                    }
                    await StartWatchdogAsync(lease, token).ConfigureAwait(false);
                    using var held = await store.AcquireAsync(token).ConfigureAwait(false);
                    var saved = store.Read();
                    if (saved == null || saved.Nonce != lease.Nonce || !SnapshotSame(SystemProxyRegistry.Read(), lease.Before))
                        throw new IOException("系统代理在接入准备期间已变化，请重新接入。");
                    if (watchdog.HasExited || !watchdogPipe.IsConnected) throw new IOException("系统代理恢复助手未就绪。");
                    token.ThrowIfCancellationRequested();
                    // A pre-apply lease is already durable, covering a crash between individual registry writes.
                    SystemProxyRegistry.Write(lease.Applied);
                    lease.AppliedSuccessfully = true;
                    store.Write(lease);
                    IsActive = true;
                    Emit("系统代理兼容接入已开启；关闭 WCAE 时会恢复原设置。请在微信中刷新文章。");
                }
                catch
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { using var held = await store.AcquireAsync(recovery.Token).ConfigureAwait(false); RestoreOwned(store, lease, status); }
                    catch { /* Keep the durable lease; the already-armed watchdog performs recovery. */ }
                    ReleaseWatchdog(); lease = null; IsActive = false;
                    throw;
                }
            }
            finally { gate.Release(); }
        }

        public Task<bool> CheckActiveAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsActive || lease == null) return Task.FromResult(false);
            var current = SystemProxyRegistry.Read();
            bool active = current["ProxyServer"].Same(lease.Applied["ProxyServer"])
                && current["ProxyEnable"].Same(lease.Applied["ProxyEnable"])
                && watchdog != null && !watchdog.HasExited;
            return Task.FromResult(active);
        }

        public Task<int> RefreshWechatConnectionsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (IsActive) SystemProxyRegistry.NotifyChanged();
            // Existing WeChat sockets are not destroyed in a system-wide compatibility mode.
            return Task.FromResult(0);
        }

        public async Task<bool> RestoreAsync(CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (lease == null) { IsActive = false; ReleaseWatchdog(); return true; }
                using var held = await store.AcquireAsync(token).ConfigureAwait(false);
                var saved = store.Read();
                bool complete = true;
                if (saved != null && saved.Nonce == lease.Nonce) complete = RestoreOwned(store, saved, status);
                lease = null; IsActive = false; ReleaseWatchdog();
                return complete;
            }
            finally { gate.Release(); }
        }

        private async Task StartWatchdogAsync(SystemProxyLease record, CancellationToken token)
        {
            watchdogPipe = new NamedPipeServerStream(PipeName(record.Nonce), PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("无法定位系统代理恢复助手。"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--system-proxy-watchdog"); start.ArgumentList.Add(store.Path);
            start.ArgumentList.Add(record.OwnerPid.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(record.OwnerStartTicks.ToString(CultureInfo.InvariantCulture)); start.ArgumentList.Add(record.Nonce);
            watchdog = Process.Start(start) ?? throw new IOException("系统代理恢复助手启动失败。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            await watchdogPipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            using var reader = new StreamReader(watchdogPipe, Encoding.UTF8, false, 1024, true);
            if (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) != "READY " + record.Nonce)
                throw new IOException("系统代理恢复助手握手失败。");
        }

        public static async Task<int> RunWatchdogAsync(string[] args)
        {
            if (args == null || args.Length != 5 || args[0] != "--system-proxy-watchdog"
                || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int ownerPid)
                || !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out long ownerTicks)) return 2;
            try
            {
                if (!ValidateWatchdogPath(args[1]) || !ValidNonce(args[4])) return 2;
                var storage = new SystemProxyLeaseStore(args[1]);
                SystemProxyLease record;
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                using (var held = await storage.AcquireAsync(deadline.Token).ConfigureAwait(false)) record = storage.Read();
                if (!MatchesWatchdog(record, ownerPid, ownerTicks, args[4], CurrentSid())) return 2;
                var exited = WaitForOwnerAsync(ownerPid, ownerTicks);
                using var pipe = new NamedPipeClientStream(".", PipeName(record.Nonce), PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                        await writer.WriteLineAsync("READY " + record.Nonce).ConfigureAwait(false);
                    // Pipe closure also requests recovery when the route is stopped but the GUI remains open.
                    var disconnected = pipe.ReadAsync(new byte[1]).AsTask();
                    await Task.WhenAny(exited, disconnected).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException || ex is OperationCanceledException) { }
                // Retry recoverable registry/file contention. A failed record stays available for next startup.
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        using var held = await storage.AcquireAsync(deadline.Token).ConfigureAwait(false);
                        var latest = storage.Read();
                        if (latest == null || !MatchesWatchdog(latest, ownerPid, ownerTicks, args[4], CurrentSid())) return 0;
                        RestoreOwned(storage, latest, null); return 0;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is OperationCanceledException)
                    { if (attempt == 3) return 2; await Task.Delay(250).ConfigureAwait(false); }
                }
                return 2;
            }
            catch { return 2; }
        }

        internal static bool ValidateWatchdogPath(string path)
        {
            try { return string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(DefaultLeasePath), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return false; }
        }
        internal static bool MatchesWatchdog(SystemProxyLease record, int pid, long ticks, string nonce, string sid)
        {
            if (record == null || pid < 1 || ticks < 1 || !ValidNonce(nonce)) return false;
            record.Validate();
            return record.OwnerPid == pid && record.OwnerStartTicks == ticks && record.Nonce == nonce && record.OwnerSid == sid;
        }
        private static string PipeName(string nonce) => "WCAE-SystemProxy-" + nonce;
        internal static bool ValidNonce(string nonce) => nonce?.Length == 32 && nonce.All(Uri.IsHexDigit);
        private static string CurrentSid() => WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("无法读取当前用户标识。");
        private static bool OwnerAlive(int pid, long ticks)
        {
            try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { return false; }
        }
        private static async Task WaitForOwnerAsync(int pid, long ticks)
        {
            try
            {
                using var owner = Process.GetProcessById(pid);
                if (!owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == ticks) await owner.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
        }
        private static bool RestoreOwned(SystemProxyLeaseStore storage, SystemProxyLease record, Action<string> status)
        {
            record.Validate();
            if (record.OwnerSid != CurrentSid()) throw new IOException("系统代理恢复记录不属于当前用户。");
            var current = SystemProxyRegistry.Read();
            var planned = record.PlanRestore(current, out bool complete);
            if (planned != null && !SnapshotSame(current, planned)) SystemProxyRegistry.Write(planned);
            storage.Delete();
            try { status?.Invoke(complete ? "已恢复原系统代理设置。" : "已撤销本次系统代理接入，并保留其他程序或用户的新设置。"); } catch { }
            return complete;
        }
        internal static bool SnapshotSame(Dictionary<string, SystemProxyValue> left, Dictionary<string, SystemProxyValue> right)
            => SystemProxyRegistry.Names.All(name => left.TryGetValue(name, out var value) && right.TryGetValue(name, out var other) && value.Same(other));
        private void Emit(string message) { try { status?.Invoke(message); } catch { } }
        private void ReleaseWatchdog()
        {
            watchdogPipe?.Dispose(); watchdogPipe = null;
            watchdog?.Dispose(); watchdog = null;
        }
        public void Dispose()
        {
            if (disposed) return;
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); RestoreAsync(timeout.Token).GetAwaiter().GetResult(); }
            finally { disposed = true; ReleaseWatchdog(); }
        }
    }

    internal sealed class SystemProxyValue
    {
        public bool Exists { get; set; }
        public RegistryValueKind Kind { get; set; }
        public string Text { get; set; }
        public int Number { get; set; }
        public byte[] Bytes { get; set; }
        public bool Same(SystemProxyValue other) => other != null && Exists == other.Exists && (!Exists || Kind == other.Kind && Text == other.Text && Number == other.Number
            && (Bytes == null && other.Bytes == null || Bytes != null && other.Bytes != null && Bytes.SequenceEqual(other.Bytes)));
        public static SystemProxyValue String(string value) => new SystemProxyValue { Exists = true, Kind = RegistryValueKind.String, Text = value };
        public static SystemProxyValue Dword(int value) => new SystemProxyValue { Exists = true, Kind = RegistryValueKind.DWord, Number = value };
        public static SystemProxyValue Binary(byte[] value) => new SystemProxyValue { Exists = true, Kind = RegistryValueKind.Binary, Bytes = value };
    }

    internal sealed class SystemProxyLease
    {
        public int Version { get; set; } = 1;
        public int OwnerPid { get; set; }
        public long OwnerStartTicks { get; set; }
        public string OwnerSid { get; set; }
        public string Nonce { get; set; }
        public int CapturePort { get; set; }
        public bool AppliedSuccessfully { get; set; }
        public Dictionary<string, SystemProxyValue> Before { get; set; }
        public Dictionary<string, SystemProxyValue> Applied { get; set; }

        public static SystemProxyLease Create(int port, Dictionary<string, SystemProxyValue> before, int ownerPid, long ticks, string sid)
        {
            string target = "http=127.0.0.1:" + port + ";https=127.0.0.1:" + port;
            string bypass = before.TryGetValue("ProxyOverride", out var oldBypass) && oldBypass.Exists ? oldBypass.Text ?? "" : "<local>";
            var applied = new Dictionary<string, SystemProxyValue>(before, StringComparer.Ordinal)
            {
                ["ProxyEnable"] = SystemProxyValue.Dword(1), ["ProxyServer"] = SystemProxyValue.String(target),
                ["ProxyOverride"] = SystemProxyValue.String(bypass), ["AutoConfigURL"] = new SystemProxyValue(), ["AutoDetect"] = SystemProxyValue.Dword(0),
                ["ConnectionFlags"] = SystemProxyValue.Dword((before["ConnectionFlags"].Number & ~15) | 3)
            };
            var record = new SystemProxyLease { OwnerPid = ownerPid, OwnerStartTicks = ticks, OwnerSid = sid,
                Nonce = Guid.NewGuid().ToString("N"), CapturePort = port, Before = before, Applied = applied };
            record.Validate(); return record;
        }

        public void Validate()
        {
            if (Version != 1 || OwnerPid < 1 || OwnerStartTicks < 1 || string.IsNullOrEmpty(OwnerSid) || !SystemProxyCaptureRoute.ValidNonce(Nonce)
                || CapturePort < 1 || CapturePort > 65535 || Before == null || Applied == null)
                throw new IOException("系统代理恢复记录无效。");
            foreach (string name in SystemProxyRegistry.Names)
                if (!Before.TryGetValue(name, out var before) || before == null || !Applied.TryGetValue(name, out var applied) || applied == null)
                    throw new IOException("系统代理恢复记录不完整。");
            if (Applied["ProxyServer"].Text != "http=127.0.0.1:" + CapturePort + ";https=127.0.0.1:" + CapturePort || Applied["ProxyEnable"].Number != 1)
                throw new IOException("系统代理恢复记录的监听目标不匹配。");
        }

        public Dictionary<string, SystemProxyValue> PlanRestore(Dictionary<string, SystemProxyValue> current, out bool complete)
        {
            Validate(); complete = false;
            foreach (string name in SystemProxyRegistry.Names) if (!current.ContainsKey(name) || current[name] == null) throw new IOException("当前系统代理快照不完整。");
            if (AppliedSuccessfully && !current["ProxyServer"].Same(Applied["ProxyServer"])) return null;
            if (!AppliedSuccessfully && !current["ProxyServer"].Same(Applied["ProxyServer"]) && !current["ProxyServer"].Same(Before["ProxyServer"])) return null;
            var planned = new Dictionary<string, SystemProxyValue>(current, StringComparer.Ordinal);
            bool changedElsewhere = false;
            foreach (string name in SystemProxyRegistry.Names)
            {
                if (current[name].Same(Applied[name])) planned[name] = Before[name];
                else if (current[name].Same(Before[name])) { }
                else if (name == "ConnectionFlags")
                {
                    int flags = current[name].Number, prior = Before[name].Number, expected = Applied[name].Number;
                    int changedBits = prior ^ expected;
                    for (int bit = 0; bit < 32; bit++)
                    {
                        int mask = 1 << bit;
                        if ((changedBits & mask) != 0 && (flags & mask) == (expected & mask)) flags = (flags & ~mask) | (prior & mask);
                    }
                    planned[name] = SystemProxyValue.Dword(flags);
                    changedElsewhere = true;
                }
                else changedElsewhere = true;
            }
            complete = !changedElsewhere;
            return planned;
        }
    }

    internal static class SystemProxyRegistry
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        internal static readonly string[] Names = { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL", "AutoDetect", "ConnectionFlags" };
        public static Dictionary<string, SystemProxyValue> Read()
        {
            var snapshot = new Dictionary<string, SystemProxyValue>(StringComparer.Ordinal);
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
            foreach (string name in Names)
            {
                if (name == "ConnectionFlags") { snapshot[name] = SystemProxyValue.Dword(QueryFlags()); continue; }
                object value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) { snapshot[name] = new SystemProxyValue(); continue; }
                var kind = key.GetValueKind(name);
                if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString && kind != RegistryValueKind.DWord)
                    throw new IOException("Windows 系统代理注册表类型不受支持。");
                snapshot[name] = new SystemProxyValue { Exists = true, Kind = kind, Text = value as string, Number = value is int number ? number : 0 };
            }
            return snapshot;
        }
        public static void Write(Dictionary<string, SystemProxyValue> snapshot)
        {
            // Let WinINet maintain its version-specific Connections records. Never synthesize their binary layout.
            SetConnectionOptions(snapshot["ConnectionFlags"].Number, snapshot["ProxyServer"].Text ?? "", snapshot["ProxyOverride"].Text ?? "", snapshot["AutoConfigURL"].Text ?? "");
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                foreach (string name in Names)
                {
                    if (name == "ConnectionFlags") continue;
                    // Restore the captured root-value existence and kind after the official API synchronizes settings.
                    var value = snapshot[name];
                    if (!value.Exists) key.DeleteValue(name, false);
                    else key.SetValue(name, value.Kind == RegistryValueKind.DWord ? (object)value.Number : value.Text ?? "", value.Kind);
                }
                key.Flush();
            }
            NotifyChanged();
        }
        internal static void NotifyChanged()
        {
            if (!InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0) || !InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0))
                throw new IOException("Windows 系统代理刷新失败（" + Marshal.GetLastWin32Error() + "）。");
        }
        [DllImport("wininet.dll", SetLastError = true)] private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct ConnectionOption
        {
            [FieldOffset(0)] public int Option;
            [FieldOffset(8)] public int Number;
            [FieldOffset(8)] public IntPtr Text;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ConnectionOptionList
        {
            public int Size; public IntPtr Connection; public int Count; public int Error; public IntPtr Options;
        }
        private static int QueryFlags()
        {
            IntPtr options = Marshal.AllocHGlobal(16);
            try
            {
                foreach (int kind in new[] { 10, 1 }) // FLAGS_UI first on Windows 7+, FLAGS for compatibility.
                {
                    Marshal.StructureToPtr(new ConnectionOption { Option = kind }, options, false);
                    var list = new ConnectionOptionList { Size = Marshal.SizeOf<ConnectionOptionList>(), Count = 1, Options = options };
                    int size = list.Size;
                    if (InternetQueryOptionW(IntPtr.Zero, 75, ref list, ref size)) return Marshal.PtrToStructure<ConnectionOption>(options).Number;
                }
                throw new IOException("Windows 未能读取当前连接代理标志。");
            }
            finally { Marshal.FreeHGlobal(options); }
        }
        private static void SetConnectionOptions(int flags, string proxy, string bypass, string pac)
        {
            IntPtr options = Marshal.AllocHGlobal(64);
            IntPtr proxyText = IntPtr.Zero, bypassText = IntPtr.Zero, pacText = IntPtr.Zero;
            try
            {
                proxyText = Marshal.StringToHGlobalUni(proxy); bypassText = Marshal.StringToHGlobalUni(bypass); pacText = Marshal.StringToHGlobalUni(pac);
                Marshal.StructureToPtr(new ConnectionOption { Option = 1, Number = flags }, options, false);
                Marshal.StructureToPtr(new ConnectionOption { Option = 2, Text = proxyText }, IntPtr.Add(options, 16), false);
                Marshal.StructureToPtr(new ConnectionOption { Option = 3, Text = bypassText }, IntPtr.Add(options, 32), false);
                Marshal.StructureToPtr(new ConnectionOption { Option = 4, Text = pacText }, IntPtr.Add(options, 48), false);
                var list = new ConnectionOptionList { Size = Marshal.SizeOf<ConnectionOptionList>(), Count = 4, Options = options };
                if (!InternetSetOptionW(IntPtr.Zero, 75, ref list, list.Size)) throw new IOException("Windows 未能设置当前用户连接代理（" + Marshal.GetLastWin32Error() + "）。");
            }
            finally
            {
                Marshal.FreeHGlobal(proxyText); Marshal.FreeHGlobal(bypassText); Marshal.FreeHGlobal(pacText); Marshal.FreeHGlobal(options);
            }
        }
        [DllImport("wininet.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetQueryOptionW(IntPtr handle, int option, ref ConnectionOptionList list, ref int size);
        [DllImport("wininet.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetSetOptionW(IntPtr handle, int option, ref ConnectionOptionList list, int size);
    }

    internal sealed class SystemProxyLeaseStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WCAE.SystemProxyRoute.v1");
        public string Path { get; }
        public SystemProxyLeaseStore(string path) { Path = System.IO.Path.GetFullPath(path); }
        public async Task<FileStream> AcquireAsync(CancellationToken token)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); }
                catch (IOException) { await Task.Delay(40, token).ConfigureAwait(false); }
            }
        }
        public SystemProxyLease Read()
        {
            if (!File.Exists(Path)) return null;
            if (new FileInfo(Path).Length > 4 * 1024 * 1024) throw new IOException("系统代理恢复文件过大。");
            byte[] clear;
            try { clear = ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { throw new IOException("系统代理恢复记录无法由当前用户读取，已保留文件。"); }
            try
            {
                var record = JsonConvert.DeserializeObject<SystemProxyLease>(Encoding.UTF8.GetString(clear));
                if (record == null) throw new IOException("系统代理恢复记录为空。"); record.Validate(); return record;
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        public void Write(SystemProxyLease record)
        {
            record.Validate(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            byte[] clear = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(record)); byte[] encrypted;
            try { encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(encrypted); stream.Flush(true); }
                if (File.Exists(Path)) File.Replace(temporary, Path, null); else File.Move(temporary, Path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public void Delete() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
