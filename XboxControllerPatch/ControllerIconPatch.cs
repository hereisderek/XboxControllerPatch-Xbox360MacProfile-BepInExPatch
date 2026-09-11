using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The Mac build of this game has no real per-brand controller detection at
// all: ControllerIconLookup.PlatformSet.GetIconPack(DeviceContext) hardcodes
// PS4 icons for every non-keyboard gamepad (DeviceContext.Pad), regardless
// of what's actually connected - confirmed by decompiling the method itself.
// On other platforms this is presumably overridden by Steamworks's own
// controller-type detection (ISteamInput), which isn't available when the
// game doesn't have a live Steam session (our own logs always show
// "[API loaded no]" when launched this way).
//
// This patches PlatformSet.GetIconPack to check the currently active
// InControl device's Name - which Xbox360Patch.cs's fix already makes read
// "XBox 360 Controller" for a correctly-recognized Xbox controller - and
// return the XboxOne icon set in that case, falling through to the existing
// (PS4-for-everything-else) logic otherwise so real PS4/unknown controllers
// are unaffected.
public static class ControllerIconPatch
{
    private const string PlatformSetTypeName = "ControllerIconLookup/PlatformSet";
    private const string ControllerTypeSpritesTypeName = "ControllerTypeSprites";
    private const string SemanticPlatformSetTypeName = "SemanticIconLookup/PlatformSet";
    private const string KeyboardUtilsTypeName = "KeyboardUtils";
    private const string InputManagerTypeName = "InControl.InputManager";
    private const string InputDeviceTypeName = "InControl.InputDevice";
    private const string XboxNameSubstring = "XBox";

    public static void Apply(AssemblyDefinition assembly)
    {
        try
        {
            if (assembly == null)
            {
                Log("Assembly is null, skipping icon patch");
                return;
            }

            var module = assembly.MainModule;

            var inputManagerType = module.GetType(InputManagerTypeName);
            var inputDeviceType = module.GetType(InputDeviceTypeName);
            if (inputManagerType == null || inputDeviceType == null)
            {
                Log("InControl types not found - skipping icon patch");
                return;
            }

            var getActiveDevice = FindMethodByName(inputManagerType.Methods, "get_ActiveDevice");
            var getName = FindMethodByName(inputDeviceType.Methods, "get_Name");
            if (getActiveDevice == null || getName == null)
            {
                Log("InControl.InputManager/InputDevice accessors not found - skipping icon patch");
                return;
            }

            var stringType = module.TypeSystem.String;
            var boolType = module.TypeSystem.Boolean;
            var stringContains = new MethodReference("Contains", boolType, stringType) { HasThis = true };
            stringContains.Parameters.Add(new ParameterDefinition(stringType));

            PatchPlatformSetIconPack(module, inputDeviceType, getActiveDevice, getName, stringContains);
            PatchControllerTypeSprites(module, inputDeviceType, getActiveDevice, getName, stringContains);
            // Temporarily disabled while isolating a freeze reported when
            // opening the Controller Options / "game control" screen - see
            // if the freeze goes away without this one specifically before
            // digging further into it.
            // PatchSemanticPlatformSet(module, inputDeviceType, getActiveDevice, getName, stringContains);
        }
        catch (Exception ex)
        {
            Log("Icon patch failed: " + ex);
        }
    }

    // Fixes button-prompt icons (e.g. HUD "press A" hints) shown during
    // gameplay - see ControllerIconLookup.PlatformSet.GetIconPack.
    private static void PatchPlatformSetIconPack(
        ModuleDefinition module,
        TypeDefinition inputDeviceType,
        MethodDefinition getActiveDevice,
        MethodDefinition getName,
        MethodReference stringContains)
    {
        var platformSetType = module.GetType(PlatformSetTypeName);
        if (platformSetType == null)
        {
            Log("PlatformSet type not found - skipping button icon patch");
            return;
        }

        var getIconPack = FindMethodByName(platformSetType.Methods, "GetIconPack");
        if (getIconPack == null || !getIconPack.HasBody)
        {
            Log("PlatformSet.GetIconPack not found - skipping button icon patch");
            return;
        }

        if (AlreadyPatched(getIconPack))
        {
            Log("Button icon patch already present, skipping");
            return;
        }

        var xboxOneField = FindFieldByName(platformSetType.Fields, "XboxOne");
        if (xboxOneField == null)
        {
            Log("PlatformSet.XboxOne field not found - skipping button icon patch");
            return;
        }

        InsertXboxIconCheck(getIconPack, inputDeviceType, xboxOneField, getActiveDevice, getName, stringContains);

        Log("Patched button-prompt icon lookup to prefer Xbox icons for a recognized Xbox device");
    }

