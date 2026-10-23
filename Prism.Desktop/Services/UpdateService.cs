using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Prism.Desktop.Services;

/// <summary>发行版里的一个可下载文件。</summary>
internal sealed record UpdateAsset(string Name, long Size, string DownloadUrl, string? Sha256)
{
    public string SizeText => FormatBytes(Size);

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "未知大小";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>一次成功的发行版查询结果。</summary>
internal sealed record UpdateInfo(
    string Tag,
    string Version,
    string Title,
    string Notes,
    DateTimeOffset PublishedAt,
    string HtmlUrl,
    IReadOnlyList<UpdateAsset> Assets)
{
    /// <summary>正文按 markdown 解析成的块序列（欢迎页直接渲染它）。</summary>
    public IReadOnlyList<UpdateNote> NoteBlocks => UpdateNotes.Parse(Notes);

    public string PublishedText => PublishedAt.ToLocalTime().ToString("yyyy-MM-dd");

    public string AssetSummary => Assets.Count == 0
        ? "无附件"
        : string.Join(" · ", Assets.Take(3).Select(asset => $"{asset.Name}（{asset.SizeText}）"));
}

/// <summary>查询结果：要么成功（可能没有新版本），要么失败并带一条能显示给用户的原因。</summary>
internal sealed record UpdateCheckResult(bool Succeeded, UpdateInfo? Update, string Message)
{
    public bool HasUpdate => Update is not null;
}

/// <summary>
/// 发行版检查 / 下载。
///
/// 只依赖 GitHub 的公开 REST 接口（不需要 token）：<c>/releases/latest</c> 返回最新的
/// 正式发行版。失败原因一律翻译成中文短句 —— 它会直接显示在设置页上，用户不该看到
/// <c>HttpRequestException</c> 这种字眼。
///
/// 这里刻意不引用任何 UI 类型：解析、版本比较、正文解析都是纯函数，可以在测试里
/// 用固定 JSON 断言，不必真的联网。
/// </summary>
internal static class UpdateService
{
    /// <summary>仓库全名（GitHub API 用）。</summary>
    public const string Repository = "CCB-TEAM/Prism";

    public const string ReleasesPageUrl = "https://github.com/CCB-TEAM/Prism/releases";

    private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // GitHub 要求带 User-Agent；超时短一些，别让启动流程等它。
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Prism-Desktop", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// 查最新发行版。<paramref name="currentVersion"/> 用于判断"是不是真的更新了"；
    /// 传 null 时只要查到就当作有更新（供测试 / 强制展示用）。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(Version? currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Client
                .GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failure(DescribeHttpFailure((int)response.StatusCode));
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            UpdateInfo? info = ParseRelease(json);
            if (info is null)
            {
                return Failure("GitHub 返回的内容看不懂（发行版信息缺字段）。");
            }

            if (!IsNewer(info.Version, currentVersion))
            {
                return new UpdateCheckResult(true, null, $"已是最新版本（v{currentVersion!.ToString(3)}）。");
            }

            return new UpdateCheckResult(true, info, $"发现新版本 v{info.Version}。");
        }
        catch (OperationCanceledException)
        {
            return Failure("检查更新超时，稍后再试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return Failure($"连不上 GitHub：{ex.Message}");
        }
    }

    /// <summary>
    /// 是不是真的比当前版本新。
    ///
    /// 两处"不知道"都按"有更新"处理：当前版本号读不出来、或 tag 解析不了。
    /// 静默不提示的代价比误报一次大得多 —— 用户最多点一次检查，之后就知道结果了。
    /// </summary>
    public static bool IsNewer(string version, Version? currentVersion)
    {
        if (!Version.TryParse(version, out Version? latest))
        {
            return true;
        }

        return currentVersion is null || latest > currentVersion;
    }

    /// <summary>
    /// 把 <c>/releases/latest</c> 的 JSON 解析成 <see cref="UpdateInfo"/>。
    /// 抽成公开方法是为了让测试不必联网（直接喂一段固定 JSON 即可）。
    /// </summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string tag = GetString(root, "tag_name");
        if (tag.Length == 0)
        {
            return null;
        }

