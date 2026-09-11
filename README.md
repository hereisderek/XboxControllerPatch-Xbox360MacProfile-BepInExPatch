# XboxControllerPatch

A BepInEx preloader patcher for Overcooked! 2 (macOS, Unity Mono) that extends controller name detection in `InControl.Xbox360MacProfile`.

This project patches `Assembly-CSharp.dll` in memory during startup using Mono.Cecil, so the original game assembly on disk is not permanently modified.

## Quick Start

Use this one-pass setup to build and install the patcher:

```bash
# 1) Go to repository root
cd /path/to/XboxControllerPatch

# 2) Download the latest BepInEx release (no game-directory install needed to compile)
curl -L -o /tmp/bepinex.zip "$(curl -s https://api.github.com/repos/BepInEx/BepInEx/releases/latest \
  | grep -o '"browser_download_url": *"[^"]*macos[^"]*"' | head -1 | cut -d'"' -f4)"
mkdir -p /tmp/bepinex && unzip -o /tmp/bepinex.zip -d /tmp/bepinex

# 3) Copy dependencies from the downloaded BepInEx into this project
cd XboxControllerPatch
mkdir -p lib
cp /tmp/bepinex/BepInEx/core/BepInEx.dll lib/
cp /tmp/bepinex/BepInEx/core/BepInEx.Preloader.dll lib/
cp /tmp/bepinex/BepInEx/core/Mono.Cecil.dll lib/
cp /tmp/bepinex/BepInEx/core/0Harmony.dll lib/

# 4) Build
dotnet build -c Release

# 5) Install patcher into BepInEx patchers folder
cp bin/Release/net46/MacOSXboxControllerPatchNoAuth.dll \
  "<GameDir>/BepInEx/patchers/"

# 6) Run game via BepInEx launcher
cd "<GameDir>"
./run_bepinex.sh Overcooked2.app
```

Expected startup logs:

```text
[XboxPatch] Found Xbox360MacProfile
[XboxPatch] Found JoystickNames array
[XboxPatch] Added controller names
[XboxPatch] Patch completed
```

## What This Project Does

Overcooked! 2 includes a hardcoded `JoystickNames` list in `InControl.Xbox360MacProfile`.

This patcher updates that list from 6 entries to 11 entries and appends:

- Microsoft GamePad-1
- Microsoft GamePad-2
- Microsoft GamePad-3
- Microsoft GamePad-4
- Xbox Wireless Controller

The patch is applied by BepInEx preloader at launch time.

## How It Works

- Patcher entrypoint: `XboxControllerPatch/Patcher.cs`
- Patch logic: `XboxControllerPatch/Xbox360Patch.cs`
- Target assembly: `Assembly-CSharp.dll`
- Target type: `InControl.Xbox360MacProfile`
- Strategy:
  1. Find the instance constructor.
  2. Find unique marker string `Microsoft Wireless 360 Controller`.
  3. Find preceding `newarr System.String`.
  4. Change array size from 6 to 11.
  5. Insert IL to append indices 6..10 before `stfld JoystickNames`.

## Repository Layout

```text
XboxControllerPatch/
├── README.md
├── ai-instructions.md
├── Overcooked2.app                    # symlink in this workspace
└── XboxControllerPatch/
    ├── XboxControllerPatch.csproj
    ├── Patcher.cs
    ├── Xbox360Patch.cs
    ├── lib/
    │   ├── BepInEx.dll
    │   ├── BepInEx.Preloader.dll
    │   ├── Mono.Cecil.dll
    │   └── 0Harmony.dll
    └── bin/
      └── Release/net46/
            └── MacOSXboxControllerPatchNoAuth.dll
```

## Prerequisites

- macOS
- Overcooked! 2 installed
- .NET SDK (8+ works; project target is net46)

Building does not require BepInEx to be installed in the game directory — the latest
BepInEx release is downloaded directly for build-time dependencies (see Dependency Setup).
BepInEx is only needed in the game directory at runtime, to load the built patcher.

Check .NET:

```bash
dotnet --version
```

## Game/BepInEx Expected Layout

```text
<GameDir>/
├── Overcooked2.app
├── run_bepinex.sh
└── BepInEx/
    ├── core/
    ├── plugins/
    └── patchers/
```

## Dependency Setup

Download the latest BepInEx release and copy its core dependencies into project `lib/`.
This does not require BepInEx to be installed in any game directory:

