using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.RepresentationModel;

namespace WCAE
{
    /// <summary>Offline controller/lease doubles only: no listener, controller request, certificate, or system proxy changes.</summary>
    public static class ClashCaptureRouteSelfTests
    {
        public static async Task RunAsync()
        {
            await GlobalRouteAndRestoreAsync();
            await RuleModeAndConflictAsync();
            await PreflightRaceAsync();
            await CancelledApplyAsync();
            await PreparedRecoveryAsync();
            await RejectAutomaticGroupsAsync();
            await GlobalConflictAndRetryAsync();
            await CleanupFailureRetainsLeaseAsync();
            await ControllerWireFormatAsync();
            TestMetadataOwner();
        }
        static ClashCaptureRoute Route(FakeClient client, MemoryStore store, List<string> messages = null)
            => new ClashCaptureRoute(client, store, text => messages?.Add(text), _ => { }, m => (string)m["process"] == "WeChatAppEx.exe");

        static async Task GlobalRouteAndRestoreAsync()
        {
            var client = new FakeClient("global"); var store = new MemoryStore(); var messages = new List<string>();
            using var route = Route(client, store, messages);
            string original = client.Raw;
            await route.ActivateAsync(8879, 7897, CancellationToken.None);
            Assert(await route.CheckActiveAsync(CancellationToken.None), "active route health");
            var yaml = ClashYaml.Parse(client.LastPayload);
            var rules = ClashYaml.Sequence(yaml, "rules");
            Assert(((YamlScalarNode)rules[0]).Value.Contains("(DOMAIN,mp.weixin.qq.com),(NETWORK,tcp),(OR,((PROCESS-NAME,WeChatAppEx.exe),(PROCESS-NAME,Weixin.exe)))"), "domain/network/process rule");
            Assert(((YamlScalarNode)rules[1]).Value == "MATCH,GLOBAL", "global non-WeChat routing preserved");
            Assert(rules.Children.Count == 4 && ((YamlScalarNode)rules[2]).Value == "DOMAIN,example.test,DIRECT", "original rules retained behind global wrapper");
            Assert(ClashYaml.Scalar(yaml, "custom-test-field") == "preserve-me", "unknown configuration retained");
            var node = ClashYaml.Sequence(yaml, "proxies").Children.OfType<YamlMappingNode>().Single(p => ClashYaml.Scalar(p, "name") == route.NodeName);
            Assert(ClashYaml.Scalar(node, "username") == route.CaptureUsername && ClashYaml.Scalar(node, "password") == route.CapturePassword
                && route.CapturePassword.Length >= 32, "per-instance authentication");
            Assert(!(client.State.Proxies["GLOBAL"]["all"] as JArray).Values<string>().Contains(route.NodeName), "GLOBAL cannot select the capture node");
            Assert((string)client.State.Proxies["GLOBAL"]["now"] == "Proxy-A", "initial GLOBAL selection restored after payload reload");
            Assert(ClashCaptureRoute.Disabled(client.State.Rules[3]), "original disabled rule shifted correctly");
            Assert(client.Raw == original, "user configuration file unchanged");
            client.State.Proxies["GLOBAL"]["now"] = "Proxy-B";
            Assert(await route.CheckActiveAsync(CancellationToken.None), "dynamic selector change is valid");
            string eligible = Guid.NewGuid().ToString(); string redirected = Guid.NewGuid().ToString();
            client.Connections = new JArray(Connection(eligible, "mp.weixin.qq.com", "WeChatAppEx.exe"),
                Connection(Guid.NewGuid().ToString(), "example.test", "WeChatAppEx.exe"),
                Connection(Guid.NewGuid().ToString(), "mp.weixin.qq.com", "Chrome.exe"),
                Connection(Guid.NewGuid().ToString(), "mp.weixin.qq.com", ""),
                Connection(redirected, "mp.weixin.qq.com", "WeChatAppEx.exe", route.NodeName));
            Assert(await route.RefreshWechatConnectionsAsync(CancellationToken.None) == 1 && client.Closed.SequenceEqual(new[] { eligible }), "only confirmed old WeChat article connections closed");
            Assert(await route.RestoreAsync(CancellationToken.None), "exact configuration restored");
            Assert((string)client.State.General["mode"] == "global" && (string)client.State.Proxies["GLOBAL"]["now"] == "Proxy-B", "original global mode and user's latest selection retained");
            Assert(client.State.Rules.Count == 2 && ClashCaptureRoute.Disabled(client.State.Rules[1]) && store.Record == null, "original rule disabled state and lease removal");
            Assert(!messages.Any(s => s.Contains(route.CapturePassword) || s.Contains(route.CaptureUsername)), "credentials excluded from status");
        }

