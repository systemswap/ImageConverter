namespace ImageConverter.Core.Ai.SyntheticDoc;

/// <summary>
/// SyntheticDoc-Checkpoint → ONNX, ohne Python: baut das Netz aus <c>extern/SyntheticDoc/training/model.py</c> (UVDocNet mit
/// Schatten-Decoder) Schicht für Schicht nach und setzt die Gewichte ein. BatchNorm wird in die Faltung davor gerechnet.
/// Unterschiede zum Python-Export: die Eingabegröße ist offen (Höhe und Breite durch 16 teilbar; trainiert wurde 1440 ×
/// 1024, ein grober Lauf geht auch mit 720 × 512), und die Schattenkarte kommt in Eingabegröße heraus.
/// <para>Ausgänge: <c>bm_map</c> (1 × 2 × Zeilen × Spalten, −1 … 1, align_corners = True) und <c>shadow_map</c> (1 × 3 × H × W,
/// −1 … 1; Beleuchtung = Wert · 0,5 + 0,5).</para>
/// </summary>
public static class SyntheticDocNet
{
    private const float BnEps = 1e-5f;

    /// <summary>Gitter der Rückabbildung laut Checkpoint (Zeilen, Spalten), sonst 121 × 177.</summary>
    public static (int Rows, int Cols) GridSize(TorchCheckpoint ckpt) =>
        ckpt.Get("hyper_parameters/data/bm_out_size") is List<object?> { Count: 2 } l
            ? (Convert.ToInt32(l[0]), Convert.ToInt32(l[1]))
            : (121, 177);

    /// <summary>Trainierte Eingabegröße (Höhe, Breite) laut Checkpoint, sonst 1440 × 1024.</summary>
    public static (int Height, int Width) TrainedInput(TorchCheckpoint ckpt) =>
        ckpt.Get("hyper_parameters/data/image_size") is List<object?> { Count: 2 } l
            ? (Convert.ToInt32(l[0]), Convert.ToInt32(l[1]))
            : (1440, 1024);

    /// <summary>Checkpoint-Datei → ONNX-Datei (Bytes).</summary>
    public static byte[] ToOnnx(string checkpointPath) => ToOnnx(TorchCheckpoint.Load(checkpointPath));

