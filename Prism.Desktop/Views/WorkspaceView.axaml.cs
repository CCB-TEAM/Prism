using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Prism.Desktop.ViewModels;

namespace Prism.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    private MainViewModel? _vm;
    private Transitions? _drawerTransitions;
    private bool _isDragging;
    private double _dragStartY;
    private double _dragStartHeight;
    private double _customDrawerHeight; // 0 = 未初始化

    /// <summary>拖拽上限：参考 Web 版 parentHeight - 8。</summary>
    private double MaxDrawerHeight => Math.Max(200, PortraitLayout.Bounds.Height - 8);

    /// <summary>搜索框回车触发搜索（按钮可能被遮挡时的兜底）。</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm)
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>本地化文本失焦写回。</summary>
    private void OnLocresTextLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedPatchItem is { IsLocres: true } patch)
        {
            vm.UpdateLocresEntryCommand.Execute(patch);
        }
    }

    /// <summary>本地化文本回车写回。</summary>
    private void OnLocresTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm && vm.SelectedPatchItem is { IsLocres: true } patch)
        {
            vm.UpdateLocresEntryCommand.Execute(patch);
            e.Handled = true;
        }
    }

    public WorkspaceView()
    {
        InitializeComponent();
        _drawerTransitions = PortraitDrawer.Transitions;
        // 双保险：布局容器自身宽度变化时也刷新响应式状态
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
                ApplyLayout(_vm);
            }
        };
    }

    // ============ 抽屉拖拽 ============

    private void OnDrawerHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        _isDragging = true;
        _dragStartY = e.GetPosition(this).Y;
        _dragStartHeight = PortraitDrawer.MaxHeight;
        // 拖拽期间禁用过渡动画，避免跟手延迟
        PortraitDrawer.Transitions = null;
        e.Pointer.Capture(DrawerHandle);
        e.Handled = true;
    }

    private void OnDrawerHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        double dy = e.GetPosition(this).Y - _dragStartY;
        _customDrawerHeight = Math.Clamp(_dragStartHeight - dy, 44, MaxDrawerHeight);
        PortraitDrawer.MaxHeight = _customDrawerHeight;
        e.Handled = true;
    }

    private void OnDrawerHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        e.Pointer.Capture(null);
        // 恢复过渡动画
        PortraitDrawer.Transitions = _drawerTransitions;
        if (_vm is null)
        {
            return;
        }

        // 悬停：松手保持当前高度；拖到接近标题栏高度则收起
        if (_customDrawerHeight < 60)
        {
            _vm.IsPreviewExpanded = false;
        }
        else
        {
            _vm.IsPreviewExpanded = true;
        }

        e.Handled = true;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsLandscape):
            case nameof(MainViewModel.WindowWidth):
                ApplyLayout(_vm);
                break;
            case nameof(MainViewModel.HasPreview):
                PortraitDrawer.IsVisible = _vm.HasPreview;
                UpdateDrawerHeight();
                break;
            case nameof(MainViewModel.IsPreviewExpanded):
                UpdateDrawerHeight();
                break;
        }
    }

    /// <summary>直接以代码控制布局切换（不依赖 XAML 绑定，避免绑定未刷新问题）。</summary>
    private void ApplyLayout(MainViewModel vm)
    {
        LandscapeLayout.IsVisible = vm.IsLandscape;
        PortraitLayout.IsVisible = !vm.IsLandscape;
        PatchLandscapeLayout.IsVisible = vm.IsLandscape;
        PatchPortraitLayout.IsVisible = !vm.IsLandscape;
        PortraitDrawer.IsVisible = vm.HasPreview;
        UpdateDrawerHeight();
    }

    /// <summary>抽屉高度：收起仅标题栏，展开为记忆的拖拽高度（0.35s 过渡动画）。</summary>
    private void UpdateDrawerHeight()
    {
        if (_vm is null)
        {
            return;
        }

        // 首次展开时参考 Web 版 open 高度：max(200px, 38vh)
        if (_customDrawerHeight <= 0)
        {
            _customDrawerHeight = Math.Clamp(PortraitLayout.Bounds.Height * 0.38, 200, MaxDrawerHeight);
        }

        PortraitDrawer.MaxHeight = !_vm.HasPreview ? 0 : (_vm.IsPreviewExpanded ? _customDrawerHeight : 44);
    }
}
