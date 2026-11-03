using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
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

    /// <summary>最新 Release 信息；Sha256 为 GitHub 提供的资产摘要（小写十六进制，可能为 null）。</summary>
    public sealed record ReleaseInfo(string Tag, Version Version, string AssetUrl, long AssetSize, string? Sha256);

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
                string? digest = asset.TryGetProperty("digest", out var dg) ? dg.GetString() : null;
                if (url.Length > 0)
                {
                    return new ReleaseInfo(tag, version, url, size, NormalizeDigest(digest));
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

    /// <summary>
    /// 生成覆盖安装的 PowerShell 脚本：等本进程退出 → 覆盖程序目录 → 清理临时目录 → 重启。
    /// 复制与清理各自独立容错，且重启无条件放在最后：只要清理和复制共用一个 try，
    /// 清理失败就会连带跳过重启，程序关掉后不再打开。
    /// </summary>
    public static string BuildInstallScript(string sourceDir, string appDir, string tempDir, string exePath)
    {
        return
            "param([int]$TargetProcId)\r\n" +
            "$ErrorActionPreference = 'Stop'\r\n" +
            "$p = Get-Process -Id $TargetProcId -ErrorAction SilentlyContinue\r\n" +
            "if ($p) { $p.WaitForExit(15000) | Out-Null }\r\n" +
            "try {\r\n" +
            $"    Copy-Item -Path (Join-Path '{EscapePs(sourceDir)}' '*') -Destination '{EscapePs(appDir)}' -Recurse -Force\r\n" +
            "} catch {\r\n" +
            $"    $_ | Out-File (Join-Path '{EscapePs(appDir)}' 'update_error.log') -Encoding utf8\r\n" +
            "}\r\n" +
            "try {\r\n" +
            $"    Remove-Item -Path '{EscapePs(tempDir)}' -Recurse -Force\r\n" +
            "} catch {\r\n" +
            "    # 临时目录清理失败不影响安装结果\r\n" +
            "}\r\n" +
            // 覆盖失败也要重启：至少还能用旧版本，错误已写进 update_error.log
            $"Start-Process -FilePath '{EscapePs(exePath)}'\r\n";
    }

    /// <summary>PowerShell 单引号字符串内的单引号需翻倍。</summary>
    private static string EscapePs(string s) => s.Replace("'", "''");

    /// <summary>
    /// 把 GitHub 资产摘要（形如 "sha256:abcd…"）归一为小写十六进制；
    /// 缺失、算法不是 sha256 或长度异常时返回 null，表示无法做哈希校验。
    /// </summary>
    public static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;

        string s = digest.Trim();
        int colon = s.IndexOf(':');
        if (colon >= 0)
        {
            if (!string.Equals(s[..colon].Trim(), "sha256", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            s = s[(colon + 1)..];
        }

        s = s.Trim().ToLowerInvariant();
        if (s.Length != 64) return null;
        foreach (char c in s)
        {
            if (!Uri.IsHexDigit(c)) return null;
        }
        return s;
    }

    /// <summary>计算文件的 SHA256，返回小写十六进制字符串。</summary>
    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct = default)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var hasher = SHA256.Create();
        byte[] hash = await hasher.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
