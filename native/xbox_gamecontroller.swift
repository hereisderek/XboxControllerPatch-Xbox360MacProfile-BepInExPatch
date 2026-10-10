// Native helper for genuine (non-Steam-Input) Xbox controller support on
// macOS. Ported from this project's patch-v2/native/ research track (see
// patch-v2/TODO.md there for the full history: Unity's own native joystick
// backend mis-handles modern Xbox controllers' HID reports - confirmed via
// Ghidra decompilation of the game's engine binary - and an earlier raw-HID
// version of this file worked for one controller model but produced wrong
// button/axis assignments on another). Apple's GameController framework
// normalizes any compatible controller's model-specific report format into
// one consistent set of named inputs, so this reads through it instead of
// parsing HID reports by hand.
//
// Exposes three C-callable functions, P/Invoked from Assembly-CSharp.dll by
// NativeControllerPatch.cs (in ../XboxControllerPatch/) via a full absolute
// path baked into the in-memory Cecil patch at launch time - see that
// file's own header comment for why a bare DllImport logical name doesn't
// resolve on this old embedded Mono: OC2Xbox_IsConnectedForJoystick,
// OC2Xbox_GetButtonByLegacyIndexForJoystick,
// OC2Xbox_GetAxisByLegacyIndexForJoystick. "Legacy index" is InControl's own
// UnityInputDeviceProfile.ButtonN/AnalogN numbering, matching
// Xbox360MacProfile's own ButtonMappings/AnalogMappings tables directly.
//
// Build (see build.sh in this directory):
//   swiftc -emit-library -o libOC2NativeXboxInput.dylib xbox_gamecontroller.swift \
//     -framework GameController -target x86_64-apple-macos13.0
// (x86_64 because the game binary itself is x86_64-only, even on Apple
// Silicon under Rosetta - a dylib loaded into that process must match.)

import GameController
import Foundation
import IOKit.hid

// Selects by Unity's own joystick slot number (1-based, i.e.
// InControl.UnityInputDevice.JoystickId - see NativeControllerPatch), not
// just "the first connected controller": with two controllers connected,
// InControl assigns each player a distinct InputDevice/JoystickId, and every
// call into this bridge needs to land on the *matching* physical controller,
// not silently collapse onto one for every player. Does not fall back to
// "first" on an out-of-range index - an invalid/unmatched index means no
// native override for that device, which is the correct behavior (let the
// caller fall through to Unity's own path instead of misrouting).
//
// Filters out anything that isn't a real, claimable gamepad (see
// isClaimableController below) before indexing - GCController.controllers() isn't just the
// physically-connected gamepads. Two known non-gamepad entries can show up
// alongside them:
//   1. The "inert macOS GameController-framework proxy device" (vid=0x045e
//      pid=0x028e, reported by IOHIDManager as "GamePad-1") that
//      ../TODO.md's raw-HID predecessor (xbox_hid.c) already had to filter
//      out the same way for the same reason - it satisfies GCController's
//      gamepad criteria but never reports real input. Left unfiltered here
//      (this GameController-framework rewrite dropped that filter), it can
//      occupy an array slot and shift every index after it, or simply be the
//      one gamepadForJoystick(1) returns.
//   2. Steam Input's own virtual controller, when Steam Input is the ACTIVE
//      remapping mechanism for this device (Xbox360Patch.cs's
//      "Microsoft GamePad-N" rename) - InControl's InputDevice.Name becomes
//      "XBox 360 Controller" for that case too (same profile, same string),
//      so NativeControllerPatch.cs's Name-based check can't tell "matched via
//      Steam Input" apart from "matched via a real Bluetooth device" and
//      tries this native path either way. If GCController.controllers() also
//      exposes some view of that virtual device (or the inert proxy above)
//      at the relevant index, this used to silently return "connected" with
//      dead/stale data - overriding what otherwise would have correctly
//      fallen through to Unity's own Input.GetKey/GetAxisRaw path, which is
//      what Steam Input actually needs (it already works through that path,
//      same as before any of this native-redirect work existed).
// Both of those report a vendorName containing "GamePad" ("GamePad-1" - the
// same marker NativeControllerPatch.cs already uses to recognize Steam
// Input), so that is what gets excluded; this used to be a positive "vendorName
// contains xbox" match, which also dropped every non-Xbox controller. Any
// other controller GameController exposes an extendedGamepad for is now
// claimed (confirmed: a Switch Pro Controller reports "Pro Controller" with
// a full extendedGamepad, and Apple maps its face buttons by position -
// bottom = buttonA, right = buttonB - the same layout as Xbox). Whether the
// game actually routes a given device here is still decided on the C# side
// by its raw Unity name (Xbox360Patch.cs), not by this filter.
private let virtualPadMarker = "gamepad"

