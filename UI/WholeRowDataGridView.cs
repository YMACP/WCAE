using System;
using System.Drawing;
using System.Windows.Forms;

namespace WCAE
{
    /// <summary>Keeps a clipped bottom row from drawing a fragment of its text or checkbox.</summary>
    public sealed class WholeRowDataGridView : DataGridView
    {
        public WholeRowDataGridView()
        {
            DoubleBuffered = true;
        }

        int DataViewportBottom
        {
            get
            {
                int border = BorderStyle switch
                {
                    BorderStyle.FixedSingle => SystemInformation.BorderSize.Height,
                    BorderStyle.Fixed3D => SystemInformation.Border3DSize.Height,
                    _ => 0
                };
                int bottom = ClientRectangle.Bottom - border;
                foreach (Control child in Controls)
                    if (child is HScrollBar && child.Visible)
                        bottom = Math.Min(bottom, child.Top);
                return bottom;
            }
        }

        protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex >= 0 && e.CellBounds.Bottom > DataViewportBottom)
            {
                Rectangle visiblePart = Rectangle.Intersect(e.CellBounds, e.ClipBounds);
                visiblePart.Intersect(new Rectangle(0, 0, ClientSize.Width, Math.Max(0, DataViewportBottom)));
                if (!visiblePart.IsEmpty)
                {
                    using var background = new SolidBrush(BackgroundColor);
                    e.Graphics.FillRectangle(background, visiblePart);
                }
                e.Handled = true;
                return;
            }
            base.OnCellPainting(e);
        }

        protected override void OnScroll(ScrollEventArgs e)
        {
            base.OnScroll(e);
            // Native scrolling may copy already-painted pixels; redraw against the new viewport.
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Invalidate();
        }
    }
}
