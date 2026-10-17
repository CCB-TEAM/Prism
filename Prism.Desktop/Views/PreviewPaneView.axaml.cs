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

    /// <summary>
    /// 拖动进度条期间暂停进度回写，松手后再 seek，避免滑块来回跳动。
    ///
    /// 把事件标记为已处理是必须的：预览栏整体在 ScrollViewer 里，滚动容器的手势识别器
    /// 会在指针按下后接管拖动（触摸屏 / 触摸板尤其明显），于是滑块根本拖不动。
    /// 滑块自己该做的处理早已完成，这里只是不让事件继续往上冒。
    /// </summary>
    private void OnAudioSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsAudioSeeking = true;
        }

        e.Handled = true;
    }

    private void OnAudioSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        CommitAudioSeek();
    }

    /// <summary>指针捕获丢了也要收尾，否则 IsAudioSeeking 会一直为真、进度条再也不更新。</summary>
    private void OnAudioSliderCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CommitAudioSeek();

    private void CommitAudioSeek()
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsAudioSeeking = false;
            vm.SeekAudioCommand.Execute(null);
        }
    }

    /// <summary>拖动视频进度条期间不回写位置，松手后再 seek（否则每帧都会被拉回去）。</summary>
    private void OnVideoSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsVideoSeeking = true;
        }

        e.Handled = true;
    }

    private void OnVideoSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        CommitVideoSeek();
    }

    private void OnVideoSliderCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CommitVideoSeek();

    private void CommitVideoSeek()
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsVideoSeeking = false;
            vm.SeekVideoCommand.Execute(null);
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
