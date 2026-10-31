using Avalonia.Media;

namespace Prism.Desktop.Services.Blueprints;

/// <summary>
/// 蓝图节点图的配色。端口颜色照搬 UE 编辑器的习惯（exec 白、bool 暗红、float 亮绿、
/// object 天蓝…），节点头颜色按节点类型分组，和 Flora / UE 的观感一致。
/// </summary>
public static class BlueprintGraphStyle
{
    private static readonly Color CallColor = Color.FromRgb(84, 129, 159);
    private static readonly Color EventColor = Color.FromRgb(160, 27, 43);
    private static readonly Color FunctionColor = Color.FromRgb(134, 39, 159);
    private static readonly Color FlowColor = Color.FromRgb(130, 130, 130);
    private static readonly Color PureColor = Color.FromRgb(102, 136, 97);
    private static readonly Color SwitchColor = Color.FromRgb(149, 149, 16);
    private static readonly Color MathColor = Color.FromRgb(102, 136, 97);
    private static readonly Color UnknownColor = Color.FromRgb(150, 58, 58);

    private static readonly Dictionary<string, Color> PinColors = new(StringComparer.Ordinal)
    {
        ["exec"] = Color.FromRgb(235, 235, 240),
        ["Bool"] = Color.FromRgb(220, 20, 60),
        ["Byte"] = Color.FromRgb(0, 139, 139),
        ["Enum"] = Color.FromRgb(0, 139, 139),
        ["Int"] = Color.FromRgb(102, 205, 170),
        ["Int64"] = Color.FromRgb(152, 251, 152),
        ["Float"] = Color.FromRgb(50, 205, 50),
        ["Double"] = Color.FromRgb(50, 205, 50),
        ["Name"] = Color.FromRgb(138, 43, 226),
        ["String"] = Color.FromRgb(255, 0, 255),
        ["Text"] = Color.FromRgb(255, 105, 180),
        ["Vector"] = Color.FromRgb(255, 215, 0),
        ["Rotator"] = Color.FromRgb(123, 104, 238),
        ["Transform"] = Color.FromRgb(255, 99, 71),
        ["Interface"] = Color.FromRgb(240, 230, 140),
        ["Object"] = Color.FromRgb(0, 191, 255),
        ["SoftObject"] = Color.FromRgb(175, 238, 238),
        ["Class"] = Color.FromRgb(75, 0, 130),
        ["SoftClass"] = Color.FromRgb(238, 130, 238),
        ["Delegate"] = Color.FromRgb(220, 20, 60),
        ["Struct"] = Color.FromRgb(65, 105, 225),
        ["Wildcard"] = Color.FromRgb(150, 150, 150),
    };

    private static readonly Dictionary<string, Color> NodeColors = new(StringComparer.Ordinal)
    {
        ["AddDelegate"] = CallColor,
        ["CallDelegate"] = CallColor,
        ["CallFunction"] = CallColor,
        ["CallArrayFunction"] = CallColor,
        ["ClearDelegate"] = CallColor,
        ["RemoveDelegate"] = CallColor,
        ["SpawnActorFromClass"] = CallColor,
        ["GenericCreateObject"] = CallColor,
        ["AssignmentStatement"] = CallColor,
        ["CreateWidget"] = CallColor,
        ["Tunnel"] = CallColor,
        ["Event"] = EventColor,
        ["InputAction"] = EventColor,
        ["InputKey"] = EventColor,
        ["InputAxisKey"] = EventColor,
        ["InputAxisEvent"] = EventColor,
        ["EnhancedInputAction"] = EventColor,
        ["ComponentBoundEvent"] = EventColor,
        ["WidgetAnimationEvent"] = EventColor,
        ["FunctionEntry"] = FunctionColor,
        ["FunctionResult"] = FunctionColor,
        ["IfThenElse"] = FlowColor,
        ["ExecutionSequence"] = FlowColor,
        ["MacroInstance"] = FlowColor,
        ["VariableSet"] = Color.FromRgb(85, 85, 85),
        ["DynamicCast"] = Color.FromRgb(19, 114, 118),
        ["ClassDynamicCast"] = Color.FromRgb(63, 20, 111),
        ["Select"] = PureColor,
        ["MakeArray"] = PureColor,
        ["MakeMap"] = PureColor,
        ["MakeSet"] = PureColor,
        ["TemporaryVariable"] = PureColor,
        ["SwitchInteger"] = SwitchColor,
        ["SwitchString"] = SwitchColor,
        ["SwitchName"] = SwitchColor,
        ["SwitchEnum"] = SwitchColor,
        ["Timeline"] = SwitchColor,
        ["MakeStruct"] = Color.FromRgb(20, 50, 130),
        ["PromotableOperator"] = MathColor,
        ["CommutativeAssociativeBinaryOperator"] = MathColor,
    };

