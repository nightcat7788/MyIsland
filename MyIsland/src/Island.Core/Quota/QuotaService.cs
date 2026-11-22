using System.IO;
namespace Island.Core.Quota;

/// <summary>
/// 配额聚合服务：注册若干 Provider，并行读取后产出不可变快照。
/// 新增工具只需 providers.Add(new YourProvider())，UI 无需改动。
/// </summary>
public sealed class QuotaService
{
    private readonly List<IQuotaProvider> _providers = new();
    public IReadOnlyList<IQuotaProvider> Providers => _providers;

    public event Action<QuotaSnapshot>? Updated;

    public QuotaService()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Claude Code: ~/.claude/projects/**/*.jsonl
        Register(new JsonlUsageProvider("claude", "Claude", Path.Combine(home, ".claude", "projects")));
        // Codex: ~/.codex/sessions/**/*.jsonl
        Register(new JsonlUsageProvider("codex", "Codex", Path.Combine(home, ".codex", "sessions")));
        // Cursor 在线配额读取较复杂（需解密 state.vscdb），先留桩，不伪造数字
        Register(new PlaceholderProvider("cursor", "Cursor"));
    }

    public void Register(IQuotaProvider provider) => _providers.Add(provider);

    public QuotaSnapshot Refresh()
    {
        var results = new ProviderUsage[_providers.Count];
        Parallel.For(0, _providers.Count, i =>
        {
            try { results[i] = _providers[i].Read(); }
            catch (Exception ex)
            {
                results[i] = new ProviderUsage
                {
                    Id = _providers[i].Id,
                    DisplayName = _providers[i].DisplayName,
                    Status = ProviderStatus.Error,
                    Detail = ex.Message,
                };
            }
        });
        var snap = new QuotaSnapshot { Providers = results };
        Updated?.Invoke(snap);
        return snap;
    }
}
