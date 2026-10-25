using System.Globalization;
using System.Text;
using ImageConverter.Core.Localization;
using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.FileType;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Xmp;
using MxDirectory = MetadataExtractor.Directory;
using MxReader = MetadataExtractor.ImageMetadataReader;

namespace ImageConverter.Core.Metadata;

/// <summary>
/// Reads the metadata of an image file for display: EXIF (including GPS and many camera makers' notes), IPTC, XMP and
/// the ICC profile. This is the only place that uses MetadataExtractor; the rest of the program works with
/// <see cref="ImageMetadata"/>.
/// </summary>
/// <remarks>
/// Metadata comes from files of unknown origin, so the result is bounded: at most <see cref="MaxTags"/> entries, values
/// cut to <see cref="MaxValueLength"/> characters, control characters replaced. Reading never throws; a damaged file
/// yields what could be read plus <see cref="ImageMetadata.Problem"/>. Nothing is logged or sent anywhere.
/// </remarks>
public static class ImageMetadataReader
{
    /// <summary>Most entries listed; files with more (e.g. huge maker notes or prepared files) are cut.</summary>
    public const int MaxTags = 3000;

    /// <summary>Longest value shown; longer ones end with an ellipsis.</summary>
    public const int MaxValueLength = 400;

    /// <summary>Reads everything readable from the file.</summary>
    /// <param name="path">Image file.</param>
    /// <returns>Summary and complete list; <see cref="ImageMetadata.Empty"/> with a problem if nothing could be read.</returns>
    public static ImageMetadata Read(string path)
    {
        IReadOnlyList<MxDirectory> directories;
        string? problem = null;
        try
        {
            directories = MxReader.ReadMetadata(path);
        }
        catch (Exception e) when (e is ImageProcessingException or IOException or UnauthorizedAccessException
                                       or ArgumentException or InvalidOperationException or IndexOutOfRangeException
                                       or OverflowException or FormatException or NotSupportedException)
        {
            directories = [];
            problem = e.Message;
        }

        var groups = new List<MetadataGroup>();
        void Add(MetadataSection section, string title, IEnumerable<MetadataField?> fields)
        {
            var list = fields.Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Value)).Cast<MetadataField>().ToList();
            if (list.Count > 0) groups.Add(new MetadataGroup(section, title, list));
        }

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var iptc = directories.OfType<IptcDirectory>().FirstOrDefault();
        var xmp = SafeXmp(directories.OfType<XmpDirectory>().FirstOrDefault());
        var icc = directories.OfType<IccDirectory>().FirstOrDefault();

        Add(MetadataSection.File, Tr.T("Datei"), FileFields(path, directories));
        Add(MetadataSection.Camera, Tr.T("Kamera"),
        [
            Field(Tr.T("Hersteller"), Text(ifd0, ExifDirectoryBase.TagMake)),
            Field(Tr.T("Modell"), Text(ifd0, ExifDirectoryBase.TagModel)),
            Field(Tr.T("Objektiv"), Text(sub, ExifDirectoryBase.TagLensModel) ?? Text(sub, ExifDirectoryBase.TagLensMake)),
            Field(Tr.T("Software"), Text(ifd0, ExifDirectoryBase.TagSoftware) ?? Xmp(xmp, "xmp:CreatorTool")),
        ]);
        Add(MetadataSection.Capture, Tr.T("Aufnahme"),
        [
            Field(Tr.T("Aufnahmedatum"), CaptureDate(sub, ifd0)),
            Field(Tr.T("Belichtungszeit"), ExposureTime(sub)),
            Field(Tr.T("Blende"), Aperture(sub)),
            Field(Tr.T("ISO"), Integer(sub, ExifDirectoryBase.TagIsoEquivalent)),
            Field(Tr.T("Brennweite"), FocalLength(sub)),
            Field(Tr.T("Belichtungskorrektur"), ExposureBias(sub)),
            Field(Tr.T("Programm"), ExposureProgram(sub)),
            Field(Tr.T("Blitz"), Flash(sub)),
            Field(Tr.T("Weißabgleich"), WhiteBalance(sub)),
        ]);
        Add(MetadataSection.Location, Tr.T("Ort"),
        [
            Field(Tr.T("Koordinaten"), Coordinates(gps)),
            Field(Tr.T("Höhe"), Altitude(gps)),
            Field(Tr.T("Ort"), Join(Text(iptc, IptcDirectory.TagCity), Text(iptc, IptcDirectory.TagCountryOrPrimaryLocationName))),
        ]);
        Add(MetadataSection.Description, Tr.T("Urheber und Beschreibung"),
        [
            Field(Tr.T("Urheber"), Text(ifd0, ExifDirectoryBase.TagArtist) ?? Text(iptc, IptcDirectory.TagByLine) ?? Xmp(xmp, "dc:creator[1]")),
            Field(Tr.T("Copyright"), Text(ifd0, ExifDirectoryBase.TagCopyright) ?? Text(iptc, IptcDirectory.TagCopyrightNotice)
                                     ?? Xmp(xmp, "dc:rights[1]")),
            Field(Tr.T("Titel"), Text(iptc, IptcDirectory.TagObjectName) ?? Xmp(xmp, "dc:title[1]")),
            Field(Tr.T("Beschreibung"), Text(ifd0, ExifDirectoryBase.TagImageDescription) ?? Text(iptc, IptcDirectory.TagCaption)
                                        ?? Xmp(xmp, "dc:description[1]")),
            Field(Tr.T("Stichwörter"), Keywords(iptc, xmp)),
            Field(Tr.T("Bewertung"), Rating(xmp)),
        ]);
        Add(MetadataSection.ColourProfile, Tr.T("Farbprofil"),
        [
            Field(Tr.T("Profil"), Text(icc, IccDirectory.TagTagDesc) ?? Text(icc, IccDirectory.TagProfileClass)),
        ]);

        var (tags, truncated) = AllTags(directories);
        if (problem is null)
        {
            var errors = directories.SelectMany(d => d.Errors).Take(3).ToList();
            if (errors.Count > 0) problem = Clean(string.Join(" · ", errors));
        }
        return new ImageMetadata(groups, tags, truncated, problem);
    }

    // ------------------------------------------------------------ File

    private static IEnumerable<MetadataField?> FileFields(string path, IReadOnlyList<MxDirectory> directories)
    {
        var type = directories.OfType<FileTypeDirectory>().FirstOrDefault();
        yield return Field(Tr.T("Format"), Text(type, FileTypeDirectory.TagDetectedFileTypeName));
        long bytes = 0;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // size unknown
        }
        if (bytes > 0) yield return Field(Tr.T("Dateigröße"), FormatBytes(bytes));

        // Pixel dimensions and resolution from the image header (same reading as when opening the file)
        MagickImageInfo? info = null;
        try
        {
            info = new MagickImageInfo(path);
        }
        catch (MagickException)
        {
            // format without readable header
        }
        if (info is { Width: > 0, Height: > 0 })
        {
            yield return Field(Tr.T("Bildmaße"), Tr.T("{0} × {1} Pixel ({2:0.#} MP)", info.Width, info.Height, info.Width * (double)info.Height / 1e6));
            if (info.Density is { X: > 0 } d && d.Units != DensityUnit.Undefined)
            {
                var dpi = d.Units == DensityUnit.PixelsPerCentimeter ? d.X * 2.54 : d.X;
                yield return Field(Tr.T("Auflösung"), Tr.T("{0:0} DPI", dpi));
            }
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? Tr.T("{0:0.0} MB", bytes / 1048576.0)
        : bytes >= 1024 ? Tr.T("{0:0} KB", bytes / 1024.0)
        : Tr.T("{0} Bytes", bytes);

    // ------------------------------------------------------------ Capture

    private static string? CaptureDate(ExifSubIfdDirectory? sub, ExifIfd0Directory? ifd0)
    {
        DateTime date = default;
        var found = sub is not null && (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out date)
                                        || sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out date));
        if (!found && ifd0 is not null) found = ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out date);
        return found ? date.ToString("G", Tr.Culture) : null;
    }

    private static string? ExposureTime(ExifSubIfdDirectory? sub)
    {
        if (sub is null || !sub.TryGetRational(ExifDirectoryBase.TagExposureTime, out var r) || r.Numerator <= 0 || r.Denominator <= 0)
            return null;
        var seconds = r.ToDouble();
        return seconds < 1
            ? Tr.T("1/{0:0} s", 1 / seconds)
            : Tr.T("{0:0.#} s", seconds);
    }

    private static string? Aperture(ExifSubIfdDirectory? sub) =>
        sub is not null && sub.TryGetRational(ExifDirectoryBase.TagFNumber, out var r) && r.Denominator != 0 && r.ToDouble() > 0
            ? string.Format(Tr.Culture, "f/{0:0.0#}", r.ToDouble())
            : null;

    private static string? FocalLength(ExifSubIfdDirectory? sub)
    {
        if (sub is null) return null;
        string? mm = sub.TryGetRational(ExifDirectoryBase.TagFocalLength, out var r) && r.Denominator != 0 && r.ToDouble() > 0
            ? string.Format(Tr.Culture, "{0:0.#} mm", r.ToDouble())
            : null;
        string? kb = sub.TryGetInt32(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, out var e) && e > 0
            ? Tr.T("entspricht {0} mm Kleinbild", e)
            : null;
        return mm is null ? kb : kb is null ? mm : $"{mm} ({kb})";
    }

    private static string? ExposureBias(ExifSubIfdDirectory? sub) =>
        sub is not null && sub.TryGetRational(ExifDirectoryBase.TagExposureBias, out var r) && r.Denominator != 0
            ? string.Format(Tr.Culture, "{0:+0.0#;−0.0#;0} EV", r.ToDouble())
            : null;

    private static string? ExposureProgram(ExifSubIfdDirectory? sub) =>
        sub is not null && sub.TryGetInt32(ExifDirectoryBase.TagExposureProgram, out var p)
            ? p switch
            {
                1 => Tr.T("Manuell"),
                2 => Tr.T("Programmautomatik"),
                3 => Tr.T("Zeitautomatik (Blende vorgewählt)"),
                4 => Tr.T("Blendenautomatik (Zeit vorgewählt)"),
                5 => Tr.T("Kreativprogramm (Schärfentiefe)"),
                6 => Tr.T("Actionprogramm (kurze Zeit)"),
                7 => Tr.T("Porträt"),
                8 => Tr.T("Landschaft"),
                _ => null,
            }
            : null;

    private static string? Flash(ExifSubIfdDirectory? sub) =>
        sub is not null && sub.TryGetInt32(ExifDirectoryBase.TagFlash, out var f)
            ? (f & 1) != 0 ? Tr.T("ausgelöst") : Tr.T("nicht ausgelöst")
            : null;

    private static string? WhiteBalance(ExifSubIfdDirectory? sub) =>
        sub is not null && sub.TryGetInt32(ExifDirectoryBase.TagWhiteBalanceMode, out var w)
            ? w == 0 ? Tr.T("automatisch") : w == 1 ? Tr.T("manuell") : null
            : null;

    // ------------------------------------------------------------ Location

    private static string? Coordinates(GpsDirectory? gps)
    {
        if (gps?.GetGeoLocation() is not { IsZero: false } g || !double.IsFinite(g.Latitude) || !double.IsFinite(g.Longitude)
            || Math.Abs(g.Latitude) > 90 || Math.Abs(g.Longitude) > 180)
            return null;
        return string.Format(Tr.Culture, "{0:0.00000}° {1}, {2:0.00000}° {3}",
            Math.Abs(g.Latitude), g.Latitude >= 0 ? Tr.T("N") : Tr.T("S"),
            Math.Abs(g.Longitude), g.Longitude >= 0 ? Tr.T("O") : Tr.T("W"));
    }

    private static string? Altitude(GpsDirectory? gps)
    {
        if (gps is null || !gps.TryGetRational(GpsDirectory.TagAltitude, out var r) || r.Denominator == 0) return null;
        var below = gps.TryGetInt32(GpsDirectory.TagAltitudeRef, out var reference) && reference == 1;
        var metres = r.ToDouble();
        if (!double.IsFinite(metres) || Math.Abs(metres) > 100_000) return null;
        return string.Format(Tr.Culture, "{0:0} m", below ? -metres : metres);
    }

    // ------------------------------------------------------------ Description

    private static string? Keywords(IptcDirectory? iptc, IReadOnlyDictionary<string, string>? xmp)
    {
        var words = new List<string>();
        if (iptc?.GetStringArray(IptcDirectory.TagKeywords) is { } k) words.AddRange(k);
        if (xmp is not null)
            words.AddRange(xmp.Where(p => p.Key.StartsWith("dc:subject[", StringComparison.Ordinal)).Select(p => p.Value));
        var distinct = words.Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        return distinct.Count == 0 ? null : string.Join(", ", distinct);
    }

    private static string? Rating(IReadOnlyDictionary<string, string>? xmp)
    {
        if (Xmp(xmp, "xmp:Rating") is not { } text
            || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stars) || stars < 0 || stars > 5)
            return null;
        return new string('★', stars) + new string('☆', 5 - stars);
    }

    /// <summary>XMP properties by path ("dc:creator[1]" …), or null without XMP or if the XMP could not be read.</summary>
    private static IReadOnlyDictionary<string, string>? SafeXmp(XmpDirectory? xmp)
    {
        if (xmp is null) return null;
        try
        {
            return new Dictionary<string, string>(xmp.GetXmpProperties());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string? Xmp(IReadOnlyDictionary<string, string>? xmp, string path) =>
        xmp is not null && xmp.TryGetValue(path, out var v) && !string.IsNullOrWhiteSpace(v) ? Clean(v) : null;

    // ------------------------------------------------------------ Complete list

    private static (IReadOnlyList<MetadataTag> Tags, bool Truncated) AllTags(IReadOnlyList<MxDirectory> directories)
    {
        var tags = new List<MetadataTag>();
        foreach (var directory in directories)
        foreach (var tag in directory.Tags)
        {
            if (tags.Count >= MaxTags) return (tags, true);
            string? description;
            try
            {
                description = tag.Description;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                description = null;   // a damaged value must not cost the rest of the list
            }
            if (string.IsNullOrWhiteSpace(description)) continue;
            tags.Add(new MetadataTag(directory.Name, tag.Name, Clean(description)));
        }
        return (tags, false);
    }

    // ------------------------------------------------------------ Helpers

    private static MetadataField? Field(string label, string? value) => value is null ? null : new MetadataField(label, value);

    private static string? Text(MxDirectory? directory, int tag)
    {
        if (directory is null || !directory.ContainsTag(tag)) return null;
        string? text;
        try
        {
            text = directory.GetDescription(tag) ?? directory.GetString(tag);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(text) ? null : Clean(text);
    }

    private static string? Integer(MxDirectory? directory, int tag) =>
        directory is not null && directory.TryGetInt32(tag, out var v) && v > 0 ? v.ToString(Tr.Culture) : null;

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(", ", present);
    }

    /// <summary>Trimmed, control characters replaced by spaces, cut to <see cref="MaxValueLength"/>.</summary>
    internal static string Clean(string value)
    {
        var text = new StringBuilder(Math.Min(value.Length, MaxValueLength + 1));
        foreach (var c in value)
        {
            if (text.Length >= MaxValueLength) break;
            text.Append(char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c);
        }
        var cleaned = text.ToString().Trim();
        return value.Length > MaxValueLength ? cleaned + "…" : cleaned;
    }
}
