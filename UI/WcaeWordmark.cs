using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WCAE
{
    // The label keeps the original title's baseline and accessibility while its preferred
    // width follows the tighter spacing. Four polygon letters use the reference's diagonal
    // terminals, sharp corners and open A counter. No font is needed to draw the wordmark.
    internal sealed class WcaeWordmark : Label
    {
        const string Word = "WCAE";
        const float OriginalOutlineWidth = 348.05f;
        const float SpacingReduction = 40f;
        // The original 20 pt label has a 94 px advance and approximately 80 px of ink.
        // Before the first paint this ratio avoids a second raster scan just for layout.
        const float InitialInkRatio = 80f / 94f;
        const TextFormatFlags MeasureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        readonly GraphicsPath outline = CreateOutline();
        RectangleF cachedInk;
        Size cachedMeasurement;
        float cachedDpiX, cachedDpiY;
        bool hasInk;

        internal WcaeWordmark()
        {
            Text = Word;
            AccessibleName = Word;
            ForeColor = Color.Black;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            hasInk = false;
            base.OnFontChanged(e);
            Invalidate();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            hasInk = false;
            base.OnDpiChangedAfterParent(e);
            Invalidate();
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size original = base.GetPreferredSize(proposedSize);
            float inkWidth = hasInk && Math.Abs(cachedDpiX - DeviceDpi) < .1f
                ? cachedInk.Width : Math.Max(1, original.Width - Padding.Horizontal) * InitialInkRatio;
            int removed = (int)Math.Round(RemovedPixels(inkWidth));
            return new Size(Math.Max(Padding.Horizontal + 1, original.Width - removed), original.Height);
        }

        static float RemovedPixels(float inkWidth) => Math.Max(0, inkWidth - .5f) * SpacingReduction / OriginalOutlineWidth;

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle content = Rectangle.FromLTRB(Padding.Left, Padding.Top, ClientSize.Width - Padding.Right, ClientSize.Height - Padding.Bottom);
            if (content.Width <= 0 || content.Height <= 0) return;
            Size measured = TextRenderer.MeasureText(e.Graphics, Word, Font, new Size(int.MaxValue, int.MaxValue), MeasureFlags);
            if (!hasInk || measured != cachedMeasurement || Math.Abs(cachedDpiX - e.Graphics.DpiX) > .1f || Math.Abs(cachedDpiY - e.Graphics.DpiY) > .1f)
            {
                cachedInk = MeasureTitleInk(e.Graphics, measured);
                cachedMeasurement = measured;
                cachedDpiX = e.Graphics.DpiX; cachedDpiY = e.Graphics.DpiY;
                hasInk = true;
            }
            float compactAdvance = measured.Width - RemovedPixels(cachedInk.Width);
            float left = content.Left, top = content.Top;
            if (TextAlign is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter)
                left += (content.Width - compactAdvance) / 2f;
            else if (TextAlign is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight)
                left += content.Width - compactAdvance;
            if (TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight)
                top += (content.Height - measured.Height) / 2f;
            else if (TextAlign is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight)
                top += content.Height - measured.Height;

            // Matching the old text's measured black pixels prevents AutoSize's line-height
            // whitespace from accidentally making the new mark taller or wider than the title.
            var ink = new RectangleF(left + cachedInk.Left, top + cachedInk.Top, cachedInk.Width, cachedInk.Height);
            if (ink.Width <= 1 || ink.Height <= 1) return;
            var bounds = outline.GetBounds();
            // Keep every glyph at its original scale. Fitting the narrower path back into
            // the old ink width would stretch the letters and undo the requested spacing.
            float sx = (ink.Width - .5f) / OriginalOutlineWidth, sy = (ink.Height - .5f) / bounds.Height;
            GraphicsState saved = e.Graphics.Save();
            try
            {
                e.Graphics.SetClip(content, CombineMode.Intersect);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var transform = new Matrix(sx, 0, 0, sy, ink.Left + .25f - bounds.Left * sx, ink.Top + .25f - bounds.Top * sy);
                e.Graphics.MultiplyTransform(transform);
                e.Graphics.FillPath(Brushes.Black, outline);
            }
            finally { e.Graphics.Restore(saved); }
        }

        RectangleF MeasureTitleInk(Graphics target, Size measured)
        {
            // Measuring an offscreen copy of the existing label font is for sizing only.
            // This also preserves the old glyph padding and baseline at different font sizes.
            int width = Math.Clamp(measured.Width + 16, 32, 2048), height = Math.Clamp(measured.Height + 16, 32, 512);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            bitmap.SetResolution(target.DpiX, target.DpiY);
            Size rasterMeasured;
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                rasterMeasured = TextRenderer.MeasureText(graphics, Word, Font, new Size(int.MaxValue, int.MaxValue), MeasureFlags);
                TextRenderer.DrawText(graphics, Word, Font, Point.Empty, Color.Black, Color.White, MeasureFlags);
            }
            int left = width, top = height, right = -1, bottom = -1;
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < width; x++)
                    {
                        int p = x * 4;
                        if (row[p] > 245 && row[p + 1] > 245 && row[p + 2] > 245) continue;
                        left = Math.Min(left, x); top = Math.Min(top, y);
                        right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                    }
                }
            }
            finally { bitmap.UnlockBits(data); }
            if (right < left || bottom < top)
                return new RectangleF(measured.Height * .12f, measured.Height * .18f, Math.Max(1, measured.Width - measured.Height * .24f), measured.Height * .65f);
            float sx = measured.Width / (float)Math.Max(1, rasterMeasured.Width);
            float sy = measured.Height / (float)Math.Max(1, rasterMeasured.Height);
            return new RectangleF(left * sx, top * sy, (right - left + 1) * sx, (bottom - top + 1) * sy);
        }

        static GraphicsPath CreateOutline()
        {
            var path = new GraphicsPath(FillMode.Winding);
            // W: two descending wedges with a split central stem. It stays recognizably W
            // at header size, rather than borrowing the reference's M-shaped silhouette.
            Add(path, 0, .75f, new[] {
                new PointF(22, 0), new PointF(46, 0), new PointF(40, 57), new PointF(71, 12),
                new PointF(92, 12), new PointF(88, 57), new PointF(123, 0), new PointF(149, 0),
                new PointF(89, 100), new PointF(64, 100), new PointF(69, 53), new PointF(36, 100), new PointF(10, 100) });
            // C: angular left shoulder and matching diagonal cuts in the two open arms.
            Add(path, 101.75f, .70f, new[] {
                new PointF(96, 0), new PointF(25, 0), new PointF(3, 30), new PointF(0, 69),
                new PointF(21, 100), new PointF(66, 100), new PointF(86, 73), new PointF(35, 73),
                new PointF(25, 57), new PointF(36, 30), new PointF(72, 30) });
            // A: the reference's sharp peak and open diagonal counter, without a tiny hole
            // that would fill in when the title is rendered at roughly 20–25 pixels high.
            Add(path, 158.95f, .70f, new[] {
                new PointF(0, 100), new PointF(63, 0), new PointF(114, 100), new PointF(36, 100),
                new PointF(51, 74), new PointF(79, 74), new PointF(61, 39), new PointF(24, 100) });
            // E: three full-width-cut bars, a forward stem, and no rounded font outlines.
            Add(path, 242.75f, .65f, new[] {
                new PointF(24, 0), new PointF(112, 0), new PointF(92, 25), new PointF(47, 25),
                new PointF(41, 40), new PointF(84, 40), new PointF(65, 63), new PointF(34, 63),
                new PointF(30, 77), new PointF(84, 77), new PointF(63, 100), new PointF(0, 100) });
            return path;
        }

        static void Add(GraphicsPath path, float offset, float scale, PointF[] points)
        {
            for (int i = 0; i < points.Length; i++) points[i] = new PointF(offset + points[i].X * scale, points[i].Y);
            path.AddPolygon(points);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) outline.Dispose();
            base.Dispose(disposing);
        }
    }
}
