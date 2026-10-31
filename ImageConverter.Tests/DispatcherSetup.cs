using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace ImageConverter.Tests;

/// <summary>
/// Creates Avalonia's dispatcher once, before any test runs. The dispatcher's constructor makes itself the UI-thread
/// dispatcher before it sets its implementation; when two tests built their first view model at the same moment, the
/// second could post to the half-built dispatcher and fail with a NullReferenceException in
/// <c>Dispatcher.RequestForegroundProcessing</c> – about once in a dozen full test runs (error memory #122). The
/// program itself creates it on the UI thread at start, before other threads exist.
/// </summary>
internal static class DispatcherSetup
{
    [ModuleInitializer]
    internal static void CreateDispatcher() => _ = Dispatcher.UIThread;
}
