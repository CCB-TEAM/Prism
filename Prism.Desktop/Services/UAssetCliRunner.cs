using System.Diagnostics;
using System.Runtime.Intrinsics.X86;

namespace Prism.Desktop.Services;

/// <summary>
/// 进程外调用 UAssetCLI 完成纹理替换（ASTC/BC7/DXT 编码依赖外部
/// astcenc/texconv，本机没有 Android 版的 prism_codecs 原生库）。
/// </summary>
public sealed class UAssetCliRunner
{
    private readonly string _cliPath;
    private readonly string? _astcencPath;
    private readonly string? _texconvPath;

    private UAssetCliRunner(string cliPath, string? astcencPath, string? texconvPath)
    {
        _cliPath = cliPath;
        _astcencPath = astcencPath;
        _texconvPath = texconvPath;
    }

    public bool HasEncoders => _astcencPath is not null || _texconvPath is not null;

    public bool HasAstcenc => _astcencPath is not null;

    public bool HasTexconv => _texconvPath is not null;

    /// <summary>
    /// 缓存探测结果。<see cref="TryCreate"/> 会做若干次 <c>File.Exists</c>，
    /// 而 UI 绑定属性（如转换页的可用性提示）每次求值都会调用，缓存避免重复 IO。
    /// </summary>
    private static UAssetCliRunner? _cached;
    private static int _cacheInitialized;

    public static UAssetCliRunner? CreateCached()
    {
        if (Volatile.Read(ref _cacheInitialized) == 0)
        {
            _cached = TryCreate();
            Volatile.Write(ref _cacheInitialized, 1);
        }

        return _cached;
    }

    /// <summary>探测本机 UAssetCLI 与编码器，找不到返回 null。</summary>
    public static UAssetCliRunner? TryCreate()
    {
        string baseDir = AppContext.BaseDirectory;

        // 开发布局：从输出目录往上找仓库根。带 RID 的产物、发布目录、测试工程的输出
        // 深度各不相同（应用是向上 4 层，测试是 6 层），写死层数的历史版本在测试与
        // 发布目录里永远接不上，于是"依赖探测"只在那一种布局下是对的。
        // 现在逐层试，直到找到带 UAssetCLI 子目录或 .slnx 的目录为止。
        string[] roots = DevLayoutRoots(baseDir).ToArray();

        string[] cliCandidates =
        [
            Path.Combine(baseDir, "UAssetCLI", "UAssetCLI.exe"),
            Path.Combine(baseDir, "UAssetCLI", "UAssetCLI.dll"),
            Path.Combine(baseDir, "UAssetCLI", "win-x64", "UAssetCLI.exe"),
            Path.Combine(baseDir, "UAssetCLI", "win-x64", "UAssetCLI.dll"),
            ..roots.SelectMany(DevLayoutCliCandidates),
        ];

        string? cli = cliCandidates.FirstOrDefault(File.Exists);
        if (cli is null)
        {
            return null;
        }

        string[] encoderCandidates =
        [
            Path.Combine(baseDir, "tools"),
            ..roots.Select(root => Path.Combine(root, "tools")),
        ];

        string? Find(string fileName) => encoderCandidates.Select(p => Path.Combine(p, fileName)).FirstOrDefault(File.Exists);

        string? astcenc;
        if (Avx2.IsSupported)
        {
            astcenc = Find("astcenc-avx2.exe")
                   ?? Find("astcenc-sse4.1.exe")
                   ?? Find("astcenc-sse2.exe");
        }
        else if (Sse41.IsSupported)
        {
            astcenc = Find("astcenc-sse4.1.exe")
                   ?? Find("astcenc-sse2.exe")
                   ?? Find("astcenc-avx2.exe");
        }
        else
        {
            astcenc = Find("astcenc-sse2.exe")
                   ?? Find("astcenc-sse4.1.exe")
                   ?? Find("astcenc-avx2.exe");
        }

        string? texconv = Find("texconv.exe");

        return new UAssetCliRunner(cli, astcenc, texconv);
    }

    public async Task<string> InspectTextureAsync(string assetPath, string engine, CancellationToken ct = default)
    {
        List<string> arguments = ["inspect-texture", "--asset", assetPath, "--engine", engine];
        CliResult result = await RunAsync(arguments, ct);
        return result.CombinedOutput;
    }

