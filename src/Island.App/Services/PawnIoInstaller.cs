using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace Island.App.Services;

/// <summary>
/// 温度驱动（PawnIO）自举（2026-11-24）：程序自己带驱动、自己装，用户不用手工装任何东西。
///
/// 为什么需要：LibreHardwareMonitorLib 0.9.6 只会**打开** PawnIO 设备（\\?\GLOBALROOT\Device\PawnIO），
/// 不负责安装；MyIsland 以前也不随包带。干净的机器上哪怕以管理员运行也读不到 CPU 温度 ——
/// Intel 得到 null，AMD 得到 0 °C / −49 °C 这类假值（ThermalService.IsValidReading 会滤掉）。
/// 开发机上能读到，只是因为本机恰好装了 PawnIO 2.2.0.0。
///
/// 怎么做（与 LibreHardwareMonitor 自带桌面向导同一条路，只多了 -silent）：
/// 以管理员运行，且驱动没装或低于 <see cref="MinimumVersion"/> ⇒ 把内嵌的官方签名安装器释放到临时目录
/// ⇒ 校验 SHA-256 ⇒ 静默安装 ⇒ 回读注册表，如实报告。
/// 不走 WinRing0 那类老驱动（TrafficMonitor 的做法）：开着内存完整性（HVCI）的机器上它被系统拦掉，本机就是。
///
/// 约束：
///   - 不依赖 LibreHardwareMonitor 的类型 —— 测试工程零包依赖，靠源文件链接把本文件编进去；
///   - <see cref="EnsureInstalled"/> 绝不抛异常，只返回一行给日志的状态；
///   - 注册表只读；唯一会改动系统的动作，就是运行那份哈希校验过的安装器。
/// 本类只管"装"。装完之后已经在跑的硬件对象怎么恢复读数，见 ThermalService（DriverPresent / 自愈）。
/// </summary>
internal static class PawnIoInstaller
{
    /// <summary>低于这个版本就重装（LibreHardwareMonitor 自带向导的判定线同样是 2.0.0.0）。</summary>
    internal static readonly Version MinimumVersion = new(2, 0, 0, 0);

    /// <summary>安装器登记的卸载项：<see cref="VersionValueName"/> 形如 "2.2.0.0"。</summary>
    internal const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    /// <summary><see cref="UninstallKeyPath"/> 下记录版本的值名。</summary>
    internal const string VersionValueName = "DisplayVersion";

    /// <summary>内嵌资源的逻辑名（Island.App.csproj 里 EmbeddedResource 的 LogicalName）。</summary>
    internal const string ResourceName = "Island.App.Resources.PawnIO_setup.exe";

    /// <summary>
    /// 官方 2.2.0 安装器（driver/PawnIO_setup.exe，3410960 字节）的 SHA-256，与 GitHub release 元数据里的摘要一致。
    /// 测试 PawnIoInstallerTests 每次门禁都把仓库里那份文件与它比对一遍。
    /// 被篡改或换错的安装器绝不能运行：所以释放后、启动前各校验一次，对不上就当失败处理。
    /// </summary>
    internal const string ExpectedSha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";

    /// <summary>
    /// 安装参数。-silent = 静默，连出错也不弹界面。
    /// 绝不加 -unrestricted 或 -debuginfo：前者装的是不带签名的 unrestricted edition
    /// （pawnio.eu 的说明是 "An unsigned version without signature checking"，开着内存完整性的机器上根本加载不了），
    /// 后者额外安装 debug info。两个都不是默认安装的一部分，不是 MyIsland 该替用户做的选择。
    /// </summary>
    internal const string Arguments = "-install -silent";

    /// <summary>卸载参数。装着旧版时安装器拒绝覆盖安装（"Please uninstall it first"），只能先卸。</summary>
    internal const string UninstallArguments = "-uninstall -silent";

