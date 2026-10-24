using System.Text.Json;

namespace ImageConverter.Core.Ai.Onnx;

/// <summary>
/// Reads a <c>.safetensors</c> file (Hugging Face format).
/// </summary>
/// <remarks>
/// Layout: 8 bytes header length, JSON header with name → type, shape and byte range, followed by the raw data
/// (little-endian). The format holds data only, no code. Tensors are loaded one at a time and returned as float32
/// (F16 and BF16 are converted), so the whole file is never held in memory. The file stays open until
/// <see cref="Dispose"/>; <see cref="Get"/> may be called from several threads.
/// </remarks>
public sealed class SafeTensors : IDisposable
{
    private readonly FileStream _file;
    private readonly long _dataStart;
    private readonly Dictionary<string, (string Type, long[] Shape, long Begin, long End)> _index = new(StringComparer.Ordinal);

    /// <summary>Opens a file and reads its header.</summary>
    /// <param name="path">Path of the <c>.safetensors</c> file.</param>
    /// <exception cref="InvalidDataException">The header length is not positive or exceeds 100,000,000 bytes.</exception>
    public SafeTensors(string path)
    {
        _file = File.OpenRead(path);
        Span<byte> len = stackalloc byte[8];
        _file.ReadExactly(len);
        var headerLength = BitConverter.ToInt64(len);
        if (headerLength <= 0 || headerLength > 100_000_000) throw new InvalidDataException("Keine Safetensors-Datei (Kopflänge).");
        var header = new byte[headerLength];
        _file.ReadExactly(header);
        _dataStart = 8 + headerLength;
        using var json = JsonDocument.Parse(header);
        foreach (var p in json.RootElement.EnumerateObject())
        {
            if (p.Name == "__metadata__") continue;
            var type = p.Value.GetProperty("dtype").GetString()!;
            var shape = p.Value.GetProperty("shape").EnumerateArray().Select(e => e.GetInt64()).ToArray();
            var offs = p.Value.GetProperty("data_offsets").EnumerateArray().Select(e => e.GetInt64()).ToArray();
            _index[p.Name] = (type, shape, offs[0], offs[1]);
        }
    }

    /// <summary>Gets the names of all tensors in the file.</summary>
    public IEnumerable<string> Names => _index.Keys;

    /// <summary>Returns whether the file contains a tensor of this name.</summary>
    /// <param name="name">Tensor name (case-sensitive).</param>
    /// <returns>True if the tensor exists.</returns>
    public bool Contains(string name) => _index.ContainsKey(name);

    /// <summary>Returns the shape of a tensor without loading its data.</summary>
    /// <param name="name">Tensor name (case-sensitive).</param>
    /// <returns>The dimensions of the tensor.</returns>
    public long[] Shape(string name) => _index[name].Shape;

    /// <summary>Loads a tensor as float32 with contiguous values (C order).</summary>
    /// <param name="name">Tensor name (case-sensitive).</param>
    /// <returns>A new tensor; each call reads the data from the file again.</returns>
    /// <exception cref="KeyNotFoundException">The file contains no tensor of this name.</exception>
    /// <exception cref="NotSupportedException">The data type is not F32, F16 or BF16.</exception>
    public TorchTensor Get(string name)
    {
        if (!_index.TryGetValue(name, out var t)) throw new KeyNotFoundException($"Gewicht „{name}“ fehlt in der Safetensors-Datei.");
        var bytes = new byte[t.End - t.Begin];
        lock (_file)
        {
            _file.Position = _dataStart + t.Begin;
            _file.ReadExactly(bytes);
        }
        var count = t.Shape.Aggregate(1L, (a, b) => a * b);
        var data = new float[count];
        switch (t.Type)
        {
            case "F32":
                Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
                break;
            case "F16":
                for (var i = 0; i < count; i++) data[i] = (float)BitConverter.ToHalf(bytes, i * 2);
                break;
            case "BF16":
                for (var i = 0; i < count; i++) data[i] = BitConverter.Int32BitsToSingle(BitConverter.ToUInt16(bytes, i * 2) << 16);
                break;
            default:
                throw new NotSupportedException($"Datentyp {t.Type} in Safetensors wird nicht unterstützt.");
        }
        return new TorchTensor(data, t.Shape.Select(s => (int)s).ToArray());
    }

    /// <summary>Closes the file.</summary>
    public void Dispose() => _file.Dispose();

    /// <summary>Writes a single float32 tensor as a <c>.safetensors</c> file.</summary>
    /// <param name="path">Target file; it is created or overwritten.</param>
    /// <param name="name">Tensor name; it is written into the JSON header without escaping.</param>
    /// <param name="data">Values in C order.</param>
    /// <param name="shape">Shape of the tensor.</param>
    public static void Write(string path, string name, float[] data, params long[] shape)
    {
        var header = $"{{\"{name}\":{{\"dtype\":\"F32\",\"shape\":[{string.Join(",", shape)}],\"data_offsets\":[0,{data.Length * 4L}]}}}}";
        while ((header.Length + 8) % 8 != 0) header += " ";   // align the data to 8 bytes
        var bytes = System.Text.Encoding.UTF8.GetBytes(header);
        using var file = File.Create(path);
        file.Write(BitConverter.GetBytes((long)bytes.Length));
        file.Write(bytes);
        file.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()));
    }
}
