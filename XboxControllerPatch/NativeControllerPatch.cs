using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Unity's own native (non-.NET) joystick polling on macOS can't read modern
// Xbox controllers correctly - confirmed by decompiling the engine binary
// itself (see the project's patch-v2/TODO.md for the full Ghidra writeup).
// Xbox360Patch.cs/ControllerIconPatch.cs work around the *symptoms* (name
// recognition, icon selection); this patch fixes the actual input by
// redirecting button/axis reads to a small native helper
// (libOC2NativeXboxInput.dylib, built from patch/native/xbox_gamecontroller.swift)
// that reads the controller via Apple's GameController framework instead of
// Unity's broken path - bypassed entirely, falling through to the original
// Input.GetKey/GetAxisRaw behavior whenever the active device isn't
// Xbox-recognized, isn't currently being matched via the native/raw
// Bluetooth name (see IsRawNativeMatch below - NOT via Steam Input), or the
// native helper reports nothing connected.
//
// Deliberately does NOT engage at all for a device Xbox360Patch.cs matched
// via Steam Input's "Microsoft GamePad-N" rename: InControl overwrites
// InputDevice.Name to the same profile name ("XBox 360 Controller")
// regardless of which raw string actually matched, so Name alone can't
// distinguish "Steam Input" from "real Bluetooth device" - a real bug hit
// live (Steam Input enabled produced total non-response, a regression
// against the pre-native-patch baseline where Steam Input's own remap was
// this project's one confirmed-reliable mechanism; see patch-v2/TODO.md).
// The fix reads UnityEngine.Input.GetJoystickNames() directly - the RAW,
// never-overwritten per-slot name array InControl.UnityInputDeviceManager
// itself populates from - and only proceeds with the native redirect when
// that raw name does NOT contain "GamePad" (Steam Input's own marker,
// Xbox360Patch.cs's added "Microsoft GamePad-1".."GamePad-4" entries).
// Anything ambiguous (the accessor method not found, the array null,
// out-of-range, a null entry) defaults to *not* applying the native
// override - Steam Input is the "let the game handle it" baseline this
// patch must never break, so uncertainty always resolves in its favor.
//
// The dylib is never written into the game's own folder: it ships inside
// this launcher's own bundled BepInEx payload (BepInEx/native/ - a sibling
// of core/plugins/patchers, not a Unity "Plugins" convention) and its
// absolute path is resolved fresh from this patcher DLL's own on-disk
// location every time Apply() runs, then baked into the in-memory patch as
// a full path ModuleReference - Mono's P/Invoke resolver was confirmed (via
// a live DllNotFoundException) to pass a path containing '/' straight to
// dlopen() rather than doing bare-name search-path resolution, so this
// works without needing Contents/Plugins/, DYLD_LIBRARY_PATH, or a
// dllmap config file.
public static class NativeControllerPatch
{
    private const string DylibRelativePath = "native/libOC2NativeXboxInput.dylib";
    private const string BridgeTypeName = "OC2NativeXboxBridge";
    private const string XboxNameSubstring = "XBox";
    // Marker substring common to all four of Xbox360Patch.cs's Steam-Input-only
    // added JoystickNames entries ("Microsoft GamePad-1".."GamePad-4") and not
    // present in any of its native/raw-Bluetooth entries ("Xbox Wireless
    // Controller", " Xbox Wireless Controller", "Microsoft Xbox Wireless
    // Controller") - see that file for the full list.
    private const string SteamInputNameSubstring = "GamePad";
    // Runtime "which path is actually in effect" logging (via UnityEngine.Debug.Log,
    // captured by BepInEx into LogOutput.log the same as any other Unity log line -
    // unlike this file's own Log() helper, which runs during the BepInEx preloader
    // phase before Unity even exists and goes straight to Console.WriteLine instead).
    // GetState/GetValue run continuously (every frame, per button/axis, per player),
    // so this is budget-gated per joystick slot rather than logged unconditionally -
    // LogBudgetLimit lines per joystick slot, covering both the button and analog
    // source (they share the same OC2NativeXboxBridge.LogBudget array/index), then
    // silent for the rest of that launch.
    private const string LogBudgetFieldName = "LogBudget";
    private const int LogBudgetSize = 8;
    private const int LogBudgetLimit = 5;

