using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    // Reads data literals only. In particular, the observed numeric '133' * 1 form
    // is converted without evaluating JavaScript or invoking any page code.
    internal static class ArticleAudioParser
    {
        internal const int CurrentVersion = 1;
        internal sealed class Scan
        {
            internal bool Complete;
            internal bool HasStandalone;
            internal string Body = "";
        }
        internal static Scan FromHtml(ArticleRecord article, HtmlDocument document, string html)
        {
            var result = new Scan { Complete = document.GetElementbyId("wcae_body") == null };
            JToken page = null, inArticle = null, encodedList = null;
            foreach (string name in new[] { "voice_page_info", "voice_in_appmsg", "voice_in_appmsg_list_json" })
            {
                string raw = ArticleHtmlParser.RawVariable(html, name);
                if (raw.Length == 0)
                {
                    if (Regex.IsMatch(html ?? "", @"(?<![\w$])(?:['""'])?" + Regex.Escape(name) + @"(?:['""'])?\s*[:=]",
                        RegexOptions.None, TimeSpan.FromSeconds(2))) result.Complete = false;
                    continue;
                }
                if (!TryLiteral(raw, out JToken parsed)) { result.Complete = false; continue; }
                if (name == "voice_page_info") page = parsed;
                else if (name == "voice_in_appmsg") inArticle = parsed;
                else encodedList = parsed;
            }
            Apply(article, page, MergeLists(inArticle, encodedList, result), ArticleHtmlParser.JsValue(html, "item_show_type") == "7", result);
            return result;
        }
        internal static Scan FromJson(ArticleRecord article, JObject json)
        {
            var result = new Scan { Complete = true };
            Apply(article, json["voice_page_info"], MergeLists(json["voice_in_appmsg"], json["voice_in_appmsg_list_json"], result),
                ArticleHtmlParser.Integer(json["item_show_type"]) == 7, result, Scalar(json["title"]));
            return result;
        }
        static JArray MergeLists(JToken direct, JToken encoded, Scan result)
        {
            var resultList = new JArray();
            foreach (var value in new[] { direct, encoded })
            {
                if (value == null || value.Type == JTokenType.Null) continue;
                JToken parsed = value;
                if (value.Type == JTokenType.String)
                {
                    string text = HttpUtility.HtmlDecode((string)value);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (!TryLiteral(text, out parsed)) { result.Complete = false; continue; }
                }
                // The live page's *_list_json is {"voice_in_appmsg":[...]}, encoded
                // with \x22 inside a JS string. Ordinary pages also supply '{}'.
                if (parsed is JObject wrapper)
                {
                    if (wrapper.Count == 0) continue;
                    parsed = wrapper["voice_in_appmsg"];
                }
                if (!(parsed is JArray array)) { result.Complete = false; continue; }
                foreach (var item in array) if (item is JObject) resultList.Add(item.DeepClone()); else result.Complete = false;
            }
            return resultList;
        }
        static void Apply(ArticleRecord article, JToken page, JArray inArticle, bool explicitStandalone, Scan result, string fallbackTitle = "")
        {
            if (page != null && page.Type != JTokenType.Null && !(page is JObject)) result.Complete = false;
            var indexed = inArticle.OfType<JObject>().ToList();
            var standalone = page as JObject;
            string pageId = Scalar(standalone?["voice_id"]);
            bool pageHasId = ValidId(pageId);
            // Ordinary article responses contain a default, populated voice_page_info with
            // an empty voice_id. It is neither an audio item nor a failed audio scan.
            if (pageId.Length > 0 && !pageHasId) result.Complete = false;
            if (!explicitStandalone && !pageHasId) return;
            var sources = new List<JObject>();
            if (pageHasId) sources.Add(standalone);
            else sources.AddRange(indexed.Where(x => ValidId(Scalar(x["voice_id"]))).GroupBy(x => Scalar(x["voice_id"]), StringComparer.Ordinal).Select(x => x.First()));
            string title = ArticleMetadata.NormalizeTitle(Scalar(standalone?["title"]));
            if (string.IsNullOrWhiteSpace(article.Title) && title.Length > 0)
            { article.Title = title; article.RawTitle = Scalar(standalone["title"]); article.TitleSource = "voice_page_info:title"; }
            if (title.Length == 0) title = string.IsNullOrWhiteSpace(article.Title) ? ArticleMetadata.NormalizeTitle(fallbackTitle) : article.Title;
            string description = ArticleHtmlParser.Decode(Scalar(standalone?["desc"]));
            if (string.IsNullOrEmpty(article.Digest) && description.Length > 0) article.Digest = description;
            var assets = new List<MediaAsset>();
            foreach (var source in sources)
            {
                string id = Scalar(source["voice_id"]);
                var asset = Add(article, source, title, indexed.Where(x => Scalar(x["voice_id"]) == id).ToList());
                if (asset != null) assets.Add(asset);
            }
            if (assets.Count == 0)
            {
                result.Complete = false;
                var unavailable = new MediaAsset { Kind = MediaKind.Audio, Name = title,
                    UnavailableReason = "已识别独立音频页，但音频数据缺失或格式无法解析，尚未取得可下载地址" };
                article.Media.Add(unavailable); assets.Add(unavailable);
            }
            result.HasStandalone = true;
            var body = new StringBuilder("<section class=\"wcae-audio-page\"><h1>").Append(HttpUtility.HtmlEncode(title)).Append("</h1>");
            if (description.Length > 0) body.Append("<p>").Append(HttpUtility.HtmlEncode(description).Replace("\n", "<br />")).Append("</p>");
            foreach (var asset in assets)
                if (asset.Url.Length > 0) body.Append("<audio controls src=\"").Append(HttpUtility.HtmlAttributeEncode(asset.Url))
                    .Append("\" data-voiceid=\"").Append(HttpUtility.HtmlAttributeEncode(asset.Id)).Append("\"></audio>");
                else body.Append("<p>音频：").Append(HttpUtility.HtmlEncode(asset.Name)).Append("（尚未取得播放地址）</p>");
            body.Append("</section>"); result.Body = body.ToString();
        }
        static MediaAsset Add(ArticleRecord article, JObject info, string title, List<JObject> linked)
        {
            string id = Scalar(info["voice_id"]);
            if (!ValidId(id)) return null;
            string direct = new[] { "voice_url", "audio_url", "play_url" }.Select(k => Scalar(info[k]))
                .Select(x => ExplicitUrl(x)).FirstOrDefault(x => x.Length > 0) ?? "";
            // The separately supplied voice signature is matched by voice_id. A verified
            // playback URL builder may use it; no endpoint is guessed here.
            string[] signatures = linked.Select(x => Scalar(x["sn"])).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            string verified = PlaybackUrl(article, id, signatures.Length == 1 ? signatures[0] : "");
            if (direct.Length == 0) direct = verified;
            var existing = article.Media.FirstOrDefault(x => x.Kind == MediaKind.Audio && (x.Id == id || (direct.Length > 0 && x.Url == direct)));
            if (existing != null)
            {
                if (existing.Name.Length == 0) existing.Name = ArticleHtmlParser.Decode(title);
                if (existing.Url.Length == 0 && direct.Length > 0) { existing.Url = direct; existing.UnavailableReason = ""; }
                return existing;
            }
            var media = new MediaAsset { Kind = MediaKind.Audio, Id = id, Name = ArticleHtmlParser.Decode(title), Url = direct,
                UnavailableReason = direct.Length > 0 ? "" : "音频页仅提供了音频标识，尚未取得可验证的播放地址" };
            article.Media.Add(media); return media;
        }
        // Verified in the official common_share_audio player.js: standalone voice pages
        // use voice_type=1. The signature is not appended unless the player requires it.
        static string PlaybackUrl(ArticleRecord article, string id, string signature)
            => "https://res.wx.qq.com/voice/getvoice?mediaid=" + Uri.EscapeDataString(id) + "&voice_type=1";
        static string ExplicitUrl(string raw)
        {
            string text = ArticleHtmlParser.Decode(raw).Trim(); if (text.StartsWith("//", StringComparison.Ordinal)) text = "https:" + text;
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")
                && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : "";
        }
        static bool ValidId(string id) => id.Length > 0 && id.Length <= 512 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '=');
        static string Scalar(JToken value) => value is JValue && value.Type != JTokenType.Null ? value.ToString() : "";

        internal static bool TryLiteral(string raw, out JToken value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 2_000_000) return false;
            try { value = new LiteralReader(raw).Read(); return true; }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
        }
        sealed class LiteralReader
        {
            readonly string text; int at, nodes;
            internal LiteralReader(string text) { this.text = text; }
            internal JToken Read() { var result = Value(0); Space(); if (at != text.Length) throw Bad(); return result; }
            JToken Value(int depth)
            {
                Space(); if (depth > 32 || ++nodes > 10000 || at >= text.Length) throw Bad();
                if (Take('{'))
                {
                    var obj = new JObject(); Space(); if (Take('}')) return obj;
                    do
                    {
                        Space(); string key = Peek('"') || Peek('\'') ? Quoted() : Identifier(); Space();
                        if (!Take(':') || obj.Property(key, StringComparison.Ordinal) != null) throw Bad(); obj.Add(key, Value(depth + 1));
                        Space(); if (Take('}')) return obj; if (!Take(',')) throw Bad(); Space(); if (Take('}')) return obj;
                    } while (true);
                }
                if (Take('['))
                {
                    var array = new JArray(); Space(); if (Take(']')) return array;
                    do { array.Add(Value(depth + 1)); Space(); if (Take(']')) return array; if (!Take(',')) throw Bad(); Space(); if (Take(']')) return array; } while (true);
                }
                JToken value;
                if (Peek('"') || Peek('\'')) value = new JValue(Quoted());
                else if (Peek('-') || (at < text.Length && char.IsAsciiDigit(text[at])))
                {
                    int start = at; if (Peek('-')) at++; while (at < text.Length && char.IsAsciiDigit(text[at])) at++;
                    if (Take('.')) { int digits = at; while (at < text.Length && char.IsAsciiDigit(text[at])) at++; if (at == digits) throw Bad(); }
                    if (!decimal.TryParse(text.Substring(start, at - start), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out decimal number)) throw Bad(); value = new JValue(number);
                }
                else { string word = Identifier(); value = word == "true" ? new JValue(true) : word == "false" ? new JValue(false) : word == "null" ? JValue.CreateNull() : throw Bad(); }
                Space();
                if (Take('*'))
                {
                    Space(); if (!Take('1') || (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '.'))) throw Bad();
                    if ((value.Type != JTokenType.String && value.Type != JTokenType.Float && value.Type != JTokenType.Integer)
                        || !decimal.TryParse(Convert.ToString(((JValue)value).Value, CultureInfo.InvariantCulture), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out decimal converted)) throw Bad(); value = new JValue(converted);
                }
                return value;
            }
            string Quoted()
            {
                char quote = text[at++]; var result = new StringBuilder();
                while (at < text.Length)
                {
                    char c = text[at++]; if (c == quote) return result.ToString();
                    if (c != '\\') { if (c == '\r' || c == '\n') throw Bad(); result.Append(c); continue; }
                    if (at >= text.Length) throw Bad(); c = text[at++];
                    if (c == '\\' || c == '/' || c == '\'' || c == '"') result.Append(c);
                    else if (c == 'n') result.Append('\n'); else if (c == 'r') result.Append('\r'); else if (c == 't') result.Append('\t');
                    else if (c == 'b') result.Append('\b'); else if (c == 'f') result.Append('\f');
                    else if (c == 'u' || c == 'x')
                    { int width = c == 'u' ? 4 : 2; if (at + width > text.Length || !int.TryParse(text.Substring(at, width), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int n)) throw Bad(); result.Append((char)n); at += width; }
                    else throw Bad();
                }
                throw Bad();
            }
            string Identifier()
            {
                if (at >= text.Length || !(char.IsAsciiLetter(text[at]) || text[at] == '_' || text[at] == '$')) throw Bad();
                int start = at++; while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_' || text[at] == '$')) at++;
                return text.Substring(start, at - start);
            }
            void Space() { while (at < text.Length && char.IsWhiteSpace(text[at])) at++; }
            bool Peek(char c) => at < text.Length && text[at] == c;
            bool Take(char c) { if (!Peek(c)) return false; at++; return true; }
            static FormatException Bad() => new FormatException("Unsupported audio data literal");
        }
    }
}
