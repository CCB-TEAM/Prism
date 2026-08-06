using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace Prism.Desktop.Android;

[Activity(
    Label = "Prism",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>
    /// 返回键按页面层级返回：子目录→上级目录，其他页面→主页，主页才退出。
    /// </summary>
    public override void OnBackPressed()
    {
        if (global::Avalonia.Application.Current is Prism.Desktop.App app
            && app.MainVm?.HandleBack() == true)
        {
            return;
        }

        base.OnBackPressed();
    }
}
