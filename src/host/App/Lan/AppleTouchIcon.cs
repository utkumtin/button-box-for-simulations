using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ButtonBox.App.Lan;

/// <summary>The home-screen icon: the product logo resized to 180x180 px (decisions §7), rendered once at startup.</summary>
public static class AppleTouchIcon
{
    public const int Size = 180;

    public static byte[] Render(byte[] logoPng)
    {
        ArgumentNullException.ThrowIfNull(logoPng);
        using var input = new MemoryStream(logoPng);
        using var source = new Bitmap(input);
        using var scaled = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, Size, Size);
        }

        using var output = new MemoryStream();
        scaled.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}
