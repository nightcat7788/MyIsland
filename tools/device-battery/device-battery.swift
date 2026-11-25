import IOKit
import IOKit.hid
import Foundation

// 设备电量探针（对齐 BrimDeck 的「设备电量」页）：列出本机可读的电池设备。
//
// 覆盖三类来源：
//   1) AppleDeviceManagementHIDEventService / IOBluetoothHIDDevice —— 蓝牙鼠标/键盘/耳机/手柄
//      电量在 "BatteryPercent"（0..100），"BatteryIsCharging" 为 1 表示充电中。
//   2) AppleSmartBattery —— 内置电池（CurrentCapacity / MaxCapacity）。
//   3) IOHIDManager —— USB / 2.4G 接收器：只搬运 HID 原始信息（见下）。
//
// 输出：每行一条，制表符分隔：
//   <类型>\t<名称>\t<电量%>\t<充电中 0|1>\t<设备ID>
// 取不到电量的设备不输出（绝不编造数值）。
//
// ---- USB / 2.4G 接收器：HID 原始信息（新增两类行）----
//
//   HIDDEV\t<vid>\t<pid>\t<usagePage>\t<usage>\t<产品名>\t<序列号>\t<描述符hex>
//   HIDFTR\t<vid>\t<pid>\t<reportId>\t<报告字节hex>
//
// 【约定一 · 只搬运，不判定】
//   探针只把 HID 的原始字节搬出来，**不判断「哪个字节是电量」** ——
//   协议表、匹配、读数解析、三层校验全在 C# 侧共享层里做，与 Windows 端**同一份代码**。
//   所以这个文件里不允许出现任何「这个字节是电量 / 这个值是百分比」的判断。
//
// 【约定二 · 报告字节的第 0 字节是报告 ID】
//   HIDFTR 的「报告字节hex」第 0 个字节就是**报告 ID 字节**
//   （与 Windows HidD_GetFeature 同一约定）：C# 侧协议表的偏移量是相对
//   「含报告 ID 的整包」算的，漏掉这个字节会整体错位。
//
// 字段格式：vid/pid = 4 位大写十六进制、无 0x 前缀；usagePage/usage = 大写十六进制、不补零；
// 描述符与报告字节 = 大写十六进制、空格分隔（两者一致）；产品名/序列号里的 TAB/换行换成空格，
// 取不到就留空（描述符取不到时该行以 TAB 结尾）。

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

// ============================================================
// HID 原始信息（USB / 2.4G 接收器）—— 只搬运，不判定
// ============================================================

/// 一个 HID 设备的身份信息（只搬运，不做任何协议判断）。
struct HidDeviceInfo {
    var vid: Int = 0
    var pid: Int = 0
    var usagePage: Int = 0
    var usage: Int = 0
    var product: String = ""
    var serial: String = ""
    var descriptor: [UInt8] = []
}

/// 报告描述符里扫出来的 Feature 报告：报告 ID + 数据长度（字节，**不含**报告 ID 字节）。
struct FeatureReportSpec {
    var reportId: Int = 0
    var byteLength: Int = 0
}

// ---- 小工具：十六进制 / 字段清洗（不引第三方库）----

let HEX_CHARS: [Character] = Array("0123456789ABCDEF")

/// 固定位数的大写十六进制（vid/pid 用 4 位）。
func hexFixed(_ value: Int, _ digits: Int) -> String {
    var v = value
    if v < 0 { v = 0 }
    var buf = [Character](repeating: "0", count: digits)
    var i = digits - 1
    while v > 0 && i >= 0 {
        buf[i] = HEX_CHARS[v & 0x0F]
        v >>= 4
        i -= 1
    }
    return String(buf)
}

/// 不补零的大写十六进制（usagePage/usage 用）。
func hexLoose(_ value: Int) -> String {
    if value <= 0 { return "0" }
    var v = value
    var out = ""
    while v > 0 {
        out.append(HEX_CHARS[v & 0x0F])
        v >>= 4
    }
    return String(out.reversed())
}

