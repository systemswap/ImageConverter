using Avalonia;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Controls;

/// <summary>Messstelle einer Pipette (Weißabgleich) in Bildpixeln, mit kurzer Beschriftung (N, S, W).</summary>
public sealed record PickMarker(Point At, string Label, WbPoint Kind);
