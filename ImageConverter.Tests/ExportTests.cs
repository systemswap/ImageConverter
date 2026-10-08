using ImageConverter.Core.Imaging;
using ImageMagick;
using ImageMagick.Drawing;

namespace ImageConverter.Tests;

public sealed class ExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ic-export-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ExportOptions Options(OutputFormat format, int w = 200, int h = 150) => new(
        format, Quality: 80, Lossless: false, w, h, ResampleFilter.Lanczos3, GammaCorrect: true,
        Denoise: 0, SharpenAmount: 0, SharpenSigma: 1, Dpi: 300,
        MetadataMode.KeepAll, ColorMode.ConvertToSrgb);

    private static MagickImage Photo(int w = 400, int h = 300)
    {
        var img = new MagickImage(MagickColors.SkyBlue, (uint)w, (uint)h);
        img.Draw(new Drawables().FillColor(MagickColors.Orange).Circle(w / 2.0, h / 2.0, w / 2.0 + h / 4.0, h / 2.0));
        return img;
    }

    private string Out(string name) => Path.Combine(_dir, name);

    /// <summary>Meldet sofort im selben Thread (Progress&lt;T&gt; liefe über den UI-Kontext).</summary>
    private sealed class Collect : IProgress<WorkProgress>
    {
        public readonly List<WorkProgress> Reports = [];
        public void Report(WorkProgress value) => Reports.Add(value);
    }

    /// <summary>
    /// Fortschritt beim Speichern: ein Balken, der einmal von 0 bis 100 % läuft (vorher lief eine
    /// Endlos-Animation ohne Text), jeder Schritt einmal gemeldet — Größe, Rauschen, 3 Dunst-Durchläufe,
    /// Tiefen/Lichter, Licht und Farbe, Schärfen, Farbprofil, Datei schreiben — und am Ende „fertig“.
    /// </summary>
    [Fact]
    public void Export_ReportsEveryStep_InOneSteadyRun()
    {
        using var src = Photo();
        var progress = new Collect();
        var options = Options(OutputFormat.Jpeg) with
        {
            Denoise = 0.3, SharpenAmount = 0.5,
            Adjustments = new Adjustments(Dehaze: 60, DehazePasses: 3, Shadows: 20),
        };
        ImageExporter.Export(src, options, Out("fortschritt.jpg"), progress);

        var r = progress.Reports;
        Assert.Equal(10 + 1, r.Count);
        Assert.Equal(0, r[0].Fraction);
        Assert.Equal(1, r[^1].Fraction);
        for (var i = 1; i < r.Count; i++) Assert.True(r[i].Fraction > r[i - 1].Fraction, $"Schritt {i}: {r[i - 1].Fraction:P0} → {r[i].Fraction:P0}");
        Assert.All(r, p => Assert.False(string.IsNullOrWhiteSpace(p.Step)));
        Assert.Equal(10 + 1, r.Select(p => p.Step).Distinct().Count());
    }

    [Theory]
    [InlineData(OutputFormat.WebP, MagickFormat.WebP)]
    [InlineData(OutputFormat.Png, MagickFormat.Png)]
    [InlineData(OutputFormat.Jpeg, MagickFormat.Jpeg)]
    [InlineData(OutputFormat.Avif, MagickFormat.Avif)]
    [InlineData(OutputFormat.JpegXl, MagickFormat.Jxl)]
    [InlineData(OutputFormat.Tiff, MagickFormat.Tiff)]
    [InlineData(OutputFormat.Bmp, MagickFormat.Bmp)]
    public void EveryFormat_WritesReadableFile_InTargetSize(OutputFormat format, MagickFormat expected)
    {
        using var src = Photo();
        var path = Out("bild." + ImageExporter.Extension(format));

        var bytes = ImageExporter.Export(src, Options(format), path);

        Assert.True(bytes > 0);
        using var back = new MagickImage(path);
        Assert.Equal(expected, back.Format);
        Assert.Equal((200u, 150u), (back.Width, back.Height));
    }

    [Fact]
    public void Ico_ContainsSeveralSizes_UpTo256()
    {
        using var src = Photo(1000, 1000);
        var path = Out("icon.ico");

        ImageExporter.Export(src, Options(OutputFormat.Ico, 1000, 1000), path);

        using var frames = new MagickImageCollection(path);
        Assert.True(frames.Count >= 4);
        Assert.All(frames, f => Assert.True(f.Width <= 256));
    }

    [Fact]
    public void Jpeg_FlattensTransparency_OntoWhite()
    {
        using var src = new MagickImage(MagickColors.Transparent, 100, 100);
        var path = Out("transparent.jpg");

        ImageExporter.Export(src, Options(OutputFormat.Jpeg, 100, 100), path);

        using var back = new MagickImage(path);
        var px = ImageOps.ToBgra(back);
        Assert.True(px[0] > 245 && px[1] > 245 && px[2] > 245, "nicht weiß");
    }

    [Fact]
    public void Png_KeepsTransparency()
    {
        using var src = new MagickImage(MagickColors.Transparent, 100, 100);
        var path = Out("transparent.png");

        ImageExporter.Export(src, Options(OutputFormat.Png, 100, 100), path);

        using var back = new MagickImage(path);
        Assert.Equal(0, ImageOps.ToBgra(back)[3]);
    }

    [Fact]
    public void LowerQuality_GivesSmallerFile()
    {
        using var src = Photo(800, 600);
        var small = ImageExporter.Export(src, Options(OutputFormat.Jpeg, 800, 600) with { Quality = 30 }, Out("q30.jpg"));
        var large = ImageExporter.Export(src, Options(OutputFormat.Jpeg, 800, 600) with { Quality = 95 }, Out("q95.jpg"));

        Assert.True(small < large, $"{small} ≥ {large}");
    }

    [Fact]
    public void WritesDpi()
    {
        using var src = Photo();
        var path = Out("dpi.jpg");

        ImageExporter.Export(src, Options(OutputFormat.Jpeg) with { Dpi = 600 }, path);

        using var back = new MagickImage(path);
        Assert.Equal(600, Math.Round(back.Density.X));
    }

    [Fact]
    public void Sharpening_KeepsMetadata()
    {
        using var src = Photo();
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Testkamera");
        src.SetProfile(exif);
        var path = Out("scharf.jpg");

        ImageExporter.Export(src, Options(OutputFormat.Jpeg) with { SharpenAmount = 1.5 }, path);

        using var back = new MagickImage(path);
        Assert.Equal("Testkamera", back.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
    }

    // ------------------------------------------------------------ Metadaten

    private static MagickImage PhotoWithGps()
    {
        var img = Photo();
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Testkamera");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(52, 1), new Rational(31, 1), new Rational(0, 1)]);
        img.SetProfile(exif);
        return img;
    }

    [Fact]
    public void Metadata_KeepAll_KeepsGps()
    {
        using var src = PhotoWithGps();
        var path = Out("alles.jpg");
        ImageExporter.Export(src, Options(OutputFormat.Jpeg), path);

        using var back = new MagickImage(path);
        Assert.NotNull(back.GetExifProfile()?.GetValue(ExifTag.GPSLatitude));
    }

    [Fact]
    public void Metadata_RemoveGps_KeepsCameraButNotLocation()
    {
        using var src = PhotoWithGps();
        var path = Out("ohnegps.jpg");
        ImageExporter.Export(src, Options(OutputFormat.Jpeg) with { Metadata = MetadataMode.RemoveGps }, path);

        using var back = new MagickImage(path);
        var exif = back.GetExifProfile();
        Assert.Equal("Testkamera", exif?.GetValue(ExifTag.Make)?.Value);
        Assert.Null(exif?.GetValue(ExifTag.GPSLatitude));
    }

    [Fact]
    public void Metadata_RemoveAll_RemovesExif_ButKeepsColorProfile()
    {
        using var src = PhotoWithGps();
        var path = Out("ohnealles.jpg");
        ImageExporter.Export(src, Options(OutputFormat.Jpeg) with { Metadata = MetadataMode.RemoveAll }, path);

        using var back = new MagickImage(path);
        Assert.Null(back.GetExifProfile());
        Assert.NotNull(back.GetColorProfile());   // sRGB bleibt — sonst stimmen die Farben nicht
    }

    // ------------------------------------------------------------ Farbe

    private static MagickImage AdobeRgbPhoto()
    {
        var img = Photo();
        img.SetProfile(ColorProfiles.AdobeRGB1998);
        return img;
    }

    [Fact]
    public void Color_KeepProfile_KeepsAdobeRgb()
    {
        using var src = AdobeRgbPhoto();
        var path = Out("adobe.png");
        ImageExporter.Export(src, Options(OutputFormat.Png) with { Color = ColorMode.KeepProfile }, path);

        using var back = new MagickImage(path);
        Assert.Contains("Adobe", back.GetColorProfile()?.Description ?? "");
    }

    [Theory]
    [InlineData(OutputFormat.Jpeg)]
    [InlineData(OutputFormat.WebP)]
    public void Color_ConvertToSrgb_EmbedsSrgbProfile(OutputFormat format)
    {
        using var src = AdobeRgbPhoto();
        var path = Out("srgb." + ImageExporter.Extension(format));
        ImageExporter.Export(src, Options(format) with { Color = ColorMode.ConvertToSrgb }, path);

        using var back = new MagickImage(path);
        Assert.Contains("sRGB", back.GetColorProfile()?.Description ?? "");
    }

    [Fact]
    public void Color_ConvertToSrgb_Png_UsesCompactSrgbChunk()
    {
        // PNG kennt einen eigenen, winzigen sRGB-Baustein statt des ~3 KB großen
        // Profils — ImageMagick nutzt ihn, das ist standardkonform.
        using var src = AdobeRgbPhoto();
        var path = Out("srgb.png");
        ImageExporter.Export(src, Options(OutputFormat.Png) with { Color = ColorMode.ConvertToSrgb }, path);

        using var back = new MagickImage(path);
        Assert.NotNull(back.GetAttribute("png:sRGB"));
        Assert.DoesNotContain("Adobe", back.GetColorProfile()?.Description ?? "");
    }

    [Fact]
    public void Color_Discard_HasNoProfile()
    {
        using var src = AdobeRgbPhoto();
        var path = Out("ohneprofil.png");
        ImageExporter.Export(src, Options(OutputFormat.Png) with { Color = ColorMode.DiscardProfile }, path);

        using var back = new MagickImage(path);
        Assert.Null(back.GetColorProfile());
    }

    [Fact]
    public async Task Loader_KeepsOriginalProfile_DisplayIsConverted()
    {
        using (var img = AdobeRgbPhoto()) img.Write(Out("adobe-src.png"));

        using var loaded = await ImageLoader.LoadAsync(Out("adobe-src.png"));

        Assert.Contains("Adobe", loaded.Image.GetColorProfile()?.Description ?? "");
        Assert.NotEqual(ImageOps.ToBgra(loaded.Image), ImageOps.ToDisplayBgra(loaded.Image));
    }

    // ------------------------------------------------------------ Dateinamen

    [Fact]
    public void Naming_ReplacesPlaceholders()
    {
        var name = OutputNaming.BuildFileName("{name}_{breite}x{höhe}_{n}_{datum}", "/fotos/urlaub.jpg",
            1920, 1080, 3, new DateTime(2026, 10, 7), "webp");

        Assert.Equal("urlaub_1920x1080_3_2026-10-07.webp", name);
    }

    [Fact]
    public void Naming_NeverOverwrites()
    {
        File.WriteAllText(Out("bild.jpg"), "x");
        File.WriteAllText(Out("bild (2).jpg"), "x");

        Assert.Equal(Out("bild (3).jpg"), OutputNaming.MakeUnique(_dir, "bild.jpg"));
    }

    [Fact]
    public void Naming_DefaultFolder_IsSubfolderNextToOriginal()
    {
        var folder = OutputNaming.ResolveFolder(null, Path.Combine(_dir, "a.jpg"));
        Assert.Equal(Path.Combine(_dir, "konvertiert"), folder);
    }

    [Fact]
    public void Naming_RemovesInvalidCharacters()
    {
        var name = OutputNaming.BuildFileName("{name}:test?", "a.jpg", 1, 1, 1, DateTime.Today, "png");
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('?', name);
    }
}
