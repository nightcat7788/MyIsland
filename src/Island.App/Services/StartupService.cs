using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Island.App.Services;

/// <summary>
/// 开机自启：
/// - 普通权限：HKCU\...\Run 写 "MyIsland" =  exe 路径（免管理员）；
/// - 管理员权限：计划任务 ONLOGON + 最高权限（保住温度读数所需的管理员上下文），同时清掉 Run 键，避免双开。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MyIsland";
    private const string TaskName = "MyIsland-AutoStart";

    /// <summary>
    /// 自启要拉起的可执行文件路径（null = 无法确定，不写注册表）。
    /// 内存加载模式（run.ps1 / start-admin.ps1 用 pwsh 载入 Island.App.dll）下当前进程是 pwsh.exe，
    /// 直接把它写进 Run 键，开机时弹出来的是 PowerShell 窗口而不是灵动岛；
    /// 此时发布目录由 ISLAND_DIR 指定，里面的 Island.App.exe 才是真正的入口。
    /// ⚠️ 以 dotnet Island.App.dll 方式启动时当前进程是 dotnet.exe：
    /// 把它写进 Run 键，开机只会拉起一个空的 dotnet 宿主，灵动岛根本不会起来 →
    /// 这时返回 null，让调用方明确提示"无法确定自启路径"。
    /// </summary>
    private static string? ExePath => ResolveExePath();

    private static string? ResolveExePath()
    {
        string? dir = Environment.GetEnvironmentVariable("ISLAND_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
            return Path.Combine(dir, "Island.App.exe");

        string? main = null;
        try { main = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
        if (!string.IsNullOrEmpty(main))
        {
            // 只有 Island.App.exe 自己能当自启入口：宿主进程（dotnet.exe / pwsh.exe）写进去是错的
            bool isApp = string.Equals(
                Path.GetFileNameWithoutExtension(main), "Island.App", StringComparison.OrdinalIgnoreCase);
            return isApp ? main : null;
        }
        // 拿不到主模块：退到程序目录下的 Island.App.exe（文件存在才用）
        string guess = Path.Combine(AppContext.BaseDirectory, "Island.App.exe");
        return File.Exists(guess) ? guess : null;
    }

    /// <summary>
    /// 是否已开启开机自启。
    /// Run 键（普通权限）与计划任务（提权权限）任一存在都算开启：
    /// 只看一处的话，提权模式建过计划任务后用普通权限打开设置会显示"关"，
    /// 用户再点一次就同时留下计划任务和 Run 键，登录时启动两次。
    /// </summary>
    public static bool IsEnabled(bool elevated)
        => elevated ? (TaskExists() || RunKeyExists()) : (RunKeyExists() || TaskExists());

    /// <summary>返回 null 表示成功，否则为错误说明。</summary>
    public static string? SetEnabled(bool enabled, bool elevated)
    {
        try
        {
            if (elevated) return SetTask(enabled);
            return SetRunKey(enabled);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ---------- Run 键（普通权限） ----------

    /// <summary>
    /// Run 键里是否有一个"真实存在"的自启项。
    /// 以前只看值里含不含 "Island" 字符串：exe 被删掉/移动过之后 Run 键还留着，
    /// 界面却显示"已开启"，用户以为开机自启在工作，其实登录后什么都不会发生。
    /// 现在解析出路径并校验文件是否还在；文件没了就按"未开启"算，并顺手清掉失效值。
    /// </summary>
    private static bool RunKeyExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key?.GetValue(RunValueName) is not string s || s.Length == 0) return false;
        string? path = ExtractExePath(s);
        if (path is null) return false;
        if (File.Exists(path)) return true;
        TryDeleteRunValue();   // 指向的文件没了：清掉，免得一直误导
        return false;
    }

    /// <summary>从 Run 键的值里取出 exe 路径（支持带空格路径与附加参数）。</summary>
    private static string? ExtractExePath(string value)
    {
        string s = value.Trim();
        if (s.Length == 0) return null;
        if (s[0] == '"')
        {
            int endQuote = s.IndexOf('"', 1);
            if (endQuote <= 1) return null;
            string quoted = s.Substring(1, endQuote - 1).Trim();
            return quoted.Length == 0 ? null : quoted;
        }
        if (File.Exists(s)) return s;
        int sp = s.IndexOf(' ');
        if (sp > 0) s = s.Substring(0, sp);
        return s.Length == 0 ? null : s;
    }

    /// <summary>清掉失效的 Run 值（没有权限就算了，不影响判断结果）。</summary>
    private static void TryDeleteRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    private static string? SetRunKey(bool enabled)
    {
        // 普通模式下清掉可能残留的计划任务，避免双开。
        // 删不掉必须报错，不能静默继续：计划任务是以最高权限建的，
        // 普通权限下 schtasks /Delete 会 Access Denied，任务还在而 Run 键也写了，登录时启动两次。
        string? delErr = DeleteTask();
        if (delErr is not null) return delErr;

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return "无法打开注册表 Run 键";
        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return null;
        }
        // 写之前先校验 exe 真的存在：写一个不存在的路径 = 开机自启静默失效，用户完全看不出来
        string? exe = ExePath;
        if (exe is null)
            return "无法确定自启程序路径（当前不是以 Island.App.exe 方式运行），未写入开机自启";
        if (!File.Exists(exe))
            return $"自启程序不存在：{exe}，未写入开机自启";
        key.SetValue(RunValueName, $"\"{exe}\"");
        return null;
    }

    // ---------- 计划任务（管理员权限） ----------

    private static bool TaskExists()
    {
        var (code, output, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return code == 0 && output.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SetTask(bool enabled)
    {
        // 管理员模式下清掉 Run 键，避免双开。
        // 删不掉必须报错：Run 键留着而计划任务也建起来，登录时启动两次。
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            // DeleteValue 删不掉会抛异常（无权限/被占用），交给下面的 catch 转成错误文案
            if (key is not null && key.GetValue(RunValueName) is not null)
                key.DeleteValue(RunValueName);
        }
        catch (Exception ex)
        {
            return "无法删除已存在的注册表 Run 键：" + ex.Message;
        }

        // 关闭时同样要报告删除失败（否则任务留着，下次登录照样自启）
        if (!enabled) return DeleteTask();

        // 建任务前同样校验 exe 真的存在：任务里写一个不存在的路径 = 开机自启静默失效
        string? exe = ExePath;
        if (exe is null)
            return "无法确定自启程序路径（当前不是以 Island.App.exe 方式运行），未创建计划任务";
        if (!File.Exists(exe))
            return $"自启程序不存在：{exe}，未创建计划任务";

        var (code, _, err) = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\"\" /F");
        return code == 0 ? null : (string.IsNullOrWhiteSpace(err) ? "计划任务创建失败" : err.Trim());
    }

    /// <summary>删除计划任务；返回 null 表示成功（或任务本来就不存在），否则为错误说明。</summary>
    private static string? DeleteTask()
    {
        // 任务不存在 = 没有残留，不需要删
        // （直接删不存在的任务，schtasks 会返回"系统找不到指定的文件"，那不是失败）
        if (!TaskExists()) return null;

        var (code, _, err) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (code == 0) return null;
        return string.IsNullOrWhiteSpace(err)
            ? "删除已存在的计划任务失败（请以管理员身份运行后再操作）"
            : err.Trim();
    }

    private static (int Code, string Output, string Error) RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "无法启动 schtasks");
            // 先启动异步读取再等退出：同步 ReadToEnd 会把 WaitForExit 的超时架空，
            // 管道被写满时还会互相死等。
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(2000);
            }
            string output = outTask.Wait(2000) ? outTask.Result : "";
            string error = errTask.Wait(2000) ? errTask.Result : "";
            return (p.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
