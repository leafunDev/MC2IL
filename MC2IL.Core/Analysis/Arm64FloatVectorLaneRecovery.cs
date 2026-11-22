using System;
using System.Collections.Generic;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

// Scalarize the ARM64 SIMD lanes emitted by NewArmV8InstructionSet and connect
// lane reads back to the managed aggregate or scalar value that supplied them.
internal static class Arm64FloatVectorLaneRecovery
{
    internal static bool Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return false;

        var changed = false;
        // Earlier type and metadata passes can remove blocks after the first dominator
        // computation. Recompute against this graph before matching live vector values.
        var dominance = new DominatorInfo(method.ControlFlowGraph!);
        foreach (var block in method.ControlFlowGraph!.Blocks)
        for (var index = 0; index < block.Instructions.Count; index++)
        {
            var instruction = block.Instructions[index];
            if (instruction.Operands.Count < 3
                || instruction.Operands[instruction.Operands.Count - 1] is not FloatingPointPrecision precision
                || instruction.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                    or OpCode.Negate))
                continue;

            var elementType = precision.IsDouble
                ? method.AppContext.SystemTypes.SystemDoubleType
                : method.AppContext.SystemTypes.SystemSingleType;

            var lastInputOperand = instruction.OpCode == OpCode.Negate ? 1 : 2;
            for (var operandIndex = 1; operandIndex <= lastInputOperand; operandIndex++)
            {
                if (instruction.Operands[operandIndex] is not LocalVariable lane
                    || !TryParseLane(lane.Register.Name, out var registerName, out var laneIndex))
                    continue;

                var laneDefinition = FindDefinition(block, index, lane);
                if (laneDefinition is { OpCode: not OpCode.Phi })
                {
                    if (lane.Type == null
                        && laneDefinition is { OpCode: OpCode.Move, Operands: [_, Immediate] })
                    {
                        lane.Type = elementType;
                        changed = true;
                    }
                    continue;
                }

                if (FindNearestRegisterValue(method, dominance, block, index, registerName) is not { } registerValue)
                    continue;

                if (registerValue.Type == null
                    && InferAggregateType(method, registerValue, laneIndex, elementType) is { } inferredType)
                {
                    changed |= LocalVariables.PropagateProducedType(registerValue, inferredType);
                }

                if (registerValue.Type is { FullName: "System.Single" or "System.Double" } scalarType
                    && scalarType.FullName == elementType.FullName && laneIndex == 0)
                {
                    instruction.SetOperand(operandIndex, registerValue);
                    changed = true;
                    continue;
                }

                if (registerValue.Type is not { } aggregateType
                    || Arm64CallingConventionResolver.GetFloatAggregateFields(aggregateType) is not { } fields
                    || laneIndex >= fields.Length
                    || fields[laneIndex].FieldType.FullName != elementType.FullName)
                    continue;

                instruction.SetOperand(operandIndex,
                    new FieldReference(fields[laneIndex], registerValue, fields[laneIndex].Offset));
                changed = true;
            }
        }

        return changed;
    }

    private static Instruction? FindDefinition(Block block, int before, LocalVariable local)
    {
        for (var i = before - 1; i >= 0; i--)
            if (ReferenceEquals(block.Instructions[i].Destination, local))
                return block.Instructions[i];
        return null;
    }

    private static LocalVariable? FindNearestRegisterValue(MethodAnalysisContext method, DominatorInfo dominance,
        Block useBlock, int before, string registerName)
    {
        LocalVariable? best = null;
        Block? bestBlock = null;
        var bestIndex = -1;
        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            if (block == useBlock)
            {
                for (var i = before - 1; i >= 0; i--)
                    if (block.Instructions[i].Destination is LocalVariable candidate
                        && candidate.Register.Name == registerName)
                        return candidate;
                continue;
            }

            if (!dominance.Dominates(block, useBlock))
                continue;

            for (var i = block.Instructions.Count - 1; i >= 0; i--)
            {
                if (block.Instructions[i].Destination is not LocalVariable candidate
                    || candidate.Register.Name != registerName)
                    continue;

                if (bestBlock == null || bestBlock == block || dominance.Dominates(bestBlock, block))
                {
                    best = candidate;
                    bestBlock = block;
                    bestIndex = i;
                }
                else if (bestBlock == block && i > bestIndex)
                {
                    best = candidate;
                    bestIndex = i;
                }
                break;
            }
        }

        return best;
    }

    private static TypeAnalysisContext? InferAggregateType(MethodAnalysisContext method, LocalVariable value,
        int laneIndex, TypeAnalysisContext elementType) =>
        InferAggregateType(method, value, laneIndex, elementType, new HashSet<LocalVariable>());

    private static TypeAnalysisContext? InferAggregateType(MethodAnalysisContext method, LocalVariable value,
        int laneIndex, TypeAnalysisContext elementType, HashSet<LocalVariable> path)
    {
        if (!path.Add(value))
            return null;

        try
        {
            var definition = FindDefinition(method.ControlFlowGraph!, value);
            if (definition == null)
                return null;

            if (definition is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] })
                return source.Type != null
                    ? AggregateTypeForLane(source.Type, laneIndex, elementType)
                    : InferAggregateType(method, source, laneIndex, elementType, path);

            if (definition is not { OpCode: OpCode.Phi, Operands.Count: > 1 })
                return null;

            TypeAnalysisContext? candidateType = null;
            for (var i = 1; i < definition.Operands.Count; i++)
            {
                var incoming = definition.Operands[i];
                if (incoming is not LocalVariable input)
                    return null;

                var inputType = input.Type != null
                    ? AggregateTypeForLane(input.Type, laneIndex, elementType)
                    : InferAggregateType(method, input, laneIndex, elementType, path);
                if (inputType == null)
                    continue;

                if (candidateType != null && !MetadataResolver.IsSameType(candidateType, inputType))
                    return null;
                candidateType = inputType;
            }

            return candidateType;
        }
        finally
        {
            path.Remove(value);
        }
    }

    private static Instruction? FindDefinition(ISILControlFlowGraph graph, LocalVariable value)
    {
        foreach (var instruction in graph.Instructions)
            if (ReferenceEquals(instruction.Destination, value))
                return instruction;
        return null;
    }

    private static TypeAnalysisContext? AggregateTypeForLane(TypeAnalysisContext type, int laneIndex,
        TypeAnalysisContext elementType)
    {
        if (Arm64CallingConventionResolver.GetFloatAggregateFields(type) is not { } fields
            || laneIndex >= fields.Length || fields[laneIndex].FieldType.FullName != elementType.FullName)
            return null;
        return type;
    }

    private static bool TryParseLane(string registerName, out string baseName, out int laneIndex)
    {
        baseName = string.Empty;
        laneIndex = 0;
        var separator = registerName.LastIndexOf('.');
        if (separator <= 0 || separator + 2 >= registerName.Length
            || registerName[separator + 1] is not ('S' or 'D')
            || !int.TryParse(registerName.Substring(separator + 2), out laneIndex))
            return false;

        baseName = registerName[..separator];
        return true;
    }
}
