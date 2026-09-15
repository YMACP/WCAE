using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WCAE
{
    internal static class DisclaimerSelfTests
    {
        public static Task RunAsync()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { RunOnUiThread(); completion.TrySetResult(true); }
                catch (Exception error) { completion.TrySetException(error); }
            }) { IsBackground = true, Name = "WCAE disclaimer isolated tests" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        static void RunOnUiThread()
        {
            // Only screen-external disclaimer controls are exercised. No main window,
            // saved acceptance, local application data, proxy or clipboard is touched.
            Application.EnableVisualStyles();
            using (var form = new DisclaimerForm())
            {
                ShowOffScreen(form);
                var agreement = Field<CheckBox>(form, "agreement");
                var enter = Field<Button>(form, "enter");
                var motion = Field<Panel>(form, "agreementMotion");
                var timer = Field<System.Windows.Forms.Timer>(form, "shakeTimer");
                Check(!agreement.Checked && !form.Accepted && form.DialogResult != DialogResult.OK,
                    "首次显示默认勾选或已同意");
                Check(enter.Enabled && enter.DialogResult == DialogResult.None && ReferenceEquals(form.AcceptButton, enter),
                    "未勾选时按钮被禁用或默认按钮绕过验证");

                Size originalSize = form.ClientSize;
                form.ClientSize = new Size(500, 430);
                Application.DoEvents();
                foreach (Control control in new Control[] { enter, agreement })
                {
                    var bounds = new Rectangle(form.PointToClient(control.PointToScreen(Point.Empty)), control.Size);
                    Check(control.Visible && control.Width > 0 && control.Height > 0 && form.ClientRectangle.Contains(bounds),
                        "小工作区中同意复选框或进入按钮被裁切");
                }
                var body = Field<FlowLayoutPanel>(form, "body");
                Check(body.Height > 0 && body.AutoScroll, "小工作区中正文无法滚动阅读");
                form.ClientSize = originalSize;
                Application.DoEvents();

                int origin = motion.Left;
                bool movedLeft = false, movedRight = false;
                void Observe()
                {
                    movedLeft |= motion.Left < origin;
                    movedRight |= motion.Left > origin;
                }
                enter.PerformClick();
                Check(!form.Accepted && form.DialogResult != DialogResult.OK && form.Visible && !form.IsDisposed,
                    "未勾选点击却放行或关闭了窗口");
                PumpUntil(() => !timer.Enabled, 1600, "提示动画没有停止", Observe);
                Check(movedLeft && movedRight && motion.Left == origin && form.ShakeOffset == 0,
                    "未勾选点击没有左右晃动或结束后未回到原位");
                Check(enter.Enabled, "晃动后按钮仍被禁用");

                // Re-click while animation is in flight; its new origin must not be
                // the displaced position left by the previous animation frame.
                for (int i = 0; i < 3; i++)
                {
                    enter.PerformClick();
                    PumpFor(65, Observe);
                }
                PumpUntil(() => !timer.Enabled, 1600, "重复点击后动画没有停止", Observe);
                Check(motion.Left == origin && form.ShakeOffset == 0 && !form.Accepted && form.Visible,
                    "重复点击累计了位移或绕过同意校验");

                agreement.Checked = true;
                Check(!form.Accepted && form.DialogResult != DialogResult.OK && form.Visible,
                    "只勾选复选框就自动进入了程序");
                enter.PerformClick();
                Application.DoEvents();
                Check(form.Accepted && form.DialogResult == DialogResult.OK,
                    "勾选并点击后没有确认同意");
            }

            // Each new launch must ask again. Closing midway through the shake must
            // reject entry and remove the native timer, not leave callbacks behind.
            using (var close = new DisclaimerForm())
            {
                ShowOffScreen(close);
                Check(!Field<CheckBox>(close, "agreement").Checked && !close.Accepted,
                    "新窗口继承了上次同意状态");
                var timer = Field<System.Windows.Forms.Timer>(close, "shakeTimer");
                int ticks = 0;
                timer.Tick += (s, e) => ticks++;
                Field<Button>(close, "enter").PerformClick();
                PumpUntil(() => ticks > 0, 1000, "关闭检查前动画没有开始");
                close.Close();
                Application.DoEvents();
                int afterClose = ticks;
                PumpFor(160);
                Check(!close.Accepted && close.DialogResult != DialogResult.OK && !close.Visible,
                    "关闭窗口被当作同意");
                Check(!timer.Enabled && ticks == afterClose,
                    "关闭后仍有晃动计时器回调");
            }

            using (var escape = new DisclaimerForm())
            {
                ShowOffScreen(escape);
                var agreement = Field<CheckBox>(escape, "agreement");
                Check(!agreement.Checked && !escape.Accepted, "再次打开未恢复未勾选状态");
                agreement.Checked = true;
                var message = Message.Create(escape.Handle, 0x0100, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                object[] arguments = { message, Keys.Escape };
                var handler = typeof(DisclaimerForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(handler != null && (bool)handler.Invoke(escape, arguments), "ESC 没有被拒绝入口处理");
                Application.DoEvents();
                Check(!escape.Accepted && escape.DialogResult != DialogResult.OK && !escape.Visible,
                    "已勾选但按 ESC 被当作同意");
            }

            CheckSingleInstanceActivation();
        }

        static void CheckSingleInstanceActivation()
        {
            // Preview instance names include this self-test process ID, so signaling
            // them cannot activate another WCAE process or any real main window.
            using var instance = new SingleInstance(true);
            Check(instance.IsOwner, "未取得进程内隔离的单实例测试锁");
            using var first = new Form { Text = "WCAE isolated consent activation", ShowInTaskbar = false };
            using var second = new Form { Text = "WCAE isolated replacement activation", ShowInTaskbar = false };
            ShowOffScreen(first);
            ShowOffScreen(second);
            first.Hide();
            second.Hide();
            instance.Attach(first);
            using var activation = EventWaitHandle.OpenExisting("Local\\WCAE-Activate-Preview-" + Environment.ProcessId);
            activation.Set();
            PumpUntil(() => first.Visible, 1500, "单实例激活未唤起免责声明阶段的窗口");
            Check(!second.Visible, "单实例激活错误唤起尚未挂接的窗口");

            first.Hide();
            activation.Set();
            // Give the old wait registration a chance to enqueue an activation, but
            // do not pump the UI queue until the active form has been replaced.
            Thread.Sleep(40);
            instance.Attach(second);
            activation.Set();
            PumpUntil(() => second.Visible, 1500, "重新挂接后单实例激活未转向新窗口");
            PumpFor(80);
            Check(!first.Visible, "旧激活回调在重新挂接后重新打开了免责声明窗口");
            first.Close();
            second.Close();
        }

        static void ShowOffScreen(Form form)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();
        }

        static T Field<T>(DisclaimerForm form, string name)
            => (T)typeof(DisclaimerForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);

        static void PumpUntil(Func<bool> complete, int milliseconds, string error, Action observe = null)
        {
            var timer = Stopwatch.StartNew();
            while (!complete())
            {
                if (timer.ElapsedMilliseconds > milliseconds) throw new InvalidOperationException("免责声明：" + error);
                Application.DoEvents();
                observe?.Invoke();
                Thread.Sleep(4);
            }
            observe?.Invoke();
        }

        static void PumpFor(int milliseconds, Action observe = null)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < milliseconds)
            {
                Application.DoEvents();
                observe?.Invoke();
                Thread.Sleep(4);
            }
        }

        static void Check(bool condition, string error)
        { if (!condition) throw new InvalidOperationException("免责声明：" + error); }
    }
}
