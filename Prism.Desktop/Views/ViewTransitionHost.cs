using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Prism.Desktop.Views;

/// <summary>
/// 给"显隐式页面切换"提供淡入 + 轻微上移的过渡。
///
/// 为什么不用 XAML Transitions：本项目的页面切换靠 <c>IsVisible</c> 绑定
/// （见 <c>MainView.axaml</c>），而 <c>Visibility</c> 变化会直接从布局中移除元素，
/// Transitions 无法在其上产生视觉效果。这个控件把子元素包一层，监听自身
/// <see cref="Visual.IsVisible"/> 变化，在变可见时把子元素从透明 + 下移状态过渡到终态。
///
/// <b>为什么不是 Animation 关键帧</b>：曾试过用 Animation 同时动画 Opacity 与
/// RenderTransform（位移 + 缩放）。RenderTransform 的关键帧插值在运行期失败，
/// 整页停在 Opacity=0 上 —— 表现就是"点进某个页面后正文一片空白，只剩侧栏和页头"。
/// 现在改用 Avalonia 原生的 DoubleTransition / TransformOperationsTransition：
/// 先把起点写进去、让它在下一帧落地，再切换终态，由过渡系统负责插值。
/// 任何一步出错都只会退化成"直接出现"，不会把页面留在透明状态。
///
/// <see cref="IsAnimationEnabled"/> 为 false（用户关掉过渡动画）时直接落到终态。
/// </summary>
public class ViewTransitionHost : Decorator
{
    /// <summary>动画时长，默认 220ms（足够可感知，又不会拖慢操作）。</summary>
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<ViewTransitionHost, TimeSpan>(nameof(Duration), TimeSpan.FromMilliseconds(220));

    /// <summary>起始纵向偏移（像素）。正值表示"从下方滑入"。</summary>
    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<ViewTransitionHost, double>(nameof(OffsetY), 12d);

    /// <summary>起始缩放（1 = 不缩放）。</summary>
    public static readonly StyledProperty<double> StartScaleProperty =
        AvaloniaProperty.Register<ViewTransitionHost, double>(nameof(StartScale), 1d);

    /// <summary>是否启用动画（供"减少动态效果"设置或用户偏好直接控制）。</summary>
    public static readonly StyledProperty<bool> IsAnimationEnabledProperty =
        AvaloniaProperty.Register<ViewTransitionHost, bool>(nameof(IsAnimationEnabled), true);

    private static readonly Easing Ease = new CubicEaseOut();

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

    private void OnIsVisibleChanged(bool isVisible)
    {
        if (Child is not Visual child)
        {
            return;
        }

        if (!isVisible || ShouldSkipAnimation())
        {
            // 隐藏时立即复位（下一次显示从起点开始）；跳过动画时直接落到终态。
            child.Transitions = null;
            child.Opacity = isVisible ? 1 : 0;
            ApplyOffset(child, isVisible ? 0 : OffsetY, isVisible ? 1 : StartScale);
            return;
        }

        // 1) 关掉过渡、写入起点 —— 否则这一步本身也会被过渡，看起来像闪一下。
        child.Transitions = null;
        child.Opacity = 0;
        ApplyOffset(child, OffsetY, StartScale);

        // 2) 等起点在下一帧落地，再挂上过渡并切到终态。
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
            {
                return;
            }

            child.Transitions = BuildTransitions(Duration);
            child.Opacity = 1;
            ApplyOffset(child, 0d, 1d);
        }, DispatcherPriority.Render);
    }

    private Transitions BuildTransitions(TimeSpan duration) =>
    [
        new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = Ease },
        new TransformOperationsTransition
        {
            Property = RenderTransformProperty,
            Duration = duration,
            Easing = Ease,
        },
    ];

    private bool ShouldSkipAnimation()
    {
        if (!IsAnimationEnabled || Duration <= TimeSpan.Zero)
        {
            return true;
        }

        // 控件尚未挂到可视树（首次布局）时动画没有意义，直接落到终态。
        return VisualRoot is null;
    }

    /// <summary>用 RenderTransform 做位移/缩放：不参与布局，因此不触发重排。</summary>
    private static void ApplyOffset(Visual child, double offsetY, double scale)
    {
        child.RenderTransform = BuildTransform(offsetY, scale);
        child.RenderTransformOrigin = RelativePoint.Center;
    }

    private static TransformOperations BuildTransform(double offsetY, double scale)
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

        return builder.Build();
    }
}
