using System.Drawing;
using System.IO;

namespace WCAE
{
    internal static class AppBranding
    {
        // Exact icon from the Windows Forms runtime already used by WCAE's window.
        internal static Icon CreateApplicationIcon()
        {
            using (var stream = typeof(AppBranding).Assembly.GetManifestResourceStream("WCAE.ApplicationIcon"))
            {
                if (stream == null) throw new InvalidDataException("程序图标资源缺失。");
                using (var icon = new Icon(stream)) return (Icon)icon.Clone();
            }
        }
    }
}
