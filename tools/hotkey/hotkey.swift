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

// kEventClassKeyboard ('keyb') / kEventHotKeyPressed (5)
private let kMyEventClass: UInt32 = UInt32(kEventClassKeyboard)
private let kMyHotKeyPressed: UInt32 = UInt32(kEventHotKeyPressed)

func virtualKeyCode(for raw: String) -> UInt32? {
    switch raw.uppercased() {
    case "A": return UInt32(kVK_ANSI_A)
    case "B": return UInt32(kVK_ANSI_B)
    case "C": return UInt32(kVK_ANSI_C)
    case "D": return UInt32(kVK_ANSI_D)
    case "E": return UInt32(kVK_ANSI_E)
    case "F": return UInt32(kVK_ANSI_F)
    case "G": return UInt32(kVK_ANSI_G)
    case "H": return UInt32(kVK_ANSI_H)
    case "I": return UInt32(kVK_ANSI_I)
    case "J": return UInt32(kVK_ANSI_J)
    case "K": return UInt32(kVK_ANSI_K)
    case "L": return UInt32(kVK_ANSI_L)
    case "M": return UInt32(kVK_ANSI_M)
    case "N": return UInt32(kVK_ANSI_N)
    case "O": return UInt32(kVK_ANSI_O)
    case "P": return UInt32(kVK_ANSI_P)
    case "Q": return UInt32(kVK_ANSI_Q)
    case "R": return UInt32(kVK_ANSI_R)
    case "S": return UInt32(kVK_ANSI_S)
    case "T": return UInt32(kVK_ANSI_T)
    case "U": return UInt32(kVK_ANSI_U)
    case "V": return UInt32(kVK_ANSI_V)
    case "W": return UInt32(kVK_ANSI_W)
    case "X": return UInt32(kVK_ANSI_X)
    case "Y": return UInt32(kVK_ANSI_Y)
    case "Z": return UInt32(kVK_ANSI_Z)
    case "0": return UInt32(kVK_ANSI_0)
    case "1": return UInt32(kVK_ANSI_1)
    case "2": return UInt32(kVK_ANSI_2)
    case "3": return UInt32(kVK_ANSI_3)
    case "4": return UInt32(kVK_ANSI_4)
    case "5": return UInt32(kVK_ANSI_5)
    case "6": return UInt32(kVK_ANSI_6)
    case "7": return UInt32(kVK_ANSI_7)
    case "8": return UInt32(kVK_ANSI_8)
    case "9": return UInt32(kVK_ANSI_9)
    case "SPACE": return UInt32(kVK_Space)
    case "-", "MINUS": return UInt32(kVK_ANSI_Minus)
    case "=", "EQUAL": return UInt32(kVK_ANSI_Equal)
    case "[": return UInt32(kVK_ANSI_LeftBracket)
    case "]": return UInt32(kVK_ANSI_RightBracket)
    case "\\": return UInt32(kVK_ANSI_Backslash)
    case ";": return UInt32(kVK_ANSI_Semicolon)
    case "'": return UInt32(kVK_ANSI_Quote)
    case ",": return UInt32(kVK_ANSI_Comma)
    case ".": return UInt32(kVK_ANSI_Period)
    case "/": return UInt32(kVK_ANSI_Slash)
    case "`": return UInt32(kVK_ANSI_Grave)
    case "F1": return UInt32(kVK_F1)
    case "F2": return UInt32(kVK_F2)
    case "F3": return UInt32(kVK_F3)
    case "F4": return UInt32(kVK_F4)
    case "F5": return UInt32(kVK_F5)
    case "F6": return UInt32(kVK_F6)
    case "F7": return UInt32(kVK_F7)
    case "F8": return UInt32(kVK_F8)
    case "F9": return UInt32(kVK_F9)
    case "F10": return UInt32(kVK_F10)
    case "F11": return UInt32(kVK_F11)
    case "F12": return UInt32(kVK_F12)
    default: return nil
    }
}

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

// stdin 在后台线程读取，主线程运行 Carbon 事件循环（RunApplicationEventLoop），
// 否则注册的系统热键回调永远不会被派发。
DispatchQueue.global(qos: .userInitiated).async {
    var running = true
    while running {
        guard let line = readLine(strippingNewline: true) else {
            if selfTest { Thread.sleep(forTimeInterval: 3.0) }
            break
        }
        let parts = line.split(separator: " ").map(String.init)
        guard let cmd = parts.first else { continue }
        switch cmd {
        case "register":
            guard parts.count >= 4, let id = UInt32(parts[1]) else { continue }
            guard let keyCode = virtualKeyCode(for: parts[2]) else {
                DispatchQueue.main.async {
                    print("ERR \(id) -50")
                    fflush(stdout)
                }
                continue
            }
            let mods = modifiers(from: parts[3])
            DispatchQueue.main.sync {
                if let old = hotKeys.removeValue(forKey: id) {
                    UnregisterEventHotKey(old)
                }
                var ref: EventHotKeyRef?
                let hotKeyID = EventHotKeyID(signature: 0x4D59_4953, id: id)   // 'MYIS'
                let st = RegisterEventHotKey(keyCode, mods, hotKeyID, GetApplicationEventTarget(), 0, &ref)
                if st == noErr, let validRef = ref {
                    hotKeys[id] = validRef
                    print("OK \(id)")
                } else {
                    print("ERR \(id) \(st)")
                }
                fflush(stdout)
            }
        case "unregister":
            guard parts.count >= 2, let id = UInt32(parts[1]) else { continue }
            DispatchQueue.main.sync {
                if let ref = hotKeys.removeValue(forKey: id) {
                    UnregisterEventHotKey(ref)
                    print("OK \(id)")
                    fflush(stdout)
                }
            }
        case "fire":
            if parts.count >= 2, let id = UInt32(parts[1]) {
                DispatchQueue.main.async {
                    print("FIRED \(id)")
                    fflush(stdout)
                }
            }
        case "quit":
            running = false
        default:
            continue
        }
    }
    DispatchQueue.main.async {
        for (_, ref) in hotKeys { UnregisterEventHotKey(ref) }
        hotKeys.removeAll()
        QuitApplicationEventLoop()
    }
}

RunApplicationEventLoop()
