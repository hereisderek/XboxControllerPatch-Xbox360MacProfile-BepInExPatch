using System;
using System.Collections.Generic;
using Mono.Cecil;

public static class Patcher
{
    // Not a `yield return` iterator on purpose: the compiler-generated state
    // machine for those calls System.Environment.CurrentManagedThreadId as a
    // thread-reuse optimization, which doesn't exist in the old Mono corlib
    // Unity bundled with this game - Mono throws a MissingMethodException
    // resolving it, so BepInEx's foreach over TargetDLLs never even reaches
    // "Assembly-CSharp.dll" and the patch silently never applies.
    public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

    public static void Patch(AssemblyDefinition assembly)
    {
#if REQUIRE_LICENSE_TICKET
        Console.WriteLine("[XboxPatch] Checking authorization.");
        if (!LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(10)))
        {
            Console.WriteLine("[XboxPatch] Not authorized on this machine - skipping patch.");
            Console.WriteLine("[XboxPatch] Launch the game through the official launcher app. If it prompts for a serial number, request one for this machine (see the project README).");
            return;
        }
#else
        Console.WriteLine("[XboxPatch] Not checking authorization.");

#endif

        Xbox360Patch.Apply(assembly);
    }
}
