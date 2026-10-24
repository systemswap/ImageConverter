using System.IO.Compression;
using System.Text;

namespace ImageConverter.Core.Ai.Onnx;

/// <summary>A tensor from a checkpoint or weights file.</summary>
/// <param name="Data">Values as float32, contiguous (C order).</param>
/// <param name="Shape">Dimensions of the tensor.</param>
public sealed record TorchTensor(float[] Data, int[] Shape)
{
    /// <summary>Gets the number of values.</summary>
    public int Count => Data.Length;

    /// <summary>Returns the shape as text, for example "[64, 3, 3, 3]".</summary>
    /// <returns>The dimensions in square brackets.</returns>
    public override string ToString() => $"[{string.Join(", ", Shape)}]";
}

/// <summary>
/// Reads a PyTorch checkpoint without Python.
/// </summary>
/// <remarks>
/// Supported is the zip format of <c>torch.save</c> (PyTorch 1.6 and later, also written by Lightning): a zip archive with
/// <c>&lt;name&gt;/data.pkl</c> (pickle, protocol 2) and the raw storages <c>&lt;name&gt;/data/&lt;key&gt;</c>
/// (little-endian). A minimal unpickler handles the opcodes that <c>torch.save</c> writes; unknown classes become
/// <see cref="PyObject"/> (name and state), tensors become <see cref="TorchTensor"/> (float32; integer and boolean
/// storages are converted). Nothing is executed, unlike loading in Python. The pickle data and every referenced
/// storage are read completely into memory.
/// </remarks>
public sealed class TorchCheckpoint
{
    private TorchCheckpoint(object? root) => Root = root;

    /// <summary>
    /// Gets the content of the checkpoint (dictionaries as <see cref="Dictionary{TKey,TValue}"/>, lists as
    /// <see cref="List{T}"/>, tuples as object[]).
    /// </summary>
    public object? Root { get; }

    /// <summary>
    /// Gets the weights, name → tensor: the entry <c>state_dict</c> of the root dictionary, or the root dictionary itself
    /// if it has no such entry.
    /// </summary>
    /// <remarks>
    /// Only entries with a string key and a tensor value are included; empty if the root is not a dictionary. Each access
    /// builds a new dictionary.
    /// </remarks>
    public IReadOnlyDictionary<string, TorchTensor> StateDict
    {
        get
        {
            var dict = Root as Dictionary<object, object?>;
            var sd = dict is not null && dict.TryGetValue("state_dict", out var s) ? s as Dictionary<object, object?> : dict;
            return sd?.Where(p => p.Key is string && p.Value is TorchTensor)
                       .ToDictionary(p => (string)p.Key, p => (TorchTensor)p.Value!)
                   ?? new Dictionary<string, TorchTensor>();
        }
    }

    /// <summary>Returns the value under a path such as "hyper_parameters/data/bm_out_size".</summary>
    /// <param name="path">Keys separated by "/"; each step looks into a dictionary or into the items or state of a <see cref="PyObject"/>.</param>
    /// <returns>The value, or null if the path does not exist.</returns>
    public object? Get(string path)
    {
        object? cur = Root;
        foreach (var part in path.Split('/'))
        {
            cur = cur switch
            {
                Dictionary<object, object?> d => d.TryGetValue(part, out var v) ? v : null,
                PyObject o => o.Items.TryGetValue(part, out var v) ? v : o.State is Dictionary<object, object?> st && st.TryGetValue(part, out var w) ? w : null,
                _ => null,
            };
            if (cur is null) return null;
        }
        return cur;
    }

