using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ImageConverter.Abnahme;
using ImageConverter.Core.Imaging;
using ImageMagick;

// Abnahme der Bildkette: dieselben Bilder und Einstellungen mit der aktuellen Fassung rechnen,
// messen und mit einer Referenzversion vergleichen (Stand 0.4.45, vom Benutzer abgenommen).
//
//   abnahme raster              Vorschau je Testbild mit 10-%-Raster (Messbereiche festlegen)
//   abnahme referenz <name>     alle Fälle rechnen, als Referenz speichern (PNG + werte.json)
//   abnahme pruefen <name>      alle Fälle rechnen, messen, mit der Referenz vergleichen
//   abnahme leistung <name>     Laufzeit und Speicher großer Bilder (je Fall ein eigener Prozess)
//
// Arbeitsordner: Umgebungsvariable IC_ABNAHME, sonst <LocalApplicationData>/imageconverter-build/abnahme
// (Linux: ~/.local/share/…). Darin abnahme.json (Testbilder und Messbereiche) und je Lauf ein Ordner.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var root = Environment.GetEnvironmentVariable("IC_ABNAHME")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "imageconverter-build", "abnahme");
var config = Config.Load(Path.Combine(root, "abnahme.json"));

switch (args.FirstOrDefault())
{
    case "raster": Raster(); break;
    case "referenz" when args.Length > 1: Run(args[1], compareTo: null); break;
    case "pruefen" when args.Length > 1: Run(args[1], compareTo: config.Referenz); break;
    case "leistung" when args.Length > 1: Performance(args[1]); break;
    case "einzeln" when args.Length > 3: Single(args[1], args[2], int.Parse(args[3])); break;
    default:
        Console.WriteLine("abnahme raster | referenz <name> | pruefen <name> | leistung <name>");
        return 1;
}
return Report.Failures > 0 ? 2 : 0;

// ---------------------------------------------------------------- Rechnen und vergleichen

