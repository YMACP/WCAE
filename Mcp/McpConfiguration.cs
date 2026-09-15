using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WCAE
{
    internal sealed class McpConfiguration
    {
        internal const int DefaultPort = 8878;
        public int Port { get; set; } = DefaultPort;
        public string Token { get; set; } = CreateToken();
        public string Endpoint => "http://127.0.0.1:" + Port + "/mcp";

        internal static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        internal McpConfiguration Copy() => new McpConfiguration { Port = Port, Token = Token };

        internal string CopyConfigurationJson()
        {
            return JsonSerializer.Serialize(new
            {
                mcpServers = new
                {
                    WCAE = new
                    {
                        type = "http",
                        url = Endpoint,
                        headers = new { Authorization = "Bearer " + Token }
                    }
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        internal void Validate(bool allowEphemeralPort = false)
        {
            if (Port < (allowEphemeralPort ? 0 : 1) || Port > 65535)
                throw new ArgumentOutOfRangeException(nameof(Port), "MCP 端口须在 1 至 65535 之间。");
            if (Token == null || Token.Length != 64)
                throw new ArgumentException("MCP 访问令牌无效。");
            foreach (char c in Token)
                if (!Uri.IsHexDigit(c)) throw new ArgumentException("MCP 访问令牌无效。");
        }
    }

    internal sealed class McpConfigurationStore
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WCAE.MCP.configuration.v1");
        readonly string path;
        readonly object gate = new object();

        internal McpConfigurationStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("配置目录不能为空。", nameof(directory));
            path = Path.Combine(Path.GetFullPath(directory), "mcp-settings.json");
        }

        internal McpConfiguration LoadOrCreate()
        {
            lock (gate)
            {
                if (!File.Exists(path))
                {
                    var created = new McpConfiguration();
                    Save(created);
                    return created;
                }
                try
                {
                    var stored = JsonSerializer.Deserialize<StoredConfiguration>(File.ReadAllText(path, Encoding.UTF8));
                    if (stored == null || stored.Version != 1 || string.IsNullOrEmpty(stored.ProtectedToken))
                        throw new InvalidDataException();
                    byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedToken), Entropy, DataProtectionScope.CurrentUser);
                    try
                    {
                        var config = new McpConfiguration { Port = stored.Port, Token = Encoding.UTF8.GetString(clear) };
                        config.Validate();
                        return config;
                    }
                    finally { CryptographicOperations.ZeroMemory(clear); }
                }
                catch (Exception error) when (error is JsonException || error is CryptographicException || error is FormatException || error is InvalidDataException || error is ArgumentException)
                {
                    // Never replace unreadable settings silently: existing Agent configurations must remain stable.
                    throw new InvalidDataException("MCP 配置读取失败，请在 MCP 接入设置中重置访问令牌并保存。", error);
                }
            }
        }

        internal void Save(McpConfiguration config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                byte[] clear = Encoding.UTF8.GetBytes(config.Token);
                byte[] encrypted;
                try { encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser); }
                finally { CryptographicOperations.ZeroMemory(clear); }
                var stored = new StoredConfiguration { Version = 1, Port = config.Port, ProtectedToken = Convert.ToBase64String(encrypted) };
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        sealed class StoredConfiguration
        {
            public int Version { get; set; }
            public int Port { get; set; }
            public string ProtectedToken { get; set; }
        }
    }
}
