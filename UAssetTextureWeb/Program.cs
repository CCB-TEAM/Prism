using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using UAssetAPI;

JsonSerializerOptions JsonFileOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

var builder = WebApplication.CreateBuilder(args);
StartupConfig startupConfig = StartupConfig.LoadOrCreate(builder.Configuration, builder.Environment.ContentRootPath);

builder.WebHost.UseUrls(startupConfig.Url);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 512L * 1024L * 1024L;
});

builder.Services.AddSingleton(startupConfig.Settings);
builder.Services.AddSingleton<UAssetCliRunner>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/settings", (EncoderSettings settings) =>
{
    return Results.Ok(new
    {
        configured = settings.IsComplete,
        astcConfigured = File.Exists(settings.AstcEncoderPath),
        bc7Configured = File.Exists(settings.Bc7EncoderPath),
        cliConfigured = settings.CliInvocation.IsConfigured,
        maxParallelJobs = settings.MaxParallelJobs,
        astcEncoderPath = settings.AstcEncoderPath,
        bc7EncoderPath = settings.Bc7EncoderPath,
        cliPath = settings.CliInvocation.IsConfigured
            ? (settings.CliInvocation.PrefixArguments.Count > 0
                ? settings.CliInvocation.PrefixArguments[0]
                : settings.CliInvocation.FileName)
            : string.Empty,
        message = settings.IsComplete ? "后端编码器已配置。" : settings.GetConfigurationError(),
    });
});

app.MapPost("/api/settings", (
    SettingsUpdateRequest update,
    EncoderSettings currentSettings,
    IWebHostEnvironment env) =>
{
    string appData = Path.Combine(env.ContentRootPath, "App_Data");
    Directory.CreateDirectory(appData);
    string configPath = Path.Combine(appData, "server-settings.json");

    ServerSettingsFile fileSettings;
    if (File.Exists(configPath))
    {
        string json = File.ReadAllText(configPath);
        fileSettings = JsonSerializer.Deserialize<ServerSettingsFile>(json, JsonFileOptions)
            ?? new ServerSettingsFile();
    }
    else
    {
        fileSettings = new ServerSettingsFile();
    }

    // 根据用户选择的 ASTC 变体更新路径
    if (!string.IsNullOrWhiteSpace(update.AstcVariant))
    {
        string variant = update.AstcVariant.Trim().ToLowerInvariant();
        string[] allowed = ["auto", "avx2", "sse4.1", "sse2"];
        if (!allowed.Contains(variant))
        {
            return Results.BadRequest(new ErrorResponse($"无效的 ASTC 版本: {variant}。可选: {string.Join(", ", allowed)}"));
        }

        if (variant == "auto")
        {
            // 清除固定路径，让启动/加载逻辑按当前 CPU 自动选择
            fileSettings.AstcEncoderPath = string.Empty;
        }
        else
        {
            string fileName = $"astcenc-{variant}.exe";
            string resolvedPath = EncoderSettings.FindEncoder(env.ContentRootPath, fileName) ?? string.Empty;
            fileSettings.AstcEncoderPath = resolvedPath;
        }
    }

    string jsonOut = JsonSerializer.Serialize(fileSettings, JsonFileOptions);
    File.WriteAllText(configPath, jsonOut);

    // 重建运行时设置，并通过反射更新内存中的单例
    EncoderSettings newSettings = EncoderSettings.FromFileSettings(fileSettings, env.ContentRootPath);
    SetProperty(currentSettings, nameof(EncoderSettings.AstcEncoderPath), newSettings.AstcEncoderPath);

    return Results.Ok(new
    {
        configured = currentSettings.IsComplete,
        astcConfigured = File.Exists(currentSettings.AstcEncoderPath),
        bc7Configured = File.Exists(currentSettings.Bc7EncoderPath),
        cliConfigured = currentSettings.CliInvocation.IsConfigured,
        message = currentSettings.IsComplete
            ? "设置已保存，后端已就绪。"
            : currentSettings.GetConfigurationError(),
    });
});