/// 字节数组 → 大写十六进制，空格分隔。
func hexBytes(_ bytes: [UInt8]) -> String {
    var out = ""
    for (i, b) in bytes.enumerated() {
        if i > 0 { out.append(" ") }
        out.append(HEX_CHARS[Int(b >> 4)])
        out.append(HEX_CHARS[Int(b & 0x0F)])
    }
    return out
}

/// 字段里的 TAB / 换行会破坏行格式，统一换成空格。
func sanitizeField(_ s: String) -> String {
    var out = ""
    for ch in s {
        if ch == "\t" || ch == "\n" || ch == "\r" { out.append(" ") } else { out.append(ch) }
    }
    return out
}

// ---- HID 属性读取 ----
//
// 【不确定点】IOHIDDeviceGetProperty 的返回值在 Swift 里可能是 Unmanaged<CFTypeRef>!
// （头文件标注了 CF_RETURNS_RETAINED），也可能是 CFTypeRef! —— 取决于 SDK 的导入形态。
// 这里统一用 Any 中转再按需取出，两种形态都能工作；属性键同样用 Any 收
// （既可能是 String 常量，也可能是 CFString）。

/// 取 HID 设备属性（返回值按需 as? Int / String / Data 转换）。
func hidProperty(_ device: IOHIDDevice, _ key: Any) -> Any? {
    let cfKey: CFString
    if let s = key as? String {
        cfKey = s as CFString
    } else if let c = key as? CFString {
        cfKey = c
    } else {
        return nil
    }
    let raw: Any = IOHIDDeviceGetProperty(device, cfKey)
    if let u = raw as? Unmanaged<CFTypeRef> { return u.takeRetainedValue() }
    return raw
}

func hidPropertyInt(_ device: IOHIDDevice, _ key: Any) -> Int {
    guard let v = hidProperty(device, key) else { return 0 }
    if let n = v as? Int { return n }
    if let n = v as? NSNumber { return n.intValue }
    return 0
}

func hidPropertyString(_ device: IOHIDDevice, _ key: Any) -> String {
    guard let v = hidProperty(device, key) else { return "" }
    if let s = v as? String { return s }
    if let s = v as? NSString { return s as String }
    return ""
}

func hidPropertyData(_ device: IOHIDDevice, _ key: Any) -> [UInt8] {
    guard let v = hidProperty(device, key) else { return [] }
    if let d = v as? Data { return [UInt8](d) }
    if let d = v as? NSData { return [UInt8](d as Data) }
    if let d = v as? [UInt8] { return d }
    return []
}

// ---- 报告描述符扫描 ----

/// 读 item 数据里的 little-endian 数值（Report Size / Report Count 是 1–2 字节）。
func littleEndianValue(_ data: [UInt8]) -> Int {
    var v = 0
    for (i, b) in data.enumerated() {
        if i >= 4 { break }   // 位移不能超过 Int 位宽，否则 Swift 会 trap
        v |= Int(b) << (8 * i)
    }
    return v
}