private func isClaimableController(_ controller: GCController) -> Bool {
    guard controller.extendedGamepad != nil, let vendorName = controller.vendorName else { return false }
    return !vendorName.lowercased().contains(virtualPadMarker)
}

// joystickId is a position in Unity's full Input.GetJoystickNames() list
// (InControl: joystickId = index + 1), which also holds entries this bridge
// never claims: Steam's virtual "GamePad-N" pad, which shows up whenever Steam
// is running, and empty names for unplugged slots. The C# side passes the
// whole list (joined with "\n"). Without a list (nil) this falls back to the
// plain position, as it always did.
//
// Each claimable Unity name is paired with the controller it belongs to BY
// NAME, not by rank. Taking the n-th claimable name to be the n-th controller
// assumes both lists hold the same controllers, and they need not: with Steam
// Input on, Steam can show Unity only a virtual "GamePad-N" for one controller
// while GameController still lists it. Unity's list is then [GamePad-1, B],
// GameController's [A, B], and rank pairing gives B's slot to A: A drives the
// game, B does nothing, and unplugging A makes B work. Unity's name is
// "<manufacturer> <product>" and GameController's vendorName is the product
// ("Xbox Wireless Controller", "Wireless Controller", "Pro Controller"), so a
// controller owns the Unity names that equal it or end with " <its name>";
// when several fit ("Xbox Wireless Controller" also ends with "Wireless
// Controller") the longest wins, and same-named controllers pair in order. If
// no Unity name matches any controller at all (a naming this doesn't expect)
// it pairs by rank, as before.
func isClaimableName(_ name: String) -> Bool {
    let trimmed = name.trimmingCharacters(in: .whitespaces)
    return !trimmed.isEmpty && !trimmed.lowercased().contains(virtualPadMarker)
}

private func nameKey(_ s: String) -> String { s.trimmingCharacters(in: .whitespaces).lowercased() }

// Which controller (by name) a Unity joystick name belongs to, if any.
private func owner(of unityName: String, in controllerNames: [String]) -> String? {
    var best: String?
    for name in controllerNames where !name.isEmpty && (unityName == name || unityName.hasSuffix(" " + name)) {
        if best == nil || name.count > best!.count { best = name }
    }
    return best
}

// For each Unity slot (0-based), the index of the controller it is paired with, or nil: not claimable, or no
// controller for it (the game's own input path then handles that device).
func pairSlots(unityNames: [String], controllerNames: [String]) -> [Int?] {
    let controllers = controllerNames.map(nameKey)
    var taken: [String: Int] = [:]
    var slots: [Int?] = []
    var anyMatched = false
    for raw in unityNames {
        guard isClaimableName(raw), let owned = owner(of: nameKey(raw), in: controllers) else { slots.append(nil); continue }
        let sameName = controllers.indices.filter { controllers[$0] == owned }
        let nth = taken[owned, default: 0]
        taken[owned] = nth + 1
        if nth < sameName.count { slots.append(sameName[nth]); anyMatched = true } else { slots.append(nil) }
    }
    if anyMatched { return slots }
    var rank = 0
    return unityNames.map { raw in
        guard isClaimableName(raw) else { return nil }
        defer { rank += 1 }
        return rank < controllers.count ? rank : nil
    }
}

