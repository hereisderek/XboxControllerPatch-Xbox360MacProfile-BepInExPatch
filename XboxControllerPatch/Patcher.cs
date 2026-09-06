using System;
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

    public static void Patch(AssemblyDefinition assembly)
    {
        if (!LicenseTicket.IsAuthorized(TimeSpan.FromMinutes(10)))
        {
            Console.WriteLine("[XboxPatch] Not authorized on this machine - skipping patch.");
            Console.WriteLine("[XboxPatch] Launch the game through the official launcher app. If it prompts for a serial number, request one for this machine (see the project README).");
            return;
        }

        Xbox360Patch.Apply(assembly);
    }
}
