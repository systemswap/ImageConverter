using System.Text;
using ImageConverter.Core.Localization;
using ImageMagick;

namespace ImageConverter.Core.Imaging;

public enum OutputFormat { WebP, Png, Jpeg, Avif, JpegXl, Tiff, Bmp, Ico }

public enum MetadataMode
{
    /// <summary>EXIF, IPTC, XMP bleiben wie im Original.</summary>
    KeepAll,
    /// <summary>Ortsangaben (GPS) entfernen, Rest behalten.</summary>
    RemoveGps,
    /// <summary>Alle Metadaten entfernen.</summary>
    RemoveAll,
}

public enum ColorMode
{
    /// <summary>Farbraum und Profil des Originals behalten (z. B. Adobe RGB).</summary>
    KeepProfile,
    /// <summary>Nach sRGB umrechnen und sRGB-Profil einbetten — sicher für Web und Druckdienste.</summary>
    ConvertToSrgb,
    /// <summary>Nach sRGB umrechnen, kein Profil einbetten — kleinste Datei, Browser nehmen sRGB an.</summary>
    DiscardProfile,
}

/// <summary>Alle Einstellungen eines Exports — ein vollständiger, unveränderlicher Auftrag.</summary>
public sealed record ExportOptions(
    OutputFormat Format,
    int Quality,
    bool Lossless,
    int Width,
    int Height,
    ResampleFilter Filter,
    bool GammaCorrect,
    double Denoise,
    double SharpenAmount,
    double SharpenSigma,
    int Dpi,
    MetadataMode Metadata,
    ColorMode Color,
    Adjustments? Adjustments = null,   // Kachel „Tools“; null = keine
    bool HighBitDepth = false);        // PNG/TIFF mit 16 Bit je Kanal (sonst 8 Bit mit Dither)

/// <summary>
/// Die Export-Kette: Skalieren → Entrauschen → Tools (Licht, Farbe) → Schärfen → Farbprofil
/// → Metadaten → Kodieren.
/// Dieselben Schritte wie die Vorschau, hier in voller Auflösung.
/// </summary>
public static class ImageExporter
{
    public static string Extension(OutputFormat format) => format switch
    {
        OutputFormat.WebP => "webp",
        OutputFormat.Png => "png",
        OutputFormat.Jpeg => "jpg",
        OutputFormat.Avif => "avif",
        OutputFormat.JpegXl => "jxl",
        OutputFormat.Tiff => "tif",
        OutputFormat.Bmp => "bmp",
        OutputFormat.Ico => "ico",
        _ => "bin",
    };

    /// <summary>Formate ohne Qualitätsregler — sie speichern immer verlustfrei.</summary>
    public static bool IsAlwaysLossless(OutputFormat format) =>
        format is OutputFormat.Png or OutputFormat.Tiff or OutputFormat.Bmp or OutputFormat.Ico;

    /// <summary>Formate ohne Transparenz — Alpha wird auf Weiß gelegt.</summary>
    /// <summary>Bittiefe der Quelle am gerechneten Bild (das selbst immer 16 Bit hat).</summary>
    private const string SourceDepthArtifact = "imageconverter:source-depth";

    /// <summary>Formate, die mit 16 Bit je Kanal gespeichert werden können.</summary>
    public static bool SupportsHighBitDepth(OutputFormat format) =>
        format is OutputFormat.Png or OutputFormat.Tiff;

    public static bool SupportsAlpha(OutputFormat format) =>
        format is not (OutputFormat.Jpeg or OutputFormat.Bmp);