/// 从 HID 报告描述符里扫出 Feature 报告的 ID 集合与数据长度。
///
/// 描述符是一串 item。短项 = 1 字节头 + 数据，头字节的位域是：
///   bit 7..4 = bTag，bit 3..2 = bType（0=Main / 1=Global / 2=Local），bit 1..0 = bSize
///   ⇒ bSize = head & 0x03，bType = (head >> 2) & 0x03，bTag = (head >> 4) & 0x0F
///   （例：Feature 项的前缀字节是 0xB1 = bTag 0x0B | bType 0 | bSize 1）
/// 长项（bSize==3）= 2 字节头 + 1 字节长度 + 数据；长项的 bTag/bType 无意义（HID 规范），
/// 整条跳过。
///
/// 这里只认这几类 item：
///   Global Report ID   (bType=1, bTag=0x08) —— 当前报告 ID
///   Global Report Size (bType=1, bTag=0x07) / Report Count (bType=1, bTag=0x09)
///   Main  Feature       (bType=0, bTag=0x0B) —— 一个 Feature 报告开始
/// 位偏移 / 逻辑值域之类留给 C# 侧共享层，这里不算。
func scanFeatureReports(_ desc: [UInt8]) -> [FeatureReportSpec] {
    var order: [Int] = []
    var bitsById: [Int: Int] = [:]
    var currentId = 0
    var reportSize = 0
    var reportCount = 0
    var i = 0
    while i < desc.count {
        let prefix = desc[i]
        let bSize = Int(prefix & 0x03)
        let bType = Int((prefix >> 2) & 0x03)
        let bTag = Int((prefix >> 4) & 0x0F)
        var next = i + 1
        if bSize == 3 {
            // 长 item：2 字节头 + 1 字节长度 + 数据；bTag/bType 无意义，整条跳过
            guard i + 2 < desc.count else { break }
            let size = Int(desc[i + 1])
            next = min(i + 2 + size, desc.count)
        } else {
            let end = min(i + 1 + bSize, desc.count)
            let data = Array(desc[(i + 1)..<end])
            next = end
            if bType == 1 {
                // Global item
                if bTag == 0x08 { currentId = data.first.map { Int($0) } ?? 0 }
                else if bTag == 0x07 { reportSize = littleEndianValue(data) }
                else if bTag == 0x09 { reportCount = littleEndianValue(data) }
            } else if bType == 0 && bTag == 0x0B {
                // Main item: Feature 报告
                let bits = reportSize * reportCount
                if bitsById[currentId] == nil { order.append(currentId) }
                bitsById[currentId] = (bitsById[currentId] ?? 0) + bits
            }
        }
        if next <= i { break }   // 防呆：绝不死循环
        i = next
    }
    var out: [FeatureReportSpec] = []
    for id in order {
        let bits = bitsById[id] ?? 0
        out.append(FeatureReportSpec(reportId: id, byteLength: max((bits + 7) / 8, 1)))
    }
    return out
}

// ---- Feature 报告读取 ----

/// 读一条 Feature 报告，返回「含报告 ID 字节」的整包。
///
/// 【约定】第 0 个字节是报告 ID —— macOS 的 IOHIDDeviceGetReport 把报告 ID 作为
/// **独立参数**，返回的缓冲区里**不含**报告 ID 字节；而 C# 侧协议表的偏移量是相对
/// 「含报告 ID 的整包」算的（与 Windows HidD_GetFeature 同约定），
/// 所以这里手工把报告 ID 补到第 0 字节。
///
/// 【不确定点】若实测发现 macOS 返回的缓冲区里已经带了报告 ID 字节，
/// 把下面 return 里的 [UInt8(...)] + … 改成直接返回 buf 即可。
func readFeatureReport(_ device: IOHIDDevice, _ spec: FeatureReportSpec, _ maxFeatureSize: Int) -> [UInt8]? {
    // 缓冲区长度必须与设备实际的 Feature 报告长度一致（长度不对 IOKit 会报
    // -536850432），所以先按描述符算，再用设备属性 MaxFeatureReportSize 兜一次。
    var candidates: [Int] = []
    if spec.byteLength > 0 { candidates.append(spec.byteLength) }
    if maxFeatureSize > 0 && !candidates.contains(maxFeatureSize) { candidates.append(maxFeatureSize) }
    if candidates.isEmpty { candidates.append(32) }
    for len in candidates {
        var buf = [UInt8](repeating: 0, count: len)
        var bufLen = CFIndex(len)
        let res = IOHIDDeviceGetReport(device, kIOHIDReportTypeFeature, CFIndex(spec.reportId), &buf, &bufLen)
        guard res == kIOReturnSuccess else { continue }
        let n = min(Int(bufLen), buf.count)
        guard n > 0 else { continue }
        return [UInt8(spec.reportId & 0xFF)] + Array(buf[0..<n])
    }
    return nil
}

