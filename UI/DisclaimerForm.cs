using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace WCAE
{
    internal sealed class DisclaimerForm : Form
    {
        readonly CheckBox agreement = new CheckBox
        {
            AutoSize = false, Checked = false, ThreeState = false,
            CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft,
            UseVisualStyleBackColor = true, TabIndex = 0, Name = "agreement"
        };
        readonly Button enter = new ContinueButton { Text = "同意并进入", DialogResult = DialogResult.None, TabIndex = 1, Name = "enter" };
        readonly Label hint = new Label { AutoSize = false, ForeColor = Color.FromArgb(156, 48, 48), TextAlign = ContentAlignment.MiddleLeft, Name = "hint" };
        readonly Panel agreementMotion = new Panel { BackColor = Color.White, Name = "agreementMotion" };
        readonly System.Windows.Forms.Timer shakeTimer = new System.Windows.Forms.Timer { Interval = 16 };
        readonly Stopwatch shakeWatch = new Stopwatch();
        readonly RoundedSurface card = new RoundedSurface(Color.White, true);
        readonly TableLayoutPanel shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.White, Margin = Padding.Empty };
        readonly Panel header = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
        readonly Label title = new Label { Text = "免责声明", AutoSize = false, ForeColor = Color.FromArgb(38, 47, 44), TextAlign = ContentAlignment.MiddleLeft };
        readonly Label subtitle = new Label { Text = "使用前请仔细阅读，您需要确认后才能进入程序", AutoSize = false, ForeColor = Color.FromArgb(104, 122, 112), TextAlign = ContentAlignment.TopLeft };
        readonly Panel separator = new Panel { BackColor = Color.FromArgb(226, 234, 229) };
        readonly FlowLayoutPanel body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, BackColor = Color.White, Margin = Padding.Empty, TabStop = false
        };
        readonly TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.White, Margin = Padding.Empty };
        readonly Panel agreementStage = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
        readonly Panel buttonRow = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
        readonly List<Label> bodyLabels = new List<Label>();
        readonly RoundedSurface confirmation = new RoundedSurface(Color.FromArgb(232, 245, 238), false);
        readonly Label confirmationText = new Label { AutoSize = false, ForeColor = Color.FromArgb(20, 114, 78), TextAlign = ContentAlignment.TopLeft };
        bool arranging, shakeDisposed;
        int shakeOriginX;

        internal bool Accepted { get; private set; }
        internal int ShakeOffset => agreementMotion.Left - shakeOriginX;

        internal DisclaimerForm()
        {
            Text = "WCAE";
            Icon = AppBranding.CreateApplicationIcon();
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(242, 246, 243);
            ClientSize = new Size(680, 650);

            card.Dock = DockStyle.Fill;
            card.Controls.Add(shell);
            Controls.Add(card);
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            shell.Controls.Add(header, 0, 0);
            shell.Controls.Add(body, 0, 1);
            shell.Controls.Add(footer, 0, 2);
            title.Font = new Font(Font.FontFamily, 18F, FontStyle.Bold);
            subtitle.Font = new Font(Font.FontFamily, 9F);
            hint.Font = new Font(Font.FontFamily, 9F);
            enter.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            header.Controls.Add(title); header.Controls.Add(subtitle); header.Controls.Add(separator);

            AddBodyLabel(DisclaimerContent.Introduction, false);
            for (int i = 0; i < DisclaimerContent.Sections.Length; i++)
            {
                var section = DisclaimerContent.Sections[i];
                AddBodyLabel((i + 1) + ". " + section.Heading, true);
                AddBodyLabel(section.Body, false);
            }
            confirmationText.Text = DisclaimerContent.Confirmation;
            confirmationText.Font = new Font(Font, FontStyle.Bold);
            confirmation.Controls.Add(confirmationText);
            body.Controls.Add(confirmation);

            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            agreement.Text = DisclaimerContent.CheckboxText;
            agreement.AccessibleName = DisclaimerContent.CheckboxText;
            agreement.Dock = DockStyle.Fill;
            agreementMotion.Controls.Add(agreement);
            agreementStage.Controls.Add(agreementMotion);
            footer.Controls.Add(agreementStage, 0, 0);
            hint.Dock = DockStyle.Fill; hint.Margin = Padding.Empty;
            footer.Controls.Add(hint, 0, 1);
            buttonRow.Controls.Add(enter); footer.Controls.Add(buttonRow, 0, 2);
            AcceptButton = enter;

            agreement.CheckedChanged += (_, __) =>
            {
                if (agreement.Checked) { hint.Text = ""; StopShake(); }
            };
            enter.Click += (_, __) =>
            {
                if (!agreement.Checked)
                {
                    hint.Text = "请先勾选确认，再进入WCAE。";
                    BeginShake();
                    agreement.Focus();
                    return;
                }
                StopShake();
                Accepted = true;
                DialogResult = DialogResult.OK;
                Close();
            };
            shakeTimer.Tick += (_, __) =>
            {
                double elapsed = shakeWatch.Elapsed.TotalMilliseconds / 400d;
                if (elapsed >= 1 || IsDisposed || Disposing) { StopShake(); return; }
                int offset = (int)Math.Round(Math.Sin(elapsed * Math.PI * 8) * Scale(8) * (1 - elapsed));
                agreementMotion.Left = shakeOriginX + offset;
            };
            shell.SizeChanged += (_, __) => ArrangeContent();
            body.SizeChanged += (_, __) => ArrangeContent();
            buttonRow.SizeChanged += (_, __) => ArrangeContent();
            Load += (_, __) => { FitWorkingArea(); ArrangeContent(); };
            Shown += (_, __) => agreement.Focus();
            ArrangeContent();
        }

        void AddBodyLabel(string text, bool heading)
        {
            var label = new Label
            {
                Text = text ?? "", AutoSize = true, UseMnemonic = false, TabStop = false,
                ForeColor = heading ? Color.FromArgb(22, 124, 78) : Color.FromArgb(103, 114, 109)
            };
            if (heading) label.Font = new Font(Font, FontStyle.Bold);
            label.Tag = heading;
            bodyLabels.Add(label); body.Controls.Add(label);
        }

        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

        void FitWorkingArea()
        {
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int maxWidth = Math.Max(280, work.Width - Scale(32));
            int maxHeight = Math.Max(320, work.Height - Scale(40));
            Size = new Size(Math.Min(Width, maxWidth), Math.Min(Height, maxHeight));
            // Screen-outside previews deliberately keep their manual position. Real startup
            // dialogs remain wholly inside the work area, including the confirmation button.
            if (StartPosition != FormStartPosition.Manual)
                Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
        }

        void ArrangeContent()
        {
            if (arranging || IsDisposed || shell.RowStyles.Count < 3 || footer.RowStyles.Count < 3) return;
            arranging = true;
            try
            {
                Padding = new Padding(Scale(10));
                card.Padding = new Padding(Scale(24), Scale(20), Scale(24), Scale(18));
                int width = Math.Max(120, shell.ClientSize.Width);
                int titleHeight = TextRenderer.MeasureText(title.Text, title.Font).Height + Scale(8);
                int subtitleHeight = TextRenderer.MeasureText(subtitle.Text, subtitle.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + Scale(6);
                int headerHeight = titleHeight + subtitleHeight + Scale(12);
                title.SetBounds(0, 0, width, titleHeight);
                subtitle.SetBounds(0, titleHeight + Scale(2), width, subtitleHeight);
                separator.SetBounds(0, headerHeight - 1, width, 1);
                shell.RowStyles[0].Height = headerHeight;
                body.Padding = new Padding(0, Scale(10), Scale(6), Scale(8));
                int bodyWidth = Math.Max(100, body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - body.Padding.Horizontal - Scale(2));
                foreach (Label label in bodyLabels)
                {
                    bool heading = label.Tag is true;
                    label.MaximumSize = new Size(bodyWidth, 0);
                    label.Margin = new Padding(0, heading ? Scale(3) : 0, 0, heading ? Scale(4) : Scale(11));
                }
                int emphasisPadding = Scale(12);
                int emphasisTextWidth = Math.Max(60, bodyWidth - emphasisPadding * 2);
                int emphasisHeight = TextRenderer.MeasureText(confirmationText.Text, confirmationText.Font, new Size(emphasisTextWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;
                confirmation.Size = new Size(bodyWidth, emphasisHeight + emphasisPadding * 2);
                confirmation.Margin = new Padding(0, Scale(3), 0, Scale(4));
                confirmationText.SetBounds(emphasisPadding, emphasisPadding, emphasisTextWidth, emphasisHeight);

                int offset = shakeTimer.Enabled ? ShakeOffset : 0;
                shakeOriginX = Scale(9);
                int agreementWidth = Math.Max(100, width - shakeOriginX * 2);
                int agreementHeight = TextRenderer.MeasureText(agreement.Text, agreement.Font,
                    new Size(Math.Max(60, agreementWidth - Scale(26)), int.MaxValue), TextFormatFlags.WordBreak).Height + Scale(6);
                agreementMotion.SetBounds(shakeOriginX + offset, Scale(4), agreementWidth, agreementHeight);
                int hintHeight = Math.Max(Scale(18), TextRenderer.MeasureText("请先勾选确认，再进入WCAE。", hint.Font).Height + Scale(2));
                int buttonHeight = Math.Max(Scale(42), TextRenderer.MeasureText(enter.Text, enter.Font).Height + Scale(16));
                footer.RowStyles[0].Height = agreementHeight + Scale(4);
                footer.RowStyles[1].Height = hintHeight;
                footer.RowStyles[2].Height = buttonHeight + Scale(6);
                footer.Padding = new Padding(0, Scale(8), 0, 0);
                shell.RowStyles[2].Height = agreementHeight + Scale(4) + hintHeight + buttonHeight + Scale(6) + footer.Padding.Vertical;
                hint.Padding = new Padding(shakeOriginX, 0, 0, 0);
                int buttonWidth = Math.Max(Scale(128), TextRenderer.MeasureText(enter.Text, enter.Font).Width + Scale(34));
                enter.SetBounds(Math.Max(0, buttonRow.ClientSize.Width - buttonWidth), Scale(4), buttonWidth, buttonHeight);
            }
            finally { arranging = false; }
        }

        void BeginShake()
        {
            StopShake();
            agreementMotion.Left = shakeOriginX + Scale(8);
            shakeWatch.Restart(); shakeTimer.Start();
        }

        void StopShake()
        {
            if (!shakeDisposed) shakeTimer.Stop(); shakeWatch.Stop();
            if (!agreementMotion.IsDisposed) agreementMotion.Left = shakeOriginX;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Accepted = false; StopShake(); DialogResult = DialogResult.Cancel; Close(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopShake();
            if (!Accepted || DialogResult != DialogResult.OK) { Accepted = false; DialogResult = DialogResult.Cancel; }
            base.OnFormClosing(e);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            FitWorkingArea(); ArrangeContent();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !shakeDisposed) { StopShake(); shakeTimer.Dispose(); shakeDisposed = true; }
            base.Dispose(disposing);
        }

        internal static void RenderPreview(string path)
        {
            using var form = new DisclaimerForm { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
            form.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(path, ImageFormat.Png);
            form.Close(); Application.DoEvents();
        }

        static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            radius = Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2f);
            var path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(rectangle); return path; }
            float diameter = radius * 2;
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }

        sealed class RoundedSurface : Panel
        {
            readonly Color fill;
            readonly bool border;
            internal RoundedSurface(Color fill, bool border)
            {
                this.fill = fill; this.border = border;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
                BackColor = fill;
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent?.BackColor ?? Color.White);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundedRectangle(new RectangleF(.5f, .5f, Math.Max(0, Width - 1), Math.Max(0, Height - 1)), (border ? 12 : 8) * DeviceDpi / 96f);
                using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, path);
                if (border) { using var pen = new Pen(Color.FromArgb(219, 229, 223)); e.Graphics.DrawPath(pen, path); }
            }
        }

        sealed class ContinueButton : Button
        {
            bool hovered, pressed;
            internal ContinueButton()
            {
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
                BackColor = Color.FromArgb(24, 126, 91); ForeColor = Color.White;
                UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent?.BackColor ?? Color.White);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundedRectangle(new RectangleF(0, 0, Width, Height), 7 * DeviceDpi / 96f);
                using var brush = new SolidBrush(pressed ? Color.FromArgb(15, 98, 69) : hovered ? Color.FromArgb(20, 114, 80) : BackColor);
                e.Graphics.FillPath(brush, path);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), Color.White, BackColor);
            }
            protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        }
    }
}
