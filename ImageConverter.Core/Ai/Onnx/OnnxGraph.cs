using System.Text;

namespace ImageConverter.Core.Ai.Onnx;

/// <summary>
/// Builds an ONNX model (graph of nodes, weights as initialisers) and writes it as Protobuf.
/// </summary>
/// <remarks>
/// No external dependency: only the fields of <c>onnx.proto</c> that an inference model needs are written (IR version 8,
/// default opset <see cref="Opset"/>). The graph assigns the names of intermediate values itself; the methods return them.
/// Not thread-safe.
/// </remarks>
public sealed class OnnxGraph : IDisposable
{
    private readonly List<byte[]> _nodes = [];
    private readonly List<byte[]> _initializers = [];
    private readonly List<byte[]> _inputs = [];
    private readonly List<byte[]> _outputs = [];
    private int _next;

    // External weights (models over 2 GB, the Protobuf limit): large initialisers go straight into a file next to the
    // model when they are created; the model only stores location, offset and length
    private readonly FileStream? _external;
    private readonly string? _externalName;
    private const int ExternalFrom = 4096;   // store externally from this many bytes

    /// <summary>Version of the default ONNX operator set the model is written for.</summary>
    public const int Opset = 17;

    /// <summary>Creates a graph that keeps all weights inside the model (up to 2 GB in total).</summary>
    public OnnxGraph()
    {
    }

    /// <summary>
    /// Creates a graph that stores large weights (4096 bytes and more) in <paramref name="externalDataPath"/>
    /// (ONNX "external data").
    /// </summary>
    /// <param name="externalDataPath">File for the weights; it is created or overwritten immediately.</param>
    /// <remarks>
    /// The model must then be saved in the same folder, because it refers to the data file by file name only.
    /// Dispose the graph after <see cref="Save"/> to close the data file.
    /// </remarks>
    public OnnxGraph(string externalDataPath)
    {
        _external = File.Create(externalDataPath);
        _externalName = Path.GetFileName(externalDataPath);
    }

    /// <summary>Closes the external data file, if any.</summary>
    public void Dispose() => _external?.Dispose();

    /// <summary>Declares a float32 graph input.</summary>
    /// <param name="name">Name of the input.</param>
    /// <param name="dims">Shape: numbers are fixed sizes, strings are named (dynamic) dimensions.</param>
    /// <returns><paramref name="name"/>, for use as a node input.</returns>
    /// <remarks>Inputs and outputs are always float32; a graph in half precision converts itself (Cast).</remarks>
    public string Input(string name, params object[] dims)
    {
        _inputs.Add(ValueInfo(name, dims));
        return name;
    }

    /// <summary>Declares an existing value as a float32 graph output (through an Identity node under the desired name).</summary>
    /// <param name="value">Name of the value to expose.</param>
    /// <param name="name">Name of the output.</param>
    /// <param name="dims">Shape: numbers are fixed sizes, strings are named (dynamic) dimensions.</param>
    public void Output(string value, string name, params object[] dims)
    {
        Node("Identity", [value], name);
        _outputs.Add(ValueInfo(name, dims));
    }

