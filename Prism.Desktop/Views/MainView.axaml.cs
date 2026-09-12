using Avalonia.Controls;
using Prism.Desktop.ViewModels;

namespace Prism.Desktop.Views;

public partial class MainView : UserControl
{
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
    }

    // 页面切换动画由 XAML 里的 ViewTransitionHost 统一处理：
    // 它按 IsVisible 变化驱动，覆盖全部页面（含后加的 Pak 转换页），
    // 并支持通过 IsAnimationsEnabled 关闭（低配设备）。
    // 这里不再用代码后置的 Transitions —— 两套机制同时作用于同一元素会互相覆盖。
}
