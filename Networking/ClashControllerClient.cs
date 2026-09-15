using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.RepresentationModel;

namespace WCAE
{
    public sealed class ClashRuntimeSnapshot
    {
        public JObject General { get; set; } = new JObject();
        public JObject Proxies { get; set; } = new JObject();
        public JArray Rules { get; set; } = new JArray();
        public Dictionary<string, string> Selections() => Proxies.Properties()
            .Where(p => string.Equals((string)p.Value["type"], "Selector", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Name, p => (string)p.Value["now"] ?? "", StringComparer.Ordinal);
    }

    public interface IClashControllerClient : IDisposable
    {
        string ConfigPath { get; }
        Task<string> ReadConfigurationAsync(CancellationToken token);
        Task<ClashRuntimeSnapshot> GetSnapshotAsync(CancellationToken token);
        Task ApplyPayloadAsync(string yaml, CancellationToken token);
        Task SelectAsync(string group, string selection, CancellationToken token);
        Task SetRulesDisabledAsync(IDictionary<int, bool> values, CancellationToken token);
        Task<JArray> GetConnectionsAsync(CancellationToken token);
        Task CloseConnectionAsync(string id, CancellationToken token);
    }

    /// <summary>Local controller only. Authentication and configuration payloads never enter status messages.</summary>
    public sealed class ClashControllerClient : IClashControllerClient
    {
        readonly HttpClient client;
        public string ConfigPath { get; }

        public static ClashControllerClient Discover()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "io.github.clash-verge-rev.clash-verge-rev", "clash-verge.yaml");
            if (!File.Exists(path)) throw new IOException("未找到 Clash Verge 当前运行配置，请先启动 Clash Verge。");
            return FromConfig(path);
        }

        public static ClashControllerClient FromConfig(string path)
            => new ClashControllerClient(Path.GetFullPath(path), File.ReadAllText(path, Encoding.UTF8));

