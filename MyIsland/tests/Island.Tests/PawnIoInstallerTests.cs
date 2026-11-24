using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// 温度驱动（PawnIO）自举的用例（2026-11-24）。
///
/// 背景：LibreHardwareMonitorLib 0.9.6 只会**打开** PawnIO 设备（\\?\GLOBALROOT\Device\PawnIO），不会装驱动，
/// MyIsland 以前也不随包带。于是在干净的最终用户机器上，哪怕以管理员运行也永远读不到 CPU 温度
/// （Intel / AMD 都一样）—— 开发机上能读到，只是因为本机恰好装了 PawnIO 2.2.0.0。
/// 现在 MyIsland 内嵌官方签名的安装器：以管理员运行、且驱动缺失或低于 2.0.0.0 时，自己在后台静默装好。
///
/// 这里钉住的是"绝不出事"的几条线：
///   ① 要不要装的判定（没装 / 读不懂 / 太旧才装；够新就什么都不做，也绝不降级）；
///   ② 传给安装器的参数 —— 只有 -install -silent，绝不带 -unrestricted（不签名的驱动版本）/ -debuginfo；
///   ③ 校验：SHA-256 对不上的文件必须被拒绝（被篡改或换错的安装器绝不能跑）；
///   ④ 仓库里那份安装器的哈希与 ExpectedSha256 完全一致（谁换了文件，门禁就红）；
///   ⑤ 读已安装版本不抛异常，且不依赖任何具体的已安装版本；
///   ⑥ 温度服务自愈的判定（打开时没有驱动、现在有了，才重建）；
///   ⑦ 运行器的退出码 / 超时杀进程 / 先校验再启动 —— 用 cmd.exe 顶替，**不跑真安装器**。
///
/// 安全说明：测试工程**没有内嵌**安装器资源；本文件也绝不调用 EnsureInstalled、绝不运行 PawnIO_setup.exe，
/// 注册表只读。所以这套用例在任何机器上（包括用管理员身份跑门禁）都不会改动系统。
/// </summary>
internal static class PawnIoInstallerTests
{
    public static void Run(Action<string, bool> check)
    {
        NeedsInstall(check);
        ArgumentsArePinned(check);
        VerifySha256(check);
        PinnedInstaller(check);
        InstalledVersionIsSafe(check);
        NeedsReopen(check);
        RunInstallerWithStandIn(check);
    }

    // ---------- ① 要不要装 ----------
    private static void NeedsInstall(Action<string, bool> check)
    {
        check("PawnIO：没装（null）需要安装", PawnIoInstaller.NeedsInstall(null, PawnIoInstaller.MinimumVersion));
        check("PawnIO：版本号是空串需要安装", PawnIoInstaller.NeedsInstall("", PawnIoInstaller.MinimumVersion));
        check("PawnIO：版本号读不懂（乱码）需要安装",
            PawnIoInstaller.NeedsInstall("not-a-version", PawnIoInstaller.MinimumVersion));
        check("PawnIO：低于最低版本（1.9.9.9）需要安装",
            PawnIoInstaller.NeedsInstall("1.9.9.9", PawnIoInstaller.MinimumVersion));
        check("PawnIO：正好是最低版本（2.0.0.0）不用装",
            !PawnIoInstaller.NeedsInstall("2.0.0.0", PawnIoInstaller.MinimumVersion));
        check("PawnIO：当前随包版本（2.2.0.0）不用装",
            !PawnIoInstaller.NeedsInstall("2.2.0.0", PawnIoInstaller.MinimumVersion));
        check("PawnIO：比最低版本新（9.9.9.9）也不用装（绝不降级）",
            !PawnIoInstaller.NeedsInstall("9.9.9.9", PawnIoInstaller.MinimumVersion));
        check("PawnIO：最低版本以参数为准（不是写死的 2.0.0.0）",
            !PawnIoInstaller.NeedsInstall("1.9.9.9", new Version(1, 9, 9, 9)));
        check("PawnIO：两段式版本号（2.0）就是 2.0.0.0，不算低于最低版本",
            !PawnIoInstaller.NeedsInstall("2.0", PawnIoInstaller.MinimumVersion));
        check("PawnIO：三段式版本号（2.0.0）同样不算低于最低版本",
            !PawnIoInstaller.NeedsInstall("2.0.0", PawnIoInstaller.MinimumVersion));
        check("PawnIO：最低版本是 2.0.0.0（与 LibreHardwareMonitor 自带向导的判定线一致）",
            PawnIoInstaller.MinimumVersion == new Version(2, 0, 0, 0));
    }