    // Fixes the full controller graphic shown in the player-join/lobby
    // "gamepad menu" (FrontendPlayerSlot.SetControllerIconForUser ->
    // ControllerTypeSprites.GetImage -> GetControllerSpritesForPlatform).
    // That method picks Xbox/PS4/Switch sprites from a fixed serialized
    // field (m_padToUseOnPC, baked in as PS4 in this build) - it never
    // looks at which controller is actually connected, unlike the
    // in-gameplay button-prompt icons patched above. Confirmed by
    // decompiling: nothing in the assembly ever writes to
    // m_padToUseOnPC other than the constructor's serialized default.
    private static void PatchControllerTypeSprites(
        ModuleDefinition module,
        TypeDefinition inputDeviceType,
        MethodDefinition getActiveDevice,
        MethodDefinition getName,
        MethodReference stringContains)
    {
        var spritesType = module.GetType(ControllerTypeSpritesTypeName);
        if (spritesType == null)
        {
            Log("ControllerTypeSprites type not found - skipping gamepad menu icon patch");
            return;
        }

        var getSpritesForPlatform = FindMethodByName(spritesType.Methods, "GetControllerSpritesForPlatform");
        if (getSpritesForPlatform == null || !getSpritesForPlatform.HasBody)
        {
            Log("ControllerTypeSprites.GetControllerSpritesForPlatform not found - skipping gamepad menu icon patch");
            return;
        }

        if (AlreadyPatched(getSpritesForPlatform))
        {
            Log("Gamepad menu icon patch already present, skipping");
            return;
        }

        var x1Field = FindFieldByName(spritesType.Fields, "m_padSpritesX1");
        if (x1Field == null)
        {
            Log("ControllerTypeSprites.m_padSpritesX1 field not found - skipping gamepad menu icon patch");
            return;
        }

        InsertXboxSpritesCheck(getSpritesForPlatform, inputDeviceType, x1Field, getActiveDevice, getName, stringContains);

        Log("Patched gamepad menu icon lookup to prefer Xbox sprites for a recognized Xbox device");
    }

    // Fixes semantic action icons (e.g. the "Controller Options" / control
    // legend screen, which draws button prompts by semantic action name
    // rather than by raw button) - SemanticIconLookup.PlatformSet.GetIconPack
    // hardcodes PS4 for every non-keyboard case (the XboxOne/Switch fields on
    // this same PlatformSet are never read anywhere in the assembly),
    // independent of both fixes above.
    private static void PatchSemanticPlatformSet(
        ModuleDefinition module,
        TypeDefinition inputDeviceType,
        MethodDefinition getActiveDevice,
        MethodDefinition getName,
        MethodReference stringContains)
    {
        var platformSetType = module.GetType(SemanticPlatformSetTypeName);
        if (platformSetType == null)
        {
            Log("SemanticIconLookup/PlatformSet type not found - skipping semantic icon patch");
            return;
        }

        var getIconPack = FindMethodByName(platformSetType.Methods, "GetIconPack");
        if (getIconPack == null || !getIconPack.HasBody)
        {
            Log("SemanticIconLookup/PlatformSet.GetIconPack not found - skipping semantic icon patch");
            return;
        }

        if (AlreadyPatched(getIconPack))
        {
            Log("Semantic icon patch already present, skipping");
            return;
        }

        var xboxOneField = FindFieldByName(platformSetType.Fields, "XboxOne");
        if (xboxOneField == null)
        {
            Log("SemanticIconLookup/PlatformSet.XboxOne field not found - skipping semantic icon patch");
            return;
        }

        var keyboardUtilsType = module.GetType(KeyboardUtilsTypeName);
        var isKeyboard = keyboardUtilsType == null ? null : FindMethodByName(keyboardUtilsType.Methods, "IsKeyboard");
        if (isKeyboard == null)
        {
            Log("KeyboardUtils.IsKeyboard not found - skipping semantic icon patch");
            return;
        }

        InsertXboxSemanticIconCheck(getIconPack, inputDeviceType, xboxOneField, isKeyboard, getActiveDevice, getName, stringContains);

        Log("Patched semantic icon lookup to prefer Xbox icons for a recognized Xbox device");
    }

