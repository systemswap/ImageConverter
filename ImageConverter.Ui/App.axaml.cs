using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        TooltipsOnlyWhenIdle();
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

    // Tooltip, der gerade offen ist, und ob eine Maustaste gedrückt ist (nur UI-Thread)
    private static Control? _openTip;
    private static bool _buttonDown;

    /// <summary>
    /// Tooltips nur, wenn keine Maustaste gedrückt ist, und immer nur einer: Wer einen Regler mit gedrückter Taste zog und
    /// dabei über den nächsten rutschte, hatte beide Tooltips offen. Ein Druck auf eine Maustaste schließt den offenen,
    /// solange sie gedrückt ist, geht keiner auf; öffnet ein neuer, schließt der alte. Für alle Fenster auf einmal.
    /// </summary>
    private static void TooltipsOnlyWhenIdle()
    {
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, _) =>
        {
            _buttonDown = true;
            CloseOpenTip();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>((top, e) =>
        {
            var p = e.GetCurrentPoint(top).Properties;
            _buttonDown = p.IsLeftButtonPressed || p.IsRightButtonPressed || p.IsMiddleButtonPressed;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, e) =>
        {
            if (_buttonDown)
            {
                e.Cancel = true;
                return;
            }
            if (!ReferenceEquals(_openTip, control)) CloseOpenTip();
            _openTip = control;
        }, handledEventsToo: true);
        ToolTip.ToolTipClosingEvent.AddClassHandler(typeof(Control), (sender, _) =>
        {
            if (ReferenceEquals(_openTip, sender)) _openTip = null;
        }, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void CloseOpenTip()
    {
        if (_openTip is not { } open) return;
        _openTip = null;
        ToolTip.SetIsOpen(open, false);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Desktop-only: die Single-View-Lifetimes (Android/iOS/Browser) sind entfallen.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Sprache vor dem ersten Fenster: gespeicherte Wahl, sonst Systemsprache
            LanguageViewModel.ApplyAtStartup();

            var vm = new MainViewModel { CoarseWhileDragging = true };   // beim Ziehen grobe Vorschau, fein nach dem Loslassen
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // Grafikkarte für Entrauschen und Dunst im Hintergrund vorbereiten (Kernel übersetzen); letzter
            // Geschwindigkeitstest entscheidet, ob sie oder die CPU rechnet
            Core.Imaging.GpuFilters.Measured = Core.Platform.AppSettings.Load().FilterSpeed;
            _ = Core.Imaging.GpuFilters.WarmUpAsync();

            // Dateien aus "Öffnen mit" bzw. der .desktop-Zeile (Exec=… %F) direkt laden
            if (desktop.Args is { Length: > 0 } args)
                _ = vm.OpenAsync(args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