    // ---------- ② 参数写死 ----------
    // -unrestricted 装的是不带签名的驱动版本（开着内存完整性的机器上加载不了，本机就是）；
    // -debuginfo 额外安装调试信息。两个都不是默认安装的一部分，所以连参数常量本身一起钉住。
    private static void ArgumentsArePinned(Action<string, bool> check)
    {
        check("PawnIO：安装参数固定为 -install -silent", PawnIoInstaller.Arguments == "-install -silent");
        check("PawnIO：安装参数不带 -unrestricted", !PawnIoInstaller.Arguments.Contains("-unrestricted"));
        check("PawnIO：安装参数不带 -debuginfo", !PawnIoInstaller.Arguments.Contains("-debuginfo"));
        check("PawnIO：卸载参数固定为 -uninstall -silent", PawnIoInstaller.UninstallArguments == "-uninstall -silent");
        check("PawnIO：卸载参数同样不带 -unrestricted / -debuginfo",
            !PawnIoInstaller.UninstallArguments.Contains("-unrestricted") && !PawnIoInstaller.UninstallArguments.Contains("-debuginfo"));

        string hash = PawnIoInstaller.ExpectedSha256;
        check("PawnIO：内嵌安装器的 SHA-256 是 64 位十六进制",
            hash.Length == 64 && hash.All(Uri.IsHexDigit));
    }

