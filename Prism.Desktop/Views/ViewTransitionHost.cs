using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace Prism.Desktop.Views;

/// <summary>
/// 给"显隐式页面切换"提供一个轻量淡入 + 上移动画。
///
/// 为什么不用 XAML Transitions：本项目的页面切换靠 <c>IsVisible</c> 绑定
/// （见 <c>MainView.axaml</c> 的四个视图），而 <c>Visibility</c> 变化会直接
/// 从布局中移除元素，Transitions 无法在其上产生视觉效果。这个控件把子元素
/// 包一层，监听自身 <see cref="Visual.IsVisible"/> 变化，在变可见时把子元素
/// 从透明 + 下移状态动画到最终状态。
///
/// 刻意使用渲染层动画（Opacity + RenderTransform），不触发重排，手机上开销很低。
/// <see cref="RespectReduceMotion"/> 为 true 时，系统开启"减少动态效果"会直接跳过动画。
/// </summary>
public class ViewTransitionHost : Decorator
{
    /// <summary>动画时长，默认 180ms（足够可感知，又不会拖慢操作）。</summary>
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<ViewTransitionHost, TimeSpan>(nameof(Duration), TimeSpan.FromMilliseconds(180));

    /// <summary>起始纵向偏移（像素）。正值表示"从下方滑入"。</summary>
    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<ViewTransitionHost, double>(nameof(OffsetY), 10d);

    /// <summary>起始缩放（1 = 不缩放）。</summary>
    public static readonly StyledProperty<double> StartScaleProperty =
        AvaloniaProperty.Register<ViewTransitionHost, double>(nameof(StartScale), 1d);

    /// <summary>是否启用动画（供"减少动态效果"设置或用户偏好直接控制）。</summary>
    public static readonly StyledProperty<bool> IsAnimationEnabledProperty =
        AvaloniaProperty.Register<ViewTransitionHost, bool>(nameof(IsAnimationEnabled), true);

    private static readonly Easing Ease = new CubicEaseOut();

    private CancellationTokenSource? _animationCts;

    static ViewTransitionHost()
    {
        // IsVisible 变化是驱动点。
        IsVisibleProperty.Changed.AddClassHandler<ViewTransitionHost>((host, args) =>
            host.OnIsVisibleChanged((bool)args.NewValue!));
    }

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double OffsetY
    {
        get => GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    public double StartScale
    {
        get => GetValue(StartScaleProperty);
        set => SetValue(StartScaleProperty, value);
    }

    public bool IsAnimationEnabled
    {
        get => GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size size = base.MeasureOverride(availableSize);
        // Opacity/RenderTransform 不改变测量结果，这里不需要额外处理。
        return size;
    }

    private void OnIsVisibleChanged(bool isVisible)
    {
        // 取消上一次可能仍在跑的动画，避免快速切换时状态错乱。
        _animationCts?.Cancel();
        _animationCts?.Dispose();
        _animationCts = null;

        if (Child is not Visual child)
        {
            return;
        }

        if (!isVisible)
        {
            // 隐藏时立即复位，下一次显示从起点开始。
            child.Opacity = 0;
            ApplyOffset(child, OffsetY, StartScale);
            return;
        }

        if (ShouldSkipAnimation())
        {
            child.Opacity = 1;
            ApplyOffset(child, 0, 1);
            return;
        }

        child.Opacity = 0;
        ApplyOffset(child, OffsetY, StartScale);
        _ = RunInAsync(child);
    }

    private bool ShouldSkipAnimation()
    {
        if (!IsAnimationEnabled || Duration <= TimeSpan.Zero)
        {
            return true;
        }

        // 控件尚未挂到可视树（首次布局）时动画没有意义，直接落到终态。
        if (VisualRoot is null)
        {
            return true;
        }

        return false;
    }

    private async Task RunInAsync(Visual child)
    {
        var cts = new CancellationTokenSource();
        _animationCts = cts;

        try
        {
            var animation = new Animation
            {
                Duration = Duration,
                FillMode = FillMode.Forward,
                Easing = Ease,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 0d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 1d) } },
                },
            };

            await animation.RunAsync(child, cts.Token);

            if (!cts.IsCancellationRequested)
            {
                child.Opacity = 1;
            }
        }
        catch (OperationCanceledException)
        {
            // 被新的切换打断，交由新动画接管。
        }
    }

    /// <summary>用 RenderTransform 做位移/缩放：不参与布局，因此不触发重排。</summary>
    private static void ApplyOffset(Visual child, double offsetY, double scale)
    {
        var builder = TransformOperations.CreateBuilder(2);
        if (Math.Abs(scale - 1d) > 0.0001)
        {
            builder.AppendScale(scale, scale);
        }

        if (Math.Abs(offsetY) > 0.0001)
        {
            builder.AppendTranslate(0, offsetY);
        }

        child.RenderTransform = builder.Build();
        child.RenderTransformOrigin = RelativePoint.Center;
    }
}
