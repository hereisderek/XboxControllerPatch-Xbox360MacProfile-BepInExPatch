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
// Filters out anything whose vendorName doesn't look like a real Xbox
// controller before indexing - GCController.controllers() isn't just the
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
// A real Xbox controller reports a vendorName containing "Xbox" via
// GameController framework (confirmed for both physical models tested in
// this project - see ../TODO.md); neither the inert proxy nor Steam's
// virtual device is expected to.
private func isGenuineXboxController(_ controller: GCController) -> Bool {
    guard let vendorName = controller.vendorName else { return false }
    return vendorName.lowercased().contains("xbox")
}

private func gamepadForJoystick(_ joystickId: Int32) -> GCExtendedGamepad? {
    let gamepads = GCController.controllers()
        .filter(isGenuineXboxController)
        .compactMap { $0.extendedGamepad }
    let index = Int(joystickId) - 1
    guard index >= 0 && index < gamepads.count else { return nil }
    return gamepads[index]
}

@_cdecl("OC2Xbox_IsConnectedForJoystick")
public func OC2Xbox_IsConnectedForJoystick(_ joystickId: Int32) -> Int32 {
    return gamepadForJoystick(joystickId) != nil ? 1 : 0
}

// legacyIndex matches InControl's UnityInputDeviceProfile.ButtonN numbering
// as used directly by Xbox360MacProfile's ButtonMappings table. Some
// properties (buttonOptions, thumbstick click buttons) are optional on
// GCExtendedGamepad - not every controller model exposes them - and read as
// "not pressed" rather than crashing when absent.
@_cdecl("OC2Xbox_GetButtonByLegacyIndexForJoystick")
public func OC2Xbox_GetButtonByLegacyIndexForJoystick(_ joystickId: Int32, _ legacyIndex: Int32) -> Float {
    guard let gamepad = gamepadForJoystick(joystickId) else { return 0 }
    let pressed: Bool
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
public func OC2Xbox_GetAxisByLegacyIndexForJoystick(_ joystickId: Int32, _ legacyIndex: Int32) -> Float {
    guard let gamepad = gamepadForJoystick(joystickId) else { return 0 }
    switch legacyIndex {
    case 0: return gamepad.leftThumbstick.xAxis.value
    case 1: return -gamepad.leftThumbstick.yAxis.value
    case 2: return gamepad.rightThumbstick.xAxis.value
    case 3: return -gamepad.rightThumbstick.yAxis.value
    case 4: return gamepad.leftTrigger.value
    case 5: return gamepad.rightTrigger.value
    default: return 0
    }
}
