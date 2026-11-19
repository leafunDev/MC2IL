using System.Collections.Generic;
using LibMC2IL;

namespace MC2IL.Core.Api;

public static class InstructionSetRegistry
{
    private static Dictionary<InstructionSetId, MC2ILInstructionSet> _registeredSets = new();

    public static void RegisterInstructionSet<T>(InstructionSetId forId) where T : MC2ILInstructionSet, new() => _registeredSets.Add(forId, new T());

    public static MC2ILInstructionSet GetInstructionSet(InstructionSetId forId) => _registeredSets[forId];
}
