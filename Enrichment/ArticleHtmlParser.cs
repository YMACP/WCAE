using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    internal static class ArticleHtmlParser
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
        private const string MetricNotePrefix = "未提供的统计：";

        internal static ArticleRecord Parse(ArticleRecord article, string html)
        {
            article.StatusDetail = "";
            article.Media = new List<MediaAsset>();
            article.AudioMetadataVersion = 0;
            article.Columns = article.Columns ?? new List<ColumnInfo>();
            article.Html = "";
            if (string.IsNullOrWhiteSpace(html)) return SetStatus(article, ArticleStatus.Failed, "文章响应为空");
            if (html.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                var json = ParseJson(html) as JObject;
                if (json != null) return ParseArticleJson(article, json);
            }
            var doc = new HtmlDocument(); doc.LoadHtml(html);
            var main = doc.GetElementbyId("js_content");
            var share = doc.GetElementbyId("js_share_content") ?? doc.GetElementbyId("js_common_share_desc")
                ?? doc.DocumentNode.SelectSingleNode("//*[contains(concat(' ',normalize-space(@class),' '),' share_media_text ')]");
            bool hasMain = HasContent(main), hasShare = HasContent(share);
            string title = First(Text(doc.GetElementbyId("activity-name")), Meta(doc, "og:title"), Meta(doc, "twitter:title"));
            string pageTitle = Text(doc.DocumentNode.SelectSingleNode("//title"));
            bool challenge = doc.DocumentNode.SelectSingleNode("//*[@id='captcha' or @id='tcaptcha_transform_dy' or @id='js_verify' or @id='verify-form']") != null;
            bool login = doc.DocumentNode.SelectSingleNode("//input[translate(@type,'PASSWORD','password')='password']") != null;
            if (title.Length == 0 && (challenge || login)
                && ContainsAny(pageTitle, "环境异常", "安全验证", "访问过于频繁", "登录", "验证码"))
                return SetStatus(article, ArticleStatus.Restricted, "微信返回了访问限制、登录或验证页面");
            if (!hasMain && !hasShare)
            {
                string visible = VisibleText(doc.DocumentNode);
                bool errorShell = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'weui-msg') or contains(@class,'weui_msg') or contains(@class,'global_error')]") != null;
                if (ContainsAny(visible, "该内容已被发布者删除", "此内容已被发布者删除", "内容已被发布者删除", "该内容已删除", "此内容已被删除", "该内容因违规无法查看", "此内容因违规无法查看", "该文章已被删除", "内容不存在")
                    && (errorShell || visible.Length < 1600)) return SetStatus(article, ArticleStatus.Deleted, "微信页面显示文章已删除、不可见或不存在");
                if (challenge || login || ContainsAny(pageTitle, "环境异常", "安全验证", "访问过于频繁", "登录", "验证码")
                    || ((errorShell || visible.Length < 1200) && ContainsAny(visible, "环境异常", "完成验证", "访问过于频繁", "请在微信客户端打开", "请在微信中打开", "暂无权限", "访问受限", "登录后查看", "登录后阅读")))
                    return SetStatus(article, ArticleStatus.Restricted, "微信返回了访问限制、登录或验证页面");
            }
            var pageFields = AccountNameResolver.ReadPageScalars(doc, html);
            ArticleMetadata.ApplyHtml(article, doc, html, pageFields);
            article.Author = First(Meta(doc, "author"), Text(doc.GetElementbyId("js_author_name")), article.Author);
            article.AccountName = AccountNameResolver.Choose(article.Biz, AccountNameResolver.Extract(doc, html, article.Biz), article.AccountName);
            article.Digest = First(Meta(doc, "og:description"), Meta(doc, "description"), article.Digest);
            ArticleColumnParser.Apply(article, doc, html);
            article.ReadCount = article.ReadCount ?? Counter(html, "read_num");
            article.LikeCount = article.LikeCount ?? Counter(html, "old_like_num", "old_like_count");
            article.FavoriteCount = article.FavoriteCount ?? Counter(html, "like_num", "like_count");
            article.ShareCount = article.ShareCount ?? Counter(html, "share_num", "share_count", "rt_num");
            var audio = ArticleAudioParser.FromHtml(article, doc, html);
            if (hasMain)
            {
                if (audio.HasStandalone) main.AppendChild(HtmlNode.CreateNode(audio.Body));
                article.Html = audio.HasStandalone ? doc.DocumentNode.OuterHtml : html;
                FindMedia(article, main);
            }
            else if (hasShare)
            {
                // The exporter has a stable body marker and need not export surrounding navigation.
                article.Html = ShareDocument(article.Title, share.OuterHtml);
                FindMedia(article, share);
                // Share images may be siblings of the text block rather than its children.
                foreach (var area in doc.DocumentNode.Descendants().Where(n => n.Id == "js_share_img" || n.Id == "js_share_imgs" || n.Id == "js_share_image" || HasClass(n, "share_media_image")))
                {
                    AppendShareContent(article, area.OuterHtml);
                    FindMedia(article, area);
                }
                if (audio.HasStandalone) AppendShareContent(article, audio.Body);
            }
            else if (audio.HasStandalone)
            {
                article.Html = ShareDocument(article.Title, audio.Body);
            }
            else
            {
                // Recognize script-backed share posts only through explicit article fields.
                string shareHtml = ArticleMetadata.Unique(pageFields, "content_noencode");
                string shareText = First(ArticleMetadata.Unique(pageFields, "share_text"), ArticleMetadata.Unique(pageFields, "share_content"), ArticleMetadata.Unique(pageFields, "desc"));
                var shareImages = JsonVariable(html, "picture_page_info") ?? JsonVariable(html, "picture_page_info_list");
                var imageUrls = FindPictureUrls(shareImages).Distinct(StringComparer.Ordinal).ToList();
                if ((shareHtml.Length > 0 || shareText.Length > 0 || imageUrls.Count > 0) && (title.Length > 0 || article.Mid.Length > 0))
                {
                    var content = new StringBuilder(shareHtml.Length > 0 ? shareHtml : "<p>" + HttpUtility.HtmlEncode(shareText).Replace("\n", "<br />") + "</p>");
                    foreach (var image in imageUrls)
                    {
                        string url = NormalizeUrl(image, article.Url);
                        if (url.Length == 0) continue;
                        content.Append("<img src=\"").Append(HttpUtility.HtmlAttributeEncode(url)).Append("\" />");
                    }
                    article.Html = ShareDocument(article.Title, content.ToString());
                    var normalized = new HtmlDocument(); normalized.LoadHtml(article.Html); FindMedia(article, normalized.DocumentNode);
                }
                else return SetStatus(article, ArticleStatus.Failed, "响应中未找到可识别的文章正文；未将页面外壳作为正文保存");
            }
            article.Status = ArticleStatus.Available;
            if (audio.Complete) article.AudioMetadataVersion = ArticleAudioParser.CurrentVersion;
            foreach (string variable in new[] { "video_page_info", "video_urls", "video_info", "mp_video_trans_info" }) FindVideoJson(article, JsonVariable(html, variable));
            UpdateMetricNote(article);
            return article;
        }

        private static ArticleRecord ParseArticleJson(ArticleRecord article, JObject json)
        {
            string title = Scalar(json["title"]), content = Scalar(json["content_noencode"]), desc = Scalar(json["desc"]);
            var audio = ArticleAudioParser.FromJson(article, json);
            title = First(title, audio.HasStandalone ? article.Title : "");
            var pictures = FindPictureUrls(json["picture_page_info"] ?? json["picture_page_info_list"]).ToList();
            if (title.Length == 0 || (content.Length == 0 && desc.Length == 0 && pictures.Count == 0 && json["video_page_info"] == null && !audio.HasStandalone))
                return SetStatus(article, ArticleStatus.Failed, "文章 JSON 未提供正文或媒体数据");
            ArticleMetadata.ApplyJson(article, json);
            article.Digest = First(Decode(desc), article.Digest);
            article.Author = First(Scalar(json["author"]), article.Author);
            string responseBiz = First(Scalar(json["biz"]), Scalar(json["__biz"]));
            bool sameAccount = responseBiz.Length == 0 || article.Biz.Length == 0 || responseBiz == article.Biz;
            article.AccountName = AccountNameResolver.Choose(article.Biz, sameAccount ? Scalar(json["nickname"]) : "", article.AccountName);
            var body = new StringBuilder(content.Length > 0 ? Decode(content) : "<p>" + HttpUtility.HtmlEncode(Decode(desc)).Replace("\n", "<br />") + "</p>");
            if (audio.HasStandalone) body.Append(audio.Body);
            foreach (string picture in pictures)
            {
                string url = NormalizeUrl(picture, article.Url);
                if (url.Length > 0) body.Append("<img src=\"").Append(HttpUtility.HtmlAttributeEncode(url)).Append("\" />");
            }
            article.Html = ShareDocument(article.Title, body.ToString());
            var doc = new HtmlDocument(); doc.LoadHtml(article.Html);
            FindMedia(article, doc.DocumentNode);
            ArticleColumnParser.Apply(article, doc, json.ToString(Formatting.None));
            FindVideoJson(article, json["video_page_info"] ?? json["video_urls"]);
            ArticleEnricher.ApplyStats(article, json["appmsgstat"] as JObject ?? json);
            article.Status = ArticleStatus.Available;
            if (audio.Complete) article.AudioMetadataVersion = ArticleAudioParser.CurrentVersion;
            UpdateMetricNote(article);
            return article;
        }

        private static ArticleRecord SetStatus(ArticleRecord article, ArticleStatus status, string detail) { article.Status = status; article.StatusDetail = detail; article.Html = ""; article.Media.Clear(); return article; }
        private static string ShareDocument(string title, string content) { return "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + HttpUtility.HtmlEncode(title) + "</title></head><body><div id=\"wcae_body\">" + content + "</div></body></html>"; }
        private static void AppendShareContent(ArticleRecord article, string content) { article.Html = article.Html.Replace("</div></body></html>", content + "</div></body></html>"); }
        private static bool HasContent(HtmlNode node) { return node != null && (Text(node).Length > 0 || node.Descendants().Any(n => new[] { "img", "audio", "video", "iframe", "mpvoice", "mpvideo", "mp-common-mpaudio", "mp-common-videosnap" }.Contains(n.Name))); }
        private static bool HasClass(HtmlNode node, string name) { return (" " + node.GetAttributeValue("class", "") + " ").Contains(" " + name + " "); }
        private static bool ContainsAny(string text, params string[] patterns) { return patterns.Any(p => (text ?? "").IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0); }
        private static string First(params string[] values) { return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? ""; }
        private static string Text(HtmlNode node) { return node == null ? "" : HttpUtility.HtmlDecode(node.InnerText).Trim(); }
        private static string VisibleText(HtmlNode node) { return string.Join(" ", node.DescendantsAndSelf().Where(n => n.NodeType == HtmlNodeType.Text && !n.Ancestors().Any(a => a.Name == "script" || a.Name == "style")).Select(Text)).Trim(); }
        private static string Meta(HtmlDocument doc, string name) { var node = doc.DocumentNode.Descendants("meta").FirstOrDefault(n => n.GetAttributeValue("property", "").Equals(name, StringComparison.OrdinalIgnoreCase) || n.GetAttributeValue("name", "").Equals(name, StringComparison.OrdinalIgnoreCase)); return node == null ? "" : Decode(node.GetAttributeValue("content", "")).Trim(); }

        internal static string Decode(string value)
        {
            string text = value ?? "";
            for (int i = 0; i < 2; i++) text = HttpUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\\u([0-9a-fA-F]{4})|\\x([0-9a-fA-F]{2})", m => ((char)int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString(), RegexOptions.None, RegexTimeout);
            return text.Replace(@"\/", "/").Replace(@"\""", "\"").Replace(@"\'", "'").Replace(@"\n", "\n").Replace(@"\r", "\r").Replace(@"\t", "\t");
        }
        internal static string NormalizeUrl(string value, string baseUrl)
        {
            string text = Decode(value).Trim().Trim('"', '\'');
            if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal) || text == "undefined" || text == "null") return "";
            if (text.StartsWith("//", StringComparison.Ordinal)) text = "https:" + text;
            Uri url, baseUri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out url))
            {
                if (text.Length == 0 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri) || !Uri.TryCreate(baseUri, text, out url)) return "";
            }
            return (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) ? url.AbsoluteUri : "";
        }
        internal static string JsValue(string html, string name)
        {
            string raw = RawVariable(html, name);
            if (raw.Length == 0) return "";
            if ((raw[0] == '\'' || raw[0] == '"') && raw.Length >= 2) return Decode(raw.Substring(1, raw.Length - 2));
            if (Regex.IsMatch(raw, @"^-?\d+(?:\.\d+)?$", RegexOptions.None, RegexTimeout)) return raw;
            return "";
        }
        internal static string RawVariable(string html, string name)
        {
            var match = Regex.Match(html ?? "", @"(?<![\w$])(?:['" + "\"" + @"])?" + Regex.Escape(name) + @"(?:['" + "\"" + @"])?\s*[:=]\s*", RegexOptions.None, RegexTimeout);
            if (!match.Success) return "";
            int start = match.Index + match.Length;
            if (start >= html.Length) return "";
            char opening = html[start];
            if (opening == '\'' || opening == '"')
            {
                bool escape = false;
                for (int i = start + 1; i < html.Length; i++) { char c = html[i]; if (escape) { escape = false; continue; } if (c == '\\') { escape = true; continue; } if (c == opening) return html.Substring(start, i - start + 1); }
                return "";
            }
            if (opening == '{' || opening == '[')
            {
                int depth = 0; char quote = '\0'; bool escape = false;
                for (int i = start; i < html.Length && i - start < 2000000; i++)
                {
                    char c = html[i];
                    if (quote != '\0') { if (escape) escape = false; else if (c == '\\') escape = true; else if (c == quote) quote = '\0'; continue; }
                    if (c == '\'' || c == '"') { quote = c; continue; }
                    if (c == '{' || c == '[') depth++;
                    if (c == '}' || c == ']') { if (--depth == 0) return html.Substring(start, i - start + 1); }
                }
                return "";
            }
            var number = Regex.Match(html.Substring(start, Math.Min(100, html.Length - start)), @"^-?\d+(?:\.\d+)?", RegexOptions.None, RegexTimeout);
            return number.Success ? number.Value : "";
        }
        internal static JToken ParseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 64, DateParseHandling = DateParseHandling.None }) return JToken.ReadFrom(reader); }
            catch (JsonException) { return null; }
        }
        internal static JToken JsonVariable(string html, string name)
        {
            string raw = RawVariable(html, name);
            if (raw.StartsWith("'", StringComparison.Ordinal) || raw.StartsWith("\"", StringComparison.Ordinal)) raw = Decode(raw.Substring(1, raw.Length - 2));
            return ParseJson(raw);
        }
        internal static long? Integer(JToken value) { long result; return value != null && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? (long?)result : null; }
        internal static long? NonnegativeInteger(JToken value) { long? result = Integer(value); return result >= 0 ? result : null; }
        private static IEnumerable<JToken> AllTokens(JToken token) { if (token == null) return Enumerable.Empty<JToken>(); var container = token as JContainer; return new[] { token }.Concat(container == null ? Enumerable.Empty<JToken>() : container.Descendants()); }
        private static string Scalar(JToken value) { return value == null || value.Type == JTokenType.Null || value is JContainer ? "" : value.ToString(); }
        private static long? Counter(string html, params string[] names)
        {
            foreach (var name in names) { long count; if (long.TryParse(JsValue(html, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count >= 0) return count; }
            return null;
        }
        internal static void AddNote(ArticleRecord article, string note)
        {
            if (string.IsNullOrWhiteSpace(note) || (article.StatusDetail ?? "").Contains(note)) return;
            article.StatusDetail = string.IsNullOrEmpty(article.StatusDetail) ? note : article.StatusDetail + "；" + note;
        }
        internal static void UpdateMetricNote(ArticleRecord article)
        {
            article.StatusDetail = string.Join("；", (article.StatusDetail ?? "").Split(new[] { '；' }, StringSplitOptions.RemoveEmptyEntries).Where(x => !x.StartsWith(MetricNotePrefix, StringComparison.Ordinal)));
            var names = new List<string>();
            if (!article.ReadCount.HasValue) names.Add("浏览量"); if (!article.LikeCount.HasValue) names.Add("点赞");
            if (!article.FavoriteCount.HasValue) names.Add("喜欢"); if (!article.ShareCount.HasValue) names.Add("转发");
            if (names.Count > 0) AddNote(article, MetricNotePrefix + string.Join("、", names));
        }

        private static IEnumerable<string> FindPictureUrls(JToken json)
        {
            if (json == null) yield break;
            foreach (var prop in AllTokens(json).OfType<JProperty>())
                if (new[] { "cdn_url", "url", "img_url", "pic_url" }.Contains(prop.Name) && prop.Value.Type == JTokenType.String) yield return prop.Value.ToString();
        }

        internal static void FindVideoJson(ArticleRecord article, JToken json)
        {
            if (json == null) return;
            int previousVideoCount = article.Media.Count(m => m.Kind == MediaKind.Video && m.Url.Length > 0);
            if (json is JArray)
                foreach (var value in json.OfType<JValue>().Where(v => v.Type == JTokenType.String))
                    AddMedia(article, MediaKind.Video, value.ToString(), "", "", IsManifest(value.ToString()));
            foreach (var obj in AllTokens(json).OfType<JObject>())
            {
                if (obj.Ancestors().OfType<JProperty>().Any(p => ContainsAny(p.Name, "thumb", "cover", "poster", "picture"))) continue;
                string url = First(Scalar(obj["url"]), Scalar(obj["video_url"]), Scalar(obj["play_url"]));
                if (url.Length == 0) continue;
                // Only called for explicitly video-scoped JSON, never arbitrary page data.
                string id = First(Scalar(obj["vid"]), Scalar(obj["video_id"]));
                AddMedia(article, MediaKind.Video, url, Scalar(obj["title"]), id, IsManifest(url));
            }
            if (article.Media.Count(m => m.Kind == MediaKind.Video && m.Url.Length > 0) == previousVideoCount)
                foreach (var property in AllTokens(json).OfType<JProperty>().Where(p => p.Name == "vid" || p.Name == "video_id" || p.Name == "video_ids"))
                    foreach (string id in (property.Value is JArray ? property.Value.Values<string>() : new[] { Scalar(property.Value) }).Where(v => !string.IsNullOrWhiteSpace(v)))
                        if (TencentId(id)) AddTencent(article, "", id);
                        else Unavailable(article, MediaKind.Video, id, "", "视频 JSON 只返回了视频标识，尚未取得可用播放地址");
        }
        internal static bool IsManifest(string url) { Uri uri; return Uri.TryCreate(NormalizeUrl(url, "https://mp.weixin.qq.com/"), UriKind.Absolute, out uri) && Regex.IsMatch(uri.AbsolutePath, @"\.(m3u8|mpd)$", RegexOptions.IgnoreCase, RegexTimeout); }

        private static void FindMedia(ArticleRecord article, HtmlNode root)
        {
            foreach (var node in root.DescendantsAndSelf())
            {
                string name = First(node.GetAttributeValue("name", ""), node.GetAttributeValue("title", ""), node.GetAttributeValue("alt", ""));
                if (node.Name == "img") AddMedia(article, MediaKind.Image, First(node.GetAttributeValue("data-src", ""), node.GetAttributeValue("data-original", ""), node.GetAttributeValue("src", "")), name);
                foreach (Match match in Regex.Matches(node.GetAttributeValue("style", ""), @"url\(\s*(['" + "\"" + @"]?)(.*?)\1\s*\)", RegexOptions.IgnoreCase, RegexTimeout)) AddMedia(article, MediaKind.Image, match.Groups[2].Value, name);
                if (node.Name == "mpvoice" || node.Name == "mp-common-mpaudio" || node.Name == "audio")
                {
                    string voice = First(node.GetAttributeValue("voice_encode_fileid", ""), node.GetAttributeValue("voice-encode-fileid", ""), node.GetAttributeValue("voiceid", ""), node.GetAttributeValue("data-voiceid", ""));
                    string direct = NormalizeUrl(First(node.GetAttributeValue("src", ""), node.GetAttributeValue("data-src", "")), article.Url);
                    if (direct.Length == 0 && voice.Length > 0) direct = "https://res.wx.qq.com/voice/getvoice?mediaid=" + Uri.EscapeDataString(Decode(voice));
                    if (direct.Length > 0) AddMedia(article, MediaKind.Audio, direct, name, voice);
                    else if (!node.Descendants("source").Any()) Unavailable(article, MediaKind.Audio, voice, name, "音频组件没有可解析的音频地址或 voice_encode_fileid");
                }
                if (node.Name == "source")
                {
                    bool audio = node.Ancestors("audio").Any() || node.GetAttributeValue("type", "").StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
                    AddMedia(article, audio ? MediaKind.Audio : MediaKind.Video, node.GetAttributeValue("src", ""), name);
                }
                if (node.Name == "video" || node.Name == "mpvideo" || node.Name == "mp-common-videosnap" || node.Name == "iframe") FindVideo(article, node, name);
            }
        }
        private static void FindVideo(ArticleRecord article, HtmlNode node, string name)
        {
            string explicitVideo = First(node.GetAttributeValue("data-video-url", ""), node.GetAttributeValue("video_url", ""), node.GetAttributeValue("playurl", ""), node.GetAttributeValue("play_url", ""));
            string source = First(explicitVideo, node.GetAttributeValue("src", ""), node.GetAttributeValue("data-src", ""));
            string url = NormalizeUrl(source, article.Url);
            string vid = First(node.GetAttributeValue("vid", ""), node.GetAttributeValue("data-vid", ""), node.GetAttributeValue("video_id", ""), node.GetAttributeValue("data-id", ""));
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                if (uri.Host.Equals("v.qq.com", StringComparison.OrdinalIgnoreCase))
                {
                    vid = First(HttpUtility.ParseQueryString(uri.Query)["vid"], vid);
                    if (uri.AbsolutePath.StartsWith("/x/page/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/x/cover/", StringComparison.Ordinal)) { AddMedia(article, MediaKind.Video, url, name, vid, true); return; }
                    if (TencentId(vid)) { AddTencent(article, name, vid); return; }
                }
                if (explicitVideo.Length > 0 || node.Name == "video" || Regex.IsMatch(uri.AbsolutePath, @"\.(mp4|m4v|webm|mov|m3u8|mpd)$", RegexOptions.IgnoreCase, RegexTimeout))
                { AddMedia(article, MediaKind.Video, url, name, vid, Regex.IsMatch(uri.AbsolutePath, @"\.(m3u8|mpd)$", RegexOptions.IgnoreCase, RegexTimeout)); return; }
                if (uri.Host.Equals("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0) vid = First(HttpUtility.ParseQueryString(uri.Query)["vid"], vid);
            }
            if (node.Name == "mpvideo" && TencentId(vid)) { AddTencent(article, name, vid); return; }
            if (node.Name == "video" && node.Descendants("source").Any()) return;
            if (node.Name != "iframe" || vid.Length > 0 || source.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0)
                Unavailable(article, MediaKind.Video, vid, name, node.Name == "mp-common-videosnap" ? "视频号组件只有标识，页面未提供公开视频直链" : "视频组件未提供可解析的直链；微信内部 vid 不能直接当作腾讯视频地址");
        }
        private static bool TencentId(string value) { return Regex.IsMatch(value ?? "", @"^[A-Za-z][A-Za-z0-9]{10}$", RegexOptions.None, RegexTimeout); }
        private static void AddTencent(ArticleRecord article, string name, string vid)
        {
            // yt-dlp's official VQQVideoIE accepts /x/page/{id}.html. No private API is synthesized.
            AddMedia(article, MediaKind.Video, "https://v.qq.com/x/page/" + vid + ".html", name, vid, true);
        }
        private static void AddMedia(ArticleRecord article, MediaKind kind, string source, string name, string id = "", bool extractor = false)
        {
            string url = NormalizeUrl(source, article.Url);
            if (url.Length == 0 || article.Media.Any(m => m.Kind == kind && m.Url == url)) return;
            article.Media.Add(new MediaAsset { Kind = kind, Url = url, Name = Decode(name), Id = Decode(id), RequiresExtractor = extractor });
        }
        private static void Unavailable(ArticleRecord article, MediaKind kind, string id, string name, string reason)
        {
            if (article.Media.Any(m => m.Kind == kind && m.Url.Length == 0 && m.Id == id && m.UnavailableReason == reason)) return;
            article.Media.Add(new MediaAsset { Kind = kind, Name = Decode(name), Id = Decode(id), UnavailableReason = reason });
        }
    }
}