// Which pads GameController hands to the patch and which are read directly over HID. GameController keys a
// controller by its serial number (`LOGICAL_DEVICE(<serial>)`), and cheap clones all report the same one, so of two
// identical pads it lists one and silently drops the other (their HID devices are all there, each with its own
// presses). Which twin it kept can't be told, so for every pad name where HID shows more of Apple's Xbox-driver pads
// than GameController lists, ALL the HID pads of that name are read directly and GameController's pad of that name is
// left out. Every other name keeps its GameController pads, as before. Returns the indices to use in each list.
func splitPads(gcNames: [String], hidNames: [String]) -> (gc: [Int], hid: [Int]) {
    func key(_ s: String) -> String { s.trimmingCharacters(in: .whitespaces).lowercased() }
    let gcKeys = gcNames.map(key)
    let hidKeys = hidNames.map(key)
    var direct = Set<String>()
    for k in Set(hidKeys) where hidKeys.filter({ $0 == k }).count > gcKeys.filter({ $0 == k }).count { direct.insert(k) }
    return (gcKeys.indices.filter { !direct.contains(gcKeys[$0]) }, hidKeys.indices.filter { direct.contains(hidKeys[$0]) })
}

// Pad names with more pads plugged in (as HID lists them) than GameController lists, that are not read directly: the
// extra pads can't be used. `hidNames` is every real gamepad HID shows, `directNames` the ones read directly.
func unreadablePads(gcNames: [String], hidNames: [String], directNames: [String]) -> [(name: String, plugged: Int, listed: Int)] {
    func key(_ s: String) -> String { s.trimmingCharacters(in: .whitespaces).lowercased() }
    let gcKeys = gcNames.map(key)
    let hidKeys = hidNames.map(key)
    let direct = Set(directNames.map(key))
    var result: [(name: String, plugged: Int, listed: Int)] = []
    var seen = Set<String>()
    for (i, k) in hidKeys.enumerated() where seen.insert(k).inserted && !direct.contains(k) {
        let plugged = hidKeys.filter { $0 == k }.count
        let listed = gcKeys.filter { $0 == k }.count
        if plugged > listed { result.append((hidNames[i].trimmingCharacters(in: .whitespaces), plugged, listed)) }
    }
    return result
}

// MARK: - Pads read directly over HID

// A pad with the layout Apple's XboxGamepad driver gives its pads (USB 045e:028e: what clones such as the Flydigi spoof;
// any other HID pad with the same elements is read the same way) read straight from its HID device. The layout is: page 1 axes X/Y (left stick), Rx/Ry (right stick), Z/Rz
// (triggers) and page 9 buttons 1...15 in XInput order. Input arrives on a private queue, so nothing depends on the
// game's run loop.
private final class HIDPad {
    let name: String
    let location: Int
    private let device: IOHIDDevice
    private let lock = NSLock()
    private var latest: [Int: Int] = [:]
    private var range: [Int: (min: Int, max: Int)] = [:]
    private static let queue = DispatchQueue(label: "oc2.xbox.hid")

    static func key(_ page: Int, _ usage: Int) -> Int { page << 16 | usage }

    init?(device: IOHIDDevice) {
        guard let elements = IOHIDDeviceCopyMatchingElements(device, nil, IOOptionBits(kIOHIDOptionsTypeNone)) as? [IOHIDElement] else { return nil }
        self.device = device
        name = (IOHIDDeviceGetProperty(device, kIOHIDProductKey as CFString) as? String) ?? ""
        location = (IOHIDDeviceGetProperty(device, kIOHIDLocationIDKey as CFString) as? Int) ?? 0
        for element in elements {
            let type = IOHIDElementGetType(element)
            guard type == kIOHIDElementTypeInput_Button || type == kIOHIDElementTypeInput_Misc || type == kIOHIDElementTypeInput_Axis else { continue }
            range[HIDPad.key(Int(IOHIDElementGetUsagePage(element)), Int(IOHIDElementGetUsage(element)))] = (IOHIDElementGetLogicalMin(element), IOHIDElementGetLogicalMax(element))
        }
        // Only a pad with exactly the layout described above (whoever made it: recognised by its elements, not its
        // vendor or product ID); anything else is left to the game.
        for usage in 1...15 where range[HIDPad.key(9, usage)] == nil { return nil }
        for usage in [0x30, 0x31, 0x33, 0x34, 0x32, 0x35] where range[HIDPad.key(1, usage)] == nil { return nil }
        IOHIDDeviceRegisterInputValueCallback(device, { context, _, _, value in
            guard let context = context else { return }
            let element = IOHIDValueGetElement(value)
            Unmanaged<HIDPad>.fromOpaque(context).takeUnretainedValue()
                .store(HIDPad.key(Int(IOHIDElementGetUsagePage(element)), Int(IOHIDElementGetUsage(element))), IOHIDValueGetIntegerValue(value))
        }, Unmanaged.passUnretained(self).toOpaque())
        IOHIDDeviceSetDispatchQueue(device, HIDPad.queue)
        IOHIDDeviceSetCancelHandler(device) {}
        IOHIDDeviceActivate(device)
    }

