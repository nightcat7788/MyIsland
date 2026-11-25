namespace Island.App.Services.Peripheral;

/// <summary>
/// L4：通用识别兜底。没有厂商协议时，至少把设备列出来，电量显示"未知"（绝不假装有读数）。
///
/// 识别依据两类：
///  · 名称关键词（2.4G / Wireless / Receiver / Dongle / 无线 / 接收器）—— 2.4G 接收器的典型特征；
///  · 顶层 usage（Generic Desktop 的 Mouse / Keyboard）。
/// </summary>
public static class PeripheralClassifier
{
    private static readonly string[] WirelessMarkers =
    {
        "2.4g", "wireless", "receiver", "dongle", "无线", "接收器", "无线适配器", "无线键鼠",
    };

    /// <summary>文本里是否带"无线/接收器"这类特征词。</summary>
    public static bool LooksWireless(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string lower = text.ToLowerInvariant();
        foreach (string marker in WirelessMarkers)
            if (lower.Contains(marker)) return true;
        return false;
    }

    /// <summary>
    /// 猜设备类型（只影响图标与文案，猜不出就 "other"，不硬安成别的类型）。
    /// 名称关键词优先于 usage —— dongle 的名字比它的顶层 usage 更有信息量。
    /// </summary>
    public static string KindOf(string? product, string? deviceDesc, ushort usagePage, ushort usage)
    {
        string text = ((product ?? "") + " " + (deviceDesc ?? "")).ToLowerInvariant();
        if (text.Contains("mouse") || text.Contains("鼠标")) return "mouse";
        if (text.Contains("keyboard") || text.Contains("键盘")) return "keyboard";
        if (text.Contains("headset") || text.Contains("headphone") || text.Contains("earphone")
            || text.Contains("earbud") || text.Contains("耳机")) return "headset";
        if (text.Contains("gamepad") || text.Contains("controller") || text.Contains("手柄")) return "gamepad";
        if (usagePage == 0x0001)
        {
            if (usage == 0x0002) return "mouse";
            if (usage is 0x0006 or 0x0007) return "keyboard";
        }
        return "other";
    }

    /// <summary>
    /// 一组集合（同一台物理设备）的合并名字：产品名优先，其次设备描述。
    /// </summary>
    public static string DisplayName(IEnumerable<HidInterface> members)
    {
        foreach (var m in members) if (!string.IsNullOrWhiteSpace(m.Product)) return m.Product.Trim();
        foreach (var m in members) if (!string.IsNullOrWhiteSpace(m.DeviceDesc)) return m.DeviceDesc.Trim();
        return "HID 设备";
    }
}
