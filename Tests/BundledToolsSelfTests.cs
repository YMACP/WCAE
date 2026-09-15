using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    public static class BundledToolsSelfTests
    {
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        public static async Task RunAsync()
        {
            string originalData = AppPaths.DataDirectory;
            string fixture = Path.Combine(Path.GetTempPath(), "WCAE-bundle-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.DataDirectory = fixture;
                string tools = AppPaths.ToolsDirectory;
                Check(!Directory.Exists(fixture), "仅查询组件缓存路径就触发了解包");
                using (var icon = AppBranding.CreateApplicationIcon())
                    Check(icon.Width > 0 && icon.Height > 0, "统一程序图标无法读取");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    try { await BundledTools.EnsurePdfAsync(canceled.Token); throw new InvalidOperationException("组件解包忽略已取消请求"); }
                    catch (OperationCanceledException) { }
                }
                Check(!Directory.Exists(fixture), "取消的组件解包仍创建了缓存");
                await BundledTools.EnsureIngressAsync(CancellationToken.None);
                Check(File.Exists(Path.Combine(tools,"ProxyBridgeCore.dll")) && File.Exists(Path.Combine(tools,"WinDivert64.sys")), "按进程接入组件未准备完整");
                Check(!File.Exists(Path.Combine(tools,"ffmpeg.exe")) && !File.Exists(Path.Combine(tools,"wkhtmltopdf.exe")), "按进程接入提前释放了大型导出组件");
                await Task.WhenAll(BundledTools.EnsurePdfAsync(CancellationToken.None), BundledTools.EnsurePdfAsync(CancellationToken.None));
                string pdf = Path.Combine(tools, "wkhtmltopdf.exe");
                Check(File.Exists(pdf) && File.Exists(Path.Combine(tools, "msvcp140.dll")), "首次 PDF 请求未准备所需组件");
                Check(!File.Exists(Path.Combine(tools, "ffmpeg.exe")) && !File.Exists(Path.Combine(tools, "yt-dlp.exe")), "PDF 导出提前解包了音视频组件");
                DateTime stamp = File.GetLastWriteTimeUtc(pdf);
                await BundledTools.EnsurePdfAsync(CancellationToken.None);
                Check(File.GetLastWriteTimeUtc(pdf) == stamp, "重复请求没有复用完整的组件缓存");
                string runtime = Path.Combine(tools, "vcruntime140_1.dll");
                byte[] expected = SHA256.HashData(File.ReadAllBytes(runtime));
                File.WriteAllText(runtime, "interrupted or corrupt cache");
                await BundledTools.EnsurePdfAsync(CancellationToken.None);
                Check(SHA256.HashData(File.ReadAllBytes(runtime)).SequenceEqual(expected), "损坏的组件缓存未自动恢复");
                using (var heldLock = new FileStream(Path.Combine(tools, ".extract.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    try { await BundledTools.EnsurePdfAsync(cancel.Token); throw new InvalidOperationException("等待另一个进程解包时未响应取消"); }
                    catch (OperationCanceledException) { }
                    Check(timer.ElapsedMilliseconds < 1500, "组件解包锁取消超过 1.5 秒");
                }
                Check(!Directory.EnumerateFiles(tools, "*.tmp").Any(), "组件解包残留临时文件");
            }
            finally
            {
                AppPaths.DataDirectory = originalData;
                string resolved = Path.GetFullPath(fixture);
                string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (Directory.Exists(resolved) && string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith("WCAE-bundle-tests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
            }
        }
    }
}
