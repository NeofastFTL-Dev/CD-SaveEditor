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
            // The Android screen is an Avalonia view, so the same layout can be opened
            // in a phone-sized desktop window to check it without a device.
            if (GuiHost.MobileScreenshot is { } mobilePath)
            {
                var preview = new Window
                {
                    Title = "CD-SaveEditor (phone layout)",
                    Width = 412,
                    Height = 900,
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#14161a")),
                    Content = new CDSaveEditor.Mobile.MobileView(GuiHost.Image, GuiHost.Iso),
                };
                desktop.MainWindow = preview;
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                preview.Closed += (_, _) =>
                {
                    if (OperatingSystem.IsLinux())
                    {
                        Environment.Exit(0);
                    }
                };
                ((CDSaveEditor.Mobile.MobileView)preview.Content).Ready += () =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                    {
                        // let the wrapping panels settle before the capture
                        await System.Threading.Tasks.Task.Delay(700);
                        GuiHost.CaptureAndClose(preview, mobilePath);
                    }, Avalonia.Threading.DispatcherPriority.Background);
                base.OnFrameworkInitializationCompleted();
                return;
            }

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
