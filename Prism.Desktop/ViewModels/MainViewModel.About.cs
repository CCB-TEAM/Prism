using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Prism.Desktop.Services;

namespace Prism.Desktop.ViewModels;

/// <summary>「关于」里的创作者 / 致谢条目。</summary>
public sealed record AboutPerson(string Name, string Role, string Initial, string Url, string LinkText);

public sealed record AboutCredit(string Name, string Detail, string? Url);

/// <summary>
/// 设置页的「关于」与「运行状态」。
///
/// 关于页把创作者、仓库地址、致谢和许可证集中放一处 —— 之前设置页只有一行
/// "Prism · Pak 资产管理工具"，连作者是谁都看不到。
/// </summary>
public partial class MainViewModel
{
    public string AppRepositoryUrl => "https://github.com/CCB-TEAM/Prism";

    public string AppLicenseText => "GPL-3.0 · 源码公开，修改后再发布同样要开源";

    public string RuntimeLine =>
        $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSArchitecture}";

    public IReadOnlyList<AboutPerson> Creators { get; } =
    [
        new AboutPerson(
            "氧化铜依旧CuO",
            "项目发起 · 主程序",
            "CuO",
            "https://space.bilibili.com/3546971308559129",
            "B 站主页"),
        new AboutPerson(
            "KardsWalker卡兹走路人",
            "联合创作",
            "KW",
            "https://space.bilibili.com/1253227194",
            "B 站主页"),
    ];

    public IReadOnlyList<AboutCredit> Credits { get; } =
    [
        new AboutCredit("Flora", "蓝图节点图反编译 + 控件设计器的参考实现（GPL-3.0）", "https://github.com/CCB-TEAM/Flora"),
        new AboutCredit("UE Blueprint Graph Viewer", "字节码 → 节点图反编译器的原始实现（MIT）", null),
        new AboutCredit("CUE4Parse", "UE 资产 / Pak 解析", "https://github.com/FabianFG/CUE4Parse"),
        new AboutCredit("UAssetAPI", "uasset 读写与 Kismet 序列化", "https://github.com/atenfyr/UAssetAPI"),
        new AboutCredit("Avalonia", "跨平台 UI 框架", "https://avaloniaui.net"),
        new AboutCredit("FFmpeg", "视频预览取帧（可选组件）", "https://ffmpeg.org"),
        new AboutCredit("Oodle / oo2core", "Oodle 压缩（需自行从引擎目录取得）", null),
    ];

    // ── 运行状态（依赖探测）────────────────────────────────────────────────

    [ObservableProperty]
    public partial string DependencySummary { get; set; } = "未检测";

    public string DependencyHeadline => DependencyReport.Headline;

    public bool HasMissingDependency => Dependencies.Any(item => item.IsMissing);

    public ObservableCollection<DependencyCheck> Dependencies { get; } = [];

    /// <summary>重新探测并刷新「运行状态」。启动时也调它，但不往状态栏写提示。</summary>
    private void ReloadDependencies()
    {
        Dependencies.Clear();
        foreach (var item in DependencyReport.Probe())
            Dependencies.Add(item);

        var missing = Dependencies.Count(item => item.IsMissing);
        DependencySummary = Dependencies.Count == 0
            ? "未检测到可用组件"
            : missing == 0 ? "全部就绪" : $"{missing} 项缺失";

        OnPropertyChanged(nameof(HasMissingDependency));
    }

    [RelayCommand]
    private void RefreshDependencies()
    {
        ReloadDependencies();
        StatusText = $"依赖检测完成：{DependencySummary}";
        AppLog.Info($"依赖检测：{DependencySummary}", "设置");
    }

    [RelayCommand]
    private async Task OpenLinkAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            if (TopLevel?.Launcher is { } launcher && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                await launcher.LaunchUriAsync(uri);
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception(ex, $"打开链接 {url}");
        }

        StatusText = $"打开失败，请手动访问：{url}";
    }
}
