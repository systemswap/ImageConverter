namespace ImageConverter.Core.Imaging;

/// <summary>
/// Pixel in Fließkomma (32 Bit je Kanal, B, G, R, A, sRGB-kodiert 0–1) für Ketten aus mehreren
/// Schritten wie die Dunst-Durchläufe: Zwischen den Schritten wird nicht auf 8 Bit gerundet,
/// erst einmal am Ende — mit Dither, damit dabei keine neuen Stufen entstehen.
/// </summary>
internal static class Pixels
{
    public static float[] FromBytes(byte[] bgra)
    {
        var px = new float[bgra.Length];
        Parallel.For(0, bgra.Length / 4, p =>
        {
            var i = p * 4;
            px[i] = bgra[i] / 255f;
            px[i + 1] = bgra[i + 1] / 255f;
            px[i + 2] = bgra[i + 2] / 255f;
            px[i + 3] = bgra[i + 3] / 255f;
        });
        return px;
    }

    /// <param name="dither">Dreieck-Rauschen von ±1 Stufe vor dem Runden (TPDF): Ein weicher Verlauf
    /// zwischen zwei 8-Bit-Stufen wird so zu einem feinen Korn statt zu einer sichtbaren Kante.
    /// Fest je Pixelort — die Vorschau flimmert nicht.</param>
    public static byte[] ToBytes(float[] px, bool dither)
    {
        var bgra = new byte[px.Length];
        Parallel.For(0, px.Length / 4, p =>
        {
            var i = p * 4;
            for (var c = 0; c < 3; c++)
            {
                var v = px[i + c] * 255;
                // Schwarz und Weiß bleiben rein (Rauschen läuft zu den Enden hin aus)
                if (dither) v += (Noise((uint)(i + c)) + Noise((uint)(i + c) ^ 0x9E3779B9u) - 1) * Math.Clamp(Math.Min(v, 255 - v), 0, 1);
                bgra[i + c] = ToByte(v);
            }
            bgra[i + 3] = ToByte(px[i + 3] * 255);
        });
        return bgra;
    }

    private static byte ToByte(float v) => (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v + 0.5f);

    /// <summary>Gleichverteilte Zahl 0 … 1 aus einem Ganzzahl-Schlüssel (Hash, ohne Zustand).</summary>
    internal static float Noise(uint key)
    {
        key ^= key >> 16; key *= 0x7FEB352Du;
        key ^= key >> 15; key *= 0x846CA68Bu;
        key ^= key >> 16;
        return (key >> 8) * (1f / (1 << 24));
    }
}
