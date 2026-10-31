namespace PakTool.Core.Blueprints;

/// <summary>
/// 蓝图节点图的只读视图模型（给界面画图用）。
///
/// 这里不直接暴露反编译器的 <c>BPGraph</c>：那套类型里混着 MSAGL 的几何对象、
/// 字节码表达式、可编辑连线的状态，跨线程传到 UI 层既容易踩到非线程安全的集合，
/// 也会把反编译器的实现细节焊死进界面。DTO 只保留画图与点选需要的东西。
/// </summary>
public sealed record BlueprintGraphDto(
    string Name,
    string ClassName,
    string SuperClass,
    IReadOnlyList<BlueprintFunctionDto> Functions,
    string Note);

/// <summary>函数列表里的一项。事件都共用同一个 ubergraph，反编译按项缓存。</summary>
public sealed record BlueprintFunctionDto(
    string Key,
    string DisplayName,
    string FunctionName,
    string Kind,
    int NodeCount,
    string? Status);

/// <summary>一个函数反编译出来的图。</summary>
public sealed record BlueprintGraphViewDto(
    string Key,
    string DisplayName,
    string Kind,
    IReadOnlyList<BlueprintNodeDto> Nodes,
    IReadOnlyList<BlueprintLinkDto> Links,
    float MinX,
    float MinY,
    float Width,
    float Height,
    string? Note,
    bool HasCriticalErrors);

public sealed record BlueprintNodeDto(
    int Id,
    string Name,
    string NodeType,
    string? Subtitle,
    float X,
    float Y,
    int Width,
    int Height,
    bool HeaderHidden,
    bool ShowNameAsBody,
    bool Pure,
    bool IsSignatureInferred,
    int StatementIndex,
    IReadOnlyList<BlueprintPinDto> Inputs,
    IReadOnlyList<BlueprintPinDto> Outputs);

/// <summary>
/// 一个引脚。坐标是画布绝对坐标（反编译器算好的锚点），不是节点内偏移。
/// <c>PinCategory</c> 是反编译器 <c>PinType</c> 枚举的名字（exec / Bool / Float…），
/// 颜色由界面按这张表决定，DTO 不携带画图资源。
/// </summary>
public sealed record BlueprintPinDto(
    string Id,
    string Name,
    bool IsInput,
    bool IsExec,
    string PinCategory,
    string? PinSubCategory,
    string ContainerType,
    float X,
    float Y,
    string Value,
    bool IsNameHidden,
    bool IsConnected);

public sealed record BlueprintLinkDto(
    string FromPinId,
    string ToPinId,
    string PinCategory);
