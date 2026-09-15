using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    public sealed class CollectionController : IDisposable
    {
        readonly Func<AccountSession,Func<ArticleRecord,CancellationToken,Task>,IProgress<CollectionProgress>,CancellationToken,Task> collect;
        readonly Func<ArticleRecord,AccountSession,CancellationToken,Task<ArticleRecord>> enrich;
        readonly Action<ArticleRecord> save;
        readonly HistoryCollector history;
        readonly ArticleRepository repository;
        readonly IArticleSupplementSource supplementSource;
        CancellationTokenSource cancellation;
        readonly object gate = new object();
        AccountSession recognized;
        long recognitionRevision;
        public event Action<ArticleRecord> ArticleSaved;
        public event Action<AccountSession> SessionUpdated;
        public bool IsRunning { get { lock (gate) return cancellation != null; } }
        public string CollectingAccount { get; private set; } = "";
        public int MaximumDetailAttempts { get; set; } = 2;
        public TimeSpan DetailRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
        public string LastSummary { get; private set; } = "";
        public int LastProcessedCount { get; private set; }
        public int LastSucceededCount { get; private set; }
        public int LastDeletedCount { get; private set; }
        public int LastFailedCount { get; private set; }
        public CollectionController(Func<AccountSession,Func<ArticleRecord,CancellationToken,Task>,IProgress<CollectionProgress>,CancellationToken,Task> collect, Func<ArticleRecord,AccountSession,CancellationToken,Task<ArticleRecord>> enrich, Action<ArticleRecord> save)
        { this.collect = collect; this.enrich = enrich; this.save = save; }
        public CollectionController(HistoryCollector history, Func<ArticleRecord,AccountSession,CancellationToken,Task<ArticleRecord>> enrich, ArticleRepository repository, IArticleSupplementSource supplementSource = null)
        {
            this.history = history ?? throw new ArgumentNullException(nameof(history));
            this.enrich = enrich ?? throw new ArgumentNullException(nameof(enrich));
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
            save = repository.Save;
            this.supplementSource = supplementSource;
        }
        public void Recognize(AccountSession session) { lock (gate) { recognized = session.Clone(); recognitionRevision++; } }
        public void ClearRecognition()
        {
            lock (gate)
            {
                if (cancellation != null) throw new InvalidOperationException("请先停止收集并等待当前任务结束。");
                recognized = null;
                CollectingAccount = "";
            }
        }
        public async Task StartAsync(IProgress<CollectionProgress> progress, int? maxArticles = null)
        {
            if(maxArticles.HasValue && maxArticles.Value<1)throw new ArgumentOutOfRangeException(nameof(maxArticles),"收集数量必须大于零。");
            AccountSession snapshot; CancellationTokenSource run; long observedRevision;
            lock (gate)
            {
                if (cancellation != null) throw new InvalidOperationException("正在收集，请先停止当前任务。");
                if (recognized == null || string.IsNullOrEmpty(recognized.Biz)) throw new InvalidOperationException("请先在微信中打开公众号文章。");
                snapshot = recognized.Clone(); run = new CancellationTokenSource(); cancellation = run; CollectingAccount = snapshot.Name;
                observedRevision = recognitionRevision;
                LastProcessedCount=LastSucceededCount=LastDeletedCount=LastFailedCount=0; LastSummary="";
            }
            try
            {
                if (repository != null)
                {
                    await CollectPersistedAsync(snapshot, observedRevision, progress, run.Token,maxArticles).ConfigureAwait(false);
                    return;
                }
                int legacyProcessed=0;
                try
                {
                await collect(snapshot, async (article, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    save(article); ArticleSaved?.Invoke(Clone(article));
                    if(article.Status==ArticleStatus.Deleted || (article.Status==ArticleStatus.Restricted&&string.IsNullOrEmpty(article.Url)))
                    { CountProcessed(article.Status); if(maxArticles.HasValue && ++legacyProcessed>=maxArticles.Value)throw new CollectionLimitReachedException(); return; }
                    ArticleRecord result;
                    try { result = await enrich(article, snapshot, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { article.Status = ArticleStatus.Failed; article.StatusDetail = ex.Message; result = article; }
                    token.ThrowIfCancellationRequested();
                    save(result); ArticleSaved?.Invoke(Clone(result));
                    CountProcessed(result.Status);
                    if(maxArticles.HasValue && ++legacyProcessed>=maxArticles.Value)throw new CollectionLimitReachedException();
                }, progress, run.Token).ConfigureAwait(false);
                }
                catch(CollectionLimitReachedException)
                { LastSummary="已达到本轮收集数量上限。"; Report(progress,LastSummary); }
            }
            finally { lock (gate) { if (cancellation == run) cancellation = null; } run.Dispose(); SessionUpdated?.Invoke(snapshot.Clone()); }
        }

        private async Task CollectPersistedAsync(AccountSession session, long observedRevision, IProgress<CollectionProgress> progress, CancellationToken token,int? maxArticles)
        {
            if (MaximumDetailAttempts < 1 || MaximumDetailAttempts > 5 || DetailRetryDelay < TimeSpan.Zero || DetailRetryDelay > TimeSpan.FromMinutes(1))
                throw new InvalidOperationException("正文重试次数或间隔设置无效。");
            bool historyComplete = false;
            int completed = 0, deleted = 0, failed = 0, processed = 0;
            try
            {
                token.ThrowIfCancellationRequested();
                Report(progress,supplementSource==null
                    ? "当前来源为微信历史列表；它与新版公众号主页的文章范围不同，列表到达末页不代表公众号全部文章已收齐。"
                    : "开始静默收集：历史接口为主，已保存的公众号主页缓存用于补漏；接口范围和本机缓存均有限，不代表公众号全部文章。");
                token.ThrowIfCancellationRequested();
                var checkpoint=repository.BeginCollectionRun(session.Biz,token);
                if(checkpoint.RunCompleted)
                {
                    checkpoint=repository.RestartCollectionRun(session.Biz,token);
                    Report(progress,"上一轮接口可访问范围已处理结束，本次检查新增文章。");
                }
                else Report(progress,checkpoint.PagesSaved>0 ? "从上次断点继续，先检查尚未完成的文章。" : "开始逐篇检查文章，当前页处理后再读取下一页。");
                historyComplete=checkpoint.HistoryComplete;
                // Existing interrupted work keeps priority even when its rowid cursor wraps
                // around earlier bounded failures. The supplement never changes that cursor.
                var resumedOrder=repository.GetPendingArticles(session.Biz).Select((article,index)=>new { article.Id,index })
                    .ToDictionary(item=>item.Id,item=>item.index,StringComparer.Ordinal);
                if(supplementSource!=null)
                {
                    RefreshSession(session,ref observedRevision,token);
                    await ImportSupplementAsync(session,progress,token).ConfigureAwait(false);
                }
                var attempted=new HashSet<string>(StringComparer.Ordinal);
                var offsets=new HashSet<int>();
                var pageIdentities=new HashSet<string>(StringComparer.Ordinal);
                while(true)
                {
                token.ThrowIfCancellationRequested();
                // Resume the interrupted article first. A failed item is attempted at most once
                // (with bounded retries) per click, and never blocks discovery of the next page.
                var pending=repository.GetPendingArticles(session.Biz).Where(a=>!attempted.Contains(a.Id))
                    .OrderBy(a=>resumedOrder.TryGetValue(a.Id,out int order)?order:int.MaxValue).ToList();
                if(pending.Count==0)
                {
                    if(historyComplete)break;
                    checkpoint=repository.GetCollectionCheckpoint(session.Biz);
                    int offset=checkpoint?.NextOffset??0;
                    if(!offsets.Add(offset))throw new CollectionPausedException("分页位置重复，已保存断点并暂停收集。");
                    if(checkpoint?.PagesSaved>0)await history.WaitForNextPageAsync(progress,token).ConfigureAwait(false);
                    RefreshSession(session,ref observedRevision,token);
                    var page=await history.ReadPageAtAsync(session,offset,progress,token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if(page.Articles.Count>0 && !pageIdentities.Add(string.Join("\n",page.Articles.Select(a=>a.Id))))
                        throw new CollectionPausedException("微信重复返回同一整页文章，已保存断点并暂停，未确认全部收集完成。");
                    if(!page.CanContinue && page.Articles.Count==0 && offset==0 && checkpoint.PagesSaved==0)
                        throw new CollectionPausedException("微信历史接口未返回文章，尚不能确认收集完整；请刷新公众号文章后重试。");
                    var savedPage=repository.SaveCollectionPage(session.Biz,page.Articles,page.Offset,page.NextOffset,!page.CanContinue,token,skipCompleted:supplementSource!=null);
                    historyComplete=!page.CanContinue;
                    // The page buffer is durable, but only the current article is sent for details.
                    // Items whose status is already final need no redundant article request.
                    var queued=new HashSet<string>(repository.GetPendingArticles(session.Biz).Select(a=>a.Id),StringComparer.Ordinal);
                    foreach(var saved in savedPage.Where(a=>!queued.Contains(a.Id)))
                    { NotifyArticle(saved,token); }
                    continue;
                }
                for (int index = 0; index < pending.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    ArticleRecord original = pending[index];
                    attempted.Add(original.Id);
                    repository.MarkArticleStarted(session.Biz,original.Id,token);
                    if(supplementSource!=null && repository.IsCollectionArticleCompleted(session.Biz,original.Id))
                    {
                        var reused=repository.CompleteArticle(original,token);
                        if(reused.Status==ArticleStatus.Available)completed++; else if(reused.Status==ArticleStatus.Deleted)deleted++;
                        CountProcessed(reused.Status);
                        NotifyArticle(reused,token);
                        if(maxArticles.HasValue && ++processed>=maxArticles.Value)
                        { PublishSummary(progress,session.Biz,historyComplete,"已达到本轮收集数量上限",completed,deleted,failed); return; }
                        continue;
                    }
                    var checking=Clone(original); checking.Status=ArticleStatus.Pending;
                    NotifyArticle(checking,token);
                    token.ThrowIfCancellationRequested();
                    for (int attempt = 1; attempt <= MaximumDetailAttempts; attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        // Do not reuse an attempt's partially populated metrics/media on retry.
                        var article = Clone(original);
                        article.ReadCount = article.LikeCount = article.FavoriteCount = article.ShareCount = null;
                        article.Status = ArticleStatus.Pending;
                        article.StatusDetail = "";
                        ArticleRecord result;
                        Report(progress, "正在检查第 " + (completed+deleted+failed+1) + " 篇文章（第 " + attempt + "/" + MaximumDetailAttempts + " 次尝试）。");
                        RefreshSession(session,ref observedRevision,token);
                        try { result = await enrich(article, session, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (CollectionPausedException ex)
                        {
                            token.ThrowIfCancellationRequested();
                            // A metrics rate limit can arrive after the body was retrieved. Persist
                            // that body and its metadata, retaining this item in the pending queue.
                            var paused = repository.FailArticle(article, ex.Message,token);
                            NotifyArticle(paused,token);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            token.ThrowIfCancellationRequested();
                            article.Status = ArticleStatus.Failed;
                            article.StatusDetail = "正文补全失败（" + ex.GetType().Name + "）";
                            result = article;
                        }
                        token.ThrowIfCancellationRequested();
                        if (result == null)
                        {
                            article.Status = ArticleStatus.Failed; article.StatusDetail = "正文补全未返回结果。"; result = article;
                        }
                        if(result.Biz!=session.Biz || result.Id!=original.Id)
                            throw new CollectionPausedException("正文返回的文章身份与当前待办不一致，已保留断点并暂停。");
                        bool failureRecorded = false;
                        if (result.Status == ArticleStatus.Available || result.Status == ArticleStatus.Deleted)
                        {
                            result = repository.CompleteArticle(result,token);
                            if (result.Status == ArticleStatus.Available || result.Status == ArticleStatus.Deleted)
                            {
                                if (result.Status == ArticleStatus.Available) completed++; else deleted++;
                                CountProcessed(result.Status);
                                NotifyArticle(result,token);
                                break;
                            }
                            failureRecorded = true; // The store rejected an Available result without any body.
                        }
                        string error = string.IsNullOrWhiteSpace(result.StatusDetail) ? "正文尚未补全。" : result.StatusDetail;
                        if (!failureRecorded) result = repository.FailArticle(result, error,token);
                        NotifyArticle(result,token);
                        token.ThrowIfCancellationRequested();
                        if (result.Status == ArticleStatus.Restricted)
                            throw new CollectionPausedException("正文补全已暂停：" + error + " 请稍后刷新公众号文章更新会话，再开始收集；文章清单和待处理任务已保留。");
                        if (attempt == MaximumDetailAttempts) { failed++; repository.DeferArticle(session.Biz,result.Id,token); CountProcessed(ArticleStatus.Failed); break; }
                        await Task.Delay(DetailRetryDelay, token).ConfigureAwait(false);
                    }
                    if(maxArticles.HasValue && ++processed>=maxArticles.Value)
                    { PublishSummary(progress,session.Biz,historyComplete,"已达到本轮收集数量上限",completed,deleted,failed); return; }
                }
                }
                token.ThrowIfCancellationRequested();
                repository.FinishCollectionRun(session.Biz, token);
                PublishSummary(progress, session.Biz, historyComplete, "本轮接口可访问范围处理结束", completed, deleted, failed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                PublishSummary(progress, session.Biz, historyComplete, "已停止", completed, deleted, failed);
                throw;
            }
            catch (Exception)
            {
                PublishSummary(progress, session.Biz, historyComplete, "已暂停", completed, deleted, failed);
                throw;
            }
        }

        private void PublishSummary(IProgress<CollectionProgress> progress, string biz, bool historyComplete, string state, int completed, int deleted, int failed)
        {
            int pending = repository.PendingCount(biz);
            LastSummary = state + "：" + (historyComplete ? "历史接口文章清单已到末页" : "历史接口文章清单尚未确认末页")
                + "；本轮正文成功 " + completed + " 篇，已删除 " + deleted + " 篇，重试后失败 " + failed + " 篇，待补全 " + pending + " 篇。"
                + (pending > 0 || !historyComplete ? "再次开始将从当前断点继续。" : "正文状态与各项指标以实际返回结果为准。")
                + (supplementSource!=null ? "主页缓存仅为本机已加载快照，以上结果不代表公众号全部历史文章。" : "");
            Report(progress, LastSummary);
        }

        private static void Report(IProgress<CollectionProgress> progress, string message)
            => progress?.Report(new CollectionProgress { Message = message });
        async Task ImportSupplementAsync(AccountSession session,IProgress<CollectionProgress> progress,CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var snapshot=await supplementSource.ReadAsync(session.Clone(),token).WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if(snapshot==null) { Report(progress,"未取得可用的公众号主页缓存，继续历史接口收集。"); return; }
                if(!string.IsNullOrWhiteSpace(snapshot.Message))Report(progress,snapshot.Message);
                token.ThrowIfCancellationRequested();
                var saved=repository.SaveCollectionSupplement(session.Biz,snapshot.Articles??Array.Empty<ArticleRecord>(),token);
                Report(progress,"主页缓存已核对 " + saved.Count + " 篇；已有正文和已删除记录复用，其余补充待办已保存。");
                var queued=new HashSet<string>(repository.GetPendingArticles(session.Biz).Select(a=>a.Id),StringComparer.Ordinal);
                foreach(var article in saved.Where(a=>!queued.Contains(a.Id)))NotifyArticle(article,token);
            }
            catch(OperationCanceledException) { throw; }
            catch(Exception ex)
            {
                token.ThrowIfCancellationRequested();
                Report(progress,"主页缓存补漏暂不可用（" + ex.GetType().Name + "），继续历史接口收集。");
            }
        }
        void RefreshSession(AccountSession session,ref long observedRevision,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AccountSession latest;
            lock(gate)
            {
                if(recognitionRevision==observedRevision)return;
                observedRevision=recognitionRevision;
                latest=recognized?.Clone();
            }
            if(latest==null || latest.Biz!=session.Biz || latest.CapturedAt<session.CapturedAt
                || (!string.IsNullOrEmpty(session.UserName) && !string.IsNullOrEmpty(latest.UserName) && latest.UserName!=session.UserName))return;
            token.ThrowIfCancellationRequested();
            session.Name=AccountNameResolver.Choose(session.Biz,latest.Name,session.Name);
            if(!string.IsNullOrWhiteSpace(latest.UserName))session.UserName=latest.UserName;
            if(!string.IsNullOrWhiteSpace(latest.Cookie))session.Cookie=latest.Cookie;
            if(!string.IsNullOrWhiteSpace(latest.UserAgent))session.UserAgent=latest.UserAgent;
            if(!string.IsNullOrWhiteSpace(latest.RequestUrl))session.RequestUrl=latest.RequestUrl;
            if(!string.IsNullOrWhiteSpace(latest.Uin))session.Uin=latest.Uin;
            if(!string.IsNullOrWhiteSpace(latest.Key))session.Key=latest.Key;
            if(!string.IsNullOrWhiteSpace(latest.PassTicket))session.PassTicket=latest.PassTicket;
            if(!string.IsNullOrWhiteSpace(latest.AppMsgToken))session.AppMsgToken=latest.AppMsgToken;
            if(latest.Headers!=null)foreach(var pair in latest.Headers)session.Headers[pair.Key]=pair.Value;
            session.CapturedAt=latest.CapturedAt;
        }
        void NotifyArticle(ArticleRecord article,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ArticleSaved?.Invoke(Clone(article));
            token.ThrowIfCancellationRequested();
        }
        public void Stop() { lock (gate) cancellation?.Cancel(); }
        void CountProcessed(ArticleStatus status)
        {
            LastProcessedCount++;
            if(status==ArticleStatus.Available)LastSucceededCount++;
            else if(status==ArticleStatus.Deleted)LastDeletedCount++;
            else LastFailedCount++;
        }
        sealed class CollectionLimitReachedException : Exception { }
        static ArticleRecord Clone(ArticleRecord article)
        {
            var metadata=Newtonsoft.Json.Linq.JObject.FromObject(article); metadata["Html"]="";
            return metadata.ToObject<ArticleRecord>();
        }
        public void Dispose() { Stop(); }
    }
}