app.MapPost("/api/replace", async (
    HttpRequest request,
    EncoderSettings settings,
    UAssetCliRunner runner,
    CancellationToken cancellationToken) =>
{
    if (!settings.IsComplete)
    {
        return Results.BadRequest(new ErrorResponse(settings.GetConfigurationError()));
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new ErrorResponse("请求必须是 multipart/form-data。"));
    }

    IFormCollection form = await request.ReadFormAsync(cancellationToken);
    IFormFile? uasset = form.Files.GetFile("uasset");
    IFormFile? uexp = form.Files.GetFile("uexp");
    IFormFile? ubulk = form.Files.GetFile("ubulk");
    IFormFile? image = form.Files.GetFile("image");
    string format = form["format"].FirstOrDefault() ?? string.Empty;
    string engine = form["engine"].FirstOrDefault() ?? "VER_UE5_6";
    string astcQuality = form["astcQuality"].FirstOrDefault() ?? "medium";

    if (uasset is null || uexp is null || image is null)
    {
        return Results.BadRequest(new ErrorResponse("请上传 .uasset、.uexp 和替换图片；.ubulk 对 inline-only 纹理是可选的。"));
    }

    if (!TextureFormatCatalog.IsSupported(format))
    {
        return Results.BadRequest(new ErrorResponse("请选择有效的压缩格式。"));
    }

    string jobId = Guid.NewGuid().ToString("N");
    string jobRoot = Path.Combine(app.Environment.ContentRootPath, "App_Data", "jobs", jobId);
    string inputRoot = Path.Combine(jobRoot, "input");
    string outputRoot = Path.Combine(jobRoot, "output");
    Directory.CreateDirectory(inputRoot);
    Directory.CreateDirectory(outputRoot);

    try
    {
        string baseName = Path.GetFileNameWithoutExtension(uasset.FileName);
        string uassetPath = Path.Combine(inputRoot, baseName + ".uasset");
        string uexpPath = Path.Combine(inputRoot, baseName + ".uexp");
        string imagePath = Path.Combine(inputRoot, Path.GetFileName(image.FileName));
        string outputAssetPath = Path.Combine(outputRoot, baseName + ".patched.uasset");

        await SaveUploadAsync(uasset, uassetPath, cancellationToken);
        await SaveUploadAsync(uexp, uexpPath, cancellationToken);
        if (ubulk is not null && ubulk.Length > 0)
        {
            await SaveUploadAsync(ubulk, Path.Combine(inputRoot, baseName + ".ubulk"), cancellationToken);
        }

        await SaveUploadAsync(image, imagePath, cancellationToken);

        UAssetCliResult inspectResult = await runner.InspectTextureAsync(uassetPath, engine, cancellationToken);
        if (inspectResult.ExitCode != 0)
        {
            return Results.BadRequest(new ErrorResponse(inspectResult.CombinedOutput));
        }

        string? detectedFormat = TextureFormatCatalog.TryReadFormat(inspectResult.CombinedOutput);
        if (string.IsNullOrWhiteSpace(detectedFormat))
        {
            return Results.BadRequest(new ErrorResponse("无法识别 uasset 的纹理压缩格式。"));
        }

        if (!TextureFormatCatalog.IsSupported(detectedFormat))
        {
            return Results.BadRequest(new ErrorResponse($"当前资源格式 {detectedFormat} 暂不支持网页替换。"));
        }

        string selectedFormat = format;
        format = detectedFormat;

        UAssetCliRequest cliRequest = new(
            uassetPath,
            imagePath,
            outputAssetPath,
            format,
            engine,
            settings.Bc7EncoderPath,
            settings.AstcEncoderPath,
            astcQuality);

        UAssetCliResult cliResult = await runner.ReplaceTextureAsync(cliRequest, cancellationToken);
        if (cliResult.ExitCode != 0)
        {
            return Results.BadRequest(new ErrorResponse(cliResult.CombinedOutput));
        }

        string outputUexpPath = Path.ChangeExtension(outputAssetPath, ".uexp");
        string outputUbulkPath = Path.ChangeExtension(outputAssetPath, ".ubulk");
        string zipPath = Path.Combine(jobRoot, $"{baseName}.patched.zip");
        CreateResultZip(outputAssetPath, zipPath);

        return Results.Ok(new ReplaceResponse(
            jobId,
            $"/api/jobs/{jobId}/download",
            Path.GetFileName(outputAssetPath),
            Path.GetFileName(outputUexpPath),
            File.Exists(outputUbulkPath) ? Path.GetFileName(outputUbulkPath) : null,
            format,
            selectedFormat));
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
});

