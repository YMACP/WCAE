using System;

namespace WCAE
{
    // Text and ordinal fields are navigation hints only. ArticleId is obtained from an
    // opened article URL or an exact full-text match to its published history URL;
    // a restored hint must be checked against that identity and its occurrence context.
    public sealed class NativeArticleAnchor
    {
        public string ArticleId { get; set; } = "";
        public string CanonicalId { get; set; } = "";
        // A local text snapshot has no published canonical identity. Its fingerprint
        // checks visible content only; navigation context identifies the occurrence.
        public string NativeTextFingerprint { get; set; } = "";
        public string NativeDateLabel { get; set; } = "";
        public string Title { get; set; } = "";
        public string PreviousTitle { get; set; } = "";
        public string NextTitle { get; set; } = "";
        public int OrdinalHint { get; set; } = -1;
        public string Section { get; set; } = "";
        public int SectionOrdinal { get; set; } = -1;
        public bool RequiresArticleNavigation { get; set; } = true;
        public bool VerifyDeletionOnly { get; set; }
    }

    public sealed class NativeCollectionState
    {
        public string Biz { get; set; } = "";
        public string UserName { get; set; } = "";
        public string RunId { get; set; } = "";
        public string Phase { get; set; } = "Discovering";
        public string CurrentArticleId { get; set; } = "";
        public NativeArticleAnchor CurrentAnchor { get; set; }
        public NativeArticleAnchor LastAnchor { get; set; }
        public bool EndConfirmed { get; set; }
        public int CompletedCount { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string LastError { get; set; } = "";
    }
}
