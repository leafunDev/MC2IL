using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

// This is a readability option: a second getter call can observe a different instance.
internal static class SingletonInstanceReuse
{
    private readonly record struct Read(Block Block, int Index, Instruction Call,
        LocalVariable Result, TypeAnalysisContext Owner);

    internal static void Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        Dictionary<MethodAnalysisContext, List<Read>>? reads = null;
        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];
                if (instruction is not { OpCode: OpCode.Call, Operands:
                    [ConcreteGenericMethodAnalysisContext
                    {
                        IsStatic: true, IsVoid: false,
                        BaseMethodContext: { DefaultName: "get_Instance", Parameters.Count: 0,
                            DeclaringType.DefaultName: "MonoSingleton`1" } getter,
                        DeclaringType: GenericInstanceTypeAnalysisContext owner,
                        MethodGenericParameters.Count: 0,
                        ReturnType.IsValueType: false
                    }, LocalVariable result, ..] }
                    || !getter.DeclaringType!.Properties.Any(property => property.Getter == getter))
                    continue;

                reads ??= new();
                if (!reads.TryGetValue(getter, out var candidates))
                    reads[getter] = candidates = [];
                candidates.Add(new Read(block, i, instruction, result, owner));
            }
        }

        if (reads == null || !reads.Values.Any(group => group.Count > 1))
            return;

        // Do not replace a value whose storage location can be read or changed by address.
        var addressed = new HashSet<LocalVariable>();
        foreach (var block in graph.Blocks)
            foreach (var instruction in block.Instructions)
                foreach (var operand in instruction.Operands)
                    if (operand is AddressOf { Target: LocalVariable local })
                        addressed.Add(local);

        // The original SSA dominators predate earlier CFG changes. Use the current graph.
        DominatorInfo? dominance = null;
        foreach (var candidates in reads.Values)
        {
            if (candidates.Count < 2)
                continue;

            foreach (var current in candidates)
            {
                if (addressed.Contains(current.Result))
                    continue;

                foreach (var earlier in candidates)
                {
                    if (earlier.Result == current.Result || addressed.Contains(earlier.Result)
                        || !MetadataResolver.IsSameType(earlier.Owner, current.Owner)
                        || !MetadataResolver.IsSameType(earlier.Result.Type, current.Result.Type))
                        continue;

                    var available = earlier.Block == current.Block
                        ? earlier.Index < current.Index
                        : (dominance ??= new DominatorInfo(graph)).Dominates(earlier.Block, current.Block);
                    if (!available)
                        continue;

                    current.Call.OpCode = OpCode.Move;
                    current.Call.SetOperands(current.Result, earlier.Result);
                    current.Call.OperandWidth = null;
                    current.Call.ImplicitDefinition = null;
                    current.Block.CalculateBlockType();
                    break;
                }
            }
        }
    }
}