app.MapGet("/api/jobs/{jobId}/download", async (string jobId, CancellationToken cancellationToken) =>
{
    string jobRoot = GetJobRoot(app.Environment.ContentRootPath, jobId);
    if (!Directory.Exists(jobRoot))
    {
        return Results.NotFound(new ErrorResponse("找不到替换任务。"));
    }

    string? zipPath = Directory.GetFiles(jobRoot, "*.patched.zip").FirstOrDefault();
    if (zipPath is null)
    {
        return Results.NotFound(new ErrorResponse("找不到替换结果。"));
    }

    byte[] zipBytes = await File.ReadAllBytesAsync(zipPath, cancellationToken);
    return Results.File(zipBytes, "application/zip", Path.GetFileName(zipPath));
});

app.MapPost("/api/jobs/{jobId}/pack", async (
    HttpContext context,          // 注入 HttpContext，用于注册回调
    string jobId,
    ModifiedPakRequest request,
    CancellationToken cancellationToken) =>
{
    string outputRoot = Path.Combine(GetJobRoot(app.Environment.ContentRootPath, jobId), "output");
    if (!Directory.Exists(outputRoot))
    {
        return Results.NotFound(new ErrorResponse("找不到替换任务。"));
    }

    if (string.IsNullOrEmpty(request.OutputPakName))
    {
        return Results.BadRequest(new ErrorResponse("请填写输出 Pak 文件名。"));
    }

    if (string.IsNullOrWhiteSpace(request.UassetPakPath) || string.IsNullOrWhiteSpace(request.UexpPakPath))
    {
        return Results.BadRequest(new ErrorResponse("请填写 .uasset 和 .uexp 在 Pak 内的路径。"));
    }

    try
    {
        // 打包到临时文件
        string tempPakPath = await Task.Run(
            () => PakPackService.PackModifiedFiles(outputRoot, request),
            cancellationToken);

        // 浏览器下载时使用的文件名（仅取安全文件名部分）
        string downloadFileName = Path.GetFileName(request.OutputPakName);
        if (string.IsNullOrWhiteSpace(downloadFileName))
            downloadFileName = "patched.pak";

        // 响应发送完成后删除临时文件
        context.Response.OnCompleted(() =>
        {
            try { File.Delete(tempPakPath); } catch { /* 忽略删除失败 */ }
            return Task.CompletedTask;
        });

        // 直接返回文件给前端下载
        return Results.File(tempPakPath, "application/octet-stream", downloadFileName);
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
});

app.Run();

static void SetProperty<T>(T obj, string propertyName, object? value)
{
    // EncoderSettings 使用 init 属性，运行时需要通过反射修改 backing field
    var prop = typeof(T).GetProperty(propertyName);
    if (prop == null) return;

    var backingField = typeof(T).GetField(
        $"<{propertyName}>k__BackingField",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    backingField?.SetValue(obj, value);
}

static string GetJobRoot(string contentRootPath, string jobId)
{
    string safeJobId = Path.GetFileName(jobId);
    return Path.Combine(contentRootPath, "App_Data", "jobs", safeJobId);
}

static async Task SaveUploadAsync(IFormFile file, string path, CancellationToken cancellationToken)
{
    await using FileStream stream = File.Create(path);
    await file.CopyToAsync(stream, cancellationToken);
}

static void CreateResultZip(string outputAssetPath, string zipPath)
{
    string outputUexpPath = Path.ChangeExtension(outputAssetPath, ".uexp");
    string outputUbulkPath = Path.ChangeExtension(outputAssetPath, ".ubulk");

    using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
    archive.CreateEntryFromFile(outputAssetPath, Path.GetFileName(outputAssetPath));
    archive.CreateEntryFromFile(outputUexpPath, Path.GetFileName(outputUexpPath));
    if (File.Exists(outputUbulkPath))
    {
        archive.CreateEntryFromFile(outputUbulkPath, Path.GetFileName(outputUbulkPath));
    }
}

internal sealed record ErrorResponse(string Message);

internal sealed class SettingsUpdateRequest
{
    public string AstcVariant { get; set; } = string.Empty;
}

internal sealed record ReplaceResponse(
    string JobId,
    string DownloadUrl,
    string UassetFileName,
    string UexpFileName,
    string? UbulkFileName,
    string Format,
    string SelectedFormat);

internal sealed class ModifiedPakRequest
{
    public string OutputPakName { get; set; } = string.Empty;

    public string MountPoint { get; set; } = "../../../";

    public string UassetPakPath { get; set; } = string.Empty;

    public string UexpPakPath { get; set; } = string.Empty;

    public string UbulkPakPath { get; set; } = string.Empty;

    public PakVersion Version { get; set; } = PakVersion.V11;

    public bool UseCompression { get; set; }

    public PakCompression Compression { get; set; } = PakCompression.Zlib;
}

internal sealed record UAssetCliRequest(
    string AssetPath,
    string ImagePath,
    string OutputAssetPath,
    string Format,
    string Engine,
    string Bc7EncoderPath,
    string AstcEncoderPath,
    string AstcQuality);

internal sealed record UAssetCliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)));
}