/// 有些设备要先发一次 SET_REPORT 才会把 Feature 报告吐回来。
/// 默认**不发**（探针只读，不碰设备状态）；需要时设环境变量 ISLAND_HID_PRIME_FEATURE=1。
/// 注意 macOS 的 SET_REPORT 缓冲区**不含**报告 ID 字节（ID 是独立参数）。
func primeFeatureReport(_ device: IOHIDDevice, _ reportId: Int, _ length: Int) {
    var buf = [UInt8](repeating: 0, count: max(length, 1))
    _ = IOHIDDeviceSetReport(device, kIOHIDReportTypeFeature, CFIndex(reportId), &buf, CFIndex(buf.count))
    usleep(120_000)
}

// ---- manager / 设备枚举 ----

/// 创建 HID manager（全量枚举，不做 usage 过滤 —— 过滤规则在 C# 侧协议表里）。
/// 【不确定点】IOHIDManagerCreate 的返回值在 Swift 里可能是 IOHIDManager，也可能是
/// IOHIDManager!，这里用 Any 中转统一处理。
func createHidManager() -> IOHIDManager? {
    let raw: Any = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
    guard let manager = raw as? IOHIDManager else { return nil }
    IOHIDManagerSetDeviceMatching(manager, nil)   // nil = 匹配全部
    // 打开失败（缺权限等）不致命：至少枚举本身可能还能用
    _ = IOHIDManagerOpen(manager, IOOptionBits(kIOHIDOptionsTypeNone))
    return manager
}

/// 复制 manager 当前枚举到的设备。
/// 【不确定点】IOHIDManagerCopyDevices 在 Swift 里可能是 CFSet?，也可能被导入成
/// Unmanaged<CFSet>!，这里用 Any 中转，两种形态都能取到。
func copyHidDevices(_ manager: IOHIDManager) -> [IOHIDDevice] {
    let raw: Any = IOHIDManagerCopyDevices(manager)
    if let s = raw as? Set<IOHIDDevice> { return Array(s) }
    if let u = raw as? Unmanaged<CFSet> { return (u.takeRetainedValue() as? Set<IOHIDDevice>) ?? [] }
    if let cs = raw as? CFSet { return (cs as? Set<IOHIDDevice>) ?? [] }
    return []
}

/// 读一个 HID 设备的身份信息。
/// 注意：描述符属性键用字面量 "ReportDescriptor"（等价于 kIOHIDReportDescriptorKey）。
func readHidDeviceInfo(_ device: IOHIDDevice) -> HidDeviceInfo {
    var info = HidDeviceInfo()
    info.vid = hidPropertyInt(device, kIOHIDVendorIDKey)
    info.pid = hidPropertyInt(device, kIOHIDProductIDKey)
    info.usagePage = hidPropertyInt(device, kIOHIDPrimaryUsagePageKey)
    info.usage = hidPropertyInt(device, kIOHIDPrimaryUsageKey)
    info.product = hidPropertyString(device, kIOHIDProductKey)
    info.serial = hidPropertyString(device, kIOHIDSerialNumberKey)
    info.descriptor = hidPropertyData(device, "ReportDescriptor")
    return info
}

var devices: [Device] = []

