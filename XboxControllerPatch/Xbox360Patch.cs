using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class Xbox360Patch
{
    private const string TypeName = "InControl.Xbox360MacProfile";
    private const string MarkerName = "Microsoft Wireless 360 Controller";
    private const string ExistingAddedName = "Xbox Wireless Controller";
    // Unity's Input.GetJoystickNames() reports this exact controller (a
    // genuine Xbox Wireless Controller, connected over Bluetooth) with a
    // leading space - confirmed via a diagnostic BepInEx plugin logging the
    // real runtime byte values (0x20 'X' 'b' 'o' 'x' ...). HasJoystickName
    // does an exact (if case-insensitive) match with no trimming, so without
    // this the un-prefixed name above never matches the joystick that
    // actually carries live button/axis input - only the inert macOS
    // GameController-framework proxy device ("Microsoft GamePad-1") matched,
    // which is why the patch reported success but the controller still did
    // nothing in game.
    private const string ExistingAddedNameWithLeadingSpace = " Xbox Wireless Controller";
    // macOS is inconsistent about whether it reports a Manufacturer prefix
    // for this same physical device across reconnects - a later diagnostic
    // run (same controller, same machine) reported "Microsoft Xbox Wireless
    // Controller" (Manufacturer+" "+Product, no leading space) instead of the
    // bare-Product " Xbox Wireless Controller" seen earlier. Covering both
    // rather than assuming either is stable - confirmed live: with all three
    // variants present, a real Xbox Wireless Controller registers as a known
    // "XBox 360 Controller" device in InControl and produces working button/
    // stick input in game, not just a clean patch log.
    private const string ExistingAddedNameWithManufacturerPrefix = "Microsoft Xbox Wireless Controller";

    // Nintendo Switch Pro Controller (USB vid 0x057e pid 0x2009), also what
    // third-party pads present in their Switch mode. Same Manufacturer+" "+
    // Product rule as above, read off IOHID by controller-probe.swift
    // (manufacturer "Nintendo Co., Ltd.", product "Pro Controller"); the other
    // two cover the empty-manufacturer (leading space) and bare-product
    // variants, since macOS isn't consistent about that. Adding another
    // controller that GameController exposes as an extended gamepad is just
    // another name here - no button mapping, that comes from the framework
    // (see native/xbox_gamecontroller.swift).
    private static readonly string[] ProControllerNames =
    {
        "Nintendo Co., Ltd. Pro Controller",
        " Pro Controller",
        "Pro Controller"
    };

    private const string XboxProfileTypeName = "InControl.Xbox360MacProfile";
    // The launcher app lists the Unity names of every pad GameController claims at launch (one per line), so the patch
    // recognises pads by what macOS says they are, not by a list of makers. See ControllerNames in swift/.
    private const string PadNamesEnvVar = "OC2XBOXPATCH_PAD_NAMES";
    private const int MaxPadNames = 64;

    // Sony DualShock 4 over USB (Manufacturer+" "+Product as read off IOHID; the older vendor string is for older
    // hardware). The game's own InControl.PlayStation4MacUSBProfile already lists both, and InControl uses the first
    // profile whose names match, in the alphabetical order of UnityInputDeviceProfileList: the PS4 profile comes
    // before Xbox360MacProfile and would keep the pad on Unity's raw HID path, which is the path that gets nothing
    // from a controller macOS GameController has claimed. So the names are added here and taken off the PS4 profile
    // (MoveNamesOffOtherProfiles). The pad then goes through the native bridge like the others and, as
    // with the Switch Pro Controller, shows Xbox button icons.
    private static readonly string[] DualShockNames =
    {
        "Sony Interactive Entertainment Wireless Controller",
        "Sony Computer Entertainment Wireless Controller"
    };

    // Flydigi Direwolf 3 over USB: an Xbox 360-class (XInput) pad, which macOS's own XboxGamepad driver exposes as HID
    // and GameController as "Flydigi Direwolf 3" with an extended gamepad. Manufacturer+" "+Product reads "Flydigi
    // Flydigi Direwolf 3" because the product string already starts with the brand; the other two are the usual
    // empty-manufacturer and bare-product variants. No game profile claims any of them (checked), so without these the
    // game sees an unknown controller.
    private static readonly string[] FlydigiNames =
    {
        "Flydigi Flydigi Direwolf 3",
        " Flydigi Direwolf 3",
        "Flydigi Direwolf 3"
    };

    // Everything appended after the 13 base names, in order: the fixed lists above (kept for runs without the launcher
    // app), then the names the launcher passed for the pads connected right now.
    private static string[] ExtraNames()
    {
        var all = new List<string>();
        all.AddRange(ProControllerNames);
        all.AddRange(DualShockNames);
        all.AddRange(FlydigiNames);
        var passed = Environment.GetEnvironmentVariable(PadNamesEnvVar);
        if (!string.IsNullOrEmpty(passed))
        {
            foreach (var line in passed.Split('\n'))
            {
                var name = line.TrimEnd('\r');
                if (name.Length == 0 || name.Length > 200 || name.IndexOf("GamePad", StringComparison.OrdinalIgnoreCase) >= 0 || all.Contains(name) || all.Count >= MaxPadNames)
                {
                    continue;
                }

                all.Add(name);
            }
        }

        return all.ToArray();
    }

    public static void Apply(AssemblyDefinition assembly)
    {
        try
        {
            if (assembly == null)
            {
                Log("Assembly is null, skipping patch");
                return;
            }

            var targetType = FindType(assembly.MainModule.Types, TypeName);
            if (targetType == null)
            {
                Log("Type not found: " + TypeName);
                return;
            }

            Log("Found Xbox360MacProfile");

            var ctor = FindConstructor(targetType.Methods);
            if (ctor == null)
            {
                Log("Constructor not found");
                return;
            }

            var body = ctor.Body;
            var il = body.GetILProcessor();
            var instructions = body.Instructions;

            if (AnyMatch(instructions, IsExistingAddedNameInstruction))
            {
                Log("Controller names already present, skipping");
                return;
            }

            var markerInstruction = FindFirstMatch(instructions, IsMarkerInstruction);
            if (markerInstruction == null)
            {
                Log("Marker string not found");
                return;
            }

            var markerIndex = instructions.IndexOf(markerInstruction);

            var newArrayInstruction = FindPreviousNewStringArray(instructions, markerIndex);
            if (newArrayInstruction == null)
            {
                Log("JoystickNames array allocation not found");
                return;
            }

            var newArrayIndex = instructions.IndexOf(newArrayInstruction);
            if (newArrayIndex < 1)
            {
                Log("Array size instruction not found");
                return;
            }

            var arraySizeInstruction = instructions[newArrayIndex - 1];
            const int baseNameCount = 13; // original 6 + the 7 added below
            var extraNames = ExtraNames();
            SetLdcI4(arraySizeInstruction, baseNameCount + extraNames.Length);

            var joystickStoreInstruction = FindJoystickNameStore(instructions, newArrayIndex + 1);
            if (joystickStoreInstruction == null)
            {
                Log("JoystickNames store not found");
                return;
            }

            InsertControllerName(il, joystickStoreInstruction, 6, "Microsoft GamePad-1");
            InsertControllerName(il, joystickStoreInstruction, 7, "Microsoft GamePad-2");
            InsertControllerName(il, joystickStoreInstruction, 8, "Microsoft GamePad-3");
            InsertControllerName(il, joystickStoreInstruction, 9, "Microsoft GamePad-4");
            InsertControllerName(il, joystickStoreInstruction, 10, ExistingAddedName);
            InsertControllerName(il, joystickStoreInstruction, 11, ExistingAddedNameWithLeadingSpace);
            InsertControllerName(il, joystickStoreInstruction, 12, ExistingAddedNameWithManufacturerPrefix);
            for (var i = 0; i < extraNames.Length; i++)
            {
                InsertControllerName(il, joystickStoreInstruction, baseNameCount + i, extraNames[i]);
            }

            Log("Found JoystickNames array");
            Log("Added controller names");
            MoveNamesOffOtherProfiles(assembly, extraNames);
            Log("Patch completed");
        }
        catch (Exception ex)
        {
            Log("Patch failed: " + ex);
        }
    }

    // InControl uses the FIRST profile (alphabetical order) that lists a name, so a name the game's own profiles
    // already list (the DualShock 4's, in InControl.PlayStation4MacUSBProfile) would never reach the Xbox profile. Each
    // of the names added above is made unmatchable everywhere else by changing that string constant in the other
    // profiles' constructors to one no controller reports. A name nothing else lists is left alone; if no profile
    // lists it nothing changes at all.
    private static void MoveNamesOffOtherProfiles(AssemblyDefinition assembly, string[] names)
    {
        var moved = 0;
        foreach (var type in assembly.MainModule.Types)
        {
            if (type.Namespace != "InControl" || type.FullName == XboxProfileTypeName)
            {
                continue;
            }

            foreach (var method in type.Methods)
            {
                if (!method.IsConstructor || method.IsStatic || !method.HasBody)
                {
                    continue;
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    var name = instruction.OpCode == OpCodes.Ldstr ? instruction.Operand as string : null;
                    if (name == null || !IsAddedName(names, name))
                    {
                        continue;
                    }

                    instruction.Operand = "(moved to Xbox360MacProfile) " + name;
                    moved++;
                    Log("Moved \"" + name + "\" off " + type.FullName);
                }
            }
        }

        Log("Moved " + moved + " name(s) off other profiles");
    }

    private static bool IsAddedName(string[] names, string name)
    {
        foreach (var known in names)
        {
            if (known == name)
            {
                return true;
            }
        }

        return false;
    }

    // Hand-rolled instead of System.Linq (FirstOrDefault/Any): those take a
    // Func<T,TResult> parameter, and this project's net46 reference
    // assemblies place Func<> in mscorlib - but the actual (much older) Mono
    // corlib Unity bundles with this game still has it in System.Core.dll
    // instead, so the TypeRef can't be resolved at all. Mono throws a
    // TypeLoadException just trying to JIT a method that mentions it, before
    // any of that method's own try/catch can run - see Apply()'s outer catch,
    // which never fired for this exact reason.
    private static TypeDefinition FindType(IEnumerable<TypeDefinition> types, string fullName)
    {
        foreach (var type in types)
        {
            if (type.FullName == fullName)
            {
                return type;
            }
        }

        return null;
    }

    private static MethodDefinition FindConstructor(IEnumerable<MethodDefinition> methods)
    {
        foreach (var method in methods)
        {
            if (method.IsConstructor && !method.IsStatic && method.HasBody)
            {
                return method;
            }
        }

        return null;
    }

    private static bool AnyMatch(IList<Instruction> instructions, InstructionPredicate predicate)
    {
        return FindFirstMatch(instructions, predicate) != null;
    }

    private static Instruction FindFirstMatch(IList<Instruction> instructions, InstructionPredicate predicate)
    {
        foreach (var instruction in instructions)
        {
            if (predicate(instruction))
            {
                return instruction;
            }
        }

        return null;
    }

    // A plain named delegate, unlike System.Func<T,TResult> - its type
    // identity lives in THIS assembly, not in an ambiguous BCL location, so
    // it can't hit the same resolution failure.
    private delegate bool InstructionPredicate(Instruction instruction);

    private static bool IsMarkerInstruction(Instruction instruction)
    {
        return instruction.OpCode == OpCodes.Ldstr
            && instruction.Operand is string s
            && s == MarkerName;
    }

    private static bool IsExistingAddedNameInstruction(Instruction instruction)
    {
        return instruction.OpCode == OpCodes.Ldstr
            && instruction.Operand is string s
            && s == ExistingAddedName;
    }

    private static Instruction FindPreviousNewStringArray(IList<Instruction> instructions, int startIndex)
    {
        for (var i = startIndex; i >= 0; i--)
        {
            var instruction = instructions[i];
            if (instruction.OpCode != OpCodes.Newarr)
            {
                continue;
            }

            var arrayType = instruction.Operand as TypeReference;
            if (arrayType != null && arrayType.FullName == "System.String")
            {
                return instruction;
            }
        }

        return null;
    }

    private static Instruction FindJoystickNameStore(IList<Instruction> instructions, int startIndex)
    {
        for (var i = startIndex; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.OpCode != OpCodes.Stfld)
            {
                continue;
            }

            var field = instruction.Operand as FieldReference;
            if (field != null && field.Name == "JoystickNames")
            {
                return instruction;
            }
        }

        return null;
    }

    private static void SetLdcI4(Instruction instruction, int value)
    {
        instruction.OpCode = OpCodes.Ldc_I4;
        instruction.Operand = value;
    }

    private static void InsertControllerName(ILProcessor il, Instruction insertBefore, int index, string name)
    {
        il.InsertBefore(insertBefore, il.Create(OpCodes.Dup));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Ldc_I4, index));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Ldstr, name));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Stelem_Ref));
    }

    private static void Log(string message)
    {
        Console.WriteLine("[XboxPatch] " + message);
    }
}
