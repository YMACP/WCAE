using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WCAE
{
    internal static class McpSettingsSelfTests
    {
        public static Task RunAsync()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { RunOnUiThread(); completion.TrySetResult(true); }
                catch (Exception error) { completion.TrySetException(error); }
            }) { IsBackground = true, Name = "WCAE MCP settings isolated tests" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        static void Check(bool condition, string message)
        { if (!condition) throw new Exception("MCP 设置：" + message); }

        static void RunOnUiThread()
        {
            // These forms live off-screen and use only fake callbacks. No listener, user settings,
            // network, clipboard, or WeChat operations are used by the tests.
            Application.EnableVisualStyles();
            var configuration = new McpConfiguration();
            int applied = 0;
            using (var preview = new McpSettingsForm(() => configuration, () => true, () => true,
                (c, t) => { applied++; return Task.CompletedTask; }, (r, t) => { applied++; return Task.CompletedTask; }, true))
            {
                ShowOffScreen(preview);
                Check(!Field<Button>(preview, "save").Enabled && !Field<Button>(preview, "copy").Enabled && !Field<Button>(preview, "reset").Enabled && !Field<Button>(preview, "toggle").Enabled,
                    "预览允许应用或复制真实配置");
                Check(Field<TextBox>(preview, "token").UseSystemPasswordChar, "令牌未默认隐藏");
                Check(Field<Label>(preview, "state").Text == "未运行" && Field<Label>(preview, "connection").Text == "未连接",
                    "离线预览被外部回调显示为服务已运行或已连接");
                preview.Close();
                Check(applied == 0, "离线预览调用了应用函数");
            }

            bool running = true, connected = false, unavailable = false;
            using (var form = new McpSettingsForm(() => configuration, () => running, () => unavailable ? throw new InvalidOperationException("status-test") : connected,
                (c, t) => { applied++; configuration = c.Copy(); return Task.CompletedTask; }, (r, t) => { running = r; return Task.CompletedTask; }))
            {
                ShowOffScreen(form);
                Check(Field<Label>(form, "state").Text == "已运行" && Field<Label>(form, "connection").Text == "未连接", "服务运行被误当作客户端已连接");
                connected = true; RefreshStatus(form);
                Check(Field<Label>(form, "connection").Text == "已连接", "客户端连接状态没有更新");
                connected = false; RefreshStatus(form);
                Check(Field<Label>(form, "connection").Text == "未连接", "客户端断开后仍显示已连接");
                running = false; connected = true; RefreshStatus(form);
                Check(Field<Label>(form, "state").Text == "未运行" && Field<Label>(form, "connection").Text == "未连接", "服务停止后残留已连接状态");
                running = true; unavailable = true; RefreshStatus(form);
                Check(Field<Label>(form, "state").Text == "已运行" && Field<Label>(form, "connection").Text == "未连接", "连接状态读取异常篡改了已知运行状态或保留了旧连接标记");
                unavailable = false; connected = false; RefreshStatus(form);
                int originalPort = configuration.Port;
                Field<NumericUpDown>(form, "port").Value = 18878;
                Check(configuration.Port == originalPort && applied == 0 && !Field<Button>(form, "copy").Enabled,
                    "端口未保存却已生效或仍可复制");
                string originalToken = configuration.Token;
                Field<Button>(form, "reset").PerformClick();
                Check(configuration.Token == originalToken && applied == 0 && !Field<Button>(form, "copy").Enabled,
                    "重置令牌立即生效或允许复制未保存配置");
                Field<Button>(form, "save").PerformClick();
                Application.DoEvents();
                Check(applied == 1 && configuration.Port == 18878 && configuration.Token != originalToken,
                    "保存没有应用待定配置");
                using (var copied = JsonDocument.Parse(configuration.CopyConfigurationJson()))
                    Check(copied.RootElement.GetProperty("mcpServers").GetProperty("WCAE").GetProperty("url").GetString() == "http://127.0.0.1:18878/mcp",
                        "保存后的 Agent 配置没有使用新端口或本机地址");
                Check(Field<Button>(form, "copy").Enabled && !form.IsDisposed, "保存成功后不能留在窗口复制配置");
                Check(Field<TextBox>(form, "token").UseSystemPasswordChar, "保存后显示了明文令牌");
                form.Close();
            }

            int stablePort = configuration.Port;
            string stableToken = configuration.Token;
            using (var failure = new McpSettingsForm(() => configuration, () => true, () => false,
                (c, t) => throw new InvalidOperationException("test-secret-internal-details"), (r, t) => Task.CompletedTask))
            {
                ShowOffScreen(failure);
                Field<NumericUpDown>(failure, "port").Value = 18879;
                Field<Button>(failure, "reset").PerformClick();
                Field<Button>(failure, "save").PerformClick();
                Application.DoEvents();
                Check(configuration.Port == stablePort && configuration.Token == stableToken, "保存失败覆盖了旧配置");
                Check(!failure.IsDisposed && Field<Button>(failure, "save").Enabled && !Field<Button>(failure, "copy").Enabled,
                    "保存失败后关闭窗口或错误开放了复制");
                Check(!Field<Label>(failure, "result").Text.Contains("test-secret"), "保存失败泄露内部异常");
                failure.Close();
            }

            bool cancelled = false;
            using (var cancel = new McpSettingsForm(() => configuration, () => true, () => false,
                async (c, t) =>
                {
                    try { await Task.Delay(TimeSpan.FromMinutes(1), t); }
                    catch (OperationCanceledException) { cancelled = true; throw; }
                    configuration = c.Copy();
                }, (r, t) => Task.CompletedTask))
            {
                ShowOffScreen(cancel);
                Field<NumericUpDown>(cancel, "port").Value = 18880;
                Field<Button>(cancel, "save").PerformClick();
                Application.DoEvents();
                cancel.Close();
                var timer = Stopwatch.StartNew();
                while (!cancel.IsDisposed && timer.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(5); }
                Check(cancelled && cancel.IsDisposed, "关闭没有取消并等待正在保存的操作");
                Check(configuration.Port == stablePort && configuration.Token == stableToken, "取消后仍然修改了旧配置");
            }
            StartStopDraftAndFailures();
            StartStopCancellationWaitsForCleanup(false);
            StartStopCancellationWaitsForCleanup(true);
        }

        static void StartStopDraftAndFailures()
        {
            var configuration = new McpConfiguration();
            bool running = false;
            int applied = 0, toggled = 0;
            string failure = "";
            using var form = new McpSettingsForm(() => configuration, () => running, () => running,
                (c, token) => { applied++; configuration = c.Copy(); return Task.CompletedTask; },
                (requested, token) =>
                {
                    toggled++;
                    if (failure == "before") throw new InvalidOperationException("test-start-stop-secret");
                    running = requested;
                    if (failure == "after") throw new InvalidOperationException("test-partial-start-stop-secret");
                    return Task.CompletedTask;
                });
            ShowOffScreen(form);
            var toggle = Field<Button>(form, "toggle");
            Check(toggle.Text == "启动" && toggle.Enabled, "未运行时没有提供启动操作");
            Field<NumericUpDown>(form, "port").Value = 18890;
            Check(!toggle.Enabled && toggled == 0, "未保存的停止状态配置仍可直接启动");
            Field<Button>(form, "save").PerformClick();
            Check(applied == 1 && !running && toggle.Enabled && toggle.Text == "启动", "仅保存配置意外启动服务或未恢复启动按钮");
            toggle.PerformClick();
            Check(running && toggled == 1 && toggle.Text == "停止" && Field<Label>(form, "state").Text == "已运行"
                && Field<Label>(form, "connection").Text == "已连接", "成功启动后没有显示真实运行/连接状态");
            Field<NumericUpDown>(form, "port").Value = 18891;
            Check(toggle.Enabled && !Field<Button>(form, "copy").Enabled, "有未保存配置时错误禁止停止服务");
            toggle.PerformClick();
            Check(!running && toggled == 2 && toggle.Text == "启动" && !toggle.Enabled && configuration.Port == 18890
                && Field<NumericUpDown>(form, "port").Value == 18891, "停止服务丢失草稿、隐式保存草稿或错误允许重启");
            Field<Button>(form, "save").PerformClick();
            Check(configuration.Port == 18891 && !running && toggle.Enabled, "停止后保存草稿没有保持停止状态");

            failure = "before";
            toggle.PerformClick();
            Check(!running && toggle.Text == "启动" && Field<Label>(form, "state").Text == "未运行"
                && Field<Label>(form, "result").Text.StartsWith("启动失败") && !Field<Label>(form, "result").Text.Contains("test-"), "启动失败伪造成功状态或泄露内部异常");
            failure = "after";
            toggle.PerformClick();
            Check(running && toggle.Text == "停止" && Field<Label>(form, "state").Text == "已运行", "异常后忽略实际已运行状态");
            failure = "before";
            toggle.PerformClick();
            Check(running && toggle.Text == "停止" && Field<Label>(form, "result").Text.StartsWith("停止失败"), "停止失败伪造服务已停止");
            failure = "after";
            toggle.PerformClick();
            Check(!running && toggle.Text == "启动" && Field<Label>(form, "connection").Text == "未连接", "停止后异常保留过期的运行/连接状态");
            form.Close();
        }

        static void StartStopCancellationWaitsForCleanup(bool initiallyRunning)
        {
            var configuration = new McpConfiguration();
            bool running = initiallyRunning, operationStarted = false, cancelled = false;
            int applied = 0;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var form = new McpSettingsForm(() => configuration, () => running, () => false,
                (c, token) => { applied++; return Task.CompletedTask; },
                async (requested, token) =>
                {
                    operationStarted = true;
                    try { await Task.Delay(TimeSpan.FromMinutes(1), token); running = requested; }
                    catch (OperationCanceledException) { cancelled = true; throw; }
                    finally { await release.Task; }
                });
            try
            {
                ShowOffScreen(form);
                Field<Button>(form, "toggle").PerformClick();
                Check(operationStarted && !Field<Button>(form, "toggle").Enabled && !Field<Button>(form, "save").Enabled
                    && !Field<Button>(form, "copy").Enabled && !Field<Button>(form, "reset").Enabled && !Field<NumericUpDown>(form, "port").Enabled,
                    "服务启停期间没有禁止并发修改/保存/复制");
                var save = (Task)typeof(McpSettingsForm).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                Check(save.IsCompleted && applied == 0, "正在启停时仍执行了配置保存");
                form.Close();
                var timer = Stopwatch.StartNew();
                while (!cancelled && timer.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(5); }
                Check(cancelled && !form.IsDisposed && !Field<Button>(form, "close").Enabled, "关闭未取消启停回调或提前释放窗口");
                release.TrySetResult(true);
                timer.Restart();
                while (!form.IsDisposed && timer.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(5); }
                Check(form.IsDisposed && running == initiallyRunning && applied == 0, "关闭未等待启停清理完成或取消后仍修改运行状态");
            }
            finally { release.TrySetResult(true); }
        }

        static T Field<T>(McpSettingsForm form, string name)
            => (T)typeof(McpSettingsForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);

        static void RefreshStatus(McpSettingsForm form)
            => typeof(McpSettingsForm).GetMethod("RefreshStatus", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);

        static void ShowOffScreen(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();
        }
    }
}