```bash
curl -L -o /tmp/bepinex.zip "$(curl -s https://api.github.com/repos/BepInEx/BepInEx/releases/latest \
  | grep -o '"browser_download_url": *"[^"]*macos[^"]*"' | head -1 | cut -d'"' -f4)"
mkdir -p /tmp/bepinex && unzip -o /tmp/bepinex.zip -d /tmp/bepinex

cd XboxControllerPatch/XboxControllerPatch
mkdir -p lib

cp /tmp/bepinex/BepInEx/core/BepInEx.dll lib/
cp /tmp/bepinex/BepInEx/core/BepInEx.Preloader.dll lib/
cp /tmp/bepinex/BepInEx/core/Mono.Cecil.dll lib/
cp /tmp/bepinex/BepInEx/core/0Harmony.dll lib/
```

Why download the latest release instead of using NuGet or a game-installed copy:

- NuGet packages for BepInEx are not kept in lockstep with upstream releases.
- Building against the latest release keeps `lib/` independent of any particular
  game install, while still matching the BepInEx runtime that will load the patcher
  at install time.

Get BepInEx binaries from:

- https://github.com/BepInEx/BepInEx/releases/latest

## Binary Files and Git

This repository should track source code, not runtime dependency binaries or build outputs.

- Do not commit files copied into `XboxControllerPatch/lib/*.dll`.
- Do not commit `XboxControllerPatch/bin/` or `XboxControllerPatch/obj/` outputs.
- Keep dependency setup reproducible via the documented copy steps above.

## Build

From the project directory:

```bash
cd XboxControllerPatch/XboxControllerPatch
dotnet build -c Release
```

Expected output:

```text
XboxControllerPatch/bin/Release/net46/MacOSXboxControllerPatchNoAuth.dll
```

This plain build is fully standalone - it patches on its own with no external dependency or check
of any kind, exactly as described above.

### About `LicenseTicket.cs`

This repo also contains `LicenseTicket.cs`, an optional check that a separate downstream project
(a distributed launcher app, not part of this repo) uses to gate its own precompiled build of this
DLL behind a short-lived, machine-bound ticket only that launcher can issue. It's wired up in
`Patcher.cs` behind `#if REQUIRE_LICENSE_TICKET`, which is **off unless you explicitly build with**
`-p:RequireLicenseTicket=true` (see the `Condition` in `XboxControllerPatch.csproj`). The plain build
above never sets it, so the check compiles out entirely and has no effect - nothing to remove, no
behavior to work around.

The two variants also get distinct output filenames (via the same `Condition` in
`XboxControllerPatch.csproj`), so it's obvious at a glance which one ended up in a
`BepInEx/patchers/` folder:

