using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

//Flip op_Equality to op_Inequality (and vice versa) to make ILSpy decompilation suck less (double negation)
public static class EqualityBranchInverter
{
    public static void Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        foreach (var instruction in cfg.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Call, Operands: [MethodAnalysisContext conversion, var result, var value, ..] }
                || !IsUnityObjectBooleanConversion(conversion)
                || ReferenceEquals(method.DeclaringType, conversion.DeclaringType))
                continue;

            var inequality = conversion.DeclaringType!.Methods.FirstOrDefault(candidate =>
                candidate is { Name: "op_Inequality", IsStatic: true, Parameters.Count: 2 }
                && ReferenceEquals(candidate.ReturnType, conversion.ReturnType)
                && candidate.Parameters.All(parameter => ReferenceEquals(parameter.ParameterType, conversion.DeclaringType)));
            if (inequality != null)
                instruction.SetOperands(inequality, result, value, new Immediate(0));
        }

        Run(cfg);
    }

    internal static bool IsUnityObjectBooleanConversion(MethodAnalysisContext method) =>
        method is { Name: "op_Implicit", IsStatic: true, Parameters.Count: 1,
            DeclaringType.FullName: "UnityEngine.Object", ReturnType.FullName: "System.Boolean" }
        && method.DeclaringType.DeclaringAssembly.CleanAssemblyName is "UnityEngine.CoreModule" or "UnityEngine"
        && ReferenceEquals(method.Parameters[0].ParameterType, method.DeclaringType);

    public static void Run(ISILControlFlowGraph cfg)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        var appearances = new Dictionary<LocalVariable, int>();

        foreach (var instruction in cfg.Instructions)
        {
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

            foreach (var local in LocalsReferencedBy(instruction))
                appearances[local] = (appearances.TryGetValue(local, out var count) ? count : 0) + 1;
        }

        foreach (var block in cfg.Blocks)
        {
            if (block.Instructions.Count == 0)
                continue;

            var jump = block.Instructions[^1];

            if (jump.OpCode != OpCode.ConditionalJump
                || jump.Operands[0] is not Block taken
                || jump.Operands[1] is not LocalVariable condition)
                continue;

            if (block.Successors.FirstOrDefault(successor => successor != taken) is not { } fallenThrough
                || taken == cfg.ExitBlock || fallenThrough == cfg.ExitBlock)
                continue;

            if (!ShouldFlip(taken, fallenThrough, block))
                continue;

            // must be 2 appearances (definition, and this branch we're looking at), so we don't change anything which has side effects
            if (Appearances(appearances, condition) != 2
                || !definitions.TryGetValue(condition, out var definition)
                || !definition.IsCall
                || definition.Operands[0] is not MethodAnalysisContext called
                || InverseOperator(called) is not { } inverse)
                continue;

            definition.SetOperand(0, inverse);
            jump.SetOperand(0, fallenThrough);
        }
    }

    // ILSpy *seems* to put the main body of an if in the taken branch and the early-out in the else. Match that logic for whether we invert
    private static bool ShouldFlip(Block taken, Block fallenThrough, Block branch) =>
        Size(UniquelyReachable(taken, fallenThrough, branch)) < Size(UniquelyReachable(fallenThrough, taken, branch));

    private static int Size(HashSet<Block> region) => region.Sum(block => block.Instructions.Count);

    // The blocks only this branch reaches, i.e. everything up to the continuation after the statement
    private static HashSet<Block> UniquelyReachable(Block from, Block other, Block branch)
    {
        var mine = Reachable(from, branch);
        mine.ExceptWith(Reachable(other, branch));

        return mine;
    }

    private static HashSet<Block> Reachable(Block start, Block branch)
    {
        var visited = new HashSet<Block> { branch };
        var queue = new Queue<Block>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var block = queue.Dequeue();

            if (!visited.Add(block))
                continue;

            foreach (var successor in block.Successors)
                queue.Enqueue(successor);
        }

        visited.Remove(branch);
        return visited;
    }

    private static int Appearances(Dictionary<LocalVariable, int> appearances, LocalVariable local) =>
        appearances.TryGetValue(local, out var count) ? count : 0;

    private static MethodAnalysisContext? InverseOperator(MethodAnalysisContext method)
    {
        var inverseName = method.Name switch
        {
            "op_Equality" => "op_Inequality",
            "op_Inequality" => "op_Equality",
            _ => null
        };

        if (inverseName == null || method.DeclaringType is not { } declaringType)
            return null;

        return declaringType.Methods.FirstOrDefault(candidate => candidate.Name == inverseName
            && candidate.IsStatic == method.IsStatic
            && ReferenceEquals(candidate.ReturnType, method.ReturnType)
            && candidate.Parameters.Count == method.Parameters.Count
            && SameParameterTypes(candidate, method));
    }

    private static bool SameParameterTypes(MethodAnalysisContext a, MethodAnalysisContext b)
    {
        for (var i = 0; i < a.Parameters.Count; i++)
        {
            if (!ReferenceEquals(a.Parameters[i].ParameterType, b.Parameters[i].ParameterType))
                return false;
        }

        return true;
    }

    private static IEnumerable<LocalVariable> LocalsReferencedBy(Instruction instruction)
    {
        foreach (var operand in instruction.Operands)
        {
            switch (operand)
            {
                case LocalVariable local:
                    yield return local;
                    break;
                case MemoryOperand memory:
                    if (memory.Base is LocalVariable memoryBase)
                        yield return memoryBase;
                    if (memory.Index is LocalVariable memoryIndex)
                        yield return memoryIndex;
                    break;
                case FieldReference field:
                    yield return field.Local;
                    break;
                case ArrayAccess array:
                    yield return array.Array;
                    if (array.Index is LocalVariable arrayIndex)
                        yield return arrayIndex;
                    break;
                case ArrayLength length:
                    yield return length.Array;
                    break;
                case AddressOf { Target: LocalVariable addressed }:
                    yield return addressed;
                    break;
            }
        }
    }
}
