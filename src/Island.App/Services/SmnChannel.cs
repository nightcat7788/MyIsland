using System;

namespace Island.App.Services;

/// <summary>
/// AMD SMN 直读（CPU 温度）通道的**唯一开关与生命周期所有者**。
///
/// 为什么单独成文件（2026-11-23 三次 0x1EA 蓝屏之后的直接整改）：
///   事故链是"以管理员运行 → 首次温度采样 → 加载 inpoutx64.dll → 装未签名内核驱动
///   → 映射 PCIe ECAM 物理内存 → 读挂总线 → 蓝屏"。事故的**触发点**是
///   "首次温度采样时到底要不要构造 SMN 服务"。
///   以前这个判断写在 <see cref="ThermalService"/> 内部，而测试工程没有链接
///   ThermalService.cs ⇒ 门禁根本走不到这段代码 ⇒ 把
///   <c>if (AllowSmnDirectRead &amp;&amp; _smu is null)</c> 里的条件改掉，
///   测试仍然全绿（假绿）。
///   现在决策集中在本类：测试工程链接本文件即可断言
///   "未显式开启 ⇒ 绝不构造 SMN 服务"，回归会被门禁挡住。
///
/// 铁律（别再加回来）：
///   - 未显式开启 ⇒ 不创建服务、不 LoadLibrary、不装驱动、不映射物理内存；
///   - 关闭开关 ⇒ **立即**释放已映射的物理区与已加载的 dll
///     （否则用户以为"已关闭"，其实驱动还装在系统里、映射还在进程地址空间）；
///   - "是不是 AMD"的判定在 <see cref="InpOutSmnService"/> 内部，
///     必须在任何驱动/物理内存操作**之前**完成。
/// </summary>
public sealed class SmnChannel : IDisposable
{
    private readonly object _gate = new();
    private InpOutSmnService? _svc;
    private bool _allow;

    /// <summary>是否允许创建 SMN 直读服务。默认 false。</summary>
    public bool AllowSmnDirectRead
    {
        get { lock (_gate) return _allow; }
        set
        {
            lock (_gate)
            {
                if (_allow == value) return;
                _allow = value;
                // 关闭 ⇒ 立刻回收：已映射的 ECAM 物理区与已加载的 inpoutx64.dll。
                // 不回收的话，开关只是"不再用"，机器上的未签名驱动与进程里的映射都还在。
                if (!value) ReleaseLocked();
            }
        }
    }

    /// <summary>服务是否已创建（诊断/测试用）。</summary>
    public bool IsCreated
    {
        get { lock (_gate) return _svc is not null; }
    }

    /// <summary>
    /// 取 SMN 服务；**未显式开启时返回 null，绝不构造**。
    /// 这是"温度采样是否触碰未签名驱动"的唯一入口。
    /// </summary>
    public InpOutSmnService? Ensure()
    {
        lock (_gate)
        {
            if (!_allow) return null;          // ← 关键闸门：未开启 ⇒ 不构造
            _svc ??= new InpOutSmnService();
            return _svc;
        }
    }

    /// <summary>
    /// 首次启动的自动开启策略已废弃：SMN 会加载未签名内核驱动并映射物理内存，
    /// 不能因 CPU 厂商或温度缺失替用户做高风险决定。保留纯函数仅为旧测试与配置迁移兼容，
    /// 永远返回 false；真正启用只能来自设置中心的主动确认。
    /// </summary>
    public static bool AutoEnableOnFirstRun(bool isAmdCpu) => false;

    /// <summary>通道状态文本（诊断日志用）。</summary>
    public string Status
    {
        get
        {
            lock (_gate)
            {
                if (_svc is not null) return _svc.Status;
                return _allow ? "not tried" : "disabled (EnableSmnDirectRead=off)";
            }
        }
    }

    /// <summary>释放已创建的服务（关闭开关或 Dispose 时调用）。</summary>
    private void ReleaseLocked()
    {
        var svc = _svc;
        _svc = null;
        try { svc?.Dispose(); } catch { /* 释放失败不影响运行 */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _allow = false;
            ReleaseLocked();
        }
    }
}
