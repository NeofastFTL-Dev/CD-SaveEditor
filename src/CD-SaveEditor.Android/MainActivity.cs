using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace CDSaveEditor.Android;

[Activity(
    Label = "CD-SaveEditor",
    Theme = "@android:style/Theme.Material.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density
        | ConfigChanges.KeyboardHidden)]
public class MainActivity : AvaloniaMainActivity<AndroidApp>
{
}