internal sealed class StartupConfig
{
    public string Url { get; init; } = "http://0.0.0.0:5299";

    public required EncoderSettings Settings { get; init; }

    public static StartupConfig LoadOrCreate(IConfiguration configuration, string contentRootPath)
    {
        string appData = Path.Combine(contentRootPath, "App_Data");
        Directory.CreateDirectory(appData);
        string configPath = Path.Combine(appData, "server-settings.json");

        ServerSettingsFile fileSettings = LoadFile(configPath);
        ApplyConfigurationOverrides(fileSettings, configuration);

        EncoderSettings settings = EncoderSettings.FromFileSettings(fileSettings, contentRootPath);
        if (!settings.IsComplete || string.IsNullOrWhiteSpace(fileSettings.Url))
        {
            if (!Environment.UserInteractive)
            {
                throw new InvalidOperationException(
                    $"配置不完整，且当前环境不可交互。请编辑 {configPath} 或使用环境变量/启动参数设置。{Environment.NewLine}{settings.GetConfigurationError()}");
            }

            Console.WriteLine("UAssetTextureWeb 首次启动配置");
            Console.WriteLine($"配置文件: {configPath}");
            fileSettings = PromptForSettings(fileSettings, contentRootPath);
            SaveFile(configPath, fileSettings);
            settings = EncoderSettings.FromFileSettings(fileSettings, contentRootPath);
        }

        if (!settings.IsComplete)
        {
            throw new InvalidOperationException(settings.GetConfigurationError());
        }

        string url = string.IsNullOrWhiteSpace(fileSettings.Url) ? "http://0.0.0.0:5299" : fileSettings.Url;
        Console.WriteLine($"UAssetTextureWeb 将监听: {url}");
        Console.WriteLine($"最大并发编码任务: {settings.MaxParallelJobs}");

        return new StartupConfig
        {
            Url = url,
            Settings = settings,
        };
    }

    private static ServerSettingsFile LoadFile(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return new ServerSettingsFile();
        }