    public static void Apply(AssemblyDefinition assembly)
    {
        try
        {
            if (assembly == null)
            {
                Log("Assembly is null, skipping native controller patch");
                return;
            }

            var dylibPath = ResolveDylibPath();
            if (dylibPath == null)
            {
                Log("Could not resolve this patcher's own on-disk location - skipping native controller patch");
                return;
            }

            Log("Resolved native helper path: " + dylibPath);
            if (!File.Exists(dylibPath))
            {
                Log("libOC2NativeXboxInput.dylib not found at the path above - native controller " +
                    "support will be unavailable this launch (Steam Input and the Xbox360MacProfile " +
                    "name patch are unaffected). Rebuild with ./build.sh from the repo root to produce it.");
                return;
            }

            var module = assembly.MainModule;

            var inputDeviceType = module.GetType("InControl.InputDevice");
            var unityInputDeviceType = module.GetType("InControl.UnityInputDevice");
            var unityInputDeviceManagerType = module.GetType("InControl.UnityInputDeviceManager");
            var buttonSourceType = module.GetType("InControl.UnityButtonSource");
            var analogSourceType = module.GetType("InControl.UnityAnalogSource");
            if (inputDeviceType == null || unityInputDeviceType == null || unityInputDeviceManagerType == null
                || buttonSourceType == null || analogSourceType == null)
            {
                Log("InControl.UnityButtonSource/UnityAnalogSource/UnityInputDevice/UnityInputDeviceManager not found - skipping native controller patch");
                return;
            }

            var getName = FindMethodByName(inputDeviceType.Methods, "get_Name");
            var getJoystickId = FindMethodByName(unityInputDeviceType.Methods, "get_JoystickId");
            if (getName == null || getJoystickId == null)
            {
                Log("InputDevice.get_Name/UnityInputDevice.get_JoystickId accessors not found - skipping native controller patch");
                return;
            }

            // Found by scanning UnityInputDeviceManager's own IL for its call
            // to UnityEngine.Input.GetJoystickNames(), rather than hand-built
            // as a fresh cross-assembly reference - reusing an existing,
            // already-resolved MethodReference from this exact module avoids
            // needing to construct a correct TypeReference into
            // UnityEngine.CoreModule by hand. If this ever comes back null
            // (e.g. a future game update inlines/renames this call), the
            // whole native patch is skipped rather than risk applying the
            // redirect without any way to tell Steam Input's path apart from
            // the native one - see the file header comment for why.
            var getJoystickNames = FindGetJoystickNamesMethod(unityInputDeviceManagerType);
            if (getJoystickNames == null)
            {
                Log("Could not find UnityEngine.Input.GetJoystickNames() referenced from InControl.UnityInputDeviceManager - " +
                    "skipping native controller patch (can't safely tell Steam Input's path apart from the native one without it)");
                return;
            }

            var bridge = AddOrReuseNativeBridge(module, dylibPath);
            if (bridge == null)
            {
                Log("Failed to add OC2NativeXboxBridge P/Invoke declarations - skipping native controller patch");
                return;
            }

            // Runtime path logging is best-effort and purely additive - found
            // by scanning the whole module for existing call sites to these
            // extremely common methods (reusing valid references the same
            // way as GetJoystickNames above), same reasoning as everywhere
            // else in this file. If any piece is missing (including the
            // LogBudget field itself, e.g. an older bridge type left over
            // from reusing an already-patched module), the redirect/Steam
            // Input logic above is entirely unaffected - only the extra log
            // lines are skipped.
            var debugLog = FindMethodReferenceInModule(module, "Debug", "Log", 1);
            var stringConcat3 = FindMethodReferenceInModule(module, "String", "Concat", 3);
            var objectToString = FindMethodReferenceInModule(module, "Object", "ToString", 0);
            var loggingAvailable = bridge.LogBudgetField != null && debugLog != null && stringConcat3 != null && objectToString != null;
            if (!loggingAvailable)
            {
                Log("Could not find Debug.Log/String.Concat/Object.ToString references (or LogBudget field) in this module - " +
                    "runtime \"which path is active\" logging will be unavailable this launch; the redirect/Steam Input logic itself is unaffected");
            }
            var runtimeLog = new RuntimeLogRefs
            {
                Available = loggingAvailable,
                LogBudgetField = bridge.LogBudgetField,
                DebugLog = debugLog,
                StringConcat3 = stringConcat3,
                ObjectToString = objectToString,
            };

            var buttonPatched = PatchButtonSource(module, buttonSourceType, inputDeviceType, unityInputDeviceType, getName, getJoystickId, getJoystickNames, bridge, runtimeLog);
            var analogPatched = PatchAnalogSource(module, analogSourceType, inputDeviceType, unityInputDeviceType, getName, getJoystickId, getJoystickNames, bridge, runtimeLog);

            if (buttonPatched && analogPatched)
            {
                Log("Native controller patch completed");
            }
        }
        catch (Exception ex)
        {
            Log("Native controller patch failed: " + ex);
        }
    }

