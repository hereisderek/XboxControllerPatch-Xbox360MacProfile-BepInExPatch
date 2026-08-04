# BepInEx Preloader Patcher Project Specification
## Xbox360MacProfile Controller Name Patch

the original game app can be found following the symlink in the `Overcooked2.app` bundle:

```
Overcooked2.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll
```

## Goal

Create a BepInEx preloader patcher that modifies `Assembly-CSharp.dll` during game startup without modifying the original DLL on disk.

The target class is:

```
InControl.Xbox360MacProfile
```

The goal is to extend the existing `JoystickNames` array.

Original:

```csharp
JoystickNames = new string[6]
{
    "",
    "Microsoft Wireless 360 Controller",
    "Mad Catz, Inc. Mad Catz FPS Pro GamePad",
    "MadCatz Call of Duty GamePad",
    "©Microsoft Corporation Controller",
    "©Microsoft Corporation Xbox Original Wired Controller"
};
```

Desired result:

```csharp
JoystickNames = new string[11]
{
    "",
    "Microsoft Wireless 360 Controller",
    "Mad Catz, Inc. Mad Catz FPS Pro GamePad",
    "MadCatz Call of Duty GamePad",
    "©Microsoft Corporation Controller",
    "©Microsoft Corporation Xbox Original Wired Controller",
    "Microsoft GamePad-1",
    "Microsoft GamePad-2",
    "Microsoft GamePad-3",
    "Microsoft GamePad-4",
    "Xbox Wireless Controller"
};
```

The original `Assembly-CSharp.dll` must remain unchanged.

The patch must happen through a BepInEx preloader patcher using Mono.Cecil.

---

# Environment

The target game already has:

```
<GameDir>
├── Overcooked2.app
├── run_bepinex.sh
└── BepInEx
    ├── core
    ├── plugins
    └── patchers
```

Assumptions:

- Unity Mono game
- Doorstop installed
- BepInEx preloader available
- Mono.Cecil available

---

# Final Output

The final installed patcher should be:

```
<GameDir>
└── BepInEx
    └── patchers
        └── XboxControllerPatch.dll
```

---

# Patch Requirements

The patcher must:

1. Target:

```
Assembly-CSharp.dll
```

2. Find:

```
InControl.Xbox360MacProfile
```

3. Find the constructor:

```
Xbox360MacProfile()
```

4. Locate the `JoystickNames` array initialization.

5. Change:

```
new string[6]
```

to:

```
new string[11]
```

6. Append the following entries:

Index 6:

```
Microsoft GamePad-1
```

Index 7:

```
Microsoft GamePad-2
```

Index 8:

```
Microsoft GamePad-3
```

Index 9:

```
Microsoft GamePad-4
```

Index 10:

```
Xbox Wireless Controller
```

---

# Development Setup

## Install .NET SDK

Install:

```
.NET SDK 8
```

Verify:

```bash
dotnet --version
```

---

# Create Project

Run:

```bash
mkdir XboxControllerPatch
cd XboxControllerPatch

dotnet new classlib \
    -n XboxControllerPatch \
    -f netstandard2.0

cd XboxControllerPatch
```

Delete:

```
Class1.cs
```

---

# Add Dependencies

Copy these files from:

```
<GameDir>/BepInEx/core/
```

Required:

```
BepInEx.dll
BepInEx.Preloader.dll
Mono.Cecil.dll
0Harmony.dll
```

Create:

```
XboxControllerPatch
└── lib
    ├── BepInEx.dll
    ├── BepInEx.Preloader.dll
    ├── Mono.Cecil.dll
    └── 0Harmony.dll
```

---

# Configure csproj

Replace:

```
XboxControllerPatch.csproj
```

with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

<PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>XboxControllerPatch</AssemblyName>
    <Nullable>disable</Nullable>
</PropertyGroup>

<ItemGroup>

<Reference Include="BepInEx.Preloader">
    <HintPath>lib/BepInEx.Preloader.dll</HintPath>
</Reference>

<Reference Include="BepInEx">
    <HintPath>lib/BepInEx.dll</HintPath>
</Reference>

<Reference Include="Mono.Cecil">
    <HintPath>lib/Mono.Cecil.dll</HintPath>
</Reference>

</ItemGroup>

</Project>
```

---

# Create Patcher Entry Point

Create:

```
Patcher.cs
```

Content:

```csharp
using System.Collections.Generic;
using Mono.Cecil;

public static class Patcher
{
    public static IEnumerable<string> TargetDLLs
    {
        get
        {
            yield return "Assembly-CSharp.dll";
        }
    }

    public static void Patch(
        AssemblyDefinition assembly
    )
    {
        Xbox360Patch.Apply(assembly);
    }
}
```

---

# Create Patch Logic

Create:

```
Xbox360Patch.cs
```

The patch logic must:

1. Find the type:

```
InControl.Xbox360MacProfile
```

2. Find the instance constructor.

3. Search for the unique string:

```
Microsoft Wireless 360 Controller
```

This is the marker that identifies the correct `JoystickNames` array.

4. Find the preceding:

```
newarr System.String
```

instruction.

5. Change the array size:

```
6 -> 11
```

6. Find:

```
stfld JoystickNames
```

7. Insert before it the following IL:

```text
dup
ldc.i4 6
ldstr "Microsoft GamePad-1"
stelem.ref

dup
ldc.i4 7
ldstr "Microsoft GamePad-2"
stelem.ref

dup
ldc.i4 8
ldstr "Microsoft GamePad-3"
stelem.ref

dup
ldc.i4 9
ldstr "Microsoft GamePad-4"
stelem.ref

dup
ldc.i4 10
ldstr "Xbox Wireless Controller"
stelem.ref
```

---

# Build

Run:

```bash
dotnet build -c Release
```

Expected output:

```
bin/Release/netstandard2.0/XboxControllerPatch.dll
```

---

# Install

Copy:

```
XboxControllerPatch.dll
```

to:

```
<GameDir>/BepInEx/patchers/
```

Result:

```
<GameDir>
└── BepInEx
    └── patchers
        └── XboxControllerPatch.dll
```

---

# Enable Debug Logging

Edit:

```
<GameDir>/BepInEx/config/BepInEx.cfg
```

Enable:

```ini
[Logging.Console]
Enabled=true
```

---

# Test

Launch:

```bash
./run_bepinex.sh Overcooked2.app
```

Expected log:

```
[XboxPatch] Found Xbox360MacProfile
[XboxPatch] Found JoystickNames array
[XboxPatch] Added controller names
[XboxPatch] Patch completed
```

---

# Troubleshooting

## Type not found

Check the namespace in ILSpy.

The full type name must match:

```
InControl.Xbox360MacProfile
```

---

## JoystickNames not found

Do not match only:

```
ldc.i4.6
```

because many arrays may have the same size.

Use the unique string:

```
Microsoft Wireless 360 Controller
```

as the search marker.

---

## Dump IL

Install:

```bash
dotnet tool install -g ilspycmd
```

Dump:

```bash
ilspycmd \
-t InControl.Xbox360MacProfile \
Assembly-CSharp.dll
```

Inspect the constructor IL.

---

# Final Result

The game files remain:

```
Overcooked2.app              unchanged

BepInEx
└── patchers
    └── XboxControllerPatch.dll
```

The game automatically accepts:

```
Microsoft GamePad-1
Microsoft GamePad-2
Microsoft GamePad-3
Microsoft GamePad-4
Xbox Wireless Controller
```

without modifying the original assembly.