using System.Diagnostics;

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

    /// <summary>探测本机 UAssetCLI 与编码器，找不到返回 null。</summary>
    public static UAssetCliRunner? TryCreate()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] cliCandidates =
        [
            Path.Combine(baseDir, "UAssetCLI", "UAssetCLI.exe"),
            Path.Combine(baseDir, "UAssetCLI", "UAssetCLI.dll"),
            // 开发布局：prism/UAssetCLI/bin/{Debug,Release}/net10.0
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "UAssetCLI", "bin", "Debug", "net10.0", "UAssetCLI.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "UAssetCLI", "bin", "Release", "net10.0", "UAssetCLI.exe")),
        ];

        string? cli = cliCandidates.FirstOrDefault(File.Exists);
        if (cli is null)
        {
            return null;
        }

        string[] encoderCandidates =
        [
            Path.Combine(baseDir, "tools"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "tools")),
        ];

        string? Find(string fileName) => encoderCandidates.Select(p => Path.Combine(p, fileName)).FirstOrDefault(File.Exists);

        string? astcenc = Find("astcenc-avx2.exe")
                       ?? Find("astcenc-sse4.1.exe")
                       ?? Find("astcenc-sse2.exe");
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
            "--expected-format", format,
        ];

        if (format.StartsWith("PF_ASTC_", StringComparison.OrdinalIgnoreCase))
        {
            if (_astcencPath is null)
            {
                return CliResult.Fail("缺少 astcenc 编码器，无法编码 ASTC 纹理。");
            }

            arguments.AddRange(["--astcenc", _astcencPath, "--astc-quality", astcQuality]);
        }
        else
        {
            if (_texconvPath is null)
            {
                return CliResult.Fail("缺少 texconv 编码器，无法编码该格式纹理。");
            }

            arguments.AddRange(["--texconv", _texconvPath]);
        }

        return await RunAsync(arguments, ct);
    }

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

        public static CliResult Fail(string message) => new(1, string.Empty, message);
    }
}
