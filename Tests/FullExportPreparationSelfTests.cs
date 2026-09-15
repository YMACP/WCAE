using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public static class FullExportPreparationSelfTests
    {
        public static async Task RunAsync()
        {
            string root=Path.Combine(AppPaths.DataDirectory,"full-preparation-"+Guid.NewGuid().ToString("N"));
            var repository=new ArticleRepository(root);
            const string body="<div id='wcae_body'>已有正文</div>";
            var saved=Article("1",body); repository.Save(saved);
            int requests=0;
            var preparation=new ExportPreparation(repository,(a,s,t)=>
            { requests++; a.Status=ArticleStatus.Restricted; a.StatusDetail="文章会话已失效"; a.Html=""; return Task.FromResult(a); });
            var partial=await preparation.PrepareAsync(saved,new AccountSession{Biz=saved.Biz},ExportFormat.Full,CancellationToken.None);
            Check(partial.Status==ArticleStatus.Available && partial.Html==body && requests==1,"全文音频补全失败丢失了可导出的已有正文");
            Check(partial.Media.Single().Kind==MediaKind.Audio && partial.Media.Single().UnavailableReason.Contains("会话已失效"),"全文缺失音频没有保留具体原因");
            Check(repository.LoadBody(saved.Id)==body && repository.Load(saved.Biz).Single().Media.Count==0,"全文临时错误污染了原始缓存");

            var complete=Article("2",body); complete.AudioMetadataVersion=1; repository.Save(complete);
            await preparation.PrepareAsync(complete,new AccountSession{Biz=complete.Biz},ExportFormat.Full,CancellationToken.None);
            Check(requests==1,"已扫描无音频的全文重复获取文章");

            var missing=Article("3","");
            missing.Media.Add(new MediaAsset{Kind=MediaKind.Audio,Id="known-audio",Url="https://media.invalid/known.mp3"}); repository.Save(missing);
            var refresh=new ExportPreparation(repository,(a,s,t)=>
            { requests++; a.Html="<div id='js_content'>恢复的正文</div>"; a.AudioMetadataVersion=1; return Task.FromResult(a); });
            var recovered=await refresh.PrepareAsync(missing,new AccountSession{Biz=missing.Biz},ExportFormat.Full,CancellationToken.None);
            Check(requests==2 && recovered.Html.Contains("恢复的正文") && recovered.Media.Single().Id=="known-audio","全文因已有音频跳过缺失正文的恢复");

            var inline=Article("4","<div id='js_content'>正文<audio src='https://media.invalid/inline.mp3'></audio></div>"); repository.Save(inline);
            var local=await preparation.PrepareAsync(inline,new AccountSession{Biz=inline.Biz},ExportFormat.Full,CancellationToken.None);
            Check(requests==2 && local.Media.Single().Kind==MediaKind.Audio && local.AudioMetadataVersion==1,"全文没有复用原始缓存内嵌音频");

            var unavailable=Article("5","");
            unavailable.Media.Add(new MediaAsset{Kind=MediaKind.Audio,Url="https://media.invalid/available.mp3"}); repository.Save(unavailable);
            var failedBody=await preparation.PrepareAsync(unavailable,new AccountSession{Biz=unavailable.Biz},ExportFormat.Full,CancellationToken.None);
            Check(failedBody.Status==ArticleStatus.Restricted && failedBody.Media.Single().Url.EndsWith("available.mp3"),"全文正文获取失败没有保留可用音频及明确正文错误");

            var empty=Article("6",""); repository.Save(empty);
            var partialRefresh=new ExportPreparation(repository,(a,s,t)=>
            {
                a.Html="<div id='js_content'>本次取得的正文<img src='https://media.invalid/p.png'></div>";
                a.Media.Add(new MediaAsset{Kind=MediaKind.Image,Url="https://media.invalid/p.png"});
                a.AudioMetadataVersion=0; return Task.FromResult(a);
            });
            var newBody=await partialRefresh.PrepareAsync(empty,new AccountSession{Biz=empty.Biz},ExportFormat.Full,CancellationToken.None);
            Check(newBody.Status==ArticleStatus.Available && newBody.Html.Contains("本次取得的正文") && newBody.Media.Any(m=>m.Kind==MediaKind.Image)
                && newBody.Media.Any(m=>m.Kind==MediaKind.Audio && m.UnavailableReason.Length>0),"全文因音频扫描不完整丢弃刚取得的正文和图片");
            Check(repository.LoadBody(empty.Id)=="" && repository.Load(empty.Biz).Single(a=>a.Id==empty.Id).Media.Count==0,"不完整的全文准备结果写入了原缓存");
        }
        static ArticleRecord Article(string mid,string html)=>new ArticleRecord
        { Id="full-preparation:"+mid+":1",Biz="full-preparation",Mid=mid,Idx=1,Title="全文准备",Status=ArticleStatus.Available,
            Url="https://mp.weixin.qq.com/s?__biz=full-preparation&mid="+mid+"&idx=1",Html=html };
        static void Check(bool ok,string reason){if(!ok)throw new InvalidOperationException(reason);}
    }
}
