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
        // Tooltips above the element instead of at the pointer: at the pointer they covered sliders
        // and buttons, and clicks landed on the tooltip instead of the element. Applied to all
        // controls at once, without a style that would have to be evaluated for every element.
        // Control itself already has a default that Avalonia does not allow to be overridden, so
        // the default is overridden for the types that carry tooltips: all interactive controls
        // (TemplatedControl), text blocks, borders, panels and images.
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
        ToolTip.VerticalOffsetProperty.OverrideDefaultValue<T>(-6);   // small gap upwards
    }

    // Currently open tooltip and whether a mouse button is pressed (UI thread only).
    private static Control? _openTip;
    private static bool _buttonDown;

    /// <summary>
    /// Shows tooltips only while no mouse button is pressed, and only one at a time; otherwise dragging a slider
    /// across the next one left both tooltips open. Pressing a mouse button closes the open tooltip, none opens while
    /// a button is held, and opening a new one closes the previous one. Applies to all windows at once.
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
        // Desktop only; there are no single-view lifetimes (Android/iOS/browser).
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Language before the first window: the saved choice, otherwise the system language.
            LanguageViewModel.ApplyAtStartup();

            var vm = new MainViewModel { CoarseWhileDragging = true };   // coarse preview while dragging, full quality after release
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // Keep the computer responsive: CPU limit and low priority (Settings › Performance), applied before any computation
            var settings = Core.Platform.AppSettings.Load();
            Core.Platform.ComputeLimits.SetPercent(settings.CpuPercent);
            if (settings.LowPriority) Core.Platform.ComputeLimits.LowerPriority();

            // Prepare the GPU for denoising and dehazing in the background (compile the kernels); the last
            // speed test decides whether the GPU or the CPU computes.
            Core.Imaging.GpuFilters.Measured = settings.FilterSpeed;
            _ = Core.Imaging.GpuFilters.WarmUpAsync();

            // Load files passed via "Open with" or the .desktop line (Exec=… %F) right away.
            if (desktop.Args is { Length: > 0 } args)
                _ = vm.OpenAsync(args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
