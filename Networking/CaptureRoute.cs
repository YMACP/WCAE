using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    /// <summary>Separates how WeChat enters WCAE from the selected outbound proxy.</summary>
    internal sealed class CaptureRoute : IDisposable
    {
        ClashCaptureRoute clash;
        ProcessCaptureRoute process;
        readonly SystemProxyCaptureRoute system;
        readonly Action<string> status;
        CaptureRoute(ClashCaptureRoute value, Action<string> status) { clash = value; this.status = status; }
        CaptureRoute(ProcessCaptureRoute value) { process = value; }
        CaptureRoute(SystemProxyCaptureRoute value) { system = value; }
        public bool RequiresAuthentication => system == null;
        public string CaptureUsername => clash?.CaptureUsername ?? process?.CaptureUsername;
        public string CapturePassword => clash?.CapturePassword ?? process?.CapturePassword;
        public static async Task<CaptureRoute> CreateAsync(ProxyEndpoint upstream, CaptureIngressMode mode, Action<string> status, CancellationToken token)
        {
            if (mode == CaptureIngressMode.SystemProxy) return new CaptureRoute(new SystemProxyCaptureRoute(status));
            // An already configured compatible controller avoids an unnecessary elevation prompt.
            // Other clients, SOCKS-only endpoints and direct connections use independent ingress.
            if (mode == CaptureIngressMode.Auto && upstream?.Protocol == ProxyProtocol.Http && ProxyEndpoint.IsLocalHost(upstream.Host))
            {
                try
                {
                    using var client = ClashControllerClient.Discover();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    var snapshot = await client.GetSnapshotAsync(deadline.Token).ConfigureAwait(false);
                    string currentMode = (string)snapshot.General["mode"];
                    if (((int?)snapshot.General["mixed-port"] == upstream.Port || (int?)snapshot.General["port"] == upstream.Port)
                        && (currentMode == "rule" || currentMode == "global") && (string)snapshot.General["find-process-mode"] != "off")
                        return new CaptureRoute(new ClashCaptureRoute(status), status);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    token.ThrowIfCancellationRequested();
                    // Controller support is optional. No configuration has been changed here.
                }
            }
            token.ThrowIfCancellationRequested();
            return new CaptureRoute(new ProcessCaptureRoute(status));
        }
        public async Task ActivateAsync(int capturePort, int upstreamPort, CancellationToken token)
        {
            if (clash != null)
            {
                try { await clash.ActivateAsync(capturePort, upstreamPort, token).ConfigureAwait(false); return; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    token.ThrowIfCancellationRequested();
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    if (!await clash.RestoreAsync(cleanup.Token).ConfigureAwait(false))
                        throw new IOException("原接入恢复尚未完成，请先重新接入。", ex);
                    clash.Dispose(); clash = null;
                    status?.Invoke("当前代理控制器未能建立微信分流，正在改用独立接入；网络出口保持所选端口。");
                    process = new ProcessCaptureRoute(status);
                }
            }
            if (process != null) await process.ActivateAsync(capturePort, upstreamPort, token).ConfigureAwait(false);
            else await system.ActivateAsync(capturePort, upstreamPort, token).ConfigureAwait(false);
        }
        public Task<bool> RestoreAsync(CancellationToken token)
            => clash != null ? clash.RestoreAsync(token) : process != null ? process.RestoreAsync(token) : system.RestoreAsync(token);
        public Task<bool> CheckActiveAsync(CancellationToken token)
            => clash != null ? clash.CheckActiveAsync(token) : process != null ? process.CheckActiveAsync(token) : system.CheckActiveAsync(token);
        public Task<int> RefreshWechatConnectionsAsync(CancellationToken token)
            => clash != null ? clash.RefreshWechatConnectionsAsync(token) : process != null ? process.RefreshWechatConnectionsAsync(token) : system.RefreshWechatConnectionsAsync(token);
        public void Dispose() { clash?.Dispose(); process?.Dispose(); system?.Dispose(); }
    }
}
