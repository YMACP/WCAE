using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public sealed class ExportPreparation
    {
        readonly ArticleRepository repository;
        readonly Func<ArticleRecord,AccountSession,CancellationToken,Task<ArticleRecord>> enrich;
        public ExportPreparation(ArticleRepository repository,Func<ArticleRecord,AccountSession,CancellationToken,Task<ArticleRecord>> enrich)
        {
            this.repository=repository??throw new ArgumentNullException(nameof(repository));
            this.enrich=enrich??throw new ArgumentNullException(nameof(enrich));
        }
        public async Task<ArticleRecord> PrepareAsync(ArticleRecord article,AccountSession session,ExportFormat format,CancellationToken token)
        {
            if(article==null)throw new ArgumentNullException(nameof(article));
            token.ThrowIfCancellationRequested();
            var persisted=repository.ReadExportMetadata(article.Biz,article.Id);
            var original=Clone(persisted??article);
            if(original.Status==ArticleStatus.Deleted || NativeProfileTextSnapshot.IsSnapshot(original))return original;
            original.Html=persisted!=null ? repository.LoadBody(original.Id) : original.Html;
            token.ThrowIfCancellationRequested();
            bool fullExport=format==ExportFormat.Full;
            bool audioExport=format==ExportFormat.Audio || format==ExportFormat.AllMedia || fullExport;
            bool hasAudio=(original.Media??new List<MediaAsset>()).Any(m=>m?.Kind==MediaKind.Audio && Ready(m));
            bool audioScanNeeded=audioExport && original.AudioMetadataVersion<1 && !hasAudio;
            bool hasBody=!string.IsNullOrWhiteSpace(original.Html);
            bool preserveExisting=audioExport && persisted!=null && original.Status==ArticleStatus.Available && hasBody;
            if(audioExport && hasAudio && (!fullExport || hasBody))return original; // Full export also needs its HTML body.

            if(audioScanNeeded && hasBody)
            {
                ArticleRecord scanned=null;
                try { scanned=ArticleEnricher.ParseHtml(Clone(original),original.Html); }
                catch(OperationCanceledException) { throw; }
                catch(Exception) { /* An incomplete cached page needs a fresh response. */ }
                token.ThrowIfCancellationRequested();
                if(IsAudioScan(scanned,original))
                    return PersistAudio(original,scanned,preserveExisting,token);
            }
            // Version 1 also means a successful scan found no audio. An empty media list
            // after that scan is not a reason to re-fetch on each Audio/AllMedia export.
            bool ordinaryMediaRefresh=!audioExport && format>=ExportFormat.Images && (original.Media?.Count??0)==0;
            if(hasBody && !audioScanNeeded && !ordinaryMediaRefresh)return original;
            if(string.IsNullOrWhiteSpace(original.Url))
                return AudioFailure(original,format,Failure(original,"缓存没有完成音频/正文解析，且缺少可刷新文章链接。"));
            if(session!=null && !string.IsNullOrEmpty(session.Biz) && session.Biz!=original.Biz)
                return Failure(original,"导出会话与当前公众号不一致，未刷新文章。");
            ArticleRecord refreshed;
            try
            {
                var request=Clone(original);
                // A retry must prove this scan itself; stale metadata must not certify it.
                if(audioExport)request.AudioMetadataVersion=0;
                refreshed=await enrich(request,session,token).ConfigureAwait(false);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
            catch(Exception ex)
            {
                token.ThrowIfCancellationRequested();
                return AudioFailure(original,format,Failure(original,"文章音频/正文更新失败（"+ex.GetType().Name+"），原缓存已保留。"));
            }
            token.ThrowIfCancellationRequested();
            if(refreshed==null)return AudioFailure(original,format,Failure(original,"文章音频/正文更新未返回结果，原缓存已保留。"));
            if(refreshed.Biz!=original.Biz || refreshed.Id!=original.Id)
                return Failure(original,"刷新结果的公众号或文章身份不一致，原缓存已保留。");
            if(refreshed.Status!=ArticleStatus.Available)return AudioFailure(original,format,refreshed); // Never replace the saved body/status.
            if(audioExport && !IsAudioScan(refreshed,original))
            {
                // Full export can still use a newly obtained body and images when only
                // the audio scan is incomplete. Keep that partial result export-only.
                if(fullExport && !hasBody && !string.IsNullOrWhiteSpace(refreshed.Html))
                    return AudioFailure(refreshed,format,Failure(original,"音频信息尚未完整识别，已保留本次取得的正文和其他媒体。"));
                return AudioFailure(original,format,Failure(original,"尚未取得可完整扫描的原始音频信息，原缓存已保留；请刷新原文后重试。"));
            }
            if(string.IsNullOrWhiteSpace(refreshed.Html))
                return AudioFailure(original,format,Failure(original,"文章更新没有返回有效正文，原缓存已保留。"));
            if(audioExport && preserveExisting)return PersistAudio(original,refreshed,true,token);
            repository.Save(refreshed,token);
            return refreshed;
        }
        ArticleRecord PersistAudio(ArticleRecord original,ArticleRecord scanned,bool preserveExisting,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(preserveExisting)
            {
                var stored=repository.SaveAudioMetadata(scanned,token);
                stored.Html=original.Html;
                return stored;
            }
            var result=Clone(original);
            ApplyAudioMetadata(result,scanned);
            result.Status=scanned.Status;
            if(original.Status!=scanned.Status)result.StatusDetail=scanned.StatusDetail;
            if(string.IsNullOrWhiteSpace(result.Html))result.Html=scanned.Html;
            repository.Save(result,token);
            return result;
        }
        static bool IsAudioScan(ArticleRecord scanned,ArticleRecord original)
            => scanned!=null && scanned.Biz==original.Biz && scanned.Id==original.Id
                && scanned.Status==ArticleStatus.Available && scanned.AudioMetadataVersion>=1;
        internal static void ApplyAudioMetadata(ArticleRecord target,ArticleRecord scanned)
        {
            target.Media=(target.Media??new List<MediaAsset>()).Where(m=>m!=null && m.Kind!=MediaKind.Audio)
                .Concat((scanned.Media??new List<MediaAsset>()).Where(m=>m!=null && m.Kind==MediaKind.Audio))
                .Select(m=>JObject.FromObject(m).ToObject<MediaAsset>()).ToList();
            target.AudioMetadataVersion=scanned.AudioMetadataVersion;
        }
        static bool Ready(MediaAsset asset)=>asset!=null && !string.IsNullOrWhiteSpace(asset.Url) && string.IsNullOrWhiteSpace(asset.UnavailableReason);
        static ArticleRecord AudioFailure(ArticleRecord original,ExportFormat format,ArticleRecord failure)
        {
            bool fullExport=format==ExportFormat.Full;
            if((format!=ExportFormat.AllMedia && !fullExport) || original.Status!=ArticleStatus.Available
                || (!(original.Media??new List<MediaAsset>()).Any(Ready) && !(fullExport && !string.IsNullOrWhiteSpace(original.Html))))return failure;
            var result=Clone(original);
            if(fullExport && string.IsNullOrWhiteSpace(result.Html))
            { result.Status=failure.Status; result.StatusDetail=failure.StatusDetail; }
            result.Media=(result.Media??new List<MediaAsset>()).Where(m=>m!=null && (m.Kind!=MediaKind.Audio || Ready(m))).ToList();
            if(!result.Media.Any(m=>m.Kind==MediaKind.Audio && Ready(m)))
                result.Media.Add(new MediaAsset { Kind=MediaKind.Audio,Id="wcae-audio-recovery",Name="音频信息补全",
                    UnavailableReason="音频信息补全失败："+failure.StatusText+(string.IsNullOrWhiteSpace(failure.StatusDetail)?"":"；"+failure.StatusDetail) });
            // This diagnostic is export-only. Existing assets and version 0 remain durable,
            // so a later request can try audio recovery with a usable session.
            return result;
        }
        static ArticleRecord Failure(ArticleRecord original,string reason)
        { var result=Clone(original); result.Status=ArticleStatus.Failed; result.StatusDetail=reason; return result; }
        static ArticleRecord Clone(ArticleRecord article)=>JObject.FromObject(article).ToObject<ArticleRecord>();
    }
}
