using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WCAE
{
    public enum ActionIcon { Start, Stop, Reconnect, Search, ClearCache, Settings, Mcp }

    /// <summary>A keyboard accessible action button with a vector icon.</summary>
    public sealed class IconActionButton : Button
    {
        readonly ActionIcon icon;
        bool hovered;
        bool pressed;

        public IconActionButton(ActionIcon icon, string accessibleName)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.icon = icon;
            AccessibleName = accessibleName;
            AccessibleDescription = accessibleName;
            AccessibleRole = AccessibleRole.PushButton;
            Text = string.Empty;
            Size = new Size(36, 36);
            MinimumSize = new Size(32, 32);
            Margin = new Padding(0, 0, 6, 0);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = false; Invalidate(); } base.OnKeyUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { pressed = false; hovered = false; Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // Erase ButtonBase's native pixels with the toolbar background; draw only the icon.
            g.Clear(Parent?.BackColor ?? SystemColors.Control);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = DeviceDpi / 96f;
            Color normal = icon switch
            {
                ActionIcon.Start => Color.FromArgb(43, 151, 89),
                ActionIcon.Stop => Color.FromArgb(215, 67, 67),
                ActionIcon.ClearCache => Color.FromArgb(215, 67, 67),
                ActionIcon.Reconnect => Color.FromArgb(218, 170, 13),
                _ => Color.FromArgb(77, 94, 84)
            };
            bool keyboardFocused = Enabled && Focused && ShowFocusCues;
            Color foreground = !Enabled ? Blend(normal, Color.White, 0.56f)
                : pressed ? Blend(normal, Color.Black, 0.25f)
                : hovered || keyboardFocused ? Blend(normal, Color.Black, 0.13f)
                : normal;

            float centerX = ClientSize.Width / 2f;
            float centerY = ClientSize.Height / 2f + (pressed && Enabled ? scale : 0);
            // Compact toolbar buttons reduce side padding without shrinking the glyph.
            float iconScale = Math.Min(scale, ClientSize.Height / 32f);
            if (keyboardFocused) iconScale *= 1.08f;
            using var foregroundBrush = new SolidBrush(foreground);
            if (icon == ActionIcon.Start)
            {
                g.FillPolygon(foregroundBrush, new[]
                {
                    new PointF(centerX - 5f * iconScale, centerY - 8f * iconScale),
                    new PointF(centerX + 8f * iconScale, centerY),
                    new PointF(centerX - 5f * iconScale, centerY + 8f * iconScale)
                });
            }
            else if (icon == ActionIcon.Stop)
            {
                g.FillRectangle(foregroundBrush, centerX - 8f * iconScale,
                    centerY - 8f * iconScale, 16f * iconScale, 16f * iconScale);
            }
            else if (icon == ActionIcon.Reconnect)
            {
                // Scale the complete 18 px stroked shape to the same 16 px height as start/stop.
                float reconnectScale = iconScale * (8f / 9f);
                float radius = 8f * reconnectScale;
                using var arc = new Pen(foreground, 2f * reconnectScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, centerX - radius, centerY - radius, radius * 2, radius * 2, 45, 300);
                double angle = -15 * Math.PI / 180;
                var tip = new PointF(centerX + radius * (float)Math.Cos(angle), centerY + radius * (float)Math.Sin(angle));
                float tangentX = -(float)Math.Sin(angle), tangentY = (float)Math.Cos(angle);
                float normalX = -tangentY, normalY = tangentX;
                g.FillPolygon(foregroundBrush, new[]
                {
                    new PointF(tip.X + tangentX * 2f * reconnectScale, tip.Y + tangentY * 2f * reconnectScale),
                    new PointF(tip.X - tangentX * 4f * reconnectScale + normalX * 3.5f * reconnectScale, tip.Y - tangentY * 4f * reconnectScale + normalY * 3.5f * reconnectScale),
                    new PointF(tip.X - tangentX * 4f * reconnectScale - normalX * 3.5f * reconnectScale, tip.Y - tangentY * 4f * reconnectScale - normalY * 3.5f * reconnectScale)
                });
            }
            else if (icon == ActionIcon.ClearCache)
            {
                using var stroke = new Pen(foreground, 1.9f * iconScale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                // A lid, raised handle and tapered bin, all within the same 16 px icon area.
                g.DrawLine(stroke, centerX - 7f * iconScale, centerY - 5f * iconScale,
                    centerX + 7f * iconScale, centerY - 5f * iconScale);
                g.DrawLines(stroke, new[]
                {
                    new PointF(centerX - 3f * iconScale, centerY - 5f * iconScale),
                    new PointF(centerX - 3f * iconScale, centerY - 8f * iconScale),
                    new PointF(centerX + 3f * iconScale, centerY - 8f * iconScale),
                    new PointF(centerX + 3f * iconScale, centerY - 5f * iconScale)
                });
                g.DrawLines(stroke, new[]
                {
                    new PointF(centerX - 5.5f * iconScale, centerY - 5f * iconScale),
                    new PointF(centerX - 4.5f * iconScale, centerY + 8f * iconScale),
                    new PointF(centerX + 4.5f * iconScale, centerY + 8f * iconScale),
                    new PointF(centerX + 5.5f * iconScale, centerY - 5f * iconScale)
                });
                g.DrawLine(stroke, centerX - 2f * iconScale, centerY - 1f * iconScale,
                    centerX - 1.7f * iconScale, centerY + 4.5f * iconScale);
                g.DrawLine(stroke, centerX + 2f * iconScale, centerY - 1f * iconScale,
                    centerX + 1.7f * iconScale, centerY + 4.5f * iconScale);
            }
            else if (icon == ActionIcon.Mcp)
            {
                // MCP's vector mark, adapted from the official docs favicon (MIT).
                // Source and license: docs/THIRD_PARTY_NOTICES.md / docs/licenses/MCP-logo-LICENSE.txt.
                var state = g.Save();
                try
                {
                    float factor = 20f * iconScale / 180f;
                    g.TranslateTransform(centerX - 90f * factor, centerY - 90f * factor);
                    g.ScaleTransform(factor, factor);
                    using var stroke = new Pen(foreground, 12f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    using var mark = new GraphicsPath();
                    mark.AddLine(18f,84.8528f,85.8822f,16.9706f);
                    mark.AddBezier(85.8822f,16.9706f,95.2548f,7.59798f,110.451f,7.59798f,119.823f,16.9706f);
                    mark.AddBezier(119.823f,16.9706f,129.196f,26.3431f,129.196f,41.5391f,119.823f,50.9117f);
                    mark.AddLine(119.823f,50.9117f,68.5581f,102.177f);
                    mark.StartFigure();
                    mark.AddLine(69.2652f,101.47f,119.823f,50.9117f);
                    mark.AddBezier(119.823f,50.9117f,129.196f,41.5391f,144.392f,41.5391f,153.765f,50.9117f);
                    mark.AddLine(153.765f,50.9117f,154.118f,51.2652f);
                    mark.AddBezier(154.118f,51.2652f,163.491f,60.6378f,163.491f,75.8338f,154.118f,85.2063f);
                    mark.AddLine(154.118f,85.2063f,92.7248f,146.6f);
                    mark.AddBezier(92.7248f,146.6f,89.6006f,149.724f,89.6006f,154.789f,92.7248f,157.913f);
                    mark.AddLine(92.7248f,157.913f,105.331f,170.52f);
                    mark.StartFigure();
                    mark.AddLine(102.853f,33.9411f,52.6482f,84.1457f);
                    mark.AddBezier(52.6482f,84.1457f,43.2756f,93.5183f,43.2756f,108.714f,52.6482f,118.087f);
                    mark.AddBezier(52.6482f,118.087f,62.0208f,127.459f,77.2167f,127.459f,86.5893f,118.087f);
                    mark.AddLine(86.5893f,118.087f,136.794f,67.8822f);
                    g.DrawPath(stroke, mark);
                }
                finally { g.Restore(state); }
            }
            else if (icon == ActionIcon.Settings)
            {
                using var stroke = new Pen(foreground, 1.8f * iconScale) { LineJoin = LineJoin.Round };
                var teeth = new PointF[32];
                for (int i = 0; i < teeth.Length; i++)
                {
                    double angle = (i * 360d / teeth.Length - 90) * Math.PI / 180;
                    float radius = (i % 4 == 1 || i % 4 == 2 ? 8.2f : 6.5f) * iconScale;
                    teeth[i] = new PointF(centerX + radius * (float)Math.Cos(angle), centerY + radius * (float)Math.Sin(angle));
                }
                g.DrawPolygon(stroke, teeth);
                g.DrawEllipse(stroke, centerX - 2.7f * iconScale, centerY - 2.7f * iconScale, 5.4f * iconScale, 5.4f * iconScale);
            }
            else if (icon == ActionIcon.Search)
            {
                using var stroke = new Pen(foreground, 2f * iconScale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawEllipse(stroke, centerX - 8f * iconScale, centerY - 8f * iconScale,
                    12f * iconScale, 12f * iconScale);
                g.DrawLine(stroke, centerX + 2.3f * iconScale, centerY + 2.3f * iconScale,
                    centerX + 8f * iconScale, centerY + 8f * iconScale);
            }
        }

        static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
