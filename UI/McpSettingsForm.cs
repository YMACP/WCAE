using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WCAE
{
    internal sealed class McpSettingsForm : Form
    {
        readonly Func<McpConfiguration> getConfig;
        readonly Func<bool> isRunning;
        readonly Func<bool> isConnected;
        readonly Func<McpConfiguration, CancellationToken, Task> apply;
        readonly Func<bool, CancellationToken, Task> setRunning;
        readonly bool preview;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly NumericUpDown port = new NumericUpDown { Minimum = 1, Maximum = 65535, ThousandsSeparator = false };
        readonly TextBox token = new TextBox { ReadOnly = true, UseSystemPasswordChar = true, AutoSize = false, Multiline = false, BackColor = Color.FromArgb(245, 249, 246), BorderStyle = BorderStyle.FixedSingle };
        readonly Label state = ValueLabel(), connection = ValueLabel();
        readonly Label result = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(80, 102, 90), TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, Visible = false };
        readonly Button toggle = ActionButton("启动"), reset = ActionButton("重置"), copy = ActionButton("复制Agent配置"), save = ActionButton("保存"), close = ActionButton("关闭");
        readonly TableLayoutPanel layout, tokenRow;
        readonly FlowLayoutPanel buttons;
        McpConfiguration current, draft;
        bool saving, switching, closingRequested, allowClose, loading, adjustingLayout;
        int lifetimeDisposed;

        internal McpSettingsForm(Func<McpConfiguration> getConfig, Func<bool> isRunning, Func<bool> isConnected,
            Func<McpConfiguration, CancellationToken, Task> apply, Func<bool, CancellationToken, Task> setRunning, bool preview = false)
        {
            this.getConfig = getConfig ?? throw new ArgumentNullException(nameof(getConfig));
            this.isRunning = isRunning ?? throw new ArgumentNullException(nameof(isRunning));
            this.isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
            this.apply = apply ?? throw new ArgumentNullException(nameof(apply));
            this.setRunning = setRunning ?? throw new ArgumentNullException(nameof(setRunning));
            this.preview = preview;
            current = (getConfig() ?? new McpConfiguration()).Copy();
            draft = current.Copy();

            Text = "MCP接入";
            Icon = AppBranding.CreateApplicationIcon();
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            ClientSize = new Size(390, 190);

            layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 16, 18, 12), ColumnCount = 2, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            AddRow(layout, 0, "服务端口", port);
            port.Anchor = AnchorStyles.Left;
            port.Width = 100;
            tokenRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
            tokenRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tokenRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
            token.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            token.Margin = new Padding(0, 3, 6, 3);
            token.AccessibleName = "访问令牌（已隐藏）";
            reset.Anchor = AnchorStyles.Left;
            reset.Margin = new Padding(0, 3, 0, 3);
            tokenRow.Controls.Add(token, 0, 0);
            tokenRow.Controls.Add(reset, 1, 0);
            AddRow(layout, 1, "访问令牌", tokenRow);
            tokenRow.Dock = DockStyle.Fill;
            tokenRow.Margin = Padding.Empty;
            AddRow(layout, 2, "运行状态", state);
            AddRow(layout, 3, "连接状态", connection);
            layout.Controls.Add(result, 0, 4);
            layout.SetColumnSpan(result, 2);

            buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            buttons.Controls.Add(close);
            buttons.Controls.Add(save);
            buttons.Controls.Add(copy);
            buttons.Controls.Add(toggle);
            layout.Controls.Add(buttons, 0, 5);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);
            result.TextChanged += (_, __) => PerformLayout();
            AcceptButton = save;
            CancelButton = close;

            LoadDraft();
            RefreshStatus();
            port.ValueChanged += (s, e) =>
            {
                if (loading) return;
                draft.Port = (int)port.Value;
                DraftChanged("端口修改将在保存后生效，请保存后再复制连接配置。");
            };
            reset.Click += (s, e) =>
            {
                draft.Token = McpConfiguration.CreateToken();
                token.Text = draft.Token;
                DraftChanged("新令牌将在保存后生效。保存后请重新复制Agent配置。");
            };
            copy.Click += (s, e) => CopyConfiguration();
            save.Click += async (s, e) => await SaveAsync();
            toggle.Click += async (s, e) => await ToggleRunningAsync();
            close.Click += (s, e) => Close();
            refresh.Tick += (s, e) => RefreshStatus();
            if (!preview) refresh.Start();
            FormClosing += HandleClosing;
            PerformLayout();
        }

        internal static void RenderPreview(string path)
        {
            var configuration = new McpConfiguration { Port = 8878, Token = new string('0', 64) };
            using var form = new McpSettingsForm(() => configuration, () => false, () => false,
                (config, token) => throw new InvalidOperationException("预览不应应用连接配置。"),
                (running, token) => throw new InvalidOperationException("预览不应启停服务。"), true);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            form.Close();
            Application.DoEvents();
        }

        static Label ValueLabel() => new Label { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        static Button ActionButton(string text)
        {
            return new Button { Text = text, Width = 76, Height = 28, FlatStyle = FlatStyle.Standard,
                BackColor = Color.White, UseVisualStyleBackColor = true, Cursor = Cursors.Hand, Padding = Padding.Empty };
        }
        protected override void OnLayout(LayoutEventArgs levent)
        {
            if (layout != null && tokenRow != null && buttons != null && !adjustingLayout)
            {
                adjustingLayout = true;
                try
                {
                    int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);
                    layout.Padding = new Padding(Scale(18), Scale(16), Scale(18), Scale(12));
                    layout.ColumnStyles[0].Width = Math.Max(Scale(76), TextRenderer.MeasureText("运行状态", Font).Width + Scale(12));
                    int tokenHeight = token.PreferredHeight + Scale(4);
                    token.Height = tokenHeight;
                    int rowHeight = Math.Max(Scale(30), Math.Max(tokenHeight, port.PreferredHeight) + Scale(6));
                    for (int i = 0; i < 4; i++) layout.RowStyles[i].Height = rowHeight;
                    port.Width = Scale(100);
                    token.Margin = new Padding(0, Scale(3), Scale(6), Scale(3));
                    reset.Margin = new Padding(0, Scale(3), 0, Scale(3));
                    reset.Size = new Size(TextRenderer.MeasureText(reset.Text, reset.Font).Width + Scale(16), tokenHeight);
                    tokenRow.ColumnStyles[1].Width = reset.Width;
                    int buttonHeight = Math.Max(token.PreferredHeight, TextRenderer.MeasureText("保存", Font).Height + Scale(10));
                    foreach (var button in new[] { toggle, copy, save, close })
                    {
                        button.Size = new Size(TextRenderer.MeasureText(button.Text, button.Font).Width + Scale(22), buttonHeight);
                        button.Margin = new Padding(button == toggle ? 0 : Scale(8), Scale(5), 0, 0);
                    }
                    int feedbackHeight = 0;
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        int width = Math.Max(Scale(120), ClientSize.Width - layout.Padding.Horizontal);
                        feedbackHeight = TextRenderer.MeasureText(result.Text, result.Font, new Size(width, int.MaxValue),
                            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + Scale(8);
                    }
                    result.Visible = feedbackHeight > 0;
                    layout.RowStyles[4].Height = feedbackHeight;
                    layout.RowStyles[5].Height = buttonHeight + Scale(5);
                    int height = layout.Padding.Vertical + rowHeight * 4 + feedbackHeight + buttonHeight + Scale(5);
                    if (ClientSize.Height != height) ClientSize = new Size(ClientSize.Width, height);
                }
                finally { adjustingLayout = false; }
            }
            base.OnLayout(levent);
        }
        static void AddRow(TableLayoutPanel layout, int row, string text, Control control)
        {
            layout.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 0, row);
            control.AccessibleName = text;
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            control.Margin = new Padding(0, 3, 0, 3);
            layout.Controls.Add(control, 1, row);
        }
        bool IsDirty => draft.Port != current.Port || !string.Equals(draft.Token, current.Token, StringComparison.Ordinal);
        bool Busy => saving || switching;
        bool CanUpdate => !IsDisposed && !Disposing && !closingRequested;

        void LoadDraft()
        {
            loading = true;
            try
            {
                port.Value = Math.Clamp(draft.Port, 1, 65535);
                token.Text = draft.Token ?? "";
                UpdateButtons();
            }
            finally { loading = false; }
        }

        void DraftChanged(string message)
        {
            result.ForeColor = Color.FromArgb(80, 102, 90);
            result.Text = IsDirty ? message : "";
            UpdateButtons();
        }

        void UpdateButtons(bool? running = null)
        {
            bool actualRunning = running ?? ReadRunning();
            bool editable = !preview && !Busy && !closingRequested;
            port.Enabled = editable;
            reset.Enabled = editable;
            save.Enabled = editable;
            copy.Enabled = editable && !IsDirty;
            toggle.Text = actualRunning ? "停止" : "启动";
            toggle.Enabled = editable && (actualRunning || !IsDirty);
            save.Text = saving ? "保存中…" : "保存";
            PerformLayout();
        }

        void RefreshStatus()
        {
            if (!CanUpdate) return;
            bool running = false, connected = false;
            running = ReadRunning();
            if (running) try { connected = isConnected(); } catch { }
            SetStatus(running, connected);
            UpdateButtons(running);
        }
        bool ReadRunning()
        { try { return !preview && isRunning(); } catch { return false; } }
        void SetStatus(bool running, bool connected)
        {
            state.Text = running ? "已运行" : "未运行";
            connection.Text = running && connected ? "已连接" : "未连接";
            state.ForeColor = running ? Color.FromArgb(27, 94, 45) : Color.FromArgb(139, 32, 32);
            connection.ForeColor = running && connected ? Color.FromArgb(27, 94, 45) : Color.FromArgb(139, 32, 32);
        }

        void CopyConfiguration()
        {
            if (preview || Busy || IsDirty || !CanUpdate) return;
            try
            {
                var applied = getConfig()?.Copy() ?? current.Copy();
                Clipboard.SetText(applied.CopyConfigurationJson());
                result.ForeColor = Color.FromArgb(36, 132, 74);
                result.Text = "";
            }
            catch
            {
                result.ForeColor = Color.Firebrick;
                result.Text = "复制失败，请稍后重试。";
            }
        }

        async Task SaveAsync()
        {
            if (preview || Busy || !CanUpdate) return;
            try
            {
                draft.Port = (int)port.Value;
                draft.Validate();
                saving = true;
                UpdateButtons();
                result.ForeColor = Color.FromArgb(80, 102, 90);
                result.Text = "正在保存并应用连接设置…";
                var requested = draft.Copy();
                await apply(requested, lifetime.Token);
                if (!CanUpdate) return;
                current = (getConfig() ?? requested).Copy();
                draft = current.Copy();
                LoadDraft();
                RefreshStatus();
                result.ForeColor = Color.FromArgb(36, 132, 74);
                result.Text = "";
            }
            catch (OperationCanceledException)
            {
                if (CanUpdate) { result.ForeColor = Color.FromArgb(80, 102, 90); result.Text = "保存已取消。"; }
            }
            catch (Exception error)
            {
                if (!CanUpdate) return;
                result.ForeColor = Color.Firebrick;
                result.Text = error is ArgumentOutOfRangeException ? "端口须在 1 至 65535 之间。"
                    : error is ArgumentException ? "连接配置无效，请检查端口或重置令牌后重试。"
                    : "保存失败，请检查端口是否被占用后重试；详情见 WCAE 日志。";
                RefreshStatus();
            }
            finally
            {
                saving = false;
                FinishOperation();
            }
        }

        async Task ToggleRunningAsync()
        {
            if (preview || Busy || !CanUpdate) return;
            bool start = !ReadRunning();
            if (start && IsDirty)
            {
                DraftChanged("请先保存修改后的配置，再启动MCP服务。");
                return;
            }
            try
            {
                switching = true;
                UpdateButtons();
                result.ForeColor = Color.FromArgb(80, 102, 90);
                result.Text = start ? "正在启动MCP服务…" : "正在停止MCP服务…";
                await setRunning(start, lifetime.Token);
                if (CanUpdate) result.Text = "";
            }
            catch (OperationCanceledException)
            {
                if (CanUpdate) { result.ForeColor = Color.FromArgb(80, 102, 90); result.Text = "操作已取消。"; }
            }
            catch
            {
                if (CanUpdate)
                {
                    result.ForeColor = Color.Firebrick;
                    result.Text = start ? "启动失败，请检查端口是否被占用；详情见WCAE日志。" : "停止失败，请稍后重试；详情见WCAE日志。";
                }
            }
            finally
            {
                switching = false;
                FinishOperation();
            }
        }

        void FinishOperation()
        {
            if (IsDisposed || Disposing) DisposeLifetime();
            else if (closingRequested)
            {
                allowClose = true;
                Close();
            }
            else RefreshStatus();
        }

        void HandleClosing(object sender, FormClosingEventArgs e)
        {
            if (allowClose) return;
            closingRequested = true;
            refresh.Stop();
            lifetime.Cancel();
            if (Busy && (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.FormOwnerClosing))
            {
                e.Cancel = true;
                result.ForeColor = Color.FromArgb(80, 102, 90);
                result.Text = "正在取消操作并等待服务清理，请稍候…";
                close.Enabled = false;
                UpdateButtons();
            }
        }

        void DisposeLifetime()
        {
            if (Interlocked.Exchange(ref lifetimeDisposed, 1) == 0) lifetime.Dispose();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closingRequested = true;
                refresh.Dispose();
                if (Volatile.Read(ref lifetimeDisposed) == 0) lifetime.Cancel();
                if (!Busy) DisposeLifetime();
            }
            base.Dispose(disposing);
        }
    }
}
