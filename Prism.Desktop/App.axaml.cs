using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Prism.Desktop.ViewModels;
using Prism.Desktop.Views;

namespace Prism.Desktop;

public partial class App : Application
{
    /// <summary>供平台壳（Android MainActivity）访问返回键处理。</summary>
    public MainViewModel? MainVm { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var vm = new MainViewModel();
        MainVm = vm;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new MainView
            {
                DataContext = vm,
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView
            {
                DataContext = vm,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
