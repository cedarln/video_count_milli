using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoCountMilli;

/// <summary>
/// 检查 GitHub 最新 Release 并下载更新包。
/// </summary>
public static class Updater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/cedarln/video_count_milli/releases/latest";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VideoCountMilli-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>最新 Release 信息。</summary>
    public sealed record ReleaseInfo(string Tag, Version Version, string AssetUrl, long AssetSize);

    /// <summary>
    /// 获取最新 Release 信息；网络错误时抛出异常，无可用安装包时返回 null。
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync()
    {
        using var resp = await Http.GetAsync(LatestReleaseApi).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        Version? version = ParseVersion(tag);
        if (version == null) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
            {
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                long size = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                if (url.Length > 0)
                {
                    return new ReleaseInfo(tag, version, url, size);
                }
            }
        }
        return null;
    }

    /// <summary>解析形如 v1.4.0 / 1.4.0 的标签为版本号。</summary>
    public static Version? ParseVersion(string tag)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        int plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        return Version.TryParse(t, out var v) ? v : null;
    }

    /// <summary>下载文件到指定路径，progress 报告 0-100 的百分比。</summary>
    public static async Task DownloadFileAsync(string url, string destPath, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, useAsync: true);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            if (total > 0)
            {
                progress?.Report(downloaded * 100.0 / total);
            }
        }
    }
}
