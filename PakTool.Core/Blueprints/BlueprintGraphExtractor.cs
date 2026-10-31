using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Prism.BlueprintGraph;
using Prism.BlueprintGraph.Assets;
using Prism.BlueprintGraph.Decompiler;
using Prism.BlueprintGraph.Nodes;
using Prism.BlueprintGraph.Signatures;
using static Prism.BlueprintGraph.Engine.EngineBPData;

namespace PakTool.Core.Blueprints;

/// <summary>
/// 把 cook 过的蓝图资产反编译成节点图（只读）。
///
/// 为什么不是"读 UEdGraph"：编辑器里的节点、连线、注释都是 editor-only 数据，
/// cook 时被剥掉了，包里只剩生成的类与每个函数的 Kismet 字节码。所以节点图只能
/// 从字节码**重建** —— 这正是 Flora 里 FModel.BlueprintGraph 做的事（原始实现
/// 来自 UE Blueprint Graph Viewer，MIT）。这里把它接进 Prism，产物转成 DTO 给界面。
///
/// 一个实例绑定一个包：反编译要反复查 UFUNCTION / 属性表，重建一次比每次重新
/// 加载包便宜得多。调用方负责缓存实例，并在切换资产时释放。
/// </summary>
public sealed class BlueprintGraphExtractor
{
    private readonly BlueprintGraphContext _context;
    private readonly Asset _asset;
    private readonly Dictionary<string, UFunction> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlueprintGraphViewDto> _graphs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private BlueprintGraphExtractor(Asset asset, BlueprintGraphContext context)
    {
        _asset = asset;
        _context = context;
    }

    public string DisplayName => _asset.ObjectName;

    public string ClassName => _asset.GeneratedClass?.Name ?? DisplayName;

    public string SuperClass => _asset.GeneratedClass?.SuperStruct?.ResolvedObject?.GetPathName() ?? "None";

