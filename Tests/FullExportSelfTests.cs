using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace WCAE
{
    public static class FullExportSelfTests
    {
        const string Image = "https://cdn.example.test/photo?wx_fmt=png", ExtraImage = "https://cdn.example.test/extra.png";
        const string Audio = "https://res.wx.qq.com/voice/getvoice?mediaid=full-fixture&voice_type=1";
        const string Video = "https://cdn.example.test/movie.mp4";
        public static void Run() => RunAsync().GetAwaiter().GetResult();
        public static async Task RunAsync()
        {
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.Combine(temp, "WCAE-full-export-" + Guid.NewGuid().ToString("N"));
            string registryRoot = root + "-registry";
            Directory.CreateDirectory(root); bool complete = false;
            using var registryScope = ExportFileSystem.UseDirectoryRegistry(new ExportDirectoryRegistry(registryRoot));
            try
            {
                await MixedMediaAreLocalAndDownloadedOnce(Path.Combine(root, "mixed"));
                await MediaFailureKeepsBodyAndLaterAssets(Path.Combine(root, "media-failure"));
                await BodyAndPreparationFailureKeepMedia(Path.Combine(root, "body-failure"));
                await ExistingFilesAndOverwriteFailureRemainAtomic(Path.Combine(root, "overwrite"));
                await ExistingHtmlRetriesDomOnlyMissingMedia(Path.Combine(root, "retry-dom"));
                await ReorderedMediaReuseStableFiles(Path.Combine(root, "reordered"));
                await MissingSecondImageRestoresItsOriginalReference(Path.Combine(root, "restore-second"));
                await ChangedAudioFormatDoesNotMasqueradeAsOldExtension(Path.Combine(root, "changed-audio"));
                await CancellationKeepsOnlyCompletedFiles(Path.Combine(root, "cancel"));
                await SnapshotFullKeepsItsKnownLimit(Path.Combine(root, "snapshot"));
                Check(!Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Any()
                    && !Directory.GetFiles(root, "*.csv", SearchOption.AllDirectories).Any(), "导出目录产生了 JSON/CSV 附加报告");
                complete = true;
            }
            finally
            {
                string resolved = Path.GetFullPath(root);
                if (complete && Path.GetDirectoryName(resolved) == temp && Path.GetFileName(resolved).StartsWith("WCAE-full-export-", StringComparison.Ordinal))
                    Directory.Delete(resolved, true);
                string registry = Path.GetFullPath(registryRoot);
                if (complete && Path.GetDirectoryName(registry) == temp && Path.GetFileName(registry).StartsWith("WCAE-full-export-", StringComparison.Ordinal)
                    && registry.EndsWith("-registry", StringComparison.Ordinal) && Directory.Exists(registry)) Directory.Delete(registry, true);
            }
        }
        static ArticleRecord Article(string id = "full-fixture") => new ArticleRecord
        { Id = id, Biz = "full-biz", Mid = "101", Idx = 1, Title = "完整导出测试", Status = ArticleStatus.Available,
            Url = "https://mp.weixin.qq.com/s?__biz=full-biz&mid=101&idx=1", Html = "<div id='js_content'><p>保存正文</p></div>" };
        static ExportOptions Options(string root, bool overwrite = false) => new ExportOptions
        { Destination = root, Format = ExportFormat.Full, DownloadImages = false, Overwrite = overwrite };
        static MediaAsset Asset(MediaKind kind, string url, string id = "") => new MediaAsset { Kind = kind, Url = url, Id = id, Name = "媒体" };
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Full export self-test: " + message); }
        static string HtmlOutput(ExportService service) => service.LastManifest.Results.Single(x => x.Kind == "Html").OutputPath;

        static async Task MixedMediaAreLocalAndDownloadedOnce(string root)
        {
            var article = Article();
            article.Media.AddRange(new[] { Asset(MediaKind.Image, Image), Asset(MediaKind.Image, Image, "duplicate-id"),
                Asset(MediaKind.Audio, Audio, "voice-id"), Asset(MediaKind.Video, Video, "video-id") });
            article.Html = "<div id='js_content'><p>保存正文</p><img data-src='" + Image + "'><img src='" + Image + "'>"
                + "<img data-original='" + ExtraImage + "'><mpvoice voice_encode_fileid='voice-id'></mpvoice>"
                + "<audio><source src='" + Audio.Replace("&", "&amp;") + "' type='audio/mpeg'></audio>"
                + "<video src='" + Video + "' poster='" + Image + "'></video><mpvideo vid='video-id'></mpvideo></div>";
            var transport = new FakeTransport(); var progress = new Messages(); var service = new ExportService(transport);
            await service.ExportAsync(new[] { article }, new AccountSession(), Options(root), progress, CancellationToken.None);
            Check(transport.Downloads.Count == 4 && transport.Downloads.GroupBy(x => x).All(x => x.Count() == 1),
                "HTML 和媒体清单重复下载，或未强制下载正文图片");
            Check(service.LastManifest.Succeeded == 5 && service.LastManifest.Failed == 0 && service.LastManifest.FinishedUtc.HasValue,
                "内存结果缺少正文/四项媒体或未结束");
            string output = HtmlOutput(service), directory = Path.GetDirectoryName(output);
            var doc = new HtmlDocument(); doc.Load(output);
            Check(!doc.DocumentNode.Descendants().Any(x => x.Name == "mpvoice" || x.Name == "mpvideo"), "平台组件未替换为本地媒体控件");
            var references = doc.DocumentNode.Descendants().Where(x => x.Name == "img" || x.Name == "audio" || x.Name == "source" || x.Name == "video")
                .SelectMany(x => new[] { x.GetAttributeValue("src", ""), x.GetAttributeValue("poster", "") }).Where(x => x.Length > 0).ToList();
            Check(references.Count >= 7 && references.All(x => !Uri.TryCreate(x, UriKind.Absolute, out _)
                && File.Exists(Path.Combine(directory, Uri.UnescapeDataString(x).Replace('/', Path.DirectorySeparatorChar)))),
                "正文中仍有在线媒体或本地引用未指向保存文件");
            Check(Directory.GetFiles(directory, "*.mp3", SearchOption.AllDirectories).Length == 1, "音频扩展名或去重错误");
            Check(progress.Items.Last().Message.Contains("成功 5") && progress.Items.All(x => !x.Message.Contains("export-results")),
                "日志缺少最终计数或仍指向外部报告");
        }
        static async Task MediaFailureKeepsBodyAndLaterAssets(string root)
        {
            var article = Article(); article.Media.AddRange(new[] { Asset(MediaKind.Image, Image), Asset(MediaKind.Audio, Audio), Asset(MediaKind.Video, Video) });
            article.Html = "<div id='js_content'><p>图片失败也保存本文</p><img src='" + Image + "'><audio src='" + Audio + "'></audio><video src='" + Video + "'></video></div>";
            var transport = new FakeTransport { FailUrl = Image }; var progress = new Messages(); var service = new ExportService(transport);
            await service.ExportAsync(new[] { article }, null, Options(root), progress, CancellationToken.None);
            Check(service.LastManifest.Failed == 1 && service.LastManifest.Results.Single(x => x.Kind == "Html").State == "Success"
                && transport.Downloads.Count == 3 && File.Exists(HtmlOutput(service)), "媒体失败阻断正文或后续媒体");
            Check(progress.Items.Any(x => x.Message.Contains("图片：失败") && x.Message.Contains("fixture-media-error")), "日志未显示单个媒体失败原因");
            var document = new HtmlDocument(); document.Load(HtmlOutput(service));
            Check(document.DocumentNode.Descendants("img").Single().GetAttributeValue("src", "").Length == 0,
                "失败媒体仍保留在线引用");
        }
        static async Task BodyAndPreparationFailureKeepMedia(string root)
        {
            foreach (bool prepareFails in new[] { false, true })
            {
                var article = Article(prepareFails ? "prepare-failure" : "body-failure"); article.Html = "";
                article.Media.Add(Asset(MediaKind.Audio, Audio)); var transport = new FakeTransport { FailBody = true };
                var service = new ExportService(transport);
                if (prepareFails) service.PrepareArticleAsync = (a, token) => throw new IOException("fixture-prepare-error");
                await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
                Check(service.LastManifest.Results.Any(x => x.Kind == "Html" && x.State == "Failed")
                    && service.LastManifest.Results.Any(x => x.Kind == "Audio" && x.State == "Success") && transport.Downloads.Count == 1,
                    "正文准备/获取失败阻断已有音频导出");
            }
        }
        static async Task ExistingFilesAndOverwriteFailureRemainAtomic(string root)
        {
            var article = Article(); article.Media.Add(Asset(MediaKind.Audio, Audio));
            var transport = new FakeTransport(); var service = new ExportService(transport);
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            string html = HtmlOutput(service), audio = service.LastManifest.Results.Single(x => x.Kind == "Audio").OutputPath;
            byte[] oldHtml = File.ReadAllBytes(html), oldAudio = File.ReadAllBytes(audio);
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            Check(transport.Downloads.Count == 1 && service.LastManifest.Skipped == 2 && HtmlOutput(service) == html,
                "不覆盖重复下载已有媒体或目录不复用");
            transport.FailUrl = Audio;
            await service.ExportAsync(new[] { article }, null, Options(root, true), null, CancellationToken.None);
            Check(File.ReadAllBytes(audio).SequenceEqual(oldAudio) && service.LastManifest.Failed == 1 && File.Exists(html),
                "覆盖下载失败破坏了已有音频文件");
            Check(!Directory.GetFiles(root, "*.tmp*", SearchOption.AllDirectories).Any(), "覆盖失败留下临时文件");
        }
        static async Task ExistingHtmlRetriesDomOnlyMissingMedia(string root)
        {
            var article = Article();
            article.Html = "<div id='js_content'><picture><source srcset='https://cdn.example.test/responsive.webp 2x' type='image/webp'>"
                + "<img src='" + Image + "'></picture></div>";
            var transport = new FakeTransport { FailUrl = Image }; var service = new ExportService(transport);
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            string output = HtmlOutput(service); byte[] previous = File.ReadAllBytes(output);
            Check(service.LastManifest.Failed == 1 && service.LastManifest.Results.All(x => x.Kind != "Video"),
                "picture source被错当视频或首轮图片失败结果不正确");
            transport.FailUrl = null;
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            Check(service.LastManifest.Results.Any(x => x.Kind == "Image" && x.State == "Success") && service.LastManifest.Failed == 0
                && transport.Downloads.Count == 2 && File.ReadAllBytes(output).SequenceEqual(previous),
                "已存在HTML使DOM独有缺失媒体不再重试，或不覆盖时改写了正文");
            // A subsequent run may only have the saved HTML. It must never reinterpret
            // exported relative filenames as remote mp.weixin.qq.com download addresses.
            await service.ExportAsync(new[] { article }, null, Options(root, true), null, CancellationToken.None);
            article.Html = ""; int downloads = transport.Downloads.Count;
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            Check(transport.Downloads.Count == downloads && service.LastManifest.Failed == 0,
                "已保存本地媒体引用被当成远程URL再次请求");
        }
        static async Task ReorderedMediaReuseStableFiles(string root)
        {
            var article = Article();
            article.Media.AddRange(new[] { Asset(MediaKind.Image, Image), Asset(MediaKind.Image, ExtraImage) });
            article.Html = "<div id='js_content'><img src='" + Image + "'><img src='" + ExtraImage + "'></div>";
            var transport = new FakeTransport(); var service = new ExportService(transport);
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            string html = HtmlOutput(service); byte[] originalHtml = File.ReadAllBytes(html);
            var originalFiles = service.LastManifest.Results.Where(x => x.Kind == "Image").ToDictionary(x => x.Source, x => x.OutputPath);
            article.Media.Reverse();
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            Check(transport.Downloads.Count == 2 && service.LastManifest.Skipped == 3 && service.LastManifest.Failed == 0,
                "媒体顺序改变后又下载了已存在的文件");
            Check(service.LastManifest.Results.Where(x => x.Kind == "Image").All(x => x.OutputPath == originalFiles[x.Source])
                && Directory.GetFiles(root, "*.png", SearchOption.AllDirectories).Length == 2 && File.ReadAllBytes(html).SequenceEqual(originalHtml),
                "媒体顺序改变导致新序号文件或改写不允许覆盖的正文");
        }
        static async Task MissingSecondImageRestoresItsOriginalReference(string root)
        {
            var article = Article();
            article.Media.AddRange(new[] { Asset(MediaKind.Image, Image), Asset(MediaKind.Image, ExtraImage) });
            article.Html = "<div id='js_content'><img src='" + Image + "'><img src='" + ExtraImage + "'></div>";
            var transport = new FakeTransport(); var service = new ExportService(transport);
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            string html = HtmlOutput(service); byte[] originalHtml = File.ReadAllBytes(html);
            string second = service.LastManifest.Results.Single(x => x.Kind == "Image" && x.Source == ExtraImage).OutputPath;
            Check(Path.GetFileName(second).StartsWith("image_002_", StringComparison.Ordinal)
                && Path.GetFullPath(second).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "测试第二图片路径不正确");
            File.Delete(second);
            article.Html = ""; article.Media.Clear(); article.Media.Add(Asset(MediaKind.Image, ExtraImage));
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            Check(transport.Downloads.Count == 3 && service.LastManifest.Failed == 0
                && service.LastManifest.Results.Single(x => x.Kind == "Image").OutputPath == second && File.Exists(second),
                "仅剩第二媒体重试时没有恢复正文引用的原序号路径");
            Check(Directory.GetFiles(root, "*.png", SearchOption.AllDirectories).Length == 2 && File.ReadAllBytes(html).SequenceEqual(originalHtml),
                "补回第二图产生了错误序号文件或改动现有正文");
        }
        static async Task ChangedAudioFormatDoesNotMasqueradeAsOldExtension(string root)
        {
            var article = Article(); article.Media.Add(Asset(MediaKind.Audio, Audio));
            article.Html = "<div id='js_content'><audio src='" + Audio.Replace("&", "&amp;") + "'></audio></div>";
            var transport = new FakeTransport(); var service = new ExportService(transport);
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            string html = HtmlOutput(service), oldAudio = service.LastManifest.Results.Single(x => x.Kind == "Audio").OutputPath;
            byte[] originalHtml = File.ReadAllBytes(html);
            Check(Path.GetExtension(oldAudio) == ".mp3" && Path.GetFullPath(oldAudio).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "测试音频路径不正确");
            File.Delete(oldAudio); article.Html = "";
            transport.AudioBytes = new byte[] { 255, 241, 80, 128, 1, 127, 252, 0, 0, 0, 0 };
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            var result = service.LastManifest.Results.Single(x => x.Kind == "Audio");
            Check(result.State == "Failed" && result.Reason.Contains("实际格式") && result.Reason.Contains("覆盖")
                && Path.GetExtension(result.OutputPath) == ".aac" && File.Exists(result.OutputPath) && !File.Exists(oldAudio)
                && File.ReadAllBytes(html).SequenceEqual(originalHtml), "格式变化被伪装为旧后缀或把失效正文引用报告成成功修复");
        }
        static async Task CancellationKeepsOnlyCompletedFiles(string root)
        {
            using var cancel = new CancellationTokenSource();
            var article = Article(); article.Media.AddRange(new[] { Asset(MediaKind.Image, Image), Asset(MediaKind.Audio, Audio), Asset(MediaKind.Video, Video) });
            var transport = new FakeTransport { CancelOnUrl = Audio, Cancellation = cancel }; var service = new ExportService(transport);
            bool canceled = false;
            try { await service.ExportAsync(new[] { article }, null, Options(root), null, cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && service.LastManifest.Cancelled && service.LastManifest.FinishedUtc.HasValue
                && service.LastManifest.Results.Count(x => x.State == "Success") == 1 && !transport.Downloads.Contains(Video),
                "取消后继续下载或未保留内存取消结果");
            Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 1
                && !Directory.GetFiles(root, "article.html", SearchOption.AllDirectories).Any(), "取消后提交了当前临时文件或未完成正文");
        }
        static async Task SnapshotFullKeepsItsKnownLimit(string root)
        {
            var article = NativeProfileTextSnapshot.Finalize(NativeProfileTextSnapshot.Create(new AccountSession { Biz = "snapshot-biz", Name = "主页测试号" },
                "主页短帖", "主页短帖\n完整可见正文", "今天"));
            var transport = new FakeTransport(); var service = new ExportService(transport);
            await service.ExportAsync(new[] { article }, null, Options(root), null, CancellationToken.None);
            Check(service.LastManifest.Results.Any(x => x.Kind == "Html" && x.State == "Success")
                && service.LastManifest.Results.Any(x => x.Kind == "AllMedia" && x.Reason.Contains("尚未取得")) && transport.Downloads.Count == 0,
                "主页正文未导出或虚称已取得全部媒体");
        }
        sealed class Messages : IProgress<ExportProgress>
        { internal readonly List<ExportProgress> Items = new List<ExportProgress>(); public void Report(ExportProgress value) => Items.Add(value); }
        sealed class FakeTransport : IHttpTransport
        {
            internal readonly List<string> Downloads = new List<string>();
            internal string FailUrl, CancelOnUrl; internal bool FailBody; internal CancellationTokenSource Cancellation;
            internal byte[] AudioBytes;
            public Task<string> GetStringAsync(string url, AccountSession session, CancellationToken token)
            { token.ThrowIfCancellationRequested(); if (FailBody) throw new IOException("fixture-body-error"); return Task.FromResult("<div id='js_content'>正文</div>"); }
            public Task<string> PostFormAsync(string url, IDictionary<string, string> values, AccountSession session, CancellationToken token) => throw new InvalidOperationException("Unexpected form request");
            public async Task DownloadAsync(string url, string destination, AccountSession session, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Downloads.Add(url);
                byte[] data = url == Audio ? AudioBytes ?? new byte[] { 255, 251, 144, 100, 1, 2, 3, 4 }
                    : url == Video ? new byte[] { 0, 0, 0, 16, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0 }
                    : new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };
                await File.WriteAllBytesAsync(destination, data, token);
                if (url == CancelOnUrl) { Cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                if (url == FailUrl) throw new IOException("fixture-media-error");
            }
        }
    }
}
