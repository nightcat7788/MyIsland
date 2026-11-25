using System.Runtime.InteropServices;

namespace Island.Avalonia;

/// <summary>
/// 触觉/音效反馈（借鉴 NotchBar 的 NSHapticFeedbackManager 用法）。
/// macOS 用 AudioToolbox 的 AudioServicesPlaySystemSound（纯 C API，无 ObjC 对象 ABI 风险）；
/// 其他平台没有等价 API，静默跳过。
/// 用途：悬停进入面板时给一次轻"咔哒"，让界面"活了一下"。
/// </summary>
internal static class Haptics
{
    // kSystemSoundID_Vibrate / 点击类：1103 = 轻点击（用户界面敲击），1520 = 渐强提示音
    private const uint ClickSoundId = 1103;

    /// <summary>悬停进入的轻反馈。非 macOS 直接返回。</summary>
    public static void Alignment()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try { AudioServicesPlaySystemSound(ClickSoundId); }
        catch (Exception ex) { Console.Error.WriteLine($"[haptics] {ex.Message}"); }
    }

    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
    private static extern void AudioServicesPlaySystemSound(uint inSystemSoundID);
}