        internal ClashControllerClient(HttpMessageHandler offlineHandler)
        {
            ConfigPath = "offline-controller";
            client = new HttpClient(offlineHandler) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromSeconds(35) };
        }

        internal ClashControllerClient(string path, string configuration)
        {
            ConfigPath = path;
            var document = ClashYaml.Parse(configuration);
            string pipe = ClashYaml.Scalar(document, "external-controller-pipe");
            string address = ClashYaml.Scalar(document, "external-controller");
            string secret = ClashYaml.Scalar(document, "secret");
            var handler = new SocketsHttpHandler
            {
                UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            Uri baseAddress;
            if (!string.IsNullOrWhiteSpace(pipe))
            {
                const string prefix = @"\\.\pipe\";
                if (!pipe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || pipe.Length <= prefix.Length)
                    throw new IOException("Clash 控制器管道地址不是本机命名管道。");
                string pipeName = pipe.Substring(prefix.Length);
                handler.ConnectCallback = async (context, token) =>
                {
                    var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try { await stream.ConnectAsync(5000, token).ConfigureAwait(false); return stream; }
                    catch { stream.Dispose(); throw; }
                };
                baseAddress = new Uri("http://localhost/");
            }
            else
            {
                if (!Uri.TryCreate("http://" + address, UriKind.Absolute, out baseAddress) || !baseAddress.IsLoopback
                    || !string.IsNullOrEmpty(baseAddress.UserInfo))
                    throw new IOException("Clash 未启用可访问的本机控制器。");
            }
            client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(35) };
            if (!string.IsNullOrEmpty(secret)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        public Task<string> ReadConfigurationAsync(CancellationToken token) => File.ReadAllTextAsync(ConfigPath, Encoding.UTF8, token);

        public async Task<ClashRuntimeSnapshot> GetSnapshotAsync(CancellationToken token)
        {
            var general = SendAsync(HttpMethod.Get, "configs", null, token);
            var proxies = SendAsync(HttpMethod.Get, "proxies", null, token);
            var rules = SendAsync(HttpMethod.Get, "rules", null, token);
            await Task.WhenAll(general, proxies, rules).ConfigureAwait(false);
            return new ClashRuntimeSnapshot
            {
                General = (JObject)general.Result,
                Proxies = (JObject)proxies.Result["proxies"] ?? throw new IOException("Clash 未返回代理列表。"),
                Rules = (JArray)rules.Result["rules"] ?? throw new IOException("Clash 未返回规则列表。")
            };
        }

        public async Task ApplyPayloadAsync(string yaml, CancellationToken token)
            => await SendAsync(HttpMethod.Put, "configs?force=false", new JObject { ["payload"] = yaml }, token).ConfigureAwait(false);
        public async Task SelectAsync(string group, string selection, CancellationToken token)
            => await SendAsync(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(group), new JObject { ["name"] = selection }, token).ConfigureAwait(false);
        public async Task SetRulesDisabledAsync(IDictionary<int, bool> values, CancellationToken token)
        {
            var body = new JObject(); foreach (var value in values) body[value.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = value.Value;
            await SendAsync(HttpMethod.Patch, "rules/disable", body, token).ConfigureAwait(false);
        }
        public async Task<JArray> GetConnectionsAsync(CancellationToken token)
            => (JArray)(await SendAsync(HttpMethod.Get, "connections", null, token).ConfigureAwait(false))["connections"] ?? new JArray();
        public async Task CloseConnectionAsync(string id, CancellationToken token)
        {
            if (!Guid.TryParse(id, out _)) throw new IOException("Clash 连接标识无效。");
            await SendAsync(HttpMethod.Delete, "connections/" + Uri.EscapeDataString(id), null, token).ConfigureAwait(false);
        }

        async Task<JToken> SendAsync(HttpMethod method, string relativePath, JObject body, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            using var request = new HttpRequestMessage(method, relativePath);
            if (body != null) request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new IOException("Clash 控制器返回 HTTP " + (int)response.StatusCode + "。");
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return new JObject();
                string text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                if (text.Length > 24 * 1024 * 1024) throw new IOException("Clash 控制器响应过大。");
                try { return string.IsNullOrWhiteSpace(text) ? new JObject() : JToken.Parse(text); }
                catch (JsonException) { throw new IOException("Clash 控制器返回无效 JSON。"); }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Clash 控制器请求超时。"); }
            catch (HttpRequestException) { throw new IOException("无法连接 Clash 本机控制器，请检查 Clash 是否运行。"); }
        }

        public void Dispose() => client.Dispose();
    }

    internal static class ClashYaml
    {
        public static YamlMappingNode Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 24 * 1024 * 1024) throw new IOException("Clash 配置为空或过大。");
            try
            {
                var stream = new YamlStream(); stream.Load(new StringReader(text));
                if (stream.Documents.Count != 1 || !(stream.Documents[0].RootNode is YamlMappingNode root)) throw new IOException("Clash 配置根节点无效。");
                return root;
            }
            catch (YamlDotNet.Core.YamlException) { throw new IOException("Clash 当前配置无法解析。"); }
        }
        public static string Scalar(YamlMappingNode root, string key)
            => root.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value ?? "" : "";
        public static void Set(YamlMappingNode root, string key, YamlNode value) => root.Children[new YamlScalarNode(key)] = value;
        public static YamlSequenceNode Sequence(YamlMappingNode root, string key)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode(key), out var value)) { value = new YamlSequenceNode(); Set(root, key, value); }
            return value as YamlSequenceNode ?? throw new IOException("Clash 配置列表格式无效：" + key);
        }
        public static void MergeJson(YamlMappingNode root, JObject values)
        {
            foreach (var property in values.Properties())
            {
                if (property.Value.Type == JTokenType.Null) continue;
                var key = new YamlScalarNode(property.Name);
                if (property.Value is JObject child && root.Children.TryGetValue(key, out var old) && old is YamlMappingNode mapping) MergeJson(mapping, child);
                else root.Children[key] = FromJson(property.Value);
            }
        }
        static YamlNode FromJson(JToken value)
        {
            if (value is JObject mapping) { var result = new YamlMappingNode(); MergeJson(result, mapping); return result; }
            if (value is JArray array) return new YamlSequenceNode(array.Select(FromJson));
            if (value.Type == JTokenType.String) return new YamlScalarNode(value.Value<string>()) { Style = YamlDotNet.Core.ScalarStyle.DoubleQuoted };
            return new YamlScalarNode(value.ToString(Formatting.None)) { Style = YamlDotNet.Core.ScalarStyle.Plain };
        }
        public static string Write(YamlMappingNode root)
        {
            var stream = new YamlStream(new YamlDocument(root)); using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            stream.Save(writer, false); return writer.ToString();
        }
    }
}
