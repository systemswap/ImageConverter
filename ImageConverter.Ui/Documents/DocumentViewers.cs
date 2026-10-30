using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Documents;

/// <summary>
/// Shows files that are not images (for example 3D models) in the main window instead of the picture. Registered once at
/// program start with <see cref="DocumentViewers.Register"/>; the rest of the program knows only this interface.
/// </summary>
public interface IDocumentViewer
{
    /// <summary>File extensions with the leading dot, lower case, for example ".stl".</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>Name of the file type in the open dialog, already translated.</summary>
    string FileTypeName { get; }

    /// <summary>Reads a file; runs the slow work in the background.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="ct">Cancelled when another file is chosen meanwhile.</param>
    /// <returns>The open document; the caller disposes it.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <remarks>Other exceptions carry a message for the user (the file is damaged, too large …).</remarks>
    Task<IOpenDocument> OpenAsync(string path, CancellationToken ct);
}

/// <summary>An open document: what replaces the picture and the settings column while it is shown.</summary>
public interface IOpenDocument : IDisposable
{
    /// <summary>Line under the file name (size, format …).</summary>
    string Details { get; }

    /// <summary>Replaces the picture area.</summary>
    Control View { get; }

    /// <summary>Replaces the settings column.</summary>
    Control Panel { get; }
}

/// <summary>The registered viewers and which files they take.</summary>
public static class DocumentViewers
{
    private static readonly List<IDocumentViewer> Registered = [];

    /// <summary>Adds a viewer; call before the main window opens.</summary>
    public static void Register(IDocumentViewer viewer) => Registered.Add(viewer);

    public static IReadOnlyList<IDocumentViewer> All => Registered;

    /// <summary>The viewer for a file by its extension, or null for images and unknown files.</summary>
    public static IDocumentViewer? For(string? path)
    {
        if (path is null || Registered.Count == 0) return null;
        var ext = Path.GetExtension(path);
        return Registered.FirstOrDefault(v => v.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Whether the program opens the file: an image it can read or a document a viewer takes.</summary>
    public static bool CanOpen(string path) => ImageFormats.CanRead(path) || For(path) is not null;
}