    // ---------- ③ 校验 ----------
    // 用任意字节 + 已知向量（SHA-256("abc")），不依赖仓库里的真安装器。
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static void VerifySha256(Action<string, bool> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "Island-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string good = Path.Combine(dir, "good.bin");
            string tampered = Path.Combine(dir, "tampered.bin");
            string appended = Path.Combine(dir, "appended.bin");
            File.WriteAllBytes(good, Encoding.ASCII.GetBytes("abc"));
            File.WriteAllBytes(tampered, Encoding.ASCII.GetBytes("abd"));       // 改了 1 个字节
            File.WriteAllBytes(appended, Encoding.ASCII.GetBytes("abc\0"));     // 多了 1 个字节

            check("PawnIO：内容相符的文件通过校验（不抛异常）",
                !Throws(() => PawnIoInstaller.VerifySha256(good, AbcSha256)));
            check("PawnIO：期望值不分大小写（大写十六进制同样通过）",
                !Throws(() => PawnIoInstaller.VerifySha256(good, AbcSha256.ToUpperInvariant())));
            check("PawnIO：改过 1 个字节的文件被拒绝（InvalidOperationException）",
                Throws<InvalidOperationException>(() => PawnIoInstaller.VerifySha256(tampered, AbcSha256)));
            check("PawnIO：多出 1 个字节的文件被拒绝",
                Throws<InvalidOperationException>(() => PawnIoInstaller.VerifySha256(appended, AbcSha256)));
            check("PawnIO：期望值为空（常量被改坏）时一律拒绝，不会放行",
                Throws<InvalidOperationException>(() => PawnIoInstaller.VerifySha256(good, "")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------- ④ 仓库里的安装器被钉住 ----------
    // 从测试输出目录往上找 Island.sln 所在的 MyIsland/：driver/ 与它同级。
    private static void PinnedInstaller(Action<string, bool> check)
    {
        string? slnDir = FindSolutionDir();
        check("PawnIO：从测试输出目录往上能找到 Island.sln 所在的目录", slnDir is not null);
        if (slnDir is null) return;

        string installer = Path.Combine(slnDir, "driver", "PawnIO_setup.exe");
        check("PawnIO：仓库里有 driver/PawnIO_setup.exe", File.Exists(installer));
        if (!File.Exists(installer)) return;

        // 与 ExpectedSha256 比对，而不是把哈希再抄一遍到用例里：升级驱动时只需要换文件 + 改常量（见 driver/README.md）。
        check("PawnIO：仓库里的安装器与 ExpectedSha256 完全一致（换了文件必须同步改常量）",
            !Throws(() => PawnIoInstaller.VerifySha256(installer, PawnIoInstaller.ExpectedSha256)));
    }

    private static string? FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Island.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // ---------- ⑤ 读注册表不抛异常 ----------
    // 本机装没装、装的是哪个版本都不重要：这里只断言"读不到只是 null"，不断言具体值
    // （门禁会在没装驱动的机器上跑，也会在装了更新版本的机器上跑）。
    private static void InstalledVersionIsSafe(Action<string, bool> check)
    {
        string? version = null;
        bool threw = false;
        try { version = PawnIoInstaller.InstalledVersion(); }
        catch { threw = true; }

        check("PawnIO：读已安装版本不抛异常（没装 / 没权限 / 键坏了都只是 null）", !threw);
        check("PawnIO：读到的版本要么是 null 要么是非空文本", version is null || version.Length > 0);
        check("PawnIO：连续读两次结果一致（每次都现读，没有进程内缓存）",
            !threw && string.Equals(version, PawnIoInstaller.InstalledVersion(), StringComparison.Ordinal));

        // "到位"的判定还要求本进程此刻没在跑安装器（安装器可能先写注册表、后装好驱动）。
        // 不动系统、也不真的跑安装器：用测试钩子把"安装中"标志立起来，看判定会不会被它压住。
        PawnIoInstaller.SetInstallInProgressForTest(true);
        try
        {
            bool inProgress = PawnIoInstaller.InstallInProgress;
            check("PawnIO：测试钩子能立起\"安装中\"标志", inProgress);
            // 这条只有在已装驱动的机器上才有区分度（没装的机器上 IsDriverReady 本来就是 false）
            check("PawnIO：安装器还在跑的时候，驱动一律不算到位（哪怕注册表已经写了版本）",
                inProgress && !PawnIoInstaller.IsDriverReady());
        }
        finally { PawnIoInstaller.SetInstallInProgressForTest(false); }

        // 测试进程里此后永远不会有安装在进行，所以只断言它退化成"注册表版本够新"，不断言具体是 true 还是 false。
        check("PawnIO：安装结束后恢复按注册表判定（InstallInProgress 回到 false）",
            !PawnIoInstaller.InstallInProgress
            && !threw
            && PawnIoInstaller.IsDriverReady() == !PawnIoInstaller.NeedsInstall(version, PawnIoInstaller.MinimumVersion));
    }

    // ---------- ⑥ 温度服务自愈的判定 ----------
    // LHM 里用到驱动的模块对象在构造时就打开驱动设备，句柄终身不变：驱动是启动后才装上的话，必须重建 Computer 才看得见。
    private static void NeedsReopen(Action<string, bool> check)
    {
        check("PawnIO：打开时没有驱动、现在有了 ⇒ 要重建",
            PawnIoInstaller.NeedsReopen(openedWithDriver: false, driverPresentNow: true));
        check("PawnIO：驱动仍未到位 ⇒ 不重建（继续用原来那份采样，不空转）",
            !PawnIoInstaller.NeedsReopen(openedWithDriver: false, driverPresentNow: false));
        check("PawnIO：打开时驱动已在 ⇒ 永不重建（之后驱动没了也不进入重建循环）",
            !PawnIoInstaller.NeedsReopen(openedWithDriver: true, driverPresentNow: false)
            && !PawnIoInstaller.NeedsReopen(openedWithDriver: true, driverPresentNow: true));
    }

    // ---------- ⑦ 运行器：用 cmd.exe 顶替 ----------
    // 真安装器不能跑（也不该在门禁里跑）。退出码、超时、先校验再启动这三件事与"跑的是谁"无关，
    // 用系统自带的 cmd.exe 就能验证。
    private static void RunInstallerWithStandIn(Action<string, bool> check)
    {
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        if (!File.Exists(cmd))
        {
            check("PawnIO：运行器用例需要 cmd.exe 作为替身", false);
            return;
        }

        check("PawnIO：运行器如实带回退出码（cmd /c exit 3 → exit=3）",
            PawnIoInstaller.RunInstaller(cmd, "/c exit 3", 15000) == "exit=3");

        // ping 要跑将近 2 分钟，时限 800ms：必须按时返回 timeout，并把整棵进程树（cmd + ping）杀干净。
        // 杀不干净的话临时目录里的安装器一直被占用，删不掉。
        DateTime started = DateTime.Now;
        var sw = Stopwatch.StartNew();
        string status = PawnIoInstaller.RunInstaller(cmd, "/c ping -n 120 127.0.0.1 >nul", 800);
        sw.Stop();

        check("PawnIO：超过时限返回 timeout", status == "timeout");
        check("PawnIO：超时后不等它自己结束（800ms 时限，10 秒内必须返回）", sw.ElapsedMilliseconds < 10_000);
        check("PawnIO：超时后整棵进程树被杀（没有残留的 ping）",
            WaitUntil(() => CountPings(startedSince: started) == 0, 3000));

        check("PawnIO：安装器路径不存在时抛异常（不会假装成功；由 EnsureInstalled 兜成状态文本）",
            Throws(() => PawnIoInstaller.RunInstaller(
                Path.Combine(Path.GetTempPath(), "island-tests-no-such-" + Guid.NewGuid().ToString("N") + ".exe"), "", 1000)));

        // 先校验再启动：校验通过照常运行（启动时文件被只读句柄攥着，不妨碍新进程启动）；
        // 校验不过必须在启动之前就拒绝 —— 这里让它跑 "exit 7"，若真跑了就会返回 exit=7 而不是抛异常。
        string cmdHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cmd)));
        check("PawnIO：校验通过后照常运行（cmd /c exit 0 → exit=0）",
            PawnIoInstaller.RunVerifiedInstaller(cmd, "/c exit 0", 15000, cmdHash) == "exit=0");
        string wrongHash = (cmdHash[^1] == '0' ? cmdHash[..^1] + "1" : cmdHash[..^1] + "0");
        check("PawnIO：校验不过时在启动前拒绝（InvalidOperationException，进程没跑起来）",
            Throws<InvalidOperationException>(() => PawnIoInstaller.RunVerifiedInstaller(cmd, "/c exit 7", 15000, wrongHash)));
    }

    private static int CountPings(DateTime startedSince)
    {
        int count = 0;
        foreach (var process in Process.GetProcessesByName("PING"))
        {
            using (process)
            {
                try { if (!process.HasExited && process.StartTime >= startedSince) count++; }
                catch { /* 别的用户的进程读不到启动时间：不算 */ }
            }
        }
        return count;
    }

    // ---------- 小工具 ----------
    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    /// <summary>轮询等待条件成立（每 20ms 看一次）；超时前没成立返回 false。</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }
}
