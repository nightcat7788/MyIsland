import IOKit
import Foundation

// 设备电量探针（对齐 BrimDeck 的「设备电量」页）：列出本机可读的电池设备。
//
// 覆盖两类来源：
//   1) AppleDeviceManagementHIDEventService / IOBluetoothHIDDevice —— 蓝牙鼠标/键盘/耳机/手柄
//      电量在 "BatteryPercent"（0..100），"BatteryIsCharging" 为 1 表示充电中。
//   2) AppleSmartBattery —— 内置电池（CurrentCapacity / MaxCapacity）。
//
// 输出：每行一个设备，制表符分隔：
//   <类型>\t<名称>\t<电量%>\t<充电中 0|1>\t<设备ID>
// 取不到电量的设备不输出（绝不编造数值）。

struct Device {
    var kind: String
    var name: String
    var percent: Int
    var charging: Bool
    var id: String
}

func propInt(_ svc: io_service_t, _ key: String) -> Int? {
    guard let v = IORegistryEntryCreateCFProperty(svc, key as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() else { return nil }
    if let d = v as? NSNumber { return d.intValue }
    return nil
}
func propStr(_ svc: io_service_t, _ key: String) -> String {
    guard let v = IORegistryEntryCreateCFProperty(svc, key as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() else { return "" }
    return (v as? String) ?? ""
}

func cleanField(_ s: String) -> String {
    s.replacingOccurrences(of: "\t", with: " ")
     .replacingOccurrences(of: "\r", with: " ")
     .replacingOccurrences(of: "\n", with: " ")
     .trimmingCharacters(in: .whitespacesAndNewlines)
}

var devices: [Device] = []
var seenKeys: [String: Int] = [:]

// ---- 蓝牙 / USB 2.4G HID 设备电量 ----
for className in ["AppleDeviceManagementHIDEventService", "IOBluetoothHIDDevice", "IOHIDDevice"] {
    var it: io_iterator_t = 0
    guard let matching = IOServiceMatching(className),
          IOServiceGetMatchingServices(kIOMainPortDefault, matching, &it) == KERN_SUCCESS else { continue }
    while true {
        let svc = IOIteratorNext(it)
        if svc == 0 { break }
        var name = cleanField(propStr(svc, "Product"))
        let addr = cleanField(propStr(svc, "DeviceAddress"))
        if name.isEmpty { name = addr }
        if name.isEmpty { name = "蓝牙设备" }
        var pct = propInt(svc, "BatteryPercent")
        if pct == nil || pct! < 0 {
            pct = propInt(svc, "BatteryLevel")
        }
        let charging = (propInt(svc, "BatteryIsCharging") ?? 0) == 1
        let devId = addr.isEmpty ? name : addr
        if let p = pct, p >= 0 {
            let clamped = min(100, max(0, p))
            let dedupKey = (addr.isEmpty ? name : addr).lowercased()
            let candidate = Device(kind: className == "IOBluetoothHIDDevice" ? "蓝牙" : "HID",
                                   name: name, percent: clamped, charging: charging, id: devId)
            if let existingIdx = seenKeys[dedupKey] {
                // 同一蓝牙设备在 HID 与 Bluetooth 服务树同时出现时去重，优先保留“蓝牙”分类与有意义名称
                if devices[existingIdx].kind != "蓝牙" && candidate.kind == "蓝牙" {
                    devices[existingIdx] = candidate
                }
            } else {
                seenKeys[dedupKey] = devices.count
                devices.append(candidate)
            }
        }
        IOObjectRelease(svc)
    }
    IOObjectRelease(it)
}

// ---- 内置电池 ----
var itb: io_iterator_t = 0
if let m = IOServiceMatching("AppleSmartBattery"),
   IOServiceGetMatchingServices(kIOMainPortDefault, m, &itb) == KERN_SUCCESS {
    while true {
        let svc = IOIteratorNext(itb)
        if svc == 0 { break }
        let cur = propInt(svc, "CurrentCapacity")
        let maxCap = propInt(svc, "MaxCapacity")
        if let c = cur, let mx = maxCap, mx > 0 {
            let pct = min(100, max(0, Int((Double(c) / Double(mx) * 100).rounded())))
            var charging = false
            if let isCharging = propInt(svc, "IsCharging"), isCharging == 1 { charging = true }
            if seenKeys["internal"] == nil {
                seenKeys["internal"] = devices.count
                devices.append(Device(kind: "内置", name: "内置电池", percent: pct, charging: charging, id: "internal"))
            }
        }
        IOObjectRelease(svc)
    }
    IOObjectRelease(itb)
}

// ---- 输出 ----
for d in devices {
    print("\(d.kind)\t\(d.name)\t\(d.percent)\t\(d.charging ? 1 : 0)\t\(d.id)")
}
