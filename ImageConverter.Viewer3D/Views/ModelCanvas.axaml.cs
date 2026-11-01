using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ImageConverter.Viewer3D.Views;

/// <summary>Picture area for a model: the 3D view with its dimension lines and the tool bar below.</summary>
public partial class ModelCanvas : UserControl
{
    private readonly ModelViewModel? _model;

    /// <summary>For the designer only.</summary>
    public ModelCanvas() => InitializeComponent();

    public ModelCanvas(ModelViewModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
        View = new ModelGlView(model);
        var overlay = new MeasureOverlay(model, View);
        View.LabelAt = overlay.LabelAt;
        var stage = this.FindControl<Panel>("Stage")!;
        var grips = new SketchOverlay(model, View);
        View.GripAt = grips.GripAt;
        stage.Children.Insert(0, grips);
        stage.Children.Insert(0, overlay);
        stage.Children.Insert(0, View);
        View.Failed += () =>
        {
            this.FindControl<Border>("NoGl")!.IsVisible = true;
            this.FindControl<TextBlock>("NoGlDetail")!.Text = View.Failure;
        };
    }

    /// <summary>The 3D view (for checks of the drawing).</summary>
    internal ModelGlView? View { get; }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelViewModel.HoverText) or nameof(ModelViewModel.MeasureHint) or nameof(ModelViewModel.MeasureTool)
            or nameof(ModelViewModel.HoverFace) or nameof(ModelViewModel.Pending) or nameof(ModelViewModel.SectionHint))
            UpdateStatus();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => UpdateStatus();

    /// <summary>While measuring: what lies under the mouse, else how to go on.</summary>
    private void UpdateStatus()
    {
        if (_model is null || this.FindControl<TextBlock>("Status") is not { } status) return;
        status.Text = _model.SectionHint.Length > 0 ? _model.SectionHint
            : _model.MeasureTool && _model.HoverText.Length > 0 ? _model.HoverText
            : _model.MeasureTool ? _model.MeasureHint : "";
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_model is not null) _model.PropertyChanged += OnModelPropertyChanged;
        Tr.Instance.PropertyChanged += OnLanguageChanged;
        UpdateStatus();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_model is not null) _model.PropertyChanged -= OnModelPropertyChanged;
        Tr.Instance.PropertyChanged -= OnLanguageChanged;
    }
}