    public static byte[] ToOnnx(TorchCheckpoint ckpt, bool shadow = true, (int Height, int Width)? fixedInput = null)
    {
        var sd = ckpt.StateDict;
        if (sd.Count == 0) throw new InvalidDataException("Im Checkpoint sind keine Gewichte.");
        // Lightning legt das Netz unter „model.“ ab, ein reiner state_dict nicht
        var prefix = sd.Keys.Any(k => k.StartsWith("model.", StringComparison.Ordinal)) ? "model." : "";
        TorchTensor T(string name) => sd.TryGetValue(prefix + name, out var t) ? t : throw new InvalidDataException($"Gewicht {name} fehlt im Checkpoint.");
        bool Has(string name) => sd.ContainsKey(prefix + name);

        var (rows, cols) = GridSize(ckpt);
        var g = new OnnxGraph();
        object ih = fixedInput?.Height ?? (object)"height", iw = fixedInput?.Width ?? (object)"width";
        var x = g.Input("image", 1, 3, ih, iw);

        // Faltung (+ BatchNorm gefaltet); Polster gespiegelt oder mit Nullen
        string Conv(string input, string conv, string? bn, int stride = 1, int dilation = 1, bool reflect = false)
        {
            var w = T(conv + ".weight");
            int outC = w.Shape[0], k = w.Shape[2];
            var weight = (float[])w.Data.Clone();
            var bias = Has(conv + ".bias") ? (float[])T(conv + ".bias").Data.Clone() : new float[outC];
            if (bn is not null)
            {
                float[] gamma = T(bn + ".weight").Data, beta = T(bn + ".bias").Data, mean = T(bn + ".running_mean").Data, var = T(bn + ".running_var").Data;
                var per = weight.Length / outC;
                for (var o = 0; o < outC; o++)
                {
                    var s = gamma[o] / MathF.Sqrt(var[o] + BnEps);
                    for (var i = 0; i < per; i++) weight[o * per + i] *= s;
                    bias[o] = (bias[o] - mean[o]) * s + beta[o];
                }
            }
            var pad = dilation * (k / 2);
            if (reflect && pad > 0)
            {
                input = g.Node("Pad", [input, g.Ints(0, 0, pad, pad, 0, 0, pad, pad)], ("mode", "reflect"));
                pad = 0;
            }
            return g.Node("Conv", [input, g.Weight(weight, w.Shape.Select(d => (long)d).ToArray()), g.Weight(bias, outC)],
                ("kernel_shape", new long[] { k, k }), ("strides", new long[] { stride, stride }),
                ("dilations", new long[] { dilation, dilation }), ("pads", new long[] { pad, pad, pad, pad }));
        }
        string Relu(string v) => g.Node("Relu", [v]);

        // Größe wie ein anderer Wert (Höhe, Breite), Kanäle wie der eigene
        string ResizeLike(string v, string like)
        {
            var sizes = g.Node("Concat", [Slice(g.Node("Shape", [v]), 0, 2), Slice(g.Node("Shape", [like]), 2, 4)], ("axis", 0L));
            return g.Node("Resize", [v, "", "", sizes], ("mode", "linear"), ("coordinate_transformation_mode", "half_pixel"));
        }
        string Slice(string v, long from, long to) => g.Node("Slice", [v, g.Ints(from), g.Ints(to), g.Ints(0)]);

        // Restblock (ResidualBlockWithDilation): oberster mit gewöhnlichen Faltungen (Schritt), die übrigen gedehnt (3)
        string Block(string input, string p, int stride, bool top)
        {
            var c1 = top ? p + ".conv1" : p + ".conv1.0";
            var c2 = top ? p + ".conv2" : p + ".conv2.0";
            var dil = top ? 1 : 3;
            var residual = Has(p + ".downsample.0.weight") ? Conv(input, p + ".downsample.0", p + ".downsample.1", stride) : input;
            var o = Relu(Conv(input, c1, p + ".bn1", top ? stride : 1, dil));
            o = Conv(o, c2, p + ".bn2", 1, dil);
            return Relu(g.Node("Add", [o, residual]));
        }
        string Layer(string input, string p, int blocks, int stride)
        {
            for (var b = 0; b < blocks; b++) input = Block(input, $"{p}.{b}", b == 0 ? stride : 1, top: b == 0);
            return input;
        }
        int Count(string p) => Enumerable.Range(0, 64).TakeWhile(b => Has($"{p}.{b}.bn1.weight")).Count();

        // Encoder mit Abzweigen
        var s1 = Relu(Conv(x, "enc_s1.0", "enc_s1.1", 2));
        var s2 = Relu(Conv(s1, "enc_s2.0", "enc_s2.1", 2));
        var s3 = Layer(s2, "enc_s3", Count("enc_s3"), 1);
        var s4 = Layer(s3, "enc_s4", Count("enc_s4"), 2);
        var feat = Layer(s4, "enc_s5", Count("enc_s5"), 2);

        // Brücke: sechs gedehnte Zweige, verbunden und auf die Kanäle zurück
        string Dil(string v, string p, int d) => Relu(Conv(v, p + ".0", p + ".1", 1, d));
        var b1 = Dil(feat, "bridge_1", 1);
        var b2 = Dil(feat, "bridge_2", 2);
        var b3 = Dil(feat, "bridge_3", 5);
        string Chain(string p, int[] d) => d.Select((dd, i) => (dd, i)).Aggregate(feat, (v, t) => Dil(v, $"{p}.{t.i}", t.dd));
        var b4 = Chain("bridge_4", [8, 3, 2]);
        var b5 = Chain("bridge_5", [12, 7, 4]);
        var b6 = Chain("bridge_6", [18, 12, 6]);
        var bridge = Relu(Conv(g.Node("Concat", [b1, b2, b3, b4, b5, b6], ("axis", 1L)), "bridge_concat.0", "bridge_concat.1"));

        // Rückabbildung: Kopf, auf 121 × 177 (bzw. 31 × 45 über jeden vierten Knoten von 121 × 177)
        var h = Conv(bridge, "bm_head.0", "bm_head.1", reflect: true);
        h = g.Node("PRelu", [h, g.Weight(T("bm_head.2.weight").Data, 1, 1, 1)]);
        h = g.Node("Tanh", [Conv(h, "bm_head.3", null, reflect: true)]);
        var full = (rows, cols) == (31, 45) ? (121L, 177L) : (rows, cols);
        var bm = g.Node("Resize", [h, "", "", g.Ints(1, 2, full.Item1, full.Item2)], ("mode", "linear"), ("coordinate_transformation_mode", "half_pixel"));
        if ((rows, cols) == (31, 45))
            bm = g.Node("Slice", [bm, g.Ints(0, 0), g.Ints(121, 177), g.Ints(2, 3), g.Ints(4, 4)]);
        g.Output(bm, "bm_map", 1, 2, rows, cols);

        // Schattenkarte: U-Net-Decoder über die Abzweige, am Ende auf Eingabegröße
        if (shadow && Has("shd_out.weight"))
        {
            string Dec(string v, string skip, string p, bool resize)
            {
                if (resize) v = ResizeLike(v, skip);
                v = g.Node("Concat", [v, skip], ("axis", 1L));
                v = Relu(Conv(v, p + ".0", p + ".1", reflect: true));
                return Relu(Conv(v, p + ".3", p + ".4"));
            }
            var d = Dec(bridge, s4, "shd_dec1", resize: true);
            d = Dec(d, s3, "shd_dec2", resize: true);
            d = Dec(d, s2, "shd_dec3", resize: false);
            d = Dec(d, s1, "shd_dec4", resize: true);
            var map = ResizeLike(Conv(d, "shd_out", null), x);
            g.Output(map, "shadow_map", 1, 3, ih, iw);
        }
        return g.ToModel("ImageConverter SyntheticDoc");
    }
}