    // Resolves this exact patcher DLL's own on-disk path, then walks up from
    // .../BepInEx/patchers/<this>.dll to .../BepInEx/ so the dylib path is
    // always computed relative to wherever THIS launch's BepInEx payload
    // actually lives (inside the launcher app's own bundle - see
    // ownBepInExRoot() in swift/launcher/main.swift), never a path baked in
    // at build time. Falls back to CodeBase (a file:// URI) if Location is
    // ever empty, which can happen if a future BepInEx version loads
    // patchers from an in-memory byte array instead of LoadFile - not
    // observed in practice, but cheap to guard against.
    private static string ResolveDylibPath()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();

        var location = asm.Location;
        if (string.IsNullOrEmpty(location))
        {
            var codeBase = asm.CodeBase;
            if (string.IsNullOrEmpty(codeBase)) return null;
            try
            {
                location = new Uri(codeBase).LocalPath;
            }
            catch (Exception)
            {
                return null;
            }
        }

        var patchersDir = Path.GetDirectoryName(location);
        if (patchersDir == null) return null;
        var bepInExDir = Path.GetDirectoryName(patchersDir);
        if (bepInExDir == null) return null;

        return Path.Combine(bepInExDir, DylibRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private class Bridge
    {
        public MethodReference IsConnected;
        public MethodReference GetButton;
        public MethodReference GetAxis;
        public FieldReference LogBudgetField;
    }

    // Bundles the (possibly-null, best-effort) references needed to emit
    // runtime "which path is active" log lines - see the constants above and
    // EmitBudgetedLog below. A plain class rather than named tuple/Func-like
    // construct for the same old-Mono-corlib-resolution reason as TailBuilder.
    private class RuntimeLogRefs
    {
        public bool Available;
        public FieldReference LogBudgetField;
        public MethodReference DebugLog;
        public MethodReference StringConcat3;
        public MethodReference ObjectToString;
    }

    private static Bridge AddOrReuseNativeBridge(ModuleDefinition module, string dylibPath)
    {
        var existing = module.GetType(BridgeTypeName);
        if (existing != null)
        {
            Log("OC2NativeXboxBridge already present, reusing it");
            var reusedIsConnected = FindMethodByName(existing.Methods, "OC2Xbox_IsConnectedForJoystick");
            var reusedGetButton = FindMethodByName(existing.Methods, "OC2Xbox_GetButtonByLegacyIndexForJoystick");
            var reusedGetAxis = FindMethodByName(existing.Methods, "OC2Xbox_GetAxisByLegacyIndexForJoystick");
            if (reusedIsConnected == null || reusedGetButton == null || reusedGetAxis == null)
            {
                Log("OC2NativeXboxBridge present but missing an expected method - not reusing it");
                return null;
            }
            // Missing on a bridge type added before runtime path logging
            // existed - not fatal, just means that logging stays unavailable
            // this launch (see the Available check built from this in Apply()).
            var reusedLogBudgetField = FindFieldByName(existing.Fields, LogBudgetFieldName);
            return new Bridge { IsConnected = reusedIsConnected, GetButton = reusedGetButton, GetAxis = reusedGetAxis, LogBudgetField = reusedLogBudgetField };
        }

        var moduleRef = new ModuleReference(dylibPath);
        module.ModuleReferences.Add(moduleRef);

        var bridgeType = new TypeDefinition(
            string.Empty,
            BridgeTypeName,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        module.Types.Add(bridgeType);

        MethodDefinition MakePInvoke(string name, TypeReference returnType, params TypeReference[] paramTypes)
        {
            var method = new MethodDefinition(
                name,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl,
                returnType);
            method.IsPreserveSig = true;
            foreach (var paramType in paramTypes)
            {
                method.Parameters.Add(new ParameterDefinition(paramType));
            }
            method.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, name, moduleRef);
            bridgeType.Methods.Add(method);
            return method;
        }

        // Every entry point takes joystickId first (InControl's own
        // UnityInputDevice.JoystickId, 1-based) so the native side can pick
        // the matching physical controller when more than one is connected.
        var int32Type = module.TypeSystem.Int32;
        var isConnected = MakePInvoke("OC2Xbox_IsConnectedForJoystick", int32Type, int32Type);
        var getButton = MakePInvoke("OC2Xbox_GetButtonByLegacyIndexForJoystick", module.TypeSystem.Single, int32Type, int32Type);
        var getAxis = MakePInvoke("OC2Xbox_GetAxisByLegacyIndexForJoystick", module.TypeSystem.Single, int32Type, int32Type);

        // One log-line budget per joystick slot (0..LogBudgetSize-1), shared
        // between the button and analog source's runtime logging - see the
        // constants' own comment. Static int[] fields are zero-initialized by
        // the CLR itself with no explicit .cctor needed for a *scalar*, but
        // an array still needs one instantiated somewhere - added here rather
        // than relying on any implicit default (a static array field defaults
        // to null, not an allocated zero-length/zero-filled array).
        var logBudgetField = new FieldDefinition(LogBudgetFieldName, FieldAttributes.Public | FieldAttributes.Static, new ArrayType(int32Type));
        bridgeType.Fields.Add(logBudgetField);

        var cctor = new MethodDefinition(
            ".cctor",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        var cctorIl = cctor.Body.GetILProcessor();
        cctorIl.Append(Instruction.Create(OpCodes.Ldc_I4, LogBudgetSize));
        cctorIl.Append(Instruction.Create(OpCodes.Newarr, int32Type));
        cctorIl.Append(Instruction.Create(OpCodes.Stsfld, logBudgetField));
        cctorIl.Append(Instruction.Create(OpCodes.Ret));
        bridgeType.Methods.Add(cctor);

        Log("Added OC2NativeXboxBridge P/Invoke declarations targeting: " + dylibPath);
        return new Bridge { IsConnected = isConnected, GetButton = getButton, GetAxis = getAxis, LogBudgetField = logBudgetField };
    }

    private static bool PatchButtonSource(
        ModuleDefinition module,
        TypeDefinition buttonSourceType,
        TypeDefinition inputDeviceType,
        TypeDefinition unityInputDeviceType,
        MethodDefinition getName,
        MethodDefinition getJoystickId,
        MethodReference getJoystickNames,
        Bridge bridge,
        RuntimeLogRefs runtimeLog)
    {
        var method = FindMethodWithParamCount(buttonSourceType.Methods, "GetState", 1);
        if (method == null || !method.HasBody)
        {
            Log("UnityButtonSource.GetState not found - skipping button redirect");
            return false;
        }

        if (AlreadyPatched(method))
        {
            Log("UnityButtonSource.GetState already patched, skipping");
            return true;
        }

        var buttonIdField = FindFieldByName(buttonSourceType.Fields, "ButtonId");
        if (buttonIdField == null)
        {
            Log("UnityButtonSource.ButtonId field not found - skipping button redirect");
            return false;
        }

        InsertRedirect(module, method, inputDeviceType, unityInputDeviceType, getName, getJoystickId, getJoystickNames, bridge.IsConnected, runtimeLog, il =>
        {
            // return OC2NativeXboxBridge.OC2Xbox_GetButtonByLegacyIndexForJoystick(joystickId, this.ButtonId) != 0f;
            return new[]
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, buttonIdField),
                Instruction.Create(OpCodes.Call, bridge.GetButton),
                Instruction.Create(OpCodes.Ldc_R4, 0f),
                Instruction.Create(OpCodes.Ceq),
                Instruction.Create(OpCodes.Ldc_I4_0),
                Instruction.Create(OpCodes.Ceq),
                Instruction.Create(OpCodes.Ret),
            };
        });

        Log("Patched UnityButtonSource.GetState to redirect through the native Xbox bridge when connected");
        return true;
    }

