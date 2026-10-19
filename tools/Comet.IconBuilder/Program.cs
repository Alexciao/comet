using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Draws Comet's icons: a comet (round head, tapered tail) with a small sparkle.
//   Comet.ico      app icon, full color, 16–256 px
//   TrayLight.ico  dark glyph for light taskbars
//   TrayDark.ico   light glyph for dark taskbars
//   Tray*Paused.ico the same glyphs dimmed, shown while Comet is paused
//   AppIcon16/48.scale-*.png  title bar and Settings pane icons, one file per display scale
// Every size is drawn from vectors (no downscaling) so 16 px stays crisp.

var root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "Comet.sln")))
    root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Run inside the repository.");
var assets = Path.Combine(root, "src", "Comet.App", "Assets");
Directory.CreateDirectory(assets);

WriteIcon(Path.Combine(assets, "Comet.ico"), [16, 20, 24, 32, 40, 48, 64, 96, 128, 256], DrawColor);
WriteIcon(Path.Combine(assets, "TrayLight.ico"), [16, 20, 24, 32, 40, 48, 64], s => DrawGlyph(s, Color.FromArgb(0x1A, 0x1A, 0x1A)));
WriteIcon(Path.Combine(assets, "TrayDark.ico"), [16, 20, 24, 32, 40, 48, 64], s => DrawGlyph(s, Color.White));
WriteIcon(Path.Combine(assets, "TrayLightPaused.ico"), [16, 20, 24, 32, 40, 48, 64], s => DrawGlyph(s, Color.FromArgb(0x70, 0x1A, 0x1A, 0x1A)));
WriteIcon(Path.Combine(assets, "TrayDarkPaused.ico"), [16, 20, 24, 32, 40, 48, 64], s => DrawGlyph(s, Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
foreach (var (scale, factor) in new[] { (100, 1.0), (125, 1.25), (150, 1.5), (200, 2.0) })
{
    foreach (var logical in new[] { 16, 48 })
    {
        using var bmp = DrawColor((int)Math.Round(logical * factor));
        bmp.Save(Path.Combine(assets, $"AppIcon{logical}.scale-{scale}.png"), ImageFormat.Png);
    }
}

using (var preview = DrawColor(256))
    preview.Save(Path.Combine(assets, "Comet.png"), ImageFormat.Png);
Console.WriteLine($"icons written to {assets}");

// Geometry in a 0..1 box: head lower-right, tail streaming to the upper-left.
static (PointF Head, float Radius, PointF Tip) Geometry(int size)
{
    var s = (float)size;
    var pad = s < 24 ? 0.5f : s * 0.04f;
    var head = new PointF(s * 0.64f, s * 0.64f);
    var radius = s * 0.24f;
    var tip = new PointF(pad + (s * 0.06f), pad + (s * 0.06f));
    return (head, radius, tip);
}

static GraphicsPath CometPath(int size)
{
    var (head, r, tip) = Geometry(size);
    var dx = head.X - tip.X;
    var dy = head.Y - tip.Y;
    var len = MathF.Sqrt((dx * dx) + (dy * dy));
    var px = -dy / len;
    var py = dx / len;
    var path = new GraphicsPath { FillMode = FillMode.Winding };
    // Tail: a wedge from the tip to the head's tangent points, then the head.
    path.AddPolygon([tip, new PointF(head.X + (px * r), head.Y + (py * r)), new PointF(head.X - (px * r), head.Y - (py * r))]);
    path.AddEllipse(head.X - r, head.Y - r, r * 2, r * 2);
    return path;
}

static void Sparkle(Graphics g, Brush brush, int size)
{
    // A four-point star in the upper-right, balancing the tail.
    var c = new PointF(size * 0.80f, size * 0.20f);
    var a = size * 0.13f;
    var b = a * 0.28f;
    g.FillPolygon(brush, new[]
    {
        new PointF(c.X, c.Y - a), new PointF(c.X + b, c.Y - b), new PointF(c.X + a, c.Y), new PointF(c.X + b, c.Y + b),
        new PointF(c.X, c.Y + a), new PointF(c.X - b, c.Y + b), new PointF(c.X - a, c.Y), new PointF(c.X - b, c.Y - b),
    });
}

static Bitmap DrawGlyph(int size, Color color)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    using var brush = new SolidBrush(color);
    using var path = CometPath(size);
    g.FillPath(brush, path);
    if (size >= 20)
        Sparkle(g, brush, size);
    return bmp;
}

static Bitmap DrawColor(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    var (head, r, tip) = Geometry(size);

    // Tail: violet fading into transparency toward the tip.
    using (var tail = CometPath(size))
    using (var gradient = new LinearGradientBrush(tip, head, Color.FromArgb(0, 0x7B, 0x61, 0xFF), Color.FromArgb(255, 0x7B, 0x61, 0xFF)))
        g.FillPath(gradient, tail);

    // Head: warm core with a lighter center.
    using (var headPath = new GraphicsPath())
    {
        headPath.AddEllipse(head.X - r, head.Y - r, r * 2, r * 2);
        using var radial = new PathGradientBrush(headPath)
        {
            CenterColor = Color.FromArgb(255, 0xFF, 0xE0, 0x8A),
            SurroundColors = [Color.FromArgb(255, 0xFF, 0x8A, 0x3D)],
            CenterPoint = new PointF(head.X - (r * 0.25f), head.Y - (r * 0.25f)),
        };
        g.FillPath(radial, headPath);
    }

    if (size >= 20)
    {
        using var sparkle = new SolidBrush(Color.FromArgb(255, 0xFF, 0xC8, 0x3D));
        Sparkle(g, sparkle, size);
    }

    return bmp;
}

static void WriteIcon(string path, int[] sizes, Func<int, Bitmap> draw)
{
    var images = sizes.Select(s =>
    {
        using var bmp = draw(s);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return (Size: s, Png: ms.ToArray());
    }).ToList();

    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((short)0);
    w.Write((short)1);
    w.Write((short)images.Count);
    var offset = 6 + (16 * images.Count);
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }

    foreach (var (_, png) in images)
        w.Write(png);
}
