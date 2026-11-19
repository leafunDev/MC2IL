using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

// Prefer direct equality operators in reconstructed branches.
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
        method is { Name: "op_Implicit", IsStatic: true, Parameters.Count: 1, ReturnType.FullName: "System.Boolean" }
        && IsUnityObjectType(method.DeclaringType)
        && ReferenceEquals(method.Parameters[0].ParameterType, method.DeclaringType);

    private static bool IsUnityObjectType(TypeAnalysisContext? type) =>
        type is { FullName: "UnityEngine.Object", DeclaringAssembly.CleanAssemblyName: "UnityEngine.CoreModule" or "UnityEngine" };

    public static void Run(ISILControlFlowGraph cfg)
    {
        var definitions = new Dictionary<LocalVariable, (Block Block, int Index, Instruction Instruction)>();
        var appearances = new Dictionary<LocalVariable, int>();

        foreach (var block in cfg.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];
                if (instruction.Destination is LocalVariable destination)
                    definitions[destination] = (block, i, instruction);

                foreach (var local in LocalsReferencedBy(instruction))
                    appearances[local] = (appearances.TryGetValue(local, out var count) ? count : 0) + 1;
            }
        }

        foreach (var block in cfg.Blocks)
        {
            if (block.Instructions.Count == 0)
                continue;

            var jump = block.Instructions[^1];
            if (jump is not { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable condition] }
                || block.Successors.FirstOrDefault(successor => successor != taken) is not { } fallenThrough
                || taken == cfg.ExitBlock || fallenThrough == cfg.ExitBlock)
                continue;

            var consumerBlock = block;
            var consumerIndex = block.Instructions.Count - 1;
            var negate = false;

            // Native flag recovery can leave several boolean comparisons between the operator
            // and the jump. Follow each value only if it has one definition and one use.
            while (condition.Type?.FullName == "System.Boolean"
                && Appearances(appearances, condition) == 2
                && definitions.TryGetValue(condition, out var definition)
                && PrecedesOnLinearPath(definition.Block, definition.Index, consumerBlock, consumerIndex))
            {
                if (TryUnwrapBooleanTest(definition.Instruction, out var input, out var invert))
                {
                    negate ^= invert;
                    condition = input;
                    consumerBlock = definition.Block;
                    consumerIndex = definition.Index;
                    continue;
                }

                if (definition.Instruction is not { OpCode: OpCode.Call, Operands: [MethodAnalysisContext called, ..] }
                    || InverseOperator(called) is not { } inverse)
                    break;

                var flipBranches = ShouldFlip(taken, fallenThrough, block);
                if (negate != flipBranches)
                    definition.Instruction.SetOperand(0, inverse);
                jump.SetOperands(flipBranches ? fallenThrough : taken, condition);
                break;
            }
        }
    }

    private static bool TryUnwrapBooleanTest(Instruction instruction, out LocalVariable input, out bool invert)
    {
        if (instruction is { OpCode: OpCode.Not, Operands: [_, LocalVariable { Type.FullName: "System.Boolean" } operand] })
        {
            input = operand;
            invert = true;
            return true;
        }

        input = null!;
        invert = false;
        if (instruction is not { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, Operands: [_, var left, var right] })
            return false;

        if (left is Immediate)
            (left, right) = (right, left);
        if (left is not LocalVariable { Type.FullName: "System.Boolean" } value || right is not Immediate { Value: 0 or 1 } literal)
            return false;

        input = value;
        invert = (instruction.OpCode == OpCode.CheckEqual) == (literal.Value == 0);
        return true;
    }

    private static bool PrecedesOnLinearPath(Block producer, int producerIndex, Block consumer, int consumerIndex)
    {
        if (producer == consumer)
            return producerIndex < consumerIndex;

        var visited = new HashSet<Block> { consumer };
        while (consumer.Predecessors is [var predecessor]
            && predecessor.Successors is [var successor] && successor == consumer
            && visited.Add(predecessor))
        {
            if (predecessor == producer)
                return true;
            consumer = predecessor;
        }

        return false;
    }

    private static bool ShouldFlip(Block taken, Block fallenThrough, Block branch)
    {
        // A return reached after the main body can also be the target of a guard clause.
        // When several conditions alone target an exit, prefer the continuing arm so
        // short-circuit tests can be printed together without negating their operators.
        var takenExits = IsGuardExit(taken);
        var fallthroughExits = IsGuardExit(fallenThrough);
        if (takenExits != fallthroughExits)
            return fallthroughExits;

        return Size(UniquelyReachable(taken, fallenThrough, branch)) < Size(UniquelyReachable(fallenThrough, taken, branch));
    }

    private static bool IsGuardExit(Block block) =>
        block.Instructions.Count != 0
        && block.Instructions[^1].OpCode is OpCode.Return or OpCode.Throw
        && block.Instructions.Take(block.Instructions.Count - 1).All(instruction => instruction.OpCode == OpCode.Nop)
        && (block.Predecessors.Count == 1 || block.Predecessors.Any(predecessor => predecessor.Successors.Count == 1));

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

        if (inverseName == null || method is not { IsStatic: true, ReturnType.FullName: "System.Boolean", Parameters.Count: 2 }
            || method.DeclaringType is not { } declaringType || !IsUnityObjectType(declaringType)
            || method.Parameters.Any(parameter => !ReferenceEquals(parameter.ParameterType, declaringType)))
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
