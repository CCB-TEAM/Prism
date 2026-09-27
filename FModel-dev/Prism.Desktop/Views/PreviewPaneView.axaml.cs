using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Prism.Desktop.ViewModels;

namespace Prism.Desktop.Views;

public partial class PreviewPaneView : UserControl
{
    public PreviewPaneView()
    {
        InitializeComponent();
    }

    /// <summary>拖动进度条期间暂停进度回写，松手后再 seek，避免滑块来回跳动。</summary>
    private void OnAudioSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsAudioSeeking = true;
        }
    }

    private void OnAudioSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsAudioSeeking = false;
            vm.SeekAudioCommand.Execute(null);
        }
    }

    /// <summary>长按预览抽屉底部的资源路径复制到剪贴板。</summary>
    private void OnSelectedPathHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started || DataContext is not MainViewModel vm || sender is not Control anchor)
        {
            return;
        }

        e.Handled = true;
        if (string.IsNullOrWhiteSpace(vm.SelectedPathText))
        {
            return;
        }

        _ = CopySelectedPathAsync(anchor, vm);
    }

    private static async Task CopySelectedPathAsync(Control anchor, MainViewModel vm)
    {
        try
        {
            IClipboard? clipboard = TopLevel.GetTopLevel(anchor)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(vm.SelectedPathText);
                vm.StatusText = "路径已复制";
            }
        }
        catch
        {
            // 剪贴板不可用时忽略。
        }
    }
}
