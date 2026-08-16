using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Prism.Desktop.ViewModels;

namespace Prism.Desktop.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _vm;

    public MainView()
    {
        InitializeComponent();
        // 桌面由 MainWindow.Opened 注入 TopLevel；Android（MainView 生命周期）在此注入
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.TopLevel ??= TopLevel.GetTopLevel(this);
                if (OperatingSystem.IsAndroid())
                {
                    _ = vm.RestoreAndroidExportFolderAsync();
                }
            }
        };
        SizeChanged += (_, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.WindowWidth = e.NewSize.Width;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
            }

            _vm = DataContext as MainViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                AnimateViewIn(HomeViewControl);
            }
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null || e.PropertyName != nameof(MainViewModel.CurrentView))
        {
            return;
        }

        // 参照 Web 版 .view.slide-in：60px 位移 + 透明度，0.28s
        AnimateViewIn(_vm.CurrentView switch
        {
            "Workspace" => WorkspaceViewControl,
            "Merge" => MergeViewControl,
            "Settings" => SettingsViewControl,
            _ => HomeViewControl,
        });
    }

    private void AnimateViewIn(Control view)
    {
        var transition = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(280),
                Easing = new CubicEaseOut(),
            },
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(280),
                Easing = new CubicEaseOut(),
            },
        };

        view.RenderTransform = new TranslateTransform(60, 0);
        view.Opacity = 0;
        view.Transitions = transition;
        view.Opacity = 1;
        ((TranslateTransform)view.RenderTransform).X = 0;
    }
}
