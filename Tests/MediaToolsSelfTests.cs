using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace WCAE
{
    // Real-tool smoke tests use only generated media and a server bound to 127.0.0.1.
    public static class MediaToolsSelfTests
    {
        public static async Task RunAsync()
        {
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.Combine(tempRoot, "WCAE-tools-fixture-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(root);
            using var registryScope = ExportFileSystem.UseDirectoryRegistry(new ExportDirectoryRegistry(Path.Combine(root, "registry")));
            bool complete = false;
            try
            {
                await BundledToolsSelfTests.RunAsync();
                await BundledTools.EnsureAllAsync(CancellationToken.None);
                string ffmpeg = Path.Combine(AppPaths.ToolsDirectory, "ffmpeg.exe");
                foreach (string tool in new[] { "ffmpeg.exe", "yt-dlp.exe", "wkhtmltopdf.exe" })
                    Assert(File.Exists(Path.Combine(AppPaths.ToolsDirectory, tool)), "缺少真实测试工具：" + tool);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                using (var transport = new HttpTransport { Timeout = TimeSpan.FromSeconds(15) })
                {
                    var token = timeout.Token;
                    var exporter = new ExportService(transport);
                    string pdfDestination = Path.Combine(root, "pdf");
                    await exporter.ExportAsync(new[] { new ArticleRecord
                    {
                        Id = "pdf-tool-fixture", Title = "离线中文 PDF 测试", AccountName = "测试公众号", Author = "测试作者",
                        PublishedAt = new DateTime(2026, 9, 13), Status = ArticleStatus.Available,
                        Html = "<html><body><div id='js_content'><h2>中文正文</h2><p>这是无需联网的 PDF 导出测试。</p><p>第二段内容：123 ABC。</p></div></body></html>"
                    } }, new AccountSession(), new ExportOptions { Destination = pdfDestination, Format = ExportFormat.Pdf }, null, token);
                    ExportManifest pdfReport = ReadReport(exporter, pdfDestination);
                    Assert(pdfReport.Succeeded == 1 && pdfReport.Failed == 0, "真实 PDF 导出失败：" + Reasons(pdfReport));
                    byte[] pdf = File.ReadAllBytes(pdfReport.Results.Single().OutputPath);
                    Assert(pdf.Length > 1000 && Encoding.ASCII.GetString(pdf, 0, 5) == "%PDF-" &&
                        Encoding.ASCII.GetString(pdf, Math.Max(0, pdf.Length - 128), Math.Min(128, pdf.Length)).Contains("%%EOF"), "真实 PDF 没有完整文件结构");

                    string fixtures = Path.Combine(root, "fixtures");
                    Directory.CreateDirectory(fixtures);
                    string video = Path.Combine(fixtures, "clip.mp4"), audio = Path.Combine(fixtures, "tone.mp3"), playlist = Path.Combine(fixtures, "stream.m3u8");
                    await ExternalToolRunner.RunAsync(ffmpeg, new[]
                    {
                        "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=blue:s=160x90:r=12",
                        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "1", "-shortest",
                        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", video
                    }, fixtures, TimeSpan.FromSeconds(40), token);
                    await ExternalToolRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", video, "-vn", "-c:a", "libmp3lame", audio }, fixtures, TimeSpan.FromSeconds(30), token);
                    await ExternalToolRunner.RunAsync(ffmpeg, new[]
                    {
                        "-hide_banner", "-loglevel", "error", "-nostdin", "-i", video, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy",
                        "-f", "hls", "-hls_time", "0.4", "-hls_list_size", "0", "-hls_playlist_type", "vod",
                        "-hls_segment_filename", Path.Combine(fixtures, "segment%03d.ts"), playlist
                    }, fixtures, TimeSpan.FromSeconds(30), token);

                    using (var server = new LoopbackMediaServer(fixtures))
                    {
                        var article = new ArticleRecord
                        {
                            Id = "real-media-fixture", Title = "本地媒体测试", Status = ArticleStatus.Available, Url = server.BaseUrl + "article",
                            Media = new List<MediaAsset>
                            {
                                new MediaAsset { Id = "direct-video", Kind = MediaKind.Video, Url = server.BaseUrl + "clip.mp4" },
                                new MediaAsset { Id = "direct-audio", Kind = MediaKind.Audio, Url = server.BaseUrl + "tone.mp3" },
                                new MediaAsset { Id = "hls-video", Kind = MediaKind.Video, Url = server.BaseUrl + "stream.m3u8", RequiresExtractor = true }
                            }
                        };
                        string destination = Path.Combine(root, "media");
                        await exporter.ExportAsync(new[] { article }, new AccountSession { Cookie = "fixture-secret-cookie", Key = "fixture-secret-key" },
                            new ExportOptions { Destination = destination, Format = ExportFormat.AllMedia }, null, token);
                        ExportManifest report = ReadReport(exporter, destination);
                        Assert(report.Succeeded == 3 && report.Failed == 0, "真实媒体导出失败：" + Reasons(report));
                        var directVideo = report.Results.Single(x => x.Source.EndsWith("/clip.mp4", StringComparison.Ordinal));
                        var directAudio = report.Results.Single(x => x.Source.EndsWith("/tone.mp3", StringComparison.Ordinal));
                        Assert(File.ReadAllBytes(directVideo.OutputPath).SequenceEqual(File.ReadAllBytes(video)), "直链视频数据不一致");
                        Assert(File.ReadAllBytes(directAudio.OutputPath).SequenceEqual(File.ReadAllBytes(audio)), "直链音频数据不一致");
                        foreach (var result in report.Results)
                            await ExternalToolRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-xerror", "-i", result.OutputPath, "-f", "null", "-" }, fixtures, TimeSpan.FromSeconds(30), token);
                        Assert(server.Requests.Any(x => x == "/stream.m3u8") && server.Requests.Any(x => x.EndsWith(".ts", StringComparison.Ordinal)), "HLS 未实际请求播放列表和视频分片");
                        Assert(!server.SawCredentials, "本地媒体服务收到不应传入的会话凭据");
                        Assert(!Directory.GetFiles(destination, "*.part*", SearchOption.AllDirectories).Any() &&
                            !Directory.GetDirectories(destination, ".work-*", SearchOption.AllDirectories).Any(), "真实工具运行完成后残留临时文件");
                        string longDestination = Path.Combine(root, new string('x', Math.Max(1, 160 - root.Length - 1)));
                        var longArticle = new ArticleRecord
                        {
                            Id = "long-path-fixture", Title = new string('长', 90), Status = ArticleStatus.Available,
                            Media = new List<MediaAsset> { new MediaAsset { Kind = MediaKind.Audio, Url = server.BaseUrl + "tone.mp3" } }
                        };
                        await exporter.ExportAsync(new[] { longArticle }, new AccountSession(), new ExportOptions { Destination = longDestination, Format = ExportFormat.Audio }, null, token);
                        var longReport = ReadReport(exporter, longDestination);
                        Assert(longReport.Succeeded == 1 && longReport.Failed == 0, "长路径下载失败：" + Reasons(longReport));
                    }
                    await TestPreparationAsync(exporter, root, token);
                }
                AssertNoReportFiles(root);
                complete = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("真实媒体工具离线测试失败，测试目录保留在 " + root + "。" + ex.Message, ex);
            }
            finally
            {
                string target = Path.GetFullPath(root);
                if (complete && target.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetDirectoryName(target), tempRoot, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(target).StartsWith("WCAE-tools-fixture-", StringComparison.Ordinal)) Directory.Delete(target, true);
            }
        }

        private static async Task TestPreparationAsync(ExportService exporter, string root, CancellationToken token)
        {
            var originals = new[]
            {
                new ArticleRecord { Id = "lazy-1", Title = "逐篇加载一", Html = "保留调用者正文" },
                new ArticleRecord { Id = "lazy-failed", Title = "准备失败" },
                new ArticleRecord { Id = "lazy-2", Title = "逐篇加载二" }
            };
            var prepared = new List<ArticleRecord>();
            exporter.ClearPreparedHtml = true;
            exporter.PrepareArticleAsync = (article, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Assert(prepared.All(x => x.Html.Length == 0), "上一篇准备正文没有及时释放");
                if (article.Id == "lazy-failed") throw new IOException("准备测试失败：https://example.invalid/?key=fixture-secret&mid=1");
                var clone = new ArticleRecord { Id = article.Id, Title = article.Title, Status = ArticleStatus.Available, Html = "<div id='js_content'><p>逐篇加载后的正文。</p></div>" };
                prepared.Add(clone);
                return Task.FromResult(clone);
            };
            string destination = Path.Combine(root, "lazy");
            await exporter.ExportAsync(originals, new AccountSession(), new ExportOptions { Destination = destination, Format = ExportFormat.Text }, null, token);
            var report = ReadReport(exporter, destination);
            Assert(report.Succeeded == 2 && report.Failed == 1 && report.ArticlesProcessed == 3, "逐篇准备失败未独立计数或阻断后续文章");
            Assert(prepared.All(x => x.Html.Length == 0) && originals[0].Html == "保留调用者正文", "逐篇正文释放改变了调用者原记录");
            Assert(!JsonConvert.SerializeObject(report).Contains("fixture-secret"), "内存导出结果的失败原因回显了会话参数");
            Assert(ExportFileSystem.ArticleDirectory(root, new ArticleRecord { Id = "same-id", Title = "同篇", Url = "https://mp.weixin.qq.com/s?mid=1&key=old" }, ExportFormat.Text) ==
                ExportFileSystem.ArticleDirectory(root, new ArticleRecord { Id = "same-id", Title = "同篇", Url = "https://mp.weixin.qq.com/s?mid=1&key=new" }, ExportFormat.Text), "会话参数改变导致同一文章重复建目录");
            exporter.PrepareArticleAsync = null;
            exporter.ClearPreparedHtml = false;
        }

        private static ExportManifest ReadReport(ExportService exporter, string destination)
        {
            AssertNoReportFiles(destination);
            Assert(exporter.LastManifest != null && exporter.LastManifest.FinishedUtc.HasValue, "导出结束后缺少内存结果");
            return exporter.LastManifest;
        }
        private static void AssertNoReportFiles(string destination)
            => Assert(!Directory.GetFiles(destination, "export-results-*", SearchOption.AllDirectories).Any(), "用户导出目录仍生成了结果清单文件");
        private static string Reasons(ExportManifest report) => string.Join("; ", report.Results.Where(x => x.State == "Failed").Select(x => x.Reason));
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private sealed class LoopbackMediaServer : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Dictionary<string, string> files;
            private readonly ConcurrentDictionary<TcpClient, byte> clients = new ConcurrentDictionary<TcpClient, byte>();
            private readonly CancellationTokenSource stopped = new CancellationTokenSource();
            private readonly Task acceptLoop;
            private int credentials;
            internal ConcurrentBag<string> Requests { get; } = new ConcurrentBag<string>();
            internal bool SawCredentials => Volatile.Read(ref credentials) != 0;
            internal string BaseUrl { get; }
            internal LoopbackMediaServer(string directory)
            {
                files = Directory.GetFiles(directory).ToDictionary(x => "/" + Path.GetFileName(x), x => x, StringComparer.Ordinal);
                listener.Start();
                BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
                acceptLoop = AcceptAsync();
            }

            private async Task AcceptAsync()
            {
                while (!stopped.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                    catch (ObjectDisposedException) when (stopped.IsCancellationRequested) { return; }
                    catch (SocketException) when (stopped.IsCancellationRequested) { return; }
                    clients.TryAdd(client, 0);
                    _ = ServeAsync(client);
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true))
                    {
                        string requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (requestLine == null) return;
                        string[] fields = requestLine.Split(' ');
                        if (fields.Length < 2) return;
                        string path = fields[1].Split('?')[0];
                        string range = null;
                        int headerBytes = requestLine.Length;
                        while (true)
                        {
                            string line = await reader.ReadLineAsync().ConfigureAwait(false);
                            if (string.IsNullOrEmpty(line)) break;
                            headerBytes += line.Length;
                            if (headerBytes > 32768) return;
                            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line.Substring(6).Trim();
                            if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) Interlocked.Exchange(ref credentials, 1);
                        }
                        Requests.Add(path);
                        string file;
                        if ((fields[0] != "GET" && fields[0] != "HEAD") || !files.TryGetValue(path, out file))
                        {
                            byte[] missing = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(missing, 0, missing.Length, stopped.Token).ConfigureAwait(false);
                            return;
                        }
                        byte[] content = File.ReadAllBytes(file);
                        long start = 0, end = content.LongLength - 1;
                        bool partial = false;
                        if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] bounds = range.Substring(6).Split('-');
                            long parsedStart, parsedEnd;
                            if (bounds.Length == 2 && long.TryParse(bounds[0], out parsedStart) && parsedStart >= 0 && parsedStart < content.LongLength)
                            {
                                start = parsedStart;
                                if (long.TryParse(bounds[1], out parsedEnd) && parsedEnd >= start) end = Math.Min(end, parsedEnd);
                                partial = true;
                            }
                        }
                        string extension = Path.GetExtension(file).ToLowerInvariant();
                        string type = extension == ".m3u8" ? "application/vnd.apple.mpegurl" : extension == ".ts" ? "video/mp2t" : extension == ".mp3" ? "audio/mpeg" : "video/mp4";
                        string header = "HTTP/1.1 " + (partial ? "206 Partial Content" : "200 OK") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + (end - start + 1) +
                            "\r\nAccept-Ranges: bytes\r\nConnection: close\r\n" + (partial ? "Content-Range: bytes " + start + "-" + end + "/" + content.LongLength + "\r\n" : "") + "\r\n";
                        byte[] encoded = Encoding.ASCII.GetBytes(header);
                        await stream.WriteAsync(encoded, 0, encoded.Length, stopped.Token).ConfigureAwait(false);
                        if (fields[0] != "HEAD") await stream.WriteAsync(content, (int)start, (int)(end - start + 1), stopped.Token).ConfigureAwait(false);
                        await stream.FlushAsync(stopped.Token).ConfigureAwait(false);
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
                finally { byte unused; clients.TryRemove(client, out unused); client.Dispose(); }
            }

            public void Dispose()
            {
                stopped.Cancel();
                listener.Stop();
                foreach (var client in clients.Keys) client.Dispose();
                acceptLoop.GetAwaiter().GetResult();
                stopped.Dispose();
            }
        }
    }
}
