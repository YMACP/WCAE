using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace WCAE
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--process-capture-helper")
                return ProcessCaptureRoute.RunHelperAsync(args).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--system-proxy-watchdog")
                return SystemProxyCaptureRoute.RunWatchdogAsync(args).GetAwaiter().GetResult();
            if (args.Length == 3 && args[0] == "--process-route-driver-test")
            {
                AppPaths.DataDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "driver-test-data");
                return ProcessCaptureRouteDriverTests.RunAsync(args[1],args[2]).GetAwaiter().GetResult();
            }
            if (args.Length == 3 && args[0] == "--process-helper-test")
                return ProcessCaptureRouteDriverTests.RunHelperTestAsync(args[1],args[2]).GetAwaiter().GetResult();
            if (args.Length == 2 && args[0] == "--network-detection-test")
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var found = ProxyDiscovery.DiscoverAsync(8879,deadline.Token).GetAwaiter().GetResult();
                    File.WriteAllText(args[1],(found.Success?"PASS ":"FAIL ")+found.Message+Environment.NewLine+"Source="+found.Source);
                    return found.Success?0:1;
                }
                catch(Exception ex) { File.WriteAllText(args[1],"FAIL "+ex.GetType().Name); return 1; }
            }
            if (args.Length == 4 && args[0] == "--clash-route-watchdog"
                && int.TryParse(args[2], out int ownerPid) && long.TryParse(args[3], out long ownerStartTicks))
                return ClashCaptureRoute.RunWatchdogAsync(args[1], ownerPid, ownerStartTicks).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--export-test-helper") return ExportTestHelperHost.Run(args.Skip(1).ToArray());
            if (args.Contains("--self-test"))
            {
                var result = args.SkipWhile(x=>x!="--self-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test-results.txt");
                try { var report=SelfTests.RunAsync().GetAwaiter().GetResult(); File.WriteAllText(result,report); return 0; }
                catch(Exception ex) { File.WriteAllText(result,"FAIL\r\n"+ex); return 1; }
            }
            if ((args.Length == 2 || args.Length == 3) && args[0] == "--repair-article-metadata")
            {
                bool isolated = args.Length == 3 && !string.Equals(Path.GetFullPath(args[2]).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(AppPaths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);
                if (isolated) AppPaths.DataDirectory = Path.GetFullPath(args[2]);
                using var maintenanceInstance = new SingleInstance(isolated);
                if (!maintenanceInstance.IsOwner) { File.WriteAllText(args[1],"FAIL WCAE is running; close it before offline maintenance."); return 2; }
                try
                {
                    var report = ArticleMetadataMaintenance.Repair(new ArticleRepository(AppPaths.DataDirectory));
                    File.WriteAllText(args[1],Newtonsoft.Json.JsonConvert.SerializeObject(report,Newtonsoft.Json.Formatting.Indented));
                    return 0;
                }
                catch (Exception ex) { File.WriteAllText(args[1],"FAIL "+ex.GetType().Name); return 1; }
            }
            if ((args.Length == 2 || args.Length == 3) && args[0] == "--repair-account-names")
            {
                // Offline maintenance: no listener, network discovery, or article collection.
                bool isolated = args.Length == 3 && !string.Equals(Path.GetFullPath(args[2]).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(AppPaths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);
                if (isolated) AppPaths.DataDirectory = Path.GetFullPath(args[2]);
                using var maintenanceInstance = new SingleInstance(isolated);
                if (!maintenanceInstance.IsOwner) return 2;
                try
                {
                    var repository = new ArticleRepository(AppPaths.DataDirectory);
                    var vault = new SessionVault(AppPaths.DataDirectory);
                    var names = AccountNameMaintenance.Repair(repository, vault, vault.Load());
                    File.WriteAllText(args[1],"PASS account-name repair; accounts="+names.Count+"; resolved="+names.Count(p=>!string.IsNullOrEmpty(p.Value)));
                    return 0;
                }
                catch (Exception ex) { File.WriteAllText(args[1],"FAIL "+ex.GetType().Name); return 1; }
            }
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Contains("--startup-lifecycle-test"))
            {
                var target=args.SkipWhile(x=>x!="--startup-lifecycle-test").Skip(1).First();
                try { File.WriteAllText(target,StartupLifecycleSelfTests.RunMeasured()); return 0; }
                catch(Exception ex) { File.WriteAllText(target,"FAIL\r\n"+ex); return 1; }
            }
            if(args.Contains("--render-settings"))
            {
                var target=args.SkipWhile(x=>x!="--render-settings").Skip(1).First();
                ConnectionSettingsForm.RenderPreview(target,args.Contains("--settings-manual")); return 0;
            }
            if(args.Contains("--render-mcp-settings"))
            {
                var target=args.SkipWhile(x=>x!="--render-mcp-settings").Skip(1).First();
                McpSettingsForm.RenderPreview(target); return 0;
            }
            if(args.Contains("--render-disclaimer"))
            {
                var target=args.SkipWhile(x=>x!="--render-disclaimer").Skip(1).First();
                DisclaimerForm.RenderPreview(target); return 0;
            }
            var preview=args.Contains("--preview") || args.Contains("--render-preview");
            using(var instance=new SingleInstance(preview))
            {
                if(!instance.IsOwner) return 0;
                if(preview)AppPaths.DataDirectory=Path.Combine(Path.GetTempPath(),"WCAE-preview-"+Guid.NewGuid().ToString("N"));
                try
                {
                    // No MainForm, history, capture or MCP services exist before consent.
                    if(!preview && !RequestStartupConsent(instance))return 0;
                    using(var form=new MainForm(preview))
                    {
                        instance.Attach(form);
                        if(preview && args.Contains("--preview-pending")) form.ShowCapturePreview(false,false,"");
                        if(preview && args.Contains("--preview-partial")) form.ShowCapturePreview(true,false,"");
                        if(args.Contains("--render-preview"))
                        {
                            var target=args.SkipWhile(x=>x!="--render-preview").Skip(1).First();
                            form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-32000,-32000); form.Show(); Application.DoEvents();
                            if(args.Contains("--preview-bottom")) { form.ShowLastPreviewArticles(); Application.DoEvents(); }
                            using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size)); bitmap.Save(target,System.Drawing.Imaging.ImageFormat.Png); }
                            form.Close(); Application.DoEvents(); return 0;
                        }
                        Application.Run(form);
                    }
                }
                catch(Exception ex) { MessageBox.Show("WCAE 启动失败："+ex.Message,"WCAE",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1; }
            }
            return 0;
        }

        static bool RequestStartupConsent(SingleInstance instance)
        {
            using var dialog=new DisclaimerForm();
            instance.Attach(dialog);
            return dialog.ShowDialog()==DialogResult.OK && dialog.Accepted;
        }
    }
}
