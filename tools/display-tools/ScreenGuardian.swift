import Foundation
import CoreGraphics

private final class Commands {
    private let lock = NSLock()
    private var confirmed = false
    private var connected = true
    private var restoring = false

    func accept(_ command: String?) {
        lock.lock()
        defer { lock.unlock() }
        if command == "keep" { confirmed = true }
        if command == "restore" { restoring = true }
        if command == nil { connected = false }
    }

    func shouldRestore(externalAvailable: Bool, expired: Bool) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return restoring || ScreenRecoveryPolicy.shouldRestore(parentConnected: connected,
            externalAvailable: externalAvailable, confirmed: confirmed, expired: expired)
    }
}

@main
enum ScreenGuardian {
    static func main() {
        signal(SIGPIPE, SIG_IGN)
        signal(SIGTERM, SIG_IGN)
        signal(SIGINT, SIG_IGN)
        signal(SIGHUP, SIG_IGN)
        setbuf(stdout, nil)
        let arguments = CommandLine.arguments
        guard arguments.count == 3, let id = UInt32(arguments[1]),
              BuiltinDisplayEngine.uuid(id) == arguments[2], CGDisplayIsBuiltin(id) != 0,
              CGDisplayIsActive(id) != 0 else {
            print("ERROR:内置屏幕身份核对失败。")
            exit(1)
        }
        let commands = Commands()
        let sigQueue = DispatchQueue(label: "island.guardian.signals")
        var signalSources: [DispatchSourceSignal] = []
        for sig in [SIGTERM, SIGINT, SIGHUP] {
            let src = DispatchSource.makeSignalSource(signal: sig, queue: sigQueue)
            src.setEventHandler { commands.accept("restore") }
            src.resume()
            signalSources.append(src)
        }
        let parentPid = getppid()
        var procSource: DispatchSourceProcess?
        if parentPid > 1 {
            let ps = DispatchSource.makeProcessSource(identifier: parentPid, eventMask: .exit, queue: sigQueue)
            ps.setEventHandler { commands.accept(nil) }
            ps.resume()
            procSource = ps
            if getppid() != parentPid { commands.accept(nil) }
        } else {
            commands.accept(nil)
        }
        DispatchQueue.global().async {
            while let line = readLine() { commands.accept(line) }
            commands.accept(nil)
        }
        var didDisable = false
        do {
            try BuiltinDisplayEngine.setEnabled(false, id: id)
            didDisable = true
            print("READY")
            let deadline = DispatchTime.now() + 15
            while CGDisplayIsOnline(id) == 0 {
                if commands.shouldRestore(externalAvailable: !BuiltinDisplayEngine.visibleExternalIDs().isEmpty,
                                          expired: DispatchTime.now() >= deadline) { break }
                Thread.sleep(forTimeInterval: 0.25)
            }
        } catch {
            print("ERROR:\(error.localizedDescription)")
        }
        withExtendedLifetime((signalSources, procSource)) {}
        guard didDisable || CGDisplayIsOnline(id) == 0 else {
            exit(1)
        }
        // The guardian outlives the UI process so an app crash or termination signal still triggers recovery.
        for _ in 0..<3 {
            do {
                try BuiltinDisplayEngine.setEnabled(true, id: id)
                print("RESTORED")
                return
            } catch {
                print("ERROR:\(error.localizedDescription)")
                Thread.sleep(forTimeInterval: 0.5)
            }
        }
        exit(1)
    }
}
