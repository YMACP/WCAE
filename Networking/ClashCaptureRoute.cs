using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.RepresentationModel;

namespace WCAE
{
    /// <summary>Owns one temporary, authenticated WeChat-only route. Never writes the user's Clash YAML.</summary>
    public sealed class ClashCaptureRoute : IDisposable
    {
        internal const string CaptureRulePayload = "((DOMAIN,mp.weixin.qq.com),(NETWORK,tcp),(OR,((PROCESS-NAME,WeChatAppEx.exe),(PROCESS-NAME,Weixin.exe))))";
        // mihomo v1.19.25 Logic.Payload() renders semantic types and infix operators in GET /rules.
        internal const string CaptureRuntimePayload = "((Domain,mp.weixin.qq.com) && (Network,tcp) && (OR,((ProcessName,WeChatAppEx.exe) || (ProcessName,Weixin.exe))))";
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        readonly Action<string> status;
        readonly IClashLeaseStore store;
        readonly Action<ClashRouteLease> startWatchdog;
        readonly Func<JObject, bool> ownsWechatProcess;
        IClashControllerClient client;
        ClashRouteLease lease;
        bool disposed;
        public string CaptureUsername { get; }
        public string CapturePassword { get; }
        public bool IsActive { get; private set; }
        internal string NodeName { get; private set; }

        public ClashCaptureRoute(Action<string> status = null)
            : this(null, new ProtectedClashLeaseStore(Path.Combine(AppPaths.DataDirectory, "clash-route-lease.bin")), status, null, null) { }

        internal ClashCaptureRoute(IClashControllerClient client, IClashLeaseStore store, Action<string> status,
            Action<ClashRouteLease> startWatchdog, Func<JObject, bool> ownsWechatProcess)
        {
            this.client = client; this.store = store; this.status = status;
            this.startWatchdog = startWatchdog ?? LaunchWatchdog;
            this.ownsWechatProcess = ownsWechatProcess ?? WechatConnectionOwner.IsWechat;
            string nonce = Guid.NewGuid().ToString("N");
            NodeName = "WCAE_CAPTURE_" + nonce;
            CaptureUsername = "wcae_" + nonce;
            CapturePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }

