using ImageConverter.Core.Metadata;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>Reading metadata for display: summary sections, complete list, limits and prepared files.</summary>
public class ImageMetadataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ic-meta-" + Guid.NewGuid().ToString("N"));

    public ImageMetadataTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>JPEG with invented camera, exposure, GPS, author, IPTC and XMP values.</summary>
    private string WriteTaggedJpeg(string? xmp = null, string? longCopyright = null)
    {
        using var img = new MagickImage(new MagickColor(20000, 30000, 40000), 64, 48);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Testhersteller");
        exif.SetValue(ExifTag.Model, "Modell 1");
        exif.SetValue(ExifTag.Software, "Testprogramm 2.0");
        exif.SetValue(ExifTag.Artist, "Erfundene Person");
        exif.SetValue(ExifTag.Copyright, longCopyright ?? "Beispiel-Copyright");
        exif.SetValue(ExifTag.DateTimeOriginal, "2026:10:25 14:30:00");
        exif.SetValue(ExifTag.ExposureTime, new Rational(1, 250));
        exif.SetValue(ExifTag.FNumber, new Rational(28, 10));
        exif.SetValue(ExifTag.ISOSpeedRatings, [200]);
        exif.SetValue(ExifTag.FocalLength, new Rational(50, 1));
        exif.SetValue(ExifTag.FocalLengthIn35mmFilm, (ushort)75);
        exif.SetValue(ExifTag.Flash, (ushort)1);
        exif.SetValue(ExifTag.ExposureProgram, (ushort)3);
        exif.SetValue(ExifTag.WhiteBalance, (ushort)0);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(48, 1), new Rational(8, 1), new Rational(156, 10)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(11, 1), new Rational(34, 1), new Rational(30, 1)]);
        exif.SetValue(ExifTag.GPSAltitudeRef, (byte)0);
        exif.SetValue(ExifTag.GPSAltitude, new Rational(520, 1));
        img.SetProfile(exif);
        var iptc = new IptcProfile();
        iptc.SetValue(IptcTag.Keyword, "Berg");
        iptc.SetValue(IptcTag.City, "Beispielstadt");
        img.SetProfile(iptc);
        if (xmp is not null) img.SetProfile(new XmpProfile(System.Text.Encoding.UTF8.GetBytes(xmp)));
        img.Density = new Density(300, 300, DensityUnit.PixelsPerInch);
        var path = Path.Combine(_dir, "bild.jpg");
        img.Write(path);
        return path;
    }

    private const string RatedXmp =
        "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
        "<rdf:Description rdf:about='' xmlns:xmp='http://ns.adobe.com/xap/1.0/' xmlns:dc='http://purl.org/dc/elements/1.1/' xmp:Rating='4'>" +
        "<dc:subject><rdf:Bag><rdf:li>Wald</rdf:li><rdf:li>berg</rdf:li></rdf:Bag></dc:subject>" +
        "</rdf:Description></rdf:RDF></x:xmpmeta>";

    private static string Value(ImageMetadata m, MetadataSection section, string label) =>
        m[section]?.Fields.FirstOrDefault(f => f.Label == label)?.Value ?? "";

    [Fact]
    public void Summary_ShowsCameraExposureLocationAuthorAndKeywords()
    {
        var m = ImageMetadataReader.Read(WriteTaggedJpeg(RatedXmp));

        Assert.Null(m.Problem);
        Assert.Equal("Testhersteller", Value(m, MetadataSection.Camera, "Hersteller"));
        Assert.Equal("Modell 1", Value(m, MetadataSection.Camera, "Modell"));
        Assert.Equal("1/250 s", Value(m, MetadataSection.Capture, "Belichtungszeit"));
        Assert.StartsWith("f/2", Value(m, MetadataSection.Capture, "Blende"));
        Assert.Equal("200", Value(m, MetadataSection.Capture, "ISO"));
        Assert.Contains("50 mm", Value(m, MetadataSection.Capture, "Brennweite"));
        Assert.Contains("75 mm", Value(m, MetadataSection.Capture, "Brennweite"));
        Assert.Equal("ausgelöst", Value(m, MetadataSection.Capture, "Blitz"));
        Assert.Contains("2026", Value(m, MetadataSection.Capture, "Aufnahmedatum"));

        var coordinates = Value(m, MetadataSection.Location, "Koordinaten");
        Assert.Contains("48", coordinates);
        Assert.Contains("° N", coordinates);
        Assert.Contains("11", coordinates);
        Assert.Equal("520 m", Value(m, MetadataSection.Location, "Höhe"));
        Assert.Contains("Beispielstadt", Value(m, MetadataSection.Location, "Ort"));

        Assert.Equal("Erfundene Person", Value(m, MetadataSection.Description, "Urheber"));
        Assert.Equal("Beispiel-Copyright", Value(m, MetadataSection.Description, "Copyright"));
        Assert.Equal("Berg, Wald", Value(m, MetadataSection.Description, "Stichwörter"));   // gleiche Wörter nur einmal
        Assert.Equal("★★★★☆", Value(m, MetadataSection.Description, "Bewertung"));

        Assert.Contains("64 × 48", Value(m, MetadataSection.File, "Bildmaße"));
        Assert.Equal("300 DPI", Value(m, MetadataSection.File, "Auflösung"));

        // Vollständige Liste: GPS und IPTC stehen mit Herkunft darin
        Assert.Contains(m.Tags, t => t.Directory == "GPS");
        Assert.Contains(m.Tags, t => t.Directory == "IPTC" && t.Value.Contains("Berg"));
        Assert.False(m.Truncated);
    }

    [Fact]
    public void FileWithoutMetadata_HasOnlyTheFileSection()
    {
        using var img = new MagickImage(new MagickColor(1000, 2000, 3000), 10, 10);
        var path = Path.Combine(_dir, "leer.png");
        img.Write(path);

        var m = ImageMetadataReader.Read(path);
        Assert.Null(m[MetadataSection.Camera]);
        Assert.Null(m[MetadataSection.Location]);
        Assert.NotNull(m[MetadataSection.File]);
    }

    [Fact]
    public void DamagedOrMissingFile_DoesNotThrow()
    {
        var broken = Path.Combine(_dir, "kaputt.jpg");
        File.WriteAllBytes(broken, [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0x4D, 0x4D, 0x00]);
        var m = ImageMetadataReader.Read(broken);
        Assert.NotNull(m);

        var missing = ImageMetadataReader.Read(Path.Combine(_dir, "gibt-es-nicht.jpg"));
        Assert.NotNull(missing.Problem);
        Assert.Empty(missing.Tags);
    }

    [Fact]
    public void VeryLongValue_IsCut_AndControlCharactersAreReplaced()
    {
        var m = ImageMetadataReader.Read(WriteTaggedJpeg(longCopyright: "A\u0007B" + new string('x', 5000)));
        var copyright = Value(m, MetadataSection.Description, "Copyright");
        Assert.True(copyright.Length <= ImageMetadataReader.MaxValueLength + 1, $"{copyright.Length}");
        Assert.EndsWith("…", copyright);
        Assert.StartsWith("A B", copyright);
        Assert.All(m.Tags, t => Assert.True(t.Value.Length <= ImageMetadataReader.MaxValueLength + 1));
    }

    [Fact]
    public void XmpWithNestedEntities_IsReadQuickly_WithoutExpanding()
    {
        // „Billion Laughs“: verschachtelte Entitäten blähen sich beim Auflösen auf Milliarden Zeichen auf
        var bomb = "<?xml version='1.0'?><!DOCTYPE lolz [<!ENTITY lol 'lol'>" +
                   string.Concat(Enumerable.Range(1, 9).Select(i =>
                       $"<!ENTITY lol{i} '{string.Concat(Enumerable.Repeat(i == 1 ? "&lol;" : $"&lol{i - 1};", 10))}'>")) +
                   "]><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
                   "<rdf:Description rdf:about='' xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>&lol9;</dc:title>" +
                   "</rdf:Description></rdf:RDF></x:xmpmeta>";
        var path = WriteTaggedJpeg(bomb);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var m = ImageMetadataReader.Read(path);
        Assert.True(clock.Elapsed.TotalSeconds < 5, $"{clock.Elapsed}");
        Assert.All(m.Tags, t => Assert.True(t.Value.Length <= ImageMetadataReader.MaxValueLength + 1));
        Assert.Equal("Testhersteller", Value(m, MetadataSection.Camera, "Hersteller"));   // der Rest bleibt lesbar
    }
}