    private func store(_ key: Int, _ value: Int) { lock.lock(); latest[key] = value; lock.unlock() }
    private func raw(_ page: Int, _ usage: Int) -> Int? { lock.lock(); defer { lock.unlock() }; return latest[HIDPad.key(page, usage)] }

    func button(_ usage: Int) -> Bool { (raw(9, usage) ?? 0) > 0 }

    // -1...1 for a stick axis, 0 at rest (no value seen yet), and 0...1 for a trigger.
    func stick(_ usage: Int) -> Float {
        guard let v = raw(1, usage), let r = range[HIDPad.key(1, usage)], r.max > r.min else { return 0 }
        return max(-1, min(1, (Float(v - r.min) / Float(r.max - r.min)) * 2 - 1))
    }
    func trigger(_ usage: Int) -> Float {
        guard let v = raw(1, usage), let r = range[HIDPad.key(1, usage)], r.max > r.min else { return 0 }
        return max(0, min(1, Float(v - r.min) / Float(r.max - r.min)))
    }
}

private let hidManager: IOHIDManager = {
    let manager = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
    // Every gamepad and joystick, not just one vendor's: which of them are read directly is decided by their layout.
    IOHIDManagerSetDeviceMatchingMultiple(manager, [[kIOHIDDeviceUsagePageKey: 1, kIOHIDDeviceUsageKey: 5],
                                                    [kIOHIDDeviceUsagePageKey: 1, kIOHIDDeviceUsageKey: 4]] as CFArray)
    IOHIDManagerOpen(manager, IOOptionBits(kIOHIDOptionsTypeNone))
    return manager
}()
private var hidPads: [ObjectIdentifier: HIDPad] = [:]
private var hidRejected = Set<ObjectIdentifier>()
private var hidList: [HIDPad] = []
private var hidAllNames: [String] = []   // every real gamepad HID shows, readable here or not (for the "can't be used" log)
private var hidListAt: TimeInterval = 0

// The connected pads with the layout above, by location. Looked at twice a second at most (the game asks many times a
// frame). hidAllNames gets the product name of every gamepad HID lists (not Steam's or macOS's virtual "GamePad-N").
private func currentHIDPads() -> [HIDPad] {
    let now = ProcessInfo.processInfo.systemUptime
    if now - hidListAt < 0.5 { return hidList }
    hidListAt = now
    var list: [HIDPad] = []
    var allNames: [String] = []
    for device in (IOHIDManagerCopyDevices(hidManager) as? Set<IOHIDDevice>) ?? [] {
        let product = (IOHIDDeviceGetProperty(device, kIOHIDProductKey as CFString) as? String) ?? ""
        if isClaimableName(product) { allNames.append(product) }
        let id = ObjectIdentifier(device)
        if let pad = hidPads[id] { list.append(pad); continue }
        if hidRejected.contains(id) { continue }
        if let pad = HIDPad(device: device) { hidPads[id] = pad; list.append(pad) } else { hidRejected.insert(id) }
    }
    hidList = list.sorted { $0.location < $1.location }
    hidAllNames = allNames.sorted()
    return hidList
}

// MARK: - Pairing Unity's joystick slots with pads

private enum Pad {
    case gameController(GCExtendedGamepad)
    case hid(HIDPad)
}

// Worked out again only when Unity's list or the set of connected pads changes, not on every button and axis read
// (those are many per frame). The pairing is also written to stderr then, which the launcher's saved launch log
// captures: it shows what Unity and GameController each listed, which pads are read over HID, and how they were paired.
private struct Pairing {
    var names: [CChar]
    var controllers: [ObjectIdentifier]
    var hid: [ObjectIdentifier]
    var allNames: [String]
    var pads: [Pad]
    var slots: [Int?]
}
private var pairing: Pairing?
private let pairingLock = NSLock()

