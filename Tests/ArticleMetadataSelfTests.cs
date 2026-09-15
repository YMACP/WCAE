using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    // Pure synthetic HTML/JSON and in-memory records. No cache, network, UI or external tools.
    public static class ArticleMetadataSelfTests
    {
        public static List<string> Run()
        {
            var passed = new List<string>();
            PreciseDates(); passed.Add("top-level ct / local-library exclusion / second precision / independent fallbacks");
            Titles(); passed.Add("complete title / missing-middle and emoji protection / entities and JS quotes");
            NativePageTitleSelection(); passed.Add("native UI title / authoritative page candidate selected before generic account title");
            CachedAccountTitleRepair(); passed.Add("known account-title corruption repair / save-merge retention / unrelated title protection");
            ContentAndReferences(); passed.Add("article vs short post / explicit same-account references / legacy wrapper remains unknown");
            MergeEvidence(); passed.Add("metadata refresh merge / source retention / no status or identity contamination");
            UnresolvedIdentities(); passed.Add("unresolved URL identity / meaningful query retention / source fallback / account isolation");
            ResolvedPageIdentity(); passed.Add("short-link identity from explicit page fields / local variable exclusion / immutable storage key");
            return passed;
        }

        static void PreciseDates()
        {
            var article = New("123");
            ArticleMetadata.ApplyHistory(article, "时间测试", new DateTime(2023, 11, 15, 6, 13, 0));
            Check(article.MetadataVersion == 0, "history alone must not mark an old cached body as migrated");
            string html = "<div id='js_content'>正文</div><script>"
                + "var library={ct:'1500000000'};function helper(){var ct='1500000001';}"
                + "var fake=\"var ct='1500000002';\";/* var ct='1500000003'; */"
                + "var template=`var ct='1500000004';`;var regex=/ct='1500000005'/;"
                + "var create_time='2023-11-15 06:13';var ct='1700000000';</script>";
            ArticleEnricher.ParseHtml(article, html);
            Check(article.PublishedAt == new DateTime(2023, 11, 15, 6, 13, 20) && article.PublishedUnixSeconds == 1700000000
                && article.PublishedAtSource == "page:ct", "only the actual page timestamp restores the lost seconds");
            ArticleMetadata.ApplyHtml(article, "<div id='js_content'>正文</div><script>var create_time='2023-11-15 06:13';</script><span id='publish_time'>2023-11-15 06:13</span>");
            Check(article.PublishedAt.Value.Second == 20, "a formatted minute field must not overwrite established seconds");
            article = New("123");
            ArticleMetadata.ApplyHtml(article, "<div id='js_content'>正文</div><script>window.cgiData={ct:1700000000,comment:{ct:1500000000}};</script>");
            Check(article.PublishedAt == new DateTime(2023, 11, 15, 6, 13, 20), "named page-data object direct fields are allowed, nested comments are excluded");
            article = New("123");
            ArticleMetadata.ApplyHistory(article, "时间测试", new DateTime(2024, 2, 3, 4, 5, 6));
            ArticleMetadata.ApplyHtml(article, "<script>var ct=1500000000;var ct=1700000000;</script><div id='js_content'>正文</div>");
            Check(article.PublishedAt == new DateTime(2024, 2, 3, 4, 5, 6), "conflicting top-level timestamps cannot silently pick the first");
            article = New("123");
            ArticleMetadata.ApplyHtml(article, "<script>var ct='1700000000'+dynamicValue;var create_time=1700000001;</script><div id='js_content'>正文</div>");
            Check(article.PublishedUnixSeconds == 1700000001, "invalid expression prefixes do not block an independently valid fallback");
            article = New("123");
            ArticleMetadata.ApplyHtml(article, "<script>var biz='other';var mid='123';var ct=1700000000;</script><h1 id='activity-name'>另一公众号</h1><div id='js_content'>正文</div>");
            Check(article.PublishedAt == null && article.Title == "" && article.ContentKind == ArticleContentKind.Unknown, "another account's cached HTML cannot donate title, timestamp or type");
            ArticleMetadata.ApplyHtml(article, "<script>var biz='test';var mid='999';var ct=1700000000;</script><h1 id='activity-name'>另一文章</h1><div id='js_content'>正文</div>");
            Check(article.PublishedAt == null && article.Title == "", "another message in the same account cannot donate metadata");
        }

        static void Titles()
        {
            var article = New("123");
            ArticleMetadata.ApplyHistory(article, "从一封邮件到域控：把20种打法串成一条时间线", null);
            ArticleEnricher.ParseHtml(article, "<meta property='og:title' content='从一封邮件到域控：把20种打法串成一条时间'><div id='js_share_content'>分享摘要</div>");
            Check(article.Title.EndsWith("时间线", StringComparison.Ordinal) && article.PageRawTitle.EndsWith("时间", StringComparison.Ordinal), "a shortened social-preview title cannot erase the complete history title; rejected evidence is retained");
            article = New("123");
            ArticleMetadata.ApplyHistory(article, "安全🔐研究：加密密钥为何泄露", null);
            ArticleEnricher.ParseHtml(article, "<h1 id='activity-name'>安全研究：密钥为何泄露</h1><div id='js_content'>正文</div>");
            Check(article.Title == "安全🔐研究：加密密钥为何泄露" && article.PageTitleSource == "heading", "even a heading must not erase emoji and middle words from an established full title");
            ArticleMetadata.ApplyJson(article, JObject.Parse("{\"title\":\"安全研究：密钥为何泄露\",\"desc\":\"正文\"}"));
            Check(article.Title == "安全🔐研究：加密密钥为何泄露", "a JSON title cannot repeat the missing-middle overwrite");
            ArticleMetadata.ApplyHtml(article, "<h1 id='activity-name'>这是一条完全不同而且刻意加长以避免单纯长度比较的标题</h1><div id='js_content'>正文</div>");
            Check(article.Title == "安全🔐研究：加密密钥为何泄露", "an unrelated longer heading is not proof of completion");
            article = New("123");
            ArticleMetadata.ApplyHistory(article, "Next.js的密钥泄露", null);
            ArticleMetadata.ApplyHtml(article, "<h1 id='activity-name'>Next.js的加密密钥为何泄露</h1><div id='js_content'>正文</div>");
            Check(article.Title == "Next.js的加密密钥为何泄露", "a canonical heading may explicitly complete missing middle words");
            article = New("123");
            ArticleMetadata.ApplyHtml(article, "<meta property='og:title' content='A&amp;amp;B'><div id='js_content'>正文</div>");
            Check(article.Title == "A&B" && article.RawTitle == "A&amp;amp;B", "entity normalization preserves the raw source without showing double-encoded entities");
            article = New("123");
            ArticleMetadata.ApplyHtml(article, "<script>var msg_title='Reader\\'s \"quote\" \\u6807题';</script><div id='js_content'>正文</div>");
            Check(article.Title == "Reader's \"quote\" 标题", "a quoted JS title retains apostrophes, the other quote type and unicode escapes");
        }

        static void ContentAndReferences()
        {
            string original = "https://mp.weixin.qq.com/s?__biz=test&mid=100&idx=1&sn=abc";
            var article = New("123");
            ArticleMetadata.ApplyHistory(article, "短文标题", null);
            ArticleEnricher.ParseHtml(article, "<div id='js_share_content'>独立短文第一行<br>第二行完整内容<a href='" + original + "'>普通引用链接</a></div>");
            Check(article.ContentKind == ArticleContentKind.ShortPost && article.ReferencedArticleId == "" && article.Title == "短文标题"
                && article.Html.Contains("第二行完整内容"), "independent short text and an ordinary hyperlink are not a reference record or a generated whole-body title");
            article = New("124");
            ArticleEnricher.ParseHtml(article, "<div id='js_share_content'>转发说明</div><div id='js_share_source'><a href='" + original + "'>原文</a></div>");
            Check(article.ContentKind == ArticleContentKind.ShareReference && article.ReferencedArticleId == "test:100:1"
                && article.ReferenceSource.Length > 0, "a dedicated original-article card with a same-account canonical URL establishes a reference");
            foreach (string wrapper in new[] { "<div id='js_share_content'>正文{0}</div>", "<div id='js_share_content'>正文</div><template>{0}</template>",
                "<div id='js_share_content'>正文</div><div id='js_comment'>{0}</div>" })
            {
                var embedded = New("124");
                string fakeCard = "<div id='js_share_source'><a href='" + original + "'>正文中的仿卡片</a></div>";
                ArticleMetadata.ApplyHtml(embedded, string.Format(wrapper, fakeCard));
                Check(embedded.ContentKind == ArticleContentKind.ShortPost && embedded.ReferencedArticleId == "", "author body HTML, templates and comments cannot inject an original-article relationship");
            }
            article = New("125");
            ArticleMetadata.ApplyHtml(article, "<div id='js_share_content'>正文</div><script>var original_url='" + original + "';</script>");
            Check(article.ContentKind == ArticleContentKind.ShortPost && article.ReferencedArticleId == "", "an unscoped original_url variable has no proven sharing semantics");
            Check(!ArticleMetadata.SetReference(article, original.Replace("__biz=test", "__biz=other"), "test:card")
                && !ArticleMetadata.SetReference(article, "https://mp.weixin.qq.com/s?__biz=test&mid=125&idx=1", "test:card")
                && !ArticleMetadata.SetReference(article, "https://mp.weixin.qq.com/s/opaque-token", "test:card"), "foreign-account, self and identity-free URLs do not justify merging");
            article = New("126");
            ArticleMetadata.ApplyHtml(article, "<title>和另一篇文章相同的标题</title><div id='wcae_body'>旧版规范化短文内容</div>");
            Check(article.ContentKind == ArticleContentKind.Unknown && article.ReferencedArticleId == "" && article.Title == "", "an old wcae_body wrapper does not prove article type or original relationship");
            article = New("127");
            ArticleMetadata.ApplyJson(article, JObject.Parse("{\"title\":\"标题\",\"content_noencode\":\"<p>内容</p>\"}"));
            Check(article.ContentKind == ArticleContentKind.Unknown, "generic JSON content does not invent a short-post type");
            article = New("128");
            ArticleMetadata.ApplyHtml(article, "<div id='js_content'>正常图文</div><a href='" + original + "'>参考</a>");
            Check(article.ContentKind == ArticleContentKind.Article && article.ReferencedArticleId == "", "a normal article remains separate even when it cites another article");
        }

        const string NativeAccount = "字节笔记本";
        const string NativeTitle = "拿什么拯救你，我的 Codex 额度！";
        static string NativeTitleHtml(string mid="123",bool heading=true,bool messageTitle=true)
            => "<meta property='og:title' content='"+NativeTitle+"'><meta name='twitter:title' content='"+NativeTitle+"'>"
                + "<script>var biz='test';var mid='"+mid+"';var idx=1;var title='"+NativeAccount+"';"
                + (messageTitle?"var msg_title='"+NativeTitle+"';":"")+"</script>"
                + (heading?"<h1 id='activity-name'>\n"+NativeTitle+"</h1>":"")
                + "<a id='js_name'>"+NativeAccount+"</a><div id='js_content'>合成测试正文</div>";
        static ArticleRecord PollutedNativeTitle()
        {
            var article=New("123"); article.AccountName=NativeAccount;
            article.Title=article.RawTitle=NativeAccount; article.TitleSource="script:title";
            article.PageRawTitle="\n"+NativeTitle; article.PageTitleSource="heading";
            article.MetadataVersion=1; article.Status=ArticleStatus.Available;
            return article;
        }

        static void NativePageTitleSelection()
        {
            var article=New("123"); article.AccountName=NativeAccount;
            article.Title=article.RawTitle=NativeTitle+" 付费"; article.TitleSource="native_profile_accessibility";
            ArticleEnricher.ParseHtml(article,NativeTitleHtml());
            Check(article.Title==NativeTitle && article.TitleSource=="heading"
                && ArticleMetadata.NormalizeTitle(article.PageRawTitle)==NativeTitle && article.PageTitleSource=="heading"
                && article.AccountName==NativeAccount && article.Status==ArticleStatus.Available && article.Id=="test:123:1",
                "generic title=account must not poison the native card title before msg_title/heading is applied");
            article=New("123"); article.AccountName=NativeAccount; article.Title=NativeTitle+" 付费";
            article.TitleSource="native_profile_accessibility";
            ArticleMetadata.ApplyHtml(article,NativeTitleHtml(heading:false));
            Check(article.Title==NativeTitle && article.TitleSource=="script:msg_title" && article.PageTitleSource=="script:msg_title",
                "an explicit msg_title must outrank generic account title even when no activity heading exists");
            article=New("123"); article.AccountName=NativeAccount;
            ArticleMetadata.ApplyHtml(article,NativeTitleHtml(heading:false,messageTitle:false));
            Check(article.Title==NativeTitle && article.TitleSource=="meta:og:title", "social title must precede a generic account-title fallback");
            article=New("123"); article.AccountName=NativeAccount; article.Title=NativeTitle;
            article.TitleSource="native_profile_accessibility";
            ArticleMetadata.ApplyHtml(article,"<script>var title='"+NativeAccount+"';</script><div id='js_content'>正文</div>");
            Check(article.Title==NativeTitle && article.TitleSource=="native_profile_accessibility" && article.PageRawTitle=="",
                "a generic title equal to the known account name cannot replace the observed article title");
        }

        static void CachedAccountTitleRepair()
        {
            var saved=PollutedNativeTitle(); saved.Html="caller-owned cached body";
            var repaired=JObject.FromObject(saved).ToObject<ArticleRecord>();
            ArticleMetadata.ApplyHtml(repaired,NativeTitleHtml());
            Check(repaired.Title==NativeTitle && repaired.TitleSource=="heading" && repaired.MetadataVersion==ArticleMetadata.CurrentVersion
                && repaired.Html==saved.Html && repaired.Status==saved.Status && repaired.Id==saved.Id,
                "the proven version-1 account-title bug was not corrected from its cached heading without changing body/status/identity");
            ArticleMetadata.Merge(repaired,saved);
            Check(repaired.Title==NativeTitle && repaired.TitleSource=="heading" && repaired.Html==saved.Html,
                "save-time metadata merge restored the corrupted account title over its verified correction");
            Check(!ArticleMetadata.ApplyHtml(repaired,NativeTitleHtml()),"repeated correction must be idempotent");

            foreach(string changedField in new[]{"account","source","evidence"})
            {
                var protectedRecord=PollutedNativeTitle();
                if(changedField=="account")protectedRecord.AccountName="其他已知公众号名";
                if(changedField=="source")protectedRecord.TitleSource="history";
                if(changedField=="evidence")protectedRecord.PageTitleSource="meta:og:title";
                ArticleMetadata.ApplyHtml(protectedRecord,NativeTitleHtml());
                Check(protectedRecord.Title==NativeAccount,"correction escaped its exact cached-corruption conditions: "+changedField);
            }
            var unrelated=PollutedNativeTitle();
            string changedHeading=NativeTitleHtml().Replace("<h1 id='activity-name'>\n"+NativeTitle+"</h1>","<h1 id='activity-name'>完全不相关的新标题</h1>");
            ArticleMetadata.ApplyHtml(unrelated,changedHeading);
            ArticleMetadata.ApplyHtml(unrelated,changedHeading);
            Check(unrelated.Title==NativeAccount && ArticleMetadata.NormalizeTitle(unrelated.PageRawTitle)==NativeTitle,
                "a rejected unrelated heading replaced cached repair evidence and unlocked itself on a second pass");
            var foreign=PollutedNativeTitle(); ArticleMetadata.ApplyHtml(foreign,NativeTitleHtml(mid:"999"));
            Check(foreign.Title==NativeAccount && foreign.MetadataVersion==1,"another article's HTML donated a cached-title correction");
            var other=New("999"); other.Title="另一篇自己的标题"; other.TitleSource="heading";
            ArticleMetadata.Merge(other,saved);
            Check(other.Title=="另一篇自己的标题","different storage identities borrowed this title correction");
        }

        static void MergeEvidence()
        {
            var saved = New("123");
            ArticleMetadata.ApplyHistory(saved, "完整历史标题末尾", new DateTime(2023, 11, 15, 6, 13, 0));
            ArticleMetadata.ApplyHtml(saved, "<h1 id='activity-name'>完整历史标题末尾</h1><script>var ct=1700000000;</script><div id='js_content'>完整正文</div>");
            saved.Status = ArticleStatus.Available; saved.Html = "untouched saved body";
            var fresh = New("123");
            ArticleMetadata.ApplyHistory(fresh, "完整历史标题", new DateTime(2023, 11, 15, 6, 13, 0));
            fresh.Html = "new body owned by caller";
            ArticleMetadata.Merge(fresh, saved);
            Check(fresh.Title == "完整历史标题末尾" && fresh.PublishedAt.Value.Second == 20 && fresh.PublishedAtSource == "page:ct"
                && fresh.HistoryTitle == "完整历史标题" && fresh.HistoryPublishedAt.Value.Second == 0, "a pending history refresh retains precise selected values and separately records the new list evidence");
            Check(fresh.Status == ArticleStatus.Pending && fresh.Html == "new body owned by caller" && saved.Html == "untouched saved body", "metadata merging does not alter status, caller-owned bodies or prior records");
            var other = New("999"); other.Title = "独立短文"; other.ContentKind = ArticleContentKind.ShortPost;
            ArticleMetadata.Merge(other, saved);
            Check(other.Title == "独立短文" && other.PublishedAt == null && other.ContentKind == ArticleContentKind.ShortPost, "different message IDs never borrow titles, dates or article types");
            string title = fresh.Title; DateTime? date = fresh.PublishedAt;
            ArticleMetadata.Merge(fresh, saved);
            Check(fresh.Title == title && fresh.PublishedAt == date, "repeated metadata merge is idempotent");
        }

        static ArticleRecord New(string mid) => new ArticleRecord { Biz = "test", Mid = mid, Idx = 1, Id = "test:" + mid + ":1",
            Url = "https://mp.weixin.qq.com/s?__biz=test&mid=" + mid + "&idx=1" };
        static void UnresolvedIdentities()
        {
            string one = ArticleIdentity.Create("test", "", 1, "https://mp.weixin.qq.com/s?article_token=first&key=old&uin=1");
            string two = ArticleIdentity.Create("test", "", 1, "https://mp.weixin.qq.com/s?article_token=second&key=old&uin=1");
            Check(one != two, "unknown but meaningful query parameters distinguish unresolved messages");
            Check(one == ArticleIdentity.Create("test", "", 1, "https://mp.weixin.qq.com/s?uin=2&key=new&article_token=first&scene=124#section"),
                "session refresh, query order and fragments do not duplicate an unresolved article");
            Check(one != ArticleIdentity.Create("other", "", 1, "https://mp.weixin.qq.com/s?article_token=first"), "unresolved URL identities remain isolated by account");
            Check(ArticleIdentity.Create("test", "", 1, "https://mp.weixin.qq.com/s?__biz=test&uin=1", "source-1") == "message:test:source-1:1"
                && ArticleIdentity.Create("test", "", 1, "https://mp.weixin.qq.com/s?__biz=test&uin=1", "source-2") == "message:test:source-2:1",
                "a bare article endpoint uses the actual source message instead of collapsing all messages by path");
            bool rejected = false;
            try { ArticleIdentity.Create("test", "", 1, "https://mp.weixin.qq.com/s?__biz=test&uin=1"); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "an unidentifiable generic endpoint with no source ID is not silently accepted");
            Check(ArticleIdentity.Create("test", "", 1, "https://mp.weixin.qq.com/s/TOKEN_A") != ArticleIdentity.Create("test", "", 1, "https://mp.weixin.qq.com/s/TOKEN_B"), "actual short-link path tokens remain distinct");
            Check(ArticleIdentity.Create("test", "123", 2, "https://mp.weixin.qq.com/s?key=old") == "test:123:2"
                && ArticleIdentity.Create("", "", 2, "https://mp.weixin.qq.com/s?__biz=test&mid=123&idx=2&key=new") == "test:123:2", "established biz-mid-idx identity remains compatible");
        }
        static void ResolvedPageIdentity()
        {
            var article = new ArticleRecord { Id = "url:old-storage-key", Biz = "test", Mid = "", Idx = 1,
                IdentityUnresolved = true, Url = "https://mp.weixin.qq.com/s/opaque-short-link" };
            ArticleEnricher.ParseHtml(article, "<script>function lib(){var mid='999';}var library={mid:'888'};var quoted=\"var mid='777';\";</script><div id='js_content'>正文</div>");
            Check(article.Mid == "" && article.IdentityUnresolved && article.Id == "url:old-storage-key", "local object or string mid values cannot resolve a short-link message");
            ArticleEnricher.ParseHtml(article, "<script>function lib(){var mid='999';}var biz='test';var mid='123';var appmsgid='123';var idx=1;</script><div id='js_content'>正文</div>");
            Check(article.Mid == "123" && !article.IdentityUnresolved && article.ResolvedArticleId == "test:123:1" && article.Id == "url:old-storage-key", "explicit page mid and idx resolve an alias without changing the old cache key");
            var conflict = new ArticleRecord { Id = "url:conflict", Biz = "", Mid = "", IdentityUnresolved = true };
            ArticleMetadata.ApplyHtml(conflict, "<script>var biz='test';var __biz='other';var mid='123';</script><div id='js_content'>正文</div>");
            Check(conflict.Biz == "" && conflict.Mid == "" && conflict.IdentityUnresolved, "conflicting account aliases do not populate empty identity fields");
            ArticleMetadata.ApplyHtml(conflict, "<script>var biz='test';var mid='123';var appmsgid='456';</script><div id='js_content'>正文</div>");
            Check(conflict.Biz == "" && conflict.Mid == "", "conflicting message aliases do not choose whichever appeared first");
            ArticleMetadata.ApplyHtml(conflict, "<script>var biz='test';var __biz='test';window.appmsgid='123';var idx=1;</script><div id='js_content'>正文</div>");
            Check(conflict.Biz == "test" && conflict.Mid == "123" && !conflict.IdentityUnresolved && conflict.ResolvedArticleId == "test:123:1" && conflict.Id == "url:conflict", "consistent explicit page identity fills empty fields only");
            var refreshed = new ArticleRecord { Id = article.Id, Biz = "test", Mid = "", Idx = 1, IdentityUnresolved = true, Url = article.Url };
            ArticleMetadata.Merge(refreshed, article);
            Check(refreshed.ResolvedArticleId == "test:123:1" && refreshed.Mid == "123" && refreshed.Idx == 1 && !refreshed.IdentityUnresolved
                && refreshed.Id == article.Id, "a later short-link history refresh retains the previously proven alias and exact identity");
            var legacy = New("888"); legacy.Id = "url:legacy"; legacy.MetadataVersion = ArticleMetadata.CurrentVersion;
            var legacyRefresh = new ArticleRecord { Id = legacy.Id, Biz = "test", Mid = "", IdentityUnresolved = true };
            ArticleMetadata.Merge(legacyRefresh, legacy);
            Check(legacyRefresh.Mid == "" && legacyRefresh.ResolvedArticleId == "" && legacyRefresh.IdentityUnresolved, "an old fake common.id or migration version cannot manufacture a proven alias");
            var unprovedIndex = new ArticleRecord { Id = "url:no-index", Biz = "test", IdentityUnresolved = true, Url = "https://mp.weixin.qq.com/s/opaque" };
            ArticleMetadata.ApplyHtml(unprovedIndex, "<script>var mid='123';</script><div id='js_content'>正文</div>");
            Check(unprovedIndex.Mid == "123" && unprovedIndex.ResolvedArticleId == "" && unprovedIndex.IdentityUnresolved, "a default idx of one is not explicit child-article evidence");
            unprovedIndex.Url += "?idx=1";
            ArticleMetadata.ApplyHtml(unprovedIndex, "<script>var mid='123';</script><div id='js_content'>正文</div>");
            Check(unprovedIndex.ResolvedArticleId == "test:123:1" && !unprovedIndex.IdentityUnresolved, "an explicit request idx can complete the unique page mid proof");
            var child = new ArticleRecord { Id = "url:second-child", Biz = "test", IdentityUnresolved = true, Url = "https://mp.weixin.qq.com/s/child" };
            ArticleMetadata.ApplyHtml(child, "<script>var mid='123';var idx=2;</script><div id='js_content'>正文</div>");
            Check(child.ResolvedArticleId == "test:123:2" && child.Idx == 2 && child.Id == "url:second-child", "an explicit child index can replace an unresolved default without changing the storage key");
            var differentProof = New("999"); differentProof.Id = article.Id;
            ArticleMetadata.ApplyHtml(differentProof, "<script>var mid='999';var idx=1;</script><div id='js_content'>正文</div>");
            ArticleMetadata.Merge(differentProof, article);
            Check(differentProof.ResolvedArticleId == "test:999:1" && differentProof.Mid == "999", "conflicting resolved identities never borrow each other's identity evidence");
        }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Article metadata self-test: " + message); }
    }
}
