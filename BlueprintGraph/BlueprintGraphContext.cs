using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Prism.BlueprintGraph.Signatures;

namespace Prism.BlueprintGraph;

public class BlueprintGraphOptions
{
    /// <summary>Order ubergraph events by their entry point instead of declaration order.</summary>
    public bool ReorderEvents = true;

    /// <summary>Decompile the selected event before the other ubergraph events.</summary>
    public bool SelectedEventFirst;

    /// <summary>Use MSAGL layered layout instead of the built-in one.</summary>
    public bool UseMsagl = true;

    /// <summary>Collapse inlined engine macros (loops, DoOnce...) back to macro nodes.</summary>
    public bool ParseMacros = true;

    /// <summary>
    /// Only decompile the selected event instead of every event of the ubergraph.
    /// 原版把整张 ubergraph 画在一起（点任何事件看到的都是同一张图）；只读查看器
    /// 里那样既难读又慢，所以默认按事件切开。
    /// </summary>
    public bool SingleEventGraph = true;

    public IReadOnlyDictionary<string, BPGraph> Macros = new Dictionary<string, BPGraph>();

    // debug switches kept from the original viewer
    public static bool DrawDebugGraph;
    public static bool ExperimentalExecStraightening;

    public static string DefaultMacrosDirectory => Path.Combine(AppContext.BaseDirectory, "Macros");

    public static Dictionary<string, BPGraph> LoadMacros(string directory)
    {
        Dictionary<string, BPGraph> macros = [];
        if (!Directory.Exists(directory))
            return macros;

        foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(o => o, StringComparer.Ordinal))
        {
            macros[Path.GetFileNameWithoutExtension(path)] = GraphJson.FromJson(File.ReadAllText(path));
        }
        return macros;
    }

    private static readonly Lazy<Dictionary<string, BPGraph>> EmbeddedMacros = new(LoadEmbedded, isThreadSafe: true);

    /// <summary>
    /// 宏定义（ForEach / DoOnce / Gate…）的共享表。原版从输出目录的 Macros/ 读，
    /// 安卓上没有这样一个可写目录，所以这里改成读嵌入资源；解析一次后缓存。
    /// 反编译器会拿它把展开的循环字节码折叠回一个 MacroInstance 节点。
    /// </summary>
    public static Dictionary<string, BPGraph> LoadMacros() => EmbeddedMacros.Value;

    private static Dictionary<string, BPGraph> LoadEmbedded()
    {
        Dictionary<string, BPGraph> macros = new(StringComparer.Ordinal);
        var assembly = typeof(BlueprintGraphOptions).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = resource[(resource.LastIndexOf('.', resource.Length - 6) + 1)..^5];
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
                continue;

            using var reader = new StreamReader(stream);
            try
            {
                macros[name] = GraphJson.FromJson(reader.ReadToEnd());
            }
            catch
            {
                // 一个宏坏了不该让整张图都反编译不出来。
            }
        }
        return macros;
    }
}

/// <summary>
/// Everything the decompiler needs besides the package itself (replaces the viewer's per-game profile).
/// </summary>
public class BlueprintGraphContext
{
    public ISignatureProvider Signatures;
    public BlueprintGraphOptions Options;

    public BlueprintGraphContext(ISignatureProvider signatures, BlueprintGraphOptions? options = null)
    {
        Signatures = signatures;
        Options = options ?? new BlueprintGraphOptions();
    }
}
