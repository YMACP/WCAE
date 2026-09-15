using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    // Synthetic fixtures only. This runner cannot issue network requests.
    public static class EnrichmentSelfTests
    {
        public static List<string> Run()
        {
            var passed = new List<string>();
            AccountNames(); passed.Add("公众号名称统一解析、组件字段排除、DOM优先与同号历史保留");
            var article = ArticleEnricher.ParseHtml(NewArticle(), "<html><head><meta property='og:title' content='测试文章'></head><body><div id='js_content'><p>正文谈到内容已被发布者删除，不是错误页。</p></div><script>var ct='1700000000'; var tmpAppmsgBarData={read_num:123,old_like_count:7,like_count:9,share_count:0};</script></body></html>");
            Check(article.Status == ArticleStatus.Available && article.ReadCount == 123 && article.LikeCount == 7 && article.FavoriteCount == 9 && article.ShareCount == 0, "正文和四种指标的映射");
            Check(article.PublishedAt == new DateTime(2023, 11, 15, 6, 13, 20), "微信发布日期转北京时间");
            passed.Add("正常正文、删除词误判防护、四指标、发布日期");

            article = ArticleEnricher.ParseHtml(NewArticle(), "<div id='js_content'>只有正文</div>");
            Check(article.Status == ArticleStatus.Available && article.ReadCount == null && article.LikeCount == null && article.FavoriteCount == null && article.ShareCount == null, "未知指标保持 null");
            passed.Add("未知指标保留 null");
            Check(ArticleEnricher.ParseHtml(NewArticle(), "<div class='weui-msg'>该内容已被发布者删除</div>").Status == ArticleStatus.Deleted, "删除页识别");
            Check(ArticleEnricher.ParseHtml(NewArticle(), "<title>环境异常</title><div id='captcha'>请完成验证</div>").Status == ArticleStatus.Restricted, "验证页识别");
            Check(ArticleEnricher.ParseHtml(NewArticle(), "<title>环境异常</title><div id='js_content'><div id='captcha'>完成验证</div></div>").Status == ArticleStatus.Restricted, "验证页面外壳即使有正文 ID 也不导出");
            Check(ArticleEnricher.ParseHtml(NewArticle(), "<title>公众号</title><nav>登录 首页</nav>").Status == ArticleStatus.Failed, "不导出页面外壳");
            passed.Add("删除、验证码和未知页面分类");

            article = ArticleEnricher.ParseHtml(NewArticle(), "<div id='js_content'>正文<a href='/mp/appmsgalbum?album_id=10&amp;__biz=test'>合辑甲</a></div><script>var album_list=[{album_id:'10',album_name:'合辑甲'},{album_id:'20',album_name:'合辑乙'}];</script>");
            Check(article.Columns.Count == 2 && article.Columns.Any(c => c.Id == "20" && c.Name == "合辑乙"), "一文多合集去重");
            passed.Add("DOM 和 JSON 合集归属合并");

            article = ArticleEnricher.ParseHtml(NewArticle(), "<div id='js_content'><img data-src='//mmbiz.qpic.cn/a.jpg?x=1&amp;sign=a%2Bb'><section style=\"background-image:url('//mmbiz.qpic.cn/b.png')\"></section><mpvoice voice_encode_fileid='abc+/='></mpvoice><iframe src='https://v.qq.com/iframe/player.html?vid=q326831cny0'></iframe><mpvideo vid='wxv_1234567890123'></mpvideo><video src='https://video.example/v.mp4?sign=a%2Bb&amp;ts=1'></video></div>");
            Check(article.Media.Count(x => x.Kind == MediaKind.Image) == 2, "正文和 CSS 图片");
            Check(article.Media.Any(x => x.Kind == MediaKind.Audio && x.Url.Contains("mediaid=abc%2B%2F%3D")), "微信音频标识转地址");
            Check(article.Media.Any(x => x.RequiresExtractor && x.Url == "https://v.qq.com/x/page/q326831cny0.html"), "腾讯 iframe 转解析器入口");
            Check(article.Media.Any(x => x.Id == "wxv_1234567890123" && x.Url == "" && x.UnavailableReason.Length > 0), "静态微信视频未知地址不伪造成功");
            Check(article.Media.Any(x => x.Url == "https://video.example/v.mp4?sign=a%2Bb&ts=1"), "保留签名参数");
            passed.Add("图片、音频、腾讯视频、微信视频和签名参数");

            article = ArticleEnricher.ParseHtml(NewArticle(), "<div id='js_content'><mp-common-videosnap playurl='https://video.example/play?token=a%2Bb&amp;ts=1'></mp-common-videosnap><mp-common-mpaudio src='blob:unavailable' voice_encode_fileid='fallback'></mp-common-mpaudio></div>");
            Check(article.Media.Any(x => x.Kind == MediaKind.Video && x.Url == "https://video.example/play?token=a%2Bb&ts=1"), "签名视频地址无需文件扩展名");
            Check(article.Media.Any(x => x.Kind == MediaKind.Audio && x.Url.EndsWith("mediaid=fallback", StringComparison.Ordinal)), "无效音频占位地址回落到 voice 标识");
            passed.Add("自定义媒体组件、无扩展名签名地址和音频后备");

            article = ArticleEnricher.ParseHtml(NewArticle(), "<meta property='og:title' content='分享图文'><div id='js_share_content'>分享文字</div><div id='js_share_img'><img src='https://image.example/photo.jpg'></div>");
            Check(article.Status == ArticleStatus.Available && article.Html.Contains("id=\"wcae_body\"") && article.Media.Count == 1, "分享图文独立正文约定");
            passed.Add("分享图文正文及兄弟图片容器");

            article = ArticleEnricher.ParseHtml(NewArticle(), "<meta property='og:title' content='分享'><script>var content_noencode='<p>完整正文</p><img src=\"https://image.example/in-body.jpg\">';</script>");
            Check(article.Status == ArticleStatus.Available && article.Html.Contains("<p>完整正文</p>") && article.Media.Count == 1, "脚本 content_noencode 保留 HTML 和正文图片");
            article = ArticleEnricher.ParseHtml(NewArticle(), "{\"title\":\"视频文章\",\"video_page_info\":{\"video_ids\":[\"wxv_1234567890123\"]}}");
            Check(article.Status == ArticleStatus.Available && article.Media.Single().Id == "wxv_1234567890123" && article.Media.Single().UnavailableReason.Length > 0, "JSON 视频标识进入地址解析流程");
            passed.Add("脚本分享富文本和 JSON 视频标识");

            var mock = new FakeTransport { Get = url => "<div id='js_content'>正文</div><script>var appmsg_token='abc';</script>", Post = values => "{\"base_resp\":{\"ret\":0},\"appmsgstat\":{\"read_num\":100,\"old_like_num\":12,\"like_num\":5,\"share_num\":0}}" };
            article = new ArticleEnricher(mock).EnrichAsync(NewArticle(), Session(), CancellationToken.None).GetAwaiter().GetResult();
            Check(article.Status == ArticleStatus.Available && article.ReadCount == 100 && article.LikeCount == 12 && article.FavoriteCount == 5 && article.ShareCount == 0, "统计接口指标映射");
            Check(mock.PostCalls == 1 && mock.Form["mid"] == "123" && mock.Form["sn"] == "sig" && mock.Form["appmsg_type"] == "9", "统计接口请求参数");
            passed.Add("fake transport 统计补充和请求表单");

            mock = new FakeTransport { Get = url => "<div id='js_content'>正文</div>", Post = values => "{\"base_resp\":{\"ret\":-3}}" };
            article = new ArticleEnricher(mock).EnrichAsync(NewArticle(), Session(), CancellationToken.None).GetAwaiter().GetResult();
            Check(article.Status == ArticleStatus.Available && article.ReadCount == null && article.StatusDetail.Contains("ret=-3"), "统计失败不丢正文");
            passed.Add("统计拒绝保留正文与 null");

            mock = new FakeTransport { Get = url => url.Contains("videoplayer") ? "{\"url_info\":[{\"url\":\"https://mpvideo.qpic.cn/v.mp4?sign=a%2Bb&ts=1\",\"width\":1280,\"height\":720}]}" : "<div id='js_content'><mpvideo vid='wxv_1234567890123'></mpvideo></div>" };
            article = new ArticleEnricher(mock).EnrichAsync(NewArticle(), new AccountSession(), CancellationToken.None).GetAwaiter().GetResult();
            Check(mock.GetCalls == 2 && article.Media.Single().Url == "https://mpvideo.qpic.cn/v.mp4?sign=a%2Bb&ts=1" && article.Media.Single().UnavailableReason == "", "解析微信播放响应中的真实直链");
            passed.Add("fake transport 微信视频真实响应地址");

            mock = new FakeTransport { Get = url => url.Contains("f=json") ? "{\"title\":\"纯文字\",\"content_noencode\":\"<p>完整内容</p>\",\"create_time\":1700000000}" : "<title>公众号文章</title>" };
            article = new ArticleEnricher(mock).EnrichAsync(NewArticle(), new AccountSession(), CancellationToken.None).GetAwaiter().GetResult();
            Check(mock.GetCalls == 2 && article.Status == ArticleStatus.Available && article.Html.Contains("完整内容"), "JSON 分享正文后备读取");
            passed.Add("fake transport 纯文字 JSON 后备读取");

            foreach (var code in new[] { 401, 403, 404, 410, 429, 500 })
            {
                mock = new FakeTransport { Get = url => throw new HttpRequestFailureException((System.Net.HttpStatusCode)code, "sensitive-url-must-not-leak") };
                article = new ArticleEnricher(mock).EnrichAsync(NewArticle(), Session(), CancellationToken.None).GetAwaiter().GetResult();
                var expected = code == 404 || code == 410 ? ArticleStatus.Deleted : code == 500 ? ArticleStatus.Failed : ArticleStatus.Restricted;
                Check(article.Status == expected && !article.StatusDetail.Contains("sensitive-url"), "HTTP 状态分类并抑制敏感异常内容");
            }
            passed.Add("HTTP 401/403/404/410/429/500 状态和敏感内容保护");

            var refreshedSession = Session();
            mock = new FakeTransport { Get = url => "<div id='js_content'>正文</div>", BeforeGet = value => value.Cookie = "wxuin=42; refreshed=1" };
            new ArticleEnricher(mock).EnrichAsync(NewArticle(), refreshedSession, CancellationToken.None).GetAwaiter().GetResult();
            Check(refreshedSession.Cookie.Contains("refreshed=1"), "将响应更新的 Cookie 回写供下一篇使用");
            mock = new FakeTransport { Get = url => "<div id='js_content'>正文</div>", BeforeGet = value => { value.Cookie = "stale=1"; refreshedSession.Cookie = "newer=1"; } };
            new ArticleEnricher(mock).EnrichAsync(NewArticle(), refreshedSession, CancellationToken.None).GetAwaiter().GetResult();
            Check(refreshedSession.Cookie == "newer=1", "并发会话更新不被旧快照覆盖");
            passed.Add("响应 Cookie 回写与并发更新保护");

            using (var cts = new CancellationTokenSource())
            {
                mock = new FakeTransport(); cts.Cancel(); bool cancelled = false;
                try { new ArticleEnricher(mock).EnrichAsync(NewArticle(), Session(), cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && mock.GetCalls == 0, "取消时不发请求");
            }
            passed.Add("取消贯穿且不发出网络调用");
            using (var cts = new CancellationTokenSource())
            {
                mock = new FakeTransport { Get = url => { cts.Cancel(); return "<div id='js_content'>正文</div>"; } };
                bool cancelled = false;
                try { new ArticleEnricher(mock).EnrichAsync(NewArticle(), Session(), cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && mock.GetCalls == 1 && mock.PostCalls == 0, "GET 后取消不继续统计补充");
            }
            passed.Add("请求完成边界的取消传播");
            return passed;
        }
        private static void AccountNames()
        {
            const string mapping = "<script>var component={nickname:'data-miniprogram-nickname'};var nickname=htmlDecode('船山信安');</script>";
            var article = ArticleEnricher.ParseHtml(NewArticle(), mapping + "<a id='js_name'>船山信安</a><div id='js_content'>正文</div>");
            Check(article.AccountName == "船山信安", "文章与识别共用DOM优先规则，不取组件字段映射");
            article = ArticleEnricher.ParseHtml(NewArticle(), mapping + "<div id='js_content'>正文</div>");
            Check(article.AccountName == "船山信安", "缺少DOM名称时使用明确昵称声明");
            var prior = NewArticle(); prior.AccountName = "已有名称";
            article = ArticleEnricher.ParseHtml(prior, "<script>var component={nickname:'data-miniprogram-nickname'};</script><div id='js_content'>正文</div>");
            Check(article.AccountName == "已有名称", "缺少可信新名称时保留已有名称");
            prior = NewArticle(); prior.AccountName = "data-miniprogram-nickname";
            Check(ArticleEnricher.ParseHtml(prior, "<div id='js_content'>正文</div>").AccountName == "", "不保留历史组件字段标记");
            article = ArticleEnricher.ParseHtml(NewArticle(), "<a id='js_name'>Studio123</a><div id='js_content'>正文</div>");
            Check(article.AccountName == "Studio123", "合法英文数字公众号名称不能被过滤");
            article = ArticleEnricher.ParseHtml(NewArticle(), "<a id='js_name'>其他号</a><script>var biz='other-biz';var nickname='其他号';</script><div id='js_content'>正文</div>");
            Check(article.AccountName == "", "跨Biz正文的DOM和脚本名称都不能借用");
            article = ArticleEnricher.ParseHtml(NewArticle(), "{\"title\":\"文章\",\"content_noencode\":\"正文\",\"nickname\":\"data-miniprogram-nickname\"}");
            Check(article.AccountName == "", "结构化文章JSON同样拒绝字段标记昵称");
            article = ArticleEnricher.ParseHtml(NewArticle(), "{\"title\":\"文章\",\"content_noencode\":\"正文\",\"biz\":\"other-biz\",\"nickname\":\"其他号\"}");
            Check(article.AccountName == "", "结构化文章JSON不借用其他Biz的名称");
        }
        private static ArticleRecord NewArticle() { return new ArticleRecord { Biz = "test", Mid = "123", Idx = 1, Url = "https://mp.weixin.qq.com/s?__biz=test&mid=123&idx=1&sn=sig" }; }
        private static AccountSession Session() { return new AccountSession { Biz = "test", Uin = "42", Key = "key", Cookie = "wxuin=42; wxtokenkey=777" }; }
        private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Enrichment self-test: " + name); }
        private sealed class FakeTransport : IHttpTransport
        {
            public Func<string, string> Get = url => "";
            public Func<IDictionary<string, string>, string> Post = values => "{}";
            public Action<AccountSession> BeforeGet;
            public int GetCalls, PostCalls;
            public IDictionary<string, string> Form;
            public Task<string> GetStringAsync(string url, AccountSession session, CancellationToken token) { token.ThrowIfCancellationRequested(); GetCalls++; BeforeGet?.Invoke(session); return Task.FromResult(Get(url)); }
            public Task<string> PostFormAsync(string url, IDictionary<string, string> values, AccountSession session, CancellationToken token) { token.ThrowIfCancellationRequested(); PostCalls++; Form = new Dictionary<string, string>(values); return Task.FromResult(Post(values)); }
            public Task DownloadAsync(string url, string destination, AccountSession session, CancellationToken token) { throw new InvalidOperationException("Enricher must not download media files"); }
        }
    }
}
