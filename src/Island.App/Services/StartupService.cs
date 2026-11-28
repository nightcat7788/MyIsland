using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Island.App.Services;

/// <summary>
/// 开机自启：
/// - 普通权限（或安装于用户可写目录）：HKCU\...\Run 写 "MyIsland" = exe 路径（免管理员，防用户可写路径提权风险）；
/// - 管理员权限且安装于受系统管理员 ACL 保护的 Program Files 目录：计划任务 ONLOGON + 最高权限。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MyIsland";
    private const string TaskName = "MyIsland-AutoStart";

    private static string? ExePath => ResolveExePath();

    private static string? ResolveExePath()
    {
        // 严格依据当前进程或程序基准目录，不信任用户可任意注入的 ISLAND_DIR 环境变量用于持久化开机自启
        string? main = Environment.ProcessPath;
        if (string.IsNullOrEmpty(main))
        {
            try { main = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
        }
        if (!string.IsNullOrEmpty(main) &&
            string.Equals(Path.GetFileNameWithoutExtension(main), "Island.App", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(main);
        }

        string guess = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Island.App.exe"));
        return File.Exists(guess) ? guess : null;
    }

    /// <summary>仅当程序位于系统管理员受保护目录（Program Files）下时，才允许注册 HIGHEST 权限计划任务，杜绝本地提权通道。</summary>
    private static bool IsAdminProtectedLocation(string exePath)
    {
        try
        {
            string full = Path.GetFullPath(exePath);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf) && full.StartsWith(EnsureTrailingSeparator(pf), StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(pf86) && full.StartsWith(EnsureTrailingSeparator(pf86), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return false;
    }

    private static string EnsureTrailingSeparator(string p)
        => p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;

    public static bool IsEnabled(bool elevated)
        => elevated ? (TaskExists() || RunKeyExists()) : (RunKeyExists() || TaskExists());

    public static string? SetEnabled(bool enabled, bool elevated)
    {
        try
        {
            if (elevated)
            {
                if (!enabled)
                {
                    string? taskErr = DeleteTask();
                    TryDeleteRunValue();
                    return taskErr;
                }
                string? exe = ExePath;
                if (exe is not null && IsAdminProtectedLocation(exe))
                    return SetTask(true);
                // 安装在用户目录（如 %LOCALAPPDATA%\Programs\MyIsland）时：清理旧高权限任务，改写安全的 HKCU Run 键
                _ = DeleteTask();
                return SetRunKeyCore(true);
            }
            return SetRunKey(enabled);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool RunKeyExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key?.GetValue(RunValueName) is not string s || s.Length == 0) return false;
        string? path = ExtractExePath(s);
        if (path is null) return false;
        if (File.Exists(path)) return true;
        TryDeleteRunValue();
        return false;
    }

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
        string? delErr = DeleteTask();
        if (delErr is not null) return delErr;
        return SetRunKeyCore(enabled);
    }

    private static string? SetRunKeyCore(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return "无法打开注册表 Run 键";
        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return null;
        }
        string? exe = ExePath;
        if (exe is null)
            return "无法确定自启程序路径（当前不是以 Island.App.exe 方式运行），未写入开机自启";
        if (!File.Exists(exe))
            return $"自启程序不存在：{exe}，未写入开机自启";
        key.SetValue(RunValueName, $"\"{exe}\"");
        return null;
    }

    private static bool TaskExists()
    {
        var (code, output, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return code == 0 && output.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SetTask(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is not null && key.GetValue(RunValueName) is not null)
                key.DeleteValue(RunValueName);
        }
        catch (Exception ex)
        {
            return "无法删除已存在的注册表 Run 键：" + ex.Message;
        }

        if (!enabled) return DeleteTask();

        string? exe = ExePath;
        if (exe is null)
            return "无法确定自启程序路径（当前不是以 Island.App.exe 方式运行），未创建计划任务";
        if (!File.Exists(exe))
            return $"自启程序不存在：{exe}，未创建计划任务";
        if (!IsAdminProtectedLocation(exe))
            return "出于安全限制，仅安装在 Program Files 目录时才创建管理员级计划任务";

        var (code, _, err) = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\"\" /F");
        return code == 0 ? null : (string.IsNullOrWhiteSpace(err) ? "计划任务创建失败" : err.Trim());
    }

    private static string? DeleteTask()
    {
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
                StandardOutputEncoding = ControlService.ConsoleEncoding,
                StandardErrorEncoding = ControlService.ConsoleEncoding,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "无法启动 schtasks");
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
