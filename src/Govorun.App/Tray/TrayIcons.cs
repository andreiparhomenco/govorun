using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;

namespace Govorun.App.Tray;

/// <summary>The tray icon: the Govorun parrot, full-bleed, no status decorations.</summary>
public static class TrayIcons
{
    public static Icon App { get; } = Compose();

    private static Icon Compose()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("logo.png", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var logo = new Bitmap(stream);

        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(logo, 0, 0, size, size);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
