using System.Text;
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
    Adjustments? Adjustments = null);   // Kachel „Tools“; null = keine

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
    public static bool SupportsAlpha(OutputFormat format) =>
        format is not (OutputFormat.Jpeg or OutputFormat.Bmp);

    /// <summary>Wendet die Bildkette an. Die Quelle bleibt unverändert.</summary>
    public static MagickImage Render(MagickImage source, ExportOptions o)
    {
        var result = ImageOps.Resize(source, o.Width, o.Height, o.Filter, o.GammaCorrect);

        if (o.Denoise > 0.01)
        {
            var denoised = ImageOps.Denoise(result, o.Denoise);
            result.Dispose();
            result = denoised;
        }

        var adjust = o.Adjustments is { IsNone: false } a ? a : null;
        if (adjust is not null || o.SharpenAmount > 0.01)
        {
            // Tools und Schärfen arbeiten auf 8-Bit-BGRA wie die Vorschau — in einem
            // Durchgang. Dabei gehen Profile verloren, sie werden vom Zwischenstand übernommen.
            var w = (int)result.Width;
            var h = (int)result.Height;
            var pixels = ImageOps.ToBgra(result);
            if (adjust is not null) pixels = Adjust.Apply(pixels, w, h, adjust);
            if (o.SharpenAmount > 0.01) pixels = Sharpen.UnsharpMask(pixels, w, h, o.SharpenAmount, o.SharpenSigma);
            var processed = ImageOps.FromBgra(pixels, w, h);
            CopyProfiles(result, processed);
            result.Dispose();
            result = processed;
        }

        ApplyColor(result, o.Color);
        ApplyMetadata(result, o.Metadata);
        result.Density = new Density(o.Dpi, o.Dpi, DensityUnit.PixelsPerInch);
        return result;
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
    public static long Export(MagickImage source, ExportOptions o, string path)
    {
        using var rendered = Render(source, o);

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

        return new FileInfo(path).Length;
    }

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
