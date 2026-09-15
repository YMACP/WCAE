using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public static class AudioCacheRecoverySelfTests
    {
        public static void Run()=>RunAsync().GetAwaiter().GetResult();
        static async Task RunAsync()
        {
            await OriginalHtmlRecoversAudioWithoutRequest();
            await NormalizedBodyRefreshPreservesSavedData();
            await SuccessfulNoAudioScanIsDurable();
            await FailedRefreshNeverReplacesSavedArticle();
            await IncompleteResponseAndForeignIdentityAreNotNoAudioSuccess();
            await NewerMetadataIsPreservedDuringAudioRecovery();
            await CancellationAfterResponseDoesNotPersist();
            await CancellationWhileAudioWriteWaitsDoesNotPersist();
            MetadataMergeRetainsTheCompletedAudioScan();
            await NonAudioExportKeepsOrdinaryPreparation();
            await ExistingAudioInNormalizedCacheDoesNotRequireRefresh();
            await AllMediaKeepsExistingAssetsWhenAudioRecoveryFails();
        }
        const string RawHtml="<html><head><title>音频缓存测试</title></head><body><div id='js_content'><p>完整原始正文</p><img src='https://image.invalid/p.png'><mpvoice voice_encode_fileid='synthetic-voice-id'></mpvoice></div></body></html>";
        const string NormalizedHtml="<!doctype html><html><head><title>旧副本</title></head><body><div id='wcae_body'><p>仅保留的正文</p><img src='https://image.invalid/p.png'></div></body></html>";
        static async Task OriginalHtmlRecoversAudioWithoutRequest()
        {
            using var fixture=new Fixture(); var original=Article(RawHtml); fixture.Repository.Save(original);
            int calls=0;
            var helper=new ExportPreparation(fixture.Repository,(a,s,t)=> { calls++; throw new InvalidOperationException("No request expected."); });
            var prepared=await helper.PrepareAsync(original,Session(),ExportFormat.Audio,CancellationToken.None);
            var saved=fixture.Repository.ReadExportMetadata(original.Biz,original.Id);
            Check(calls==0 && prepared.AudioMetadataVersion==1 && saved.AudioMetadataVersion==1
                && prepared.Media.Count(m=>m.Kind==MediaKind.Audio)==1,"已有图片的原始正文未在本地恢复音频");
            Check(saved.Media.Single(m=>m.Kind==MediaKind.Image).Url==original.Media[0].Url && fixture.Repository.LoadBody(original.Id)==RawHtml
                && original.AudioMetadataVersion==0 && original.Media.All(m=>m.Kind!=MediaKind.Audio),"本地恢复改变了原正文、非音频媒体或调用者快照");
        }
        static async Task NormalizedBodyRefreshPreservesSavedData()
        {
            using var fixture=new Fixture(); var original=Article(NormalizedHtml); var repo=fixture.Repository;
            repo.BeginCollectionRun(original.Biz); repo.SaveCollectionPage(original.Biz,new[]{original},0,15,false); repo.MarkArticleStarted(original.Biz,original.Id);
            string checkpoint=JsonConvert.SerializeObject(repo.GetCollectionCheckpoint(original.Biz));
            var before=Metadata(repo,original); int calls=0;
            var helper=new ExportPreparation(repo,(article,session,token)=>
            {
                calls++; Check(article.Id==original.Id && article.AudioMetadataVersion==0,"回源没有使用当前真实文章/清空扫描证明");
                var result=Scanned(article); result.ReadCount=900; result.LikeCount=800;
                result.Title="refresh title"; result.Html=RawHtml; result.Media.Add(new MediaAsset { Kind=MediaKind.Image,Url="https://image.invalid/new.png" });
                return Task.FromResult(result);
            });
            var prepared=await helper.PrepareAsync(original,Session(),ExportFormat.AllMedia,CancellationToken.None);
            var after=Metadata(repo,original);
            Check(calls==1 && prepared.Media.Any(m=>m.Kind==MediaKind.Audio) && prepared.Media.Count(m=>m.Kind==MediaKind.Image)==1
                && repo.LoadBody(original.Id)==NormalizedHtml,"旧规范化正文没有回源补音频，或原正文/图片被替换");
            StripAudio(before); StripAudio(after);
            Check(JToken.DeepEquals(before,after) && checkpoint==JsonConvert.SerializeObject(repo.GetCollectionCheckpoint(original.Biz))
                && repo.PendingCount(original.Biz)==1,"音频恢复改写了指标、标题、更新时间或采集游标");
        }
        static async Task SuccessfulNoAudioScanIsDurable()
        {
            using var fixture=new Fixture(); var original=Article("<html><body><div id='js_content'><p>没有音频的完整正文</p></div></body></html>");
            original.Media.Clear(); fixture.Repository.Save(original); int calls=0;
            var helper=new ExportPreparation(fixture.Repository,(a,s,t)=> { calls++; throw new InvalidOperationException("No request expected."); });
            var first=await helper.PrepareAsync(original,Session(),ExportFormat.Audio,CancellationToken.None);
            var second=await helper.PrepareAsync(original,Session(),ExportFormat.AllMedia,CancellationToken.None);
            Check(calls==0 && first.AudioMetadataVersion==1 && second.AudioMetadataVersion==1
                && !second.Media.Any(m=>m.Kind==MediaKind.Audio),"已确认无音频的缓存重复请求，或旧UI快照覆盖已完成版本");
        }
        static async Task FailedRefreshNeverReplacesSavedArticle()
        {
            foreach(var status in new[]{ArticleStatus.Restricted,ArticleStatus.Failed,ArticleStatus.Deleted})
            {
                using var fixture=new Fixture(); var original=Article(NormalizedHtml); fixture.Repository.Save(original);
                var before=Metadata(fixture.Repository,original);
                var helper=new ExportPreparation(fixture.Repository,(a,s,t)=>
                { a.Status=status; a.StatusDetail="fixture explicit failure"; a.Html=""; a.Media.Clear(); return Task.FromResult(a); });
                var result=await helper.PrepareAsync(original,Session(),ExportFormat.Audio,CancellationToken.None);
                Check(result.Status==status && result.StatusDetail=="fixture explicit failure" && JToken.DeepEquals(before,Metadata(fixture.Repository,original))
                    && fixture.Repository.LoadBody(original.Id)==NormalizedHtml,"访问限制/失败覆盖了原缓存，或被错报为无音频");
            }
            using var exceptionFixture=new Fixture(); var saved=Article(NormalizedHtml); exceptionFixture.Repository.Save(saved);
            var failing=new ExportPreparation(exceptionFixture.Repository,(a,s,t)=>throw new IOException("https://invalid.example/?key=fixture-private"));
            var failed=await failing.PrepareAsync(saved,Session(),ExportFormat.Audio,CancellationToken.None);
            Check(failed.Status==ArticleStatus.Failed && failed.StatusDetail.Contains("IOException") && !failed.StatusDetail.Contains("fixture-private")
                && exceptionFixture.Repository.ReadExportMetadata(saved.Biz,saved.Id).Status==ArticleStatus.Available,"异常未明确报告或泄露原始异常/破坏旧状态");
        }
        static async Task IncompleteResponseAndForeignIdentityAreNotNoAudioSuccess()
        {
            using var fixture=new Fixture(); var original=Article(NormalizedHtml); fixture.Repository.Save(original); int calls=0;
            var incomplete=new ExportPreparation(fixture.Repository,(a,s,t)=> { calls++; a.AudioMetadataVersion=0; return Task.FromResult(a); });
            var result=await incomplete.PrepareAsync(original,Session(),ExportFormat.Audio,CancellationToken.None);
            Check(calls==1 && result.Status==ArticleStatus.Failed && fixture.Repository.ReadExportMetadata(original.Biz,original.Id).AudioMetadataVersion==0,
                "不完整原始音频信息被当作已扫描无音频");
            var foreignSession=Session(); foreignSession.Biz="other";
            result=await incomplete.PrepareAsync(original,foreignSession,ExportFormat.Audio,CancellationToken.None);
            Check(calls==1 && result.Status==ArticleStatus.Failed,"使用其他公众号会话刷新音频");
            var foreign=new ExportPreparation(fixture.Repository,(a,s,t)=> { a=Scanned(a); a.Biz="other"; return Task.FromResult(a); });
            result=await foreign.PrepareAsync(original,Session(),ExportFormat.Audio,CancellationToken.None);
            Check(result.Status==ArticleStatus.Failed && fixture.Repository.Load("other").Count==0,"跨公众号刷新结果被写入缓存");
        }
        static async Task NewerMetadataIsPreservedDuringAudioRecovery()
        {
            using var fixture=new Fixture(); var original=Article(NormalizedHtml); fixture.Repository.Save(original);
            var helper=new ExportPreparation(fixture.Repository,(a,s,t)=>
            {
                var updated=fixture.Repository.ReadExportMetadata(original.Biz,original.Id);
                updated.ReadCount=999; updated.Media.Add(new MediaAsset { Kind=MediaKind.Video,Url="https://video.invalid/new.mp4" });
                fixture.Repository.Save(updated);
                return Task.FromResult(Scanned(a));
            });
            var result=await helper.PrepareAsync(original,Session(),ExportFormat.Audio,CancellationToken.None);
            var saved=fixture.Repository.ReadExportMetadata(original.Biz,original.Id);
            Check(saved.ReadCount==999 && result.ReadCount==999 && saved.Media.Any(m=>m.Kind==MediaKind.Video && m.Url.EndsWith("new.mp4"))
                && saved.Media.Any(m=>m.Kind==MediaKind.Audio),"音频回源期间更新的指标/视频被旧导出快照覆盖");
        }
        static async Task CancellationAfterResponseDoesNotPersist()
        {
            using var fixture=new Fixture(); var original=Article(NormalizedHtml); fixture.Repository.Save(original);
            var before=Metadata(fixture.Repository,original); using var stop=new CancellationTokenSource();
            var helper=new ExportPreparation(fixture.Repository,(a,s,t)=> { stop.Cancel(); return Task.FromResult(Scanned(a)); });
            await ExpectCanceled(helper.PrepareAsync(original,Session(),ExportFormat.Audio,stop.Token));
            Check(JToken.DeepEquals(before,Metadata(fixture.Repository,original)) && fixture.Repository.LoadBody(original.Id)==NormalizedHtml,
                "响应返回后取消仍保存音频版本或改写缓存");
        }
        static async Task CancellationWhileAudioWriteWaitsDoesNotPersist()
        {
            using var fixture=new Fixture(); var original=Article(RawHtml); fixture.Repository.Save(original);
            var before=Metadata(fixture.Repository,original); using var stop=new CancellationTokenSource(); using var entered=new ManualResetEventSlim(); Task pending;
            using(var writer=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(fixture.DirectoryPath,"articles.sqlite"),Pooling=false }.ToString()))
            {
                writer.Open(); using var transaction=writer.BeginTransaction();
                using(var command=writer.CreateCommand()) { command.Transaction=transaction; command.CommandText="UPDATE articles SET payload=payload"; command.ExecuteNonQuery(); }
                var helper=new ExportPreparation(fixture.Repository,(a,s,t)=>throw new InvalidOperationException("No request expected."));
                pending=Task.Run(()=> { entered.Set(); return helper.PrepareAsync(original,Session(),ExportFormat.Audio,stop.Token); });
                Check(entered.Wait(TimeSpan.FromSeconds(5)),"写锁取消测试没有启动");
                await Task.Delay(100); Check(!pending.IsCompleted,"音频写入未等待现有数据库事务");
                stop.Cancel(); transaction.Rollback();
            }
            await ExpectCanceled(pending);
            Check(JToken.DeepEquals(before,Metadata(fixture.Repository,original)),"等待写锁中取消仍提交音频扫描结果");
        }
        static void MetadataMergeRetainsTheCompletedAudioScan()
        {
            using var fixture=new Fixture(); var complete=Scanned(Article(RawHtml)); fixture.Repository.Save(complete);
            var listing=Article(""); listing.Status=ArticleStatus.Pending; listing.AudioMetadataVersion=0;
            fixture.Repository.Save(listing);
            var restored=fixture.Repository.ReadExportMetadata(complete.Biz,complete.Id);
            Check(restored.AudioMetadataVersion==1 && restored.Media.Any(m=>m.Kind==MediaKind.Audio),"新历史列表覆盖已完成音频扫描及资源");
            var noAudio=Article(RawHtml); noAudio.Media.Clear(); noAudio.AudioMetadataVersion=1; fixture.Repository.Save(noAudio);
            restored=fixture.Repository.ReadExportMetadata(complete.Biz,complete.Id);
            Check(restored.AudioMetadataVersion==1 && !restored.Media.Any(m=>m.Kind==MediaKind.Audio),"确定无音频的新扫描被旧媒体回退逻辑覆盖");
            Check(JsonConvert.DeserializeObject<ArticleRecord>("{\"Id\":\"old\"}").AudioMetadataVersion==0,"旧记录没有进入需要补扫的版本");
        }
        static async Task NonAudioExportKeepsOrdinaryPreparation()
        {
            using var fixture=new Fixture(); var original=Article(NormalizedHtml); fixture.Repository.Save(original); int calls=0;
            var helper=new ExportPreparation(fixture.Repository,(a,s,t)=> { calls++; return Task.FromResult(Scanned(a)); });
            var text=await helper.PrepareAsync(original,Session(),ExportFormat.Text,CancellationToken.None);
            var image=await helper.PrepareAsync(original,Session(),ExportFormat.Images,CancellationToken.None);
            Check(calls==0 && text.Html==NormalizedHtml && image.Media.Any(m=>m.Kind==MediaKind.Image),"非音频导出无故刷新已有正文/图片");
        }
        static async Task ExistingAudioInNormalizedCacheDoesNotRequireRefresh()
        {
            using var fixture=new Fixture(); var original=Article(NormalizedHtml);
            original.Media.Add(new MediaAsset { Kind=MediaKind.Audio,Url="https://audio.invalid/already-known.mp3" });
            fixture.Repository.Save(original); var before=Metadata(fixture.Repository,original); int calls=0;
            var helper=new ExportPreparation(fixture.Repository,(a,s,t)=> { calls++; throw new IOException("fixture expired session"); });
            foreach(var format in new[]{ExportFormat.Audio,ExportFormat.AllMedia})
            {
                var prepared=await helper.PrepareAsync(original,Session(),format,CancellationToken.None);
                Check(prepared.Status==ArticleStatus.Available && prepared.AudioMetadataVersion==0
                    && prepared.Media.Any(m=>m.Kind==MediaKind.Audio && m.Url.EndsWith("already-known.mp3")),"已知音频被强制补扫失败阻断或伪造扫描完成");
            }
            Check(calls==0 && JToken.DeepEquals(before,Metadata(fixture.Repository,original)),"已有音频仍请求失效会话或改写旧扫描版本");
        }
        static async Task AllMediaKeepsExistingAssetsWhenAudioRecoveryFails()
        {
            using var fixture=new Fixture(); var original=Article(NormalizedHtml);
            original.Media.Add(new MediaAsset { Kind=MediaKind.Video,Url="https://video.invalid/available.mp4" });
            fixture.Repository.Save(original); var before=Metadata(fixture.Repository,original);
            var helper=new ExportPreparation(fixture.Repository,(a,s,t)=>
            { a.Status=ArticleStatus.Restricted; a.StatusDetail="fixture 访问受限"; a.Media.Clear(); a.Html=""; return Task.FromResult(a); });
            var all=await helper.PrepareAsync(original,Session(),ExportFormat.AllMedia,CancellationToken.None);
            Check(all.Status==ArticleStatus.Available && all.AudioMetadataVersion==0
                && all.Media.Count(m=>m.Kind==MediaKind.Image || m.Kind==MediaKind.Video)==2
                && all.Media.Single(m=>m.Kind==MediaKind.Audio).UnavailableReason.Contains("访问受限"),"音频失败阻断其他已有媒体，或没有单独报告音频失败");
            var audio=await helper.PrepareAsync(original,Session(),ExportFormat.Audio,CancellationToken.None);
            Check(audio.Status==ArticleStatus.Restricted && JToken.DeepEquals(before,Metadata(fixture.Repository,original))
                && fixture.Repository.LoadBody(original.Id)==NormalizedHtml,"临时音频诊断落入缓存或纯音频导出误报成功");
        }
        static ArticleRecord Article(string html)=>new ArticleRecord { Id="audio-fixture:101:1",Biz="audio-fixture",Mid="101",Idx=1,
            Url="https://mp.weixin.qq.com/s?__biz=audio-fixture&mid=101&idx=1",Title="音频缓存测试",Author="fixture author",Html=html,
            Status=ArticleStatus.Available,ReadCount=123,LikeCount=45,FavoriteCount=6,ShareCount=7,
            Media=new List<MediaAsset> { new MediaAsset { Kind=MediaKind.Image,Url="https://image.invalid/p.png" } } };
        static ArticleRecord Scanned(ArticleRecord original)
        {
            var result=JObject.FromObject(original).ToObject<ArticleRecord>(); result.AudioMetadataVersion=1; result.Status=ArticleStatus.Available; result.Html=RawHtml;
            result.Media=new List<MediaAsset> { new MediaAsset { Kind=MediaKind.Audio,Id="synthetic-audio",Url="https://audio.invalid/test.mp3" } }; return result;
        }
        static AccountSession Session()=>new AccountSession { Biz="audio-fixture",Name="fixture" };
        static JObject Metadata(ArticleRepository repo,ArticleRecord article)=>JObject.FromObject(repo.ReadExportMetadata(article.Biz,article.Id));
        static void StripAudio(JObject value)
        {
            value.Remove("AudioMetadataVersion");
            value["Media"]=new JArray(((JArray)value["Media"]).Where(m=>(int)m["Kind"]!=(int)MediaKind.Audio));
        }
        static async Task ExpectCanceled(Task task)
        {
            Check(await Task.WhenAny(task,Task.Delay(5000))==task,"取消没有结束");
            try { await task; } catch(OperationCanceledException) { return; }
            throw new InvalidOperationException("Audio cache test expected cancellation.");
        }
        static void Check(bool value,string message) { if(!value)throw new InvalidOperationException("Audio cache self-test: "+message); }
        sealed class Fixture : IDisposable
        {
            public string DirectoryPath { get; }=Path.Combine(Path.GetTempPath(),"WCAE-audio-cache-tests-"+Guid.NewGuid().ToString("N"));
            public ArticleRepository Repository { get; }
            public Fixture() { Repository=new ArticleRepository(DirectoryPath); }
            public void Dispose() { if(Directory.Exists(DirectoryPath))Directory.Delete(DirectoryPath,true); }
        }
    }
}
