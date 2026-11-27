using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CDSaveEditor.Gui;

/// <summary>Starts the desktop window (and, for a smoke test, renders it once and exits).</summary>
public static class GuiHost
{
    internal static string? Image;
    internal static string? Iso;
    internal static string? Screenshot;

    /// <summary>When set, the window shows the phone layout and writes a screenshot there.</summary>
    internal static string? MobileScreenshot;

    public static int Run(string? image = null, string? iso = null, string? screenshot = null,
        string? mobileScreenshot = null)
    {
        Image = image;
        Iso = iso;
        Screenshot = screenshot;
        MobileScreenshot = mobileScreenshot;
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(Array.Empty<string>());
    }

    internal static void CaptureAndClose(Window window, string path)
    {
        try
        {
            var width = Math.Max(1, (int)window.Bounds.Width);
            var height = Math.Max(1, (int)window.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(path);
            Console.WriteLine($"window rendered to {path}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("screenshot failed: " + exception.Message);
        }
        finally
        {
            Dispatcher.UIThread.Post(() => window.Close());
        }
    }
}
