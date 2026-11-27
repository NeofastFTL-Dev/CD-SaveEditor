using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace CDSaveEditor.Gui;

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(GuiHost.Image, GuiHost.Iso);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Avalonia's portal DBus connection can throw from its dispose handler while
            // the dispatcher is already gone, which aborts the process on exit. Nothing
            // is buffered at close time, so leave cleanly instead.
            window.Closed += (_, _) =>
            {
                if (OperatingSystem.IsLinux())
                {
                    Environment.Exit(0);
                }
            };
            if (GuiHost.Screenshot is { } path)
            {
                // Capture after the first load has been rendered, so the screenshot shows
                // the slots and flags instead of the empty window.
                window.Ready += () => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => GuiHost.CaptureAndClose(window, path),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
