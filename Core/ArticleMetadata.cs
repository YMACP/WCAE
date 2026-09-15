using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    // Metadata only: these methods never fetch a page, change its storage key, or replace its body.
    public static class ArticleMetadata
    {
        public const int CurrentVersion = 2;
        static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

        public static string NormalizeTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 8192) return "";
            string text = value;
            for (int i = 0; i < 2; i++) text = HttpUtility.HtmlDecode(text);
            return Regex.Replace(text, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
        }

        public static void ApplyHistory(ArticleRecord article, string rawTitle, DateTime? publishedAt)
        {
            if (article == null) throw new ArgumentNullException(nameof(article));
            article.HistoryTitle = rawTitle ?? "";
            article.HistoryPublishedAt = publishedAt;
            ApplyTitle(article, rawTitle, "history");
            if (publishedAt.HasValue) ApplyDate(article, publishedAt.Value, "history", ToUnixSeconds(publishedAt.Value));
        }

        public static bool ApplyHtml(ArticleRecord article, string html)
        {
            if (article == null) throw new ArgumentNullException(nameof(article));
            if (string.IsNullOrWhiteSpace(html) || html.Length > 32 * 1024 * 1024) return false;
            if (html.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                try { return ApplyJson(article, JObject.Parse(html)); }
                catch (JsonException) { return false; }
            }
            var document = new HtmlDocument(); document.LoadHtml(html);
            return ApplyHtml(article, document, html);
        }

        internal static bool ApplyHtml(ArticleRecord article, HtmlDocument document, string html, Dictionary<string, List<string>> fields = null)
        {
            string before = Fingerprint(article);
            fields ??= AccountNameResolver.ReadPageScalars(document, html);
            if (!MatchesPage(article, fields)) return false;
            FillMissingIdentity(article, fields);

            var body = document.GetElementbyId("js_content");
            var shortBody = document.GetElementbyId("js_share_content") ?? document.GetElementbyId("js_common_share_desc")
                ?? document.DocumentNode.Descendants().FirstOrDefault(n => HasClass(n, "share_media_text"));
            // wcae_body is an old export wrapper, not evidence of a particular WeChat message type.
            if (HasContent(body)) article.ContentKind = ArticleContentKind.Article;
            else if (HasContent(shortBody) || Unique(fields, "content_noencode").Length > 0
                || Unique(fields, "share_text").Length > 0 || Unique(fields, "share_content").Length > 0)
                article.ContentKind = ArticleContentKind.ShortPost;

            var heading = document.GetElementbyId("activity-name");
            string headingTitle = heading != null && !heading.Ancestors().Any(n => n.GetAttributeValue("id", "") == "js_content"
                || n.Name == "script" || n.Name == "template") ? heading.InnerText : "";
            // Select the page's best candidate before applying title protection. Applying
            // a generic `title` first can promote an account name, then incorrectly block
            // the actual msg_title/heading as an unrelated replacement of a strong title.
            var titleCandidates = new[] {
                (Raw: headingTitle, Source: "heading"),
                (Raw: Unique(fields, "msg_title"), Source: "script:msg_title"),
                (Raw: Meta(document, "og:title"), Source: "meta:og:title"),
                (Raw: Meta(document, "twitter:title"), Source: "meta:twitter:title"),
                (Raw: Unique(fields, "title"), Source: "script:title")
            };
            var chosenTitle = titleCandidates.FirstOrDefault(candidate => NormalizeTitle(candidate.Raw).Length > 0
                && !(candidate.Source == "script:title" && NormalizeTitle(candidate.Raw) == NormalizeTitle(article.AccountName)));
            bool repairAccountTitle = CanRepairCachedAccountTitle(article, chosenTitle.Raw, chosenTitle.Source);
            string priorPageRaw = article.PageRawTitle, priorPageSource = article.PageTitleSource;
            bool preserveRepairEvidence = IsCachedAccountTitleError(article);
            article.PageRawTitle = ""; article.PageTitleSource = "";
            ApplyPageTitle(article, chosenTitle.Raw, chosenTitle.Source, repairAccountTitle);
            // An unrelated rejected candidate must not become the evidence used to unlock
            // this narrowly scoped correction on a subsequent cache-maintenance pass.
            if (preserveRepairEvidence && !repairAccountTitle && NormalizeTitle(article.Title) == NormalizeTitle(article.AccountName))
            { article.PageRawTitle = priorPageRaw; article.PageTitleSource = priorPageSource; }

            // A precise article ct outranks a formatted create_time or publish_time. Each fallback
            // is validated separately; a library's local ct is never a candidate.
            if (TryUnix(Unique(fields, "ct"), out long seconds, out DateTime date)) ApplyDate(article, date, "page:ct", seconds);
            else if (TryUnix(Unique(fields, "create_time"), out seconds, out date)) ApplyDate(article, date, "page:create_time", seconds);
            else if (TryDisplayDate(Meta(document, "article:published_time"), out date, out bool precise))
                ApplyDate(article, date, precise ? "meta:published:seconds" : "meta:published:minutes", ToUnixSeconds(date));
            else if (TryDisplayDate(Unique(fields, "publish_time"), out date, out precise))
                ApplyDate(article, date, precise ? "page:published:seconds" : "page:published:minutes", ToUnixSeconds(date));
            else if (TryDisplayDate(document.GetElementbyId("publish_time")?.InnerText, out date, out precise))
                ApplyDate(article, date, precise ? "dom:published:seconds" : "dom:published:minutes", ToUnixSeconds(date));

            // References require a dedicated original-article card inside an explicitly short/share
            // page. Ordinary body links and a generic original_url variable do not establish one.
            if (article.ContentKind == ArticleContentKind.ShortPost)
            {
                var cards = document.DocumentNode.Descendants().Where(n => n.GetAttributeValue("id", "") == "js_share_source"
                    || n.GetAttributeValue("id", "") == "js_share_source_link" || n.GetAttributeValue("id", "") == "js_share_original_article")
                    .Where(n => !n.AncestorsAndSelf().Any(parent => parent == shortBody || parent == body
                        || parent.GetAttributeValue("id", "") == "js_comment" || parent.GetAttributeValue("id", "") == "js_cmt_area"
                        || parent.Name == "script" || parent.Name == "style" || parent.Name == "template" || parent.Name == "noscript"));
                var links = cards.SelectMany(n => n.DescendantsAndSelf()).Where(n => n.Name == "a")
                    .Select(n => CanonicalReference(article, n.GetAttributeValue("href", "")))
                    .Where(url => url.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                if (links.Length == 1) SetReference(article, links[0], "page:share-original-card");
            }
            article.MetadataVersion = CurrentVersion;
            return before != Fingerprint(article);
        }

        public static bool ApplyJson(ArticleRecord article, JObject json)
        {
            if (article == null) throw new ArgumentNullException(nameof(article));
            if (json == null) return false;
            var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string key in new[] { "biz", "__biz", "mid", "appmsgid", "idx" })
                if (json[key] is JValue value) fields[key] = new List<string> { value.ToString() };
            if (!MatchesPage(article, fields)) return false;
            string before = Fingerprint(article);
            FillMissingIdentity(article, fields);
            article.PageRawTitle = ""; article.PageTitleSource = "";
            ApplyPageTitle(article, Scalar(json["title"]), "json:title");
            if (TryUnix(Scalar(json["ct"]), out long seconds, out DateTime date)) ApplyDate(article, date, "page:ct", seconds);
            else if (TryUnix(Scalar(json["create_time"]), out seconds, out date)) ApplyDate(article, date, "json:create_time", seconds);
            // Generic title/content/desc JSON occurs in several message formats. It does not
            // establish a short-post or reference type, and never supplies a synthesized title.
            if (article.ContentKind == ArticleContentKind.Unknown && (Scalar(json["share_text"]).Length > 0
                || Scalar(json["share_content"]).Length > 0)) article.ContentKind = ArticleContentKind.ShortPost;
            article.MetadataVersion = CurrentVersion;
            return before != Fingerprint(article);
        }

        public static bool SetReference(ArticleRecord article, string url, string evidenceSource)
        {
            if (article == null) throw new ArgumentNullException(nameof(article));
            if (string.IsNullOrWhiteSpace(evidenceSource)) return false;
            string canonical = CanonicalReference(article, url);
            if (canonical.Length == 0) return false;
            string id = ArticleIdentity.Create("", "", 1, canonical);
            if (!string.IsNullOrEmpty(article.ReferencedArticleId) && article.ReferencedArticleId != id) return false;
            article.ReferencedArticleId = id;
            article.ReferencedArticleUrl = canonical;
            article.ReferenceSource = evidenceSource;
            article.ContentKind = ArticleContentKind.ShareReference;
            return true;
        }

        public static void Merge(ArticleRecord incoming, ArticleRecord saved)
        {
            if (incoming == null || saved == null || incoming.Biz != saved.Biz || incoming.Id != saved.Id) return;
            if (!MergeResolvedIdentity(incoming, saved)) return;
            // Apply the cached evidence to the new metadata only; collection status/media remain
            // the caller's responsibility and a new request still refreshes the article body.
            string originalTitle = incoming.Title, originalSource = incoming.TitleSource, originalRaw = incoming.RawTitle;
            incoming.Title = saved.Title; incoming.TitleSource = saved.TitleSource; incoming.RawTitle = saved.RawTitle;
            bool repairAccountTitle = CanRepairCachedAccountTitle(saved, originalTitle, originalSource);
            ApplyTitle(incoming, originalTitle, string.IsNullOrEmpty(originalSource) ? "legacy" : originalSource, originalRaw, repairAccountTitle);
            if (IsCachedAccountTitleError(saved) && !repairAccountTitle && incoming.Title == saved.Title)
            { incoming.PageRawTitle = saved.PageRawTitle; incoming.PageTitleSource = saved.PageTitleSource; }
            DateTime? originalDate = incoming.PublishedAt; string originalDateSource = incoming.PublishedAtSource;
            long? originalSeconds = incoming.PublishedUnixSeconds;
            incoming.PublishedAt = saved.PublishedAt; incoming.PublishedAtSource = saved.PublishedAtSource;
            incoming.PublishedUnixSeconds = saved.PublishedUnixSeconds;
            if (originalDate.HasValue) ApplyDate(incoming, originalDate.Value, originalDateSource, originalSeconds);
            if (string.IsNullOrEmpty(incoming.HistoryTitle)) incoming.HistoryTitle = saved.HistoryTitle;
            if (string.IsNullOrEmpty(incoming.PageRawTitle)) { incoming.PageRawTitle = saved.PageRawTitle; incoming.PageTitleSource = saved.PageTitleSource; }
            if (!incoming.HistoryPublishedAt.HasValue) incoming.HistoryPublishedAt = saved.HistoryPublishedAt;
            incoming.HistoryItemShowType ??= saved.HistoryItemShowType;
            incoming.HistoryMessageType ??= saved.HistoryMessageType;
            if (incoming.ContentKind == ArticleContentKind.Unknown) incoming.ContentKind = saved.ContentKind;
            if (string.IsNullOrEmpty(incoming.ReferencedArticleId) && !string.IsNullOrEmpty(saved.ReferencedArticleId)
                && !string.IsNullOrEmpty(saved.ReferenceSource))
                SetReference(incoming, saved.ReferencedArticleUrl, saved.ReferenceSource);
            incoming.MetadataVersion = Math.Max(incoming.MetadataVersion, saved.MetadataVersion);
        }

        static void ApplyPageTitle(ArticleRecord article, string raw, string source, bool repairAccountTitle = false)
        {
            if (NormalizeTitle(raw).Length == 0) return;
            article.PageRawTitle = raw; article.PageTitleSource = source;
            ApplyTitle(article, raw, source, allowAccountTitleCorrection: repairAccountTitle);
        }
        static bool IsCachedAccountTitleError(ArticleRecord article)
            => article.TitleSource == "script:title" && article.PageTitleSource == "heading"
                && NormalizeTitle(article.AccountName).Length > 0
                && NormalizeTitle(article.Title) == NormalizeTitle(article.AccountName)
                && NormalizeTitle(article.PageRawTitle).Length > 0
                && NormalizeTitle(article.PageRawTitle) != NormalizeTitle(article.Title);
        static bool CanRepairCachedAccountTitle(ArticleRecord article, string raw, string source)
            => source == "heading" && IsCachedAccountTitleError(article)
                && NormalizeTitle(raw) == NormalizeTitle(article.PageRawTitle);
        static void ApplyTitle(ArticleRecord article, string raw, string source, string preservedRaw = null, bool allowAccountTitleCorrection = false)
        {
            string candidate = NormalizeTitle(raw), existing = NormalizeTitle(article.Title);
            if (candidate.Length == 0) return;
            int strength = TitleStrength(source), prior = TitleStrength(article.TitleSource);
            bool same = candidate == existing;
            bool extends = existing.Length > 0 && candidate.Length > existing.Length && IsSubsequence(WithoutEllipsis(existing), candidate);
            if (existing.Length > 0 && !same && !allowAccountTitleCorrection)
            {
                // A formal heading can correct a weak social-preview title. Existing history or
                // legacy titles need demonstrable completion, not just a different longer string.
                if (candidate.Length < existing.Length && prior >= 60) return;
                if (!extends && !(prior < 60 && strength >= 80)) return;
            }
            if (same && strength < prior) { article.Title = existing; return; }
            article.Title = candidate; article.TitleSource = source;
            article.RawTitle = string.IsNullOrEmpty(preservedRaw) ? raw ?? "" : preservedRaw;
        }

        static int TitleStrength(string source) => source == "heading" ? 100
            : source == "script:msg_title" || source == "script:title" ? 90 : source == "json:title" ? 80
            : source == "history" ? 70 : source == "native_profile_accessibility" ? 40
            : source?.StartsWith("meta:", StringComparison.Ordinal) == true ? 40 : 60;
        static string WithoutEllipsis(string text) => text.EndsWith("...", StringComparison.Ordinal) ? text.Substring(0, text.Length - 3).TrimEnd()
            : text.EndsWith("…", StringComparison.Ordinal) ? text.TrimEnd('…').TrimEnd() : text;
        static bool IsSubsequence(string smaller, string larger)
        {
            if (smaller.Length == 0) return false;
            int offset = 0;
            foreach (char c in larger) if (offset < smaller.Length && smaller[offset] == c) offset++;
            return offset == smaller.Length;
        }

        static void ApplyDate(ArticleRecord article, DateTime date, string source, long? seconds)
        {
            int strength = DateStrength(source), prior = DateStrength(article.PublishedAtSource);
            if (article.PublishedAt.HasValue && strength < prior) return;
            // Equal evidence cannot make a precise timestamp less precise on a refresh.
            if (article.PublishedAt.HasValue && strength == prior && article.PublishedAt.Value.Second != 0
                && date.Second == 0 && article.PublishedAt.Value.Date == date.Date && article.PublishedAt.Value.Hour == date.Hour
                && article.PublishedAt.Value.Minute == date.Minute) return;
            article.PublishedAt = date; article.PublishedAtSource = source ?? "";
            article.PublishedUnixSeconds = seconds;
        }
        static int DateStrength(string source) => source == "page:ct" ? 100 : source == "history" ? 90
            : source == "json:create_time" ? 80 : source == "page:create_time" ? 70
            : source?.EndsWith(":seconds", StringComparison.Ordinal) == true ? 60
            : source?.EndsWith(":minutes", StringComparison.Ordinal) == true ? 30 : 50;

        static bool TryUnix(string value, out long seconds, out DateTime date)
        {
            date = default; seconds = 0;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) return false;
            if (seconds < 946684800 || seconds > 7258118400L) return false;
            date = DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(TimeSpan.FromHours(8)).DateTime;
            return true;
        }
        static bool TryDisplayDate(string value, out DateTime date, out bool precise)
        {
            date = default; precise = false; string text = HttpUtility.HtmlDecode(value ?? "").Trim();
            if (!Regex.IsMatch(text, @"^\d{4}[-/]\d{1,2}[-/]\d{1,2}(?:[ T]\d{1,2}:\d{2}(?::\d{2})?(?:[ Zz+\-].*)?)?$", RegexOptions.None, RegexTimeout)) return false;
            precise = Regex.IsMatch(text, @"[ T]\d{1,2}:\d{2}:\d{2}", RegexOptions.None, RegexTimeout);
            if (Regex.IsMatch(text, @"(?:[Zz]|[+\-]\d{2}:?\d{2})$", RegexOptions.None, RegexTimeout)
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var offset))
            { date = offset.ToOffset(TimeSpan.FromHours(8)).DateTime; return true; }
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
        }
        static long ToUnixSeconds(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeSeconds();

        static bool MatchesPage(ArticleRecord article, Dictionary<string, List<string>> fields)
        {
            var accounts = IdentityValues(fields, "biz", "__biz");
            var messages = IdentityValues(fields, "mid", "appmsgid");
            if (accounts.Length > 1 || messages.Length > 1) return false;
            if (accounts.Length == 1 && !string.IsNullOrWhiteSpace(article.Biz) && accounts[0] != article.Biz) return false;
            if (messages.Length == 1 && !string.IsNullOrWhiteSpace(article.Mid) && messages[0] != article.Mid) return false;
            var indices = IdentityValues(fields, "idx");
            if (indices.Length > 1 || indices.Any(v => !int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) || idx < 1)) return false;
            if (indices.Length == 1 && int.TryParse(indices[0], out int pageIdx) && pageIdx != article.Idx)
            {
                // An unresolved short link may only have the default/history fallback child index.
                // Explicit URL or previously resolved evidence is required before treating it as fixed.
                if (!article.IdentityUnresolved || !string.IsNullOrEmpty(article.ResolvedArticleId)) return false;
                if (Uri.TryCreate(article.Url, UriKind.Absolute, out var uri))
                {
                    string urlIndex = HttpUtility.ParseQueryString(uri.Query)["idx"] ?? "";
                    if (urlIndex.Length > 0 && (!int.TryParse(urlIndex, out int urlIdx) || urlIdx != pageIdx)) return false;
                }
            }
            return true;
        }
        static void FillMissingIdentity(ArticleRecord article, Dictionary<string, List<string>> fields)
        {
            var accounts = IdentityValues(fields, "biz", "__biz");
            var messages = IdentityValues(fields, "mid", "appmsgid");
            if (string.IsNullOrWhiteSpace(article.Biz) && accounts.Length == 1) article.Biz = accounts[0];
            if (string.IsNullOrWhiteSpace(article.Mid) && messages.Length == 1
                && Regex.IsMatch(messages[0], @"^\d+$", RegexOptions.None, RegexTimeout)) article.Mid = messages[0];
            // A mid alone does not prove which child article a short link addresses. The resolved
            // alias needs an explicit page idx or the idx explicitly present in the request URL.
            string pageIndex = Unique(fields, "idx");
            var query = Uri.TryCreate(article.Url, UriKind.Absolute, out var uri) ? HttpUtility.ParseQueryString(uri.Query) : null;
            string urlIndex = query?["idx"] ?? "";
            if (messages.Length == 1 && messages[0] == article.Mid && !string.IsNullOrWhiteSpace(article.Biz)
                && Regex.IsMatch(messages[0], @"^\d+$", RegexOptions.None, RegexTimeout)
                && int.TryParse(pageIndex.Length > 0 ? pageIndex : urlIndex, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) && idx > 0
                && (urlIndex.Length == 0 || int.TryParse(urlIndex, NumberStyles.None, CultureInfo.InvariantCulture, out int urlIdx) && urlIdx == idx)
                && (string.IsNullOrEmpty(query?["__biz"]) || query["__biz"] == article.Biz)
                && (string.IsNullOrEmpty(query?["mid"] ?? query?["appmsgid"]) || (query["mid"] ?? query["appmsgid"]) == messages[0]))
            {
                string resolved = ArticleIdentity.Create(article.Biz, messages[0], idx, "");
                if (string.IsNullOrEmpty(article.ResolvedArticleId) || article.ResolvedArticleId == resolved)
                { article.ResolvedArticleId = resolved; article.Idx = idx; article.IdentityUnresolved = false; }
            }
            else if (string.IsNullOrWhiteSpace(article.Biz) || string.IsNullOrWhiteSpace(article.Mid)) article.IdentityUnresolved = true;
        }
        static bool MergeResolvedIdentity(ArticleRecord incoming, ArticleRecord saved)
        {
            bool incomingProof = TryResolvedIdentity(incoming, out string incomingMid, out int incomingIdx);
            bool savedProof = TryResolvedIdentity(saved, out string savedMid, out int savedIdx);
            if (incomingProof && savedProof && incoming.ResolvedArticleId != saved.ResolvedArticleId) return false;
            if (!incomingProof && !string.IsNullOrEmpty(incoming.ResolvedArticleId))
            { incoming.ResolvedArticleId = ""; incoming.IdentityUnresolved = true; return false; }
            if (savedProof && !incomingProof)
            {
                if (!string.IsNullOrWhiteSpace(incoming.Mid) && incoming.Mid != savedMid) return false;
                if (Uri.TryCreate(incoming.Url, UriKind.Absolute, out var uri))
                {
                    var query = HttpUtility.ParseQueryString(uri.Query);
                    string urlMid = query["mid"] ?? query["appmsgid"] ?? "", urlIndex = query["idx"] ?? "";
                    if ((urlMid.Length > 0 && urlMid != savedMid) || (urlIndex.Length > 0 && (!int.TryParse(urlIndex, out int idx) || idx != savedIdx))
                        || (!string.IsNullOrEmpty(query["__biz"]) && query["__biz"] != incoming.Biz)) return false;
                }
                incoming.ResolvedArticleId = saved.ResolvedArticleId;
                incoming.Mid = savedMid; incoming.Idx = savedIdx; incoming.IdentityUnresolved = false;
            }
            else if (incomingProof) { incoming.Mid = incomingMid; incoming.Idx = incomingIdx; incoming.IdentityUnresolved = false; }
            return true;
        }
        static bool TryResolvedIdentity(ArticleRecord article, out string mid, out int idx)
        {
            mid = ""; idx = 0;
            string prefix = (article.Biz ?? "") + ":", id = article.ResolvedArticleId ?? "";
            if (string.IsNullOrWhiteSpace(article.Biz) || !id.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string[] parts = id.Substring(prefix.Length).Split(':');
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], @"^\d+$", RegexOptions.None, RegexTimeout)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out idx) || idx < 1) return false;
            mid = parts[0];
            return (string.IsNullOrWhiteSpace(article.Mid) || article.Mid == mid) && article.Idx == idx;
        }
        static string[] IdentityValues(Dictionary<string, List<string>> fields, params string[] keys)
            => keys.Where(fields.ContainsKey).SelectMany(key => fields[key]).Select(value => HttpUtility.HtmlDecode(value).Trim())
                .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        internal static string Unique(Dictionary<string, List<string>> fields, string key)
        {
            if (!fields.TryGetValue(key, out var values)) return "";
            var distinct = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            return distinct.Length == 1 ? distinct[0] : "";
        }
        static string CanonicalReference(ArticleRecord article, string raw)
        {
            string text = HttpUtility.HtmlDecode(HttpUtility.HtmlDecode(raw ?? "")).Trim();
            if (text.StartsWith("//", StringComparison.Ordinal)) text = "https:" + text;
            if (text.StartsWith("/", StringComparison.Ordinal)) text = "https://mp.weixin.qq.com" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")
                || !uri.Host.Equals("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0 || !uri.IsDefaultPort
                || !(uri.AbsolutePath == "/s" || uri.AbsolutePath == "/mp/appmsg/show")) return "";
            var query = HttpUtility.ParseQueryString(uri.Query);
            string biz = query["__biz"] ?? "", mid = query["mid"] ?? query["appmsgid"] ?? "";
            if (biz.Length == 0 || article.Biz != biz || !Regex.IsMatch(mid, @"^\d+$", RegexOptions.None, RegexTimeout)
                || !int.TryParse(query["idx"], NumberStyles.None, CultureInfo.InvariantCulture, out int idx) || idx < 1) return "";
            string id = ArticleIdentity.Create(biz, mid, idx, "");
            if (id == article.Id || (mid == article.Mid && idx == article.Idx)) return "";
            return "https://mp.weixin.qq.com/s?__biz=" + Uri.EscapeDataString(biz) + "&mid=" + mid + "&idx=" + idx.ToString(CultureInfo.InvariantCulture);
        }
        static string Scalar(JToken value) => value is JValue && value.Type != JTokenType.Null ? value.ToString() : "";
        static string Meta(HtmlDocument doc, string name) => doc.DocumentNode.Descendants("meta").FirstOrDefault(n => n.GetAttributeValue("property", "").Equals(name, StringComparison.OrdinalIgnoreCase)
            || n.GetAttributeValue("name", "").Equals(name, StringComparison.OrdinalIgnoreCase))?.GetAttributeValue("content", "") ?? "";
        static bool HasClass(HtmlNode node, string value) => (" " + node.GetAttributeValue("class", "") + " ").Contains(" " + value + " ");
        static bool HasContent(HtmlNode node) => node != null && (!string.IsNullOrWhiteSpace(node.InnerText)
            || node.Descendants().Any(n => n.Name == "img" || n.Name == "video" || n.Name == "mpvideo" || n.Name == "audio" || n.Name == "mpvoice"));
        static string Fingerprint(ArticleRecord article) => JsonConvert.SerializeObject(new { article.Biz, article.Mid, article.Idx, article.IdentityUnresolved, article.ResolvedArticleId,
            article.Title, article.RawTitle, article.TitleSource, article.PageRawTitle, article.PageTitleSource,
            article.PublishedAt, article.PublishedAtSource, article.PublishedUnixSeconds, article.ContentKind, article.ReferencedArticleId,
            article.ReferencedArticleUrl, article.ReferenceSource, article.MetadataVersion });
    }
}