    private static void InsertXboxSemanticIconCheck(
        MethodDefinition getIconPack,
        TypeDefinition inputDeviceType,
        FieldDefinition xboxOneField,
        MethodDefinition isKeyboard,
        MethodDefinition getActiveDevice,
        MethodDefinition getName,
        MethodReference stringContains)
    {
        var body = getIconPack.Body;
        var il = body.GetILProcessor();
        var originalStart = body.Instructions[0];

        var deviceVar = new VariableDefinition(inputDeviceType);
        body.Variables.Add(deviceVar);
        var nameVar = new VariableDefinition(getIconPack.Module.TypeSystem.String);
        body.Variables.Add(nameVar);
        body.InitLocals = true;

        void Insert(Instruction instruction) => il.InsertBefore(originalStart, instruction);

        // if (KeyboardUtils.IsKeyboard(Player.Player1)) goto originalStart; -
        // preserve the existing keyboard -> PC behavior untouched (the
        // original method re-checks this itself when we fall through).
        Insert(Instruction.Create(OpCodes.Ldc_I4_0));
        Insert(Instruction.Create(OpCodes.Call, isKeyboard));
        Insert(Instruction.Create(OpCodes.Brtrue, originalStart));

        // var device = InputManager.ActiveDevice;
        // if (device == null) goto originalStart;
        Insert(Instruction.Create(OpCodes.Call, getActiveDevice));
        Insert(Instruction.Create(OpCodes.Stloc, deviceVar));
        Insert(Instruction.Create(OpCodes.Ldloc, deviceVar));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // var name = device.Name;
        // if (name == null) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldloc, deviceVar));
        Insert(Instruction.Create(OpCodes.Callvirt, getName));
        Insert(Instruction.Create(OpCodes.Dup));
        Insert(Instruction.Create(OpCodes.Stloc, nameVar));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // if (!name.Contains("XBox")) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldloc, nameVar));
        Insert(Instruction.Create(OpCodes.Ldstr, XboxNameSubstring));
        Insert(Instruction.Create(OpCodes.Callvirt, stringContains));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // return this.XboxOne;
        Insert(Instruction.Create(OpCodes.Ldarg_0));
        Insert(Instruction.Create(OpCodes.Ldfld, xboxOneField));
        Insert(Instruction.Create(OpCodes.Ret));
    }

