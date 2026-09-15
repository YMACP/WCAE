using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace WCAE
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProxyMode { Auto, Manual, Direct }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProxyProtocol { Http, Socks5 }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum CaptureIngressMode { Auto, Process, SystemProxy }

    public sealed class ProxyEndpoint
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; }
        public ProxyProtocol Protocol { get; set; } = ProxyProtocol.Http;
        // Credentials are serialized only by ProxyConfigurationStore's protected envelope.
        [JsonIgnore] public string Username { get; set; } = "";
        [JsonIgnore] public string Password { get; set; } = "";
        [JsonIgnore] public string DisplayName => (Protocol == ProxyProtocol.Socks5 ? "SOCKS5" : "HTTP") + " " + Authority;
        [JsonIgnore] public string Authority => (Host?.Contains(':') == true ? "[" + Host.Trim('[', ']') + "]" : Host) + ":" + Port;
        public ProxyEndpoint Clone() => (ProxyEndpoint)MemberwiseClone();

        public void Validate(int capturePort = 8879)
        {
            if (!Enum.IsDefined(Protocol)) throw new ArgumentException("代理协议无效。");
            string host = (Host ?? "").Trim();
            if (host.StartsWith("[", StringComparison.Ordinal) && host.EndsWith("]", StringComparison.Ordinal)) host = host[1..^1];
            if (host.Length == 0 || host.Length > 253 || host.Any(char.IsWhiteSpace) || host.IndexOfAny(new[] { '/', '\\', '@', '?', '#', '\r', '\n' }) >= 0
                || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new ArgumentException("请填写有效的代理主机地址，不要包含协议或路径。");
            if (Port < 1 || Port > 65535) throw new ArgumentException("代理端口应为 1 至 65535。");
            if (Port == capturePort && IsLocalHost(host)) throw new ArgumentException("代理出口不能指向 WCAE 自身的监听端口。");
            if ((Username ?? "").Contains(':') && Protocol == ProxyProtocol.Http) throw new ArgumentException("HTTP 代理用户名不能包含冒号。");
            if (string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password)) throw new ArgumentException("填写代理密码时还需要填写用户名。");
            if ((Username ?? "").Any(char.IsControl) || (Password ?? "").Any(char.IsControl)) throw new ArgumentException("代理认证信息不能包含控制字符。");
            if (Protocol == ProxyProtocol.Socks5 && (Encoding.UTF8.GetByteCount(Username ?? "") > 255 || Encoding.UTF8.GetByteCount(Password ?? "") > 255))
                throw new ArgumentException("SOCKS5 用户名及密码分别不能超过 255 字节。");
            Host = host;
        }

        internal static bool IsLocalHost(string host)
        {
            host = host.TrimEnd('.');
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!IPAddress.TryParse(host, out var address)) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
        }

        public WebProxy ToWebProxy()
        {
            var proxy = new WebProxy(new Uri((Protocol == ProxyProtocol.Socks5 ? "socks5" : "http") + "://" + Authority));
            if (!string.IsNullOrEmpty(Username)) proxy.Credentials = new NetworkCredential(Username, Password ?? "");
            return proxy;
        }
    }

    public sealed class ProxyConfiguration
    {
        public ProxyMode Mode { get; set; } = ProxyMode.Auto;
        public CaptureIngressMode IngressMode { get; set; } = CaptureIngressMode.Auto;
        public ProxyEndpoint ManualEndpoint { get; set; }
        public ProxyConfiguration Clone() => new ProxyConfiguration { Mode = Mode, IngressMode = IngressMode, ManualEndpoint = ManualEndpoint?.Clone() };
        public void Validate(int capturePort = 8879)
        {
            if (!Enum.IsDefined(Mode)) throw new ArgumentException("连接模式无效。");
            if (!Enum.IsDefined(IngressMode)) throw new ArgumentException("接入方式无效。");
            if (Mode == ProxyMode.Manual && ManualEndpoint == null) throw new ArgumentException("请先填写代理地址和端口。");
            ManualEndpoint?.Validate(capturePort);
        }
    }

    /// <summary>Only explicitly saved preferences persist; automatic discovery is rerun on each startup.</summary>
    public sealed class ProxyConfigurationStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WCAE/network-settings/v1");
        public string FilePath { get; }
        public ProxyConfigurationStore(string dataDirectory = null)
            => FilePath = Path.Combine(Path.GetFullPath(dataDirectory ?? AppPaths.DataDirectory), "network-settings.json");

        public ProxyConfiguration Load()
        {
            if (!File.Exists(FilePath)) return new ProxyConfiguration();
            try
            {
                if (new FileInfo(FilePath).Length > 1024 * 1024) throw new InvalidDataException();
                var stored = JsonConvert.DeserializeObject<StoredSettings>(File.ReadAllText(FilePath, Encoding.UTF8));
                if (stored == null || stored.Version != 1) throw new InvalidDataException();
                var config = new ProxyConfiguration { Mode = stored.Mode, IngressMode = stored.IngressMode, ManualEndpoint = stored.Endpoint };
                if (!string.IsNullOrEmpty(stored.ProtectedCredentials))
                {
                    if (config.ManualEndpoint == null) throw new InvalidDataException();
                    byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedCredentials), Entropy, DataProtectionScope.CurrentUser);
                    try
                    {
                        var credentials = JsonConvert.DeserializeObject<Credentials>(Encoding.UTF8.GetString(clear));
                        if (credentials == null) throw new InvalidDataException();
                        config.ManualEndpoint.Username = credentials.Username ?? "";
                        config.ManualEndpoint.Password = credentials.Password ?? "";
                    }
                    finally { CryptographicOperations.ZeroMemory(clear); }
                }
                config.Validate();
                return config;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is CryptographicException || ex is FormatException || ex is ArgumentException)
            {
                // Do not include malformed configuration or secrets in the surfaced exception.
                throw new InvalidDataException("连接设置读取失败，请在设置中重新填写并保存。", ex);
            }
        }

        public void Save(ProxyConfiguration configuration, int capturePort = 8879)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            var config = configuration.Clone();
            config.Validate(capturePort);
            var stored = new StoredSettings { Mode = config.Mode, IngressMode = config.IngressMode, Endpoint = config.ManualEndpoint };
            if (config.ManualEndpoint != null && (!string.IsNullOrEmpty(config.ManualEndpoint.Username) || !string.IsNullOrEmpty(config.ManualEndpoint.Password)))
            {
                byte[] clear = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Credentials { Username = config.ManualEndpoint.Username, Password = config.ManualEndpoint.Password }));
                try { stored.ProtectedCredentials = Convert.ToBase64String(ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] content = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(stored, Formatting.Indented));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(content); stream.Flush(true); }
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                else File.Move(temporary, FilePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private sealed class StoredSettings
        {
            public int Version { get; set; } = 1;
            public ProxyMode Mode { get; set; } = ProxyMode.Auto;
            public CaptureIngressMode IngressMode { get; set; } = CaptureIngressMode.Auto;
            public ProxyEndpoint Endpoint { get; set; }
            public string ProtectedCredentials { get; set; } = "";
        }
        private sealed class Credentials
        {
            public string Username { get; set; }
            public string Password { get; set; }
        }
    }
}