    /// <summary>
    /// 等安装器退出的上限。没有实测值（2026-11-24：本机已装 2.2.0.0，而且不在开发机上跑安装器）：
    /// 安装要落盘驱动文件、注册并启动服务，中途还可能被杀毒软件扫描拖慢，所以给到 2 分钟。
    /// 等待发生在线程池线程上，超时只决定"多久放弃"，不挡界面。
    /// </summary>
    internal const int InstallTimeoutMs = 120_000;

    /// <summary>超时杀进程之后，等它真的退掉的上限（它可能还攥着临时文件）。</summary>
    private const int KillWaitMs = 5_000;

    /// <summary>
    /// 要不要装：没装（null）、版本号读不懂、或低于 <paramref name="minimum"/> 都算要装。
    /// 纯函数，不碰注册表 —— 判定与"从哪读"分开，才测得干净。
    /// </summary>
    internal static bool NeedsInstall(string? installedVersion, Version minimum)
    {
        if (!TryNormalize(installedVersion, out Version? version)) return true;
        return version < minimum;
    }

    /// <summary>读取已安装的 PawnIO 版本；没装、读不到、非 Windows 一律返回 null，绝不抛异常。</summary>
    internal static string? InstalledVersion()
    {
        try
        {
            // 显式读两个视图，而不是依赖进程位数：32 位安装器写的是 WOW6432Node 那份，
            // 64 位进程用默认视图看不到它。本机 2026-11-24 只有 64 位视图这一份（2.2.0.0）。
            // 不用 LibreHardwareMonitor 自带的 PawnIo.IsInstalled：它在静态构造函数里只读一次注册表，
            // 同一个进程后来把驱动装上了，它也永远不会知道。
            return PickHigher(ReadDisplayVersion(RegistryView.Registry64), ReadDisplayVersion(RegistryView.Registry32));
        }
        catch { return null; }
    }

