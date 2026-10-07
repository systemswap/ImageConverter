// App-Icon für ImageConverter.
//
// Abgerundetes Quadrat im App-Blau, darin eine Landschaft mit Sonne („Bild") und
// zwei weiße Zuschnitt-Ecken („bearbeiten"). Gezeichnet in 1024 px und mit Lanczos
// verkleinert — so bleiben auch 16 px sauber.
//
// Ausgabe nach ImageConverter.Ui/Assets:
//   app.ico   — 16 bis 256 px in einer Datei (exe und Fenster unter Windows)
//   Icon.png  — 256 px (Linux: .desktop, AppImage)

using ImageConverter.Core.Imaging;
using ImageMagick;
using ImageMagick.Drawing;

var root = FindRepoRoot();
var assets = Path.Combine(root, "ImageConverter.Ui", "Assets");

const int S = 1024;
const int Inset = 40;                 // etwas Luft zum Rand, wie bei Systemicons
const int Box = S - 2 * Inset;

// Hintergrund: diagonaler Verlauf im App-Blau, als „Squircle"-artiges Rundrechteck
// Verlauf größer erzeugen, drehen und aus der Mitte schneiden — ein gedrehtes
// Quadrat der Seite L enthält mittig ein ungedrehtes der Seite L/√2.
const int Big = (int)(Box * 1.5);
using var bg = new MagickImage("gradient:#4C8DFF-#1D4ED8", Big, Big);
bg.Rotate(-45);
bg.ResetPage();   // sonst rechnet der Ausschnitt vom verschobenen Ursprung der Drehung aus
bg.Crop(new MagickGeometry((int)(bg.Width - Box) / 2, (int)(bg.Height - Box) / 2, Box, Box));
bg.ResetPage();
using var rounded = ImageOps.Crop(bg, 0, 0, Box, Box, radius: Box * 0.23);

using var icon = new MagickImage(MagickColors.Transparent, S, S);
icon.Composite(rounded, Inset, Inset, CompositeOperator.Over);

var white = new MagickColor("#FFFFFF");
var sun = new MagickColor("#FFD166");
var mountainBack = new MagickColor("#FFFFFF8C");   // halbtransparent: Tiefe (Hex gilt unabhängig von Q8/Q16)

new Drawables()
    // Sonne
    .FillColor(sun).StrokeColor(MagickColors.Transparent)
    .Circle(668, 360, 668 + 74, 360)
    // hinterer Berg (halbtransparent), vorderer Berg (weiß)
    .FillColor(mountainBack)
    .Polygon(new PointD(470, 740), new PointD(640, 500), new PointD(800, 740))
    .FillColor(white)
    .Polygon(new PointD(224, 740), new PointD(420, 450), new PointD(620, 740))
    // Zuschnitt-Ecken oben links und unten rechts
    .StrokeColor(white).StrokeWidth(46).StrokeLineCap(LineCap.Round).StrokeLineJoin(LineJoin.Round)
    .FillColor(MagickColors.Transparent)
    .Polyline(new PointD(196, 352), new PointD(196, 196), new PointD(352, 196))
    .Polyline(new PointD(672, 828), new PointD(828, 828), new PointD(828, 672))
    .Draw(icon);

// ICO: alle Größen in EINER Datei. Eine MagickImageCollection schriebe hier jede
// Größe als eigene Datei (app-0.ico …) — die Anweisung auto-resize erzeugt die
// Größen beim Schreiben selbst, wie beim Export (ImageExporter, Format ICO).
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

// Vorschau in Originalgröße zum Ansehen
icon.Write(Path.Combine(root, "tools", "IconGenerator", "icon-1024.png"), MagickFormat.Png32);

Console.WriteLine($"Icon geschrieben nach {assets}");

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ImageConverter.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("ImageConverter.sln nicht gefunden.");
}