private func currentPairing(_ joystickNames: UnsafePointer<CChar>) -> Pairing {
    pairingLock.lock()
    defer { pairingLock.unlock() }
    let controllers = GCController.controllers()
    let ids = controllers.map { ObjectIdentifier($0) }
    let hid = currentHIDPads()
    let hidIds = hid.map { ObjectIdentifier($0) }
    let allNames = hidAllNames
    if let p = pairing, p.controllers == ids, p.hid == hidIds, p.allNames == allNames, p.names.withUnsafeBufferPointer({ strcmp($0.baseAddress!, joystickNames) }) == 0 { return p }
    let claimable = controllers.filter(isClaimableController)
    let split = splitPads(gcNames: claimable.map { $0.vendorName ?? "" }, hidNames: hid.map { $0.name })
    var pads: [Pad] = []
    var padNames: [String] = []
    for i in split.gc { if let g = claimable[i].extendedGamepad { pads.append(.gameController(g)); padNames.append(claimable[i].vendorName ?? "") } }
    for i in split.hid { pads.append(.hid(hid[i])); padNames.append(hid[i].name) }
    let unity = String(cString: joystickNames).components(separatedBy: "\n")
    let slots = pairSlots(unityNames: unity, controllerNames: padNames)
    let p = Pairing(names: Array(UnsafeBufferPointer(start: joystickNames, count: strlen(joystickNames) + 1)),
                    controllers: ids, hid: hidIds, allNames: allNames, pads: pads, slots: slots)
    pairing = p
    var pairs: [String] = []
    for (i, c) in slots.enumerated() {
        guard let c = c else { continue }
        var viaHID = false
        if case .hid = pads[c] { viaHID = true }
        pairs.append("joystick \(i + 1) -> \(padNames[c])\(viaHID ? " (read over HID)" : "")")
    }
    fputs("[XboxPatch] [Native] Unity joysticks \(unity), GameController \(claimable.map { $0.vendorName ?? "" }), HID \(hid.map { $0.name }): \(pairs.isEmpty ? "none paired" : pairs.joined(separator: ", "))\n", stderr)
    // Not while GameController has listed nothing at all: it fills its list a moment after the game starts.
    for lost in claimable.isEmpty ? [] : unreadablePads(gcNames: claimable.map { $0.vendorName ?? "" }, hidNames: allNames, directNames: split.hid.map { hid[$0].name }) {
        fputs("[XboxPatch] [Native] \(lost.plugged) \"\(lost.name)\" pads are plugged in but GameController lists \(lost.listed): the rest can't be used (this helper doesn't know their HID layout)\n", stderr)
    }
    return p
}

private func padForJoystick(_ joystickId: Int32, _ joystickNames: UnsafePointer<CChar>?) -> Pad? {
    let position = Int(joystickId) - 1
    guard position >= 0 else { return nil }
    guard let joystickNames = joystickNames else {
        let pads = GCController.controllers().filter(isClaimableController).compactMap { $0.extendedGamepad }
        return position < pads.count ? .gameController(pads[position]) : nil
    }
    let p = currentPairing(joystickNames)
    guard position < p.slots.count, let index = p.slots[position], index < p.pads.count else { return nil }
    return p.pads[index]
}

@_cdecl("OC2Xbox_IsConnectedForJoystick")
public func OC2Xbox_IsConnectedForJoystick(_ joystickId: Int32, _ joystickNames: UnsafePointer<CChar>?) -> Int32 {
    return padForJoystick(joystickId, joystickNames) != nil ? 1 : 0
}

