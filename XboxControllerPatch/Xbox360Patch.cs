using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class Xbox360Patch
{
    private const string TypeName = "InControl.Xbox360MacProfile";
    private const string MarkerName = "Microsoft Wireless 360 Controller";
    private const string ExistingAddedName = "Xbox Wireless Controller";

    public static void Apply(AssemblyDefinition assembly)
    {
        try
        {
            if (assembly == null)
            {
                Log("Assembly is null, skipping patch");
                return;
            }

            var targetType = assembly.MainModule.Types.FirstOrDefault(t => t.FullName == TypeName);
            if (targetType == null)
            {
                Log("Type not found: " + TypeName);
                return;
            }

            Log("Found Xbox360MacProfile");

            var ctor = targetType.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.HasBody);
            if (ctor == null)
            {
                Log("Constructor not found");
                return;
            }

            var body = ctor.Body;
            var il = body.GetILProcessor();
            var instructions = body.Instructions;

            if (instructions.Any(IsExistingAddedNameInstruction))
            {
                Log("Controller names already present, skipping");
                return;
            }

            var markerInstruction = instructions.FirstOrDefault(IsMarkerInstruction);
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
            SetLdcI4(arraySizeInstruction, 11);

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

            Log("Found JoystickNames array");
            Log("Added controller names");
            Log("Patch completed");
        }
        catch (Exception ex)
        {
            Log("Patch failed: " + ex);
        }
    }

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