// ---- 蓝牙 / USB HID 设备电量 ----
for className in ["AppleDeviceManagementHIDEventService", "IOBluetoothHIDDevice"] {
    var it: io_iterator_t = 0
    guard let matching = IOServiceMatching(className),
          IOServiceGetMatchingServices(kIOMainPortDefault, matching, &it) == KERN_SUCCESS else { continue }
    while true {
        let svc = IOIteratorNext(it)
        if svc == 0 { break }
        // 设备名：Product / DeviceAddress / 父节点的 Product
        var name = propStr(svc, "Product")
        if name.isEmpty { name = propStr(svc, "DeviceAddress") }
        if name.isEmpty { name = "蓝牙设备" }
        // 电量
        var pct = propInt(svc, "BatteryPercent")
        if pct == nil || pct! < 0 {
            // 某些设备把电量放在 "BatteryLevel" 或父节点的 BatteryPercent
            pct = propInt(svc, "BatteryLevel")
        }
        let charging = (propInt(svc, "BatteryIsCharging") ?? 0) == 1
        let id = propStr(svc, "DeviceAddress")
        if let p = pct, p >= 0, p <= 100 {
            devices.append(Device(kind: className == "IOBluetoothHIDDevice" ? "蓝牙" : "HID",
                                  name: name, percent: p, charging: charging, id: id.isEmpty ? name : id))
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
        let max = propInt(svc, "MaxCapacity")
        if let c = cur, let mx = max, mx > 0 {
            let pct = Int((Double(c) / Double(mx) * 100).rounded())
            var charging = false
            if let isCharging = propInt(svc, "IsCharging"), isCharging == 1 { charging = true }
            devices.append(Device(kind: "内置", name: "内置电池", percent: pct, charging: charging, id: "internal"))
        }
        IOObjectRelease(svc)
    }
    IOObjectRelease(itb)
}

// ---- USB / 2.4G 接收器：HID 原始信息（只搬运，不判定）----

// 已经报过电量的设备名：同名设备不再输出 HIDDEV 行（避免同一个蓝牙鼠标出现两行）。
var batteryNames: Set<String> = []
for d in devices {
    if d.name.isEmpty || d.name == "蓝牙设备" { continue }
    batteryNames.insert(d.name)
}

var hidLines: [String] = []
let hidStart = Date()
let hidBudget: TimeInterval = 1.5    // 整体时间预算：GetReport 是同步阻塞的
let primeFeature = ProcessInfo.processInfo.environment["ISLAND_HID_PRIME_FEATURE"] == "1"

// manager 要活到所有设备都用完（设备对象在 manager 关闭后会失效），所以放到最后统一关。
var hidManager: IOHIDManager? = createHidManager()
let hidDevices = hidManager.map { copyHidDevices($0) } ?? []

for dev in hidDevices {
    if Date().timeIntervalSince(hidStart) > hidBudget { break }
    let info = readHidDeviceInfo(dev)
    // 去重：已经报过电量的同名设备，整个跳过（不再输出 HIDDEV 行）
    if !info.product.isEmpty && batteryNames.contains(info.product) { continue }
    hidLines.append("HIDDEV\t\(hexFixed(info.vid, 4))\t\(hexFixed(info.pid, 4))"
        + "\t\(hexLoose(info.usagePage))\t\(hexLoose(info.usage))"
        + "\t\(sanitizeField(info.product))\t\(sanitizeField(info.serial))"
        + "\t\(hexBytes(info.descriptor))")
    // Feature 报告：只试描述符里声明出来的 ID（通常 0–3 个），不做无节制穷举
    let specs = scanFeatureReports(info.descriptor)
    if specs.isEmpty { continue }
    let maxFeature = hidPropertyInt(dev, kIOHIDMaxFeatureReportSizeKey)
    let opened = IOHIDDeviceOpen(dev, IOOptionBits(kIOHIDOptionsTypeNone)) == kIOReturnSuccess
    for spec in specs.prefix(8) {
        if Date().timeIntervalSince(hidStart) > hidBudget { break }
        if primeFeature { primeFeatureReport(dev, spec.reportId, spec.byteLength) }
        // 读不到就跳过（很多设备不支持 Feature 报告），绝不报错退出
        guard let payload = readFeatureReport(dev, spec, maxFeature) else { continue }
        hidLines.append("HIDFTR\t\(hexFixed(info.vid, 4))\t\(hexFixed(info.pid, 4))"
            + "\t\(spec.reportId)\t\(hexBytes(payload))")
    }
    if opened { IOHIDDeviceClose(dev, IOOptionBits(kIOHIDOptionsTypeNone)) }
}
if let m = hidManager { IOHIDManagerClose(m, IOOptionBits(kIOHIDOptionsTypeNone)) }

// ---- 输出 ----
for d in devices {
    print("\(d.kind)\t\(d.name)\t\(d.percent)\t\(d.charging ? 1 : 0)\t\(d.id)")
}
// HID 原始信息：HIDDEV = 设备身份 + 报告描述符；HIDFTR = 一条 Feature 报告的原始字节
for line in hidLines {
    print(line)
}
