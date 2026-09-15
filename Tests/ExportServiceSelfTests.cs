using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace WCAE
{
    // Invoked by the application's --self-test entry point; no live service is contacted.
    public static class ExportServiceSelfTests
    {
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN1sAAAAASUVORK5CYII=");

        public static async Task RunAsync()
        {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            string root = Path.Combine(temporaryRoot, "WCAE-export-fixture-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(root);
            using var registryScope = ExportFileSystem.UseDirectoryRegistry(new ExportDirectoryRegistry(Path.Combine(root, "registry")));
            bool complete = false;
            try
            {
                var fake = new FakeTransport();
                fake.Data["https://cdn.example.test/picture"] = Png;
                var session = new AccountSession { Cookie = "secret-cookie", Key = "secret-key", Uin = "secret-uin", AppMsgToken = "secret-token", UserAgent = "fixture-agent" };
                var article = new ArticleRecord
                {
                    Id = "fixture:1", Title = "../同名:文章?", AccountName = "测试公众号", Author = "作者", PublishedAt = new DateTime(2026, 1, 2), Status = ArticleStatus.Available,
                    Url = "https://mp.weixin.qq.com/s?__biz=fixture&mid=1&uin=secret-uin&key=secret-key",
                    Html = "<html><body><nav>不属于正文</nav><div id='js_content'><p>第一段<br>第二行 &amp; 内容</p><img data-src='//cdn.example.test/picture' src='javascript:alert(1)' onerror='alert(2)'><script>do_evil()</script></div></body></html>"
                };
                var service = new ExportService(fake);
                await TestNativeSnapshotAsync(service, fake, root);
                var htmlOptions = new ExportOptions { Destination = Path.Combine(root, "html"), Format = ExportFormat.Html, DownloadImages = true };
                await service.ExportAsync(new[] { article }, session, htmlOptions, null, CancellationToken.None);
                string output = Directory.GetFiles(htmlOptions.Destination, "article.html", SearchOption.AllDirectories).Single();
                string html = File.ReadAllText(output);
                Assert(html.Contains("第一段") && html.Contains("测试公众号") && !html.Contains("不属于正文"), "HTML 正文或元信息选择错误");
                Assert(!html.Contains("<script") && !html.Contains("onerror") && !html.Contains("javascript:"), "导出 HTML 留下了主动脚本");
                Assert(html.Contains("src=\"images/") && Directory.GetFiles(Path.GetDirectoryName(output), "*.png", SearchOption.AllDirectories).Length == 1, "离线图片没有正确下载或重写");
                Assert(fake.DownloadSessions.All(x => string.IsNullOrEmpty(x.Cookie) && string.IsNullOrEmpty(x.Key)), "向外部图片域传递了微信凭据");
                var report = Manifest(service, htmlOptions.Destination);
                Assert(report.Failed == 0 && report.Succeeded == 2, "HTML 成功计数错误");
                Assert(!JsonConvert.SerializeObject(report).Contains("secret-"), "内存导出结果保留了会话凭据");
                int downloads = fake.Downloads;
                await service.ExportAsync(new[] { article }, session, htmlOptions, null, CancellationToken.None);
                var repeated = Manifest(service, htmlOptions.Destination);
                Assert(fake.Downloads == downloads && repeated.Skipped == 1, "未覆盖模式没有跳过已存在文件");
                Assert(!ReferenceEquals(report, repeated) && report.Succeeded == 2, "再次导出复用了上一轮结果对象或改写了其计数");
                var sameTitle = new ArticleRecord { Id = "fixture:2", Title = article.Title, AccountName = article.AccountName, PublishedAt = article.PublishedAt, Status = ArticleStatus.Available, Url = "https://mp.weixin.qq.com/s?mid=2", Html = "<div id='wcae_body'><p>另外一篇</p></div>" };
                await service.ExportAsync(new[] { sameTitle }, session, htmlOptions, null, CancellationToken.None);
                Assert(Directory.GetFiles(htmlOptions.Destination, "article.html", SearchOption.AllDirectories).Length == 2, "同名文章发生覆盖");
                foreach (ExportFormat format in new[] { ExportFormat.Markdown, ExportFormat.Text })
                {
                    var options = new ExportOptions { Destination = Path.Combine(root, format.ToString()), Format = format, DownloadImages = false };
                    await service.ExportAsync(new[] { article, sameTitle }, session, options, null, CancellationToken.None);
                    string file = Directory.GetFiles(options.Destination, format == ExportFormat.Text ? "*.txt" : "*.md", SearchOption.AllDirectories).First(x => File.ReadAllText(x).Contains("第一段"));
                    string text = File.ReadAllText(file);
                    Assert(!text.Contains("do_evil") && !text.Contains("不属于正文") && text.Contains("第一段"), "文本格式未正确提取正文");
                    if (format == ExportFormat.Text) Assert(text.Contains("第一段\n第二行 & 内容"), "TXT 段落换行或实体解码错误");
                    Assert(Manifest(service, options.Destination).Failed == 0, "正文片段导出失败");
                }
                await TestMediaAsync(service, fake, session, article, root);
                await TestTransportTimeoutAsync(root, session);
                await TestCancellationAsync(root, session);
                await TestProcessRunnerAsync(root);
                AssertNoReportFiles(root);
                complete = true;
            }
            finally
            {
                string resolved = Path.GetFullPath(root);
                if (complete && resolved.StartsWith(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("WCAE-export-fixture-", StringComparison.Ordinal))
                    Directory.Delete(resolved, true);
            }
        }

        private static async Task TestNativeSnapshotAsync(ExportService service, FakeTransport fake, string root)
        {
            var session = new AccountSession { Biz = "native-export", Name = "主页正文测试号" };
            var article = NativeProfileTextSnapshot.Finalize(NativeProfileTextSnapshot.Create(session,
                "本地正文标题", "完整第一段\n第二段 <script>literal()</script> & 内容", "星期五", 4439, 31));
            int requests = fake.Downloads;
            foreach (var format in new[] { ExportFormat.Html, ExportFormat.Text, ExportFormat.Markdown })
            {
                var options = new ExportOptions { Destination = Path.Combine(root, "native-" + format), Format = format };
                await service.ExportAsync(new[] { article }, session, options, null, CancellationToken.None);
                string filename = format == ExportFormat.Html ? "article.html" : format == ExportFormat.Text ? "article.txt" : "article.md";
                string file = Directory.GetFiles(options.Destination, filename, SearchOption.AllDirectories).Single();
                string content = File.ReadAllText(file);
                Assert(content.Contains("完整第一段") && content.Contains("原生主页正文") && content.Contains("星期五"),
                    "主页正文导出必须保留全文及实际来源和原始日期标签");
                Assert(!content.Contains("原文：https://") && Manifest(service, options.Destination).Failed == 0,
                    "主页正文不得制造原文地址或阻断正常文字导出");
                if (format == ExportFormat.Html) Assert(!content.Contains("<script>"), "主页文字中的HTML片段必须按文字导出");
            }
            var media = new ExportOptions { Destination = Path.Combine(root, "native-media"), Format = ExportFormat.AllMedia };
            await service.ExportAsync(new[] { article }, session, media, null, CancellationToken.None);
            Assert(Manifest(service, media.Destination).Failed == 1 && fake.Downloads == requests,
                "主页副本未取得媒体时必须明确报告，不能发网络或假报完整媒体成功");
        }

        private static async Task TestMediaAsync(ExportService service, FakeTransport fake, AccountSession session, ArticleRecord article, string root)
        {
            // ID3 is metadata only; follow it with a complete MPEG Layer III frame fixture.
            var audio = new byte[10 + 417];
            Array.Copy(Encoding.ASCII.GetBytes("ID3\u0004\0\0\0\0\0\0"), audio, 10);
            audio[10] = 0xff; audio[11] = 0xfb; audio[12] = 0x90; audio[13] = 0x00;
            fake.Data["https://cdn.example.test/audio"] = audio;
            fake.Data["https://cdn.example.test/video"] = new byte[] { 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0 };
            fake.Data["https://cdn.example.test/login.mp4"] = Encoding.UTF8.GetBytes("<!DOCTYPE html><html>登录</html>");
            fake.Data["https://cdn.example.test/link.mp4"] = Encoding.UTF8.GetBytes("https://cdn.example.test/only-a-link.mp4");
            article.Media = new List<MediaAsset>
            {
                new MediaAsset { Kind = MediaKind.Image, Url = "https://cdn.example.test/picture" },
                new MediaAsset { Kind = MediaKind.Audio, Url = "https://cdn.example.test/audio" },
                new MediaAsset { Kind = MediaKind.Video, Url = "https://cdn.example.test/video" },
                new MediaAsset { Kind = MediaKind.Video, Id = "unresolved", UnavailableReason = "未解析到可下载视频，不能仅保存播放页。" },
                new MediaAsset { Kind = MediaKind.Video, Url = "https://cdn.example.test/login.mp4" },
                new MediaAsset { Kind = MediaKind.Video, Url = "https://cdn.example.test/link.mp4" }
            };
            var options = new ExportOptions { Destination = Path.Combine(root, "media"), Format = ExportFormat.AllMedia };
            await service.ExportAsync(new[] { article }, session, options, null, CancellationToken.None);
            var manifest = Manifest(service, options.Destination);
            Assert(manifest.Succeeded == 3 && manifest.Failed == 3, "媒体失败、未解析视频或伪视频被当作成功");
            Assert(Directory.GetFiles(options.Destination, "*.mp4", SearchOption.AllDirectories).Length == 1, "网页或链接被写成视频文件");
            Assert(!Directory.GetFiles(options.Destination, "*.part", SearchOption.AllDirectories).Any(), "失败后残留下载临时文件");
            string video = Directory.GetFiles(options.Destination, "*.mp4", SearchOption.AllDirectories).Single();
            byte[] previous = File.ReadAllBytes(video);
            fake.Data["https://cdn.example.test/video"] = Encoding.UTF8.GetBytes("<html>失效</html>");
            options.Overwrite = true;
            await service.ExportAsync(new[] { article }, session, options, null, CancellationToken.None);
            Assert(File.ReadAllBytes(video).SequenceEqual(previous), "覆盖失败损坏了已有完整媒体文件");
            Assert(Manifest(service, options.Destination).Failed > 0, "覆盖失败没有保留内存错误结果");
        }

        private static async Task TestCancellationAsync(string root, AccountSession session)
        {
            var transport = new FakeTransport { WaitForCancellation = true };
            var article = new ArticleRecord { Id = "cancel", Title = "取消测试", Status = ArticleStatus.Available, Url = "https://mp.weixin.qq.com/s/cancel", Media = new List<MediaAsset> { new MediaAsset { Kind = MediaKind.Video, Url = "https://cdn.example.test/cancel.mp4" } } };
            var options = new ExportOptions { Destination = Path.Combine(root, "cancel"), Format = ExportFormat.Video };
            var service = new ExportService(transport);
            using (var cts = new CancellationTokenSource())
            {
                var task = service.ExportAsync(new[] { article }, session, options, null, cts.Token);
                await transport.Started.Task;
                cts.Cancel();
                try { await task; throw new InvalidOperationException("取消未传播给调用方"); }
                catch (OperationCanceledException) { }
            }
            Assert(Manifest(service, options.Destination).Cancelled, "取消没有保留内存导出结果");
            Assert(!Directory.GetFiles(options.Destination, "*.part", SearchOption.AllDirectories).Any(), "取消残留下载临时文件");
            Assert(!Directory.GetFiles(options.Destination, "*.mp4", SearchOption.AllDirectories).Any(), "取消后的半文件被作为视频保存");
        }

        private static async Task TestTransportTimeoutAsync(string root, AccountSession session)
        {
            var transport = new FakeTransport { InternalTimeout = true };
            var article = new ArticleRecord { Id = "timeout", Title = "超时测试", Status = ArticleStatus.Available, Url = "https://mp.weixin.qq.com/s/timeout", Media = new List<MediaAsset> { new MediaAsset { Kind = MediaKind.Audio, Url = "https://cdn.example.test/timeout.mp3" } } };
            var options = new ExportOptions { Destination = Path.Combine(root, "timeout"), Format = ExportFormat.Audio };
            var service = new ExportService(transport);
            await service.ExportAsync(new[] { article }, session, options, null, CancellationToken.None);
            var report = Manifest(service, options.Destination);
            Assert(!report.Cancelled && report.Failed == 1, "传输超时被误判为用户取消整个导出");
        }

        private static async Task TestProcessRunnerAsync(string root)
        {
            string tool = Environment.ProcessPath;
            Assert(!string.IsNullOrWhiteSpace(tool) && File.Exists(tool), "无法定位正在运行的 WCAE 可执行文件");
            Assert(!string.Equals(Path.GetFileNameWithoutExtension(tool), "dotnet", StringComparison.OrdinalIgnoreCase), "请通过 WCAE.exe --self-test 运行进程测试");
            string echoed = Path.Combine(root, "quoted-arguments.txt");
            string tricky = "包含 空格 & (括号) \"引号\" 尾部\\";
            await ExternalToolRunner.RunAsync(tool, new[] { "--export-test-helper", "echo", echoed, tricky, "--option-looking", "" }, root, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert(File.ReadAllLines(echoed).SequenceEqual(new[] { tricky, "--option-looking", "" }), "Windows 参数引用未正确保留原始参数和空参数");
            try
            {
                await ExternalToolRunner.RunAsync(tool, new[] { "--export-test-helper", "fail" }, root, TimeSpan.FromSeconds(10), CancellationToken.None);
                throw new InvalidOperationException("非零退出码被当作成功");
            }
            catch (IOException ex) { Assert(ex.Message.Contains("7"), "未报告外部工具退出码"); }
            string ready = Path.Combine(root, "child-pid.txt");
            using (var cts = new CancellationTokenSource())
            {
                var running = ExternalToolRunner.RunAsync(tool, new[] { "--export-test-helper", "parent", ready }, root, TimeSpan.FromSeconds(15), cts.Token);
                var watch = Stopwatch.StartNew();
                while (!File.Exists(ready) && watch.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
                Assert(File.Exists(ready), "进程树测试辅助进程未就绪");
                int child = int.Parse(File.ReadAllText(ready));
                cts.Cancel();
                try { await running; throw new InvalidOperationException("进程取消未传播"); }
                catch (OperationCanceledException) { }
                bool exited = false;
                watch.Restart();
                while (!exited && watch.Elapsed < TimeSpan.FromSeconds(2))
                {
                    try { using (var process = Process.GetProcessById(child)) exited = process.HasExited; }
                    catch (ArgumentException) { exited = true; }
                    if (!exited) await Task.Delay(25);
                }
                Assert(exited, "外部工具取消后子进程仍在运行");
            }
        }

        private static ExportManifest Manifest(ExportService service, string root)
        {
            AssertNoReportFiles(root);
            Assert(service.LastManifest != null && service.LastManifest.FinishedUtc.HasValue, "导出结束后没有可用的内存结果");
            return service.LastManifest;
        }
        private static void AssertNoReportFiles(string root)
            => Assert(!Directory.GetFiles(root, "export-results-*", SearchOption.AllDirectories).Any(), "用户导出目录仍生成了结果清单文件");
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Export fixture: " + message); }

        private sealed class FakeTransport : IHttpTransport
        {
            internal readonly Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
            internal readonly List<AccountSession> DownloadSessions = new List<AccountSession>();
            internal readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Downloads;
            internal bool WaitForCancellation;
            internal bool InternalTimeout;
            public Task<string> GetStringAsync(string url, AccountSession session, CancellationToken token) { throw new InvalidOperationException("fixture 不允许未注册的页面请求：" + url); }
            public Task<string> PostFormAsync(string url, IDictionary<string, string> values, AccountSession session, CancellationToken token) { throw new InvalidOperationException("fixture 不使用 POST"); }
            public async Task DownloadAsync(string url, string destination, AccountSession session, CancellationToken token)
            {
                Downloads++;
                DownloadSessions.Add(session.Clone());
                if (InternalTimeout) throw new TaskCanceledException("fixture internal timeout");
                if (WaitForCancellation)
                {
                    File.WriteAllText(destination, "partial");
                    Started.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                }
                byte[] data;
                if (!Data.TryGetValue(url, out data)) throw new IOException("fixture 未注册媒体：" + url);
                token.ThrowIfCancellationRequested();
                File.WriteAllBytes(destination, data);
            }
        }

    }
}
