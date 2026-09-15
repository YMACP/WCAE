using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WCAE
{
    /// <summary>Loaded only inside the short-lived elevated helper. ProxyBridge uses the
    /// original signed WinDivert driver; WCAE's patch restricts the native packet filter.</summary>
    internal sealed class NativeProcessRoute : IDisposable
    {
        readonly ProcessCaptureRoute.HelperConfig config;
        readonly Action<string> status;
        IntPtr driver, core;
        NativeLog callback;
        StopDelegate stop;
        bool started;
        volatile bool stopping;
        string lastError;
        internal NativeProcessRoute(ProcessCaptureRoute.HelperConfig config, Action<string> status)
        { this.config = config; this.status = status; }
        internal void Start()
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new IOException("独立微信接入需要 Windows 管理员权限。");
            driver = LoadLibraryEx(Path.Combine(config.NativeDirectory, "WinDivert.dll"), IntPtr.Zero, 0x00000100 | 0x00000800);
            if (driver == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "加载微信接入驱动组件失败。");
            core = LoadLibraryEx(Path.Combine(config.NativeDirectory, "ProxyBridgeCore.dll"), IntPtr.Zero, 0x00000100 | 0x00000800);
            if (core == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "加载微信接入核心失败。");
            callback = line =>
            {
                string message = Marshal.PtrToStringUTF8(line) ?? "";
                if (stopping && message.Contains("Failed to receive packet (232)", StringComparison.Ordinal)) return;
                if (message.Contains("failed", StringComparison.OrdinalIgnoreCase)) { lastError = message; status?.Invoke("接入组件：" + message); }
            };
            Api<SetLogDelegate>("ProxyBridge_SetLogCallback")(callback);
            Api<SetBooleanDelegate>("ProxyBridge_SetTrafficLoggingEnabled")(false);
            Api<SetBooleanDelegate>("ProxyBridge_SetLocalhostViaProxy")(true);
            string ports = string.Join(";", config.Ports);
            var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
            int relayPort = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            if (!Api<ConfigureDelegate>("Wcae_ConfigureCapture")(ports, (ushort)relayPort)) throw new IOException("微信接入端口过滤配置失败。");
            uint proxy = Api<AddProxyDelegate>("ProxyBridge_AddProxyConfig")(0, "127.0.0.1", (ushort)config.GatewayPort, config.Username, config.Password, false);
            if (proxy == 0) throw new IOException("微信接入网关配置失败。");
            var addRule = Api<AddRuleDelegate>("ProxyBridge_AddRule");
            foreach (string process in config.Processes)
                if (addRule(process, "*", ports, "", 0, 0, proxy) == 0) throw new IOException("微信进程接入规则创建失败。");
            stop = Api<StopDelegate>("ProxyBridge_Stop");
            if (!Api<StopDelegate>("ProxyBridge_Start")())
                throw new IOException("独立微信接入未启动。" + (lastError ?? "请检查驱动组件是否被 Windows 阻止。"));
            started = true;
        }
        T Api<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(core, name));
        internal void Stop() { if (started) { stopping = true; started = false; stop(); } }
        public void Dispose()
        {
            Stop();
            // Do not unload a DLL containing asynchronous relay callbacks; the short-lived
            // helper process exits immediately after this object and releases both modules.
            GC.KeepAlive(callback);
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void NativeLog(IntPtr message);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetLogDelegate(NativeLog callback);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetBooleanDelegate([MarshalAs(UnmanagedType.Bool)] bool value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] delegate bool ConfigureDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string ports, ushort relayPort);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] delegate bool StopDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint AddProxyDelegate(int type, [MarshalAs(UnmanagedType.LPUTF8Str)] string host, ushort port, [MarshalAs(UnmanagedType.LPUTF8Str)] string user, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, [MarshalAs(UnmanagedType.Bool)] bool domain);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint AddRuleDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string process, [MarshalAs(UnmanagedType.LPUTF8Str)] string hosts, [MarshalAs(UnmanagedType.LPUTF8Str)] string ports, [MarshalAs(UnmanagedType.LPUTF8Str)] string domains, int protocol, int action, uint proxy);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    }
}