* Plain `dotnet build -c Release` (patch/README's own documented steps, no special flags) → `MacOSXboxControllerPatchNoAuth.dll` - patch applies fully standalone, zero ticket check, zero [License] log lines, no OC2XBOXPATCH_TICKET needed at all.
* `dotnet build -c Release -p:RequireLicenseTicket=true` (only ever passed by this repo's build.sh --build-patch) → `MacOSXboxControllerPatchAuth.dll` - same source, but now correctly refuses to patch without a valid ticket.

### Tests

`XboxControllerPatch.Tests/` covers `LicenseTicket.IsAuthorized` and `Fingerprint.CurrentMachineHash`
directly (valid/stale/forged/tampered/malformed tickets, and hash-length handling), without needing
BepInEx, Mono.Cecil, or the launcher app. Run:

```bash
cd XboxControllerPatch.Tests
dotnet test
```

## Install

Copy built patcher into game patchers folder:

```bash
cp XboxControllerPatch/bin/Release/net46/MacOSXboxControllerPatchNoAuth.dll \
  "<GameDir>/BepInEx/patchers/"
```

Installed result:

```text
<GameDir>/BepInEx/patchers/MacOSXboxControllerPatchNoAuth.dll
```

## Enable Logging

Edit:

```text
<GameDir>/BepInEx/config/BepInEx.cfg
```

Ensure:

```ini
[Logging.Console]
Enabled=true
```

## Run and Validate

Launch with BepInEx:

```bash
cd <GameDir>
./run_bepinex.sh Overcooked2.app
```

Check logs for lines like:

```text
[XboxPatch] Found Xbox360MacProfile
[XboxPatch] Found JoystickNames array
[XboxPatch] Added controller names
[XboxPatch] Patch completed
```

## Native controller input redirect

`XboxControllerPatch/NativeControllerPatch.cs` (wired into `Patcher.cs` alongside `Xbox360Patch` and
`ControllerIconPatch`) patches `InControl.UnityButtonSource.GetState`/`InControl.UnityAnalogSource.GetValue`
to redirect through a native helper, `libOC2NativeXboxInput.dylib` (built from
`native/xbox_gamecontroller.swift` via `native/build.sh`), whenever the active device is
Xbox-recognized and the helper reports a controller connected - falling through to the original
`Input.GetKey`/`GetAxisRaw` behavior otherwise. This exists because Unity's own native joystick
polling on macOS mis-reads modern Xbox controllers' HID reports at the engine-binary level (not
fixable by patching this DLL alone - see the parent repo's `patch-v2/TODO.md` for the Ghidra
root-cause writeup); the dylib reads the controller via Apple's GameController framework instead.

The dylib's absolute path is resolved fresh every `Patch()` call from this patcher DLL's own
on-disk location (`Assembly.GetExecutingAssembly().Location`, walking up from
`.../BepInEx/patchers/<this>.dll` to `.../BepInEx/`, then into `native/libOC2NativeXboxInput.dylib`)
and baked into the in-memory Cecil patch as a full-path `ModuleReference` - never a bare
`[DllImport("...")]` name, which this old embedded Mono's P/Invoke resolver doesn't search
`Contents/Plugins/` or any other default location for. A missing dylib at that path is logged
(`[XboxPatch] [Native] ...`) and skipped, not fatal - the rest of the patch still applies.

**Known caveat:** "Xbox-recognized" (`inputDevice.Name.Contains("XBox")`) can't tell apart a
device recognized via Steam Input's `Microsoft GamePad-N` rename (`Xbox360Patch.cs`) from one
recognized via its real Bluetooth name - InControl overwrites both to the same profile name, so
this redirect is attempted either way, not only when Steam Input is off. A real regression from
this was found and fixed: `native/xbox_gamecontroller.swift`'s `GCController.controllers()` lookup
wasn't filtering out a known inert macOS GameController-framework proxy device, which could make the
redirect wrongly claim "connected" and silently override the Steam Input path it should have left
alone, or misassign input across two real controllers. See `patch-v2/TODO.md`'s "unfiltered
GCController.controllers()" entry and `patch-v2/native/README.md` for the full writeup and the fix
(`isGenuineXboxController`, filtering by `vendorName`).

## Safety and Idempotence

- Original `Overcooked2.app/.../Assembly-CSharp.dll` remains unchanged on disk.
- Patch is applied in preloader memory patching flow.
- Patcher includes a guard to skip reinsertion if the added controller string is already present.

## Troubleshooting

### Type not found

- Confirm target type in IL is exactly `InControl.Xbox360MacProfile`.
- Use ILSpy/ilspycmd to inspect `Assembly-CSharp.dll`.

### Marker string not found

- Verify constructor still contains `Microsoft Wireless 360 Controller`.
- If game updates changed strings/IL, update marker strategy in patch code.

### Build fails due to missing references

- Re-download the latest BepInEx release and re-copy `BepInEx.dll`, `BepInEx.Preloader.dll`,
  `Mono.Cecil.dll`, and `0Harmony.dll` into `lib/` (see Dependency Setup).
- Confirm `XboxControllerPatch.csproj` HintPath entries point to `lib/...`.

### Patcher not loaded

- Verify DLL path is exactly `<GameDir>/BepInEx/patchers/MacOSXboxControllerPatchNoAuth.dll` (or
  `MacOSXboxControllerPatchAuth.dll` for a `-p:RequireLicenseTicket=true` build).
- Confirm BepInEx preloader is active and no startup errors in BepInEx logs.

## Development Notes

- Target framework is `net46` to avoid `netstandard` runtime load issues in BepInEx 5 preloader contexts.
- Keep patch matching robust by using a unique marker string plus structural IL checks.
- Avoid broad pattern matching on only `ldc.i4.6`, as many arrays can share that size.

## Inspiration

The approach is inspired by this reference patch script:

- https://github.com/fr-eed/macos-incontrol-controller-patch/blob/main/patch.py

## License

This repository does not currently declare a license file.
Add one if you plan to distribute source or binaries publicly.
