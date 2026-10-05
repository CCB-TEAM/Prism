using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Prism.Desktop.ViewModels;

namespace Prism.Desktop.Views;

public partial class FileBrowserView : UserControl
{
    public FileBrowserView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 单击激活（与 Web 版一致，兼容手机触摸）：目录进入，文件预览。
    /// </summary>
    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedItem is not null)
        {
            vm.ActivateCommand.Execute(null);
        }
    }

    /// <summary>长按路径复制到剪贴板。</summary>
    private void OnPathHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started || DataContext is not MainViewModel vm)
        {
            return;
        }

        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(vm.CurrentPathText) && sender is Control anchor)
        {
            _ = CopyTextAsync(anchor, vm.CurrentPathText, "路径已复制", vm);
        }
    }

    private static async Task CopyTextAsync(Control anchor, string text, string message, MainViewModel vm)
    {
        try
        {
            IClipboard? clipboard = TopLevel.GetTopLevel(anchor)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
                vm.StatusText = message;
            }
        }
        catch
        {
            // 剪贴板不可用时忽略。
        }
    }
}
