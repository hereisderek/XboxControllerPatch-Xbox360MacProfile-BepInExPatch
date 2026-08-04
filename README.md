# XboxControllerPatch

A BepInEx preloader patcher for Overcooked! 2 (macOS, Unity Mono) that extends controller name detection in `InControl.Xbox360MacProfile`.

This project patches `Assembly-CSharp.dll` in memory during startup using Mono.Cecil, so the original game assembly on disk is not permanently modified.

## Quick Start

Use this one-pass setup to build and install the patcher:

```bash
# 1) Go to repository root
cd /path/to/XboxControllerPatch

# 2) Ensure BepInEx is installed in your Overcooked! 2 game directory.
#    Download from: https://github.com/bepinex/bepinex

# 3) Copy runtime-matched dependencies from game BepInEx/core to this project
cd XboxControllerPatch
mkdir -p lib
cp "<GameDir>/BepInEx/core/BepInEx.dll" lib/
cp "<GameDir>/BepInEx/core/BepInEx.Preloader.dll" lib/
cp "<GameDir>/BepInEx/core/Mono.Cecil.dll" lib/
cp "<GameDir>/BepInEx/core/0Harmony.dll" lib/

# 4) Build
dotnet build -c Release

# 5) Install patcher into BepInEx patchers folder
cp bin/Release/net46/XboxControllerPatch.dll \
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
            └── XboxControllerPatch.dll
```

## Prerequisites

- macOS
- Overcooked! 2 installed
- BepInEx already installed in the game directory
- .NET SDK (8+ works; project target is net46)

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

Copy runtime-matching dependencies from game BepInEx core into project `lib/`:

```bash
cd XboxControllerPatch/XboxControllerPatch
mkdir -p lib

cp "<GameDir>/BepInEx/core/BepInEx.dll" lib/
cp "<GameDir>/BepInEx/core/BepInEx.Preloader.dll" lib/
cp "<GameDir>/BepInEx/core/Mono.Cecil.dll" lib/
cp "<GameDir>/BepInEx/core/0Harmony.dll" lib/
```

Why copy from game core instead of NuGet:

- Prevents version mismatch with the exact BepInEx runtime loading the patcher.

Get BepInEx binaries from:

- https://github.com/bepinex/bepinex

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
XboxControllerPatch/bin/Release/net46/XboxControllerPatch.dll
```

## Install

Copy built patcher into game patchers folder:

```bash
cp XboxControllerPatch/bin/Release/net46/XboxControllerPatch.dll \
  "<GameDir>/BepInEx/patchers/"
```

Installed result:

```text
<GameDir>/BepInEx/patchers/XboxControllerPatch.dll
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

- Re-copy `BepInEx.dll`, `BepInEx.Preloader.dll`, and `Mono.Cecil.dll` into `lib/`.
- Confirm `XboxControllerPatch.csproj` HintPath entries point to `lib/...`.

### Patcher not loaded

- Verify DLL path is exactly `<GameDir>/BepInEx/patchers/XboxControllerPatch.dll`.
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
