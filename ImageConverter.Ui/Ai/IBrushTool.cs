using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Input;
using ImageConverter.Core.Ai;

namespace ImageConverter.Ai;

/// <summary>
/// A tab that paints in the preview ("Entfernen", "Hintergrund"): the preview binds to the one whose tab is selected
/// (<see cref="ViewModels.MainViewModel.ActiveBrush"/>).
/// </summary>
public interface IBrushTool : INotifyPropertyChanged
{
    /// <summary>Gets whether the mouse paints in the preview; setting it does nothing (the tab decides).</summary>
    bool IsBrushing { get; set; }

    /// <summary>Gets or sets the brush diameter in screen pixels.</summary>
    double BrushSize { get; set; }

    /// <summary>Gets the strokes painted so far, in the displayed image.</summary>
    IReadOnlyList<BrushStroke> Strokes { get; }

    /// <summary>Gets whether the strokes have been applied and are hidden until the next stroke.</summary>
    bool StrokesApplied { get; }

    /// <summary>Gets whether new strokes keep (green) instead of remove (red).</summary>
    bool BrushKeep { get; }

    /// <summary>Gets the command that receives a finished stroke.</summary>
    ICommand AddStrokeCommand { get; }
}
