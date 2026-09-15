using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public static class CollectionPipelineSelfTests
    {
        public static async Task RunAsync()
        {
            await EachPageDetailsBeforeFollowingPageAndBoundedFailure();
            await StopDuringPageNotificationKeepsWholePage();
            await ResumePendingSkipsCompletedThenNewRunRefreshes();
            await StopBetweenPagesUsesSavedNextOffset();
            await StopOnFailedArticleRetainsCurrentItem();
            await ListRetriesAreBoundedAndServerRejectionPauses();
            await RepeatedWholePageDoesNotPretendToAdvance();
            await RestrictedDetailsPauseWithQueueIntact();
            await MetricsRateLimitKeepsBodyAndPauses();
            await MissingBodyCannotCountAsCompleted();
            HistoryIdentityDoesNotInventArticleMid();
        }

        private static async Task EachPageDetailsBeforeFollowingPageAndBoundedFailure()
        {
            using var fixture = new StoreFixture();
            var offsets = new List<int>();
            var transport = new FakeTransport((url, session, token) =>
            {
                int offset = Offset(url); offsets.Add(offset);
                if(offset==10)Check(fixture.Repository.Load(Biz).Any(a=>a.Mid=="101" && a.Status==ArticleStatus.Available)
                    && fixture.Repository.Load(Biz).Any(a=>a.Mid=="102" && a.Status==ArticleStatus.Failed),"当前页文章尚未检查就提前读取下一页");
                session.Cookie = "fixture=page-" + offset;
                return Task.FromResult(offset == 0
                    ? Page(true, 10, Message("101"), Message("102"))
                    : Page(false, 20, Message("102"), Message("103"), Message("104")));
            });
            var attempts = new Dictionary<string, int>();
            using var controller = Controller(transport, fixture.Repository, (article, session, token) =>
            {
                bool firstPage=article.Mid=="101" || article.Mid=="102";
                Check(offsets.SequenceEqual(firstPage?new[]{0}:new[]{0,10}),"逐篇检查顺序与当前页位置不符");
                Check(fixture.Repository.Load(Biz).Count==(firstPage?2:4),"当前页没有先完整入库或跨页出现重复");
                Check(session.Cookie == (firstPage?"fixture=page-0":"fixture=page-10"), "当前页会话更新未传给顺序详情请求");
                attempts[article.Mid] = attempts.GetValueOrDefault(article.Mid) + 1;
                if (article.Mid == "102")
                {
                    article.Status = ArticleStatus.Failed; article.StatusDetail = "fixture timeout";
                    return Task.FromResult(article); // Production enrichment also returns Failed rather than throwing.
                }
                return Task.FromResult(Available(article));
            });
            await controller.StartAsync(null);
            Check(attempts["102"] == 2 && attempts["101"] == 1 && attempts["103"] == 1 && attempts["104"] == 1,
                "单篇失败未有界重试，或阻挡了后续正文");
            Check(fixture.Repository.PendingCount(Biz) == 1 && fixture.Repository.GetPendingArticles(Biz).Single().Mid == "102", "失败项未保留待补全队列");
            Check(fixture.Repository.Load(Biz).Count(x => x.Status == ArticleStatus.Available) == 3, "成功正文未独立完成");
            Check(controller.LastSummary.Contains("已到末页") && controller.LastSummary.Contains("重试后失败 1") && controller.LastSummary.Contains("待补全 1"), "清单完成与正文失败汇总未区分");
        }

        private static async Task StopDuringPageNotificationKeepsWholePage()
        {
            using var fixture = new StoreFixture();
            int details = 0;
            var transport = new FakeTransport((url, session, token) => Task.FromResult(Page(true, 10, Message("201"), Message("202", true), Message("203"))));
            using (var controller = Controller(transport, fixture.Repository, (article, session, token) =>
            {
                details++; return Task.FromResult(Available(article));
            }))
            {
                controller.ArticleSaved += article => controller.Stop();
                await ExpectCancelled(controller.StartAsync(null));
                Check(fixture.Repository.Load(Biz).Count == 4 && fixture.Repository.PendingCount(Biz) == 4, "首次通知时停止丢失了同页后续记录或多图文子项");
                var checkpoint = fixture.Repository.GetCollectionCheckpoint(Biz);
                Check(checkpoint.CurrentOffset == 0 && checkpoint.NextOffset == 10 && !checkpoint.HistoryComplete, "停止前完整页面和游标没有原子保留");
                Check(details == 0 && transport.Requests == 1, "清单被停止后仍处理详情或请求下一页");
            }

            // Resume the durable page buffer first, then request only its saved next offset.
            var offsets = new List<int>();
            var resumed = new FakeTransport((url, session, token) =>
            {
                int offset = Offset(url); offsets.Add(offset);
                Check(fixture.Repository.PendingCount(Biz)==0,"旧页剩余正文尚未完成就提前取下一页");
                return Task.FromResult(Page(false,30,Message("203"),Message("204")));
            });
            using var restarted = Controller(resumed, fixture.Repository, (article, session, token) => Task.FromResult(Available(article)));
            await restarted.StartAsync(null);
            Check(offsets.SequenceEqual(new[] { 10 }), "停止后未从保存的下一页位置继续，或错误回到顶部");
            Check(fixture.Repository.Load(Biz).Count == 5 && fixture.Repository.PendingCount(Biz) == 0, "恢复遗漏多图文子项、旧页剩余正文或后续文章");
        }

        private static async Task ResumePendingSkipsCompletedThenNewRunRefreshes()
        {
            using var fixture = new StoreFixture();
            var transport = new FakeTransport((url, session, token) => Task.FromResult(Page(false, 10, Message("301"), Message("302"), Message("303"))));
            var attempts = new Dictionary<string, int>();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var controller = Controller(transport, fixture.Repository, async (article, session, token) =>
            {
                attempts[article.Mid] = attempts.GetValueOrDefault(article.Mid) + 1;
                if (article.Mid == "302")
                {
                    entered.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                }
                return Available(article, 10);
            }))
            {
                Task running = controller.StartAsync(null);
                Check(await Task.WhenAny(entered.Task, Task.Delay(3000)) == entered.Task, "取消测试未到达详情等待点");
                controller.Stop();
                await ExpectCancelled(running);
                Check(fixture.Repository.PendingCount(Biz) == 2 && fixture.Repository.Load(Biz).Count == 3, "停止详情时未保留整页待处理任务");
                Check(controller.LastSummary.Contains("已停止") && controller.LastSummary.Contains("已到末页"), "停止未区分已经完成的清单阶段");
            }
            using (var resumed = Controller(transport, fixture.Repository, (article, session, token) =>
            {
                attempts[article.Mid] = attempts.GetValueOrDefault(article.Mid) + 1;
                return Task.FromResult(Available(article, 20));
            }))
            {
                await resumed.StartAsync(null);
                Check(attempts["301"] == 1 && attempts["302"] == 2 && attempts["303"] == 1, "恢复未跳过本轮已成功正文或没有补全未完成项");
                Check(fixture.Repository.PendingCount(Biz) == 0, "恢复完成后仍残留待处理项");
                await resumed.StartAsync(null);
                Check(attempts["301"] == 2 && attempts["302"] == 3 && attempts["303"] == 2, "已完成后的新一轮没有刷新正文/指标");
                Check(fixture.Repository.Load(Biz).All(x => x.ReadCount == 20), "新一轮指标未更新");
            }
        }

        private static async Task StopBetweenPagesUsesSavedNextOffset()
        {
            using var fixture=new StoreFixture();
            var offsets=new List<int>();
            var transport=new FakeTransport((url,session,token)=>
            {
                int offset=Offset(url); offsets.Add(offset);
                return Task.FromResult(offset==0?Page(true,24,Message("350")):Page(false,30,Message("351")));
            });
            using(var controller=Controller(transport,fixture.Repository,(article,session,token)=>Task.FromResult(Available(article))))
            {
                controller.ArticleSaved+=article=>{if(article.Status==ArticleStatus.Available)controller.Stop();};
                await ExpectCancelled(controller.StartAsync(null));
                Check(offsets.SequenceEqual(new[]{0}) && fixture.Repository.PendingCount(Biz)==0,"停止后仍请求下一页或未保存刚完成文章");
            }
            using(var resumed=Controller(transport,new ArticleRepository(fixture.DirectoryPath),(article,session,token)=>Task.FromResult(Available(article))))
            {
                await resumed.StartAsync(null);
                Check(offsets.SequenceEqual(new[]{0,24}),"页面交界停止后没有跨进程恢复保存的下一页位置");
            }
        }

        private static async Task StopOnFailedArticleRetainsCurrentItem()
        {
            using var fixture = new StoreFixture();
            var transport = new FakeTransport((url, session, token) => Task.FromResult(Page(false, 10, Message("360"), Message("361"))));
            using (var controller = Controller(transport, fixture.Repository, (article, session, token) =>
            {
                article.Status = ArticleStatus.Failed;
                article.StatusDetail = "fixture failure";
                return Task.FromResult(article);
            }))
            {
                controller.MaximumDetailAttempts = 1;
                controller.ArticleSaved += article => { if (article.Status == ArticleStatus.Failed) controller.Stop(); };
                await ExpectCancelled(controller.StartAsync(null));
                Check(fixture.Repository.GetPendingArticles(Biz).First().Mid == "360", "失败回调中停止仍推进到下一篇，丢失当前断点");
            }
            var order = new List<string>();
            using var resumed = Controller(transport, new ArticleRepository(fixture.DirectoryPath), (article, session, token) =>
            {
                order.Add(article.Mid);
                return Task.FromResult(Available(article));
            });
            await resumed.StartAsync(null);
            Check(order.SequenceEqual(new[] { "360", "361" }) && transport.Requests == 1,
                "失败处停止后没有先继续当前文章，或错误重新读取首页");
        }

        private static async Task ListRetriesAreBoundedAndServerRejectionPauses()
        {
            using var fixture = new StoreFixture();
            int failures = 0, details = 0;
            var transport = new FakeTransport((url, session, token) =>
            {
                if (failures++ < 2) throw new TimeoutException("fixture");
                return Task.FromResult(Page(false, 10, Message("401")));
            });
            using (var controller = Controller(transport, fixture.Repository, (article, session, token) =>
            {
                details++; return Task.FromResult(Available(article));
            }))
            {
                await controller.StartAsync(null);
                Check(transport.Requests == 3 && details == 1, "列表临时错误未按限次重试后继续");
            }
            var broken = new FakeTransport((url, session, token) => throw new IOException("fixture offline"));
            using (var controller = Controller(broken, fixture.Repository, (article, session, token) => Task.FromResult(Available(article))))
            {
                await ExpectPaused(controller.StartAsync(null));
                Check(broken.Requests == 3 && controller.LastSummary.Contains("尚未确认末页"), "连续网络失败无界重试或误报清单完成");
            }
            foreach (string response in new[] { "{\"ret\":-3}", "<html>login</html>" })
            {
                var denied = new FakeTransport((url, session, token) => Task.FromResult(response));
                using var controller = Controller(denied, fixture.Repository, (article, session, token) => throw new Exception("不应启动详情"));
                await ExpectPaused(controller.StartAsync(null));
                Check(denied.Requests == 1, "明确会话/接口拒绝被自动重复请求");
            }
            var rateLimited = new FakeTransport((url, session, token) => throw new HttpRequestFailureException((HttpStatusCode)429, "fixture"));
            using (var controller = Controller(rateLimited, fixture.Repository, (article, session, token) => throw new Exception("不应启动详情")))
            {
                await ExpectPaused(controller.StartAsync(null));
                Check(rateLimited.Requests == 1, "HTTP 429 未立即暂停");
            }
        }

        private static async Task RestrictedDetailsPauseWithQueueIntact()
        {
            using var fixture = new StoreFixture();
            var transport = new FakeTransport((url, session, token) => Task.FromResult(Page(false, 10, Message("501"), Message("502"))));
            int attempts = 0;
            using var controller = Controller(transport, fixture.Repository, (article, session, token) =>
            {
                attempts++; article.Status = ArticleStatus.Restricted; article.StatusDetail = "文章请求被拒绝或受限（HTTP 429）";
                return Task.FromResult(article);
            });
            await ExpectPaused(controller.StartAsync(null));
            Check(attempts == 1 && fixture.Repository.PendingCount(Biz) == 2, "受限详情没有立即暂停或丢失后续队列");
            Check(fixture.Repository.Load(Biz).Any(x => x.Status == ArticleStatus.Restricted), "受限原因未落库");
        }

        private static async Task RepeatedWholePageDoesNotPretendToAdvance()
        {
            using var fixture = new StoreFixture();
            var transport = new FakeTransport((url, session, token) => Task.FromResult(Page(true, Offset(url) + 10, Message("550"), Message("551"))));
            int details=0;
            using var controller = Controller(transport, fixture.Repository, (article, session, token) => { details++; return Task.FromResult(Available(article)); });
            await ExpectPaused(controller.StartAsync(null));
            Check(transport.Requests == 2 && details==2 && fixture.Repository.PendingCount(Biz) == 0 && fixture.Repository.Load(Biz).Count==2, "相同整页加递增 offset 导致无限分页，重复检查或丢失已保存记录");
            Check(!fixture.Repository.GetCollectionCheckpoint(Biz).HistoryComplete, "重复整页被错误当成真正末页");
        }

        private static async Task MissingBodyCannotCountAsCompleted()
        {
            using var fixture = new StoreFixture();
            var transport = new FakeTransport((url, session, token) => Task.FromResult(Page(false, 10, Message("560"))));
            int attempts = 0;
            using var controller = Controller(transport, fixture.Repository, (article, session, token) =>
            {
                attempts++; article.Status = ArticleStatus.Available; article.Html = ""; return Task.FromResult(article);
            });
            await controller.StartAsync(null);
            Check(attempts == 2 && fixture.Repository.PendingCount(Biz) == 1, "无正文的成功标记被错误移出队列");
            Check(controller.LastSummary.Contains("正文成功 0") && controller.LastSummary.Contains("已删除 0") && controller.LastSummary.Contains("重试后失败 1"), "仓储拒绝假成功后汇总仍误报成功或删除");
        }

        private static async Task MetricsRateLimitKeepsBodyAndPauses()
        {
            using var fixture = new StoreFixture();
            var first = Message("570");
            first["app_msg_ext_info"]["content_url"] = first["app_msg_ext_info"]["content_url"].ToString() + "&sn=fixture-signature";
            int detailRequests = 0, metricRequests = 0;
            var transport = new FakeTransport((url, session, token) =>
            {
                if (new Uri(url).AbsolutePath == "/mp/profile_ext") return Task.FromResult(Page(false, 10, first, Message("571")));
                detailRequests++;
                return Task.FromResult("<meta property='og:title' content='已取得的正文'><div id='js_content'>限流前已经取得的本地测试正文</div>");
            }, (url, values, session, token) =>
            {
                metricRequests++;
                throw new HttpRequestFailureException((HttpStatusCode)429, "fixture rate limit");
            });
            var enricher = new ArticleEnricher(transport);
            using var controller = Controller(transport, fixture.Repository, enricher.EnrichAsync);
            await ExpectPaused(controller.StartAsync(null));
            Check(detailRequests == 1 && metricRequests == 1, "指标限流被重试或仍请求后续正文");
            Check(fixture.Repository.PendingCount(Biz) == 2 && fixture.Repository.LoadBody(Biz + ":570:1").Contains("限流前已经取得"), "指标限流丢失已取正文或未保留待补全队列");
        }

        private static void HistoryIdentityDoesNotInventArticleMid()
        {
            var first = Message("601");
            first["comm_msg_info"]["id"] = "other-message-id";
            var page = HistoryCollector.ParsePage(Page(false, 10, first), Session());
            Check(page.Articles.Single().Mid == "601" && page.Articles.Single().Id == Biz + ":601:1", "源消息标识覆盖了链接的真实 mid");
            Check(page.Articles.Single().SourceMessageId == "other-message-id" && !page.Articles.Single().IdentityUnresolved, "源消息标识未单独保留");
            Check(page.Articles.Single().PublishedAt == new DateTime(2023, 11, 15, 6, 13, 20), "历史发布时间没有固定使用北京时间");
            var shortMessage = Message("ignored");
            shortMessage["comm_msg_info"]["id"] = "message-only";
            shortMessage["app_msg_ext_info"]["content_url"] = "https://mp.weixin.qq.com/s/short-article#rd";
            var shortArticle = HistoryCollector.ParsePage(Page(false, 10, shortMessage), Session()).Articles.Single();
            Check(shortArticle.Mid == "" && shortArticle.Id.StartsWith("url:", StringComparison.Ordinal), "短链接用消息 id 冒充文章 mid");
            shortMessage["app_msg_ext_info"]["content_url"] = "https://mp.weixin.qq.com/s/short-article#wechat_redirect";
            Check(HistoryCollector.ParsePage(Page(false, 10, shortMessage), Session()).Articles.Single().Id == shortArticle.Id, "URL 片段制造不同文章身份");
            shortMessage["app_msg_ext_info"]["content_url"] = "";
            shortMessage["app_msg_ext_info"]["is_deleted"] = 1;
            var deleted = HistoryCollector.ParsePage(Page(false, 10, shortMessage), Session()).Articles.Single();
            Check(deleted.Mid == "" && deleted.IdentityUnresolved && deleted.Id.StartsWith("message:", StringComparison.Ordinal), "无链接删除项未使用独立消息回退身份");
        }

        private const string Biz = "pipeline-fixture-biz";
        private static AccountSession Session() => new AccountSession { Biz = Biz, Name = "流水线测试号", Cookie = "fixture=1", Uin = "12", Key = "fixture" };
        private static int Offset(string url) => int.Parse(HttpUtility.ParseQueryString(new Uri(url).Query)["offset"]);
        private static CollectionController Controller(IHttpTransport transport, ArticleRepository repository,
            Func<ArticleRecord, AccountSession, CancellationToken, Task<ArticleRecord>> enrich)
        {
            var history = new HistoryCollector(transport) { MinimumDelay = TimeSpan.Zero, MaximumDelay = TimeSpan.Zero, RetryDelay = TimeSpan.Zero };
            var controller = new CollectionController(history, enrich, repository) { DetailRetryDelay = TimeSpan.Zero };
            controller.Recognize(Session());
            return controller;
        }
        private static ArticleRecord Available(ArticleRecord article, long count = 1)
        {
            article.Status = ArticleStatus.Available; article.ReadCount = count;
            article.Html = "<div id='js_content'>本地测试正文 " + article.Mid + "</div>";
            return article;
        }
        private static JObject Message(string mid, bool child = false)
        {
            var article = new JObject { ["title"] = "测试文章 " + mid, ["content_url"] = "https://mp.weixin.qq.com/s?__biz=" + Biz + "&mid=" + mid + "&idx=1" };
            if (child) article["multi_app_msg_item_list"] = new JArray(new JObject { ["title"] = "子文章 " + mid, ["content_url"] = "https://mp.weixin.qq.com/s?__biz=" + Biz + "&mid=" + mid + "&idx=2" });
            return new JObject { ["comm_msg_info"] = new JObject { ["id"] = mid, ["datetime"] = 1700000000 }, ["app_msg_ext_info"] = article };
        }
        private static string Page(bool more, int next, params JObject[] messages) => new JObject
        {
            ["ret"] = 0, ["can_msg_continue"] = more ? 1 : 0, ["next_offset"] = next,
            ["general_msg_list"] = new JObject { ["list"] = new JArray(messages) }.ToString(Formatting.None)
        }.ToString(Formatting.None);
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("Collection pipeline self-test failed: " + message); }
        private static async Task ExpectCancelled(Task task)
        {
            Check(await Task.WhenAny(task, Task.Delay(3000)) == task, "取消未及时完成");
            try { await task; } catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("Collection pipeline self-test failed: 预期取消");
        }
        private static async Task ExpectPaused(Task task)
        {
            try { await task; } catch (CollectionPausedException) { return; }
            throw new InvalidOperationException("Collection pipeline self-test failed: 预期暂停");
        }
        private sealed class FakeTransport : IHttpTransport
        {
            private readonly Func<string, AccountSession, CancellationToken, Task<string>> get;
            private readonly Func<string, IDictionary<string, string>, AccountSession, CancellationToken, Task<string>> post;
            public int Requests { get; private set; }
            public FakeTransport(Func<string, AccountSession, CancellationToken, Task<string>> get,
                Func<string, IDictionary<string, string>, AccountSession, CancellationToken, Task<string>> post = null) { this.get = get; this.post = post; }
            public Task<string> GetStringAsync(string url, AccountSession session, CancellationToken token) { Requests++; return get(url, session, token); }
            public Task<string> PostFormAsync(string url, IDictionary<string, string> values, AccountSession session, CancellationToken token)
                => post == null ? throw new NotSupportedException() : post(url, values, session, token);
            public Task DownloadAsync(string url, string destination, AccountSession session, CancellationToken token) => throw new NotSupportedException();
        }
        private sealed class StoreFixture : IDisposable
        {
            private readonly string directory = Path.Combine(Path.GetTempPath(), "WCAE-pipeline-tests-" + Guid.NewGuid().ToString("N"));
            public string DirectoryPath=>directory;
            public ArticleRepository Repository { get; }
            public StoreFixture() { Repository = new ArticleRepository(directory); }
            public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
