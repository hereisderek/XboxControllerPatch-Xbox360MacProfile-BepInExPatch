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
        Xbox360Patch.Apply(assembly);
    }
}
