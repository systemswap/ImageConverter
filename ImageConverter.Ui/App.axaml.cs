using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ImageConverter.ViewModels;
using ImageConverter.Views;

namespace ImageConverter;

public partial class App : Application
{
    public override void Initialize()
    {
        // Tooltips über dem Element statt am Mauszeiger: Dort lagen sie auf Reglern und
        // Knöpfen, man klickte ständig auf den Tooltip statt aufs Element. Für alle
        // Steuerelemente auf einmal, ohne Stil, der jedes Element prüfen müsste. Für Control
        // selbst ist die Vorgabe schon gesetzt (Avalonia lässt sie dort nicht überschreiben) —
        // darum für die Arten, die Tooltips tragen: alle Bedienelemente (TemplatedControl),
        // Texte, Rahmen und Flächen.
        TooltipAbove<TemplatedControl>();
        TooltipAbove<TextBlock>();
        TooltipAbove<Border>();
        TooltipAbove<Panel>();
        TooltipAbove<Image>();
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    private static void TooltipAbove<T>() where T : Control
    {
        ToolTip.PlacementProperty.OverrideDefaultValue<T>(PlacementMode.Top);
        ToolTip.VerticalOffsetProperty.OverrideDefaultValue<T>(-6);   // kleiner Abstand nach oben
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Desktop-only: die Single-View-Lifetimes (Android/iOS/Browser) sind entfallen.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Sprache vor dem ersten Fenster: gespeicherte Wahl, sonst Systemsprache
            LanguageViewModel.ApplyAtStartup();

            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Dateien aus "Öffnen mit" bzw. der .desktop-Zeile (Exec=… %F) direkt laden
            if (desktop.Args is { Length: > 0 } args)
                _ = vm.OpenAsync(args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