    /// <summary>Loads a checkpoint from a file.</summary>
    /// <param name="path">Path of the checkpoint file.</param>
    /// <returns>The loaded checkpoint.</returns>
    /// <exception cref="InvalidDataException">
    /// The file is not a supported checkpoint (no data.pkl, big-endian, missing storage, unsupported pickle opcode,
    /// persistent reference or storage type).
    /// </exception>
    public static TorchCheckpoint Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Loads a checkpoint from a stream.</summary>
    /// <param name="stream">Stream with the zip archive; it is left open.</param>
    /// <returns>The loaded checkpoint.</returns>
    /// <exception cref="InvalidDataException">
    /// The data is not a supported checkpoint (no data.pkl, big-endian, missing storage, unsupported pickle opcode,
    /// persistent reference or storage type).
    /// </exception>
    public static TorchCheckpoint Load(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var pkl = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/data.pkl", StringComparison.Ordinal) || e.FullName == "data.pkl")
                  ?? throw new InvalidDataException("Kein PyTorch-Checkpoint (data.pkl fehlt).");
        var prefix = pkl.FullName[..^"data.pkl".Length];
        var byteOrder = zip.GetEntry(prefix + "byteorder");
        if (byteOrder is not null)
        {
            using var r = new StreamReader(byteOrder.Open());
            if (r.ReadToEnd().Trim() != "little") throw new InvalidDataException("Nur Little-Endian-Checkpoints werden unterstützt.");
        }
        byte[] data;
        using (var s = pkl.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            data = ms.ToArray();
        }
        byte[] Storage(string key)
        {
            var entry = zip.GetEntry(prefix + "data/" + key) ?? throw new InvalidDataException($"Speicher {key} fehlt im Checkpoint.");
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        return new TorchCheckpoint(new Unpickler(data, Storage).Run());
    }

    /// <summary>Unknown Python class (or call) in the pickle: name, call arguments, state and attached entries.</summary>
    /// <param name="name">Full name (module.name) of the class or function.</param>
    /// <param name="args">Arguments of the call, or null.</param>
    public sealed class PyObject(string name, object?[]? args = null)
    {
        /// <summary>Gets the full name (module.name) of the class or function; for other callables their text form.</summary>
        public string Name { get; } = name;

        /// <summary>Gets the arguments of the call, or null.</summary>
        public object?[]? Args { get; } = args;

        /// <summary>Gets or sets the state passed by the pickle BUILD opcode.</summary>
        public object? State { get; set; }

        /// <summary>Gets the entries set by SETITEM/SETITEMS.</summary>
        public Dictionary<object, object?> Items { get; } = [];

        /// <summary>Gets the entries added by APPEND/APPENDS/ADDITEMS.</summary>
        public List<object?> List { get; } = [];

        /// <summary>Returns <see cref="Name"/>.</summary>
        /// <returns>The full name.</returns>
        public override string ToString() => Name;
    }

    private sealed record Global(string Module, string Name)
    {
        public string Full => Module + "." + Name;
    }

    private sealed record StorageRef(byte[] Bytes, string Type);

    private sealed class Mark;

    /// <summary>Unpickler for a subset of the opcodes of pickle protocols 2–4 (without executing code).</summary>
    private sealed class Unpickler(byte[] data, Func<string, byte[]> storage)
    {
        private int _pos;
        private readonly List<object?> _stack = [];
        private readonly Dictionary<long, object?> _memo = [];
        private readonly Dictionary<string, StorageRef> _storages = [];
        private static readonly Mark MarkObj = new();

        public object? Run()
        {
            while (true)
            {
                var op = data[_pos++];
                switch (op)
                {
                    case 0x80: _pos++; break;                                           // PROTO
                    case 0x95: _pos += 8; break;                                        // FRAME
                    case (byte)'.': return Pop();                                       // STOP
                    case (byte)'(': _stack.Add(MarkObj); break;                         // MARK
                    case (byte)'}': _stack.Add(new Dictionary<object, object?>()); break; // EMPTY_DICT
                    case (byte)']': _stack.Add(new List<object?>()); break;             // EMPTY_LIST
                    case (byte)')': _stack.Add(Array.Empty<object?>()); break;          // EMPTY_TUPLE
                    case 0x8f: _stack.Add(new HashSet<object?>()); break;               // EMPTY_SET
                    case (byte)'N': _stack.Add(null); break;                            // NONE
                    case 0x88: _stack.Add(true); break;                                 // NEWTRUE
                    case 0x89: _stack.Add(false); break;                                // NEWFALSE
                    case (byte)'J': _stack.Add((long)BitConverter.ToInt32(data, _pos)); _pos += 4; break;   // BININT
                    case (byte)'K': _stack.Add((long)data[_pos++]); break;                                  // BININT1
                    case (byte)'M': _stack.Add((long)BitConverter.ToUInt16(data, _pos)); _pos += 2; break;  // BININT2
                    case 0x8a:                                                                              // LONG1
                    {
                        int n = data[_pos++];
                        long v = 0;
                        for (var i = 0; i < n && i < 8; i++) v |= (long)data[_pos + i] << (8 * i);
                        if (n is > 0 and < 8 && (data[_pos + n - 1] & 0x80) != 0) v -= 1L << (8 * n);   // sign
                        _pos += n;
                        _stack.Add(v);
                        break;
                    }
                    case (byte)'G':                                                                         // BINFLOAT (Big Endian)
                    {
                        var b = data.AsSpan(_pos, 8).ToArray();
                        Array.Reverse(b);
                        _stack.Add(BitConverter.ToDouble(b));
                        _pos += 8;
                        break;
                    }
                    case (byte)'X': _stack.Add(Str(BitConverter.ToInt32(data, _pos), 4)); break;            // BINUNICODE
                    case 0x8c: _stack.Add(Str(data[_pos], 1)); break;                                       // SHORT_BINUNICODE
                    case 0x8d: _stack.Add(Str((int)BitConverter.ToInt64(data, _pos), 8)); break;            // BINUNICODE8
                    case (byte)'C': _stack.Add(Bytes(data[_pos], 1)); break;                                // SHORT_BINBYTES
                    case (byte)'B': _stack.Add(Bytes(BitConverter.ToInt32(data, _pos), 4)); break;          // BINBYTES
                    case (byte)'t': _stack.Add(PopMark().ToArray()); break;                                 // TUPLE
                    case 0x85: _stack.Add(new[] { Pop() }); break;                                          // TUPLE1
                    case 0x86: { var b = Pop(); var a = Pop(); _stack.Add(new[] { a, b }); break; }        // TUPLE2
                    case 0x87: { var c = Pop(); var b = Pop(); var a = Pop(); _stack.Add(new[] { a, b, c }); break; } // TUPLE3
                    case (byte)'l': _stack.Add(PopMark()); break;                                           // LIST
                    case (byte)'d':                                                                         // DICT
                    {
                        var items = PopMark();
                        var d = new Dictionary<object, object?>();
                        for (var i = 0; i + 1 < items.Count; i += 2) d[Key(items[i])] = items[i + 1];
                        _stack.Add(d);
                        break;
                    }
                    case (byte)'s': { var v = Pop(); var k = Pop(); SetItem(Top(), k, v); break; }         // SETITEM
                    case (byte)'u':                                                                         // SETITEMS
                    {
                        var items = PopMark();
                        for (var i = 0; i + 1 < items.Count; i += 2) SetItem(Top(), items[i], items[i + 1]);
                        break;
                    }
                    case (byte)'a': { var v = Pop(); Append(Top(), v); break; }                             // APPEND
                    case (byte)'e': foreach (var v in PopMark()) Append(Top(), v); break;                   // APPENDS
                    case 0x90: foreach (var v in PopMark()) Append(Top(), v); break;                        // ADDITEMS
                    case 0x91: _stack.Add(new HashSet<object?>(PopMark())); break;                          // FROZENSET
                    case (byte)'q': _memo[data[_pos++]] = Top(); break;                                     // BINPUT
                    case (byte)'r': _memo[BitConverter.ToUInt32(data, _pos)] = Top(); _pos += 4; break;     // LONG_BINPUT
                    case 0x94: _memo[_memo.Count] = Top(); break;                                           // MEMOIZE
                    case (byte)'h': _stack.Add(_memo[data[_pos++]]); break;                                 // BINGET
                    case (byte)'j': _stack.Add(_memo[BitConverter.ToUInt32(data, _pos)]); _pos += 4; break; // LONG_BINGET
                    case (byte)'c':                                                                         // GLOBAL
                    {
                        var module = Line();
                        var name = Line();
                        _stack.Add(new Global(module, name));
                        break;
                    }
                    case 0x93: { var name = (string)Pop()!; var module = (string)Pop()!; _stack.Add(new Global(module, name)); break; } // STACK_GLOBAL
                    case (byte)'R': { var args = (object?[])Pop()!; var f = Pop(); _stack.Add(Call(f, args)); break; }                 // REDUCE
                    case 0x81: { var args = (object?[])Pop()!; var cls = Pop(); _stack.Add(Call(cls, args)); break; }                  // NEWOBJ
                    case (byte)'b': { var state = Pop(); Build(Top(), state); break; }                                                 // BUILD
                    case (byte)'Q': _stack.Add(Persistent(Pop())); break;                                                              // BINPERSID
                    default:
                        throw new InvalidDataException($"Pickle-Befehl 0x{op:x2} an Stelle {_pos - 1} wird nicht unterstützt.");
                }
            }
        }

        private string Str(int length, int header)
        {
            var s = Encoding.UTF8.GetString(data, _pos + header, length);
            _pos += header + length;
            return s;
        }

        private byte[] Bytes(int length, int header)
        {
            var b = data.AsSpan(_pos + header, length).ToArray();
            _pos += header + length;
            return b;
        }

        private string Line()
        {
            var end = Array.IndexOf(data, (byte)'\n', _pos);
            var s = Encoding.ASCII.GetString(data, _pos, end - _pos);
            _pos = end + 1;
            return s;
        }

        private object? Pop()
        {
            var v = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return v;
        }

        private object? Top() => _stack[^1];

        private List<object?> PopMark()
        {
            var i = _stack.FindLastIndex(o => ReferenceEquals(o, MarkObj));
            var items = _stack.GetRange(i + 1, _stack.Count - i - 1);
            _stack.RemoveRange(i, _stack.Count - i);
            return items;
        }

        private static object Key(object? k) => k ?? "None";

        private static void SetItem(object? target, object? key, object? value)
        {
            switch (target)
            {
                case Dictionary<object, object?> d: d[Key(key)] = value; break;
                case PyObject o: o.Items[Key(key)] = value; break;
            }
        }

        private static void Append(object? target, object? value)
        {
            switch (target)
            {
                case List<object?> l: l.Add(value); break;
                case HashSet<object?> s: s.Add(value); break;
                case PyObject o: o.List.Add(value); break;
            }
        }

        private static void Build(object? target, object? state)
        {
            if (target is PyObject o) o.State = state;
            // Tensor: its state (requires_grad and others) is not needed; ordered dictionaries stay dictionaries
        }

        /// <summary>Persistent references ('storage', type, key, device, count): the raw storage from the zip archive.</summary>
        private object? Persistent(object? pid)
        {
            if (pid is not object?[] t || t.Length < 3 || t[0] as string != "storage")
                throw new InvalidDataException("Unbekannter persistenter Verweis im Checkpoint.");
            var key = t[2]?.ToString() ?? "";
            var type = t[1] is Global g ? g.Name : t[1]?.ToString() ?? "";
            if (!_storages.TryGetValue(key, out var s)) _storages[key] = s = new StorageRef(storage(key), type);
            return s;
        }

        private object? Call(object? f, object?[] args)
        {
            if (f is not Global g) return new PyObject(f?.ToString() ?? "?", args);
            switch (g.Full)
            {
                case "collections.OrderedDict":
                case "builtins.dict":
                    return new Dictionary<object, object?>();
                case "builtins.list":
                    return new List<object?>();
                case "builtins.set":
                case "builtins.frozenset":
                    return new HashSet<object?>(args.Length > 0 && args[0] is IEnumerable<object?> e ? e : []);
                case "torch._utils._rebuild_tensor_v2":
                case "torch._utils._rebuild_tensor":
                    return RebuildTensor(args);
                case "torch._utils._rebuild_parameter":
                    return args.Length > 0 ? args[0] : null;   // nn.Parameter(tensor, requires_grad, hooks)
                default:
                    return new PyObject(g.Full, args);         // unknown: record only, execute nothing
            }
        }

        /// <summary>_rebuild_tensor_v2(storage, offset, size, stride, …) → contiguous float32 tensor.</summary>
        private static TorchTensor RebuildTensor(object?[] args)
        {
            var s = (StorageRef)args[0]!;
            var offset = Convert.ToInt64(args[1]);
            var shape = ((object?[])args[2]!).Select(Convert.ToInt32).ToArray();
            var stride = ((object?[])args[3]!).Select(Convert.ToInt64).ToArray();
            var count = shape.Aggregate(1, (a, b) => a * b);
            var (size, read) = s.Type switch
            {
                "FloatStorage" => (4, (Func<long, float>)(i => BitConverter.ToSingle(s.Bytes, (int)(i * 4)))),
                "DoubleStorage" => (8, i => (float)BitConverter.ToDouble(s.Bytes, (int)(i * 8))),
                "HalfStorage" => (2, i => (float)BitConverter.ToHalf(s.Bytes, (int)(i * 2))),
                "BFloat16Storage" => (2, i => BitConverter.Int32BitsToSingle(BitConverter.ToUInt16(s.Bytes, (int)(i * 2)) << 16)),
                "LongStorage" => (8, i => BitConverter.ToInt64(s.Bytes, (int)(i * 8))),
                "IntStorage" => (4, i => BitConverter.ToInt32(s.Bytes, (int)(i * 4))),
                "BoolStorage" or "ByteStorage" => (1, i => s.Bytes[i]),
                _ => throw new InvalidDataException($"Speichertyp {s.Type} wird nicht unterstützt."),
            };
            _ = size;
            var result = new float[count];
            var index = new int[shape.Length];
            for (var k = 0; k < count; k++)
            {
                long at = offset;
                for (var d = 0; d < shape.Length; d++) at += index[d] * stride[d];
                result[k] = read(at);
                for (var d = shape.Length - 1; d >= 0; d--)
                {
                    if (++index[d] < shape[d]) break;
                    index[d] = 0;
                }
            }
            return new TorchTensor(result, shape);
        }
    }
}
