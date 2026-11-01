using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Viewer3D.Editing;
using ImageConverter.Viewer3D.Geometry;
using ImageConverter.Viewer3D.Rendering;

namespace ImageConverter.Viewer3D;

/// <summary>
/// A face drawn on a plane across file axis <c>Axis</c> at <c>Plane</c>, the material on the side opposite <c>Sign</c>.
/// <c>A</c>/<c>B</c>: the corners in the two other axes (rectangle) or centre and a point on the rim (circle).
/// </summary>
internal readonly record struct DrawnFace(int Axis, int Sign, float Plane, Vector2 A, Vector2 B, bool Circle);

/// <summary>A drawn face in the list: kept after building up or cutting, so that it can be moved and used again.</summary>
public sealed partial class SketchItem : ObservableObject
{
    private readonly ModelViewModel _owner;
    private DrawnFace _data;

    internal SketchItem(ModelViewModel owner, string name, DrawnFace data)
    {
        _owner = owner;
        Name = name;
        _data = data;
        DeleteCommand = new RelayCommand(() => _owner.RemoveSketch(this));
    }

    public string Name { get; }

    internal DrawnFace Data
    {
        get => _data;
        set
        {
            _data = value;
            OnPropertyChanged(nameof(ShapeText));
        }
    }

    /// <summary>Height of a build-up or depth of a cut (shown unit).</summary>
    internal decimal Depth { get; set; } = 2;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>"Rechteck 20 × 8 mm" or "Kreis ⌀ 3 mm".</summary>
    public string ShapeText
    {
        get
        {
            var f = _owner.UnitFactor;
            if (_data.Circle) return Tr.T("Kreis ⌀ {0} {1}", $"{2 * Vector2.Distance(_data.A, _data.B) / f:0.##}", _owner.UnitLabel);
            return Tr.T("Rechteck {0} × {1} {2}", $"{MathF.Abs(_data.B.X - _data.A.X) / f:0.##}", $"{MathF.Abs(_data.B.Y - _data.A.Y) / f:0.##}", _owner.UnitLabel);
        }
    }

    public IRelayCommand DeleteCommand { get; }
}
