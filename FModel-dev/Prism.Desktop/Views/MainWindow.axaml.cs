using Avalonia.Controls;
using Prism.Desktop.ViewModels;

namespace Prism.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.TopLevel = this;
                // 应用持久化的窗口大小（随后 SizeChanged 会同步 WindowWidth）
                if (vm.WindowWidth > 0)
                {
                    Width = vm.WindowWidth;
                }

                if (vm.WindowHeight > 0)
                {
                    Height = vm.WindowHeight;
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
        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.WindowWidth = Width;
                vm.WindowHeight = Height;
                vm.SaveWindowState();
            }
        };
    }
}
