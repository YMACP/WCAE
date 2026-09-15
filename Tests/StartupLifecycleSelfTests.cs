using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WCAE
{
    public static class StartupLifecycleSelfTests
    {
        public static void Run() => RunMeasured();

        public static string RunMeasured()
        {
            Exception failure=null;
            string original=AppPaths.DataDirectory;
            string fixture=Path.Combine(Path.GetTempPath(),"WCAE-deferred-startup-"+Guid.NewGuid().ToString("N"));
            var report=new StringBuilder();
            var thread=new Thread(()=>
            {
                try
                {
                    AppPaths.DataDirectory=fixture;
                    using(var form=new MainForm())form.ValidateDeferredConstruction();
                    if(Directory.Exists(fixture))throw new InvalidOperationException("仅构建或关闭未显示的窗口就创建了数据或释放了导出组件。");
                    report.AppendLine("PASS construction does not open history/network or extract tools");
                    var repository=new ArticleRepository(fixture);
                    repository.SaveAccount("lifecycle-fixture","生命周期测试公众号");
                    repository.Save(new ArticleRecord { Id="lifecycle-article", Biz="lifecycle-fixture", Title="后台载入测试", Status=ArticleStatus.Available });
                    new SessionVault(fixture).Save(new Dictionary<string,AccountSession> { ["lifecycle-fixture"]=new AccountSession { Biz="lifecycle-fixture", Name="生命周期测试公众号", Cookie="isolated-fixture" } });
                    CheckShownLifecycle(false,report);
                    CheckShownLifecycle(true,report);
                    CheckManualMcpLifecycle(report,fixture);
                    if(Directory.Exists(Path.Combine(fixture,"components")) || File.Exists(Path.Combine(fixture,"network-settings.json")) || File.Exists(Path.Combine(fixture,"system-proxy-lease.bin")))
                        throw new InvalidOperationException("隔离生命周期检查释放了组件或改写了连接状态。");
                    report.AppendLine("No proxy discovery, capture listener, driver, certificate change or WeChat request was started; only isolated loopback MCP listeners were tested.");
                }
                catch(Exception ex) { failure=ex; }
                finally { AppPaths.DataDirectory=original; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if(!thread.Join(30000))throw new InvalidOperationException("延迟初始化检查超时。");
            if(failure!=null)throw new InvalidOperationException("延迟初始化检查失败。",failure);
            string resolved=Path.GetFullPath(fixture);
            string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if(Directory.Exists(resolved) && string.Equals(Path.GetDirectoryName(resolved),temp,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("WCAE-deferred-startup-",StringComparison.Ordinal))Directory.Delete(resolved,true);
            return report.ToString();
        }

        private static void CheckManualMcpLifecycle(StringBuilder report,string fixture)
        {
            var initial=new McpConfiguration { Port=MainForm.FindManualMcpPort() };
            new McpConfigurationStore(fixture).Save(initial);
            McpConfiguration persisted=null;
            for(int attempt=0;attempt<2;attempt++)
            {
                bool reached=false;
                using var form=new MainForm(false,token=> { token.ThrowIfCancellationRequested(); reached=true; return Task.CompletedTask; });
                form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new System.Drawing.Point(-32000,-32000);
                form.Show();
                try
                {
                    PumpUntil(()=>reached && form.InitializationTask.IsCompleted,"MCP 隔离窗口初始化未完成");
                    form.InitializationTask.GetAwaiter().GetResult();
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    if(attempt==0)
                    {
                        var test=form.ValidateManualMcpLifecycleAsync(timeout.Token);
                        PumpUntil(()=>test.IsCompleted,"手动 MCP 生命周期检查未完成");
                        persisted=test.GetAwaiter().GetResult();
                    }
                    else
                    {
                        var test=form.ValidateManualMcpReloadAsync(persisted,timeout.Token);
                        PumpUntil(()=>test.IsCompleted,"MCP 配置跨初始化检查未完成");
                        test.GetAwaiter().GetResult();
                    }
                }
                finally
                {
                    form.Close();
                    PumpUntil(()=>form.ShutdownTask.IsCompleted && form.IsDisposed,"MCP 隔离窗口关闭未完成");
                    form.ShutdownTask.GetAwaiter().GetResult();
                }
                if(persisted!=null)MainForm.AssertManualMcpPortClosed(persisted.Port);
            }
            report.AppendLine("PASS manual MCP default-off / save while off / explicit start-stop-restart / occupied-port rollback / queued-start cancellation / persisted token / close releases listener");
        }

        private static void CheckShownLifecycle(bool closeDuringInitialization, StringBuilder report)
        {
            var connectionReached=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectionRelease=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool canceled=false;
            using(var form=new MainForm(false,async token=>
            {
                connectionReached.TrySetResult(true);
                try { await connectionRelease.Task.WaitAsync(token); }
                catch(OperationCanceledException) { canceled=true; throw; }
            }))
            {
                form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual;
                form.Location=new System.Drawing.Point(-32000,-32000);
                var frame=Stopwatch.StartNew(); form.Show(); frame.Stop();
                // WinForms can post Shown after Show returns. Pump to the controlled pending
                // connection stage before inspecting InitializationTask; its null sentinel is completed.
                PumpUntil(()=>connectionReached.Task.IsCompleted,"隔离连接阶段未开始");
                if(!form.Visible || form.InitializationTask.IsCompleted)throw new InvalidOperationException("显示窗口没有先于后台初始化完成。");
                form.ValidateIsolatedStartupHistory("lifecycle-article");
                if(!closeDuringInitialization)
                {
                    connectionRelease.TrySetResult(true);
                    PumpUntil(()=>form.InitializationTask.IsCompleted,"后台初始化未完成");
                    form.InitializationTask.GetAwaiter().GetResult();
                    form.ValidateIsolatedStartupHistory("lifecycle-article");
                    form.ValidateAccountNameSynchronization();
                    report.AppendLine("PASS account-name synchronization / invalid refresh protection / unchanged selection / no account switch");
                }
                var close=Stopwatch.StartNew(); form.Close();
                double hideMilliseconds=close.Elapsed.TotalMilliseconds;
                if(form.Visible || !form.IsClosing)throw new InvalidOperationException("关闭操作没有立即隐藏窗口。");
                PumpUntil(()=>form.ShutdownTask.IsCompleted && form.IsDisposed,"关闭清理任务未结束");
                form.ShutdownTask.GetAwaiter().GetResult();
                close.Stop();
                if(closeDuringInitialization&&!canceled)throw new InvalidOperationException("关闭时没有取消未完成的连接初始化。");
                report.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PASS {0}: Show={1:F1}ms; Hide={2:F1}ms; cleanup={3:F1}ms; history loaded; no auto collection",
                    closeDuringInitialization?"close during initialization":"close after initialization",frame.Elapsed.TotalMilliseconds,hideMilliseconds,close.Elapsed.TotalMilliseconds));
            }
        }

        private static void PumpUntil(Func<bool> completed, string failure)
        {
            var deadline=Stopwatch.StartNew();
            while(!completed())
            {
                if(deadline.Elapsed>TimeSpan.FromSeconds(10))throw new InvalidOperationException(failure);
                Application.DoEvents(); Thread.Sleep(1);
            }
        }
    }
}
