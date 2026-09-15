using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    // This is an independently observed occurrence, never a substitute for a WeChat
    // article ID. Identical text may have been published more than once.
    public static class NativeProfileTextSnapshot
    {
        const string Prefix = "native-profile:";
        public static ArticleRecord Create(AccountSession session, string title, string fullText, string dateLabel,
            long? readCount = null, long? likeCount = null)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Biz))
                throw new InvalidOperationException("主页正文缺少公众号标识。");
            var article = new ArticleRecord
            {
                Id = Prefix + session.Biz + ":" + Guid.NewGuid().ToString("N"), Biz = session.Biz,
                AccountName = session.Name ?? "", Title = ArticleMetadata.NormalizeTitle(title),
                NativeTextContent = NormalizeLines(fullText), NativeDateLabel = dateLabel ?? "",
                NativeTextFingerprint = Fingerprint(session.Biz, fullText), IdentityUnresolved = true,
                ContentKind = ArticleContentKind.ShortPost, Status = ArticleStatus.Pending,
                TitleSource = "native_profile_accessibility", RawTitle = fullText ?? "",
                ReadCount = readCount, LikeCount = likeCount, MetadataVersion = ArticleMetadata.CurrentVersion,
                StatusDetail = "来源：原生主页正文；未取得独立原文链接，发布时间及未显示的数据未知。"
            };
            Validate(article);
            article.Html = BuildHtml(article);
            return article;
        }

        // Recognize any claimed local snapshot so malformed claims cannot fall back
        // into the normal HTTP/canonical path without validation.
        public static bool IsSnapshot(ArticleRecord article) => article != null
            && ((article.Id ?? "").StartsWith(Prefix, StringComparison.Ordinal)
                || !string.IsNullOrEmpty(article.NativeTextFingerprint) || !string.IsNullOrEmpty(article.NativeTextContent)
                || article.Status == ArticleStatus.LocalSnapshot);

        public static bool MatchesFingerprint(string biz, string fullText, string expected)
            => !string.IsNullOrWhiteSpace(biz) && !string.IsNullOrWhiteSpace(fullText)
                && !string.IsNullOrEmpty(expected) && expected == Fingerprint(biz, fullText);

        public static void Validate(ArticleRecord article)
        {
            string prefix = Prefix + (article?.Biz ?? "") + ":";
            if (article == null || string.IsNullOrWhiteSpace(article.Biz)
                || !(article.Id ?? "").StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(article.Id.Substring(prefix.Length), "N", out var _)
                || string.IsNullOrWhiteSpace(article.Title)
                || !MatchesFingerprint(article.Biz, article.NativeTextContent, article.NativeTextFingerprint)
                || !string.IsNullOrEmpty(article.Url) || !string.IsNullOrEmpty(article.Mid)
                || !string.IsNullOrEmpty(article.ResolvedArticleId) || !string.IsNullOrEmpty(article.SourceMessageId)
                || article.PublishedAt.HasValue || article.PublishedUnixSeconds.HasValue || article.HistoryPublishedAt.HasValue
                || !string.IsNullOrEmpty(article.PublishedAtSource)
                || article.ContentKind != ArticleContentKind.ShortPost || !article.IdentityUnresolved
                || (article.Status != ArticleStatus.Pending && article.Status != ArticleStatus.LocalSnapshot)
                || !string.IsNullOrEmpty(article.ReferencedArticleId) || !string.IsNullOrEmpty(article.ReferencedArticleUrl)
                || article.ReadCount < 0 || article.LikeCount < 0)
                throw new InvalidOperationException("主页正文的本地标识、全文证据或未知字段不一致，已保留当前项。");
        }

        public static ArticleRecord Finalize(ArticleRecord article)
        {
            Validate(article);
            var copy = JObject.FromObject(article).ToObject<ArticleRecord>();
            copy.Status = ArticleStatus.LocalSnapshot;
            copy.StatusDetail = "来源：原生主页正文；未取得独立原文链接，发布时间及未显示的数据未知。";
            // Metadata survives body separation and stop/restart. Render only the
            // persisted, fingerprint-checked plain text; never trust supplied HTML.
            copy.Html = BuildHtml(copy);
            return copy;
        }

        static string NormalizeLines(string text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        static string Fingerprint(string biz, string fullText)
        {
            string decoded = HttpUtility.HtmlDecode(fullText ?? "");
            string normalized = new string(decoded.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (normalized.Length == 0) return "";
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(biz + "\n" + normalized)))
                .Replace("-", "").ToLowerInvariant();
        }
        static string BuildHtml(ArticleRecord article)
        {
            var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>");
            html.Append(HttpUtility.HtmlEncode(article.Title));
            html.Append("</title></head><body><p>来源：原生主页正文；未取得独立原文链接。</p><div id=\"wcae_body\">");
            foreach (string paragraph in NormalizeLines(article.NativeTextContent).Split('\n'))
                if (!string.IsNullOrWhiteSpace(paragraph)) html.Append("<p>").Append(HttpUtility.HtmlEncode(paragraph)).Append("</p>");
            return html.Append("</div></body></html>").ToString();
        }
    }
}