        static async Task RuleModeAndConflictAsync()
        {
            var client = new FakeClient("rule"); var store = new MemoryStore(); using var route = Route(client, store);
            await route.ActivateAsync(8879, 7897, CancellationToken.None);
            Assert(client.State.Rules.Count == 3 && (string)client.State.Rules[1]["type"] == "Domain", "rule mode gets only one prefix");
            client.State.General["log-level"] = "debug";
            client.State.Rules.Insert(0, Rule("DOMAIN,user-added.test,Proxy-B", 0));
            Reindex(client.State.Rules);
            client.State.Rules[0]["extra"]["disabled"] = true;
            client.DisableCalls.Clear();
            Assert(!await route.CheckActiveAsync(CancellationToken.None), "changed route order reported unhealthy");
            Assert(!await route.RestoreAsync(CancellationToken.None), "external changes cause surgical fallback");
            Assert(client.ApplyCalls == 1 && (string)client.State.General["log-level"] == "debug", "conflict never triggers full reload");
            Assert(client.DisableCalls.Count == 1 && client.DisableCalls[0].Count == 1 && client.DisableCalls[0][1], "PATCH disables only owned rule at current index");
            Assert(ClashCaptureRoute.Disabled(client.State.Rules[0]) && ClashCaptureRoute.Disabled(client.State.Rules[1]) && ClashCaptureRoute.Disabled(client.State.Rules[3]), "other disabled flags retained");
            Assert(store.Record == null, "safe fallback clears lease");
        }

        static async Task PreflightRaceAsync()
        {
            var client = new FakeClient("global"); var store = new MemoryStore(); using var route = Route(client, store);
            client.BeforeSnapshot = count => { if (count == 2) client.State.General["log-level"] = "debug"; };
            await ThrowsAsync<IOException>(() => route.ActivateAsync(8879, 7897, CancellationToken.None), "preflight fingerprint race");
            Assert(client.ApplyCalls == 0 && store.Record == null, "race cancels before any payload mutation");
        }

        static async Task CancelledApplyAsync()
        {
            var client = new FakeClient("global"); var store = new MemoryStore(); using var route = Route(client, store);
            using var cts = new CancellationTokenSource();
            client.AfterApply = calls => { if (calls == 1) { cts.Cancel(); throw new OperationCanceledException(cts.Token); } };
            await ThrowsAsync<OperationCanceledException>(() => route.ActivateAsync(8879, 7897, cts.Token), "cancel during apply");
            Assert(!client.State.Rules.Any(r => (string)r["proxy"] == route.NodeName && !ClashCaptureRoute.Disabled(r)), "cancel leaves no live dead-proxy route");
            Assert(store.Record == null, "cancel cleanup uses an independent token");
        }

        static async Task PreparedRecoveryAsync()
        {
            var client = new FakeClient("global"); var store = new MemoryStore(); using var route = Route(client, store);
            await route.ActivateAsync(8879, 7897, CancellationToken.None);
            var lease = store.Record;
            // JSON roundtrip represents an independent helper reading the persisted pre-apply lease.
            var recovered = JsonConvert.DeserializeObject<ClashRouteLease>(JsonConvert.SerializeObject(lease));
            Assert(await ClashCaptureRoute.RestoreLeaseAsync(client, store, recovered, null, CancellationToken.None), "helper restores from pre-apply encrypted-record contents");
            Assert((string)client.State.General["mode"] == "global", "helper restores original mode");
            // Dispose's second restore sees no owned rule and must not apply another payload.
            int calls = client.ApplyCalls;
            await route.RestoreAsync(CancellationToken.None);
            Assert(client.ApplyCalls == calls, "restoration is idempotent");
        }