        string json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<ServerSettingsFile>(json, JsonOptions) ?? new ServerSettingsFile();
    }

    private static void SaveFile(string configPath, ServerSettingsFile settings)
    {
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(configPath, json);
        Console.WriteLine("配置已保存。");
    }

    private static void ApplyConfigurationOverrides(ServerSettingsFile settings, IConfiguration configuration)
    {
        settings.Url = FirstNonEmpty(configuration["Server:Url"], configuration["URLS"], configuration["ASPNETCORE_URLS"], settings.Url);
        settings.Bc7EncoderPath = FirstNonEmpty(configuration["Encoders:BC7"], configuration["BC7_ENCODER_PATH"], settings.Bc7EncoderPath);
        settings.AstcEncoderPath = FirstNonEmpty(configuration["Encoders:ASTC"], configuration["ASTC_ENCODER_PATH"], settings.AstcEncoderPath);
        settings.UAssetCliPath = FirstNonEmpty(configuration["UAssetCli:Path"], settings.UAssetCliPath);
        settings.MaxParallelJobs = configuration.GetValue("Jobs:MaxParallel", settings.MaxParallelJobs);
    }

    private static ServerSettingsFile PromptForSettings(ServerSettingsFile current, string contentRootPath)
    {
        string? defaultCli = CliInvocation.FindDefaultCliPath(contentRootPath);
        current.Url = PromptUrl(current.Url);
        current.UAssetCliPath = PromptExistingPath("UAssetCLI 路径 (.exe 或 .dll)", current.UAssetCliPath, defaultCli);
        current.Bc7EncoderPath = PromptExistingPath("BC7 编码器路径 texconv.exe", current.Bc7EncoderPath, null);
        current.AstcEncoderPath = PromptExistingPath("ASTC 编码器路径 astcenc.exe", current.AstcEncoderPath, null);
        current.MaxParallelJobs = PromptInt("最大并发编码任务数", current.MaxParallelJobs <= 0 ? Math.Max(1, Environment.ProcessorCount / 2) : current.MaxParallelJobs, 1, 64);
        return current;
    }

    private static string PromptUrl(string? current)
    {
        string defaultUrl = string.IsNullOrWhiteSpace(current) ? "http://0.0.0.0:5299" : current;
        while (true)
        {
            string value = Prompt("监听地址或端口", defaultUrl);
            if (int.TryParse(value, out int port) && port is > 0 and <= 65535)
            {
                return $"http://0.0.0.0:{port}";
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return value;
            }

            Console.WriteLine("请输入端口号，例如 5299，或完整地址，例如 http://0.0.0.0:5299。");
        }
    }

    private static string PromptExistingPath(string label, string? current, string? fallback)
    {
        string defaultValue = FirstNonEmpty(current, fallback, string.Empty);
        while (true)
        {
            string value = Prompt(label, defaultValue);
            if (File.Exists(value))
            {
                return Path.GetFullPath(value);
            }

            Console.WriteLine("文件不存在，请重新输入。");
        }
    }

    private static int PromptInt(string label, int defaultValue, int min, int max)
    {
        while (true)
        {
            string value = Prompt(label, defaultValue.ToString());
            if (int.TryParse(value, out int parsed) && parsed >= min && parsed <= max)
            {
                return parsed;
            }

            Console.WriteLine($"请输入 {min} 到 {max} 之间的整数。");
        }
    }

    private static string Prompt(string label, string defaultValue)
    {
        Console.Write($"{label} [{defaultValue}]: ");
        string? value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim().Trim('"');
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

internal sealed class ServerSettingsFile
{
    public string Url { get; set; } = "http://0.0.0.0:5299";

    public string UAssetCliPath { get; set; } = string.Empty;

    public string Bc7EncoderPath { get; set; } = string.Empty;

    public string AstcEncoderPath { get; set; } = string.Empty;

    public int MaxParallelJobs { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
}

internal sealed class EncoderSettings
{
    public required string Bc7EncoderPath { get; init; }

    public required string AstcEncoderPath { get; init; }

    public required CliInvocation CliInvocation { get; init; }

    public int MaxParallelJobs { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);

    public bool IsComplete =>
        File.Exists(Bc7EncoderPath)
        && File.Exists(AstcEncoderPath)
        && CliInvocation.IsConfigured;

    public static EncoderSettings FromFileSettings(ServerSettingsFile fileSettings, string contentRootPath)
    {
        // 自动发现编码器路径：如果配置为空，则在 tools/ 子目录中查找
        string bc7Path = fileSettings.Bc7EncoderPath;
        string astcPath = fileSettings.AstcEncoderPath;

        if (string.IsNullOrWhiteSpace(bc7Path))
        {
            bc7Path = FindEncoder(contentRootPath, "texconv.exe") ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(astcPath))
        {
            // 自动选择当前 CPU 支持的 ASTC 编码器版本
            astcPath = FindBestAstcEncoder(contentRootPath) ?? string.Empty;
        }

        return new EncoderSettings
        {
            Bc7EncoderPath = bc7Path,
            AstcEncoderPath = astcPath,
            CliInvocation = CliInvocation.Resolve(fileSettings.UAssetCliPath, contentRootPath),
            MaxParallelJobs = Math.Max(1, fileSettings.MaxParallelJobs),
        };
    }

    /// <summary>
    /// 在内容根目录和 tools/ 子目录中查找编码器可执行文件。
    /// </summary>
    internal static string? FindEncoder(string contentRootPath, string fileName)
    {
        string[] searchPaths =
        [
            Path.Combine(contentRootPath, "tools", fileName),
            Path.Combine(contentRootPath, fileName),
            Path.Combine(AppContext.BaseDirectory, "tools", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
        ];

        return searchPaths.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 根据当前 CPU 支持的指令集自动选择最佳 ASTC 编码器。
    /// 支持 AVX2 时优先 AVX2，否则优先 SSE4.1，最后回退 SSE2。
    /// </summary>
    internal static string? FindBestAstcEncoder(string contentRootPath)
    {
        if (Avx2.IsSupported)
        {
            return FindEncoder(contentRootPath, "astcenc-avx2.exe")
                ?? FindEncoder(contentRootPath, "astcenc-sse4.1.exe")
                ?? FindEncoder(contentRootPath, "astcenc-sse2.exe");
        }

        if (Sse41.IsSupported)
        {
            return FindEncoder(contentRootPath, "astcenc-sse4.1.exe")
                ?? FindEncoder(contentRootPath, "astcenc-sse2.exe")
                ?? FindEncoder(contentRootPath, "astcenc-avx2.exe");
        }

        return FindEncoder(contentRootPath, "astcenc-sse2.exe")
            ?? FindEncoder(contentRootPath, "astcenc-sse4.1.exe")
            ?? FindEncoder(contentRootPath, "astcenc-avx2.exe");
    }

    public string GetConfigurationError()
    {
        List<string> errors = [];
        if (!File.Exists(Bc7EncoderPath))
        {
            errors.Add("BC7 编码器未配置或路径不存在。");
        }

        if (!File.Exists(AstcEncoderPath))
        {
            errors.Add("ASTC 编码器未配置或路径不存在。");
        }

        if (!CliInvocation.IsConfigured)
        {
            errors.Add("UAssetCLI 未配置或路径不存在。");
        }

        return string.Join(Environment.NewLine, errors);
    }
}

internal sealed record CliInvocation(string FileName, IReadOnlyList<string> PrefixArguments, bool IsConfigured)
{
    public static CliInvocation Resolve(string? configuredPath, string contentRootPath)
    {
        string? path = string.IsNullOrWhiteSpace(configuredPath) ? FindDefaultCliPath(contentRootPath) : configuredPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new CliInvocation(string.Empty, [], false);
        }

        path = ResolvePath(path, contentRootPath);
        if (!File.Exists(path))
        {
            return new CliInvocation(string.Empty, [], false);
        }

        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
            ? new CliInvocation("dotnet", [Path.GetFullPath(path)], true)
            : new CliInvocation(Path.GetFullPath(path), [], true);
    }

    public static string? FindDefaultCliPath(string contentRootPath)
    {
        string baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, "UAssetCLI", "UAssetCLI.exe"),
            Path.Combine(baseDirectory, "UAssetCLI", "UAssetCLI.dll"),
            Path.Combine(baseDirectory, "UAssetCLI", "win-x64", "UAssetCLI.exe"),
            Path.Combine(baseDirectory, "UAssetCLI", "win-x64", "UAssetCLI.dll"),
            Path.Combine(contentRootPath, "UAssetCLI.exe"),
            Path.Combine(contentRootPath, "UAssetCLI.dll"),
            Path.Combine(baseDirectory, "UAssetCLI.exe"),
            Path.Combine(baseDirectory, "UAssetCLI.dll"),
            Path.Combine(contentRootPath, "..", "UAssetCLI", "bin", "Release", "net10.0", "UAssetCLI.exe"),
            Path.Combine(contentRootPath, "..", "UAssetCLI", "bin", "Release", "net10.0", "UAssetCLI.dll"),
            Path.Combine(contentRootPath, "..", "UAssetCLI", "bin", "Release", "net10.0", "win-x64", "UAssetCLI.exe"),
            Path.Combine(contentRootPath, "..", "UAssetCLI", "bin", "Release", "net10.0", "win-x64", "UAssetCLI.dll"),
            Path.Combine(contentRootPath, "..", "UAssetCLI", "bin", "Debug", "net10.0", "UAssetCLI.exe"),
            Path.Combine(contentRootPath, "..", "UAssetCLI", "bin", "Debug", "net10.0", "UAssetCLI.dll"),
            Path.Combine(contentRootPath, "..", "UAssetCLI", "bin", "Debug", "net10.0", "win-x64", "UAssetCLI.exe"),
            Path.Combine(contentRootPath, "..", "UAssetCLI", "bin", "Debug", "net10.0", "win-x64", "UAssetCLI.dll"),
        ];

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static string ResolvePath(string path, string contentRootPath)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        string contentRootCandidate = Path.GetFullPath(Path.Combine(contentRootPath, path));
        if (File.Exists(contentRootCandidate))
        {
            return contentRootCandidate;
        }

        string baseDirectoryCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (File.Exists(baseDirectoryCandidate))
        {
            return baseDirectoryCandidate;
        }

        return contentRootCandidate;
    }
}

internal sealed class UAssetCliRunner
{
    private readonly EncoderSettings _settings;
    private readonly SemaphoreSlim _parallelJobs;

    public UAssetCliRunner(EncoderSettings settings)
    {
        _settings = settings;
        _parallelJobs = new SemaphoreSlim(settings.MaxParallelJobs, settings.MaxParallelJobs);
    }

    public async Task<UAssetCliResult> ReplaceTextureAsync(UAssetCliRequest request, CancellationToken cancellationToken)
    {
        await _parallelJobs.WaitAsync(cancellationToken);
        try
        {
            return await RunReplaceCliAsync(request, cancellationToken);
        }
        finally
        {
            _parallelJobs.Release();
        }
    }

    public async Task<UAssetCliResult> InspectTextureAsync(string assetPath, string engine, CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            .._settings.CliInvocation.PrefixArguments,
            "inspect-texture",
            "--asset",
            assetPath,
            "--engine",
            engine,
        ];

        return await RunCliAsync(arguments, cancellationToken);
    }

    private async Task<UAssetCliResult> RunReplaceCliAsync(UAssetCliRequest request, CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            .._settings.CliInvocation.PrefixArguments,
            "replace-texture",
            "--asset",
            request.AssetPath,
            "--source",
            request.ImagePath,
            "--output",
            request.OutputAssetPath,
            "--engine",
            request.Engine,
            "--expected-format",
            request.Format,
        ];

        if (TextureFormatCatalog.IsAstc(request.Format))
        {
            arguments.AddRange(["--astcenc", request.AstcEncoderPath, "--astc-quality", request.AstcQuality]);
        }
        else if (TextureFormatCatalog.RequiresTexconv(request.Format))
        {
            arguments.AddRange(["--texconv", request.Bc7EncoderPath]);
        }

        return await RunCliAsync(arguments, cancellationToken);
    }

    private async Task<UAssetCliResult> RunCliAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _settings.CliInvocation.FileName,
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

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string[] output = await Task.WhenAll(stdoutTask, stderrTask);
        return new UAssetCliResult(process.ExitCode, output[0], output[1]);
    }
}