    /// <summary>Adds a float32 weight.</summary>
    /// <param name="data">Values in C order.</param>
    /// <param name="dims">Shape of the tensor.</param>
    /// <returns>Name of the initialiser.</returns>
    public string Weight(float[] data, params long[] dims) =>
        Initializer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()), 1, dims);

    /// <summary>Adds a weight in half precision (float16), which halves the memory on the graphics card.</summary>
    /// <param name="data">Values in C order; they are rounded to float16.</param>
    /// <param name="dims">Shape of the tensor.</param>
    /// <returns>Name of the initialiser.</returns>
    public string WeightHalf(float[] data, params long[] dims)
    {
        var half = new Half[data.Length];
        for (var i = 0; i < data.Length; i++) half[i] = (Half)data[i];
        return Initializer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(half.AsSpan()), 10, dims);
    }

    private string Initializer(ReadOnlySpan<byte> bytes, int dataType, long[] dims)
    {
        var name = "w" + _next++;
        if (_external is not null && bytes.Length >= ExternalFrom)
        {
            // Align to 64 bytes so that ONNX Runtime can memory-map the file directly
            var pad = (int)((64 - _external.Position % 64) % 64);
            if (pad > 0) _external.Write(new byte[pad]);
            var offset = _external.Position;
            _external.Write(bytes);
            _initializers.Add(ExternalTensor(name, dataType, dims, _externalName!, offset, bytes.Length));
        }
        else
        {
            _initializers.Add(Tensor(name, dataType, dims, bytes.ToArray()));
        }
        return name;
    }

    /// <summary>Writes the model to a file.</summary>
    /// <param name="path">Target file; with external weights it must be in the same folder as the data file.</param>
    /// <param name="producer">Producer name stored in the model.</param>
    /// <remarks>External weights are flushed to their file first.</remarks>
    public void Save(string path, string producer = "ImageConverter")
    {
        _external?.Flush();
        File.WriteAllBytes(path, ToModel(producer));
    }

    /// <summary>Adds a one-dimensional int64 constant, for example padding or sizes.</summary>
    /// <param name="values">Values of the constant.</param>
    /// <returns>Name of the initialiser.</returns>
    public string Ints(params long[] values)
    {
        var name = "c" + _next++;
        var raw = new byte[values.Length * 8];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        _initializers.Add(Tensor(name, 7, [values.Length], raw));
        return name;
    }

    /// <summary>Adds a node with one output.</summary>
    /// <param name="op">ONNX operator type, for example "Conv".</param>
    /// <param name="inputs">Names of the input values.</param>
    /// <param name="attributes">
    /// Attributes as (name, value); supported value types are int, long, float, double (stored as float), string,
    /// int[], long[] and float[].
    /// </param>
    /// <returns>Name of the output value.</returns>
    /// <exception cref="ArgumentException">An attribute value has an unsupported type.</exception>
    public string Node(string op, string[] inputs, params (string Name, object Value)[] attributes) =>
        Node(op, inputs, "v" + _next++, attributes);

    private string Node(string op, string[] inputs, string output, params (string Name, object Value)[] attributes)
    {
        var w = new ProtoWriter();
        foreach (var i in inputs) w.String(1, i);
        w.String(2, output);
        w.String(3, op + "_" + _next++);
        w.String(4, op);
        foreach (var (name, value) in attributes) w.Message(5, Attribute(name, value));
        _nodes.Add(w.ToArray());
        return output;
    }

    /// <summary>Serialises the model (ModelProto).</summary>
    /// <param name="producer">Producer name stored in the model.</param>
    /// <returns>The encoded model; with external weights it refers to the data file.</returns>
    public byte[] ToModel(string producer = "ImageConverter")
    {
        var graph = new ProtoWriter();
        foreach (var n in _nodes) graph.Message(1, n);
        graph.String(2, "main");
        foreach (var t in _initializers) graph.Message(5, t);
        foreach (var i in _inputs) graph.Message(11, i);
        foreach (var o in _outputs) graph.Message(12, o);

        var opset = new ProtoWriter();
        opset.String(1, "");
        opset.Int(2, Opset);

        var model = new ProtoWriter();
        model.Int(1, 8);              // ir_version
        model.String(2, producer);
        model.Message(7, graph.ToArray());
        model.Message(8, opset.ToArray());
        return model.ToArray();
    }

    private static byte[] Tensor(string name, int dataType, long[] dims, byte[] raw)
    {
        var w = new ProtoWriter();
        foreach (var d in dims) w.Int(1, d);
        w.Int(2, dataType);
        w.String(8, name);
        w.Bytes(9, raw);
        return w.ToArray();
    }

    private static byte[] ExternalTensor(string name, int dataType, long[] dims, string location, long offset, long length)
    {
        var w = new ProtoWriter();
        foreach (var d in dims) w.Int(1, d);
        w.Int(2, dataType);
        w.String(8, name);
        foreach (var (k, v) in new[] { ("location", location), ("offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                     ("length", length.ToString(System.Globalization.CultureInfo.InvariantCulture)) })
        {
            var entry = new ProtoWriter();
            entry.String(1, k);
            entry.String(2, v);
            w.Message(13, entry.ToArray());   // external_data
        }
        w.Int(14, 1);                         // data_location = EXTERNAL
        return w.ToArray();
    }

    private static byte[] ValueInfo(string name, object[] dims)
    {
        var shape = new ProtoWriter();
        foreach (var d in dims)
        {
            var dim = new ProtoWriter();
            if (d is string p) dim.String(2, p);
            else dim.Int(1, Convert.ToInt64(d));
            shape.Message(1, dim.ToArray());
        }
        var tensorType = new ProtoWriter();
        tensorType.Int(1, 1);                       // float32
        tensorType.Message(2, shape.ToArray());
        var type = new ProtoWriter();
        type.Message(1, tensorType.ToArray());
        var w = new ProtoWriter();
        w.String(1, name);
        w.Message(2, type.ToArray());
        return w.ToArray();
    }

    private static byte[] Attribute(string name, object value)
    {
        var w = new ProtoWriter();
        w.String(1, name);
        switch (value)
        {
            case float f: w.Float(2, f); w.Int(20, 1); break;
            case double d: w.Float(2, (float)d); w.Int(20, 1); break;
            case int i: w.Int(3, i); w.Int(20, 2); break;
            case long l: w.Int(3, l); w.Int(20, 2); break;
            case string s: w.Bytes(4, Encoding.UTF8.GetBytes(s)); w.Int(20, 3); break;
            case float[] fs: foreach (var f in fs) w.Float(7, f); w.Int(20, 6); break;
            case long[] ls: foreach (var l in ls) w.Int(8, l); w.Int(20, 7); break;
            case int[] ints: foreach (var l in ints) w.Int(8, l); w.Int(20, 7); break;
            default: throw new ArgumentException($"Attributtyp {value.GetType().Name} wird nicht unterstützt.", nameof(value));
        }
        return w.ToArray();
    }

    /// <summary>Protobuf encoding: varint, length prefix, 32-bit float.</summary>
    private sealed class ProtoWriter
    {
        private readonly MemoryStream _s = new();

        public byte[] ToArray() => _s.ToArray();

        private void Varint(ulong v)
        {
            while (v >= 0x80)
            {
                _s.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            _s.WriteByte((byte)v);
        }

        private void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

        public void Int(int field, long value)
        {
            Tag(field, 0);
            Varint(unchecked((ulong)value));
        }

        public void Float(int field, float value)
        {
            Tag(field, 5);
            Span<byte> b = stackalloc byte[4];
            BitConverter.TryWriteBytes(b, value);
            _s.Write(b);
        }

        public void Bytes(int field, byte[] value)
        {
            Tag(field, 2);
            Varint((ulong)value.Length);
            _s.Write(value);
        }

        public void String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public void Message(int field, byte[] message) => Bytes(field, message);
    }
}
