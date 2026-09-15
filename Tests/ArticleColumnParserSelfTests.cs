using System;
using System.Collections.Generic;
using System.Linq;

namespace WCAE
{
    public static class ArticleColumnParserSelfTests
    {
        public static List<string> Run()
        {
            var passed = new List<string>();
            var article = Parse(@"var cgiData={appmsgalbuminfo:{album_id:'4113194063615115265',title:'码农小站',
isupdating:'1' * 1, content_size:'3' * 1, fee:'0' * 1, article_titles:[]}};");
            Expect(article, "4113194063615115265", "码农小站");
            article = Parse(@"var album_info_list=[{title:'渗透测试',size:'57' * 1,link:'https://mp.weixin.qq.com/mp/appmsgalbum?album_id=2321406246047760386',
type:'0' * 1,albumId:2321406246047760400,albumIdStr:'2321406246047760386',tagId:'' * 1,
id:'url' ? (('12345678'.match(/[0-9]{8,}/)) ? ('12345678'.match(/[0-9]{8,}/))[0] : '') : '',continousReadOn:'1' * 1}];");
            Expect(article, "2321406246047760386", "渗透测试");
            Check(article.ColumnMetadataVersion == ArticleColumnParser.CurrentVersion, "未标记合集元数据版本");
            passed.Add("合集真实页面表达式、字符串ID优先与中文名称");

            article = Parse(@"var album_list=[{album_id:4113194063615115265,album_name:'合辑甲'},{album_id:'20',album_name:'合辑乙'},
{album_id:'20',title:'合辑乙'},{album_id:'0',title:'无效合集'}];");
            Check(article.Columns.Count == 2, "多个合集未正确去重或零ID进入归属");
            Expect(article, "4113194063615115265", "合辑甲");
            Expect(article, "20", "合辑乙");
            article = Parse(@"var album_id='30'; var album_name='旧版合集';");
            Expect(article, "30", "旧版合集");
            article = new ArticleRecord { Biz = "test" };
            ArticleColumnParser.Apply(article, "<script>var album_id='34';</script><script>var album_name='跨脚本旧合集';</script>");
            Expect(article, "34", "跨脚本旧合集");
            article = new ArticleRecord { Biz = "test" };
            ArticleColumnParser.Apply(article, "<script>var album_id='35';var album_name='不确定甲';</script><script>var album_id='36';var album_name='不确定乙';</script>");
            Check(article.Columns.Count == 2 && article.Columns.All(ArticleColumnParser.IsPlaceholder), "存在冲突的顶层旧变量被错误配对");
            article = Parse("recommendations.album_id='99'; recommendations.album_name='不属于本文'; window.album_id='37';window.album_name='全局旧合集';");
            Check(article.Columns.Count == 1, "其他对象的字段赋值串入全局旧合集");
            Expect(article, "37", "全局旧合集");
            article = new ArticleRecord { Biz = "test", Url = "https://mp.weixin.qq.com/s" };
            ArticleColumnParser.Apply(article, "{\"album_info\":{\"album_id\":\"31\",\"title\":\"JSON合集\"}}");
            Expect(article, "31", "JSON合集");
            article = Parse("var albumData='{\"album_id\":\"32\",\"title\":\"字符串内合集\"}';");
            Expect(article, "32", "字符串内合集");
            article = new ArticleRecord { Biz = "test" };
            ArticleColumnParser.Apply(article, "<section id='js_album'><a href='https://mp.weixin.qq.com/mp/appmsgalbum?album_id=33'>页面合集</a></section>");
            Expect(article, "33", "页面合集");
            passed.Add("旧版列表、多个合集、长数字ID、JSON及DOM兼容");

            article = Parse(@"var album_info={album_id:'40', ignored: /[,{}\[\]'""/]/g.test('x') ? make(1,2,{x:'y'}) : false,
title:'\u7801\u519c\x26amp;\'引号\""与\\路径', more:(function(){throw new Error('must not execute');})()};");
            Expect(article, "40", "码农&'引号\"与\\路径");
            article = Parse("var appmsgalbuminfo={album_id:'41',title:'{',extra:'}',other:']'};");
            Expect(article, "41", "{");
            article = Parse("var appmsgalbuminfo={album_id:'42',title:sideEffect(),size:'1'*1};");
            Check(article.Columns.Single().Id == "42" && ArticleColumnParser.IsPlaceholder(article.Columns.Single()), "动态表达式被误作名称");
            passed.Add("特殊字符、正则和函数表达式安全跳过、不执行脚本");

