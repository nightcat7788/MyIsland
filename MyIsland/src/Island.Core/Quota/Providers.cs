using System.IO;
namespace Island.Core.Quota;

/// <summary>基于本地会话文件的用量 Provider（Claude Code / Codex）。</summary>
public sealed class JsonlUsageProvider : IQuotaProvider
{
    private readonly string[] _roots;

    public string Id { get; }
    public string DisplayName { get; }

    public JsonlUsageProvider(string id, string displayName, params string[] roots)
    {
        Id = id;
        DisplayName = displayName;
        _roots = roots;
    }

    public ProviderUsage Read()
    {
        var files = _roots.SelectMany(r => LocalUsageScanner.EnumerateJsonl(r)).ToList();
        bool installed = _roots.Any(r => !string.IsNullOrEmpty(r) && Directory.Exists(r));
        if (!installed)
            return new ProviderUsage { Id = Id, DisplayName = DisplayName, Status = ProviderStatus.NotInstalled };

        var totals = LocalUsageScanner.Scan(files);
        if (totals.Sessions == 0)
            return new ProviderUsage { Id = Id, DisplayName = DisplayName, Status = ProviderStatus.NoData };

        return new ProviderUsage
        {
            Id = Id,
            DisplayName = DisplayName,
            Status = ProviderStatus.Connected,
            Plan = "本地用量",
            TokensToday = totals.Today,
            CostToday = totals.CostToday,
            UsedPercent = DailyShare(totals),
            Detail = $"今日 {FormatTokens(totals.Today)} · 7日 {FormatTokens(totals.Week)}",
        };
    }

    /// <summary>
    /// 配额环填充度：本地 JSONL 没有真实配额接口，用「今日 / 近 7 日日均」的相对强度
    /// （100 封顶）驱动收起态配额环——读不到真实额度时绝不编造（与 BrimDeck 同原则）。
    /// </summary>
    private static double DailyShare(UsageTotals t)
    {
        if (t.Week <= 0) return 0;
        double avg = t.Week / 7.0;
        return Math.Clamp(t.Today / Math.Max(avg, 1) * 100, 0, 100);
    }

    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1e6:F1}M" : n >= 1000 ? $"{n / 1000:F0}K" : n.ToString();
}

/// <summary>预留的在线配额 Provider 桩：不伪造数据，明确告诉用户当前版本只统计本地用量。</summary>
public sealed class PlaceholderProvider : IQuotaProvider
{
    public string Id { get; }
    public string DisplayName { get; }

    public PlaceholderProvider(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public ProviderUsage Read() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Status = ProviderStatus.NoData,
        Detail = "在线配额待接入（无需伪造数字）",
    };
}