    /// <summary>Wendet die Bildkette an. Die Quelle bleibt unverändert.</summary>
    public static MagickImage Render(MagickImage source, ExportOptions o, WorkPlan? plan = null)
    {
        uint sourceDepth;
        lock (source) sourceDepth = source.Depth;
        plan?.Next();
        var result = ImageOps.Resize(source, o.Width, o.Height, o.Filter, o.GammaCorrect);

        if (o.Denoise > 0.01)
        {
            plan?.Next();
            var denoised = ImageOps.Denoise(result, o.Denoise);
            result.Dispose();
            result = denoised;
        }

        var adjust = o.Adjustments is { IsNone: false } a ? a : null;
        if (adjust is not null || o.SharpenAmount > 0.01)
        {
            // Tools und Schärfen rechnen im 32-Bit-Puffer wie die Vorschau, gelesen mit voller Tiefe (Q16)
            // und als 16-Bit-Bild zurück — gerundet wird erst beim Kodieren. Profile werden übernommen.
            var w = (int)result.Width;
            var h = (int)result.Height;
            var pixels = Pixels.FromMagick(result);
            if (adjust is not null) pixels = Swap(pixels, Adjust.Apply(pixels, w, h, adjust, plan));
            if (o.SharpenAmount > 0.01)
            {
                plan?.Next();
                pixels = Swap(pixels, Sharpen.UnsharpMask(pixels, w, h, o.SharpenAmount, o.SharpenSigma));
            }
            var processed = Pixels.ToMagick16(pixels, w, h, result.HasAlpha);
            PixelPool.Return(pixels);
            CopyProfiles(result, processed);
            result.Dispose();
            result = processed;
        }

        plan?.Next();
        ApplyColor(result, o.Color);
        ApplyMetadata(result, o.Metadata);
        result.Density = new Density(o.Dpi, o.Dpi, DensityUnit.PixelsPerInch);
        result.SetArtifact(SourceDepthArtifact, sourceDepth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return result;
    }

    /// <summary>Den alten Puffer zurück in den Pool, den neuen weiterreichen (die Export-Puffer gehören nur uns).</summary>
    private static float[] Swap(float[] old, float[] next)
    {
        if (!ReferenceEquals(old, next)) PixelPool.Return(old);
        return next;
    }

    /// <summary>Kodiert ein fertig gerechnetes Bild im Zielformat.</summary>
    public static void Encode(MagickImage image, ExportOptions o, Stream output)
    {
        using var img = ImageOps.CloneOf(image);

        if (!SupportsAlpha(o.Format) && img.HasAlpha)
        {
            img.BackgroundColor = MagickColors.White;
            img.Alpha(AlphaOption.Remove);
        }

        // Bittiefe: 16 Bit, wo das Format es kann und es gewünscht ist oder die Quelle schon 16 Bit hatte;
        // sonst einmal mit Dither auf 8 Bit (Magick würde ohne Dither runden — weiche Verläufe bekämen
        // dabei wieder Stufen)
        var sourceDepth = int.TryParse(img.GetArtifact(SourceDepthArtifact), out var d) ? d : (int)img.Depth;
        if (SupportsHighBitDepth(o.Format) && (o.HighBitDepth || sourceDepth > 8))
            img.Depth = 16;
        else
            Pixels.DitherTo8Bit(img);

        var quality = (uint)Math.Clamp(o.Quality, 1, 100);
        switch (o.Format)
        {
            case OutputFormat.Jpeg:
                img.Format = MagickFormat.Jpeg;
                img.Quality = quality;
                img.Settings.Interlace = Interlace.Plane;   // progressiv: lädt im Web sofort grob
                break;

            case OutputFormat.WebP:
                img.Format = MagickFormat.WebP;
                img.Quality = o.Lossless ? 100 : quality;
                if (o.Lossless) img.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
                break;

            case OutputFormat.Avif:
                img.Format = MagickFormat.Avif;
                img.Quality = o.Lossless ? 100 : quality;
                if (o.Lossless) img.Settings.SetDefine(MagickFormat.Heic, "lossless", true);
                break;

            case OutputFormat.JpegXl:
                img.Format = MagickFormat.Jxl;
                img.Quality = o.Lossless ? 100 : quality;   // 100 = mathematisch verlustfrei
                break;

            case OutputFormat.Png:
                img.Format = MagickFormat.Png;
                img.Quality = 95;   // zlib-Stufe 9, adaptive Filter
                break;

            case OutputFormat.Tiff:
                img.Format = MagickFormat.Tiff;
                img.Settings.Compression = CompressionMethod.Zip;
                break;

            case OutputFormat.Bmp:
                img.Format = MagickFormat.Bmp;
                break;

            case OutputFormat.Ico:
                img.Format = MagickFormat.Icon;
                // Icons sind höchstens 256 px; mehrere Größen in einer Datei
                img.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,128,64,48,32,16");
                break;
        }

        img.Write(output);
    }

    /// <summary>Rechnen und schreiben. Gibt die Dateigröße in Bytes zurück.</summary>
    /// <param name="progress">Fortschritt über alle Schritte bis zur fertigen Datei, mit dem
    /// Schritt, der gerade läuft.</param>
    public static long Export(MagickImage source, ExportOptions o, string path, IProgress<WorkProgress>? progress = null)
    {
        var plan = progress is null ? null : Plan(source, o, progress);
        using var rendered = Render(source, o, plan);
        plan?.Next();

        // Erst in eine temporäre Datei, dann umbenennen: ein Abbruch hinterlässt
        // nie eine halb geschriebene Zieldatei.
        var temp = path + ".tmp";
        try
        {
            using (var fs = File.Create(temp))
                Encode(rendered, o, fs);
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        PixelPool.Trim(release: true);   // Puffer dieser Rechnung loslassen — sonst bleibt der Speicher belegt
        plan?.Done(Tr.T("Gespeichert"));
        return new FileInfo(path).Length;
    }

    /// <summary>Die Schritte von <see cref="Render"/> und dem Speichern, in ihrer Reihenfolge.</summary>
    private static WorkPlan Plan(MagickImage source, ExportOptions o, IProgress<WorkProgress> progress)
    {
        var plan = new WorkPlan(progress);
        var mp = (double)o.Width * o.Height / 1e6;
        plan.Add(Tr.T("Größe anpassen"), WorkPlan.ResizeCost * Math.Max(mp, (double)source.Width * source.Height / 1e6));
        if (o.Denoise > 0.01) plan.Add(Tr.T("Rauschen entfernen"), WorkPlan.DenoiseCost * mp);
        if (o.Adjustments is { IsNone: false } a) Adjust.PlanSteps(a, plan, mp);
        if (o.SharpenAmount > 0.01) plan.Add(Tr.T("Schärfen"), WorkPlan.SharpenCost * mp);
        plan.Add(Tr.T("Farbprofil und Metadaten"), WorkPlan.ColorCost * mp);
        plan.Add(Tr.T("Datei schreiben ({0})", Extension(o.Format).TrimStart('.').ToUpperInvariant()), EncodeCost(o) * mp);
        return plan;
    }

    /// <summary>Aufwand des Kodierens je Megapixel in Sekunden (grob gemessen; AVIF und JPEG XL sind langsam).</summary>
    private static double EncodeCost(ExportOptions o) => o.Format switch
    {
        OutputFormat.Jpeg => 0.04,
        OutputFormat.Png => 0.15,
        OutputFormat.WebP => o.Lossless ? 0.4 : 0.15,
        OutputFormat.Avif => 0.8,
        OutputFormat.JpegXl => 0.4,
        OutputFormat.Tiff => 0.06,
        _ => 0.03,
    };

    // ------------------------------------------------------------ Farbe

    private static void ApplyColor(MagickImage image, ColorMode mode)
    {
        if (mode == ColorMode.KeepProfile) return;

        // Nach sRGB — nur nötig, wenn ein anderes Profil eingebettet ist
        var profile = image.GetColorProfile();
        if (profile is not null && !IsSrgb(profile))
            image.TransformColorSpace(ColorProfiles.SRGB);

        if (mode == ColorMode.ConvertToSrgb)
            image.SetProfile(ColorProfiles.SRGB);
        else
            image.RemoveProfile("icc");
    }

    internal static bool IsSrgb(IColorProfile profile) =>
        profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) == true;

    // ------------------------------------------------------------ Metadaten

    private static void ApplyMetadata(MagickImage image, MetadataMode mode)
    {
        switch (mode)
        {
            case MetadataMode.RemoveAll:
            {
                // Strip entfernt auch das Farbprofil — das wird danach wieder gesetzt
                var icc = image.GetColorProfile();
                image.Strip();
                if (icc is not null) image.SetProfile(icc);
                break;
            }

            case MetadataMode.RemoveGps:
            {
                var exif = image.GetExifProfile();
                if (exif is not null)
                {
                    // Einzeln entfernen — ExifParts allein ließ die Koordinaten drin
                    // (gemessen mit Magick.NET 14.17.2).
                    var gpsTags = exif.Values
                        .Select(v => v.Tag)
                        .Where(t => t.ToString().StartsWith("GPS", StringComparison.Ordinal))
                        .ToList();
                    foreach (var tag in gpsTags)
                        exif.RemoveValue(tag);
                    exif.Parts = ExifParts.IfdTags | ExifParts.ExifTags;
                    image.SetProfile(exif);
                }

                // XMP kann ebenfalls Koordinaten enthalten (exif:GPS…) — dann ganz entfernen
                var xmp = image.GetXmpProfile();
                if (xmp?.ToByteArray() is { } bytes && Encoding.UTF8.GetString(bytes).Contains("GPS", StringComparison.Ordinal))
                    image.RemoveProfile("xmp");
                break;
            }
        }
    }

    internal static void CopyProfiles(MagickImage from, MagickImage to)
    {
        // „from" ist oft das gemeinsame Original (siehe ImageOps.CloneOf) — gesperrt lesen
        var profiles = new List<IImageProfile>();
        lock (from)
        {
            foreach (var name in from.ProfileNames)
                if (from.GetProfile(name) is { } profile)
                    profiles.Add(profile);
        }

        foreach (var profile in profiles)
            to.SetProfile(profile);
    }
}
