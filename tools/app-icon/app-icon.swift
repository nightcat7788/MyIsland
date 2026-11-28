import AppKit
import Foundation

// 应用图标探针：从 stdin 逐行读可执行路径，每行输出  "<路径>\t<base64 PNG>\n"（取不到图标则 base64 为空）。
// 用 NSWorkspace 的图标缓存：同一个路径只解析一次，结果按路径缓存（进程生命周期内）。
// 供 MyIsland 应用榜显示真实应用图标（对齐 BrimDeck）。

var cache: [String: String] = [:]
let out = FileHandle.standardOutput
func emit(_ path: String, _ b64: String) {
    out.write(Data((path + "\t" + b64 + "\n").utf8))
}
while let line = readLine(strippingNewline: true) {
    autoreleasepool {
        let path = line.trimmingCharacters(in: .whitespacesAndNewlines)
        if path.isEmpty { return }
        if let hit = cache[path] { emit(path, hit); return }
        var b64 = ""
        // 只给 .app 包出图标：NSWorkspace.icon(forFile:) 对普通可执行文件（如 /bin/zsh）会
        // 回退到默认应用图标——那会造成"所有命令行工具都显示同一个图标"的假象。
        // 非 .app 路径输出空，.NET 侧用首字母兜底。
        var appURL: URL? = nil
        var probe = URL(fileURLWithPath: path).deletingLastPathComponent()
        for _ in 0..<4 {
            if probe.pathExtension == "app" { appURL = probe; break }
            let parent = probe.deletingLastPathComponent()
            if parent.path == probe.path { break }
            probe = parent
        }
        guard let url = appURL else {
            cache[path] = ""
            emit(path, "")
            return
        }
        let icon = NSWorkspace.shared.icon(forFile: url.path)
        if let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: 32,
            pixelsHigh: 32,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ), let ctx = NSGraphicsContext(bitmapImageRep: rep) {
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = ctx
            ctx.imageInterpolation = .high
            icon.draw(in: NSRect(x: 0, y: 0, width: 32, height: 32),
                      from: .zero,
                      operation: .copy,
                      fraction: 1.0)
            NSGraphicsContext.restoreGraphicsState()
            if let png = rep.representation(using: .png, properties: [:]) {
                b64 = png.base64EncodedString()
            }
        }
        cache[path] = b64
        emit(path, b64)
    }
}