        public async Task ActivateAsync(int capturePort, int upstreamPort, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            bool mayHaveApplied = false;
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (IsActive) return;
                if (capturePort < 1024 || capturePort > 65535 || upstreamPort < 1 || upstreamPort > 65535 || capturePort == upstreamPort)
                    throw new IOException("微信监听端口与 Clash 上游端口无效或冲突。");
                client ??= ClashControllerClient.Discover();
                var old = store.Read();
                if (old != null)
                {
                    if (OwnerAlive(old.OwnerPid, old.OwnerStartTicks)) throw new IOException("已有 WCAE 正在使用 Clash 微信分流，请先关闭该实例。");
                    await RestoreLeaseAsync(client, store, old, status, token).ConfigureAwait(false);
                }
                string raw = await client.ReadConfigurationAsync(token).ConfigureAwait(false);
                var before = await client.GetSnapshotAsync(token).ConfigureAwait(false);
                string mode = (string)before.General["mode"] ?? "";
                if (mode != "global" && mode != "rule") throw new IOException("当前 Clash 模式不支持微信分流，请使用规则或全局模式。");
                if ((int?)before.General["mixed-port"] != upstreamPort && (int?)before.General["port"] != upstreamPort)
                    throw new IOException("Clash HTTP 上游端口已变化，请重新启动监听。");
                if ((string)before.General["find-process-mode"] == "off") throw new IOException("Clash 已关闭进程识别，无法建立仅微信生效的分流。");
                if (before.Proxies[NodeName] != null) NodeName = "WCAE_CAPTURE_" + Guid.NewGuid().ToString("N");
                var selections = before.Selections();
                if (!selections.TryGetValue("GLOBAL", out string global) || string.IsNullOrWhiteSpace(global) || global.StartsWith("WCAE_CAPTURE_", StringComparison.Ordinal))
                    throw new IOException("Clash GLOBAL 当前选择不是有效的普通上游节点。");
                using var owner = Process.GetCurrentProcess();
                lease = new ClashRouteLease
                {
                    ConfigPath = client.ConfigPath, OriginalYaml = raw, Original = before, NodeName = NodeName,
                    CaptureUsername = CaptureUsername, CapturePassword = CapturePassword, OriginalMode = mode,
                    RawHash = Hash(raw), Baseline = Fingerprint(before), OriginalGlobalSelection = global,
                    OwnerPid = owner.Id, OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks,
                    InsertedRules = mode == "global" ? 2 : 1
                };
                string payload = BuildPayload(lease, capturePort);
                // Persist rollback data and arm recovery before the first runtime mutation.
                store.Write(lease);
                startWatchdog(lease);
                if (Hash(await client.ReadConfigurationAsync(token).ConfigureAwait(false)) != lease.RawHash
                    || Fingerprint(await client.GetSnapshotAsync(token).ConfigureAwait(false)) != lease.Baseline)
                    throw new IOException("Clash 配置刚刚发生变化，已取消分流；请重试。");
                mayHaveApplied = true;
                await client.ApplyPayloadAsync(payload, token).ConfigureAwait(false);
                await RestoreSelectionsAsync(client, selections, NodeName, token).ConfigureAwait(false);
                await RestoreDisabledAsync(client, before.Rules, lease.InsertedRules, token).ConfigureAwait(false);
                var after = await client.GetSnapshotAsync(token).ConfigureAwait(false);
                if (!MatchesOriginal(after, lease) || (string)after.Proxies["GLOBAL"]?["now"] != global)
                    throw new IOException("Clash 微信分流校验失败，正在撤销临时配置。");
                IsActive = true;
                Emit("微信专用分流已启用，系统代理保持 Clash；请刷新微信当前文章。");
            }
            catch
            {
                if (lease != null)
                {
                    if (mayHaveApplied)
                    {
                        try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(6)); await RestoreLeaseAsync(client, store, lease, status, cleanup.Token).ConfigureAwait(false); lease = null; }
                        catch { Emit("临时分流撤销未完成，恢复记录已保留；请保持 Clash 运行。"); }
                    }
                    else { store.Delete(); lease = null; }
                }
                throw;
            }
            finally { gate.Release(); }
        }

        public async Task<bool> RestoreAsync(CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var owned = lease;
                if (owned == null) return true;
                bool restored = await RestoreLeaseAsync(client, store, owned, status, token).ConfigureAwait(false);
                lease = null; IsActive = false; return restored;
            }
            finally { gate.Release(); }
        }

        public async Task<int> RefreshWechatConnectionsAsync(CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!IsActive) return 0;
                int count = 0;
                foreach (JObject connection in (await client.GetConnectionsAsync(token).ConfigureAwait(false)).OfType<JObject>())
                {
                    token.ThrowIfCancellationRequested();
                    var metadata = connection["metadata"] as JObject;
                    if (metadata == null || !string.Equals((string)metadata["host"], "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals((string)metadata["network"], "tcp", StringComparison.OrdinalIgnoreCase)
                        || !ownsWechatProcess(metadata)
                        || (connection["chains"] as JArray)?.Values<string>().Contains(NodeName, StringComparer.Ordinal) == true) continue;
                    string id = (string)connection["id"];
                    if (!Guid.TryParse(id, out _)) continue;
                    await client.CloseConnectionAsync(id, token).ConfigureAwait(false); count++;
                }
                Emit(count == 0 ? "分流已准备好，请在微信中刷新当前文章。" : "已刷新微信文章的旧连接，请重新打开或刷新当前文章。");
                return count;
            }
            finally { gate.Release(); }
        }

        public async Task<bool> CheckActiveAsync(CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!IsActive || lease == null) return false;
                var current = await client.GetSnapshotAsync(token).ConfigureAwait(false);
                if ((string)current.General["mode"] != "rule" || current.Proxies[lease.NodeName] == null) return false;
                if (current.Rules.Count == 0 || !IsCaptureRule(current.Rules[0], lease.NodeName) || Disabled(current.Rules[0])) return false;
                if (lease.OriginalMode == "global" && (current.Rules.Count < 2
                    || !string.Equals((string)current.Rules[1]["type"], "Match", StringComparison.OrdinalIgnoreCase)
                    || (string)current.Rules[1]["proxy"] != "GLOBAL" || Disabled(current.Rules[1]))) return false;
                return !current.Selections().Any(p => p.Value == lease.NodeName);
            }
            finally { gate.Release(); }
        }

        internal static string BuildPayload(ClashRouteLease lease, int port)
        {
            var root = ClashYaml.Parse(lease.OriginalYaml);
            ClashYaml.MergeJson(root, lease.Original.General);
            ClashYaml.Set(root, "mode", new YamlScalarNode("rule"));
            var groups = ClashYaml.Sequence(root, "proxy-groups");
            foreach (var group in groups.Children.OfType<YamlMappingNode>())
                if (ClashYaml.Scalar(group, "include-all").Equals("true", StringComparison.OrdinalIgnoreCase)
                    || ClashYaml.Scalar(group, "include-all-proxies").Equals("true", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("当前 Clash 含自动纳入所有节点的代理组，暂不接管，以免把监听节点选作上游。");
            var global = groups.Children.OfType<YamlMappingNode>().FirstOrDefault(g => ClashYaml.Scalar(g, "name") == "GLOBAL");
            if (global == null)
            {
                global = new YamlMappingNode { { "name", "GLOBAL" }, { "type", "select" } };
                groups.Add(global);
            }
            // Explicit candidates prevent the new HTTP node becoming a GLOBAL upstream (a proxy loop).
            var candidates = lease.Original.Proxies["GLOBAL"]?["all"] as JArray;
            if (candidates == null || !candidates.Values<string>().Contains(lease.OriginalGlobalSelection)) throw new IOException("Clash GLOBAL 候选列表无效。");
            ClashYaml.Set(global, "proxies", new YamlSequenceNode(candidates.Values<string>().Where(s => s != lease.NodeName).Select(s => new YamlScalarNode(s))));
            global.Children.Remove(new YamlScalarNode("use"));
            ClashYaml.Sequence(root, "proxies").Add(new YamlMappingNode
            {
                { "name", lease.NodeName }, { "type", "http" }, { "server", "127.0.0.1" }, { "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                { "username", lease.CaptureUsername }, { "password", lease.CapturePassword }
            });
            var rules = ClashYaml.Sequence(root, "rules");
            if (lease.OriginalMode == "global") rules.Children.Insert(0, new YamlScalarNode("MATCH,GLOBAL"));
            rules.Children.Insert(0, new YamlScalarNode("AND," + CaptureRulePayload + "," + lease.NodeName));
            return ClashYaml.Write(root);
        }

        internal static async Task<bool> RestoreLeaseAsync(IClashControllerClient client, IClashLeaseStore store, ClashRouteLease lease,
            Action<string> status, CancellationToken token)
        {
            var current = await client.GetSnapshotAsync(token).ConfigureAwait(false);
            // Even an external selection of the orphan node must not survive teardown.
            await GuardSelectionsAsync(client, current, lease, token).ConfigureAwait(false);
            current = await client.GetSnapshotAsync(token).ConfigureAwait(false);
            bool exact = Hash(await client.ReadConfigurationAsync(token).ConfigureAwait(false)) == lease.RawHash && MatchesOriginal(current, lease);
            bool hasOwned = current.Rules.OfType<JObject>().Any(r => (string)r["proxy"] == lease.NodeName);
            if (exact && hasOwned)
            {
                var selections = current.Selections();
                var original = ClashYaml.Parse(lease.OriginalYaml); ClashYaml.MergeJson(original, lease.Original.General);
                // Compare again immediately before a whole-payload operation.
                var check = await client.GetSnapshotAsync(token).ConfigureAwait(false);
                if (!MatchesOriginal(check, lease) || Hash(await client.ReadConfigurationAsync(token).ConfigureAwait(false)) != lease.RawHash) exact = false;
                else
                {
                    await client.ApplyPayloadAsync(ClashYaml.Write(original), token).ConfigureAwait(false);
                    await RestoreSelectionsAsync(client, selections, lease.NodeName, token).ConfigureAwait(false);
                    await RestoreDisabledAsync(client, lease.Original.Rules, 0, token).ConfigureAwait(false);
                    var verified = await client.GetSnapshotAsync(token).ConfigureAwait(false);
                    if (verified.Rules.OfType<JObject>().Any(r => (string)r["proxy"] == lease.NodeName)) throw new IOException("Clash 分流规则仍存在，恢复记录已保留。");
                    store.Delete(); status?.Invoke("已恢复 Clash 原运行配置，保留当前节点选择。"); return true;
                }
            }
            if (!hasOwned) { store.Delete(); status?.Invoke("Clash 已更新配置，当前已没有本次微信分流。"); return exact; }
            // Config changed elsewhere: touch only our nonce's current indexes; PATCH merges flags.
            current = await client.GetSnapshotAsync(token).ConfigureAwait(false);
            var disable = new Dictionary<int, bool>();
            for (int i = 0; i < current.Rules.Count; i++)
                if ((string)current.Rules[i]["proxy"] == lease.NodeName)
                {
                    disable[(int?)current.Rules[i]["index"] ?? i] = true;
                    // The exact nonce rule and its adjacent fallback were inserted as one ordered pair.
                    if (lease.OriginalMode == "global" && IsCaptureRule(current.Rules[i], lease.NodeName)
                        && i + 1 < current.Rules.Count && IsGlobalFallback(current.Rules[i + 1]))
                        disable[(int?)current.Rules[i + 1]["index"] ?? (i + 1)] = true;
                }
            if (disable.Count != 0) await client.SetRulesDisabledAsync(disable, token).ConfigureAwait(false);
            var safe = await client.GetSnapshotAsync(token).ConfigureAwait(false);
            if (safe.Rules.OfType<JObject>().Any(r => (string)r["proxy"] == lease.NodeName && !Disabled(r)))
                throw new IOException("Clash 临时规则尚未安全停用，恢复记录已保留。");
            await GuardSelectionsAsync(client, safe, lease, token).ConfigureAwait(false);
            store.Delete(); status?.Invoke("检测到 Clash 配置被其他操作更新；已停用本次微信规则及可确认的相邻回退规则，保留其他修改及未引用节点；无法确认归属的规则未改动。"); return false;
        }

        static async Task GuardSelectionsAsync(IClashControllerClient client, ClashRuntimeSnapshot current, ClashRouteLease lease, CancellationToken token)
        {
            foreach (var selection in current.Selections().Where(p => p.Value == lease.NodeName))
            {
                var all = (current.Proxies[selection.Key]?["all"] as JArray)?.Values<string>().ToArray() ?? Array.Empty<string>();
                lease.Original.Selections().TryGetValue(selection.Key, out string safe);
                if (string.IsNullOrEmpty(safe) || safe == lease.NodeName || !all.Contains(safe)) safe = all.FirstOrDefault(x => x != lease.NodeName && !x.StartsWith("WCAE_CAPTURE_", StringComparison.Ordinal));
                if (safe == null) throw new IOException("Clash 代理组正指向临时监听节点且无可恢复上游，请先切换普通节点。");
                await client.SelectAsync(selection.Key, safe, token).ConfigureAwait(false);
            }
        }

        static async Task RestoreSelectionsAsync(IClashControllerClient client, IDictionary<string, string> selections, string nodeName, CancellationToken token)
        {
            var current = await client.GetSnapshotAsync(token).ConfigureAwait(false);
            foreach (var selection in selections)
            {
                if (selection.Value == nodeName) throw new IOException("不能将监听节点作为 Clash 上游。");
                var proxy = current.Proxies[selection.Key];
                if (proxy == null || (string)proxy["now"] == selection.Value) continue;
                if ((proxy["all"] as JArray)?.Values<string>().Contains(selection.Value) == true)
                    await client.SelectAsync(selection.Key, selection.Value, token).ConfigureAwait(false);
            }
        }
        static async Task RestoreDisabledAsync(IClashControllerClient client, JArray rules, int shift, CancellationToken token)
        {
            var values = new Dictionary<int, bool>();
            for (int i = 0; i < rules.Count; i++) if (Disabled(rules[i])) values[i + shift] = true;
            if (values.Count != 0) await client.SetRulesDisabledAsync(values, token).ConfigureAwait(false);
        }
        internal static bool Disabled(JToken rule) => (bool?)rule["extra"]?["disabled"] == true;
        internal static bool IsCaptureRule(JToken rule, string nodeName) => (string)rule["proxy"] == nodeName
            && (string)rule["type"] == "AND" && (string)rule["payload"] == CaptureRuntimePayload;
        static bool IsGlobalFallback(JToken rule) => string.Equals((string)rule["type"], "Match", StringComparison.OrdinalIgnoreCase)
            && (string)rule["proxy"] == "GLOBAL" && string.IsNullOrEmpty((string)rule["payload"]);
        internal static bool MatchesOriginal(ClashRuntimeSnapshot current, ClashRouteLease lease)
        {
            var normalized = new ClashRuntimeSnapshot
            {
                General = (JObject)current.General.DeepClone(), Proxies = (JObject)current.Proxies.DeepClone(), Rules = (JArray)current.Rules.DeepClone()
            };
            normalized.Proxies.Remove(lease.NodeName);
            if (normalized.Proxies["GLOBAL"]?["all"] is JArray all)
                for (int i = all.Count - 1; i >= 0; i--) if ((string)all[i] == lease.NodeName) all.RemoveAt(i);
            int owned = -1;
            for (int i = 0; i < normalized.Rules.Count; i++) if ((string)normalized.Rules[i]["proxy"] == lease.NodeName) { if (owned >= 0) return false; owned = i; }
            if (owned != 0 || !IsCaptureRule(normalized.Rules[owned], lease.NodeName)) return false;
            if (lease.OriginalMode == "global")
            {
                if (normalized.Rules.Count < 2 || !string.Equals((string)normalized.Rules[1]["type"], "Match", StringComparison.OrdinalIgnoreCase)
                    || (string)normalized.Rules[1]["proxy"] != "GLOBAL" || Disabled(normalized.Rules[1])) return false;
                normalized.Rules.RemoveAt(1);
            }
            normalized.Rules.RemoveAt(0);
            if ((string)normalized.General["mode"] != "rule") return false;
            normalized.General["mode"] = lease.OriginalMode;
            return Fingerprint(normalized) == lease.Baseline;
        }
        internal static string Fingerprint(ClashRuntimeSnapshot snapshot)
        {
            var proxies = new JObject();
            string[] stable = { "name", "type", "all", "udp", "uot", "xudp", "tfo", "mptcp", "smux", "interface", "routing-mark", "provider-name", "dialer-proxy" };
            foreach (var property in snapshot.Proxies.Properties())
            {
                var proxy = new JObject(); foreach (string key in stable) if (property.Value[key] != null) proxy[key] = property.Value[key].DeepClone();
                proxies[property.Name] = proxy;
            }
            var rules = new JArray(snapshot.Rules.Select(rule => new JObject
            {
                ["type"] = rule["type"]?.DeepClone(), ["payload"] = rule["payload"]?.DeepClone(), ["proxy"] = rule["proxy"]?.DeepClone(),
                ["size"] = rule["size"]?.DeepClone(), ["disabled"] = Disabled(rule)
            }));
            return Hash(Canonical(new JObject { ["general"] = snapshot.General.DeepClone(), ["proxies"] = proxies, ["rules"] = rules }).ToString(Formatting.None));
        }
        static JToken Canonical(JToken token)
        {
            if (token is JObject obj) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value))));
            if (token is JArray array) return new JArray(array.Select(Canonical)); return token.DeepClone();
        }
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        void Emit(string text) { try { status?.Invoke(text); } catch { } }

        void LaunchWatchdog(ClashRouteLease record)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("无法定位恢复辅助程序。"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--clash-route-watchdog"); start.ArgumentList.Add(store.Path);
            start.ArgumentList.Add(record.OwnerPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(record.OwnerStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(start) ?? throw new IOException("无法启动 Clash 异常退出恢复程序。");
        }
        public static async Task<int> RunWatchdogAsync(string leasePath, int ownerPid, long ownerStartTicks)
        {
            try
            {
                try
                {
                    using var process = Process.GetProcessById(ownerPid);
                    if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == ownerStartTicks)
                        await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (ArgumentException) { } // Owner may exit between lookup and the wait being armed.
                var storage = new ProtectedClashLeaseStore(leasePath); var record = storage.Read();
                if (record == null || record.OwnerPid != ownerPid || record.OwnerStartTicks != ownerStartTicks) return 0;
                using var client = ClashControllerClient.FromConfig(record.ConfigPath);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await RestoreLeaseAsync(client, storage, record, null, timeout.Token).ConfigureAwait(false); return 0;
            }
            catch { return 2; }
        }
        static bool OwnerAlive(int pid, long startTicks)
        {
            try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks; }
            catch { return false; }
        }
        public void Dispose()
        {
            if (disposed) return;
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); RestoreAsync(timeout.Token).GetAwaiter().GetResult(); }
            finally { disposed = true; client?.Dispose(); }
        }
    }

    internal sealed class ClashRouteLease
    {
        public string ConfigPath { get; set; }
        public string OriginalYaml { get; set; }
        public ClashRuntimeSnapshot Original { get; set; }
        public string NodeName { get; set; }
        public string CaptureUsername { get; set; }
        public string CapturePassword { get; set; }
        public string OriginalMode { get; set; }
        public string OriginalGlobalSelection { get; set; }
        public string RawHash { get; set; }
        public string Baseline { get; set; }
        public int InsertedRules { get; set; }
        public int OwnerPid { get; set; }
        public long OwnerStartTicks { get; set; }
    }
    internal interface IClashLeaseStore { string Path { get; } ClashRouteLease Read(); void Write(ClashRouteLease lease); void Delete(); }
    internal sealed class ProtectedClashLeaseStore : IClashLeaseStore
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WCAE.ClashRoute.v1");
        public string Path { get; }
        public ProtectedClashLeaseStore(string path) { Path = System.IO.Path.GetFullPath(path); }
        public ClashRouteLease Read()
        {
            if (!File.Exists(Path)) return null;
            try
            {
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser);
                try { return JsonConvert.DeserializeObject<ClashRouteLease>(Encoding.UTF8.GetString(plain)) ?? throw new IOException("Clash 恢复记录为空。"); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (CryptographicException) { throw new IOException("Clash 恢复记录无法由当前用户解密，已保留原文件。"); }
        }
        public void Write(ClashRouteLease lease)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            byte[] plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(lease));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, Path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public void Delete() { if (File.Exists(Path)) File.Delete(Path); }
    }

    internal static class WechatConnectionOwner
    {
        public static bool IsWechat(JObject metadata)
        {
            string process = (string)metadata["process"] ?? (string)metadata["processName"];
            string path = (string)metadata["processPath"];
            if (IsName(process) || IsName(path)) return true;
            // A present non-WeChat owner is authoritative. Empty strict-mode metadata can use local TCP ownership.
            if (!string.IsNullOrEmpty(process) || !string.IsNullOrEmpty(path)) return false;
            if (!IPAddress.TryParse((string)metadata["sourceIP"], out var address)
                || !int.TryParse((string)metadata["sourcePort"], out int port)) return false;
            try
            {
                int family = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 23 : 2;
                int size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
                if (size <= 4 || size > 16 * 1024 * 1024) return false;
                IntPtr memory = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(memory, ref size, false, family, 5, 0) != 0) return false;
                    int count = Marshal.ReadInt32(memory); int rowSize = family == 2 ? 24 : 56;
                    for (int i = 0; i < count && 4 + (i + 1) * rowSize <= size; i++)
                    {
                        IntPtr row = IntPtr.Add(memory, 4 + i * rowSize); int portOffset = family == 2 ? 8 : 20;
                        if (Marshal.ReadByte(row, portOffset) * 256 + Marshal.ReadByte(row, portOffset + 1) != port) continue;
                        if (Marshal.ReadInt32(row, family == 2 ? 0 : 48) != 5) continue;
                        byte[] bytes = new byte[family == 2 ? 4 : 16]; Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                        if (!new IPAddress(bytes).Equals(address)) continue;
                        using var owner = Process.GetProcessById(Marshal.ReadInt32(row, family == 2 ? 20 : 52));
                        return IsName(owner.ProcessName);
                    }
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            catch { }
            return false;
        }
        static bool IsName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string file = System.IO.Path.GetFileNameWithoutExtension(name);
            return file.Equals("WeChatAppEx", StringComparison.OrdinalIgnoreCase) || file.Equals("Weixin", StringComparison.OrdinalIgnoreCase) || file.Equals("WeChat", StringComparison.OrdinalIgnoreCase);
        }
        [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    }
}