        static async Task RejectAutomaticGroupsAsync()
        {
            var client = new FakeClient("global"); var store = new MemoryStore(); using var route = Route(client, store);
            client.Raw += "proxy-groups:\n  - name: Auto\n    type: select\n    include-all: true\n";
            await ThrowsAsync<IOException>(() => route.ActivateAsync(8879, 7897, CancellationToken.None), "automatic groups could select capture node");
            Assert(client.ApplyCalls == 0, "unsafe automatic inclusion rejected before reload");
        }
        static async Task GlobalConflictAndRetryAsync()
        {
            var client = new FakeClient("global"); var store = new MemoryStore(); using var route = Route(client, store);
            await route.ActivateAsync(8879, 7897, CancellationToken.None);
            client.State.General["log-level"] = "debug";
            client.DisableCalls.Clear();
            Assert(!await route.RestoreAsync(CancellationToken.None), "global conflicting configuration uses fallback");
            Assert(client.DisableCalls.Count == 1 && client.DisableCalls[0].Count == 2 && client.DisableCalls[0][0] && client.DisableCalls[0][1], "own GLOBAL fallback also disabled");
            Assert(ClashCaptureRoute.Disabled(client.State.Rules[3]), "preexisting disabled flag survives global cleanup");
            Assert(client.ApplyCalls == 1, "global conflict avoids reload");
        }
        static async Task CleanupFailureRetainsLeaseAsync()
        {
            var client = new FakeClient("global"); var store = new MemoryStore(); using var route = Route(client, store);
            client.AfterApply = calls => { if (calls == 1) client.BeforeSnapshot = _ => throw new IOException("offline injected controller failure"); };
            await ThrowsAsync<IOException>(() => route.ActivateAsync(8879, 7897, CancellationToken.None), "apply cleanup failure");
            Assert(store.Record != null, "failed rollback preserves lease");
            client.BeforeSnapshot = null;
            await route.RestoreAsync(CancellationToken.None);
            Assert(store.Record == null && !client.State.Rules.Any(r => (string)r["proxy"] == route.NodeName && !ClashCaptureRoute.Disabled(r)), "subsequent cleanup retries retained lease");
        }
        static void TestMetadataOwner()
        {
            Assert(WechatConnectionOwner.IsWechat(new JObject { ["processPath"] = @"C:\Program Files\Tencent\Weixin.exe" }), "Weixin metadata path");
            Assert(!WechatConnectionOwner.IsWechat(new JObject { ["process"] = "NotWeChatAppEx.exe" }), "exact process matching");
        }
        static async Task ControllerWireFormatAsync()
        {
            var handler = new RecordingHandler(); using var client = new ClashControllerClient(handler);
            await client.SetRulesDisabledAsync(new Dictionary<int, bool> { [7] = true, [11] = false }, CancellationToken.None);
            Assert(handler.Method == "PATCH" && handler.Path == "/rules/disable", "mihomo PATCH endpoint");
            var body = JObject.Parse(handler.Body);
            Assert(body.Count == 2 && (bool)body["7"] && !(bool)body["11"], "PATCH index-to-boolean partial map");
            await client.ApplyPayloadAsync("mode: rule\n", CancellationToken.None);
            Assert(handler.Method == "PUT" && handler.Path == "/configs?force=false"
                && (string)JObject.Parse(handler.Body)["payload"] == "mode: rule\n", "payload reload preserves listeners and uses structured body");
        }
        sealed class RecordingHandler : HttpMessageHandler
        {
            public string Method, Path, Body;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Method = request.Method.Method; Path = request.RequestUri.PathAndQuery;
                Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }
        static JObject Connection(string id, string host, string process, string chain = "Proxy-A") => new JObject
        {
            ["id"] = id, ["metadata"] = new JObject { ["host"] = host, ["network"] = "tcp", ["process"] = process }, ["chains"] = new JArray(chain)
        };
        static JObject Rule(string value, int index)
        {
            int first = value.IndexOf(','); int last = value.LastIndexOf(','); string type = value.Substring(0, first);
            return new JObject { ["index"] = index, ["type"] = type == "MATCH" ? "Match" : type == "AND" ? "AND" : "Domain",
                ["payload"] = type == "AND" ? ClashCaptureRoute.CaptureRuntimePayload : first == last ? "" : value.Substring(first + 1, last - first - 1), ["proxy"] = value.Substring(last + 1),
                ["size"] = -1, ["extra"] = new JObject { ["disabled"] = false, ["hitCount"] = 0 } };
        }
        static void Reindex(JArray rules) { for (int i = 0; i < rules.Count; i++) rules[i]["index"] = i; }
        static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException("Clash route self-test: " + name); }
        static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
        { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Clash route self-test expected " + typeof(T).Name + ": " + name); }

        sealed class MemoryStore : IClashLeaseStore
        {
            public string Path => "offline-memory";
            public ClashRouteLease Record;
            public ClashRouteLease Read() => Record;
            public void Write(ClashRouteLease lease) => Record = JsonConvert.DeserializeObject<ClashRouteLease>(JsonConvert.SerializeObject(lease));
            public void Delete() => Record = null;
        }
        sealed class FakeClient : IClashControllerClient
        {
            public string ConfigPath => @"C:\offline\clash.yaml";
            public string Raw;
            public ClashRuntimeSnapshot State;
            public string LastPayload;
            public int ApplyCalls;
            int snapshots;
            public Action<int> BeforeSnapshot;
            public Action<int> AfterApply;
            public JArray Connections = new JArray();
            public List<string> Closed = new List<string>();
            public List<Dictionary<int, bool>> DisableCalls = new List<Dictionary<int, bool>>();
            public FakeClient(string mode)
            {
                Raw = "mixed-port: 7897\nmode: " + mode + "\nfind-process-mode: strict\nlog-level: info\ncustom-test-field: preserve-me\n"
                    + "proxies:\n  - {name: Proxy-A, type: http, server: 127.0.0.1, port: 10001}\n  - {name: Proxy-B, type: http, server: 127.0.0.1, port: 10002}\n"
                    + "rules:\n  - DOMAIN,example.test,DIRECT\n  - MATCH,Proxy-A\n";
                State = Parse(Raw); State.Proxies["GLOBAL"]["now"] = "Proxy-A"; State.Rules[1]["extra"]["disabled"] = true;
            }
            public Task<string> ReadConfigurationAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Raw); }
            public Task<ClashRuntimeSnapshot> GetSnapshotAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); BeforeSnapshot?.Invoke(++snapshots);
                return Task.FromResult(JsonConvert.DeserializeObject<ClashRuntimeSnapshot>(JsonConvert.SerializeObject(State)));
            }
            public Task ApplyPayloadAsync(string yaml, CancellationToken token)
            { token.ThrowIfCancellationRequested(); LastPayload = yaml; ApplyCalls++; State = Parse(yaml); AfterApply?.Invoke(ApplyCalls); return Task.CompletedTask; }
            public Task SelectAsync(string group, string selection, CancellationToken token)
            { token.ThrowIfCancellationRequested(); State.Proxies[group]["now"] = selection; return Task.CompletedTask; }
            public Task SetRulesDisabledAsync(IDictionary<int, bool> values, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); DisableCalls.Add(new Dictionary<int, bool>(values));
                foreach (var entry in values) State.Rules[entry.Key]["extra"]["disabled"] = entry.Value; return Task.CompletedTask;
            }
            public Task<JArray> GetConnectionsAsync(CancellationToken token) => Task.FromResult(Connections);
            public Task CloseConnectionAsync(string id, CancellationToken token) { Closed.Add(id); return Task.CompletedTask; }
            public void Dispose() { }
            static ClashRuntimeSnapshot Parse(string yaml)
            {
                var root = ClashYaml.Parse(yaml);
                var result = new ClashRuntimeSnapshot
                {
                    General = new JObject { ["mode"] = ClashYaml.Scalar(root, "mode"), ["mixed-port"] = 7897, ["find-process-mode"] = "strict", ["log-level"] = ClashYaml.Scalar(root, "log-level") },
                    Proxies = new JObject { ["DIRECT"] = new JObject { ["name"] = "DIRECT", ["type"] = "Direct", ["udp"] = true } }
                };
                foreach (var node in ClashYaml.Sequence(root, "proxies").Children.OfType<YamlMappingNode>())
                { string name = ClashYaml.Scalar(node, "name"); result.Proxies[name] = new JObject { ["name"] = name, ["type"] = "Http", ["udp"] = false }; }
                var names = result.Proxies.Properties().Select(p => p.Name).ToArray();
                result.Proxies["GLOBAL"] = new JObject { ["name"] = "GLOBAL", ["type"] = "Selector", ["udp"] = true, ["now"] = "DIRECT", ["all"] = new JArray(names) };
                foreach (var group in ClashYaml.Sequence(root, "proxy-groups").Children.OfType<YamlMappingNode>())
                {
                    string name = ClashYaml.Scalar(group, "name");
                    result.Proxies[name] = new JObject { ["name"] = name, ["type"] = "Selector", ["udp"] = true, ["now"] = "DIRECT",
                        ["all"] = new JArray(ClashYaml.Sequence(group, "proxies").Children.OfType<YamlScalarNode>().Select(n => n.Value)) };
                }
                result.Rules = new JArray(ClashYaml.Sequence(root, "rules").Children.OfType<YamlScalarNode>().Select((n, i) => Rule(n.Value, i)));
                return result;
            }
        }
    }
}
