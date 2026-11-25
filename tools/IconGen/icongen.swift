import AppKit
extension NSColor { convenience init(hex: UInt32) { self.init(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255, blue: CGFloat(hex & 0xFF) / 255, alpha: 1) } }

// 方案 A 定稿：胶囊岛 + 270° 仪表环 + 三条读数条。全部矢量绘制，每个尺寸独立渲染（不缩放），小尺寸专门调参。
func draw(_ s: CGFloat) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(s), pixelsHigh: Int(s), bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    let small = s <= 64   // 小尺寸：线条加粗、去掉细节，保证像素级清晰

    // macOS 图标规范：1024 画布内图形占 ~824（约 80%），四周留透明边距
    let canvas = s, inset = s * 0.1, body = canvas - inset * 2
    let corner = body * 0.225
    let bodyRect = CGRect(x: inset, y: inset, width: body, height: body)
    // 底板渐变
    ctx.saveGState()
    ctx.addPath(CGPath(roundedRect: bodyRect, cornerWidth: corner, cornerHeight: corner, transform: nil)); ctx.clip()
    let g = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: [NSColor(hex: 0x2E3240).cgColor, NSColor(hex: 0x15171E).cgColor] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(g, start: CGPoint(x: 0, y: bodyRect.maxY), end: CGPoint(x: 0, y: bodyRect.minY), options: [])
    ctx.restoreGState()
    // 底板顶部细高光（小尺寸省略）
    if !small {
        ctx.saveGState()
        ctx.addPath(CGPath(roundedRect: bodyRect, cornerWidth: corner, cornerHeight: corner, transform: nil)); ctx.clip()
        let hg = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: [NSColor(white: 1, alpha: 0.10).cgColor, NSColor(white: 1, alpha: 0).cgColor] as CFArray, locations: [0, 1])!
        ctx.drawLinearGradient(hg, start: CGPoint(x: 0, y: bodyRect.maxY), end: CGPoint(x: 0, y: bodyRect.midY), options: [])
        ctx.restoreGState()
    }
    // 胶囊岛
    let capW = body * 0.715, capH = body * (small ? 0.40 : 0.357)
    let cap = CGRect(x: (canvas - capW) / 2, y: (canvas - capH) / 2, width: capW, height: capH)
    ctx.addPath(CGPath(roundedRect: cap, cornerWidth: capH / 2, cornerHeight: capH / 2, transform: nil))
    ctx.setFillColor(NSColor(hex: 0x0B0D12).cgColor); ctx.fillPath()

    // 仪表环（270° 开口）
    let cx = cap.minX + capH * 0.52, cy = cap.midY
    let r = capH * (small ? 0.26 : 0.33), lw = capH * (small ? 0.17 : 0.14)
    ctx.setLineCap(.round); ctx.setLineWidth(lw)
    ctx.addArc(center: CGPoint(x: cx, y: cy), radius: r, startAngle: .pi * 0.75, endAngle: .pi * 2.25, clockwise: false)
    ctx.setStrokeColor(NSColor(hex: 0x2A2F3C).cgColor); ctx.strokePath()
    ctx.addArc(center: CGPoint(x: cx, y: cy), radius: r, startAngle: .pi * 0.75, endAngle: .pi * 0.75 + .pi * 1.5 * 0.72, clockwise: false)
    ctx.setStrokeColor(NSColor(hex: 0x5A96FF).cgColor); ctx.strokePath()
    ctx.setFillColor(NSColor(hex: 0xE8ECF4).cgColor)
    let dot = r * (small ? 0.5 : 0.31)
    ctx.fillEllipse(in: CGRect(x: cx - dot, y: cy - dot, width: dot * 2, height: dot * 2))

    // 三条读数条
    let bx = cx + r + lw + capH * 0.18, bw = cap.maxX - bx - capH * 0.16
    let bh = capH * (small ? 0.14 : 0.113), gap = capH * (small ? 0.09 : 0.08)
    let bars: [(CGFloat, UInt32)] = [(0.72, 0x5A96FF), (0.48, 0x4FE3D7), (0.33, 0x3CBE78)]
    for (i, (frac, col)) in bars.enumerated() {
        let y = cy + gap + bh - CGFloat(i) * (bh + gap) - bh   // 从上到下
        for (w, c) in [(bw, 0x2A2F3C as UInt32), (bw * frac, col)] {
            ctx.addPath(CGPath(roundedRect: CGRect(x: bx, y: y, width: w, height: bh), cornerWidth: bh / 2, cornerHeight: bh / 2, transform: nil))
            ctx.setFillColor(NSColor(hex: c).cgColor); ctx.fillPath()
        }
    }
    NSGraphicsContext.restoreGraphicsState()
    return rep
}

// iconset 标准：16/32/128/256/512 各 1x + 2x
let set: [(String, CGFloat)] = [("icon_16x16", 16), ("icon_16x16@2x", 32), ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256), ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024)]
try? FileManager.default.createDirectory(atPath: "island.iconset", withIntermediateDirectories: true)
for (name, s) in set {
    let rep = draw(s)
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "island.iconset/\(name).png"))
}
// 另存一张 1024 给 README/品牌用
try! draw(1024).representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "island-logo-1024.png"))

for sz in [16, 24, 32, 48, 64, 128, 256, 512, 1024] {
    try! draw(CGFloat(sz)).representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "brand-\(sz).png"))
}
print("brand done")
