using System.Diagnostics;
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

    private static string ExePath =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? System.AppContext.BaseDirectory + "Island.App.exe";

    public static bool IsEnabled(bool elevated)
        => elevated ? TaskExists() : RunKeyExists();

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

    private static bool RunKeyExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string s && s.Contains("Island.App", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SetRunKey(bool enabled)
    {
        // 普通模式下清掉可能残留的计划任务，避免双开
        DeleteTask();
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return "无法打开注册表 Run 键";
        if (enabled) key.SetValue(RunValueName, $"\"{ExePath}\"");
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
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
        // 管理员模式下清掉 Run 键，避免双开
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);

        if (!enabled) { DeleteTask(); return null; }

        var (code, _, err) = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{ExePath}\\\"\" /F");
        return code == 0 ? null : (string.IsNullOrWhiteSpace(err) ? "计划任务创建失败" : err.Trim());
    }

    private static void DeleteTask()
        => RunSchtasks($"/Delete /TN \"{TaskName}\" /F");

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
            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
