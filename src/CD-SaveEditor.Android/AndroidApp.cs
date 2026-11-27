using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using CDSaveEditor.Mobile;

namespace CDSaveEditor.Android;

public sealed class AndroidApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());

        // Android gives the app no writable folder by its executable, so point the
        // shared code at the app's private files directory for baselines and copies.
        var data = Path.Combine(global::Android.App.Application.Context.FilesDir!.AbsolutePath, "cd-saveeditor");
        Directory.CreateDirectory(data);
        AppPaths.UseDataDir(data);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new MobileView();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
