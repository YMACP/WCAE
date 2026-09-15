using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace WCAE
{
    /// <summary>Pure restore planning and isolated encrypted-file tests. Never reads/writes Windows proxy settings or launches a watchdog.</summary>
    public static class SystemProxyCaptureRouteSelfTests
    {
        public static async Task RunAsync()
        {
            RestoreOriginalSettings();
            PreserveExternalChanges();
            RecoverInterruptedApply();
            ValidateWatchdogIdentity();
            await ProtectedLeaseAndLock().ConfigureAwait(false);
        }

        private static Dictionary<string, SystemProxyValue> Before()
            => new Dictionary<string, SystemProxyValue>(StringComparer.Ordinal)
            {
                ["ProxyEnable"] = SystemProxyValue.Dword(0),
                ["ProxyServer"] = SystemProxyValue.String("http=127.0.0.1:10809;https=127.0.0.1:10809"),
                ["ProxyOverride"] = new SystemProxyValue { Exists = true, Kind = RegistryValueKind.ExpandString, Text = "<local>;*.internal.fixture" },
                ["AutoConfigURL"] = SystemProxyValue.String("http://127.0.0.1:38080/fixture.pac"),
                ["AutoDetect"] = new SystemProxyValue(),
                ["ConnectionFlags"] = SystemProxyValue.Dword(13)
            };
        private static SystemProxyLease Fixture()
            => SystemProxyLease.Create(8879, Before(), 2345, 123456789, "S-1-5-21-fixture");

        private static void RestoreOriginalSettings()
        {
            var record = Fixture(); record.AppliedSuccessfully = true;
            record = JsonConvert.DeserializeObject<SystemProxyLease>(JsonConvert.SerializeObject(record));
            var planned = record.PlanRestore(new Dictionary<string, SystemProxyValue>(record.Applied), out bool complete);
            Check(complete && SystemProxyCaptureRoute.SnapshotSame(planned, record.Before), "正常退出恢复代理、PAC、自动检测、绕过列表和原值类型/存在性");
            Check(record.Applied["ConnectionFlags"].Number == 3 && !record.Applied["AutoConfigURL"].Exists, "兼容接入必须关闭PAC/自动检测并打开显式代理");
            Check(record.Applied["ProxyOverride"].Text == record.Before["ProxyOverride"].Text, "原有绕过列表必须保留");
        }

        private static void PreserveExternalChanges()
        {
            var record = Fixture(); record.AppliedSuccessfully = true;
            var changed = new Dictionary<string, SystemProxyValue>(record.Applied)
                { ["ProxyServer"] = SystemProxyValue.String("127.0.0.1:39090") };
            Check(record.PlanRestore(changed, out bool complete) == null && !complete, "另一个程序切换出口后不能覆盖它的新代理");

            changed = new Dictionary<string, SystemProxyValue>(record.Applied)
                { ["ProxyOverride"] = SystemProxyValue.String("user-edited-bypass") };
            var planned = record.PlanRestore(changed, out complete);
            Check(!complete && planned["ProxyOverride"].Text == "user-edited-bypass" && planned["ProxyServer"].Same(record.Before["ProxyServer"]), "保留用户绕过列表，同时移除本工具失效监听端口");

            changed = new Dictionary<string, SystemProxyValue>(record.Applied)
                { ["ProxyEnable"] = SystemProxyValue.Dword(0), ["ConnectionFlags"] = SystemProxyValue.Dword(1) };
            planned = record.PlanRestore(changed, out complete);
            Check(planned["ProxyEnable"].Number == 0 && (planned["ConnectionFlags"].Number & 2) == 0, "保留用户主动关闭代理的选择");

            changed = new Dictionary<string, SystemProxyValue>(record.Applied)
            {
                ["AutoConfigURL"] = SystemProxyValue.String("http://127.0.0.1:38081/user-new.pac"),
                ["AutoDetect"] = SystemProxyValue.Dword(1), ["ConnectionFlags"] = SystemProxyValue.Dword(15)
            };
            planned = record.PlanRestore(changed, out complete);
            Check(!complete && planned["AutoConfigURL"].Text.EndsWith("user-new.pac", StringComparison.Ordinal)
                && planned["AutoDetect"].Number == 1 && (planned["ConnectionFlags"].Number & 12) == 12,
                "用户在运行期间启用的新PAC和自动检测不得被清除");
        }

        private static void RecoverInterruptedApply()
        {
            var record = Fixture();
            foreach (int written in Enumerable.Range(0, SystemProxyRegistry.Names.Length + 1))
            {
                var partial = new Dictionary<string, SystemProxyValue>(record.Before);
                foreach (string name in SystemProxyRegistry.Names.Take(written)) partial[name] = record.Applied[name];
                var planned = record.PlanRestore(partial, out bool complete);
                Check(complete && SystemProxyCaptureRoute.SnapshotSame(planned, record.Before), "持久化但未确认应用的lease必须恢复任意写入中断点：" + written);
            }
            var malformed = Fixture(); malformed.Applied.Remove("ConnectionFlags");
            bool rejected = false; try { malformed.Validate(); } catch (IOException) { rejected = true; }
            Check(rejected, "不完整的恢复文件不能用于修改系统设置");
        }

        private static void ValidateWatchdogIdentity()
        {
            var record = Fixture();
            Check(SystemProxyCaptureRoute.MatchesWatchdog(record, record.OwnerPid, record.OwnerStartTicks, record.Nonce, record.OwnerSid), "准确的看门狗owner身份匹配");
            Check(!SystemProxyCaptureRoute.MatchesWatchdog(record, record.OwnerPid, record.OwnerStartTicks + 1, record.Nonce, record.OwnerSid), "启动时间必须阻止PID复用");
            Check(!SystemProxyCaptureRoute.MatchesWatchdog(record, record.OwnerPid, record.OwnerStartTicks, Guid.NewGuid().ToString("N"), record.OwnerSid), "nonce必须隔离不同接入会话");
            Check(!SystemProxyCaptureRoute.MatchesWatchdog(record, record.OwnerPid, record.OwnerStartTicks, record.Nonce, "S-other"), "看门狗不能恢复别的用户记录");
            Check(SystemProxyCaptureRoute.ValidateWatchdogPath(SystemProxyCaptureRoute.DefaultLeasePath), "允许当前用户固定lease路径");
            Check(!SystemProxyCaptureRoute.ValidateWatchdogPath(Path.Combine(AppPaths.DataDirectory, "other-file.bin"))
                && !SystemProxyCaptureRoute.ValidateWatchdogPath(Path.Combine(AppPaths.DataDirectory, "..", "system-proxy-lease.bin")), "拒绝其他文件及目录逃逸路径");
        }

        private static async Task ProtectedLeaseAndLock()
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WCAE-system-route-tests-" + Guid.NewGuid().ToString("N")));
            var store = new SystemProxyLeaseStore(Path.Combine(root, "fixture-lease.bin"));
            try
            {
                var record = Fixture();
                using (var held = await store.AcquireAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    store.Write(record);
                    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(store.Path)).Contains("fixture.pac"), "系统代理及PAC记录必须按当前用户加密");
                    var loaded = store.Read();
                    Check(loaded.Nonce == record.Nonce && loaded.OwnerStartTicks == record.OwnerStartTicks && SystemProxyCaptureRoute.SnapshotSame(loaded.Before, record.Before), "完整快照加密往返");
                    using var cancellation = new CancellationTokenSource(120);
                    bool cancelled = false;
                    try { using var blocked = await store.AcquireAsync(cancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled, "恢复锁必须互斥且支持取消，不能卡住退出");
                }
                using (var held = await store.AcquireAsync(CancellationToken.None).ConfigureAwait(false)) { store.Delete(); Check(store.Read() == null, "恢复完成后清除lease"); }
            }
            finally
            {
                string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new IOException("隔离测试目录不在临时目录内。");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception("SystemProxyCaptureRoute: " + message); }
    }
}