void Run(string name, string? compareTo)
{
    var outDir = Path.Combine(root, name);
    Directory.CreateDirectory(outDir);
    var values = new Dictionary<string, Metrics>();
    Dictionary<string, Metrics>? reference = null;
    if (compareTo is not null)
    {
        var refFile = Path.Combine(root, compareTo, "werte.json");
        if (!File.Exists(refFile)) throw new FileNotFoundException($"Referenz fehlt: {refFile} (erst „abnahme referenz {compareTo}“)");
        reference = JsonSerializer.Deserialize<Dictionary<string, Metrics>>(File.ReadAllText(refFile))!;
    }

    var watch = Stopwatch.StartNew();
    foreach (var image in config.Bilder)
    {
        using var source = new MagickImage(Path.Combine(root, image.Datei));
        Console.WriteLine($"\n== {image.Name} ({source.Width} × {source.Height})");
        Report.Header();
        (int X, int Y)? sun = null;
        double? raysOriginal = null;
        foreach (var c in Cases.All)
        {
            var px = Render(source, c, out var w, out var h);
            if (c.Name == Cases.Original)
            {
                if (image.Sonne) sun = Measure.FindSun(px, w, h);
                raysOriginal = sun is { } s0 ? Measure.Rays(px, w, h, s0) : null;
            }
            var m = Measure.All(px, w, h, image, sun, raysOriginal);
            var key = $"{image.Name}/{c.Name}";
            var file = Path.Combine(outDir, $"{image.Name}_{c.Name}.png");
            if (reference is not null)
            {
                var refPng = Path.Combine(root, compareTo!, $"{image.Name}_{c.Name}.png");
                if (File.Exists(refPng))
                {
                    using var r = new MagickImage(refPng);
                    var rp = ImageOps.ToBgra(r);
                    if (rp.Length == px.Length) (m.AbweichungMittel, m.AbweichungSpitze) = Measure.Difference(px, rp, w, h);
                }
            }
            values[key] = m;
            Save(px, w, h, file);
            Report.Line(c.Name, m, reference?.GetValueOrDefault(key));
        }
    }

    foreach (var synth in config.Synthetisch)
    {
        using var clear = new MagickImage(Path.Combine(root, synth.Datei));
        Console.WriteLine($"\n== {synth.Name} (künstlicher Dunst, bekannte Wahrheit)");
        Report.SynthHeader();
        var (truth, hazy, w, h) = Synthetic.Make(clear);
        foreach (var c in Cases.Synthetic)
        {
            var result = c.Adjust is null ? hazy : Adjust.Apply(hazy, w, h, c.Adjust);
            var m = Synthetic.Measure(truth, result, w, h);
            var key = $"{synth.Name}/{c.Name}";
            values[key] = m;
            Save(result, w, h, Path.Combine(outDir, $"{synth.Name}_{c.Name}.png"));
            Report.SynthLine(c.Name, m, reference?.GetValueOrDefault(key));
        }
    }

    File.WriteAllText(Path.Combine(outDir, "werte.json"), JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
    if (reference is not null) Compare.Pictures(root, compareTo!, name, config);
    Console.WriteLine($"\n{values.Count} Fälle in {watch.Elapsed.TotalSeconds:F0} s → {outDir}");
    if (reference is not null)
        Console.WriteLine(Report.Failures == 0 ? "Abnahme: nichts schlechter als die Referenz." : $"Abnahme: {Report.Failures} Werte schlechter als die Referenz (✗).");
}

byte[] Render(MagickImage source, Case c, out int w, out int h)
{
    var edge = Math.Max((int)source.Width, (int)source.Height);
    var scale = Math.Min(1.0, (double)config.Kante / edge);
    w = Math.Max(1, (int)Math.Round(source.Width * scale));
    h = Math.Max(1, (int)Math.Round(source.Height * scale));
    var options = new ExportOptions(OutputFormat.Png, 95, false, w, h, ResampleFilter.Lanczos3, true,
        c.Denoise, c.Sharpen, c.SharpenSigma, 300, MetadataMode.KeepAll, ColorMode.ConvertToSrgb, c.Adjust);
    using var rendered = ImageExporter.Render(source, options);
    return ImageOps.ToBgra(rendered);
}

static void Save(byte[] px, int w, int h, string path)
{
    using var img = ImageOps.FromBgra(px, w, h);
    img.Write(path, MagickFormat.Png24);
}

// ---------------------------------------------------------------- Leistung

void Performance(string name)
{
    var outDir = Path.Combine(root, name);
    Directory.CreateDirectory(outDir);
    var exe = Environment.ProcessPath!;
    var results = new Dictionary<string, double[]>();
    var big = config.Bilder.First(b => b.Name == config.Leistungsbild);
    Console.WriteLine($"Leistung: {big.Name}, {Environment.ProcessorCount} Kerne");
    Console.WriteLine($"{"Fall",-22} {"Kante",6} {"Zeit s",8} {"Spitze GB",10}");
    foreach (var edge in new[] { 2048, 4000, 0 })
    foreach (var c in Cases.Performance)
    {
        var psi = new ProcessStartInfo(exe, ["einzeln", big.Name, c, edge.ToString()]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var line = p.StandardOutput.ReadToEnd().Trim().Split('\n')[^1];
        p.WaitForExit();
        var parts = line.Split(';').Select(double.Parse).ToArray();   // Sekunden;Spitze GB;Kante
        results[$"{c}/{(edge == 0 ? "voll" : edge.ToString())}"] = parts;
        Console.WriteLine($"{c,-22} {parts[2],6:F0} {parts[0],8:F2} {parts[1],10:F2}");
    }
    File.WriteAllText(Path.Combine(outDir, "leistung.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    var refFile = Path.Combine(root, config.Referenz, "leistung.json");
    if (name != config.Referenz && File.Exists(refFile))
    {
        var reference = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(refFile))!;
        Console.WriteLine($"\nGegen {config.Referenz}:");
        foreach (var (k, v) in results)
            if (reference.TryGetValue(k, out var r))
                Console.WriteLine($"{k,-28} Zeit {v[0] / r[0],5:P0}  Speicher {v[1] / r[1],5:P0}");
    }
}

void Single(string imageName, string caseName, int edge)
{
    var image = config.Bilder.First(b => b.Name == imageName);
    var c = Cases.All.First(x => x.Name == caseName);
    using var source = new MagickImage(Path.Combine(root, image.Datei));
    if (edge > 0) config.Kante = edge; else config.Kante = int.MaxValue;
    Render(source, c, out _, out _);   // warm (JIT, Tabellen)
    GC.Collect();
    var watch = Stopwatch.StartNew();
    Render(source, c, out var w, out var h);
    watch.Stop();
    using var self = Process.GetCurrentProcess();
    self.Refresh();
    Console.WriteLine($"{watch.Elapsed.TotalSeconds:F3};{self.PeakWorkingSet64 / 1e9:F3};{Math.Max(w, h)}");
}

// ---------------------------------------------------------------- Raster

void Raster()
{
    foreach (var image in config.Bilder.Select(b => (b.Name, b.Datei)).Concat(config.Synthetisch.Select(s => (s.Name, s.Datei))))
    {
        using var img = new MagickImage(Path.Combine(root, image.Datei));
        img.Resize(900, 0);
        var d = new ImageMagick.Drawing.Drawables().StrokeColor(new MagickColor(255, 0, 0, 160)).StrokeWidth(1);
        for (var k = 1; k < 10; k++)
        {
            d.Line(img.Width * k / 10.0, 0, img.Width * k / 10.0, img.Height);
            d.Line(0, img.Height * k / 10.0, img.Width, img.Height * k / 10.0);
        }
        d.Draw(img);
        var path = Path.Combine(root, $"raster-{image.Name}.png");
        img.Write(path);
        Console.WriteLine(path);
    }
}