    /// <summary>这个导出集合看起来是不是蓝图（生成类一定在包里）。</summary>
    public static bool LooksLikeBlueprint(IPackage package)
    {
        try
        {
            return package.GetExports().Any(export => export is UBlueprintGeneratedClass);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析资产的功能列表。不是蓝图、或者生成类读不出来时返回 null。
    /// </summary>
    public static BlueprintGraphExtractor? TryCreate(IPackage package, string name, TypeMappings? mappings, out string? error)
    {
        error = null;
        try
        {
            var options = new BlueprintGraphOptions
            {
                ReorderEvents = true,
                ParseMacros = true,
                UseMsagl = true,
                SingleEventGraph = true,
                Macros = BlueprintGraphOptions.LoadMacros()
            };

            var providers = new List<ISignatureProvider>();
            if (mappings is not null)
                providers.Add(new MappingsSignatureProvider(mappings));

            var context = new BlueprintGraphContext(new CompositeSignatureProvider(providers), options);
            var asset = new Asset(package, name, options.ReorderEvents);
            if (!asset.IsBP || asset.GeneratedClass is null)
            {
                error = "不是蓝图生成类。";
                return null;
            }

            var extractor = new BlueprintGraphExtractor(asset, context);
            extractor.IndexFunctions();
            return extractor;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>事件按 ubergraph 入口排序在前，函数按名字排在后面。</summary>
    private void IndexFunctions()
    {
        // Events 的键是给人看的名字（可能带组件/输入事件前缀），值才是 UFunction。
        foreach (var pair in _asset.Events)
        {
            if (pair.Value is null)
                continue;

            var key = UniqueKey(pair.Value.Name);
            _byKey[key] = pair.Value;
            _display[key] = pair.Key;
        }

        foreach (var function in _asset.Functions.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = UniqueKey(function.Name);
            _byKey[key] = function;
            _display[key] = function.Name;
        }
    }

    private readonly Dictionary<string, string> _display = new(StringComparer.Ordinal);

    private string UniqueKey(string name)
    {
        var key = name;
        var suffix = 2;
        while (_byKey.ContainsKey(key))
            key = $"{name}#{suffix++}";
        return key;
    }

    public BlueprintGraphDto Describe()
    {
        var functions = new List<BlueprintFunctionDto>(_byKey.Count);
        foreach (var pair in _byKey)
        {
            var isEvent = _asset.IsEvent(pair.Value);
            functions.Add(new BlueprintFunctionDto(
                pair.Key,
                _display[pair.Key],
                pair.Value.Name,
                isEvent ? "event" : "function",
                0,
                null));
        }

        // 事件在前，函数在后；组内保持索引里已有的顺序。
        functions = functions
            .OrderByDescending(f => f.Kind == "event")
            .ToList();

        return new BlueprintGraphDto(
            DisplayName,
            ClassName,
            SuperClass,
            functions,
            _asset.Events.Count == 0 ? "没有事件，只有函数。" : $"{_asset.Events.Count} 个事件 / {_asset.Functions.Count} 个函数");
    }

    /// <summary>
    /// 反编译一个函数（事件会反编译到它所在的 ubergraph 入口）。结果按 key 缓存。
    /// 失败返回 null，并把原因写进 <paramref name="error"/>。
    /// </summary>
    public BlueprintGraphViewDto? Decompile(string key, out string? error)
    {
        error = null;
        lock (_gate)
        {
            if (_graphs.TryGetValue(key, out var cached))
                return cached;

            if (!_byKey.TryGetValue(key, out var function))
            {
                error = $"找不到函数 {key}。";
                return null;
            }

            try
            {
                var decompiler = new FunctionDecompiler(new GlobalDecompilerContext(_asset, _context, function));
                var result = decompiler.Decompile(null);
                var graph = decompiler.Graph;

                // 坐标是布局算出来的：先算节点尺寸，再跑分层布局。
                graph.LayoutNodesMsagl(null, useMsagl: true);

                var view = ToDto(key, graph, result);
                _graphs[key] = view;
                return view;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }
    }

    private BlueprintGraphViewDto ToDto(string key, BPGraph graph, DecompilationResult result)
    {
        var nodes = new List<BlueprintNodeDto>(graph.Nodes.Count);
        var pinIds = new Dictionary<GraphPin, string>();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var node = graph.Nodes[i];
            var inputs = new List<BlueprintPinDto>();
            var outputs = new List<BlueprintPinDto>();

            AddPins(node.Input, isInput: true);
            AddPins(node.Output, isInput: false);

            nodes.Add(new BlueprintNodeDto(
                i,
                string.IsNullOrWhiteSpace(node.Name)
                    ? node.Input.Concat(node.Output).FirstOrDefault(p => !p.PinType.PinCategory.Equals(PinType.exec))?.PinFriendlyName ?? node.NodeType
                    : node.Name,
                node.NodeType,
                ShortOwner(node.DefinitionOwner),
                node.X,
                node.Y,
                Math.Max(node.NodeWidth, 60),
                Math.Max(node.NodeHeight, 24),
                node.HeaderHidden,
                node.ShowNameAsBody,
                node.Pure,
                node.IsSignatureInferred,
                node.StatementIndex,
                inputs,
                outputs));

            minX = Math.Min(minX, node.X);
            minY = Math.Min(minY, node.Y);
            maxX = Math.Max(maxX, node.Right);
            maxY = Math.Max(maxY, node.Bottom);

            void AddPins(List<GraphPin> pins, bool isInput)
            {
                for (var p = 0; p < pins.Count; p++)
                {
                    var pin = pins[p];
                    var id = $"n{i}{(isInput ? 'i' : 'o')}{p}";
                    pinIds[pin] = id;

                    // 被折叠掉的引脚（宏内部、隐藏的参数）不画，也不占位置。
                    if (pin.IsHidden)
                        continue;

                    var dto = new BlueprintPinDto(
                        id,
                        string.IsNullOrWhiteSpace(pin.PinFriendlyName) ? pin.PinName : pin.PinFriendlyName,
                        isInput,
                        pin.PinType.PinCategory == PinType.exec,
                        pin.PinType.PinCategory.ToString(),
                        pin.PinType.PinSubCategoryObject,
                        pin.PinType.ContainerType.ToString(),
                        pin.X,
                        pin.Y,
                        pin.Value,
                        pin.IsNameHidden,
                        pin.IsConnected);

                    if (isInput)
                        inputs.Add(dto);
                    else
                        outputs.Add(dto);
                }
            }
        }

        var links = new List<BlueprintLinkDto>();
        foreach (var node in graph.Nodes)
        {
            foreach (var pin in node.Output)
            {
                if (!pinIds.TryGetValue(pin, out var from))
                    continue;

                foreach (var target in pin.LinkedTo)
                {
                    if (!pinIds.TryGetValue(target, out var to))
                        continue;

                    links.Add(new BlueprintLinkDto(from, to, pin.PinType.PinCategory.ToString()));
                }
            }
        }

        if (nodes.Count == 0)
        {
            minX = minY = 0;
            maxX = maxY = 0;
        }

        return new BlueprintGraphViewDto(
            key,
            _display.GetValueOrDefault(key, key),
            _asset.IsEvent(_byKey[key]) ? "event" : "function",
            nodes,
            links,
            minX,
            minY,
            Math.Max(maxX - minX, 1),
            Math.Max(maxY - minY, 1),
            BuildNote(graph, result),
            result.HasCriticalErrors);
    }

    /// <summary>
    /// 反编译器的 Problems 大多是"没有映射签名，已按调用处推断"这类英文提示，
    /// 原样铺到画布底栏会变成一墙英文。这里只留真正影响结果的信息，其余折成一句中文。
    /// </summary>
    private static string? BuildNote(BPGraph graph, DecompilationResult result)
    {
        if (result.HasCriticalErrors)
        {
            var critical = result.Problems.FirstOrDefault(p => p.IsCritical);
            return $"反编译不完整：{critical?.Message ?? "部分字节码读不出来"}";
        }

        var inferred = graph.Nodes.Count(node => node.IsSignatureInferred);
        return inferred == 0 ? null : $"{inferred} 个节点的函数签名由调用处推断";
    }

    private static string? ShortOwner(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return null;

        var slash = owner.LastIndexOf('/');
        var dot = owner.LastIndexOf('.');
        var index = Math.Max(slash, dot);
        return index >= 0 && index + 1 < owner.Length ? owner[(index + 1)..] : owner;
    }
}
