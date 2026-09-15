using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace WCAE
{
    public sealed class ExportResult
    {
        public string ArticleId { get; set; }
        public string Title { get; set; }
        public string Kind { get; set; }
        public string Source { get; set; }
        public string OutputPath { get; set; }
        public string State { get; set; }
        public string Reason { get; set; }
    }

    public sealed class ExportManifest
    {
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string Format { get; set; }
        public bool Cancelled { get; set; }
        public int ArticlesProcessed { get; set; }
        public int Succeeded => Results.Count(x => x.State == "Success");
        public int Failed => Results.Count(x => x.State == "Failed");
        public int Skipped => Results.Count(x => x.State == "Skipped");
        public List<ExportResult> Results { get; set; } = new List<ExportResult>();
        internal Action<ExportResult> ResultAdded { get; set; }
    }

    public sealed class ExportService
    {
        private readonly IHttpTransport transport;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".avif", ".ico" };
        private static readonly string[] AudioExtensions = { ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac", ".mp4", ".webm" };
        private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".ts", ".m4v", ".flv" };

        public ExportService(IHttpTransport transport)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        public Func<ArticleRecord, CancellationToken, Task<ArticleRecord>> PrepareArticleAsync { get; set; }
        public bool ClearPreparedHtml { get; set; }
        public ExportManifest LastManifest { get; private set; }

        public async Task ExportAsync(IReadOnlyList<ArticleRecord> articles, AccountSession session, ExportOptions options, IProgress<ExportProgress> progress, CancellationToken token)
        {
            LastManifest = null;
            if (articles == null) throw new ArgumentNullException(nameof(articles));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.Destination)) throw new ArgumentException("请选择导出位置。");
            if (!Enum.IsDefined(typeof(ExportFormat), options.Format)) throw new ArgumentException("不支持的导出格式。");
            var settings = new ExportOptions { Destination = Path.GetFullPath(options.Destination), Format = options.Format, DownloadImages = options.DownloadImages, Overwrite = options.Overwrite };
            if (settings.Destination.Length > 165) throw new IOException("导出位置过长，请选择更短的目录。");
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(settings.Destination);
            var account = session == null ? new AccountSession() : session.Clone();
            var prepareArticle = PrepareArticleAsync;
            bool clearPreparedHtml = ClearPreparedHtml;
            var manifest = new ExportManifest { StartedUtc = DateTime.UtcNow, Format = settings.Format.ToString() };
            LastManifest = manifest;
            int processed = 0;
            manifest.ResultAdded = result => Report(progress, processed, articles.Count, manifest,
                result.Title + "｜" + KindDisplay(result.Kind) + "：" + (result.State == "Success" ? "成功" : result.State == "Failed" ? "失败" : result.State == "Cancelled" ? "已取消" : "跳过")
                + (string.IsNullOrEmpty(result.Reason) ? "" : "，" + result.Reason));
            ArticleRecord current = null;
            try
            {
                foreach (var article in articles)
                {
                    token.ThrowIfCancellationRequested();
                    current = article;
                    if (article == null) { processed++; continue; }
                    ArticleRecord prepared = null;
                    Exception preparationError = null;
                    try
                    {
                        if (prepareArticle != null)
                        {
                            try
                            {
                                prepared = await prepareArticle(article, token).ConfigureAwait(false);
                                if (prepared == null) throw new IOException("正文准备服务未返回文章记录。");
                                current = prepared;
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch (Exception ex) when (settings.Format == ExportFormat.Full) { preparationError = ex; }
                        }
                        token.ThrowIfCancellationRequested();
                        if (settings.Format != ExportFormat.Full && (current.Status == ArticleStatus.Deleted || current.Status == ArticleStatus.Restricted || current.Status == ArticleStatus.Failed))
                            throw new IOException(current.StatusText + (string.IsNullOrWhiteSpace(current.StatusDetail) ? "" : "：" + current.StatusDetail));
                        if (NativeProfileTextSnapshot.IsSnapshot(current))
                        {
                            NativeProfileTextSnapshot.Validate(current);
                            if (current.Status != ArticleStatus.LocalSnapshot) throw new IOException("主页正文尚未完成保存，请完成当前条后再导出。");
                            if (IsMediaFormat(settings.Format)) throw new IOException("此条仅保存了主页正文，尚未取得原文图片、音频或视频。");
                            current = NativeProfileTextSnapshot.Finalize(current);
                        }
                        string directory = ExportFileSystem.ArticleDirectory(settings.Destination, current, settings.Format, token);
                        Directory.CreateDirectory(directory);
                        if (settings.Format == ExportFormat.Full) await ExportFullAsync(current, directory, account, settings, manifest, preparationError, token).ConfigureAwait(false);
                        else if (IsMediaFormat(settings.Format)) await ExportMediaAsync(current, directory, account, settings, manifest, token).ConfigureAwait(false);
                        else await ExportDocumentAsync(current, directory, account, settings, manifest, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Record(manifest, Result(current, settings.Format.ToString(), "Failed", "", ErrorReason(ex))); }
                    finally { if (clearPreparedHtml && prepared != null) prepared.Html = ""; }
                    processed++;
                    manifest.ArticlesProcessed = processed;
                    Report(progress, processed, articles.Count, manifest, "已处理：" + current.Title);
                    current = null;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                manifest.Cancelled = true;
                if (current != null) Record(manifest, Result(current, settings.Format.ToString(), "Cancelled", "", "用户取消，当前临时文件已清理。"));
                throw;
            }
            finally
            {
                manifest.FinishedUtc = DateTime.UtcNow;
                Report(progress, processed, articles.Count, manifest,
                    (manifest.Cancelled ? "已停止" : "导出结束") + "；成功 " + manifest.Succeeded + "，失败 " + manifest.Failed + "，跳过 " + manifest.Skipped + "。");
                manifest.ResultAdded = null;
            }
        }

        private static bool IsMediaFormat(ExportFormat format)
        {
            return format == ExportFormat.Images || format == ExportFormat.Audio || format == ExportFormat.Video || format == ExportFormat.AllMedia;
        }

        private sealed class FullMedia
        {
            internal MediaAsset Asset;
            internal string PreferredFileName;
            internal bool ConflictingLocalNames;
            internal readonly List<FullReference> References = new List<FullReference>();
        }
        private sealed class FullReference
        {
            internal HtmlNode Node;
            internal string Attribute;
            internal bool Component;
        }

        private async Task ExportFullAsync(ArticleRecord article, string directory, AccountSession session, ExportOptions options,
            ExportManifest manifest, Exception preparationError, CancellationToken token)
        {
            string output = Path.Combine(directory, "article.html");
            bool skipDocument = !options.Overwrite && File.Exists(output);
            bool bodyFromExisting = false;
            HtmlNode body = null;
            if (skipDocument) Record(manifest, Result(article, "Html", "Skipped", output, "目标文件已存在，未覆盖。"));
            try
            {
                if (skipDocument)
                {
                    // The document is not rewritten, but its original DOM may contain media
                    // absent from article.Media or a previous failed download that needs retry.
                    string html = article.Html;
                    if (string.IsNullOrWhiteSpace(html)) { html = File.ReadAllText(output, Utf8); bodyFromExisting = true; }
                    body = PrepareBody(html);
                }
                else
                {
                    if (preparationError != null) throw new IOException("正文准备失败：" + ErrorReason(preparationError), preparationError);
                    if (article.Status == ArticleStatus.Deleted || article.Status == ArticleStatus.Restricted || article.Status == ArticleStatus.Failed)
                        throw new IOException(article.StatusText + (string.IsNullOrWhiteSpace(article.StatusDetail) ? "" : "：" + article.StatusDetail));
                    string html = article.Html;
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        Uri uri = HttpUri(article.Url);
                        html = await transport.GetStringAsync(uri.AbsoluteUri, SessionFor(uri, session), token).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(html)) throw new IOException("文章正文为空。");
                    body = PrepareBody(html);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { Record(manifest, Result(article, "Html", "Failed", "", ErrorReason(ex))); }

            var entries = new List<FullMedia>();
            var bySource = new Dictionary<string, FullMedia>(StringComparer.Ordinal);
            FullMedia Add(MediaAsset source)
            {
                string normalized = FullSource(source.Url, article.Url);
                string key = source.Kind + "|" + normalized + (normalized.Length == 0 ? "|" + source.Id + "|" + source.UnavailableReason : "");
                if (bySource.TryGetValue(key, out var found))
                {
                    if (!string.IsNullOrWhiteSpace(found.Asset.UnavailableReason) && string.IsNullOrWhiteSpace(source.UnavailableReason) && normalized.Length > 0)
                        found.Asset.UnavailableReason = "";
                    if (string.IsNullOrEmpty(found.Asset.Id)) found.Asset.Id = source.Id ?? "";
                    if (string.IsNullOrEmpty(found.Asset.Name)) found.Asset.Name = source.Name ?? "";
                    return found;
                }
                var entry = new FullMedia { Asset = new MediaAsset { Kind = source.Kind, Url = normalized, Id = source.Id ?? "",
                    Name = source.Name ?? "", RequiresExtractor = source.RequiresExtractor, UnavailableReason = source.UnavailableReason ?? "" } };
                bySource.Add(key, entry); entries.Add(entry); return entry;
            }
            foreach (var asset in article.Media ?? new List<MediaAsset>())
            { token.ThrowIfCancellationRequested(); if (asset != null) Add(asset); }
            if (body != null)
            {
                void Reference(HtmlNode node, MediaKind kind, string attribute, string raw, string id = "", bool component = false)
                {
                    if (bodyFromExisting && TryLocalReference(raw, directory, out string local))
                    {
                        // An exported relative path is never treated as a new WeChat URL.
                        // Existing files need no request. Missing files can only be repaired
                        // when their established filename identifies one known source asset.
                        string name = Path.GetFileNameWithoutExtension(local);
                        string expectedFolder = Path.Combine(directory, kind == MediaKind.Image ? "images" : kind == MediaKind.Audio ? "audio" : "video");
                        var known = entries.Where(x => x.Asset.Kind == kind
                            && string.Equals(Path.GetDirectoryName(local), expectedFolder, StringComparison.OrdinalIgnoreCase)
                            && AllowedExtensions(kind).Contains(Path.GetExtension(local).ToLowerInvariant())
                            && MatchesMediaStem(name, kind, MediaHash(x.Asset))).Take(2).ToList();
                        if (known.Count == 1)
                        {
                            string fileName = Path.GetFileName(local);
                            if (known[0].PreferredFileName != null && !string.Equals(known[0].PreferredFileName, fileName, StringComparison.OrdinalIgnoreCase))
                                known[0].ConflictingLocalNames = true;
                            else known[0].PreferredFileName = fileName;
                            known[0].References.Add(new FullReference { Node = node, Attribute = attribute, Component = component }); return;
                        }
                        if (File.Exists(local)) return;
                        var missing = Add(new MediaAsset { Kind = kind, Id = "local-reference:" + raw,
                            UnavailableReason = "本地正文引用的媒体文件缺失，且没有可验证的原始下载地址；刷新原文后重试。" });
                        missing.References.Add(new FullReference { Node = node, Attribute = attribute, Component = component }); return;
                    }
                    string source = FullSource(raw, article.Url);
                    FullMedia entry = null;
                    if (source.Length > 0) bySource.TryGetValue(kind + "|" + source, out entry);
                    if (entry == null && id.Length > 0)
                    {
                        var identified = entries.Where(x => x.Asset.Kind == kind && x.Asset.Id == id).Take(2).ToList();
                        if (identified.Count == 1) entry = identified[0];
                    }
                    if (entry == null)
                        entry = Add(new MediaAsset { Kind = kind, Url = source, Id = id,
                            Name = FirstNonEmpty(node.GetAttributeValue("name", ""), node.GetAttributeValue("title", ""), node.GetAttributeValue("alt", "")),
                            UnavailableReason = source.Length == 0 ? "正文中的媒体组件未提供可匹配的下载地址。" : "" });
                    entry.References.Add(new FullReference { Node = node, Attribute = attribute, Component = component });
                }
                foreach (var node in body.Descendants().ToList())
                {
                    token.ThrowIfCancellationRequested();
                    string name = node.Name.ToLowerInvariant();
                    if (name == "img")
                    {
                        string source = FirstNonEmpty(node.GetAttributeValue("data-src", ""), node.GetAttributeValue("data-original", ""), node.GetAttributeValue("src", ""));
                        if (source.Length == 0) node.Remove(); else Reference(node, MediaKind.Image, "src", source);
                    }
                    else if (name == "audio" || name == "mpvoice" || name == "mp-common-mpaudio")
                    {
                        string source = FirstNonEmpty(node.GetAttributeValue("src", ""), node.GetAttributeValue("data-src", ""));
                        string id = FirstNonEmpty(node.GetAttributeValue("voice_encode_fileid", ""), node.GetAttributeValue("voice-encode-fileid", ""),
                            node.GetAttributeValue("voiceid", ""), node.GetAttributeValue("data-voiceid", ""));
                        if (source.Length > 0 || id.Length > 0 || !node.Descendants("source").Any())
                            Reference(node, MediaKind.Audio, "src", source, id, name != "audio");
                    }
                    else if (name == "video" || name == "mpvideo" || name == "mp-common-videosnap")
                    {
                        string source = FirstNonEmpty(node.GetAttributeValue("data-video-url", ""), node.GetAttributeValue("video_url", ""),
                            node.GetAttributeValue("playurl", ""), node.GetAttributeValue("play_url", ""), node.GetAttributeValue("src", ""), node.GetAttributeValue("data-src", ""));
                        string id = FirstNonEmpty(node.GetAttributeValue("vid", ""), node.GetAttributeValue("data-vid", ""), node.GetAttributeValue("video_id", ""), node.GetAttributeValue("data-id", ""));
                        if (source.Length > 0 || id.Length > 0 || !node.Descendants("source").Any())
                            Reference(node, MediaKind.Video, "src", source, id, name != "video");
                        string poster = node.GetAttributeValue("poster", "");
                        if (poster.Length > 0) Reference(node, MediaKind.Image, "poster", poster);
                    }
                    else if (name == "source")
                    {
                        string type = node.GetAttributeValue("type", "");
                        bool audio = node.Ancestors("audio").Any() || type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
                        bool video = node.Ancestors("video").Any() || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
                        if (audio || video) Reference(node, audio ? MediaKind.Audio : MediaKind.Video, "src", node.GetAttributeValue("src", ""));
                    }
                }
            }
            if (NativeProfileTextSnapshot.IsSnapshot(article))
                Record(manifest, Result(article, "AllMedia", "Skipped", "", "此条仅保存了主页正文，尚未取得原文图片、音频或视频；已导出可用正文。"));
            var indexes = new Dictionary<MediaKind, int>(); int failed = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                string folder = Path.Combine(directory, entry.Asset.Kind == MediaKind.Image ? "images" : entry.Asset.Kind == MediaKind.Audio ? "audio" : "video");
                indexes.TryGetValue(entry.Asset.Kind, out int index); indexes[entry.Asset.Kind] = ++index;
                var result = entry.ConflictingLocalNames
                    ? Result(article, entry.Asset.Kind.ToString(), "Failed", "", "现有正文对同一媒体引用了多个文件名，无法唯一确认补回位置。", entry.Asset.Url)
                    : await ExportAssetAsync(article, entry.Asset, folder, index, session, options.Overwrite, token, true, entry.PreferredFileName).ConfigureAwait(false);
                if (skipDocument && entry.PreferredFileName != null && (result.State == "Success" || result.State == "Skipped")
                    && !string.Equals(Path.GetFileName(result.OutputPath), entry.PreferredFileName, StringComparison.OrdinalIgnoreCase))
                {
                    result.State = "Failed";
                    result.Reason = "媒体已按实际格式保存，但格式已变化，现有正文仍引用旧扩展名；请启用覆盖导出以更新正文引用。";
                }
                Record(manifest, result);
                bool saved = (result.State == "Success" || result.State == "Skipped") && !string.IsNullOrEmpty(result.OutputPath);
                string relative = saved ? string.Join("/", Path.GetRelativePath(directory, result.OutputPath).Replace('\\', '/').Split('/').Select(Uri.EscapeDataString)) : "";
                if (!saved) failed++;
                foreach (var reference in entry.References) ApplyFullReference(reference, entry.Asset.Kind, relative);
            }
            if (body != null && !skipDocument)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    bool committed = AtomicText(output, BuildDocument(body.InnerHtml, article), options.Overwrite, token);
                    Record(manifest, Result(article, "Html", committed ? "Success" : "Skipped", output,
                        !committed ? "目标文件已存在，未覆盖。" : failed > 0 ? "正文已导出；有 " + failed + " 项媒体未能保存，具体原因见日志。" : "正文及可匹配媒体引用已保存到本地。"));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { Record(manifest, Result(article, "Html", "Failed", "", ErrorReason(ex))); }
            }
        }

        private static string FullSource(string source, string articleUrl)
        {
            if (string.IsNullOrWhiteSpace(source)) return "";
            try { return ResolveSource(source, articleUrl); }
            catch (Exception ex) when (ex is IOException || ex is UriFormatException || ex is ArgumentException) { return WebUtility.HtmlDecode(source).Trim(); }
        }
        private static bool TryLocalReference(string source, string directory, out string path)
        {
            path = ""; string raw = WebUtility.HtmlDecode(source ?? "").Trim();
            if (raw.Length == 0 || raw.StartsWith("//", StringComparison.Ordinal) || Uri.TryCreate(raw, UriKind.Absolute, out _)) return false;
            try
            {
                string relative = Uri.UnescapeDataString(raw).Replace('/', Path.DirectorySeparatorChar);
                string candidate = Path.GetFullPath(Path.Combine(directory, relative));
                if (!candidate.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
                path = candidate; return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException) { return false; }
        }
        private static void ApplyFullReference(FullReference reference, MediaKind kind, string relative)
        {
            var node = reference.Node;
            if (reference.Component)
            {
                string tag = kind == MediaKind.Audio ? "audio" : "video";
                var replacement = relative.Length > 0 ? HtmlNode.CreateNode("<" + tag + " controls src=\"" + WebUtility.HtmlEncode(relative) + "\"></" + tag + ">")
                    : HtmlNode.CreateNode("<span>" + (kind == MediaKind.Audio ? "[音频未能下载]" : "[视频未能下载]") + "</span>");
                node.ParentNode?.ReplaceChild(replacement, node); return;
            }
            node.Attributes.Remove("srcset");
            foreach (var attribute in node.Attributes.Where(x => x.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase)).ToList()) node.Attributes.Remove(attribute);
            if (relative.Length > 0)
            {
                node.SetAttributeValue(reference.Attribute, relative);
                if (node.Name == "audio" || node.Name == "video") node.SetAttributeValue("controls", "controls");
            }
            else
            {
                node.Attributes.Remove(reference.Attribute);
                if (node.Name == "img") node.SetAttributeValue("alt", "[图片未能下载]");
            }
        }

        private async Task ExportDocumentAsync(ArticleRecord article, string directory, AccountSession session, ExportOptions options, ExportManifest manifest, CancellationToken token)
        {
            string extension = options.Format == ExportFormat.Markdown ? ".md" : options.Format == ExportFormat.Text ? ".txt" : options.Format == ExportFormat.Pdf ? ".pdf" : ".html";
            string output = Path.Combine(directory, "article" + extension);
            if (!options.Overwrite && File.Exists(output))
            {
                Record(manifest, Result(article, options.Format.ToString(), "Skipped", output, "目标文件已存在，未覆盖。"));
                return;
            }
            string html = article.Html;
            if (string.IsNullOrWhiteSpace(html))
            {
                var uri = HttpUri(article.Url);
                html = await transport.GetStringAsync(uri.AbsoluteUri, SessionFor(uri, session), token).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(html)) throw new IOException("文章正文为空。");
            var body = PrepareBody(html);
            int imageFailures = 0;
            if (options.Format != ExportFormat.Text)
                imageFailures = await PrepareImagesAsync(body, article, directory, session, options, manifest, token).ConfigureAwait(false);
            string document = BuildDocument(body.InnerHtml, article);
            token.ThrowIfCancellationRequested();
            bool committed;
            if (options.Format == ExportFormat.Pdf)
            {
                committed = await WritePdfAsync(document, output, options.Overwrite, token).ConfigureAwait(false);
            }
            else
            {
                string content = document;
                if (options.Format == ExportFormat.Markdown)
                {
                    content = "# " + EscapeMarkdown(article.Title) + "\n\n" + MarkdownMetadata(article) + "\n\n" + new ReverseMarkdown.Converter().Convert(body.InnerHtml);
                    content = Regex.Replace(content, @"\n{3,}", "\n\n").Trim() + "\n";
                }
                else if (options.Format == ExportFormat.Text)
                {
                    content = (article.Title ?? "") + "\n" + PlainMetadata(article) + "\n\n" + ToPlainText(body.InnerHtml);
                }
                committed = AtomicText(output, content, options.Overwrite, token);
            }
            string reason = imageFailures > 0 ? "文档已写入，但有 " + imageFailures + " 张图片未下载，详见逐项结果。" :
                !options.DownloadImages && options.Format != ExportFormat.Text ? "图片保留在线地址，未请求下载。" : "";
            Record(manifest, Result(article, options.Format.ToString(), !committed ? "Skipped" : imageFailures > 0 ? "Failed" : "Success", output, !committed ? "目标文件已存在，未覆盖。" : reason));
        }

        private async Task<int> PrepareImagesAsync(HtmlNode body, ArticleRecord article, string directory, AccountSession session, ExportOptions options, ExportManifest manifest, CancellationToken token)
        {
            var images = body.SelectNodes(".//img")?.ToList() ?? new List<HtmlNode>();
            var completed = new Dictionary<string, ExportResult>(StringComparer.Ordinal);
            int failed = 0, index = 0;
            foreach (var image in images)
            {
                token.ThrowIfCancellationRequested();
                string source = FirstNonEmpty(image.GetAttributeValue("data-src", ""), image.GetAttributeValue("data-original", ""), image.GetAttributeValue("src", ""));
                image.Attributes.Remove("srcset");
                foreach (var attribute in image.Attributes.Where(x => x.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase)).ToList()) image.Attributes.Remove(attribute);
                index++;
                if (string.IsNullOrWhiteSpace(source)) { image.Remove(); continue; }
                try
                {
                    source = ResolveSource(source, article.Url);
                    if (!options.DownloadImages) { image.SetAttributeValue("src", source); continue; }
                    ExportResult result;
                    if (!completed.TryGetValue(source, out result))
                    {
                        var asset = new MediaAsset { Kind = MediaKind.Image, Url = source, Name = "正文图片 " + index };
                        result = await ExportAssetAsync(article, asset, Path.Combine(directory, "images"), index, session, options.Overwrite, token).ConfigureAwait(false);
                        completed[source] = result;
                        Record(manifest, result);
                    }
                    if (result.State == "Success" || result.State == "Skipped")
                        image.SetAttributeValue("src", "images/" + Uri.EscapeDataString(Path.GetFileName(result.OutputPath)));
                    else
                    {
                        failed++;
                        image.Attributes.Remove("src");
                        image.SetAttributeValue("alt", "[图片未能下载]");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    image.Attributes.Remove("src");
                    image.SetAttributeValue("alt", "[图片地址无效]");
                    Record(manifest, Result(article, "Image", "Failed", "", ErrorReason(ex), source));
                }
            }
            return failed;
        }

        private async Task ExportMediaAsync(ArticleRecord article, string directory, AccountSession session, ExportOptions options, ExportManifest manifest, CancellationToken token)
        {
            var assets = (article.Media ?? new List<MediaAsset>()).Where(x => x != null &&
                (options.Format == ExportFormat.AllMedia || options.Format == ExportFormat.Images && x.Kind == MediaKind.Image ||
                 options.Format == ExportFormat.Audio && x.Kind == MediaKind.Audio || options.Format == ExportFormat.Video && x.Kind == MediaKind.Video))
                .GroupBy(x => x.Kind + "|" + x.Url + "|" + x.Id + "|" + x.UnavailableReason, StringComparer.Ordinal).Select(x => x.First()).ToList();
            if (assets.Count == 0)
            {
                Record(manifest, Result(article, options.Format.ToString(), "Skipped", "", options.Format == ExportFormat.Audio
                    ? "文章中未识别到音频资源，尚未发起音频下载。" : "文章中未发现该类型媒体资源。"));
                return;
            }
            var indexes = new Dictionary<MediaKind, int>();
            foreach (var asset in assets)
            {
                token.ThrowIfCancellationRequested();
                var folder = Path.Combine(directory, asset.Kind == MediaKind.Image ? "images" : asset.Kind == MediaKind.Audio ? "audio" : "video");
                int index;
                indexes.TryGetValue(asset.Kind, out index);
                indexes[asset.Kind] = ++index;
                Record(manifest, await ExportAssetAsync(article, asset, folder, index, session, options.Overwrite, token).ConfigureAwait(false));
            }
        }

        private static string MediaHash(MediaAsset asset) => ExportFileSystem.Hash(FirstNonEmpty(asset.Id, PublicSource(asset.Url), asset.Url), 10);
        private static bool MatchesMediaStem(string stem, MediaKind kind, string hash)
            => Regex.IsMatch(stem ?? "", "^" + kind.ToString().ToLowerInvariant() + @"_[0-9]{3,}_" + Regex.Escape(hash) + "$", RegexOptions.CultureInvariant);

        private async Task<ExportResult> ExportAssetAsync(ArticleRecord article, MediaAsset asset, string directory, int index, AccountSession session, bool overwrite,
            CancellationToken token, bool stableIdentity = false, string preferredFileName = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(asset.UnavailableReason)) throw new IOException(asset.Kind == MediaKind.Audio
                    ? "音频暂不可下载：" + asset.UnavailableReason : asset.UnavailableReason);
                if (string.IsNullOrWhiteSpace(asset.Url)) throw new IOException(asset.Kind == MediaKind.Audio
                    ? "已识别音频，但尚未解析到可下载地址。" : "资源没有可下载地址。");
                string source = ResolveSource(asset.Url, article.Url);
                string mediaKey = !string.IsNullOrWhiteSpace(asset.Id) ? asset.Id : PublicSource(source);
                if (mediaKey.Length == 0) mediaKey = source;
                string hash = ExportFileSystem.Hash(mediaKey, 10);
                string stem = asset.Kind.ToString().ToLowerInvariant() + "_" + index.ToString("D3") + "_" + hash;
                Directory.CreateDirectory(directory);
                string existing;
                if (stableIdentity)
                {
                    if (preferredFileName != null)
                    {
                        if (Path.GetFileName(preferredFileName) != preferredFileName
                            || !AllowedExtensions(asset.Kind).Contains(Path.GetExtension(preferredFileName).ToLowerInvariant())
                            || !MatchesMediaStem(Path.GetFileNameWithoutExtension(preferredFileName), asset.Kind, hash))
                            throw new IOException("已有正文媒体文件名与当前媒体身份不一致，未改写文件。");
                        stem = Path.GetFileNameWithoutExtension(preferredFileName);
                    }
                    string preferred = preferredFileName == null ? null : Path.Combine(directory, preferredFileName);
                    if (preferred != null && File.Exists(preferred)) existing = preferred;
                    else
                    {
                        var candidates = new List<string>();
                        foreach (string file in Directory.EnumerateFiles(directory, asset.Kind.ToString().ToLowerInvariant() + "_*_" + hash + ".*", SearchOption.TopDirectoryOnly))
                        {
                            token.ThrowIfCancellationRequested();
                            if (AllowedExtensions(asset.Kind).Contains(Path.GetExtension(file).ToLowerInvariant())
                                && MatchesMediaStem(Path.GetFileNameWithoutExtension(file), asset.Kind, hash)) candidates.Add(file);
                            if (candidates.Count > 1) throw new IOException("同一媒体存在多个既有文件，无法唯一确认应复用哪一个；未重复下载。");
                        }
                        existing = candidates.SingleOrDefault();
                    }
                    if (preferredFileName == null && existing != null) stem = Path.GetFileNameWithoutExtension(existing);
                    if (!overwrite && existing != null && preferredFileName != null
                        && !string.Equals(Path.GetFileName(existing), preferredFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        string actual = VerifyMediaFile(existing, asset.Kind) ?? Path.GetExtension(existing);
                        string repaired = Path.Combine(directory, stem + actual), temporaryCopy = ExportFileSystem.TemporaryFile(repaired);
                        try
                        {
                            using (var input = new FileStream(existing, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                            using (var copy = new FileStream(temporaryCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                                await input.CopyToAsync(copy, 81920, token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            bool restored = ExportFileSystem.Commit(temporaryCopy, repaired, false);
                            return Result(article, asset.Kind.ToString(), restored ? "Success" : "Skipped", repaired,
                                restored ? "已从现有媒体恢复本地正文引用，未重复下载。" : "目标文件已存在，未覆盖。", source);
                        }
                        finally { ExportFileSystem.DeleteTemporaryFile(temporaryCopy); }
                    }
                }
                else existing = Directory.EnumerateFiles(directory, stem + ".*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(x => AllowedExtensions(asset.Kind).Contains(Path.GetExtension(x).ToLowerInvariant()));
                if (!overwrite && existing != null) return Result(article, asset.Kind.ToString(), "Skipped", existing, "目标文件已存在，未覆盖。", source);
                Uri uri = null;
                bool dataImage = source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
                if (!dataImage) uri = HttpUri(source);
                bool extracted = asset.Kind != MediaKind.Image && (asset.RequiresExtractor || IsPlaylist(uri));
                if (extracted) return await ExtractMediaAsync(article, asset, uri, directory, stem, overwrite, token).ConfigureAwait(false);
                string output = Path.Combine(directory, stem + GuessExtension(uri, asset.Kind));
                string temporary = ExportFileSystem.TemporaryFile(output);
                try
                {
                    if (dataImage)
                    {
                        if (asset.Kind != MediaKind.Image) throw new IOException("不支持该媒体的数据地址。");
                        int comma = source.IndexOf(',');
                        if (comma < 0 || comma > 120 || !source.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) throw new IOException("不支持的内嵌图片编码。");
                        if (source.Length > 16 * 1024 * 1024) throw new IOException("内嵌图片超过允许大小。");
                        byte[] data = Convert.FromBase64String(source.Substring(comma + 1));
                        await WriteBytesAsync(temporary, data, token).ConfigureAwait(false);
                    }
                    else
                    {
                        try { await transport.DownloadAsync(uri.AbsoluteUri, temporary, SessionFor(uri, session), token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) when (asset.Kind == MediaKind.Audio) { throw AudioDownloadFailure(ex); }
                    }
                    token.ThrowIfCancellationRequested();
                    string detected = VerifyMediaFile(temporary, asset.Kind);
                    if ((asset.Kind == MediaKind.Image || asset.Kind == MediaKind.Audio) && detected != null)
                        output = Path.Combine(directory, stem + detected);
                    token.ThrowIfCancellationRequested();
                    bool saved = ExportFileSystem.Commit(temporary, output, overwrite);
                    return Result(article, asset.Kind.ToString(), saved ? "Success" : "Skipped", output, saved ? "" : "目标文件已存在，未覆盖。", source);
                }
                finally { ExportFileSystem.DeleteTemporaryFile(temporary); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Result(article, asset.Kind.ToString(), "Failed", "", ErrorReason(ex), asset.Url); }
        }

        private async Task<ExportResult> ExtractMediaAsync(ArticleRecord article, MediaAsset asset, Uri uri, string directory, string stem, bool overwrite, CancellationToken token)
        {
            await BundledTools.EnsureMediaAsync(token).ConfigureAwait(false);
            string tool = Path.Combine(AppPaths.ToolsDirectory, "yt-dlp.exe");
            string ffmpeg = Path.Combine(AppPaths.ToolsDirectory, "ffmpeg.exe");
            if (!File.Exists(ffmpeg)) throw new FileNotFoundException("缺少 ffmpeg.exe，无法可靠合并或转换该媒体。");
            string working = ExportFileSystem.CreateWorkDirectory(directory);
            try
            {
                var arguments = new List<string>
                {
                    "--ignore-config", "--no-playlist", "--no-progress", "--newline", "--restrict-filenames", "--no-cache-dir",
                    "--socket-timeout", "20", "--retries", "2", "--fragment-retries", "2", "--concurrent-fragments", "1",
                    "--ffmpeg-location", AppPaths.ToolsDirectory, "--output", Path.Combine(working, "media.%(ext)s")
                };
                if (asset.Kind == MediaKind.Audio) arguments.AddRange(new[] { "--extract-audio", "--audio-format", "mp3" });
                else arguments.AddRange(new[] { "--merge-output-format", "mp4/mkv" });
                arguments.Add("--");
                arguments.Add(uri.AbsoluteUri);
                if ((transport as HttpTransport)?.SelectedProxy == null) arguments.InsertRange(1, new[] { "--proxy", "" });
                // Match the explicit outbound selection without putting proxy credentials on the command line.
                // No cookies, browser profiles, authorization headers or session files are passed to this tool.
                await ExternalToolRunner.RunAsync(tool, arguments, working, TimeSpan.FromMinutes(45), token, ProxyEnvironment()).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var outputs = Directory.EnumerateFiles(working, "media.*", SearchOption.TopDirectoryOnly)
                    .Where(x => AllowedExtensions(asset.Kind).Contains(Path.GetExtension(x).ToLowerInvariant())).ToList();
                if (outputs.Count != 1) throw new IOException("媒体工具未产生唯一的完整媒体文件；不将播放链接或分片作为导出成功。");
                string detected = VerifyMediaFile(outputs[0], asset.Kind);
                string output = Path.Combine(directory, stem + (asset.Kind == MediaKind.Audio && detected != null
                    ? detected : Path.GetExtension(outputs[0]).ToLowerInvariant()));
                token.ThrowIfCancellationRequested();
                bool saved = ExportFileSystem.Commit(outputs[0], output, overwrite);
                return Result(article, asset.Kind.ToString(), saved ? "Success" : "Skipped", output, saved ? "" : "目标文件已存在，未覆盖。", uri.AbsoluteUri);
            }
            finally { ExportFileSystem.DeleteWorkDirectory(directory, working); }
        }

        private async Task<bool> WritePdfAsync(string html, string output, bool overwrite, CancellationToken token)
        {
            await BundledTools.EnsurePdfAsync(token).ConfigureAwait(false);
            var directory = Path.GetDirectoryName(output);
            string input = Path.Combine(directory, ".pdf-input-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".html");
            string temporary = ExportFileSystem.TemporaryFile(output);
            try
            {
                AtomicText(input, html, false, token);
                await ExternalToolRunner.RunAsync(Path.Combine(AppPaths.ToolsDirectory, "wkhtmltopdf.exe"), new[]
                {
                    "--quiet", "--encoding", "utf-8", "--disable-javascript", "--disable-local-file-access", "--allow", directory,
                    "--load-error-handling", "ignore", "--load-media-error-handling", "ignore", input, temporary
                }, directory, TimeSpan.FromMinutes(3), token, ProxyEnvironment()).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!File.Exists(temporary) || new FileInfo(temporary).Length < 5) throw new IOException("PDF 工具未生成有效文件。");
                using (var stream = File.OpenRead(temporary))
                {
                    var header = new byte[5];
                    if (stream.Read(header, 0, 5) != 5 || Encoding.ASCII.GetString(header) != "%PDF-") throw new IOException("导出结果不是有效 PDF 文件。");
                }
                return ExportFileSystem.Commit(temporary, output, overwrite);
            }
            finally { ExportFileSystem.DeleteTemporaryFile(input); ExportFileSystem.DeleteTemporaryFile(temporary); }
        }

        internal IReadOnlyDictionary<string,string> ProxyEnvironment()
        {
            var proxy = (transport as HttpTransport)?.SelectedProxy;
            string address = "";
            if (proxy != null)
            {
                string credentials = string.IsNullOrEmpty(proxy.Username) ? "" : Uri.EscapeDataString(proxy.Username) + ":" + Uri.EscapeDataString(proxy.Password ?? "") + "@";
                address = (proxy.Protocol == ProxyProtocol.Socks5 ? "socks5://" : "http://") + credentials + proxy.Authority;
            }
            return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            { ["http_proxy"] = address, ["https_proxy"] = address, ["all_proxy"] = address, ["no_proxy"] = "" };
        }

        internal static HtmlNode PrepareBody(string html)
        {
            var document = new HtmlDocument();
            document.LoadHtml(html ?? "");
            var source = document.GetElementbyId("js_content") ?? document.GetElementbyId("wcae_body") ?? document.GetElementbyId("wechatlist_body") ?? document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;
            var bodyDocument = new HtmlDocument();
            bodyDocument.LoadHtml("<div>" + source.InnerHtml + "</div>");
            var body = bodyDocument.DocumentNode.SelectSingleNode("/div") ?? bodyDocument.DocumentNode;
            foreach (var node in body.Descendants().Where(x => new[] { "script", "style", "iframe", "object", "embed", "base", "link", "meta", "form", "input", "button", "noscript", "foreignobject" }.Contains(x.Name.ToLowerInvariant())).ToList()) node.Remove();
            foreach (var node in body.DescendantsAndSelf().ToList())
            {
                foreach (var attribute in node.Attributes.ToList())
                {
                    string name = attribute.Name.ToLowerInvariant();
                    if (name.StartsWith("on", StringComparison.Ordinal) || name == "srcdoc" || name == "formaction" || name == "srcset") node.Attributes.Remove(attribute);
                    else if (name == "style" && Regex.IsMatch(attribute.Value, @"url\s*\(|expression\s*\(|@import|behavior\s*:|-moz-binding", RegexOptions.IgnoreCase)) node.Attributes.Remove(attribute);
                    else if ((name == "href" || name == "src" || name == "poster" || name.EndsWith(":href", StringComparison.Ordinal)) && !SafeLink(attribute.Value, name != "href")) node.Attributes.Remove(attribute);
                }
            }
            return body;
        }

        private static bool SafeLink(string source, bool allowImageData)
        {
            source = WebUtility.HtmlDecode(source ?? "").Trim();
            if (source.StartsWith("#", StringComparison.Ordinal) || source.StartsWith("//", StringComparison.Ordinal)) return true;
            if (allowImageData && source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
            Uri uri;
            if (Uri.TryCreate(source, UriKind.Absolute, out uri)) return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || !allowImageData && uri.Scheme == "mailto";
            return !source.Contains(":") && !source.StartsWith("\\", StringComparison.Ordinal);
        }

        private static string BuildDocument(string body, ArticleRecord article)
        {
            var metadata = WebUtility.HtmlEncode(PlainMetadata(article)).Replace("\n", "<br>");
            return "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + WebUtility.HtmlEncode(article.Title ?? "") +
                "</title><style>body{font-family:Arial,\"Microsoft YaHei\",sans-serif;line-height:1.8;margin:32px auto;padding:0 24px;max-width:820px;color:#222}h1{line-height:1.4}.meta{color:#666;font-size:14px;margin-bottom:28px;word-break:break-all}img{max-width:100%;height:auto}pre,table{max-width:100%;overflow-wrap:anywhere}a{color:#176a51}blockquote{border-left:3px solid #bbb;margin-left:0;padding-left:16px}</style></head><body><h1>" +
                WebUtility.HtmlEncode(article.Title ?? "") + "</h1><div class=\"meta\">" + metadata + "</div><article id=\"js_content\">" + body + "</article></body></html>";
        }

        private static string PlainMetadata(ArticleRecord article)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(article.AccountName)) lines.Add("公众号：" + article.AccountName);
            if (!string.IsNullOrWhiteSpace(article.Author)) lines.Add("作者：" + article.Author);
            if (NativeProfileTextSnapshot.IsSnapshot(article))
            {
                lines.Add("来源：原生主页正文；未取得独立原文链接及完整媒体。");
                if (!string.IsNullOrWhiteSpace(article.NativeDateLabel)) lines.Add("页面日期标记：" + article.NativeDateLabel + "（采集时显示，非精确发布时间）");
            }
            if (article.PublishedAt.HasValue) lines.Add("发布时间：" + article.PublishedAt.Value.ToString("yyyy-MM-dd HH:mm"));
            if (!string.IsNullOrWhiteSpace(article.Url)) lines.Add("原文：" + PublicSource(article.Url));
            return string.Join("\n", lines);
        }

        private static string MarkdownMetadata(ArticleRecord article) { return EscapeMarkdown(PlainMetadata(article)).Replace("\n", "  \n"); }
        private static string EscapeMarkdown(string text) { return Regex.Replace(text ?? "", @"([\\`*_{}\[\]<>])", @"\$1"); }
        internal static string ToPlainText(string html)
        {
            string value = Regex.Replace(html ?? "", @"<br\s*/?>|</(?:p|div|h[1-6]|li|blockquote|tr|section|article)>", "\n", RegexOptions.IgnoreCase);
            value = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", ""));
            value = Regex.Replace(value, @"[ \t\u00a0]+", " ");
            value = Regex.Replace(value, @" *\r?\n *", "\n");
            return Regex.Replace(value, @"\n{3,}", "\n\n").Trim() + "\n";
        }

        private static string ResolveSource(string source, string page)
        {
            source = WebUtility.HtmlDecode(source ?? "").Trim();
            if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return source;
            if (source.StartsWith("//", StringComparison.Ordinal)) source = "https:" + source;
            Uri uri;
            if (Uri.TryCreate(source, UriKind.Absolute, out uri)) return HttpUri(source).AbsoluteUri;
            Uri root;
            if (Uri.TryCreate(page, UriKind.Absolute, out root) && Uri.TryCreate(root, source, out uri)) return HttpUri(uri.AbsoluteUri).AbsoluteUri;
            throw new IOException("资源地址无法解析。");
        }

        private static Uri HttpUri(string value)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo)) throw new IOException("仅支持有效的 HTTP/HTTPS 媒体地址。");
            return uri;
        }

        private static AccountSession SessionFor(Uri uri, AccountSession session)
        {
            if (uri != null && string.Equals(uri.Host, "mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase)) return session.Clone();
            return new AccountSession { UserAgent = string.IsNullOrWhiteSpace(session.UserAgent) ? "Mozilla/5.0" : session.UserAgent };
        }

        private static bool IsPlaylist(Uri uri)
        {
            return uri != null && (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase));
        }
        private static string[] AllowedExtensions(MediaKind kind) { return kind == MediaKind.Image ? ImageExtensions : kind == MediaKind.Audio ? AudioExtensions : VideoExtensions; }
        private static string GuessExtension(Uri uri, MediaKind kind)
        {
            string extension = uri == null ? "" : Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (AllowedExtensions(kind).Contains(extension)) return extension;
            if (kind == MediaKind.Image && uri != null)
            {
                string format = System.Web.HttpUtility.ParseQueryString(uri.Query)["wx_fmt"];
                if (!string.IsNullOrWhiteSpace(format) && ImageExtensions.Contains("." + format.ToLowerInvariant())) return "." + format.ToLowerInvariant();
            }
            return kind == MediaKind.Image ? ".jpg" : kind == MediaKind.Audio ? ".mp3" : ".mp4";
        }

        private static string VerifyMediaFile(string file, MediaKind kind)
        {
            if (!File.Exists(file) || new FileInfo(file).Length == 0) throw new IOException("下载结果为空，未生成媒体文件。");
            byte[] prefix = new byte[512]; int count;
            using (var stream = File.OpenRead(file)) count = stream.Read(prefix, 0, prefix.Length);
            string text = Encoding.UTF8.GetString(prefix, 0, count).TrimStart('\uFEFF', ' ', '\t', '\r', '\n').ToLowerInvariant();
            if (text.StartsWith("<!doctype html") || text.StartsWith("<html") || text.StartsWith("<head") || text.StartsWith("<body") || text.StartsWith("{") || text.StartsWith("["))
                throw new IOException("服务器返回网页或错误信息，没有返回媒体内容。");
            if (text.StartsWith("#extm3u") || text.StartsWith("http://") || text.StartsWith("https://"))
                throw new IOException("返回内容是播放列表或链接，需要媒体提取器处理，未将它当作音视频文件。");
            if (kind == MediaKind.Image)
            {
                string extension = DetectImageExtension(file);
                if (extension == null) throw new IOException("下载内容没有可识别的图片格式，未保存为图片。");
                return extension;
            }
            if (kind == MediaKind.Audio)
            {
                string extension = DetectAudioExtension(file);
                if (extension == null) throw new IOException("下载内容没有可识别的音频文件头，未生成音频文件。");
                return extension;
            }
            bool mp4 = count >= 12 && Encoding.ASCII.GetString(prefix, 4, 4) == "ftyp";
            bool webm = count >= 4 && prefix[0] == 0x1a && prefix[1] == 0x45 && prefix[2] == 0xdf && prefix[3] == 0xa3;
            bool riff = count >= 12 && Encoding.ASCII.GetString(prefix, 0, 4) == "RIFF";
            bool recognized;
            recognized = mp4 || webm || riff && Encoding.ASCII.GetString(prefix, 8, 4) == "AVI " ||
                    count >= 3 && Encoding.ASCII.GetString(prefix, 0, 3) == "FLV" ||
                    count > 188 && prefix[0] == 0x47 && prefix[188] == 0x47 ||
                    count >= 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 1 && (prefix[3] == 0xba || prefix[3] == 0xb3);
            if (!recognized) throw new IOException("下载内容没有可识别的音视频文件头，未把文本或播放地址报告为成功。");
            return null;
        }

        private static IOException AudioDownloadFailure(Exception error)
        {
            HttpStatusCode? status = error is HttpRequestFailureException failure ? failure.StatusCode
                : error is WebException web && web.Response is HttpWebResponse response ? response.StatusCode : null;
            if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden || (int?)status == 429)
                return new IOException("音频下载受限（HTTP " + (int)status.Value + "），请刷新原文更新会话后再试。", error);
            if (status.HasValue) return new IOException("音频下载失败（HTTP " + (int)status.Value + "），播放地址可能已失效。", error);
            if (error is OperationCanceledException || error is TimeoutException)
                return new IOException("音频网络下载超时，未生成音频文件。", error);
            return new IOException("音频下载失败（" + error.GetType().Name + "），请检查网络连接或保存位置。", error);
        }

        // Playback endpoints often omit a suffix or advertise .mp3 for ADTS AAC.
        // Keep the returned bytes intact and name the actual file/container format.
        private static string DetectAudioExtension(string file)
        {
            using var stream = File.OpenRead(file);
            byte[] b = new byte[512]; int n = stream.Read(b, 0, b.Length);
            bool id3 = n >= 10 && Encoding.ASCII.GetString(b, 0, 3) == "ID3"
                && b[3] >= 2 && b[3] <= 4 && b.Skip(6).Take(4).All(x => x < 128);
            if (id3)
            {
                long tagEnd = 10L + ((long)b[6] << 21 | (long)b[7] << 14 | (long)b[8] << 7 | b[9]);
                if (b[3] == 4 && (b[5] & 0x10) != 0) tagEnd += 10;
                if (tagEnd >= stream.Length) return null;
                stream.Position = tagEnd; n = stream.Read(b, 0, b.Length);
            }
            // ADTS has a 12-bit sync word and zero layer bits; test it before MPEG audio.
            if (n >= 7 && b[0] == 0xff && (b[1] & 0xf6) == 0xf0 && ((b[2] >> 2) & 15) <= 12)
            {
                int frameLength = ((b[3] & 3) << 11) | (b[4] << 3) | (b[5] >> 5);
                int headerLength = (b[1] & 1) == 0 ? 9 : 7;
                if (frameLength >= headerLength && frameLength <= stream.Length - (stream.Position - n)) return ".aac";
                return null;
            }
            // MP3 is MPEG Layer III; reserved version/sample-rate/bitrate values are not evidence.
            if (n >= 4 && b[0] == 0xff && (b[1] & 0xe0) == 0xe0 && ((b[1] >> 3) & 3) != 1
                && ((b[1] >> 1) & 3) == 1 && (b[2] >> 4) != 15 && ((b[2] >> 2) & 3) != 3) return ".mp3";
            if (n >= 16 && Encoding.ASCII.GetString(b, 4, 4) == "ftyp")
            {
                uint boxSize = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
                if (boxSize >= 16 && boxSize <= stream.Length) return ".m4a";
            }
            if (n >= 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WAVE") return ".wav";
            if (n >= 4 && Encoding.ASCII.GetString(b, 0, 4) == "fLaC") return ".flac";
            if (n >= 4 && Encoding.ASCII.GetString(b, 0, 4) == "OggS")
                return Encoding.ASCII.GetString(b, 0, n).Contains("OpusHead", StringComparison.Ordinal) ? ".opus" : ".ogg";
            if (n >= 4 && b[0] == 0x1a && b[1] == 0x45 && b[2] == 0xdf && b[3] == 0xa3) return ".webm";
            return null;
        }

        private static string DetectImageExtension(string file)
        {
            byte[] b = new byte[64]; int n;
            using (var stream = File.OpenRead(file)) n = stream.Read(b, 0, b.Length);
            if (n >= 3 && b[0] == 0xff && b[1] == 0xd8 && b[2] == 0xff) return ".jpg";
            if (n >= 8 && b[0] == 137 && Encoding.ASCII.GetString(b, 1, 3) == "PNG" && b[4] == 13 && b[5] == 10 && b[6] == 26 && b[7] == 10) return ".png";
            if (n >= 6 && Encoding.ASCII.GetString(b, 0, 3) == "GIF") return ".gif";
            if (n >= 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP") return ".webp";
            if (n >= 2 && b[0] == 'B' && b[1] == 'M') return ".bmp";
            if (n >= 12 && Encoding.ASCII.GetString(b, 4, 4) == "ftyp" && Encoding.ASCII.GetString(b, 8, 4) == "avif") return ".avif";
            if (n >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0) return ".ico";
            string text = Encoding.UTF8.GetString(b, 0, n).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)) return ".svg";
            return null;
        }

        private static async Task WriteBytesAsync(string destination, byte[] data, CancellationToken token)
        {
            using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await stream.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        private static bool AtomicText(string destination, string value, bool overwrite, CancellationToken token)
        {
            string temporary = ExportFileSystem.TemporaryFile(destination);
            try
            {
                token.ThrowIfCancellationRequested();
                File.WriteAllText(temporary, value, Utf8);
                token.ThrowIfCancellationRequested();
                return ExportFileSystem.Commit(temporary, destination, overwrite);
            }
            finally { ExportFileSystem.DeleteTemporaryFile(temporary); }
        }

        private static ExportResult Result(ArticleRecord article, string kind, string state, string path, string reason, string source = null)
        {
            reason = Regex.Replace(reason ?? "", @"([?&](?:uin|key|pass_ticket|appmsg_token|wxtoken|token|access_token|auth|signature)=)[^&\s\""'<>]+", "$1[redacted]", RegexOptions.IgnoreCase);
            return new ExportResult { ArticleId = article.Id ?? "", Title = article.Title ?? "", Kind = kind, State = state, OutputPath = path ?? "", Reason = reason.Length > 6000 ? reason.Substring(0, 6000) : reason, Source = PublicSource(source ?? article.Url) };
        }

        private static string ErrorReason(Exception error)
        {
            return error is OperationCanceledException ? "网络请求超时或由传输层取消，当前条目失败。" : error.Message;
        }

        private static string PublicSource(string source)
        {
            Uri uri;
            if (Uri.TryCreate(source, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                // Preserve article identity parameters, but never persist captured session credentials in reports.
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                foreach (string key in query.AllKeys.Where(k => k != null && new[] { "uin", "key", "pass_ticket", "appmsg_token", "wxtoken", "token", "access_token", "auth", "signature" }.Contains(k.ToLowerInvariant())).ToArray()) query.Remove(key);
                var safe = new UriBuilder(uri) { Query = query.ToString(), Fragment = "", UserName = "", Password = "" };
                return safe.Uri.AbsoluteUri;
            }
            return "";
        }

        private static void Record(ExportManifest manifest, ExportResult result)
        { manifest.Results.Add(result); manifest.ResultAdded?.Invoke(result); }
        private static string KindDisplay(string kind) => kind == "Html" ? "正文HTML" : kind == "Text" ? "正文TXT"
            : kind == "Markdown" ? "正文Markdown" : kind == "Pdf" ? "正文PDF" : kind == "Image" || kind == "Images" ? "图片"
            : kind == "Audio" ? "音频" : kind == "Video" ? "视频" : kind == "AllMedia" ? "全部媒体" : kind == "Full" ? "全文" : kind;
        private static void Report(IProgress<ExportProgress> progress, int completed, int total, ExportManifest manifest, string message)
        {
            progress?.Report(new ExportProgress { Completed = completed, Total = total, Failed = manifest.Results.Where(x => x.State == "Failed").Select(x => x.ArticleId).Distinct().Count(), Message = message });
        }
        private static string FirstNonEmpty(params string[] values) { return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? ""; }
    }
}
