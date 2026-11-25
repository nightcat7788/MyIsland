namespace Island.Avalonia;

/// <summary>
/// 历史曲线缓冲（对齐 BrimDeck 监控页每个读数旁边的曲线）。
///
/// 纯 UI 侧：画布在收到 Snapshot 时调 <see cref="Push"/>，绘制时调 <see cref="Series"/> 取最近 N 个点。
/// 固定容量环形缓冲，不分配新数组（避免每帧 GC）。
///
/// 采样间隔不固定（1s 一个快照），曲线按"点序"画而不是按时间轴——收起态和展开页都够用，
/// 也省掉时间戳的换算。
/// </summary>
public sealed class HistoryBuffer
{
    private readonly double[][] _buf;
    private readonly int _cap;
    private int _head;      // 下一个写入位置
    private int _count;     // 已写入的点数（≤ _cap）

    /// <summary>曲线种类。顺序即数组下标。</summary>
    public enum Kind { Cpu, Gpu, Mem, Down, Up, DiskRead, DiskWrite, Count }

    /// <param name="capacity">每个序列保留多少个点。</param>
    public HistoryBuffer(int capacity)
    {
        _cap = Math.Max(2, capacity);
        _buf = new double[(int)Kind.Count][];
        for (int i = 0; i < _buf.Length; i++) _buf[i] = new double[_cap];
    }

    public int Capacity => _cap;
    /// <summary>已保留的点数（曲线启动后会慢慢涨到 Capacity）。</summary>
    public int Count => _count;

    /// <summary>追加一个点。传入 null 忽略。</summary>
    public void Push(Island.Core.Monitoring.MonitorSnapshot s)
    {
        if (s is null) return;
        // 网速用字节/秒；磁盘读写同。画图时各自按量程归一。
        Put((int)Kind.Cpu, s.CpuUsage);
        Put((int)Kind.Gpu, s.GpuUsage);
        Put((int)Kind.Mem, s.MemoryUsagePercent);
        Put((int)Kind.Down, s.DownloadSpeed);
        Put((int)Kind.Up, s.UploadSpeed);
        Put((int)Kind.DiskRead, s.DiskReadSpeed);
        Put((int)Kind.DiskWrite, s.DiskWriteSpeed);
        _head = (_head + 1) % _cap;
        if (_count < _cap) _count++;
    }

    private void Put(int kind, double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) v = 0;
        _buf[kind][_head] = v;
    }

    /// <summary>
    /// 把某条曲线最近 <paramref name="n"/> 个点按时间顺序拷到 <paramref name="dst"/>（从索引 0 开始），
    /// 并把值归一到 0..1：<paramref name="max"/> 是量程上界（≤0 表示用这批点的最大值自动量程）。
    /// 返回实际拷贝的点数。
    /// </summary>
    public int Series(Kind kind, int n, Span<double> dst, double max = 0)
    {
        int m = Math.Min(Math.Min(n, _count), dst.Length);
        if (m <= 0) return 0;
        int start = (_head - m + _cap * 2) % _cap;     // 最早的一个点
        var src = _buf[(int)kind];
        double hi = max;
        if (hi <= 0)
        {
            for (int i = 0; i < m; i++) hi = Math.Max(hi, src[(start + i) % _cap]);
            hi = hi <= 0 ? 1 : hi;
        }
        for (int i = 0; i < m; i++)
            dst[i] = Math.Clamp(src[(start + i) % _cap] / hi, 0, 1);
        return m;
    }

    /// <summary>清空（切视图 / 休眠唤醒后重新积累）。</summary>
    public void Clear() { _head = 0; _count = 0; }
}
