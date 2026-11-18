using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ButtonBox.App;

internal static class TrayIcon
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("tray-logo.png")
            ?? throw new InvalidOperationException("Embedded tray logo is missing.");
        using var source = new Bitmap(stream);
        var size = SystemInformation.SmallIconSize;
        using var scaled = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, size.Width, size.Height);
        }

        var hIcon = scaled.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
