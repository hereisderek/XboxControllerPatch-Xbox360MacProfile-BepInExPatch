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
            SetLdcI4(arraySizeInstruction, 12);

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

            Log("Found JoystickNames array");
            Log("Added controller names");
            Log("Patch completed");
        }
        catch (Exception ex)
        {
            Log("Patch failed: " + ex);
        }
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
