using System.Buffers.Binary;
using System.Numerics;

namespace ImageConverter.Viewer3D.Geometry;

/// <summary>Writes a <see cref="Mesh"/> as binary STL (80-byte header, triangle count, 50 bytes per triangle).</summary>
public static class StlWriter
{
    /// <summary>
    /// Writes the file next to the target first and replaces the target only when it is complete, so that a failure
    /// (disk full, cancelled) never leaves a half-written model.
    /// </summary>
    /// <param name="path">Target file; replaced if it exists.</param>
    /// <param name="mesh">The model.</param>
    /// <param name="header">Text for the header (ASCII, up to 80 characters; must not start with "solid").</param>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">No permission for the folder.</exception>
    public static void Write(string path, Mesh mesh, string header = "binary STL")
    {
        var tmp = path + ".part";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                Write(stream, mesh, header);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Writes the binary STL data to a stream.</summary>
    public static void Write(Stream stream, Mesh mesh, string header = "binary STL")
    {
        Span<byte> head = stackalloc byte[84];
        head.Clear();
        var text = header.StartsWith("solid", StringComparison.OrdinalIgnoreCase) ? "binary " + header : header;
        for (var i = 0; i < Math.Min(80, text.Length); i++) head[i] = text[i] is >= ' ' and <= '~' ? (byte)text[i] : (byte)'?';
        BinaryPrimitives.WriteUInt32LittleEndian(head[80..], (uint)mesh.TriangleCount);
        stream.Write(head);
        var buffer = new byte[50 * 1024];
        var used = 0;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.Corners(t);
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.Zero;
            var span = buffer.AsSpan(used, 50);
            Put(span, 0, n);
            Put(span, 12, a);
            Put(span, 24, b);
            Put(span, 36, c);
            span[48] = span[49] = 0;
            used += 50;
            if (used == buffer.Length)
            {
                stream.Write(buffer, 0, used);
                used = 0;
            }
        }
        stream.Write(buffer, 0, used);
    }

    private static void Put(Span<byte> span, int at, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(span[at..], v.X);
        BinaryPrimitives.WriteSingleLittleEndian(span[(at + 4)..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(span[(at + 8)..], v.Z);
    }
}