// legacyIndex matches InControl's UnityInputDeviceProfile.ButtonN numbering
// as used directly by Xbox360MacProfile's ButtonMappings table. Some
// properties (buttonOptions, thumbstick click buttons) are optional on
// GCExtendedGamepad - not every controller model exposes them - and read as
// "not pressed" rather than crashing when absent.
@_cdecl("OC2Xbox_GetButtonByLegacyIndexForJoystick")
public func OC2Xbox_GetButtonByLegacyIndexForJoystick(_ joystickId: Int32, _ joystickNames: UnsafePointer<CChar>?, _ legacyIndex: Int32) -> Float {
    guard let pad = padForJoystick(joystickId, joystickNames) else { return 0 }
    let pressed: Bool
    switch pad {
    case .gameController(let gamepad):
        switch legacyIndex {
        case 16: pressed = gamepad.buttonA.isPressed
        case 17: pressed = gamepad.buttonB.isPressed
        case 18: pressed = gamepad.buttonX.isPressed
        case 19: pressed = gamepad.buttonY.isPressed
        case 5:  pressed = gamepad.dpad.up.isPressed
        case 6:  pressed = gamepad.dpad.down.isPressed
        case 7:  pressed = gamepad.dpad.left.isPressed
        case 8:  pressed = gamepad.dpad.right.isPressed
        case 13: pressed = gamepad.leftShoulder.isPressed
        case 14: pressed = gamepad.rightShoulder.isPressed
        case 9:  pressed = gamepad.buttonMenu.isPressed        // Start
        case 10: pressed = gamepad.buttonOptions?.isPressed ?? false   // Back/View - optional
        case 11: pressed = gamepad.leftThumbstickButton?.isPressed ?? false  // L3 - optional
        case 12: pressed = gamepad.rightThumbstickButton?.isPressed ?? false // R3 - optional
        default: pressed = false // includes legacy 15 (System/Guide) - GCExtendedGamepad's
                                  // buttonHome is reserved by the OS on most controllers anyway
        }
    case .hid(let hid):
        // Apple's XboxGamepad driver numbers the page 9 buttons in XInput order.
        switch legacyIndex {
        case 16: pressed = hid.button(1)    // A
        case 17: pressed = hid.button(2)    // B
        case 18: pressed = hid.button(3)    // X
        case 19: pressed = hid.button(4)    // Y
        case 13: pressed = hid.button(5)    // LB
        case 14: pressed = hid.button(6)    // RB
        case 11: pressed = hid.button(7)    // L3
        case 12: pressed = hid.button(8)    // R3
        case 9:  pressed = hid.button(9)    // Start
        case 10: pressed = hid.button(10)   // Back
        case 5:  pressed = hid.button(12)   // D-pad up
        case 6:  pressed = hid.button(13)   // down
        case 7:  pressed = hid.button(14)   // left
        case 8:  pressed = hid.button(15)   // right
        default: pressed = false
        }
    }
    return pressed ? 1.0 : 0.0
}

// legacyIndex matches InControl's UnityInputDeviceProfile.AnalogN numbering
// as used directly by Xbox360MacProfile's AnalogMappings table (Analog0/1 =
// left stick X/Y, Analog2/3 = right stick X/Y, Analog4/5 = left/right
// trigger). GCControllerAxisInput.value is documented by Apple as ranging -1
// (bottom) to 1 (top) for Y, which reads as the correct sign in a raw
// standalone probe (values move smoothly through the documented range) but
// tested backwards in the actual game with two different controllers, both
// sticks, uniformly - confirmed live, not assumed, so Y is negated here
// rather than trusting the documented convention against InControl's actual
// expectation. GCControllerButtonInput.value (used for analog triggers) is
// 0...1 and did test correctly, so triggers are left alone.
@_cdecl("OC2Xbox_GetAxisByLegacyIndexForJoystick")
public func OC2Xbox_GetAxisByLegacyIndexForJoystick(_ joystickId: Int32, _ joystickNames: UnsafePointer<CChar>?, _ legacyIndex: Int32) -> Float {
    guard let pad = padForJoystick(joystickId, joystickNames) else { return 0 }
    switch pad {
    case .gameController(let gamepad):
        switch legacyIndex {
        case 0: return gamepad.leftThumbstick.xAxis.value
        case 1: return -gamepad.leftThumbstick.yAxis.value
        case 2: return gamepad.rightThumbstick.xAxis.value
        case 3: return -gamepad.rightThumbstick.yAxis.value
        case 4: return gamepad.leftTrigger.value
        case 5: return gamepad.rightTrigger.value
        default: return 0
        }
    case .hid(let hid):
        // The driver reports a stick pushed up as a positive Y (as XInput does), like GameController's yAxis, so Y is
        // negated here too (see the note above).
        switch legacyIndex {
        case 0: return hid.stick(0x30)
        case 1: return -hid.stick(0x31)
        case 2: return hid.stick(0x33)
        case 3: return -hid.stick(0x34)
        case 4: return hid.trigger(0x32)
        case 5: return hid.trigger(0x35)
        default: return 0
        }
    }
}
