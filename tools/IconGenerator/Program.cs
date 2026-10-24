// App icon for ImageConverter.
//
// Rounded square in the app blue, containing a landscape with a sun ("image") and
// two white crop corners ("edit"). Drawn at 1024 px and downscaled with Lanczos
// so that even 16 px stays clean.
//
// Output to ImageConverter.Ui/Assets:
//   app.ico   — 16 to 256 px in one file (exe and window on Windows)
//   Icon.png  — 256 px (Linux: .desktop, AppImage)

using ImageConverter.Core.Imaging;
using ImageMagick;
using ImageMagick.Drawing;

var root = FindRepoRoot();
var assets = Path.Combine(root, "ImageConverter.Ui", "Assets");

const int S = 1024;
const int Inset = 40;                 // some space to the edge, as with system icons
const int Box = S - 2 * Inset;

// Background: diagonal gradient in the app blue, as a squircle-like rounded rectangle.
// The gradient is created larger, rotated and cut from the centre — a rotated
// square of side L contains, centred, an unrotated one of side L/√2.
const int Big = (int)(Box * 1.5);
using var bg = new MagickImage("gradient:#4C8DFF-#1D4ED8", Big, Big);
bg.Rotate(-45);
bg.ResetPage();   // otherwise the crop is measured from the shifted origin of the rotation
bg.Crop(new MagickGeometry((int)(bg.Width - Box) / 2, (int)(bg.Height - Box) / 2, Box, Box));
bg.ResetPage();
using var rounded = ImageOps.Crop(bg, 0, 0, Box, Box, radius: Box * 0.23);

using var icon = new MagickImage(MagickColors.Transparent, S, S);
icon.Composite(rounded, Inset, Inset, CompositeOperator.Over);

var white = new MagickColor("#FFFFFF");
var sun = new MagickColor("#FFD166");
var mountainBack = new MagickColor("#FFFFFF8C");   // semi-transparent: depth (hex notation is independent of Q8/Q16)

new Drawables()
    // sun
    .FillColor(sun).StrokeColor(MagickColors.Transparent)
    .Circle(668, 360, 668 + 74, 360)
    // rear mountain (semi-transparent), front mountain (white)
    .FillColor(mountainBack)
    .Polygon(new PointD(470, 740), new PointD(640, 500), new PointD(800, 740))
    .FillColor(white)
    .Polygon(new PointD(224, 740), new PointD(420, 450), new PointD(620, 740))
    // crop corners top left and bottom right
    .StrokeColor(white).StrokeWidth(46).StrokeLineCap(LineCap.Round).StrokeLineJoin(LineJoin.Round)
    .FillColor(MagickColors.Transparent)
    .Polyline(new PointD(196, 352), new PointD(196, 196), new PointD(352, 196))
    .Polyline(new PointD(672, 828), new PointD(828, 828), new PointD(828, 672))
    .Draw(icon);

// ICO: all sizes in ONE file. A MagickImageCollection would write each size as a
// separate file here (app-0.ico …) — the auto-resize define creates the sizes
// itself while writing, as in the export (ImageExporter, ICO format).
using (var ico = (MagickImage)icon.Clone())
{
    ico.FilterType = FilterType.Lanczos;
    ico.Resize(256, 256);
    ico.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,128,64,48,32,24,16");
    ico.Write(Path.Combine(assets, "app.ico"), MagickFormat.Icon);
}

using var png = (MagickImage)icon.Clone();
png.FilterType = FilterType.Lanczos;
png.Resize(256, 256);
png.Write(Path.Combine(assets, "Icon.png"), MagickFormat.Png32);

// preview at original size for viewing
icon.Write(Path.Combine(root, "tools", "IconGenerator", "icon-1024.png"), MagickFormat.Png32);

Console.WriteLine($"Icon geschrieben nach {assets}");

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ImageConverter.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("ImageConverter.sln nicht gefunden.");
}