    private static readonly Dictionary<string, IBrush> PinBrushes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IPen> LinkPens = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IBrush> NodeBrushes = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static Color PinColor(string category) =>
        PinColors.GetValueOrDefault(category, UnknownColor);

    public static Color NodeColor(string nodeType, bool pure)
    {
        var type = nodeType.StartsWith("K2Node_", StringComparison.Ordinal) ? nodeType[7..] : nodeType;
        if (pure && type is "CallFunction" or "CallArrayFunction")
            return PureColor;

        return NodeColors.GetValueOrDefault(type, UnknownColor);
    }

    public static IBrush PinBrush(string category)
    {
        lock (Gate)
        {
            if (!PinBrushes.TryGetValue(category, out var brush))
            {
                brush = new SolidColorBrush(PinColor(category));
                PinBrushes[category] = brush;
            }
            return brush;
        }
    }

    public static IPen LinkPen(string category)
    {
        lock (Gate)
        {
            if (!LinkPens.TryGetValue(category, out var pen))
            {
                var color = PinColor(category);
                pen = new Pen(new SolidColorBrush(Color.FromArgb(210, color.R, color.G, color.B)), 1.8);
                LinkPens[category] = pen;
            }
            return pen;
        }
    }

    /// <summary>节点头部用的深色底：把端口色压暗，白字才看得清。</summary>
    public static IBrush NodeHeaderBrush(string nodeType, bool pure)
    {
        var key = nodeType + (pure ? "|pure" : string.Empty);
        lock (Gate)
        {
            if (!NodeBrushes.TryGetValue(key, out var brush))
            {
                var color = NodeColor(nodeType, pure);
                brush = new SolidColorBrush(Color.FromRgb(
                    (byte)(color.R * 0.72),
                    (byte)(color.G * 0.72),
                    (byte)(color.B * 0.72)));
                NodeBrushes[key] = brush;
            }
            return brush;
        }
    }

    /// <summary>节点标题栏上叠加的一层：保证暗色主题下也有层次。</summary>
    public static IBrush NodeBodyBrush { get; } = new SolidColorBrush(Color.FromRgb(28, 30, 36));

    public static IBrush NodeBodySelectedBrush { get; } = new SolidColorBrush(Color.FromRgb(36, 40, 48));

    public static IBrush NodeBorderBrush { get; } = new SolidColorBrush(Color.FromRgb(58, 62, 72));

    public static IPen NodeBorderPen { get; } = new Pen(NodeBorderBrush, 1);

    public static IPen SelectedPen { get; } = new Pen(new SolidColorBrush(Color.FromRgb(255, 165, 0)), 2);

    public static IPen HoverPen { get; } = new Pen(new SolidColorBrush(Color.FromRgb(100, 180, 255)), 1.6);

    public static IBrush CanvasBackground { get; } = new SolidColorBrush(Color.FromRgb(22, 23, 28));

    public static IBrush GridDotBrush { get; } = new SolidColorBrush(Color.FromRgb(34, 36, 43));

    public static IBrush TitleBrush { get; } = Brushes.White;

    public static IBrush PinTextBrush { get; } = new SolidColorBrush(Color.FromRgb(205, 208, 215));

    public static IBrush ValueTextBrush { get; } = new SolidColorBrush(Color.FromRgb(150, 200, 240));

    public static IBrush MutedTextBrush { get; } = new SolidColorBrush(Color.FromRgb(140, 145, 155));

    public static IBrush FooterBrush { get; } = new SolidColorBrush(Color.FromArgb(220, 16, 17, 21));
}
