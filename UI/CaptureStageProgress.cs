using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WCAE
{
    /// <summary>Centered connection, identification and account-name progress.</summary>
    public sealed class CaptureStageProgress : Control
    {
        public bool Connected { get; private set; }
        public bool Identified { get; private set; }
        public string AccountName { get; private set; } = string.Empty;

        public CaptureStageProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(1080, 78);
            TabStop = false;
            AccessibleRole = AccessibleRole.Indicator;
            AccessibleName = "公众号识别进度";
            UpdateAccessibleDescription();
        }

        public void SetStages(bool connected, bool identified, string accountName)
        {
            string name = AccountNameResolver.Normalize(accountName);
            if (Connected == connected && Identified == identified && AccountName == name)
                return;
            Connected = connected;
            Identified = identified;
            AccountName = name;
            UpdateAccessibleDescription();
            Invalidate();
        }

        void UpdateAccessibleDescription()
        {
            AccessibleDescription = "接入状态：" + (Connected ? "已完成" : "未完成") +
                "；识别状态：" + (Identified ? "已完成" : "未完成") +
                "；公众号名称：" + (string.IsNullOrEmpty(AccountName) ? "待识别" : AccountName);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width < 4 || ClientSize.Height < 4) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = DeviceDpi / 96f;
            float available = Math.Min(ClientSize.Width, 1080f * scale);
            float cellWidth = available / 3f;
            float left = (ClientSize.Width - available) / 2f;
            float diameter = Math.Min(30f * scale, Math.Max(12f * scale, cellWidth * .38f));
            float labelHeight = Math.Max(Font.Height + 5f * scale, 23f * scale);
            float blockHeight = diameter + 8f * scale + labelHeight;
            float top = Math.Max(0, (ClientSize.Height - blockHeight) / 2f);
            float centerY = top + diameter / 2f;
            bool[] complete = { Connected, Identified, Identified && !string.IsNullOrEmpty(AccountName) };
            string[] labels = { "接入状态", "识别状态", string.IsNullOrEmpty(AccountName) ? (Identified ? "名称待识别" : "公众号名称") : AccountName };

            for (int i = 0; i < complete.Length - 1; i++)
            {
                float x1 = left + cellWidth * (i + .5f) + diameter / 2f + 11f * scale;
                float x2 = left + cellWidth * (i + 1.5f) - diameter / 2f - 11f * scale;
                if (x2 <= x1) continue;
                using var line = new Pen(complete[i] && complete[i + 1]
                    ? Color.FromArgb(167, 213, 186) : Color.FromArgb(207, 219, 211), 2f * scale);
                line.StartCap = LineCap.Round;
                line.EndCap = LineCap.Round;
                g.DrawLine(line, x1, centerY, x2, centerY);
            }

            for (int i = 0; i < complete.Length; i++)
            {
                float centerX = left + cellWidth * (i + .5f);
                var circle = new RectangleF(centerX - diameter / 2f, top, diameter, diameter);
                using var fill = new SolidBrush(complete[i] ? Color.FromArgb(222, 243, 230) : Color.FromArgb(235, 239, 236));
                using var border = new Pen(complete[i] ? Color.FromArgb(81, 166, 112) : Color.FromArgb(178, 190, 182), 1.35f * scale);
                g.FillEllipse(fill, circle);
                g.DrawEllipse(border, circle);
                if (complete[i])
                {
                    using var check = new Pen(Color.FromArgb(34, 143, 78), 2.7f * scale)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round,
                        LineJoin = LineJoin.Round
                    };
                    g.DrawLines(check, new[]
                    {
                        new PointF(centerX - diameter * .22f, centerY),
                        new PointF(centerX - diameter * .065f, centerY + diameter * .16f),
                        new PointF(centerX + diameter * .235f, centerY - diameter * .17f)
                    });
                }
                var textBounds = Rectangle.Round(new RectangleF(left + cellWidth * i + 5f * scale,
                    top + diameter + 8f * scale, Math.Max(1, cellWidth - 10f * scale), labelHeight));
                TextRenderer.DrawText(g, labels[i], Font, textBounds,
                    complete[i] ? Color.FromArgb(35, 104, 67) : Color.FromArgb(116, 129, 120),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }
}
