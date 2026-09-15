using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    public static class SelfTests
    {
        static void Check(bool condition,string message) { if(!condition)throw new Exception(message); }
        public static async Task<string> RunAsync()
        {
            var report=new StringBuilder();
            var root=Path.Combine(Path.GetTempPath(),"WCAE-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            AppPaths.DataDirectory=root;
            var id1=ArticleIdentity.Create("biz","mid",1,"https://mp.weixin.qq.com/s?sn=a");
            var id2=ArticleIdentity.Create("biz","mid",2,"https://mp.weixin.qq.com/s?sn=a");
            Check(id1!=id2,"多图文子文章标识重复");
            Check(ArticleIdentity.Create("","",1,"https://mp.weixin.qq.com/s?__biz=biz&mid=mid&idx=1&key=secret")==id1,"链接标识归一化失败");
            var repo=new ArticleRepository(root);
            repo.SaveAccount("biz","测试号");
            var articles=new List<ArticleRecord>{
                new ArticleRecord{Id=id1,Biz="biz",Title="同一个标题",PublishedAt=new DateTime(2026,1,1,23,59,0),ReadCount=0,LikeCount=50,Columns=new List<ColumnInfo>{new ColumnInfo{Name="栏目A"}},Status=ArticleStatus.Available},
                new ArticleRecord{Id=id2,Biz="biz",Title="同一个标题",PublishedAt=new DateTime(2026,1,2),ReadCount=100,LikeCount=3,Columns=new List<ColumnInfo>{new ColumnInfo{Name="栏目A"},new ColumnInfo{Name="栏目B"}},Status=ArticleStatus.Available},
                new ArticleRecord{Id="unknown",Biz="biz",Title="未知指标",ReadCount=null,Status=ArticleStatus.Restricted}
            };
            foreach(var a in articles)repo.Save(a); Check(repo.Load("biz").Count==3,"数据库按标题误去重");
            articles[0].ReadCount=42; articles[0].Html="<div id='js_content'>正文压缩存储测试</div>"; repo.Save(articles[0]); Check(repo.Load("biz").Count==3&&repo.Load("biz").Single(x=>x.Id==id1).ReadCount==42,"更新导致重复或指标未持久化");
            Check(repo.Load("biz").Single(x=>x.Id==id1).Html==""&&repo.LoadBody(id1)==articles[0].Html,"正文没有独立压缩存储/读取");
            var refreshed=new ArticleRecord{Id=id1,Biz="biz",Title="同一个标题",Status=ArticleStatus.Pending};
            repo.Save(refreshed);
            Check(repo.Load("biz").Single(x=>x.Id==id1).ReadCount==42,"重采待处理条目清空了历史统计");
            Check(refreshed.ReadCount==null&&refreshed.Status==ArticleStatus.Pending,"历史缓存污染了待刷新的条目，可能导致重复采集沿用旧统计");
            Check(ArticleQuery.Apply(articles,new ArticleFilter{Column="栏目A",Status=ArticleStatus.Available,From=new DateTime(2026,1,1),Through=new DateTime(2026,1,1)}).Single().Id==id1,"组合筛选或日期闭区间错误");
            var sorted=ArticleQuery.Apply(articles,new ArticleFilter{SortBy="ReadCount",Descending=false}); Check(sorted[0].Id==id1&&sorted.Last().Id=="unknown","数值排序或null排序错误");
            Check(ArticleQuery.Apply(articles,new ArticleFilter{Column="栏目B"}).Single().Id==id2,"多专栏筛选失败");
            report.AppendLine("PASS identity / same-title persistence / multi-column filters / inclusive dates / numeric-null sorting");
            var vault=new SessionVault(root); var session=new AccountSession{Biz="biz",Name="测试号",Cookie="session=secret-test-marker"};
            vault.Save(new Dictionary<string,AccountSession>{{"biz",session}});
            Check(vault.Load()["biz"].Cookie==session.Cookie,"会话存储往返失败");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"sessions.bin"))).Contains("secret-test-marker"),"Cookie被明文保存");
            report.AppendLine("PASS current-user protected session storage");
            var calls=0; var observed=""; var persisted=0;
            using(var controller=new CollectionController(async (s,save,p,t)=>{ calls++; observed=s.Biz; await save(new ArticleRecord{Id="test",Biz=s.Biz},t); await Task.Delay(60000,t); },(a,s,t)=>Task.FromResult(a),a=>persisted++))
            {
                controller.Recognize(session); Check(calls==0&&!controller.IsRunning,"识别触发了自动采集");
                var task=controller.StartAsync(null); Check(controller.IsRunning,"Start尚未建立取消状态");
                controller.Recognize(new AccountSession{Biz="other",Name="另一个号"});
                var timer=System.Diagnostics.Stopwatch.StartNew(); controller.Stop();
                try{await task;throw new Exception("停止没有取消任务");}catch(OperationCanceledException){}
                Check(timer.ElapsedMilliseconds<1500,"停止响应超过1.5秒"); Check(observed=="biz"&&persisted==2,"切换公众号污染当前任务或已存数据丢失"); Check(!controller.IsRunning,"停止后状态未归位");
            }
            report.AppendLine("PASS recognize-only / start / immediate stop / account snapshot / retained records");
            await TestHttpCancellation(); report.AppendLine("PASS live localhost in-flight HTTP cancellation");
            await NativeProfileCacheReaderSelfTests.RunAsync(); report.AppendLine("PASS native cache passive reader / account isolation / bounded decoding");
            await QuietHybridCollectionSelfTests.RunAsync(); report.AppendLine("PASS quiet hybrid collection / supplement deduplication / stop and resume");
            StandaloneAudioSelfTests.Run(); report.AppendLine("PASS standalone audio HTML and JSON / literal data parsing / legacy audio compatibility");
            ArticleColumnParserSelfTests.Run(); report.AppendLine("PASS album names / JavaScript data expressions / exact IDs / placeholder upgrade");
            ArticleColumnMaintenanceSelfTests.Run(); report.AppendLine("PASS local album name repair / preserved article and collection data / restart persistence");
            AudioCacheRecoverySelfTests.Run(); report.AppendLine("PASS audio-specific old cache recovery / preserved body and collection state");
            await AudioDownloadSelfTests.RunAsync(); report.AppendLine("PASS audio formats / download errors / atomic output / cancellation");
            ExportDirectorySelfTests.Run(); report.AppendLine("PASS title and format export folders / collision protection / reusable directory ownership");
            await FullExportPreparationSelfTests.RunAsync(); report.AppendLine("PASS full export preparation / old media recovery / retained HTML");
            await FullExportSelfTests.RunAsync(); report.AppendLine("PASS full HTML and media export / local references / no report files");
            foreach(var name in new[]{"ArticleMetadataSelfTests","CollectionStorageSelfTests","IncrementalStorageSelfTests","HistoryPageSelfTests","CollectionPipelineSelfTests","NativeCollectionStorageSelfTests","CacheStorageSelfTests","CaptureCacheSelfTests","NetworkingSelfTests","ProxyDiscoverySelfTests","ProxyTransportSelfTests","ProcessCaptureRouteSelfTests","SystemProxyCaptureRouteSelfTests","CaptureDiagnosticsSelfTests","WechatCaptureParserSelfTests","WechatPageObserverSelfTests","ClashCaptureRouteSelfTests","EnrichmentSelfTests","ExportServiceSelfTests","MediaToolsSelfTests"})
            {
                var type=Assembly.GetExecutingAssembly().GetTypes().FirstOrDefault(t=>t.Name==name);
                Check(type!=null,"缺少模块验证: "+name);
                var method=type.GetMethod("RunAsync",BindingFlags.Public|BindingFlags.Static)??type.GetMethod("Run",BindingFlags.Public|BindingFlags.Static);
                Check(method!=null,"缺少测试入口: "+name);
                var returned=method.Invoke(null,null); if(returned is Task)await (Task)returned;
                report.AppendLine("PASS "+name);
            }
            await TitaniumIngressSelfTests.RunAsync(); report.AppendLine("PASS authenticated Titanium CONNECT / TLS session marker / WeChat parser / other hosts opaque");
            await DisclaimerSelfTests.RunAsync();
            StartupLifecycleSelfTests.Run(); report.AppendLine("PASS startup consent / checkbox shake / deferred UI-first startup / close before initialization / no tool extraction");
            await McpHostSelfTests.RunAsync(); report.AppendLine("PASS embedded MCP / official clients / auth / host lifecycle / protected configuration");
            McpQuerySelfTests.Run(); report.AppendLine("PASS MCP query / stable cursors / Chinese search / bounded content / credential removal");
            await McpTaskSelfTests.RunAsync(); report.AppendLine("PASS MCP shared task registry / idempotency / cancellation / interrupted recovery");
            await CollectionLimitSelfTests.RunAsync(); report.AppendLine("PASS incremental article limits / commit before next / retained checkpoints / resume");
            await McpSettingsSelfTests.RunAsync(); report.AppendLine("PASS MCP settings / protected draft / save rollback / close cancellation");
            await McpApplicationSelfTests.RunAsync(); report.AppendLine("PASS Agent HTTP to WCAE / real local queries and export / shared tasks / cancel and shutdown");
            Exception uiError=null;
            var uiThread=new Thread(()=>
            {
                try
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    using(var form=new MainForm(true))
                    {
                        form.ShowInTaskbar=false; form.StartPosition=System.Windows.Forms.FormStartPosition.Manual;
                        form.Location=new System.Drawing.Point(-32000,-32000); form.Show();
                        System.Windows.Forms.Application.DoEvents();
                        try { form.ValidatePreviewControls(); } finally { form.Close(); }
                    }
                }
                catch(Exception ex) { uiError=ex; }
            });
            uiThread.SetApartmentState(ApartmentState.STA); uiThread.Start(); Check(uiThread.Join(10000),"界面检查超时"); if(uiError!=null)throw new Exception("界面集成检查失败",uiError);
            report.AppendLine("PASS WinForms real controls / status mapping / optional dates / independent title search / selection / export / cache clear / stale UI callbacks / no auto-collection");
            report.AppendLine("No WeChat requests, no proxy settings or certificate changes performed by these tests.");
            return report.ToString();
        }
        static async Task TestHttpCancellation()
        {
            var server=new TcpListener(IPAddress.Loopback,0); server.Start();
            try
            {
                var port=((IPEndPoint)server.LocalEndpoint).Port;
                using(var transport=new HttpTransport()) using(var cts=new CancellationTokenSource())
                {
                    var accept=server.AcceptTcpClientAsync();
                    var request=transport.GetStringAsync("http://127.0.0.1:"+port+"/hold",new AccountSession(),cts.Token);
                    Check(await Task.WhenAny(accept,Task.Delay(4000))==accept,"本地HTTP测试未建立连接");
                    using(var client=await accept)
                    {
                        var timer=System.Diagnostics.Stopwatch.StartNew(); cts.Cancel();
                        Check(await Task.WhenAny(request,Task.Delay(1500))==request,"取消未中断在途HTTP请求");
                        try{await request;throw new Exception("被取消HTTP请求返回成功");}catch(OperationCanceledException){}
                        Check(timer.ElapsedMilliseconds<1500,"在途请求取消超时");
                    }
                }
            }
            finally{server.Stop();}
        }
    }
}
