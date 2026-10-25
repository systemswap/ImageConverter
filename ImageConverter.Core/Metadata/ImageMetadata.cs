namespace ImageConverter.Core.Metadata;

/// <summary>Sections of the readable summary, in display order.</summary>
public enum MetadataSection
{
    /// <summary>Format, file size, pixel dimensions, resolution.</summary>
    File,

    /// <summary>Camera, lens, software.</summary>
    Camera,

    /// <summary>Capture date and exposure settings.</summary>
    Capture,

    /// <summary>GPS position.</summary>
    Location,

    /// <summary>Author, copyright, title, caption, keywords, rating.</summary>
    Description,

    /// <summary>Embedded ICC colour profile.</summary>
    ColourProfile,
}

/// <summary>One labelled value of the summary, already formatted for display.</summary>
/// <param name="Label">Localised label.</param>
/// <param name="Value">Formatted value.</param>
public sealed record MetadataField(string Label, string Value);

/// <summary>A section of the summary with its fields (never empty).</summary>
/// <param name="Section">Which section this is.</param>
/// <param name="Title">Localised section title.</param>
/// <param name="Fields">Fields in display order.</param>
public sealed record MetadataGroup(MetadataSection Section, string Title, IReadOnlyList<MetadataField> Fields);

/// <summary>One raw entry as stored in the file, for the complete list.</summary>
/// <param name="Directory">Where it is stored, e.g. "Exif IFD0", "GPS", "IPTC", "XMP".</param>
/// <param name="Name">Name of the tag.</param>
/// <param name="Value">Value as described by the reader, shortened if very long.</param>
public sealed record MetadataTag(string Directory, string Name, string Value);

/// <summary>
/// Everything readable about an image file: a short summary in sections and the complete list of entries. Read once
/// from the original file; edits in the program do not change it.
/// </summary>
/// <param name="Groups">Summary sections that have at least one value.</param>
/// <param name="Tags">All entries, at most <see cref="ImageMetadataReader.MaxTags"/>.</param>
/// <param name="Truncated">More entries were present than are listed.</param>
/// <param name="Problem">Why reading failed or was incomplete, or null.</param>
public sealed record ImageMetadata(IReadOnlyList<MetadataGroup> Groups, IReadOnlyList<MetadataTag> Tags, bool Truncated, string? Problem)
{
    /// <summary>Nothing known (no image, or the file could not be read at all).</summary>
    public static ImageMetadata Empty { get; } = new([], [], false, null);

    /// <summary>The summary section of the given kind, or null if the file has none of its values.</summary>
    public MetadataGroup? this[MetadataSection section] => Groups.FirstOrDefault(g => g.Section == section);
}