    public async Task<CliResult> ReplaceTextureAsync(
        string assetPath,
        string imagePath,
        string outputAssetPath,
        string format,
        string engine,
        string astcQuality,
        CancellationToken ct = default)
    {
        List<string> arguments =
        [
            "replace-texture",
            "--asset", assetPath,
            "--source", imagePath,
            "--output", outputAssetPath,
            "--engine", engine,
        ];

        // 目标格式由资产自身声明时，调用方并不知道格式 —— Pak 转换就是这种情况
        // （转换传的是空字符串，格式要等 CLI 打开主 Pak 的模板资产才知道）。
        //
        // 所以不能靠 format 前缀决定给哪个编码器：原先只有 format 以 PF_ASTC_ 开头
        // 才传 --astcenc，空字符串会落到 else 分支、只传 --texconv。而 astcenc 通常
        // 不在 PATH 上（它随应用放在 tools/ 目录里），CLI 于是报
        // "Could not find encoder tool" —— 整批 ASTC 目标全部失败。
        //
        // 改为两个路径都传，由 CLI 按目标格式自行选择；它只会用到其中一个。
        if (!string.IsNullOrWhiteSpace(format))
        {
            arguments.AddRange(["--expected-format", format]);
        }

        if (_astcencPath is not null)
        {
            arguments.AddRange(["--astcenc", _astcencPath, "--astc-quality", astcQuality]);
        }

        if (_texconvPath is not null)
        {
            arguments.AddRange(["--texconv", _texconvPath]);
        }

        if (_astcencPath is null && _texconvPath is null && !IsInternallyEncodedFormat(format))
        {
            return CliResult.Fail("缺少 astcenc 与 texconv 编码器，无法重新编码纹理。");
        }

        return await RunAsync(arguments, ct);
    }

    /// <summary>
    /// 这些格式由 CLI 自己编码（DXT1 走内置 BC1 编码器，未压缩格式直接打包像素），
    /// 不需要 astcenc / texconv。没有外部编码器时不能把它们一起挡掉。
    /// </summary>
    private static bool IsInternallyEncodedFormat(string format) =>
        string.Equals(format, "PF_DXT1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, "PF_B8G8R8A8", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, "PF_R8G8B8A8", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, "PF_A8R8G8B8", StringComparison.OrdinalIgnoreCase);

    private async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _cliPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 UAssetCLI。");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        string[] output = await Task.WhenAll(stdoutTask, stderrTask);
        return new CliResult(process.ExitCode, output[0], output[1]);
    }

    public sealed record CliResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => string.Join(Environment.NewLine,
            new[] { StandardOutput, StandardError }.Where(t => !string.IsNullOrWhiteSpace(t)));

        /// <summary>
        /// 给界面看的失败原因：CLI 未捕获异常时会打印整个 ToString()（首行是异常类型+消息，
        /// 后面全是堆栈帧），整段塞进替换项会让界面变成一面堆栈墙。这里只留首行，
        /// 完整输出由调用方写进日志。
        /// </summary>
        public string DescribeFailure()
        {
            foreach (string raw in CombinedOutput.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("at ", StringComparison.Ordinal)
                    || line.StartsWith("--- End of stack trace", StringComparison.Ordinal))
                {
                    continue;
                }

                return line;
            }

            return "替换失败（CLI 没有输出原因，完整日志见「设置 → 日志」）。";
        }

        public static CliResult Fail(string message) => new(1, string.Empty, message);
    }

    /// <summary>
    /// 开发布局下的 UAssetCLI 产物候选。优先 AOT 发布（README 推荐的发布形态，
    /// 体积小且不需要 .NET 运行时），其次普通构建产物。
    /// </summary>
    private static string[] DevLayoutCliCandidates(string repoRoot) =>
    [
        Path.Combine(repoRoot, "UAssetCLI", "bin", "Release", "net10.0", "win-x64", "publish", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "UAssetCLI", "bin", "Release", "net10.0", "publish", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "UAssetCLI", "bin", "Release", "net10.0", "win-x64", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "UAssetCLI", "bin", "Release", "net10.0", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "UAssetCLI", "bin", "Debug", "net10.0", "win-x64", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "UAssetCLI", "bin", "Debug", "net10.0", "UAssetCLI.exe"),
        Path.Combine(repoRoot, "artifacts", "dist", "win-x64", "UAssetCLI", "UAssetCLI.exe"),
    ];

    /// <summary>
    /// 从输出目录逐层向上，返回看起来像仓库根的目录（有 UAssetCLI 子目录或 .slnx）。
    /// 最多试 8 层，且全部返回 —— 发布目录里可能既有 UAssetCLI 子目录又有上层仓库根。
    /// </summary>
    private static IEnumerable<string> DevLayoutRoots(string baseDir)
    {
        DirectoryInfo? dir = new(baseDir);
        var depth = 0;
        while (dir is not null && depth++ < 8)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "UAssetCLI")) ||
                Directory.GetFiles(dir.FullName, "*.slnx").Length > 0)
            {
                yield return dir.FullName;
            }

            dir = dir.Parent;
        }
    }
}
