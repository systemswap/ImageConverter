using System.Runtime.CompilerServices;
using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Zwischenstände von <see cref="ImageExporter.Render(MagickImage, ExportOptions, WorkPlan?, LookCache?)"/> für Läufe, die
/// dasselbe Bild immer wieder mit leicht anderen Einstellungen rechnen — die Größenschätzung nach jeder Reglerbewegung.
/// Stufen wie in der Vorschau: skaliert + entrauscht → Weißabgleich + Dunst → Tiefen/Lichter → Licht und Farbe; Schärfen
/// und Körnung danach jedes Mal (schnell). Ein Regler weiter hinten rechnet so nur ab seiner Stufe neu, das Ergebnis ist
/// dasselbe wie ohne Zwischenspeicher. Nicht für gleichzeitige Läufe; die gespeicherten Puffer gehören dem Speicher.
/// </summary>
public sealed class LookCache : IDisposable
{
    // Je Stufe wenige Einträge, zuletzt benutzt vorn. Die Schätzung rechnet zwei Proben je Stand (zwei Größen): zwei
    // Einträge halten den aktuellen Stand, beim Dunst vier, damit auch der vorige Dunst-Stand noch da ist.
    private const int InputSize = 2, DehazedSize = 4, PreparedSize = 2, AdjustedSize = 2;

    private readonly List<(InputKey Key, Input Value)> _input = [];
    private readonly List<((InputKey, Adjustments), float[])> _dehazed = [];
    private readonly List<((InputKey, Adjustments), Adjust.Prepared)> _prepared = [];
    private readonly List<((InputKey, Adjustments), float[])> _adjusted = [];

    /// <summary>Das Bild, aus dem gerechnet wird, nach Identität (MagickImage vergleicht sonst die Pixel).</summary>
    internal readonly struct SourceRef(MagickImage image) : IEquatable<SourceRef>
    {
        public MagickImage Image { get; } = image;
        public bool Equals(SourceRef other) => ReferenceEquals(Image, other.Image);
        public override bool Equals(object? obj) => obj is SourceRef other && Equals(other);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(Image);
    }

    /// <summary>Alles, wovon das skalierte und entrauschte Bild abhängt.</summary>
    internal readonly record struct InputKey(SourceRef Source, int Width, int Height, ResampleFilter Filter, bool Gamma,
        double Denoise, double ColorDenoise);

    /// <summary>Skaliertes Bild (für Profile, Alpha, Dichte) und seine Pixel nach dem Entrauschen.</summary>
    internal sealed record Input(MagickImage Resized, float[] Pixels, bool Processed);

    /// <summary>Zahl der Stufen, die beim letzten Lauf neu gerechnet wurden (für Tests).</summary>
    internal int Computed { get; private set; }

    internal void BeginRun() => Computed = 0;

    internal Input GetInput(InputKey key, Func<Input> make) =>
        Get(_input, key, make, InputSize, e => e.Resized.Dispose());

    internal float[] GetDehazed(InputKey key, Adjustments dehazeKey, Func<float[]> make) =>
        Get(_dehazed, (key, dehazeKey), make, DehazedSize, null);

    internal Adjust.Prepared GetPrepared(InputKey key, Adjustments prepareKey, Func<Adjust.Prepared> make) =>
        Get(_prepared, (key, prepareKey), make, PreparedSize, null);

    internal float[] GetAdjusted(InputKey key, Adjustments adjust, Func<float[]> make) =>
        Get(_adjusted, (key, adjust), make, AdjustedSize, null);

    private T Get<TKey, T>(List<(TKey Key, T Value)> list, TKey key, Func<T> make, int size, Action<T>? evicted)
        where TKey : IEquatable<TKey>
    {
        var i = list.FindIndex(e => e.Key.Equals(key));
        if (i >= 0)
        {
            var hit = list[i];
            list.RemoveAt(i);
            list.Insert(0, hit);
            return hit.Value;
        }
        var value = make();
        Computed++;
        list.Insert(0, (key, value));
        while (list.Count > size)
        {
            evicted?.Invoke(list[^1].Value);
            list.RemoveAt(list.Count - 1);
        }
        return value;
    }

    /// <summary>Alles verwerfen (anderes Bild).</summary>
    public void Clear()
    {
        foreach (var (_, input) in _input) input.Resized.Dispose();
        _input.Clear();
        _dehazed.Clear();
        _prepared.Clear();
        _adjusted.Clear();
    }

    public void Dispose() => Clear();
}
