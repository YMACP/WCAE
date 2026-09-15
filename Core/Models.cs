using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace WCAE
{
    public enum ArticleStatus { Pending, Available, Deleted, Restricted, Failed, LocalSnapshot }
    public enum ArticleContentKind { Unknown, Article, ShortPost, ShareReference }
    public enum MediaKind { Image, Audio, Video }
    public enum ExportFormat { Html, Markdown, Text, Pdf, Images, Audio, Video, AllMedia, Full }
    public sealed class ColumnInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }
    public sealed class MediaAsset
    {
        public MediaKind Kind { get; set; }
        public string Url { get; set; } = "";
        public string Name { get; set; } = "";
        public string Id { get; set; } = "";
        public bool RequiresExtractor { get; set; }
        public string UnavailableReason { get; set; } = "";
    }
    public sealed class ArticleRecord
    {
        public string Id { get; set; } = "";
        public string Biz { get; set; } = "";
        public string AccountName { get; set; } = "";
        public string Mid { get; set; } = "";
        public string SourceMessageId { get; set; } = "";
        public bool IdentityUnresolved { get; set; }
        public string ResolvedArticleId { get; set; } = "";
        public string NativeTextFingerprint { get; set; } = "";
        public string NativeTextContent { get; set; } = "";
        public string NativeDateLabel { get; set; } = "";
        public int Idx { get; set; } = 1;
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public ArticleContentKind ContentKind { get; set; }
        public string HistoryTitle { get; set; } = "";
        public DateTime? HistoryPublishedAt { get; set; }
        public int? HistoryItemShowType { get; set; }
        public int? HistoryMessageType { get; set; }
        public string RawTitle { get; set; } = "";
        public string TitleSource { get; set; } = "";
        public string PageRawTitle { get; set; } = "";
        public string PageTitleSource { get; set; } = "";
        public string PublishedAtSource { get; set; } = "";
        public long? PublishedUnixSeconds { get; set; }
        public string ReferencedArticleUrl { get; set; } = "";
        public string ReferencedArticleId { get; set; } = "";
        public string ReferenceSource { get; set; } = "";
        public int MetadataVersion { get; set; }
        public int AudioMetadataVersion { get; set; }
        public int ColumnMetadataVersion { get; set; }
        public string Author { get; set; } = "";
        public string Digest { get; set; } = "";
        public DateTime? PublishedAt { get; set; }
        public long? ReadCount { get; set; }
        public long? LikeCount { get; set; }
        public long? FavoriteCount { get; set; }
        public long? ShareCount { get; set; }
        public ArticleStatus Status { get; set; } = ArticleStatus.Pending;
        public string StatusDetail { get; set; } = "";
        public List<ColumnInfo> Columns { get; set; } = new List<ColumnInfo>();
        public List<MediaAsset> Media { get; set; } = new List<MediaAsset>();
        public string Html { get; set; } = "";
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public string ColumnNames => string.Join("、", Columns.Select(x => x.Name).Distinct());
        public string StatusText => Status == ArticleStatus.Available ? "可访问" : Status == ArticleStatus.Deleted ? "已删除" : Status == ArticleStatus.Restricted ? "访问受限" : Status == ArticleStatus.Failed ? "采集失败" : Status == ArticleStatus.LocalSnapshot ? "主页正文" : "待检查";
    }
    public sealed class AccountSession
    {
        public string Biz { get; set; } = "";
        public string Name { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Cookie { get; set; } = "";
        public string UserAgent { get; set; } = "Mozilla/5.0";
        public string RequestUrl { get; set; } = "";
        public string Uin { get; set; } = "";
        public string Key { get; set; } = "";
        public string PassTicket { get; set; } = "";
        public string AppMsgToken { get; set; } = "";
        public DateTime CapturedAt { get; set; } = DateTime.Now;
        public Dictionary<string,string> Headers { get; set; } = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        public AccountSession Clone() { var s = (AccountSession)MemberwiseClone(); s.Headers = new Dictionary<string,string>(Headers, StringComparer.OrdinalIgnoreCase); return s; }
    }
    public sealed class CollectionProgress
    {
        public int Pages { get; set; }
        public int Articles { get; set; }
        public string Message { get; set; } = "";
    }
    public sealed class ExportOptions
    {
        public string Destination { get; set; } = "";
        public ExportFormat Format { get; set; }
        public bool DownloadImages { get; set; } = true;
        public bool Overwrite { get; set; }
    }
    public sealed class ExportProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public int Failed { get; set; }
        public string Message { get; set; } = "";
    }
    public interface IHttpTransport
    {
        Task<string> GetStringAsync(string url, AccountSession session, CancellationToken token);
        Task<string> PostFormAsync(string url, IDictionary<string,string> values, AccountSession session, CancellationToken token);
        Task DownloadAsync(string url, string destination, AccountSession session, CancellationToken token);
    }
    public static class ArticleIdentity
    {
        static readonly HashSet<string> SessionParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "uin", "key", "pass_ticket", "appmsg_token", "wxtoken", "scene", "ascene", "subscene", "clicktime", "enterid", "sessionid", "from", "isappinstalled" };
        public static string Create(string biz, string mid, int idx, string url, string sourceMessageId = "")
        {
            if (!string.IsNullOrWhiteSpace(biz) && !string.IsNullOrWhiteSpace(mid)) return biz + ":" + mid + ":" + idx;
            Uri u; string normalized = ""; string account = biz ?? "";
            if (Uri.TryCreate(url ?? "", UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https"))
            {
                var q = HttpUtility.ParseQueryString(u.Query);
                var b = q["__biz"] ?? biz; var m = q["mid"] ?? q["appmsgid"];
                int i; if (!int.TryParse(q["idx"], out i)) i = idx;
                if (!string.IsNullOrEmpty(b) && !string.IsNullOrEmpty(m)) return b + ":" + m + ":" + i;
                account = string.IsNullOrWhiteSpace(b) ? account : b;
                var keys = q.AllKeys.Where(k => k != null && !SessionParameters.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
                bool genericPath = u.AbsolutePath == "/" || (u.Host.Equals("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase)
                    && (u.AbsolutePath == "/s" || u.AbsolutePath == "/mp/appmsg/show"));
                bool informativeQuery = keys.Any(k => k != "__biz" && k != "idx" && !string.IsNullOrWhiteSpace(q[k]));
                if (!genericPath || informativeQuery)
                {
                    // Unknown query fields may be the only article discriminator. Keep them rather
                    // than guessing equivalence; only explicit session/tracking fields are removed.
                    string query = string.Join("&", keys.SelectMany(k => (q.GetValues(k) ?? new[] { "" })
                        .Select(value => Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(value ?? ""))));
                    normalized = account + "\n" + u.GetLeftPart(UriPartial.Path) + (query.Length == 0 ? "" : "?" + query);
                }
            }
            if (normalized.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(account) && !string.IsNullOrWhiteSpace(sourceMessageId))
                    return "message:" + Uri.EscapeDataString(account) + ":" + Uri.EscapeDataString(sourceMessageId) + ":" + idx;
                throw new ArgumentException("文章链接缺少稳定标识，且未提供源消息标识，已停止以避免错误合并。");
            }
            using (var sha = SHA256.Create()) return "url:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "").ToLowerInvariant();
        }
    }
    public static class AppPaths
    {
        public static string DataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WCAE");
        public static string ToolsDirectory => BundledTools.DirectoryPath;
    }
}
