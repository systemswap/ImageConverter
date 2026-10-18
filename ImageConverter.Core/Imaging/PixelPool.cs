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
    /// <remarks>
    /// Eine normale Sammlung (auch mit LOH-Verdichtung) gibt den Speicher nicht zurück: nach dem Export des Panoramas
    /// (28 MP) blieben 4,1 GB „zugesagt“ bei 0,16 GB belegt, 7,0 GB im Arbeitsspeicher. Erst
    /// <see cref="GCCollectionMode.Aggressive"/> gibt ihn frei (danach 1,2 GB, etwa 0,1 s Pause), unter Linux dazu
    /// <c>malloc_trim</c> für den freigegebenen Speicher von ImageMagick. Am Ergebnis ändert das nichts.
    /// </remarks>
    public static void Trim(bool release = false)
    {
        Free.Clear();
        if (!release) return;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        // Was erst Finalizer oder Rückrufe nach der Sammlung loslassen (z. B. geteilte Puffer-Pools), fällt erst bei der
        // nächsten Sammlung weg — gemessen blieben nach nur einer noch 2 GB belegt
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        if (OperatingSystem.IsLinux())
        {
            try
            {
                _ = malloc_trim(0);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // andere C-Bibliothek (musl): nicht nötig
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int malloc_trim(nuint pad);
}