    private static void InsertXboxIconCheck(
        MethodDefinition getIconPack,
        TypeDefinition inputDeviceType,
        FieldDefinition xboxOneField,
        MethodDefinition getActiveDevice,
        MethodDefinition getName,
        MethodReference stringContains)
    {
        var body = getIconPack.Body;
        var il = body.GetILProcessor();
        var originalStart = body.Instructions[0];

        var deviceVar = new VariableDefinition(inputDeviceType);
        body.Variables.Add(deviceVar);
        var nameVar = new VariableDefinition(getIconPack.Module.TypeSystem.String);
        body.Variables.Add(nameVar);
        body.InitLocals = true;

        void Insert(Instruction instruction) => il.InsertBefore(originalStart, instruction);

        // if (deviceContext != DeviceContext.Pad) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldarg_1));
        Insert(Instruction.Create(OpCodes.Ldc_I4_1));
        Insert(Instruction.Create(OpCodes.Bne_Un, originalStart));

        // var device = InputManager.ActiveDevice;
        // if (device == null) goto originalStart;
        Insert(Instruction.Create(OpCodes.Call, getActiveDevice));
        Insert(Instruction.Create(OpCodes.Stloc, deviceVar));
        Insert(Instruction.Create(OpCodes.Ldloc, deviceVar));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // var name = device.Name;
        // if (name == null) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldloc, deviceVar));
        Insert(Instruction.Create(OpCodes.Callvirt, getName));
        Insert(Instruction.Create(OpCodes.Dup));
        Insert(Instruction.Create(OpCodes.Stloc, nameVar));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // if (!name.Contains("XBox")) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldloc, nameVar));
        Insert(Instruction.Create(OpCodes.Ldstr, XboxNameSubstring));
        Insert(Instruction.Create(OpCodes.Callvirt, stringContains));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // return this.XboxOne;
        Insert(Instruction.Create(OpCodes.Ldarg_0));
        Insert(Instruction.Create(OpCodes.Ldfld, xboxOneField));
        Insert(Instruction.Create(OpCodes.Ret));
    }

    private static void InsertXboxSpritesCheck(
        MethodDefinition getSpritesForPlatform,
        TypeDefinition inputDeviceType,
        FieldDefinition x1Field,
        MethodDefinition getActiveDevice,
        MethodDefinition getName,
        MethodReference stringContains)
    {
        var body = getSpritesForPlatform.Body;
        var il = body.GetILProcessor();
        var originalStart = body.Instructions[0];

        var deviceVar = new VariableDefinition(inputDeviceType);
        body.Variables.Add(deviceVar);
        var nameVar = new VariableDefinition(getSpritesForPlatform.Module.TypeSystem.String);
        body.Variables.Add(nameVar);
        body.InitLocals = true;

        void Insert(Instruction instruction) => il.InsertBefore(originalStart, instruction);

        // if (controlType == ControlTypeEnum.Keyboard) goto originalStart; -
        // don't override the keyboard sprite selection.
        Insert(Instruction.Create(OpCodes.Ldarg_1));
        Insert(Instruction.Create(OpCodes.Ldc_I4_1));
        Insert(Instruction.Create(OpCodes.Beq, originalStart));

        // var device = InputManager.ActiveDevice;
        // if (device == null) goto originalStart;
        Insert(Instruction.Create(OpCodes.Call, getActiveDevice));
        Insert(Instruction.Create(OpCodes.Stloc, deviceVar));
        Insert(Instruction.Create(OpCodes.Ldloc, deviceVar));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // var name = device.Name;
        // if (name == null) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldloc, deviceVar));
        Insert(Instruction.Create(OpCodes.Callvirt, getName));
        Insert(Instruction.Create(OpCodes.Dup));
        Insert(Instruction.Create(OpCodes.Stloc, nameVar));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // if (!name.Contains("XBox")) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldloc, nameVar));
        Insert(Instruction.Create(OpCodes.Ldstr, XboxNameSubstring));
        Insert(Instruction.Create(OpCodes.Callvirt, stringContains));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // return this.m_padSpritesX1;
        Insert(Instruction.Create(OpCodes.Ldarg_0));
        Insert(Instruction.Create(OpCodes.Ldfld, x1Field));
        Insert(Instruction.Create(OpCodes.Ret));
    }

    private static bool AlreadyPatched(MethodDefinition method)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Ldstr && XboxNameSubstring.Equals(instruction.Operand as string, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static MethodDefinition FindMethodByName(IEnumerable<MethodDefinition> methods, string name)
    {
        foreach (var method in methods)
        {
            if (method.Name == name)
            {
                return method;
            }
        }

        return null;
    }

    private static FieldDefinition FindFieldByName(IEnumerable<FieldDefinition> fields, string name)
    {
        foreach (var field in fields)
        {
            if (field.Name == name)
            {
                return field;
            }
        }

        return null;
    }

    private static void Log(string message)
    {
        Console.WriteLine("[XboxPatch] [Icon] " + message);
    }
}
