using Avalonia;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Controls;

/// <summary>Sampling point of a white-balance eyedropper in image pixels, with a short label (N, S, W).</summary>
public sealed record PickMarker(Point At, string Label, WbPoint Kind);