internal static class TextureFormatCatalog
{
    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        "PF_DXT1",
        "PF_DXT5",
        "PF_BC7",
        "PF_ASTC_4x4",
        "PF_ASTC_5x4",
        "PF_ASTC_5x5",
        "PF_ASTC_6x5",
        "PF_ASTC_6x6",
        "PF_ASTC_8x5",
        "PF_ASTC_8x6",
        "PF_ASTC_8x8",
        "PF_ASTC_10x5",
        "PF_ASTC_10x6",
        "PF_ASTC_10x8",
        "PF_ASTC_10x10",
        "PF_ASTC_12x10",
        "PF_ASTC_12x12",
    };

    public static bool IsSupported(string format) => Formats.Contains(format);

    public static string? TryReadFormat(string cliOutput)
    {
        using StringReader reader = new(cliOutput);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                return line["Format:".Length..].Trim();
            }
        }

        return null;
    }

    public static bool IsAstc(string format) => format.StartsWith("PF_ASTC_", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresTexconv(string format) =>
        string.Equals(format, "PF_DXT5", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, "PF_BC7", StringComparison.OrdinalIgnoreCase);
}

internal static class PakPackService
{
    public static string PackModifiedFiles(string outputRoot, ModifiedPakRequest request)
    {
        // 在系统临时目录生成唯一文件名
        string tempPakPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pak");

        string? uassetPath = Directory.GetFiles(outputRoot, "*.uasset").FirstOrDefault();
        string? uexpPath = Directory.GetFiles(outputRoot, "*.uexp").FirstOrDefault();
        string? ubulkPath = Directory.GetFiles(outputRoot, "*.ubulk").FirstOrDefault();
        if (uassetPath is null || uexpPath is null)
        {
            throw new InvalidOperationException("替换结果缺少 .uasset 或 .uexp。");
        }

        List<(string DiskPath, string PakPath)> files =
        [
            (uassetPath, NormalizePakPath(request.UassetPakPath)),
            (uexpPath, NormalizePakPath(request.UexpPakPath)),
        ];

        if (ubulkPath is not null)
        {
            if (string.IsNullOrWhiteSpace(request.UbulkPakPath))
            {
                throw new InvalidOperationException("替换结果包含 .ubulk，请填写 .ubulk 在 Pak 内的路径。");
            }

            files.Add((ubulkPath, NormalizePakPath(request.UbulkPakPath)));
        }

        // 写入临时文件
        using (FileStream output = File.Create(tempPakPath))
        {
            PakBuilder builder = new();
            if (request.UseCompression)
            {
                builder.Compression([request.Compression]);
            }

            using PakWriter writer = builder.Writer(output, request.Version, NormalizeMountPoint(request.MountPoint));
            foreach ((string diskPath, string pakPath) in files)
            {
                writer.WriteFile(pakPath, File.ReadAllBytes(diskPath));
            }

            writer.WriteIndex();
        }

        return tempPakPath;
    }

    private static string NormalizePakPath(string pakPath)
    {
        string normalized = pakPath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("../", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Pak 内路径无效: {pakPath}");
        }

        return normalized;
    }

    private static string NormalizeMountPoint(string mountPoint)
    {
        string normalized = string.IsNullOrWhiteSpace(mountPoint) ? "../../../" : mountPoint.Replace('\\', '/');
        return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
    }
}
