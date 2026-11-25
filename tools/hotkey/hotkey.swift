import Carbon
import Foundation

// 全局快捷键探针：常驻进程，注册系统级热键，触发时在 stdout 输出一行。
// 供 MyIsland 主程序读取并派发到 UI。
//
// 为什么用 Swift 而不是 C# P/Invoke：Carbon 的 opaque 句柄是 4 字节（UInt32），
// C# 里把 EventHandlerRef 声明成 IntPtr 会让 ABI 错位 → InstallEventHandler 里
// 直接段错误、进程被杀（连 .NET 异常都来不及抛，实测卡了三轮）。
// Swift 直接调 Carbon 是官方支持的，签名由编译器保证。
//
// 协议（stdin 逐行）：
//   register <id> <keyChar> <mods>   mods: c=cmd a=alt s=shift k=ctrl
//   unregister <id>
// 触发时 stdout 输出： FIRED <id>
// 启动时 stdout 输出： READY

var hotKeys: [UInt32: EventHotKeyRef] = [:]
var eventHandler: EventHandlerRef?

// kEventClassKeyboard / kEventHotKeyPressed
private let kMyEventClass: UInt32 = 0x6B65726E   // 'kern'（Swift 里 OSType 就是 UInt32）
private let kMyHotKeyPressed: UInt32 = 9

let eventHandlerProc: EventHandlerUPP = { _, event, _ in
    var hotKeyID = EventHotKeyID()
    let status = GetEventParameter(event, EventParamName(kEventParamDirectObject),
                                   EventParamType(typeEventHotKeyID), nil,
                                   MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)
    if status == noErr {
        print("FIRED \(hotKeyID.id)")
        fflush(stdout)
    }
    return noErr
}

func installHandler() {
    var spec = EventTypeSpec(eventClass: kMyEventClass, eventKind: kMyHotKeyPressed)
    // Swift 的签名：InstallEventHandler(target, handlerProc, numTypes, eventTypes, userData, handlerRef)
    let st = InstallEventHandler(GetApplicationEventTarget(), eventHandlerProc, 1, &spec, nil, &eventHandler)
    if st != noErr { FileHandle.standardError.write("install handler failed: \(st)\n".data(using: .utf8)!) }
}

func modifiers(from s: String) -> UInt32 {
    var m: UInt32 = 0
    if s.contains("c") { m |= UInt32(cmdKey) }
    if s.contains("a") { m |= UInt32(optionKey) }
    if s.contains("s") { m |= UInt32(shiftKey) }
    if s.contains("k") { m |= UInt32(controlKey) }
    return m
}

installHandler()
print("READY")
fflush(stdout)

// 自测：ISLAND_HK_SELFTEST=1 时，注册完 2 秒自动触发一次 id=1，
// 用于验证「探针 → 主程序 → UI」这条链路（合成按键无法触发系统热键）。
let selfTest = ProcessInfo.processInfo.environment["ISLAND_HK_SELFTEST"] == "1"
if selfTest {
    DispatchQueue.global().asyncAfter(deadline: .now() + 2.0) {
        print("FIRED 1")
        fflush(stdout)
    }
}

// stdin 结束（EOF）后不立刻退出：自测模式要等定时器触发；正常模式等一小会儿再退。
// 只有收到 "quit" 才立即退出。
var running = true
while running {
    guard let line = readLine(strippingNewline: true) else {
        // EOF：自测模式再等 3 秒让定时器触发，否则直接退
        if selfTest { Thread.sleep(forTimeInterval: 3.0) }
        break
    }
    let parts = line.split(separator: " ").map(String.init)
    guard let cmd = parts.first else { continue }
    switch cmd {
    case "register":
        guard parts.count >= 4, let id = UInt32(parts[1]), let ch = parts[2].first else { continue }
        let mods = modifiers(from: parts[3])
        // EventHotKeyRef 是 UInt32 的 opaque 句柄（Swift 导入成 struct，要用 bitPattern 构造）
        var ref = EventHotKeyRef(bitPattern: 0)
        let keyCode = UInt32(ch.uppercased().unicodeScalars.first!.value)
        // 'MYIS' 的 big-endian 四字节
        let hotKeyID = EventHotKeyID(signature: 0x4D59_4953, id: id)   // 'MYIS' 
        let st = RegisterEventHotKey(keyCode, mods, hotKeyID, GetApplicationEventTarget(), 0, &ref)
        if st == noErr { hotKeys[id] = ref; print("OK \(id)") }
        else { print("ERR \(id) \(st)") }
        fflush(stdout)
    case "unregister":
        guard parts.count >= 2, let id = UInt32(parts[1]), let ref = hotKeys.removeValue(forKey: id) else { continue }
        UnregisterEventHotKey(ref)
        print("OK \(id)")
        fflush(stdout)
    case "fire":
        // 调试用：手动触发一个热键（合成按键无法触发系统热键时用于验证链路）
        if parts.count >= 2, let id = UInt32(parts[1]) {
            print("FIRED \(id)")
            fflush(stdout)
        }
    case "quit":
        running = false
    default:
        continue
    }
}
