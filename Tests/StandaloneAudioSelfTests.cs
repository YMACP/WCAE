using System;
using System.Linq;
using System.Web;
using HtmlAgilityPack;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public static class StandaloneAudioSelfTests
    {
        const string Biz = "MzkxNjYxNDc5MA==", Mid = "2247488296", Voice = "MzkxNjYxNDc5MF8yMjQ3NDg4Mjk1";
        const string Title = "爱玩牛AI机器人课";
        public static void Run()
        {
            ObservedStandaloneObjectPreservesAudioAndBody();
            StandaloneJsonUsesAudioAsContent();
            StandaloneSignatureListsAndVisibleDescriptionRetainControls();
            OrdinaryEmbeddedAudioKeepsExistingSources();
            UnsupportedExpressionsAreDataErrorsNotExecutableCode();
            OldSyntheticBodiesDoNotClaimCompletedScan();
            EmptyOrUnknownAudioDoesNotCreateAvailableArticles();
        }
        static ArticleRecord Article() => new ArticleRecord
        { Biz = Biz, Mid = Mid, Idx = 1, Id = Biz + ":" + Mid + ":1", Url = "https://mp.weixin.qq.com/s?__biz=" + Uri.EscapeDataString(Biz) + "&mid=" + Mid + "&idx=1" };
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Standalone audio self-test: " + message); }
        static string Page(string fields, string body = "", int type = 7) => "<!doctype html><html><head><meta charset='utf-8'></head><body>" + body
            + "<script>var msg_title='" + Title + "';var biz='" + Biz + "';var mid='" + Mid + "';var idx='1';var data={item_show_type:'" + type + "'*1," + fields + "};</script></body></html>";
        static string ObservedFields => "voice_in_appmsg:[{voice_id:'" + Voice + "',sn:'fixture-signature',listen_id:'222508242437534524'}],"
            + "voice_page_info:{voice_id:'" + Voice + "',duration:'133' * 1,high_size:'1069293'*1,low_size:'273643'*1,accept_aac:'1'*1,"
            + "voice_verify_state:'3'*1,title:'" + Title + "',desc:'',listen_id:'222508242437534524',light_cover_color:'#B2FFBA',dark_cover_color:'#004D08',}";
        static void ObservedStandaloneObjectPreservesAudioAndBody()
        {
            var article = ArticleHtmlParser.Parse(Article(), Page(ObservedFields));
            Check(article.Status == ArticleStatus.Available && article.AudioMetadataVersion == 1 && article.Title == Title,
                "独立音频页未成为已扫描的可访问文章或标题丢失");
            var audio = article.Media.Single(x => x.Kind == MediaKind.Audio);
            var url = new Uri(audio.Url); var query = HttpUtility.ParseQueryString(url.Query);
            Check(audio.Id == Voice && audio.Name == Title && audio.UnavailableReason.Length == 0
                && url.Host == "res.wx.qq.com" && url.AbsolutePath == "/voice/getvoice"
                && query["mediaid"] == Voice && query["voice_type"] == "1", "独立播放器身份或 voice_type 不正确");
            var document = new HtmlDocument(); document.LoadHtml(article.Html);
            Check(document.GetElementbyId("wcae_body") != null && document.DocumentNode.Descendants("audio").Single().GetAttributeValue("src", "").Contains("voice_type=1"),
                "合成正文丢失音频控件");
            Check(!article.Html.Contains("var data") && !article.Html.Contains("fixture-signature"), "正文保存了脚本外壳或不需要的签名");
            Check(ArticleAudioParser.TryLiteral("{duration:'133'*1,low_size:'273643'*1, nested:[true,null,'escaped\\\'quote'],}", out JToken literal)
                && (int)literal["duration"] == 133, "实际数字转换表达式没有作为数据解析");
        }
        static void StandaloneJsonUsesAudioAsContent()
        {
            var json = new JObject
            {
                ["biz"] = Biz, ["mid"] = Mid, ["idx"] = 1, ["item_show_type"] = 7,
                ["voice_page_info"] = new JObject { ["voice_id"] = Voice, ["title"] = Title, ["duration"] = 133,
                    ["desc"] = "录音介绍 <script>不作为 HTML 执行</script>", ["audio_url"] = "https://audio.example.invalid/recording.mp3" },
                ["voice_in_appmsg"] = new JArray(new JObject { ["voice_id"] = Voice, ["sn"] = "fixture-signature" })
            };
            var article = ArticleHtmlParser.Parse(Article(), json.ToString());
            Check(article.Status == ArticleStatus.Available && article.AudioMetadataVersion == 1 && article.Media.Count(x => x.Kind == MediaKind.Audio) == 1,
                "只有音频结构的 JSON 未保留为文章或产生重复音频");
            Check(article.Media.Single().Url == "https://audio.example.invalid/recording.mp3" && article.Title == Title && article.Digest.Contains("录音介绍"),
                "明确音频直链/音频标题/说明被丢弃");
            var doc = new HtmlDocument(); doc.LoadHtml(article.Html);
            Check(!doc.DocumentNode.Descendants("script").Any() && doc.DocumentNode.Descendants("audio").Any(), "音频说明未编码或正文缺控件");
        }
        static void OrdinaryEmbeddedAudioKeepsExistingSources()
        {
            string fields = "voice_page_info:{},voice_in_appmsg:[{voice_id:'inline_voice',sn:'fixture-inline'}]";
            string body = "<h1 id='activity-name'>普通图文</h1><div id='js_content'><p>图文正文</p>"
                + "<mpvoice voice_encode_fileid='inline_voice' name='内嵌录音'></mpvoice>"
                + "<audio><source src='https://audio.example.invalid/source.m4a' type='audio/mp4'></audio>"
                + "<audio src='https://audio.example.invalid/direct.mp3'></audio></div>";
            var article = ArticleHtmlParser.Parse(Article(), Page(fields, body, 0));
            Check(article.Status == ArticleStatus.Available && article.AudioMetadataVersion == 1
                && article.Media.Count(x => x.Kind == MediaKind.Audio) == 3, "普通 mpvoice/audio/source 回归失败或 voice_in_appmsg 被重复计入");
            Check(article.Media.Any(x => x.Url == "https://res.wx.qq.com/voice/getvoice?mediaid=inline_voice")
                && article.Media.All(x => !x.Url.Contains("voice_type=1")), "普通内嵌音频被切到独立页播放方式");
            var none = ArticleHtmlParser.Parse(Article(), "<div id='js_content'>没有音频的正常正文</div>");
            Check(none.Status == ArticleStatus.Available && none.AudioMetadataVersion == 1 && none.Media.Count == 0,
                "已完整扫描且没有音频的正常页面没有标记版本");
            var defaults = ArticleHtmlParser.Parse(Article(), Page("voice_page_info:{voice_id:'',duration:'0'*1,title:'',desc:'',accept_aac:'0'*1}", "<div id='js_content'>普通正文</div>", 0));
            Check(defaults.Status == ArticleStatus.Available && defaults.AudioMetadataVersion == 1 && defaults.Media.Count == 0,
                "普通页默认空音频对象触发了重复回源或产生虚假音频");
            var emptyWrapper = ArticleHtmlParser.Parse(Article(), Page("voice_page_info:{voice_id:''},voice_in_appmsg_list_json:'{}'", "<div id='js_content'>普通正文</div>", 0));
            Check(emptyWrapper.Status == ArticleStatus.Available && emptyWrapper.AudioMetadataVersion == 1 && emptyWrapper.Media.Count == 0,
                "默认空的转义音频包装对象被误判未知格式");
        }
        static void StandaloneSignatureListsAndVisibleDescriptionRetainControls()
        {
            string list = "[{\"voice_id\":\"" + Voice + "\",\"sn\":\"fixture-signature\"}]";
            foreach (string fields in new[] { "voice_in_appmsg:" + list,
                "voice_in_appmsg_list_json:'" + list.Replace("\"", "\\\"") + "'",
                "voice_in_appmsg_list_json:'" + ("{\"voice_in_appmsg\":" + list + "}").Replace("\"", "\\x22") + "'" })
            {
                var article = ArticleHtmlParser.Parse(Article(), Page(fields, "<div id='js_common_share_desc'>音频说明</div>"));
                Check(article.Status == ArticleStatus.Available && article.AudioMetadataVersion == 1
                    && article.Media.Count(x => x.Kind == MediaKind.Audio) == 1 && article.Media.Single().Id == Voice,
                    "明确独立页的音频列表/转义 JSON 未解析");
                var document = new HtmlDocument(); document.LoadHtml(article.Html);
                Check(document.DocumentNode.Descendants("audio").Any() && document.GetElementbyId("wcae_body").InnerText.Contains("音频说明"),
                    "有说明区的独立音频页合成正文丢失音频控件或说明");
            }
            var json = new JObject { ["title"] = Title, ["item_show_type"] = 7, ["voice_in_appmsg_list_json"] = list };
            var fromJson = ArticleHtmlParser.Parse(Article(), json.ToString());
            Check(fromJson.Status == ArticleStatus.Available && fromJson.AudioMetadataVersion == 1 && fromJson.Media.Single().Id == Voice,
                "JSON 转义音频列表未解析");
        }
        static void UnsupportedExpressionsAreDataErrorsNotExecutableCode()
        {
            foreach (string raw in new[] { "{duration:(function(){return 133})()}", "{duration:'133'*2}",
                "{duration:fetch('https://example.invalid')}", "{voice_id:'one',voice_id:'two'}", "{duration:'1'*1+1}" })
                Check(!ArticleAudioParser.TryLiteral(raw, out _), "不支持的表达式或重复属性被当成合法数据");
            var unknown = ArticleHtmlParser.Parse(Article(), Page("voice_page_info:runPageCode()", "<div id='js_content'>原图文</div>", 0));
            Check(unknown.Status == ArticleStatus.Available && unknown.AudioMetadataVersion == 0, "未读懂音频变量却标记完成扫描");
            var broken = ArticleHtmlParser.Parse(Article(), Page("voice_page_info:{voice_id:'" + Voice + "',duration:alert(1)}"));
            Check(broken.Status == ArticleStatus.Available && broken.AudioMetadataVersion == 0 && broken.Media.Single().Url.Length == 0
                && broken.Media.Single().UnavailableReason.Length > 0,
                "明确独立音频页解析失败时未保留不可下载原因或误称完成扫描");
        }
        static void OldSyntheticBodiesDoNotClaimCompletedScan()
        {
            var old = Article(); old.AudioMetadataVersion = 1;
            ArticleHtmlParser.Parse(old, "<html><body><div id='wcae_body'><p>旧分享合成正文</p></div></body></html>");
            Check(old.AudioMetadataVersion == 0, "旧合成正文沿用了已扫描标记");
            var failed = Article(); failed.AudioMetadataVersion = 1; ArticleHtmlParser.Parse(failed, "");
            Check(failed.AudioMetadataVersion == 0, "失败解析仍保留成功扫描版本");
        }
        static void EmptyOrUnknownAudioDoesNotCreateAvailableArticles()
        {
            var missing = ArticleHtmlParser.Parse(Article(), Page("voice_page_info:{title:'音频标题但没有身份'}"));
            Check(missing.Status == ArticleStatus.Available && missing.Media.Count == 1 && missing.Media.Single().Url.Length == 0
                && missing.Media.Single().UnavailableReason.Length > 0 && missing.AudioMetadataVersion == 0,
                "明确音频页缺失标识后丢失音频记录或伪造下载地址");
            var empty = ArticleHtmlParser.Parse(Article(), new JObject { ["title"] = "空壳", ["voice_page_info"] = new JObject() }.ToString());
            Check(empty.Status == ArticleStatus.Failed && empty.AudioMetadataVersion == 0, "空音频 JSON 被误判成功");
        }
    }
}
