using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Island.Native;

/// <summary>
/// 单查询的 PDH 通配符计数器读取器。
///
/// 为什么不用 `PerformanceCounter`：.NET 的每个 PerformanceCounter 自带一个 PDH 查询，
/// 一次 `NextValue()` 就要单独 collect 一次。本机实测：
///   · "GPU Engine" 共 616 个实例，其中 engtype_3D 139 个 → 逐个读一轮 **167 ms**（每秒一轮 = 17% 单核）
///   · "Processor" 14 个逻辑核 → 每秒 14 次
/// 改成"一个查询 + 一次数组读"后，一轮只剩一次 collect + 一次 `PdhGetFormattedCounterArray`（实测毫秒级）。
///
/// 顺带修掉一个正确性问题：旧实现只在启动时枚举实例列表，**启动之后新开的应用
/// （新的 GPU 引擎实例）永远读不到**，GPU 占用会偏低；通配符查询每次拿到的都是当前实例集合。
///
/// 速率计数器（Utilization Percentage）需要两次采集才有值：Open 时先 collect 一次做基线，
/// 之后每次读再 collect 一次，差分即为速率。
/// 一个查询只在一个线程里用（采样线程），不是线程安全的。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class PdhWildcardQuery : IDisposable
{
    private IntPtr _query;
    private IntPtr _counter;
    private IntPtr _buffer;
    private uint _capacity;

    public bool IsOpen => _query != IntPtr.Zero && _counter != IntPtr.Zero;

    /// <summary>打开查询并加入一个通配符计数器路径（英文计数器名，如 `\GPU Engine(*)\Utilization Percentage`）。</summary>
    public bool Open(string wildcardPath)
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
        }
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return false; }
        if (PdhAddEnglishCounterW(_query, wildcardPath, IntPtr.Zero, out _counter) != 0)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero; _counter = IntPtr.Zero;
            return false;
        }
        // 先采一次做基线：速率计数器要两次采集才有值
        PdhCollectQueryData(_query);
        return true;
    }

    /// <summary>
    /// 采集一轮，把"实例名包含 needle"的数值求和（零分配：直接在宽字符串里找子串）。
    /// 读不到任何有效值时返回 0（与旧实现"读不到就显示 0"的行为一致）。
    /// </summary>
    public double ReadSum(string needle, double cap)
    {
        if (!IsOpen) return 0;
        if (PdhCollectQueryData(_query) != 0) return 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = _capacity;
            uint status = PdhGetFormattedCounterArrayW(_counter, FormatDouble | FormatNoCap100, ref size, out uint count, _buffer);
            if (status == MoreData) { Grow(size + 4096); continue; }
            if (status != 0) return 0;
            double sum = 0;
            for (uint i = 0; i < count; i++)
            {
                int at = (int)(i * ItemSize);
                uint itemStatus = (uint)Marshal.ReadInt32(_buffer, at + StatusOffset);
                if (itemStatus is not (ValidData or NewData)) continue;
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(_buffer, at + ValueOffset));
                if (!double.IsFinite(value)) continue;
                if (!NameHas(Marshal.ReadIntPtr(_buffer, at), needle)) continue;
                sum += value;
            }
            return Math.Clamp(sum, 0, cap);
        }
        return 0;
    }

    /// <summary>
    /// 采集一轮，把"实例名包含 needle"的数值按 **(适配器, 引擎号)** 分组，
    /// 组内跨进程**求和**，组间再取 max 返回。
    /// 微软口径（DirectX 博客 "GPUs in the Task Manager"）：任务管理器的 GPU%
    /// = **最忙引擎**的利用率 —— 同一个 (适配器, 引擎) 上各进程的份额要相加，
    /// 多张卡之间再取最大。
    /// 旧实现只按适配器分组、组内取**单进程** max ⇒ 比官方口径低约 2 倍
    /// （实测 13.96 vs 24.18 / 19.02 vs 37.39 / 18.39 vs 34.92）。
    /// 零分配：分组键是实例名里的 luid 两段 + 引擎号，按字符比较，不解码字符串。
    /// </summary>
    public double ReadMaxByAdapter(string needle, double cap)
    {
        if (!IsOpen) return 0;
        if (PdhCollectQueryData(_query) != 0) return 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = _capacity;
            uint status = PdhGetFormattedCounterArrayW(_counter, FormatDouble | FormatNoCap100, ref size, out uint count, _buffer);
            if (status == MoreData) { Grow(size + 4096); continue; }
            if (status != 0) return 0;
            // ★ 轮次重置：每轮开头**无条件**清空分组（累加器跨轮不清零 = 旧的"恒 100%"根因）。
            //   旧实现把重置放在 AddItem 内部、靠轮次令牌惰性触发 ⇒ 本轮一个有效项都没有时
            //   分组状态残留上一轮，"停更"看起来却像正常读数。
            BeginRound();
            for (uint i = 0; i < count; i++)
            {
                int at = (int)(i * ItemSize);
                uint itemStatus = (uint)Marshal.ReadInt32(_buffer, at + StatusOffset);
                if (itemStatus is not (ValidData or NewData)) continue;
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(_buffer, at + ValueOffset));
                if (!double.IsFinite(value)) continue;
                AddItem(Marshal.ReadIntPtr(_buffer, at), value, needle);
            }
            return Math.Clamp(GroupBest(), 0, cap);
        }
        return 0;
    }

    // 分组用的键：实例名里适配器 luid 片段 + 引擎号的位置
    // _groupName 存该组代表实例的名字指针 —— 键是"相对名字的偏移"，必须按内容比较
    // 键加上引擎号之后组数 = 适配器数 × 每卡引擎数（本机 2 卡 × 8 引擎 = 16 组），上限 64 组。
    // 超出上限的分组直接忽略：宁可少计，也绝不能把不同 (适配器,引擎) 的份额串成一组。
    private const int MaxGroups = 64;
    private readonly IntPtr[] _groupName = new IntPtr[MaxGroups];
    private readonly int[] _groupAdStart = new int[MaxGroups];    // 适配器键（luid 两段）在名字里的起点
    private readonly int[] _groupAdLen = new int[MaxGroups];
    private readonly int[] _groupEngStart = new int[MaxGroups];  // 引擎号在名字里的起点
    private readonly int[] _groupEngLen = new int[MaxGroups];
    private readonly double[] _groupSum = new double[MaxGroups]; // 组内各进程份额之和
    private int _groupCount;
    private int _roundSeq;       // 轮次序号（每次采集 +1，仅作诊断）
    private double _unparsedMax;  // 解析不出 (适配器,引擎) 的实例：单独一桶取 max，不并进任何组

    /// <summary>
    /// 把一个实例的份额累加到它所属的 (适配器, 引擎) 分组。
    /// 轮次重置由 BeginRound() 负责，这里只做累加。
    /// </summary>
    private void AddItem(IntPtr name, double value, string needle)
    {
        if (!NameHas(name, needle)) return;
        // 解析不出适配器或引擎号的实例：不并进任何组（并进去就是"串组"），
        // 单独取 max 记在 _unparsedMax 里 —— 至少不丢读数，也不污染别的组。
        if (!TryGetAdapterKey(name, out int adStart, out int adLen)
            || !TryGetEngineKey(name, out int engStart, out int engLen))
        {
            if (_unparsedMax < value) _unparsedMax = value;
            return;
        }
        int g = -1;
        for (int k = 0; k < _groupCount; k++)
            if (KeyEquals(name, adStart, adLen, _groupName[k], _groupAdStart[k], _groupAdLen[k])
                && KeyEquals(name, engStart, engLen, _groupName[k], _groupEngStart[k], _groupEngLen[k]))
            { g = k; break; }
        if (g < 0)
        {
            if (_groupCount >= MaxGroups) return;   // 分组槽位用尽：忽略多余分组（别崩、别串组）
            g = _groupCount++;
            _groupName[g] = name;
            _groupAdStart[g] = adStart; _groupAdLen[g] = adLen;
            _groupEngStart[g] = engStart; _groupEngLen[g] = engLen;
        }
        _groupSum[g] += value;   // 同一 (适配器,引擎) 上各进程的份额相加（微软口径）
    }

    /// <summary>一轮开始：清空分组状态（每轮开头无条件调用，本轮无有效项时也不留旧值）。</summary>
    private void BeginRound()
    {
        _roundSeq++;
        _groupCount = 0;
        _unparsedMax = 0;
        Array.Clear(_groupSum, 0, _groupSum.Length);
    }

    /// <summary>所有分组里的最大值（组内已求和，组间取 max）。</summary>
    private double GroupBest()
    {
        double best = _unparsedMax;
        for (int k = 0; k < _groupCount; k++) best = Math.Max(best, _groupSum[k]);
        return best;
    }

    /// <summary>
    /// 比较两段等长文本。必须按**内容**比，不能比偏移：实例名以 pid_ 开头，
    /// pid 位数不同 ⇒ 同一张卡的键偏移量也不同，比偏移会把同一张卡拆成一堆组。
    /// </summary>
    private static bool KeyEquals(IntPtr text1, int start1, int len1, IntPtr text2, int start2, int len2)
    {
        if (len1 != len2) return false;
        for (int i = 0; i < len1; i++)
        {
            char a = (char)Marshal.ReadInt16(text1, (start1 + i) * 2);
            char b = (char)Marshal.ReadInt16(text2, (start2 + i) * 2);
            if (a != b) return false;
        }
        return true;
    }

    /// <summary>
    /// 取实例名里适配器 luid 片段的位置。真实实例名形如
    /// pid_1128_luid_0x00000000_0x00010c47_phys_0_eng_0_engtype_3D
    /// —— "luid" 之后第 1 段是 LUID 高位、第 2 段（低位）才是适配器标识，
    /// 键取这两段合起来（含中间的 '_'）才能区分不同显卡。
    /// 注意名字以 pid_ 开头：旧实现假设名字以 "luid_" 开头（少算两段），
    /// 结果所有实例都拿到同一个键 "luid"，分组彻底失效。
    /// </summary>
    private static bool TryGetAdapterKey(IntPtr text, out int start, out int len)
    {
        start = 0; len = 0;
        if (text == IntPtr.Zero) return false;
        // 先定位 "luid_"
        int i = 0;
        for (; ; i++)
        {
            char c = (char)Marshal.ReadInt16(text, i * 2);
            if (c == '\0') return false;
            if (c != 'l' || !IsAscii(text, i, "luid")) continue;
            if ((char)Marshal.ReadInt16(text, (i + 4) * 2) != '_') continue;
            i += 5;                     // 跳过 "luid_"
            break;
        }
        int segStart = i;               // 键起点 = LUID 高位段起点
        int underscores = 0;
        for (; ; i++)
        {
            char c = (char)Marshal.ReadInt16(text, i * 2);
            if (c == '\0') return false;
            if (c != '_') continue;
            underscores++;
            // 第 1 个 '_' 结束 LUID 高位，第 2 个 '_' 结束低位（适配器标识）
            if (underscores == 2) { start = segStart; len = i - segStart; return len > 0; }
        }
    }

    /// <summary>
    /// 取实例名里引擎号的范围。真实实例名形如
    /// pid_1128_luid_0x00000000_0x00010c47_phys_0_eng_0_engtype_3D
    /// —— "eng_" 后面紧跟引擎序号（eng_0 / eng_1 ...），序号决定"是不是同一个引擎"。
    /// 键只取数字段（"eng_" 前缀固定，不参与比较）。
    /// 没有 "eng_" 段、或 "eng_" 后面不是数字 ⇒ 解析失败（调用方跳过该实例，不并入任何组）。
    /// </summary>
    private static bool TryGetEngineKey(IntPtr text, out int start, out int len)
    {
        start = 0; len = 0;
        if (text == IntPtr.Zero) return false;
        for (int i = 0; ; i++)
        {
            char c = (char)Marshal.ReadInt16(text, i * 2);
            if (c == '\0') return false;
            // 注意：不能把 "engtype_3D" 里的 "eng" 当成引擎段（它的第 4 个字符是 't' 不是 '_'）
            if (c != 'e' || !IsAscii(text, i, "eng_")) continue;
            i += 4;                       // 跳过 "eng_"
            int numStart = i;
            while (true)
            {
                char d = (char)Marshal.ReadInt16(text, i * 2);
                if (d < '0' || d > '9') break;
                i++;
            }
            if (i == numStart) return false;   // "eng_" 后面没有序号
            start = numStart; len = i - numStart;
            return true;
        }
    }

    /// <summary>宽字符串的 pos 处是否匹配给定的 ASCII 片段。</summary>
    private static bool IsAscii(IntPtr text, int pos, string ascii)
    {
        for (int k = 0; k < ascii.Length; k++)
            if ((char)Marshal.ReadInt16(text, (pos + k) * 2) != ascii[k]) return false;
        return true;
    }

    /// <summary>
    /// 测试钩子：用合成的实例名/数值跑一遍与运行期完全相同的分组聚合逻辑（不碰 PDH）。
    /// 口径（与运行期一致）：按 (适配器, 引擎号) 分组 → 组内各进程份额**求和** → 组间取 max。
    /// </summary>
    internal static double AggregateForTest(string[] names, double[] values, string needle)
    {
        var q = new PdhWildcardQuery();
        IntPtr buf = IntPtr.Zero;
        try
        {
            var ptrs = BuildNameBuffer(names, out buf);
            q.BeginRound();
            for (int i = 0; i < names.Length; i++) q.AddItem(ptrs[i], values[i], needle);
            return q.GroupBest();
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// 测试钩子：同一个查询对象连续跑多轮聚合（模拟连续采样），返回每轮结果。
    /// 用来守住"第二轮不会把第一轮累加进来"这条不变量。
    /// </summary>
    internal static double[] AggregateRoundsForTest(string[] names, double[] values, string needle, int rounds)
    {
        var q = new PdhWildcardQuery();
        IntPtr buf = IntPtr.Zero;
        try
        {
            var ptrs = BuildNameBuffer(names, out buf);
            var res = new double[rounds];
            for (int r = 0; r < rounds; r++)
            {
                q.BeginRound();     // 每轮开头无条件清空分组（口径同运行期）
                for (int i = 0; i < names.Length; i++) q.AddItem(ptrs[i], values[i], needle);
                res[r] = q.GroupBest();
            }
            return res;
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    /// <summary>把一批实例名拼成以 NUL 结尾的宽字符串缓冲区，返回每个名字的指针。</summary>
    private static IntPtr[] BuildNameBuffer(string[] names, out IntPtr buf)
    {
        int bytes = 2;
        foreach (var n in names) bytes += (n.Length + 1) * 2;
        buf = Marshal.AllocHGlobal(bytes);
        var ptrs = new IntPtr[names.Length];
        int off = 0;
        for (int i = 0; i < names.Length; i++)
        {
            ptrs[i] = buf + off;
            string s = names[i] ?? "";
            for (int k = 0; k < s.Length; k++)
                Marshal.WriteInt16(buf, off + k * 2, (short)s[k]);
            Marshal.WriteInt16(buf, off + s.Length * 2, 0);
            off += (s.Length + 1) * 2;
        }
        Marshal.WriteInt16(buf, off, 0);
        return ptrs;
    }

    /// <summary>
    /// 测试钩子：从实例名文本里取出适配器键。运行期走上面的零分配版本，这里只给测试用。
    /// </summary>
    internal static string AdapterKeyOf(string instanceName)
    {
        if (string.IsNullOrEmpty(instanceName)) return "";
        IntPtr buf = Marshal.StringToHGlobalUni(instanceName);
        try
        {
            if (!TryGetAdapterKey(buf, out int start, out int len)) return "";
            return Marshal.PtrToStringUni(buf + start * 2, len) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// 从实例名文本里取 (适配器, 引擎) 分组键（走运行期同一套解析规则，不碰 PDH）。
    /// 供兜底路径（逐实例 PerformanceCounter）按同一口径分组用；解析不出时返回空串。
    /// </summary>
    internal static string GroupKeyOf(string instanceName)
    {
        if (string.IsNullOrEmpty(instanceName)) return "";
        IntPtr buf = Marshal.StringToHGlobalUni(instanceName);
        try
        {
            if (!TryGetAdapterKey(buf, out int adStart, out int adLen)) return "";
            if (!TryGetEngineKey(buf, out int engStart, out int engLen)) return "";
            string adapter = Marshal.PtrToStringUni(buf + adStart * 2, adLen) ?? "";
            string engine = Marshal.PtrToStringUni(buf + engStart * 2, engLen) ?? "";
            return adapter + "@" + engine;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>采集一轮并返回全部实例（实例名会分配字符串，只给实例数很少的场景用）。</summary>
    public List<(string Name, double Value)> ReadAll()
    {
        var list = new List<(string, double)>(32);
        if (!IsOpen) return list;
        if (PdhCollectQueryData(_query) != 0) return list;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = _capacity;
            uint status = PdhGetFormattedCounterArrayW(_counter, FormatDouble | FormatNoCap100, ref size, out uint count, _buffer);
            if (status == MoreData) { Grow(size + 4096); continue; }
            if (status != 0) return list;
            for (uint i = 0; i < count; i++)
            {
                int at = (int)(i * ItemSize);
                uint itemStatus = (uint)Marshal.ReadInt32(_buffer, at + StatusOffset);
                if (itemStatus is not (ValidData or NewData)) continue;
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(_buffer, at + ValueOffset));
                if (!double.IsFinite(value)) continue;
                list.Add((NameOf(Marshal.ReadIntPtr(_buffer, at)), value));
            }
            return list;
        }
        return list;
    }

    /// <summary>零分配读取以非负整数命名的实例（如逻辑核 0..N-1），跳过 "_Total" 等非数字项。</summary>
    public bool ReadIndexed(double[] target, double cap)
    {
        if (!IsOpen || target.Length == 0) return false;
        if (PdhCollectQueryData(_query) != 0) return false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = _capacity;
            uint status = PdhGetFormattedCounterArrayW(_counter, FormatDouble | FormatNoCap100, ref size, out uint count, _buffer);
            if (status == MoreData) { Grow(size + 4096); continue; }
            if (status != 0) return false;
            Array.Clear(target, 0, target.Length);
            bool any = false;
            for (uint i = 0; i < count; i++)
            {
                int at = (int)(i * ItemSize);
                uint itemStatus = (uint)Marshal.ReadInt32(_buffer, at + StatusOffset);
                if (itemStatus is not (ValidData or NewData)) continue;
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(_buffer, at + ValueOffset));
                if (!double.IsFinite(value)) continue;
                if (!TryParseIndex(Marshal.ReadIntPtr(_buffer, at), out int idx)) continue;
                if ((uint)idx >= (uint)target.Length) continue;
                target[idx] = Math.Clamp(value, 0, cap);
                any = true;
            }
            return any;
        }
        return false;
    }

    private static bool TryParseIndex(IntPtr text, out int index)
    {
        index = 0;
        if (text == IntPtr.Zero) return false;
        int acc = 0;
        int digits = 0;
        for (int i = 0; ; i++)
        {
            char c = (char)Marshal.ReadInt16(text, i * 2);
            if (c == '\0') break;
            if (c < '0' || c > '9' || digits >= 9) return false;
            acc = acc * 10 + (c - '0');
            digits++;
        }
        if (digits == 0) return false;
        index = acc;
        return true;
    }

    /// <summary>宽字符串里找子串（大小写不敏感），不解码字符串 → 零分配。</summary>
    private static bool NameHas(IntPtr text, string needle)
    {
        if (text == IntPtr.Zero) return false;
        if (string.IsNullOrEmpty(needle)) return true;
        int n = needle.Length;
        for (int i = 0; ; i++)
        {
            char c = (char)Marshal.ReadInt16(text, i * 2);
            if (c == '\0') return false;
            if (!Same(c, needle[0])) continue;
            bool ok = true;
            for (int k = 1; k < n; k++)
            {
                char c2 = (char)Marshal.ReadInt16(text, (i + k) * 2);
                if (c2 == '\0' || !Same(c2, needle[k])) { ok = false; break; }
            }
            if (ok) return true;
        }
    }

    private static string NameOf(IntPtr text)
    {
        if (text == IntPtr.Zero) return "";
        int len = lstrlenW(text);
        return string.Create(len, text, (span, ptr) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = (char)Marshal.ReadInt16(ptr, i * 2);
        });
    }

    private static bool Same(char a, char b) => a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    private void Grow(uint size)
    {
        if (size <= _capacity) return;
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _capacity = 0;
        }
        _buffer = Marshal.AllocHGlobal((nint)size);
        _capacity = size;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
        _counter = IntPtr.Zero;
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; _capacity = 0; }
    }

    // PDH_FMT_COUNTERVALUE_ITEM_W：LPWSTR szName + PDH_FMT_COUNTERVALUE{ DWORD CStatus; union{LONG; double;} }
    //   PDH_FMT_COUNTERVALUE 里的 double 按 8 字节对齐 ⇒ CStatus 与数值之间恒有 4 字节填充，
    //   结构体整体对齐到 8。因此 x86 与 x64 的成员偏移**完全相同**：
    //   名字指针@0、状态@8、数值@16、sizeof=24（x86 只有指针宽度从 8 变 4）。
    //   旧注释写的 "x86：状态@4、数值@8、sizeof=16" 是错的（x86 的 double 同样 8 字节对齐），
    //   照旧写会让 x86 构建静默读到错误偏移（表现为读数全 0）。
    private static readonly int ItemSize = 24;
    private static readonly int StatusOffset = 8;
    private static readonly int ValueOffset = 16;
    private const uint FormatDouble = 0x00000200, FormatNoCap100 = 0x00008000;
    private const uint MoreData = 0x800007D2, ValidData = 0, NewData = 1;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll")] private static extern int lstrlenW(IntPtr text);
}
