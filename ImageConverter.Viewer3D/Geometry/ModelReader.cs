namespace ImageConverter.Viewer3D.Geometry;

/// <summary>Chooses the reader for a 3D model file by its extension: STL (binary or text) or 3MF.</summary>
public static class ModelReader
{
    /// <summary>File extensions the viewer opens, lower case with the leading dot.</summary>
    public static IReadOnlyCollection<string> Extensions { get; } = [".stl", ".3mf"];

    /// <summary>Whether a file is read as 3MF (by its extension).</summary>
    /// <param name="path">Path of the file.</param>
    /// <returns>True for ".3mf".</returns>
    public static bool IsThreeMf(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), ".3mf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a model file with the reader that matches its extension.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="ct">Cancels reading.</param>
    /// <param name="progress">Share of the file read so far (0 … 1); may be null.</param>
    /// <returns>The mesh and what was found on the way.</returns>
    /// <exception cref="StlFormatException">Not readable; the message is meant for the user.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static StlFile Read(string path, CancellationToken ct = default, IProgress<double>? progress = null) =>
        IsThreeMf(path) ? ThreeMfReader.Read(path, ct, progress) : StlReader.Read(path, ct, progress);
}
