using System;

namespace VideoCountMilli;

/// <summary>
/// 将毫秒数格式化为 HH:MM:SS.mmm 形式（精确到毫秒）。
/// </summary>
public static class TimeFormatter
{
    /// <summary>
    /// 将毫秒转换为 HH:MM:SS.mmm 字符串。
    /// </summary>
    /// <param name="totalMilliseconds">总毫秒数</param>
    /// <returns>格式化字符串，例如 00:29:59.128</returns>
    public static string Format(long totalMilliseconds)
    {
        if (totalMilliseconds < 0)
        {
            totalMilliseconds = 0;
        }

        long hours = totalMilliseconds / 3_600_000;
        long minutes = (totalMilliseconds % 3_600_000) / 60_000;
        long seconds = (totalMilliseconds % 60_000) / 1_000;
        long millis = totalMilliseconds % 1_000;

        return $"{hours:00}:{minutes:00}:{seconds:00}.{millis:000}";
    }

    /// <summary>
    /// 将 TimeSpan 转换为 HH:MM:SS.mmm 字符串。
    /// </summary>
    public static string Format(TimeSpan ts)
    {
        return Format((long)ts.TotalMilliseconds);
    }

    /// <summary>
    /// 从 HH:MM:SS.mmm 字符串解析毫秒数。
    /// </summary>
    public static bool TryParse(string text, out long totalMilliseconds)
    {
        totalMilliseconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out int hours) ||
            !int.TryParse(parts[1], out int minutes))
        {
            return false;
        }

        // seconds part may contain millis: SS.mmm
        var secParts = parts[2].Split('.');
        if (!int.TryParse(secParts[0], out int seconds))
        {
            return false;
        }

        int millis = 0;
        if (secParts.Length > 1)
        {
            string msStr = secParts[1].PadRight(3, '0')[..3];
            int.TryParse(msStr, out millis);
        }

        totalMilliseconds = hours * 3_600_000L + minutes * 60_000L + seconds * 1_000L + millis;
        return true;
    }

    /// <summary>
    /// 将毫秒格式化为可用于文件名的紧凑形式，例如 00-29-59-128。
    /// </summary>
    public static string FormatForFileName(long totalMilliseconds)
    {
        return Format(totalMilliseconds).Replace(':', '-').Replace('.', '-');
    }
}
