using Avalonia.Controls;
using Prism.Desktop.ViewModels;

namespace Prism.Desktop.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// 用户已经在"有未保存改动"的确认里点了同意。
    ///
    /// 关窗口时必须**同步**决定拦不拦：<see cref="Window.Closing"/> 的处理器一旦 await，
    /// 事件早就按未取消走完了。所以这里先取消这一次关闭，问完再关第二次。
    /// </summary>
    private bool _closeConfirmed;

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
        Closing += (_, e) =>
        {
            if (DataContext is not MainViewModel vm)
            {
                return;
            }

            vm.WindowWidth = Width;
            vm.WindowHeight = Height;

            if (_closeConfirmed || !vm.HasUnsavedProjectChanges)
            {
                vm.SaveWindowState();
                return;
            }

            // 有没保存的工程改动：拦下这次关闭，问清楚再决定。
            e.Cancel = true;
            _ = ConfirmThenCloseAsync(vm);
        };
    }

    private async Task ConfirmThenCloseAsync(MainViewModel vm)
    {
        try
        {
            if (!await vm.ConfirmCloseAsync())
            {
                return;
            }
        }
        catch
        {
            // 弹窗本身失败不该把窗口锁死
        }

        _closeConfirmed = true;
        Close();
    }
}
