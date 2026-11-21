using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

/// <summary>
/// Recovers object fields addressed through an ARM32 conditional offset, such as
/// <c>this + (condition ? fieldAOffset : fieldBOffset)</c> followed by a scalar load.
/// </summary>
internal static class ArmV7DynamicFieldAccessRecovery
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not ArmV7InstructionSet || method.ControlFlowGraph is not { } graph)
            return false;

        var instructions = graph.Instructions;
        var recovered = false;
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        foreach (var load in instructions)
        {
            if (load is not { OpCode: OpCode.Move, Operands: [_,
                    MemoryOperand { Base: LocalVariable address, Index: null, Scale: 0, Addend: 0 }] }
                || !definitions.TryGetValue(address, out var addressDefinition)
                || addressDefinition is not { OpCode: OpCode.Add, Operands: [LocalVariable addressResult, var left, var right] }
                || !ReferenceEquals(addressResult, address)
                || !TryGetObjectAndOffset(left, right, definitions, out var receiver, out var offsetPhi)
                || offsetPhi is not { OpCode: OpCode.Phi, Destination: LocalVariable offsetResult }
                || offsetPhi.Operands.Count < 3
                || !ReferenceEquals(SingleUse(instructions, address), load)
                || !ReferenceEquals(SingleUse(instructions, offsetResult), addressDefinition))
                continue;

            var owner = receiver.Type;
            if (owner == null || owner.IsValueType || owner is GenericInstanceTypeAnalysisContext
                || owner.GenericParameters.Count != 0)
                continue;

            var fields = new List<FieldAnalysisContext>(offsetPhi.Operands.Count - 1);
            var valid = true;
            for (var i = 1; i < offsetPhi.Operands.Count; i++)
            {
                if (!TryGetIntegerConstant(offsetPhi.Operands[i], definitions, new HashSet<LocalVariable>(), out var offset)
                    || FindInstanceField(owner, offset) is not { } field)
                {
                    valid = false;
                    break;
                }

                fields.Add(field);
            }

            if (!valid || fields.Count == 0
                || fields.Any(field => !MetadataResolver.IsSameType(field.FieldType, fields[0].FieldType)))
                continue;

            var fieldType = fields[0].FieldType;
            var expectedSize = fieldType.IsValueType
                ? TypeSizes.UnboxedSize(fieldType, method.AppContext.Binary.PointerSizeBytes)
                : method.AppContext.Binary.PointerSizeBytes;
            if (load.MemoryAccessSize is { } accessSize && expectedSize > 0 && accessSize != expectedSize)
                continue;

            offsetResult.Type = fieldType;
            for (var i = 0; i < fields.Count; i++)
                offsetPhi.SetOperand(i + 1, new FieldReference(fields[i], receiver, fields[i].Offset));

            load.SetOperand(1, offsetResult);
            addressDefinition.OpCode = OpCode.Nop;
            addressDefinition.SetOperands();
            recovered = true;
        }

        return recovered;
    }

    private static bool TryGetObjectAndOffset(IOperand left, IOperand right,
        Dictionary<LocalVariable, Instruction> definitions, out LocalVariable receiver,
        out Instruction offsetPhi)
    {
        if (left is LocalVariable leftLocal && right is LocalVariable rightLocal
            && leftLocal.Type is { IsValueType: false } && definitions.TryGetValue(rightLocal, out offsetPhi!)
            && offsetPhi.OpCode == OpCode.Phi)
        {
            receiver = leftLocal;
            return true;
        }

        if (right is LocalVariable rightReceiver && left is LocalVariable leftOffset
            && rightReceiver.Type is { IsValueType: false } && definitions.TryGetValue(leftOffset, out offsetPhi!)
            && offsetPhi.OpCode == OpCode.Phi)
        {
            receiver = rightReceiver;
            return true;
        }

        receiver = null!;
        offsetPhi = null!;
        return false;
    }

    private static bool TryGetIntegerConstant(IOperand operand, Dictionary<LocalVariable, Instruction> definitions,
        HashSet<LocalVariable> visited, out long value)
    {
        if (operand is Immediate immediate)
        {
            value = immediate.Value;
            return true;
        }

        if (operand is LocalVariable local && visited.Add(local)
            && definitions.TryGetValue(local, out var definition))
        {
            if (definition is { OpCode: OpCode.Move, Operands: [_, Immediate constant] })
            {
                value = constant.Value;
                return true;
            }

            if (definition is { OpCode: OpCode.Move, Operands: [_, LocalVariable copy] })
                return TryGetIntegerConstant(copy, definitions, visited, out value);
        }

        value = 0;
        return false;
    }

    private static FieldAnalysisContext? FindInstanceField(TypeAnalysisContext owner, long offset)
    {
        for (var current = owner; current != null; current = current.BaseType)
        {
            var match = current.Fields.FirstOrDefault(field => !field.IsStatic
                && (field.Attributes & System.Reflection.FieldAttributes.Literal) == 0
                && field.Offset == offset);
            if (match != null)
                return match;
        }

        return null;
    }

    private static Instruction? SingleUse(IEnumerable<Instruction> instructions, LocalVariable local)
    {
        Instruction? use = null;
        foreach (var instruction in instructions)
        {
            foreach (var source in instruction.Sources)
            {
                if (!Contains(source, local))
                    continue;

                if (use != null)
                    return null;
                use = instruction;
            }
        }

        return use;
    }

    private static bool Contains(IOperand operand, LocalVariable local) => operand switch
    {
        LocalVariable candidate => ReferenceEquals(candidate, local),
        MemoryOperand memory => memory.Base is { } baseOperand && Contains(baseOperand, local)
            || memory.Index is { } indexOperand && Contains(indexOperand, local),
        AddressOf { Target: var target } => Contains(target, local),
        FieldReference field => ReferenceEquals(field.Local, local)
            || field.Parent != null && Contains(field.Parent, local),
        _ => false,
    };
}
