using System.IO;
using System.Globalization;
using System.Text.Json;

namespace Island.Core.Quota;

/// <summary>
/// 扫描本地 AI 工具会话目录（JSONL）统计 token 用量的通用逻辑。
/// Claude Code 存 ~/.claude/projects/**/*.jsonl，Codex 存 ~/.codex/sessions/**/*.jsonl。
/// 只读，不修改任何工具的数据。
/// </summary>
public static class LocalUsageScanner
{
    /// <summary>从一批 JSONL 文件里统计今天/近 7 天的 token 数。</summary>
    public static UsageTotals Scan(IEnumerable<string> jsonlFiles, DateTime? since = null)
    {
        DateTime today = DateTime.Today;
        DateTime weekAgo = today.AddDays(-7);
        long tokensToday = 0, tokensWeek = 0;
        double costToday = 0, costWeek = 0;
        int sessions = 0;
        var cutoff = since ?? DateTime.MinValue;

        foreach (var file in jsonlFiles)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.LastWriteTime < weekAgo) continue;

                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    (long tokens, double cost, DateTime ts)? parsed = null;
                    try { parsed = ParseLine(line); } catch { /* 单行解析失败跳过 */ }
                    if (parsed is null) continue;
                    var (tok, c, ts) = parsed.Value;
                    if (ts < cutoff) continue;

                    if (ts >= weekAgo)
                    {
                        tokensWeek += tok; costWeek += c;
                        if (ts >= today) { tokensToday += tok; costToday += c; }
                    }
                    sessions++;
                }
            }
            catch { /* 文件被占用/权限不足：跳过 */ }
        }
        return new UsageTotals(tokensToday, tokensWeek, costToday, costWeek, sessions);
    }

    /// <summary>
    /// 从一行 JSONL 里尽力解析 timestamp 与 usage。
    /// 兼容 Claude Code 的 {"timestamp":..,"message":{"usage":{..}}} 与
    /// Codex 的 {"timestamp":..,"info":{"token_info":{..}}} 结构。
    /// </summary>
    private static (long, double, DateTime)? ParseLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        DateTime ts = DateTime.MinValue;
        if (root.TryGetProperty("timestamp", out var tsEl))
        {
            string? s = tsEl.ValueKind == JsonValueKind.String ? tsEl.GetString() : null;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                ts = parsed.ToLocalTime();
        }
        if (ts == DateTime.MinValue) return null;

        long inTok = 0, outTok = 0, cacheRead = 0, cacheWrite = 0;
        JsonElement? usage = null;
        if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object &&
            msg.TryGetProperty("usage", out var u)) usage = u;
        if (usage is null && root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("token_info", out var ti)) usage = ti;
        if (usage is null && root.TryGetProperty("usage", out var rootUsage)) usage = rootUsage;
        if (usage is null) return null;

        var ue = usage.Value;
        inTok = GetInt(ue, "input_tokens");
        outTok = GetInt(ue, "output_tokens");
        cacheRead = GetInt(ue, "cache_read_input_tokens");
        cacheWrite = GetInt(ue, "cache_creation_input_tokens");
        long total = inTok + outTok + cacheRead + cacheWrite;
        if (total <= 0) return null;

        // 粗略估价：不同模型价格不同，这里只做量级提示，UI 会标注"估算"
        double cost = (inTok / 1e6 * 3.0) + (outTok / 1e6 * 15.0) +
                      (cacheRead / 1e6 * 0.3) + (cacheWrite / 1e6 * 3.75);
        return (total, cost, ts);
    }

    private static long GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    /// <summary>递归枚举目录里所有 .jsonl 文件。</summary>
    public static IEnumerable<string> EnumerateJsonl(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}

public sealed record UsageTotals(long Today, long Week, double CostToday, double CostWeek, int Sessions);