    private static bool PatchAnalogSource(
        ModuleDefinition module,
        TypeDefinition analogSourceType,
        TypeDefinition inputDeviceType,
        TypeDefinition unityInputDeviceType,
        MethodDefinition getName,
        MethodDefinition getJoystickId,
        MethodReference getJoystickNames,
        Bridge bridge,
        RuntimeLogRefs runtimeLog)
    {
        var method = FindMethodWithParamCount(analogSourceType.Methods, "GetValue", 1);
        if (method == null || !method.HasBody)
        {
            Log("UnityAnalogSource.GetValue not found - skipping analog redirect");
            return false;
        }

        if (AlreadyPatched(method))
        {
            Log("UnityAnalogSource.GetValue already patched, skipping");
            return true;
        }

        var analogIdField = FindFieldByName(analogSourceType.Fields, "AnalogId");
        if (analogIdField == null)
        {
            Log("UnityAnalogSource.AnalogId field not found - skipping analog redirect");
            return false;
        }

        InsertRedirect(module, method, inputDeviceType, unityInputDeviceType, getName, getJoystickId, getJoystickNames, bridge.IsConnected, runtimeLog, il =>
        {
            // return OC2NativeXboxBridge.OC2Xbox_GetAxisByLegacyIndexForJoystick(joystickId, this.AnalogId);
            return new[]
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, analogIdField),
                Instruction.Create(OpCodes.Call, bridge.GetAxis),
                Instruction.Create(OpCodes.Ret),
            };
        });

        Log("Patched UnityAnalogSource.GetValue to redirect through the native Xbox bridge when connected");
        return true;
    }

    // Shared prologue for both GetState/GetValue: bail out to the method's
    // A plain named delegate, unlike System.Func<T,TResult> - confirmed live
    // (via a TypeLoadException in BepInEx/LogOutput.log) that this exact
    // project's old embedded Mono corlib can't resolve System.Func<> even
    // when only used as a local method-signature type, not called - same
    // pitfall Xbox360Patch.cs's FindType/FindConstructor helpers already
    // avoid for the same reason. See that file's own comment for the details.
    private delegate Instruction[] TailBuilder(ILProcessor il);

    // original, untouched body (falling through to Input.GetKey/GetAxisRaw)
    // unless arg1 (the InControl InputDevice this call is for) is non-null,
    // Xbox-recognized by name, castable to UnityInputDevice, and the native
    // bridge reports that joystick connected - only then does tailInstructions
    // run and actually return a value from the native side.
    private static void InsertRedirect(
        ModuleDefinition module,
        MethodDefinition method,
        TypeDefinition inputDeviceType,
        TypeDefinition unityInputDeviceType,
        MethodDefinition getName,
        MethodDefinition getJoystickId,
        MethodReference getJoystickNames,
        MethodReference isConnected,
        RuntimeLogRefs runtimeLog,
        TailBuilder makeTail)
    {
        var stringType = module.TypeSystem.String;
        var boolType = module.TypeSystem.Boolean;
        var int32Type = module.TypeSystem.Int32;
        // ArrayType directly rather than Mono.Cecil.Rocks' MakeArrayType()
        // extension - this project only references the core Mono.Cecil.dll.
        var stringArrayType = new ArrayType(stringType);
        var stringContains = new MethodReference("Contains", boolType, stringType) { HasThis = true };
        stringContains.Parameters.Add(new ParameterDefinition(stringType));

        var body = method.Body;
        var il = body.GetILProcessor();
        var originalStart = body.Instructions[0];

        var nameVar = new VariableDefinition(stringType);
        body.Variables.Add(nameVar);
        var joystickIdVar = new VariableDefinition(int32Type);
        body.Variables.Add(joystickIdVar);
        var rawNamesVar = new VariableDefinition(stringArrayType);
        body.Variables.Add(rawNamesVar);
        var rawIndexVar = new VariableDefinition(int32Type);
        body.Variables.Add(rawIndexVar);
        var rawNameVar = new VariableDefinition(stringType);
        body.Variables.Add(rawNameVar);
        body.InitLocals = true;

        void Insert(Instruction instruction) => il.InsertBefore(originalStart, instruction);

        // if (inputDevice == null) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldarg_1));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // var name = inputDevice.Name;
        // if (name == null) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldarg_1));
        Insert(Instruction.Create(OpCodes.Callvirt, getName));
        Insert(Instruction.Create(OpCodes.Dup));
        Insert(Instruction.Create(OpCodes.Stloc, nameVar));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // if (!name.Contains("XBox")) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldloc, nameVar));
        Insert(Instruction.Create(OpCodes.Ldstr, XboxNameSubstring));
        Insert(Instruction.Create(OpCodes.Callvirt, stringContains));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // int joystickId = (inputDevice as UnityInputDevice).JoystickId; (same
        // unconditional-cast pattern InControl's own untouched code already uses)
        Insert(Instruction.Create(OpCodes.Ldarg_1));
        Insert(Instruction.Create(OpCodes.Isinst, unityInputDeviceType));
        Insert(Instruction.Create(OpCodes.Callvirt, getJoystickId));
        Insert(Instruction.Create(OpCodes.Stloc, joystickIdVar));

        // Steam Input vs. native/raw-Bluetooth disambiguation - see the file
        // header comment. inputDevice.Name is already overwritten to the
        // profile name by this point regardless of which path matched, so
        // this reads the RAW per-slot name Unity itself reports (never
        // overwritten by InControl) straight from
        // UnityEngine.Input.GetJoystickNames() and only proceeds if that raw
        // name does NOT look like Xbox360Patch.cs's Steam-Input-only
        // "Microsoft GamePad-N" entries. Any ambiguity here (null array,
        // out-of-range index, null entry) falls through to originalStart -
        // i.e. defaults to leaving Steam Input's own path completely alone.

        // var rawNames = UnityEngine.Input.GetJoystickNames();
        // if (rawNames == null) goto originalStart;
        Insert(Instruction.Create(OpCodes.Call, getJoystickNames));
        Insert(Instruction.Create(OpCodes.Dup));
        Insert(Instruction.Create(OpCodes.Stloc, rawNamesVar));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // int rawIndex = joystickId - 1;
        Insert(Instruction.Create(OpCodes.Ldloc, joystickIdVar));
        Insert(Instruction.Create(OpCodes.Ldc_I4_1));
        Insert(Instruction.Create(OpCodes.Sub));
        Insert(Instruction.Create(OpCodes.Stloc, rawIndexVar));

        // if (rawIndex < 0) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldloc, rawIndexVar));
        Insert(Instruction.Create(OpCodes.Ldc_I4_0));
        Insert(Instruction.Create(OpCodes.Blt, originalStart));

        // if (rawIndex >= rawNames.Length) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldloc, rawIndexVar));
        Insert(Instruction.Create(OpCodes.Ldloc, rawNamesVar));
        Insert(Instruction.Create(OpCodes.Ldlen));
        Insert(Instruction.Create(OpCodes.Conv_I4));
        Insert(Instruction.Create(OpCodes.Bge, originalStart));

        // var rawName = rawNames[rawIndex];
        // if (rawName == null) goto originalStart;
        Insert(Instruction.Create(OpCodes.Ldloc, rawNamesVar));
        Insert(Instruction.Create(OpCodes.Ldloc, rawIndexVar));
        Insert(Instruction.Create(OpCodes.Ldelem_Ref));
        Insert(Instruction.Create(OpCodes.Dup));
        Insert(Instruction.Create(OpCodes.Stloc, rawNameVar));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        // bool isSteamInput = rawName.Contains("GamePad"); (Steam Input's own
        // marker - Xbox360Patch.cs's "Microsoft GamePad-N" entries)
        var isSteamInputVar = new VariableDefinition(boolType);
        body.Variables.Add(isSteamInputVar);
        Insert(Instruction.Create(OpCodes.Ldloc, rawNameVar));
        Insert(Instruction.Create(OpCodes.Ldstr, SteamInputNameSubstring));
        Insert(Instruction.Create(OpCodes.Callvirt, stringContains));
        Insert(Instruction.Create(OpCodes.Stloc, isSteamInputVar));

        // nativePathStart isn't inserted yet (created here so it can be used
        // as this branch's target - the same forward-reference pattern this
        // method already uses for originalStart, just for a fresh
        // instruction instead of an existing one) - physically inserted
        // further down, right where the native-path checks actually begin.
        var nativePathStart = Instruction.Create(OpCodes.Ldloc, joystickIdVar);
        Insert(Instruction.Create(OpCodes.Ldloc, isSteamInputVar));
        Insert(Instruction.Create(OpCodes.Brfalse, nativePathStart));

        // Steam Input's own path - let it handle this device untouched.
        // EmitBudgetedLog's own internal bounds/budget checks must skip
        // forward only to THIS branch (the very next instruction after the
        // log block), never all the way to originalStart - that was a real
        // bug caught by decompiling the patched output before ever shipping
        // it (see EmitBudgetedLog's own comment): passing originalStart as
        // the skip target there made a budget-exhausted call skip the actual
        // redirect/return below it too, not just the logging, silently
        // breaking native input after LogBudgetLimit frames.
        //
        // branchToOriginalStart/tailStart below must be physically inserted
        // into the body (via Insert()) BEFORE being passed to EmitBudgetedLog
        // as its target - Cecil's ILProcessor.InsertBefore requires its
        // target to already be part of the method body's instruction list
        // (it throws ArgumentOutOfRangeException otherwise, caught live
        // here); EmitBudgetedLog's own repeated InsertBefore(target, ...)
        // calls then correctly build the log block immediately in front of
        // wherever that target currently sits, same as every other prologue
        // instruction in this method being built up in front of originalStart.
        var branchToOriginalStart = Instruction.Create(OpCodes.Br, originalStart);
        Insert(branchToOriginalStart);
        if (runtimeLog.Available)
        {
            EmitBudgetedLog(il, branchToOriginalStart, rawIndexVar, joystickIdVar, module.TypeSystem.Int32, runtimeLog,
                "[XboxPatch] [Native] Steam Input active for joystick ",
                " - native redirect intentionally skipped (raw joystick name matched Steam Input's rename); letting Unity's own Input.GetKey/GetAxisRaw handle it");
        }

        // Native/raw-Bluetooth path.
        Insert(nativePathStart); // == "Ldloc joystickIdVar", the first half of the IsConnectedForJoystick call below
        Insert(Instruction.Create(OpCodes.Call, isConnected));
        Insert(Instruction.Create(OpCodes.Brfalse, originalStart));

        var tailStart = Instruction.Create(OpCodes.Ldloc, joystickIdVar);
        Insert(tailStart);
        if (runtimeLog.Available)
        {
            EmitBudgetedLog(il, tailStart, rawIndexVar, joystickIdVar, module.TypeSystem.Int32, runtimeLog,
                "[XboxPatch] [Native] Native GameController redirect ACTIVE for joystick ",
                " (Steam Input is off or not managing this device)");
        }

        foreach (var instruction in makeTail(il))
        {
            Insert(instruction);
        }
    }

    // Emits a budget-gated UnityEngine.Debug.Log(prefix + joystickId + suffix)
    // call, inserted immediately before `insertBefore` (which doubles as the
    // "skip the rest of this log block" branch target for every bounds/budget
    // check - see the call sites' own comments). Never throws/skips silently
    // instead if rawIndex is out of LogBudget's bounds - this is a diagnostic
    // nicety layered on top of already-correct redirect logic, never allowed
    // to change behavior on its own.
    private static void EmitBudgetedLog(
        ILProcessor il,
        Instruction insertBefore,
        VariableDefinition rawIndexVar,
        VariableDefinition joystickIdVar,
        TypeReference int32Type,
        RuntimeLogRefs runtimeLog,
        string messagePrefix,
        string messageSuffix)
    {
        void Insert(Instruction instruction) => il.InsertBefore(insertBefore, instruction);

        // if (rawIndex < 0 || rawIndex >= LogBudgetSize) skip logging.
        Insert(Instruction.Create(OpCodes.Ldloc, rawIndexVar));
        Insert(Instruction.Create(OpCodes.Ldc_I4_0));
        Insert(Instruction.Create(OpCodes.Blt, insertBefore));
        Insert(Instruction.Create(OpCodes.Ldloc, rawIndexVar));
        Insert(Instruction.Create(OpCodes.Ldc_I4, LogBudgetSize));
        Insert(Instruction.Create(OpCodes.Bge, insertBefore));

        // if (LogBudget[rawIndex] >= LogBudgetLimit) skip logging.
        Insert(Instruction.Create(OpCodes.Ldsfld, runtimeLog.LogBudgetField));
        Insert(Instruction.Create(OpCodes.Ldloc, rawIndexVar));
        Insert(Instruction.Create(OpCodes.Ldelem_I4));
        Insert(Instruction.Create(OpCodes.Ldc_I4, LogBudgetLimit));
        Insert(Instruction.Create(OpCodes.Bge, insertBefore));

        // LogBudget[rawIndex]++;
        Insert(Instruction.Create(OpCodes.Ldsfld, runtimeLog.LogBudgetField));
        Insert(Instruction.Create(OpCodes.Ldloc, rawIndexVar));
        Insert(Instruction.Create(OpCodes.Ldsfld, runtimeLog.LogBudgetField));
        Insert(Instruction.Create(OpCodes.Ldloc, rawIndexVar));
        Insert(Instruction.Create(OpCodes.Ldelem_I4));
        Insert(Instruction.Create(OpCodes.Ldc_I4_1));
        Insert(Instruction.Create(OpCodes.Add));
        Insert(Instruction.Create(OpCodes.Stelem_I4));

        // UnityEngine.Debug.Log(messagePrefix + joystickId.ToString() + messageSuffix);
        // joystickId (an Int32 local) is boxed so Object.ToString() can be
        // called on it via callvirt (virtual dispatch on the boxed instance
        // resolves to Int32's own override) - the boxed reference is then
        // discarded once ToString()'s result is on the stack.
        Insert(Instruction.Create(OpCodes.Ldstr, messagePrefix));
        Insert(Instruction.Create(OpCodes.Ldloc, joystickIdVar));
        Insert(Instruction.Create(OpCodes.Box, int32Type));
        Insert(Instruction.Create(OpCodes.Callvirt, runtimeLog.ObjectToString));
        Insert(Instruction.Create(OpCodes.Ldstr, messageSuffix));
        Insert(Instruction.Create(OpCodes.Call, runtimeLog.StringConcat3));
        Insert(Instruction.Create(OpCodes.Call, runtimeLog.DebugLog));
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

    // Scans every method body in InControl.UnityInputDeviceManager for a
    // call to a method named "GetJoystickNames" and returns that exact
    // MethodReference (resolved to [UnityEngine.CoreModule]UnityEngine.Input,
    // confirmed via ilspycmd against the real assembly) - reusing an
    // already-valid external reference from this same module instead of
    // hand-constructing a fresh cross-assembly TypeReference/MethodReference,
    // which would need to get UnityEngine.CoreModule's exact assembly
    // identity right.
    private static MethodReference FindGetJoystickNamesMethod(TypeDefinition unityInputDeviceManagerType)
    {
        foreach (var method in unityInputDeviceManagerType.Methods)
        {
            if (!method.HasBody) continue;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) continue;
                if (instruction.Operand is MethodReference callee && callee.Name == "GetJoystickNames")
                {
                    return callee;
                }
            }
        }

        return null;
    }

    // Scans every method body of every TOP-LEVEL type in the module (nested
    // types, e.g. "ControllerIconLookup/PlatformSet", are not walked - not
    // needed in practice: Debug.Log/String.Concat/Object.ToString are common
    // enough to expect a hit well before exhausting top-level types alone)
    // for a call to a method matching declaringTypeSimpleName/methodName/
    // paramCount, returning that exact MethodReference for reuse - same
    // "reuse an already-valid external reference instead of hand-constructing
    // one" reasoning as FindGetJoystickNamesMethod above, just not scoped to
    // one specific type since these three are ordinary BCL/Unity methods
    // that could plausibly be called from almost anywhere.
    private static MethodReference FindMethodReferenceInModule(ModuleDefinition module, string declaringTypeSimpleName, string methodName, int paramCount)
    {
        foreach (var type in module.Types)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) continue;
                    if (instruction.Operand is MethodReference callee
                        && callee.Name == methodName
                        && callee.Parameters.Count == paramCount
                        && callee.DeclaringType.Name == declaringTypeSimpleName)
                    {
                        return callee;
                    }
                }
            }
        }

        return null;
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

    private static MethodDefinition FindMethodWithParamCount(IEnumerable<MethodDefinition> methods, string name, int paramCount)
    {
        foreach (var method in methods)
        {
            if (method.Name == name && method.HasBody && method.Parameters.Count == paramCount)
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
        Console.WriteLine("[XboxPatch] [Native] " + message);
    }
}
