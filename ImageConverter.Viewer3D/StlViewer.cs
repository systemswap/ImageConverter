using Avalonia.Controls;
using ImageConverter.Documents;
using ImageConverter.Viewer3D.Views;

namespace ImageConverter.Viewer3D;

/// <summary>Registers STL models with the main window (<see cref="DocumentViewers.Register"/>).</summary>
public sealed class StlViewer : IDocumentViewer
{
    public IReadOnlyCollection<string> Extensions { get; } = [".stl"];

    public string FileTypeName => Tr.T("3D-Modelle (STL)");

    public async Task<IOpenDocument> OpenAsync(string path, CancellationToken ct)
    {
        var model = await ModelViewModel.OpenAsync(path, ct);
        return new StlDocument(model);
    }
}

/// <summary>An open STL model in the main window.</summary>
internal sealed class StlDocument : IOpenDocument
{
    private readonly ModelViewModel _model;

    public StlDocument(ModelViewModel model)
    {
        _model = model;
        Details = model.Details;
        View = new ModelCanvas(model);
        Panel = new ModelPanel { DataContext = model };
    }

    public string Details { get; }

    public Control View { get; }

    public Control Panel { get; }

    public void Dispose() => _model.Dispose();
}
