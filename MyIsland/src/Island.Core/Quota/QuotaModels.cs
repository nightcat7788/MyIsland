namespace Island.Core.Quota;

/// <summary>配额来源的连接状态。读不到就如实显示，绝不编造数字。</summary>
public enum ProviderStatus
{
    Connected,     // 成功读到用量/配额
    NoData,        // 工具已安装但没有会话记录
    NotInstalled,  // 本地找不到该工具
    Error,         // 读取出错
}

/// <summary>一个 AI 工具的配额卡片数据。</summary>
public sealed record ProviderUsage
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ProviderStatus Status { get; init; }
    public string Plan { get; init; } = "";

    /// <summary>5 小时窗口已用百分比 0..100（可为 null 表示未知）。</summary>
    public double? UsedPercent { get; init; }
    /// <summary>重置倒计时文本，如 "2小时13分"。</summary>
    public string ResetIn { get; init; } = "";

    /// <summary>今日 token 数。</summary>
    public long TokensToday { get; init; }
    /// <summary>今日估算费用（美元）。</summary>
    public double CostToday { get; init; }

    public string Detail { get; init; } = "";
    public DateTime UpdatedAt { get; init; } = DateTime.Now;

    public string StatusText => Status switch
    {
        ProviderStatus.Connected => Plan,
        ProviderStatus.NoData => "暂无用量",
        ProviderStatus.NotInstalled => "未安装",
        _ => "读取失败",
    };
}

/// <summary>可插拔配额提供程序接口。新增工具只需实现它并注册到 QuotaService。</summary>
public interface IQuotaProvider
{
    string Id { get; }
    string DisplayName { get; }
    ProviderUsage Read();
}

/// <summary>配额聚合结果快照。</summary>
public sealed record QuotaSnapshot
{
    public IReadOnlyList<ProviderUsage> Providers { get; init; } = Array.Empty<ProviderUsage>();
    public DateTime UpdatedAt { get; init; } = DateTime.Now;
}
