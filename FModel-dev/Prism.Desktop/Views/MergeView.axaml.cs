using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Prism.Desktop.Models;
using Prism.Desktop.ViewModels;

namespace Prism.Desktop.Views;

/// <summary>
/// 合并列表的拖动排序。
///
/// 用指针事件而不是 DragDrop：Android 上系统 DragDrop 与列表滚动手势容易互相干扰，
/// 指针事件在两端行为一致、可控。
///
/// 交互差异（跨平台必需）：
/// - 桌面 / 鼠标：按住把手立即开始拖动，跟手。
/// - Android / 触摸：先<b>长按</b>把手约 350 ms 激活拖动，避免与滚动冲突；
///   长按前若移动超过阈值则判定为用户在滚动，放弃拖动。
/// </summary>
public partial class MergeView : UserControl
{
    /// <summary>触摸下长按激活拖动的时间阈值。</summary>
    private static readonly TimeSpan TouchHoldDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>判定"用户意图是滚动"的位移阈值（像素）。</summary>
    private const double ScrollIntentThreshold = 12;

    private MergePakItem? _dragItem;
    private Control? _dragGrip;
    private bool _dragActive;
    private Point _pressPoint;
    private DateTime _pressTime;

    public MergeView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnMergeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip || grip.DataContext is not MergePakItem item || !item.CanDrag)
        {
            return;
        }

        // 触摸需要长按激活；鼠标 / 触控笔立即生效。
        bool isTouch = e.Pointer.Type == PointerType.Touch;

        _dragItem = item;
        _dragGrip = grip;
        _pressPoint = e.GetPosition(this);
        _pressTime = DateTime.UtcNow;
        _dragActive = !isTouch;

        e.Pointer.Capture(grip);

        if (_dragActive)
        {
            item.IsDragging = true;
        }

        // 不设置 e.Handled：长按的默认行为与滚动仍需要正常工作。
    }

    private void OnMergeGripMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || _dragGrip is null)
        {
            return;
        }

        Point current = e.GetPosition(this);

        if (!_dragActive)
        {
            double dx = Math.Abs(current.X - _pressPoint.X);
            double dy = Math.Abs(current.Y - _pressPoint.Y);

            // 长按未完成就明显移动 → 用户在滚动列表，放弃拖动。
            if (dx > ScrollIntentThreshold || dy > ScrollIntentThreshold)
            {
                CancelDrag(e.Pointer);
                return;
            }

            // 还没到长按时间，本次移动事件不处理（后续事件会再次进来）。
            if (DateTime.UtcNow - _pressTime < TouchHoldDelay)
            {
                return;
            }

            _dragActive = true;
            _dragItem.IsDragging = true;
        }

        MoveToPointer(current, e.Pointer);
        e.Handled = true;
    }

    private void OnMergeGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragItem is null)
        {
            return;
        }

        // 触摸时若长按尚未完成，这只是普通点击，不产生移动。
        EndDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnMergeGripCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ClearDragState();
    }

    /// <summary>按当前指针位置把被拖项移动到对应下标。</summary>
    private void MoveToPointer(Point pointerInView, IPointer pointer)
    {
        MainViewModel? vm = ViewModel;
        if (vm is null || _dragItem is null)
        {
            return;
        }

        int targetIndex = FindIndexAt(pointerInView, vm);
        if (targetIndex < 0)
        {
            return;
        }

        ClearDropTarget(vm);

        int currentIndex = vm.MergePaks.IndexOf(_dragItem);
        if (targetIndex == currentIndex)
        {
            // 已在目标位置，仅做落点高亮反馈。
            _dragItem.IsDropTarget = true;
            return;
        }

        vm.MoveMergePak(_dragItem, targetIndex);
    }

    /// <summary>找到指针所在行的列表下标；不在任何行上时返回 -1。</summary>
    private int FindIndexAt(Point pointerInView, MainViewModel vm)
    {
        foreach (Visual visual in this.GetVisualsAt(pointerInView))
        {
            Visual? node = visual;
            while (node is not null)
            {
                if (node is Border { DataContext: MergePakItem item })
                {
                    int index = vm.MergePaks.IndexOf(item);
                    if (index >= 0)
                    {
                        return index;
                    }
                }

                node = node.GetVisualParent();
            }
        }

        return -1;
    }

    private void EndDrag(IPointer pointer)
    {
        pointer.Capture(null);
        ClearDragState();
    }

    private void CancelDrag(IPointer pointer)
    {
        pointer.Capture(null);
        ClearDragState();
    }

    private void ClearDragState()
    {
        MainViewModel? vm = ViewModel;
        if (vm is not null)
        {
            ClearDropTarget(vm);
        }

        if (_dragItem is not null)
        {
            _dragItem.IsDragging = false;
            _dragItem.IsDropTarget = false;
        }

        _dragItem = null;
        _dragGrip = null;
        _dragActive = false;
    }

    private static void ClearDropTarget(MainViewModel vm)
    {
        foreach (MergePakItem item in vm.MergePaks)
        {
            item.IsDropTarget = false;
        }
    }
}