            article = Parse(@"var cgiData={recommend_articles:[{album_info:{album_id:'90',title:'推荐合集'}}],
public_tag_info:{tags:[{tag_name:'普通标签',tag_id:'91'},{album_id:'92',tag_name:'缺少合集容器的标签'}]},
appmsgalbuminfo:{album_id:'50',title:'本篇合集',article_list:[{id:'93',title:'文章标题',album_info:{album_id:'94',title:'其他合集'}}]}};
var misleading='album_info:{album_id:95,title:错误}'; // album_info:{album_id:'96',title:'注释'}
var ordinary={album_id:'97',title:'普通对象'};");
            Check(article.Columns.Count == 1, "推荐文章、标签、字符串或注释串入合集");
            Expect(article, "50", "本篇合集");
            ArticleColumnParser.Apply(article, "<div id='js_content'><a href='https://mp.weixin.qq.com/mp/appmsgalbum?album_id=98'>其他文章推荐</a></div><div class='recommend'><span data-album-id='99' data-album-name='推荐'>推荐</span></div>");
            Check(article.Columns.Count == 1, "普通链接或推荐DOM串入合集");
            article = Parse("var public_tag_info={tags:[{tag_name:'无关标签',album_info:{album_id:'51',title:'明确关联合集'}}]};");
            Expect(article, "51", "明确关联合集");
            article = Parse("var recommendations={text:'}',album_info:{album_id:'99',title:'不属于本文'}}; var appmsgalbuminfo={album_id:'52',title:'正常'};");
            Check(article.Columns.Count == 1, "推荐内的字符串括号提前关闭隔离上下文");
            Expect(article, "52", "正常");
            article = Parse("var album_info={album_id:'53',title:'其他号',link:'https://mp.weixin.qq.com/mp/appmsgalbum?__biz=other&album_id=53'};");
            ArticleColumnParser.Apply(article, "<section id='js_album'><a data-album-id='54' href='https://mp.weixin.qq.com/mp/appmsgalbum?__biz=other&amp;album_id=54'>其他号</a></section>");
            Check(article.Columns.Count == 0, "明确属于其他公众号的合集串入本文");
            passed.Add("推荐文章与普通标签排除、合集容器精确关联");

            var saved = new ColumnInfo { Id = "60", Name = "真实名称", Url = "https://mp.weixin.qq.com/mp/appmsgalbum?album_id=60" };
            var placeholder = new ColumnInfo { Id = "60", Name = "合集 60" };
            var merged = ArticleColumnParser.Merge(new[] { placeholder }, new[] { saved });
            Check(merged.Single().Name == "真实名称" && placeholder.Name == "合集 60", "占位覆盖真实名称或合并修改了输入对象");
            merged = ArticleColumnParser.Merge(new[] { saved }, new[] { placeholder });
            Check(merged.Single().Name == "真实名称", "新真实名称未升级历史占位");
            merged = ArticleColumnParser.Merge(new[] { new ColumnInfo { Name = "手工历史甲" } }, new[] { new ColumnInfo { Name = "手工历史乙" } });
            Check(merged.Count == 2, "合法的仅名称历史记录丢失");
            article = new ArticleRecord { Biz = "test", Html = "原正文", Status = ArticleStatus.Available, StatusDetail = "原状态", AudioMetadataVersion = 9, Columns = new List<ColumnInfo> { placeholder } };
            ArticleColumnParser.Apply(article, "<script>var album_info={album_id:'60',title:'补全名称'};</script>");
            Expect(article, "60", "补全名称");
            Check(article.Html == "原正文" && article.StatusDetail == "原状态" && article.Status == ArticleStatus.Available && article.AudioMetadataVersion == 9, "合集维护修改无关文章字段");
            ArticleColumnParser.Apply(article, "<script>var album_info={album_id:'60',title:'新版名称'};</script>");
            Expect(article, "60", "新版名称");
            ArticleColumnParser.Apply(article, "<script>var album_info={album_id:'60'};</script>");
            Expect(article, "60", "新版名称");
            Check(ArticleColumnParser.IsPlaceholder(new ColumnInfo { Id = "60", Name = "合集60" }), "兼容无空格占位名称");
            passed.Add("占位升级、真实名称保护、重复解析与仅合集字段更新");
            return passed;
        }
        static ArticleRecord Parse(string script)
        {
            var article = new ArticleRecord { Biz = "test", Url = "https://mp.weixin.qq.com/s" };
            ArticleColumnParser.Apply(article, "<script>" + script + "</script>"); return article;
        }
        static void Expect(ArticleRecord article, string id, string name) => Check(article.Columns.Any(c => c.Id == id && c.Name == name), "合集名称/ID不匹配：" + id + " -> " + name);
        static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    }
}
