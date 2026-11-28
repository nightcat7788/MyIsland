import AppKit
import CoreGraphics
import IOKit

// 输出格式：ID|UUID|名称|builtin/external|physical/virtual
//   第 5 列对齐 LunarCN BuiltinDisplayEngine.visibleExternalIDs 的实体屏判据：
//   非虚拟设备(kCGDisplayIsVirtualDevice) + 非 AirPlay(kCGDisplayIsAirPlay) + 有 IODisplayLocation。
//   关闭内建屏前只认"可见的实体外接屏"，否则虚拟屏/AirPlay 会让校验放行 → 整机黑屏。
//
// 默认模式（NSScreen.screens）：只列当前可见的屏；内建屏被关闭后不会出现。
// --online 模式（CGGetOnlineDisplayList）：含已关闭但仍连接的屏，用于判断"机器有没有内建屏"。
let onlineOnly = CommandLine.arguments.contains("--online")

typealias DisplayInfo = @convention(c) (UInt32) -> Unmanaged<CFDictionary>?
private let coreDisplay = dlopen("/System/Library/Frameworks/CoreDisplay.framework/CoreDisplay", RTLD_LAZY)
private let infoSymbol = coreDisplay.flatMap { dlsym($0, "CoreDisplay_DisplayCreateInfoDictionary") }

/// 实体屏判据（与 LunarCN 一致）。读不到信息字典时保守判为 false。
func isPhysical(_ id: UInt32) -> Bool {
    guard let infoSymbol else { return false }
    guard let dict = unsafeBitCast(infoSymbol, to: DisplayInfo.self)(id)?.takeRetainedValue() as? [String: Any] else { return false }
    if dict["kCGDisplayIsVirtualDevice"] as? Bool == true { return false }
    if dict["kCGDisplayIsAirPlay"] as? Bool == true { return false }
    guard let location = dict["IODisplayLocation"] as? String else { return false }
    let service = IORegistryEntryCopyFromPath(kIOMainPortDefault, location as CFString)
    guard service != MACH_PORT_NULL else { return false }
    IOObjectRelease(service)
    return true
}

func sanitizeName(_ raw: String) -> String {
    raw.replacingOccurrences(of: "|", with: "/")
       .replacingOccurrences(of: "\r", with: " ")
       .replacingOccurrences(of: "\n", with: " ")
       .trimmingCharacters(in: .whitespacesAndNewlines)
}

func emit(_ id: UInt32, _ name: String, _ builtin: Bool) {
    guard let u = CGDisplayCreateUUIDFromDisplayID(id)?.takeRetainedValue() else { return }
    let uuid = CFUUIDCreateString(nil, u) as String
    let kind = isPhysical(id) ? "physical" : "virtual"
    let clean = sanitizeName(name)
    print("\(id)|\(uuid)|\(clean.isEmpty ? "Display" : clean)|\(builtin ? "builtin" : "external")|\(kind)")
}

if onlineOnly {
    var ids = [UInt32](repeating: 0, count: 32)
    var count: UInt32 = 0
    guard CGGetOnlineDisplayList(32, &ids, &count) == .success else { exit(1) }
    for id in ids.prefix(Int(count)) {
        let name = CGDisplayIsBuiltin(id) != 0 ? "Built-in Display" : "External Display"
        emit(id, name, CGDisplayIsBuiltin(id) != 0)
    }
} else {
    for s in NSScreen.screens {
        guard let n = s.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { continue }
        emit(n.uint32Value, s.localizedName, CGDisplayIsBuiltin(n.uint32Value) != 0)
    }
}
