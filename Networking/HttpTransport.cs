using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    public sealed class HttpRequestFailureException : IOException
    {
        public HttpStatusCode StatusCode { get; }
        public HttpRequestFailureException(HttpStatusCode statusCode, string message, Exception innerException = null)
            : base(message, innerException) { StatusCode = statusCode; }
    }

    /// <summary>Independent requests: never uses the capture proxy or changes TLS validation globally.</summary>
    public sealed class HttpTransport : IHttpTransport, IDisposable
    {
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly ConcurrentDictionary<HttpWebRequest, byte> active = new ConcurrentDictionary<HttpWebRequest, byte>();
        private int disposed;
        private ProxyEndpoint upstream;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(35);

        public void UseProxy(ProxyEndpoint endpoint)
        {
            var selected = endpoint?.Clone();
            selected?.Validate();
            Volatile.Write(ref upstream, selected);
        }
        internal ProxyEndpoint SelectedProxy => Volatile.Read(ref upstream)?.Clone();

        public Task<string> GetStringAsync(string url, AccountSession session, CancellationToken token)
            => SendAsync(url, "GET", null, session, ReadTextAsync, token);

        public Task<string> PostFormAsync(string url, IDictionary<string, string> values, AccountSession session, CancellationToken token)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var form = string.Join("&", values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value ?? "")));
            return SendAsync(url, "POST", Encoding.UTF8.GetBytes(form), session, ReadTextAsync, token);
        }

        public async Task DownloadAsync(string url, string destination, AccountSession session, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("下载路径不能为空。", nameof(destination));
            string fullPath = Path.GetFullPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            string temporary = fullPath + ".part-" + Guid.NewGuid().ToString("N");
            try
            {
                await SendAsync(url, "GET", null, session, async (response, ct) =>
                {
                    using (var input = response.GetResponseStream())
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        if (input == null) throw new IOException("服务器未返回下载内容。");
                        await input.CopyToAsync(output, 65536, ct).ConfigureAwait(false);
                        await output.FlushAsync(ct).ConfigureAwait(false);
                    }
                    return true;
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
                else File.Move(temporary, fullPath);
            }
            finally
            {
                if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { }
            }
        }

        private async Task<T> SendAsync<T>(string url, string method, byte[] body, AccountSession session,
            Func<HttpWebResponse, CancellationToken, Task<T>> consume, CancellationToken token)
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(HttpTransport));
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException("请求地址必须是有效的 HTTP 或 HTTPS 地址。", nameof(url));
            if (!string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("请求地址不能包含用户名或密码。", nameof(url));
            if (Timeout <= TimeSpan.Zero) throw new InvalidOperationException("请求超时必须大于零。");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
            {
                timeout.CancelAfter(Timeout);
                try
                {
                    for (int redirects = 0; redirects <= 10; redirects++)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        var request = CreateRequest(uri, method, body, session);
                        active.TryAdd(request, 0);
                        using (timeout.Token.Register(() => request.Abort()))
                        {
                            try
                            {
                                if (body != null)
                                {
                                    using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                                        await stream.WriteAsync(body, 0, body.Length, timeout.Token).ConfigureAwait(false);
                                }
                                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                                {
                                    MergeCookies(session, response.ResponseUri, response.Headers.GetValues("Set-Cookie"));
                                    int status = (int)response.StatusCode;
                                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                                    {
                                        if (redirects == 10) throw new IOException("服务器重定向次数过多。");
                                        string location = response.Headers[HttpResponseHeader.Location];
                                        Uri next;
                                        if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(uri, location, out next)
                                            || (next.Scheme != "http" && next.Scheme != "https") || !string.IsNullOrEmpty(next.UserInfo))
                                            throw new IOException("服务器返回了无效的重定向地址。");
                                        // Never forward a POST form, which can contain article tokens, to another origin.
                                        if (body != null && (next.Scheme != uri.Scheme || next.Host != uri.Host || next.Port != uri.Port))
                                            throw new IOException("提交请求发生跨站重定向，已停止发送表单内容。");
                                        if (status == 303 || ((status == 301 || status == 302) && method == "POST")) { method = "GET"; body = null; }
                                        uri = next;
                                        continue;
                                    }
                                    if (status < 200 || status >= 300) throw new HttpRequestFailureException(response.StatusCode, "服务器返回 HTTP " + status + "。");
                                    var result = await consume(response, timeout.Token).ConfigureAwait(false);
                                    timeout.Token.ThrowIfCancellationRequested();
                                    return result;
                                }
                            }
                            finally { byte unused; active.TryRemove(request, out unused); }
                        }
                    }
                    throw new IOException("服务器重定向次数过多。");
                }
                catch (Exception ex) when (timeout.IsCancellationRequested && !(ex is OutOfMemoryException))
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException("请求已取消。", ex, token);
                    if (lifetime.IsCancellationRequested) throw new OperationCanceledException("网络服务已关闭。", ex, lifetime.Token);
                    throw new TimeoutException("网络请求超时，请检查网络或稍后重试。", ex);
                }
                catch (WebException ex)
                {
                    using (var response = ex.Response as HttpWebResponse)
                    {
                        if (response != null)
                        {
                            MergeCookies(session, response.ResponseUri, response.Headers.GetValues("Set-Cookie"));
                            throw new HttpRequestFailureException(response.StatusCode, "服务器返回 HTTP " + (int)response.StatusCode + "，请检查文章访问权限或刷新微信会话。", ex);
                        }
                    }
                    throw new IOException("网络请求失败（" + ex.Status + "）。", ex);
                }
            }
        }

        private HttpWebRequest CreateRequest(Uri uri, string method, byte[] body, AccountSession session)
        {
            // Preserve the tested Abort cancellation and explicit redirect/cookie policy.
#pragma warning disable SYSLIB0014
            var request = (HttpWebRequest)WebRequest.Create(uri);
#pragma warning restore SYSLIB0014
            request.Method = method;
            request.Proxy = Volatile.Read(ref upstream)?.ToWebProxy();
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = request.ReadWriteTimeout = (int)Math.Min(int.MaxValue, Math.Max(1, Timeout.TotalMilliseconds));
            request.UserAgent = Clean(session?.UserAgent, "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            request.Accept = "*/*";
            bool wechat = uri.Scheme == "https" && string.Equals(uri.Host, "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase);
            if (wechat && session != null)
            {
                lock (session)
                    if (!string.IsNullOrWhiteSpace(session.Cookie)) request.Headers[HttpRequestHeader.Cookie] = Clean(session.Cookie, "");
                Uri referer;
                string requestedReferer = null;
                session.Headers?.TryGetValue("Referer", out requestedReferer);
                if (!Uri.TryCreate(requestedReferer, UriKind.Absolute, out referer) || referer.Scheme != "https" || !string.Equals(referer.Host, "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase))
                    Uri.TryCreate(session.RequestUrl, UriKind.Absolute, out referer);
                if (referer != null && referer.Scheme == "https" && string.Equals(referer.Host, "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase))
                    request.Referer = referer.AbsoluteUri;
                else request.Referer = "https://mp.weixin.qq.com/";
                string value;
                if (session.Headers != null)
                {
                    if (session.Headers.TryGetValue("Accept", out value)) request.Accept = Clean(value, "*/*");
                    if (session.Headers.TryGetValue("Accept-Language", out value)) request.Headers[HttpRequestHeader.AcceptLanguage] = Clean(value, "zh-CN,zh;q=0.9");
                    if (session.Headers.TryGetValue("X-Requested-With", out value)) request.Headers["X-Requested-With"] = Clean(value, "XMLHttpRequest");
                }
                if (uri.AbsolutePath.StartsWith("/mp/", StringComparison.Ordinal))
                {
                    request.Headers["X-Requested-With"] = "XMLHttpRequest";
                    request.Headers["Sec-Fetch-Site"] = "same-origin";
                    request.Headers["Sec-Fetch-Mode"] = "cors";
                    request.Headers["Sec-Fetch-Dest"] = "empty";
                }
                if (method == "POST") request.Headers["Origin"] = "https://mp.weixin.qq.com";
            }
            else request.Referer = "https://mp.weixin.qq.com/";
            if (body != null)
            {
                request.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
                request.ContentLength = body.Length;
            }
            return request;
        }

        private static string Clean(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value.Replace("\r", "").Replace("\n", "");

        internal static void MergeCookies(AccountSession session, Uri origin, IEnumerable<string> responseCookies)
        {
            if (session == null || origin == null || origin.Scheme != "https"
                || !origin.Host.Equals("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase) || responseCookies == null) return;
            lock (session)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string part in (session.Cookie ?? "").Split(';'))
                {
                    int equals = part.IndexOf('=');
                    if (equals > 0) values[part.Substring(0, equals).Trim()] = part.Substring(equals + 1).Trim();
                }
                foreach (string combined in responseCookies)
                {
                    // The comma inside Expires is not a cookie separator.
                    foreach (string header in Regex.Split(combined ?? "", @",(?=\s*[^=;,\s]+=)"))
                    {
                        int equals = header.IndexOf('=');
                        if (equals <= 0) continue;
                        string name = header.Substring(0, equals).Trim();
                        try
                        {
                            var parsed = new CookieContainer();
                            parsed.SetCookies(origin, header); // Rejects cookies for a foreign domain.
                            bool expired = false;
                            foreach (string attribute in header.Split(';').Skip(1))
                            {
                                int separator = attribute.IndexOf('=');
                                if (separator <= 0) continue;
                                string key = attribute.Substring(0, separator).Trim();
                                string value = attribute.Substring(separator + 1).Trim();
                                long seconds;
                                DateTimeOffset date;
                                if (key.Equals("max-age", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out seconds) && seconds <= 0) expired = true;
                                if (key.Equals("expires", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AssumeUniversal, out date) && date <= DateTimeOffset.UtcNow) expired = true;
                            }
                            if (expired) values.Remove(name);
                            else foreach (Cookie cookie in parsed.GetCookies(origin)) values[cookie.Name] = cookie.Value;
                        }
                        catch (CookieException) { /* One malformed Set-Cookie must not discard an otherwise usable response. */ }
                    }
                }
                session.Cookie = string.Join("; ", values.Select(x => x.Key + "=" + x.Value));
            }
        }

        private static async Task<string> ReadTextAsync(HttpWebResponse response, CancellationToken token)
        {
            using (var input = response.GetResponseStream())
            using (var output = new MemoryStream())
            {
                if (input == null) throw new IOException("服务器未返回文本内容。");
                var buffer = new byte[32768];
                int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + count > 64L * 1024 * 1024) throw new IOException("文章响应超过 64 MB，已停止读取。");
                    output.Write(buffer, 0, count);
                }
                Encoding encoding = Encoding.UTF8;
                if (response.ContentType?.IndexOf("charset=", StringComparison.OrdinalIgnoreCase) >= 0 && !string.IsNullOrWhiteSpace(response.CharacterSet))
                    try { encoding = Encoding.GetEncoding(response.CharacterSet.Trim('"', '\'')); } catch (ArgumentException) { }
                output.Position = 0;
                using (var reader = new StreamReader(output, encoding, true)) return reader.ReadToEnd();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            foreach (var request in active.Keys) request.Abort();
            // The source remains readable by requests unwinding their cancellation handlers.
        }
    }
}