    private static string? ReadDisplayVersion(RegistryView view)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = hive.OpenSubKey(UninstallKeyPath, writable: false);
        return key?.GetValue(VersionValueName) as string is { Length: > 0 } version ? version : null;
    }

    /// <summary>两个视图各有一份时取较高的那个（两份不一致时，高的那份更接近真实）。</summary>
    private static string? PickHigher(string? a, string? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        bool aIsVersion = TryNormalize(a, out Version? aVersion);
        bool bIsVersion = TryNormalize(b, out Version? bVersion);
        if (aIsVersion != bIsVersion) return aIsVersion ? a : b;     // 能当版本号解读的那份更可信
        return aIsVersion && bIsVersion && aVersion! > bVersion! ? a : b;
    }

    /// <summary>
    /// 版本号解析，并把缺的段补成 0。注册表里通常是四段（"2.2.0.0"），但也可能只有两三段（"2.0"）：
    /// Version 把缺的段当 -1，不补的话 "2.0" 会被判成低于 2.0.0.0，每次启动都白卸载重装一遍。
    /// </summary>
    private static bool TryNormalize(string? text, out Version? version)
    {
        version = null;
        if (!Version.TryParse(text, out Version? parsed)) return false;
        version = parsed.Build < 0 || parsed.Revision < 0
            ? new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision))
            : parsed;
        return true;
    }

    /// <summary>
    /// 温度服务的 Computer 要不要重开：打开它的时候驱动不在、现在在了，才需要。
    /// "打开时驱动已在"一律返回 false —— 否则驱动之后被别的软件卸掉，就会每 2 秒重建一次，停不下来。
    /// </summary>
    internal static bool NeedsReopen(bool openedWithDriver, bool driverPresentNow)
        => !openedWithDriver && driverPresentNow;

    /// <summary>
    /// 驱动是否"到位"：注册表里的版本够新，**并且本进程此刻没有在跑安装器**。
    /// 后半句不能省：安装器可能先登记卸载项、后真正把驱动装好；升级旧版时卸载项还会先消失再出现。
    /// 只看注册表的话，温度服务会对着装了一半的驱动去重建，界面也会提前把"缺驱动"换成"读不到"。
    /// </summary>
    internal static bool IsDriverReady()
        => !InstallInProgress && !NeedsInstall(InstalledVersion(), MinimumVersion);

    // 本进程正在跑安装器的那段时间为 1：从启动第一个安装器进程起，到最后一个退出（或超时被杀）止。
    private static int _installing;

    /// <summary>本进程此刻是否正在跑安装器（安装或卸载）。见 <see cref="IsDriverReady"/>。</summary>
    internal static bool InstallInProgress => Volatile.Read(ref _installing) != 0;

    /// <summary>仅供测试：直接置"安装中"标志，用来钉住 IsDriverReady 在安装期间恒为 false。</summary>
    internal static void SetInstallInProgressForTest(bool inProgress)
        => Volatile.Write(ref _installing, inProgress ? 1 : 0);

    /// <summary>校验文件的 SHA-256；对不上就抛 <see cref="InvalidOperationException"/>。</summary>
    internal static void VerifySha256(string path, string expectedHex)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        VerifySha256(stream, expectedHex);
    }

    /// <summary>
    /// 校验一个流的 SHA-256（从当前位置读到末尾）；对不上就抛 <see cref="InvalidOperationException"/>。
    /// 期望值是空的或不合法时同样抛 —— 宁可装不上，也不能放行一个没校验过的文件。
    /// </summary>
    internal static void VerifySha256(Stream stream, string expectedHex)
    {
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SHA-256 不符，拒绝运行：期望 {expectedHex}，实际 {actual}");
    }

    /// <summary>
    /// 把内嵌的安装器写到 <paramref name="destinationPath"/> 并校验，返回该路径。
    /// 校验不过会抛异常 —— 内嵌的是被换错或改坏的文件时，后面的安装步骤一步都不会执行。
    /// </summary>
    internal static string ExtractInstaller(string destinationPath)
    {
        using Stream resource = typeof(PawnIoInstaller).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"程序里没有内嵌安装器（{ResourceName}）");
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using (var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            resource.CopyTo(file);
        VerifySha256(destinationPath, ExpectedSha256);
        return destinationPath;
    }

    /// <summary>
    /// 运行安装器并等它退出，返回一行短状态（exit=&lt;退出码&gt; 或 timeout）。
    /// 本方法只管"启动 + 等 + 超时杀"，不校验文件；对释放到磁盘上的那份，请走 <see cref="RunVerifiedInstaller"/>。
    /// 启动失败（文件不存在等）会抛异常，由 <see cref="EnsureInstalled"/> 兜成状态文本。
    /// </summary>
    internal static string RunInstaller(string installerPath, string arguments, int timeoutMs)
    {
        using Process process = StartInstaller(installerPath, arguments);
        return WaitStatus(process, timeoutMs);
    }

    /// <summary>
    /// 校验通过才运行的安装器：<see cref="RunInstaller"/> 加一道"校验与启动之间换不了文件"的保证。
    ///
    /// 为什么要多这一步：安装器释放的在 %TEMP%\MyIsland\ 下，那是当前用户（包括以普通权限运行的任何程序）
    /// 可写的目录。如果"校验完、关掉句柄、再去启动"，这中间的一瞬间文件是可以被人换成别的 exe 的，
    /// 而启动它的是管理员进程。所以这里用只读共享的句柄把文件攥着：攥着的时候别的进程改不了、删不了、换不了，
    /// 就在这个状态下校验、启动；新进程映射到的只可能是校验过的那份字节。进程一起来就松手，
    /// 之后安装器怎么用自己的文件都不受影响。
    /// </summary>
    internal static string RunVerifiedInstaller(string installerPath, string arguments, int timeoutMs, string expectedSha256)
    {
        Process process;
        using (var hold = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            VerifySha256(hold, expectedSha256);
            process = StartInstaller(installerPath, arguments);
        }
        using (process)
        {
            return WaitStatus(process, timeoutMs);
        }
    }

    private static Process StartInstaller(string installerPath, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = arguments,
            // 不走 shell：不弹 UAC、不看文件关联，启动的就是校验过的那个文件本身
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? "",
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"安装器没有启动：{installerPath}");
    }

    private static string WaitStatus(Process process, int timeoutMs)
    {
        if (process.WaitForExit(timeoutMs))
            return $"exit={process.ExitCode}";

        // 超时：连子进程一起杀。安装器卡死时可能还攥着临时文件，不杀的话 finally 里删不掉。
        // 杀的只是它自己和它拉起的后代，不会波及别的进程。
        try { process.Kill(entireProcessTree: true); } catch { }
        process.WaitForExit(KillWaitMs);
        return "timeout";
    }

    /// <summary>
    /// 确保驱动装好且不旧于 <see cref="MinimumVersion"/>，返回一行给日志的中文状态，绝不抛异常。
    ///
    /// 流程：不是管理员 ⇒ 直接返回"跳过"（这里从不拉起 UAC）⇒ 读注册表 ⇒ 够新就返回"已是最新"，
    /// 什么都不做 ⇒ 否则释放安装器、校验、（有旧版先卸载）静默安装 ⇒ 回读注册表，以注册表为准报告结果。
    /// 这个方法会等安装器，可能长达 <see cref="InstallTimeoutMs"/> × 2：只能在线程池线程上调用，别放到 UI 线程。
    /// </summary>
    public static string EnsureInstalled()
    {
        try
        {
            if (!IsElevated()) return "未以管理员运行，跳过";

            string? installed = InstalledVersion();
            // 够新：只读了一次注册表就返回，不释放文件、不起进程。
            if (!NeedsInstall(installed, MinimumVersion))
                return $"已是最新（{installed}）";

            // 装着旧版（或版本号读不懂）时安装器拒绝覆盖安装，得先卸。
            bool replacing = installed is not null;
            string path = Path.Combine(Path.GetTempPath(), "MyIsland", "PawnIO_setup.exe");
            try
            {
                ExtractInstaller(path);
                Volatile.Write(ref _installing, 1);
                try
                {
                    string uninstalled = replacing
                        ? $"卸载旧版 {installed}（{RunVerifiedInstaller(path, UninstallArguments, InstallTimeoutMs, ExpectedSha256)}）；"
                        : "";
                    string run = RunVerifiedInstaller(path, Arguments, InstallTimeoutMs, ExpectedSha256);

                    // 以注册表为准，不信退出码：静默模式下安装器出错也不弹窗，退出码说明不了驱动到底在不在。
                    string? now = InstalledVersion();
                    return NeedsInstall(now, MinimumVersion)
                        ? $"{uninstalled}安装未生效：注册表版本仍为 {now ?? "无"}（{run}）"
                        : $"{uninstalled}已安装 {now}（{run}）";
                }
                finally
                {
                    // 安装器都退了才算"不在安装中"：之后 DriverPresent 才会认注册表（见 IsDriverReady）
                    Volatile.Write(ref _installing, 0);
                }
            }
            finally
            {
                // 临时文件一定要删，放在 finally 里：中途抛异常的路径同样要删。
                // 删不掉（被占用等）不影响结果，下次运行会原样覆盖。
                try { File.Delete(path); } catch { }
            }
        }
        catch (Exception ex)
        {
            // 绝不抛：本方法在启动路径的后台线程上跑，异常逃出去只会变成一条没人看的 Task 异常。
            // 把原因原样带回给日志。
            return $"失败：{ex.GetType().Name}：{ex.Message}";
        }
    }

    /// <summary>
    /// 当前进程是否以管理员运行。与 App.IsElevated 是同一段判断，这里刻意再写一份：
    /// 本类不能依赖 App（WPF），否则测试工程链接不进来。
    /// </summary>
    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