        string version = NormalizeVersion(tag);
        var assets = new List<UpdateAsset>();
        if (root.TryGetProperty("assets", out JsonElement assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assetArray.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string name = GetString(asset, "name");
                string url = GetString(asset, "browser_download_url");
                if (name.Length == 0 || url.Length == 0)
                {
                    continue;
                }

                long size = 0;
                if (asset.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsedSize))
                {
                    size = parsedSize;
                }

                // 新版接口会给 digest（"sha256:..."），旧版没有 —— 有就用来校验下载结果。
                string digest = GetString(asset, "digest");
                string? sha256 = null;
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length > 7)
                {
                    sha256 = digest[7..];
                }

                assets.Add(new UpdateAsset(name, size, url, sha256));
            }
        }

        DateTimeOffset published = DateTimeOffset.MinValue;
        if (root.TryGetProperty("published_at", out JsonElement publishedElement) &&
            publishedElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(publishedElement.GetString(), out DateTimeOffset parsedPublished))
        {
            published = parsedPublished;
        }

        string title = GetString(root, "name");
        if (title.Length == 0)
        {
            title = tag;
        }

        return new UpdateInfo(
            Tag: tag,
            Version: version,
            Title: title,
            Notes: GetString(root, "body"),
            PublishedAt: published,
            HtmlUrl: GetString(root, "html_url"),
            Assets: assets);
    }

    /// <summary>tag 归一化：<c>v1.2.3</c> → <c>1.2.3</c>。</summary>
    public static string NormalizeVersion(string tag)
    {
        string trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        return trimmed;
    }

    /// <summary>
    /// 按当前平台挑要下载的文件：Windows 找 <c>win-x64</c> 的 zip，Android 找 apk。
    /// 找不到就返回 null（界面会退回「前往 GitHub 下载」）。
    /// </summary>
    public static UpdateAsset? SelectAsset(UpdateInfo info)
    {
        IEnumerable<UpdateAsset> candidates = info.Assets;
        if (OperatingSystem.IsAndroid())
        {
            return candidates.FirstOrDefault(asset =>
                       asset.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) ??
                   candidates.FirstOrDefault(asset =>
                       asset.Name.Contains("android", StringComparison.OrdinalIgnoreCase));
        }

        return candidates.FirstOrDefault(asset =>
                   asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                   asset.Name.Contains("win", StringComparison.OrdinalIgnoreCase)) ??
               candidates.FirstOrDefault(asset =>
                   asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 下载资产到 <paramref name="destinationPath"/>，按字节回报进度（0~1）。
    /// 若发行版给了 sha256，会顺带校验；不匹配直接抛异常 —— 千万别把一个坏包装到用户机器上。
    /// </summary>
    public static async Task DownloadAsync(
        UpdateAsset asset,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Client
            .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? asset.Size;
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (FileStream file = File.Create(destinationPath))
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[81920];
            long written = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                if (total > 0)
                {
                    progress?.Report(Math.Clamp((double)written / total, 0d, 1d));
                }
            }

            progress?.Report(1d);
        }

        if (asset.Sha256 is { Length: 64 } expected)
        {
            string actual = await ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(destinationPath);
                throw new InvalidDataException("下载的文件校验失败（SHA-256 不匹配），已删除。");
            }
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await System.Security.Cryptography.SHA256
            .HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string DescribeHttpFailure(int statusCode) => statusCode switch
    {
        403 or 429 => "GitHub 拒绝了请求（查询太频繁），过一会儿再试。",
        404 => "仓库里还没有发行版。",
        >= 500 => "GitHub 服务器出错了，稍后再试。",
        _ => $"GitHub 返回 HTTP {statusCode}。",
    };

    private static UpdateCheckResult Failure(string message) => new(false, null, message);

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 删不掉就算了，下次下载会覆盖
        }
    }
}
