using System.Text;

namespace ImageConverter.Core.Ai.SyntheticDoc;

/// <summary>
/// Baut ein ONNX-Modell (Graph aus Knoten, Gewichte als Initialisierer) und schreibt es als Protobuf — ohne Abhängigkeit,
/// nur die Felder von <c>onnx.proto</c>, die ein Inferenzmodell braucht. Namen der Werte vergibt der Graph selbst.
/// </summary>
public sealed class OnnxGraph
{
    private readonly List<byte[]> _nodes = [];
    private readonly List<byte[]> _initializers = [];
    private readonly List<byte[]> _inputs = [];
    private readonly List<byte[]> _outputs = [];
    private int _next;

    public const int Opset = 17;

    /// <summary>Eingang float32 mit Form; Zahlen sind feste, Texte benannte (offene) Größen.</summary>
    public string Input(string name, params object[] dims)
    {
        _inputs.Add(ValueInfo(name, dims));
        return name;
    }

    /// <summary>Ein schon erzeugter Wert wird Ausgang (über Identity unter dem gewünschten Namen).</summary>
    public void Output(string value, string name, params object[] dims)
    {
        Node("Identity", [value], name);
        _outputs.Add(ValueInfo(name, dims));
    }

    /// <summary>Gewicht (float32).</summary>
    public string Weight(float[] data, params long[] dims)
    {
        var name = "w" + _next++;
        var raw = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, raw, 0, raw.Length);
        _initializers.Add(Tensor(name, 1, dims, raw));
        return name;
    }

    /// <summary>Ganzzahlige Konstante (int64), z. B. Polster oder Größen.</summary>
    public string Ints(params long[] values)
    {
        var name = "c" + _next++;
        var raw = new byte[values.Length * 8];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        _initializers.Add(Tensor(name, 7, [values.Length], raw));
        return name;
    }

    /// <summary>Knoten mit einem Ausgang; Attribute als (Name, Wert) mit long, float, string, long[] oder float[].</summary>
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

    /// <summary>Das fertige Modell (ModelProto).</summary>
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

    /// <summary>Protobuf-Kodierung: Varint, Längenpräfix, 32-Bit-Float.</summary>
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
