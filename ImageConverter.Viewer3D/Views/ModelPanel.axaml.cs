using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ImageConverter.Viewer3D.Views;

/// <summary>Column beside the model: its figures, how it is drawn, and the measurements.</summary>
public partial class ModelPanel : UserControl
{
    public ModelPanel() => AvaloniaXamlLoader.Load(this);
}
