using System.Text.Json;

namespace ImageConverter.Core.Ai.Onnx;

/// <summary>
/// Liest eine <c>.safetensors</c>-Datei (Hugging Face): 8 Bytes Kopflänge, JSON-Kopf mit Name → Typ, Form, Bereich, danach
/// die rohen Daten (Little Endian). Reine Daten, kein Code. Tensoren werden einzeln geladen und als float32 geliefert
/// (F16 und BF16 werden umgerechnet) — so liegt nie die ganze Datei im Speicher.
/// </summary>
public sealed class SafeTensors : IDisposable
{
    private readonly FileStream _file;
    private readonly long _dataStart;
    private readonly Dictionary<string, (string Type, long[] Shape, long Begin, long End)> _index = new(StringComparer.Ordinal);

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

    public IEnumerable<string> Names => _index.Keys;

    public bool Contains(string name) => _index.ContainsKey(name);

    public long[] Shape(string name) => _index[name].Shape;

    /// <summary>Tensor als float32, Werte zusammenhängend (C-Reihenfolge).</summary>
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

    public void Dispose() => _file.Dispose();

    /// <summary>Einen Tensor (float32) als Safetensors-Datei schreiben.</summary>
    public static void Write(string path, string name, float[] data, params long[] shape)
    {
        var header = $"{{\"{name}\":{{\"dtype\":\"F32\",\"shape\":[{string.Join(",", shape)}],\"data_offsets\":[0,{data.Length * 4L}]}}}}";
        while ((header.Length + 8) % 8 != 0) header += " ";   // Daten auf 8 Bytes ausrichten
        var bytes = System.Text.Encoding.UTF8.GetBytes(header);
        using var file = File.Create(path);
        file.Write(BitConverter.GetBytes((long)bytes.Length));
        file.Write(bytes);
        file.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()));
    }
}
