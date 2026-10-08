using System.Collections.Concurrent;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Wiederverwendbare Pixelpuffer genau einer Länge. Die Bildkette legte je Schritt neue Puffer an
/// (bei 28,7 MP je 460 MB) — die Speicherbereinigung räumte sie spät weg, die Spitze lag bei 8 GB.
/// Mit dem Pool laufen die Durchläufe mit wenigen Puffern, die immer wieder benutzt werden.
/// </summary>
/// <remarks>
/// Regeln: Nur zurückgeben, was man selbst gemietet hat und danach nicht mehr anfasst — nie den
/// Eingang eines Aufrufers oder etwas, das in einem Zwischenspeicher liegt. Gemietete Puffer sind
/// nicht geleert. Nach einer großen Rechnung <see cref="Trim"/>, sonst bleibt der Speicher belegt.
/// </remarks>
public static class PixelPool
{
    /// <summary>Höchstens so viele freie Puffer je Länge.</summary>
    private const int MaxFreePerLength = 2;

    private static readonly ConcurrentDictionary<int, ConcurrentBag<float[]>> Free = new();

    public static float[] Rent(int length)
    {
        if (Free.TryGetValue(length, out var bag) && bag.TryTake(out var buffer)) return buffer;
        return GC.AllocateUninitializedArray<float>(length);
    }

    public static void Return(float[]? buffer)
    {
        if (buffer is null || buffer.Length == 0) return;
        var bag = Free.GetOrAdd(buffer.Length, _ => []);
        if (bag.Count < MaxFreePerLength) bag.Add(buffer);
    }

    /// <summary>Alle freien Puffer loslassen (nach Export oder Vorschau).</summary>
    /// <param name="release">Nach einer großen Rechnung (Export): den Speicher auch ans System zurückgeben —
    /// sonst behält .NET die großen Bereiche, und das Programm bleibt bei mehreren GB stehen.</param>
    public static void Trim(bool release = false)
    {
        Free.Clear();
        if (!release) return;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
    }
}
