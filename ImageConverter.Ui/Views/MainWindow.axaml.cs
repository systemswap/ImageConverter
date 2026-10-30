using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using ImageConverter.ViewModels;

namespace ImageConverter.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The version comes from Directory.Build.props, e.g. "ImageConverter 0.4.1", including a suffix
        // ("0.4.41-beta" → "0.4.41 beta"); the part after "+" is the commit and is dropped.
        var info = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        var v = typeof(MainWindow).Assembly.GetName().Version;
        var text = info is not null ? info.Split('+')[0].Replace('-', ' ')
            : v is not null ? $"{v.Major}.{v.Minor}.{v.Build}" : null;
        if (text is not null)
            Title = $"ImageConverter {text}";

        // The theme symbol follows the system while the app follows it
        if (Avalonia.Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) => (DataContext as MainViewModel)?.SystemThemeChanged();
    }

    /// <summary>
    /// Viewer keys, after the controls had their turn (a focused slider, text box or list keeps its arrow keys): left/right
    /// browse unless a crop frame or a geometry tool uses the arrows; Page Up/Down always browse; Home/End go to the first and
    /// last picture; F11 switches full screen, Esc leaves it.
    /// </summary>
    private bool ViewerKey(KeyEventArgs e, MainViewModel vm)
    {
        var arrowsFree = vm.CropSelection is null && !vm.RotationTool && !vm.PerspectiveTool && !vm.DewarpTool;
        ICommand? command = e.Key switch
        {
            Key.Left when arrowsFree => vm.PreviousFileCommand,
            Key.Right when arrowsFree => vm.NextFileCommand,
            Key.PageUp => vm.PreviousFileCommand,
            Key.PageDown => vm.NextFileCommand,
            Key.Home => vm.FirstFileCommand,
            Key.End => vm.LastFileCommand,
            _ => null,
        };
        if (command is not null)
        {
            if (command.CanExecute(null)) command.Execute(null);
            return true;
        }
        if (e.Key == Key.F11 || e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            var full = e.Key == Key.F11 && WindowState != WindowState.FullScreen;
            WindowState = full ? WindowState.FullScreen : WindowState.Normal;
            vm.IsFullScreen = full;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Window-wide undo/redo (and Ctrl+P for printing). Deliberately handled in OnKeyDown instead of
    /// KeyBindings: the labelled key is evaluated (important for QWERTZ layouts with Y and Z swapped),
    /// and text boxes that already handled their own Ctrl+Z never reach this point.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainViewModel vm) return;
        if (e.KeyModifiers == KeyModifiers.None && ViewerKey(e, vm))
        {
            e.Handled = true;
            return;
        }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        ICommand? command = e.Key switch
        {
            Key.Z when !shift => vm.UndoCommand,
            Key.Z when shift => vm.RedoCommand,
            Key.Y => vm.RedoCommand,
            Key.P => vm.PrintCommand,
            _ => null,
        };

        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
