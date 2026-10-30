using Avalonia;
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

    // ══════════════════════════════════════════════════════════════════
    //  控件预览：两条拖拽条（画布高度 / 控件树宽度）
    //
    //  为什么自己写而不是 GridSplitter：这个预览栏整体在 ScrollViewer 里，
    //  GridSplitter 在这里既不跟随指针也不稳定复现；下面这套按下-捕获-移动
    //  的写法与手机端预览抽屉用的是同一套逻辑，行为可控。
    // ══════════════════════════════════════════════════════════════════

    private bool _draggingWidgetCanvas;
    private bool _draggingWidgetColumns;
    private double _dragStartY;
    private double _dragStartX;
    private double _dragStartCanvasHeight;
    private double _dragStartTreeWidth;

    private const double MinCanvasHeight = 140;
    private const double MaxCanvasHeight = 2400;
    private const double MinTreeWidth = 120;
    private const double MinPropertyWidth = 160;

    private void OnWidgetCanvasHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle)
        {
            return;
        }

        _draggingWidgetCanvas = true;
        _dragStartY = e.GetPosition(this).Y;
        _dragStartCanvasHeight = WidgetPanelGrid.RowDefinitions[1].ActualHeight;
        if (_dragStartCanvasHeight <= 1)
        {
            _dragStartCanvasHeight = WidgetPanelGrid.RowDefinitions[1].Height.Value;
        }

        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnWidgetCanvasHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingWidgetCanvas)
        {
            return;
        }

        var height = Math.Clamp(_dragStartCanvasHeight + (e.GetPosition(this).Y - _dragStartY), MinCanvasHeight, MaxCanvasHeight);
        WidgetPanelGrid.RowDefinitions[1].Height = new GridLength(height);
        e.Handled = true;
    }

    private void OnWidgetCanvasHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingWidgetCanvas = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnWidgetColumnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle)
        {
            return;
        }

        _draggingWidgetColumns = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartTreeWidth = WidgetDetailsGrid.ColumnDefinitions[0].ActualWidth;
        if (_dragStartTreeWidth <= 1)
        {
            _dragStartTreeWidth = WidgetDetailsGrid.ColumnDefinitions[0].Width.Value;
        }

        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnWidgetColumnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingWidgetColumns)
        {
            return;
        }

        var available = Math.Max(0, WidgetDetailsGrid.Bounds.Width - 10);
        var maxTreeWidth = Math.Max(MinTreeWidth, available - MinPropertyWidth);
        var width = Math.Clamp(_dragStartTreeWidth + (e.GetPosition(this).X - _dragStartX), MinTreeWidth, maxTreeWidth);
        WidgetDetailsGrid.ColumnDefinitions[0].Width = new GridLength(width);
        e.Handled = true;
    }

    private void OnWidgetColumnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingWidgetColumns = false;
        e.Pointer.Capture(null);
        e.Handled = true;
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
