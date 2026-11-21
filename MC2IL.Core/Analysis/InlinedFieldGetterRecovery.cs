using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

internal static class InlinedFieldGetterRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet instructionSet
            || method.DeclaringType == null)
            return;

        Dictionary<FieldAnalysisContext, MethodAnalysisContext?>? getters = null;
        foreach (var load in method.ControlFlowGraph!.Instructions)
        {
            if (load is not { OpCode: OpCode.Move, MemoryAccessSize: { } size, OperandWidth: null or 32,
                    Operands: [LocalVariable result, FieldReference { Parent: null, Field: var field } reference] }
                || field.Visibility != FieldAttributes.Private || field.IsStatic
                || field.DeclaringType == method.DeclaringType || field.DeclaringType.IsValueType
                || field.DeclaringType.GenericParameters.Count != 0
                || field.BackingData?.FieldOffset != reference.Offset
                || TypeSizes.UnboxedSize(field.FieldType, 8) != size
                || !MetadataResolver.IsSameType(result.Type, field.FieldType))
                continue;

            getters ??= [];
            if (!getters.TryGetValue(field, out var getter))
                getters.Add(field, getter = FindDirectGetter(field, size, instructionSet));
            if (getter == null)
                continue;

            load.OpCode = OpCode.Call;
            load.SetOperands(getter, result, reference.Local);
            load.NullCheckReceiver = true;
            load.MemoryAccessSize = null;
            load.OperandWidth = null;
        }
    }

    private static MethodAnalysisContext? FindDirectGetter(FieldAnalysisContext field, int size,
        NewArmV8InstructionSet instructionSet)
    {
        MethodAnalysisContext? match = null;
        foreach (var property in field.DeclaringType.Properties)
        {
            if (property.Getter is not
                { IsStatic: false, Parameters.Count: 0, Visibility: MethodAttributes.Public } getter
                || getter.IsVirtual && !getter.IsFinal
                || !MetadataResolver.IsSameType(getter.ReturnType, field.FieldType)
                || getter.UnderlyingPointer == 0 || getter.RawBytes.Length is 0 or > 32)
                continue;

            // Verify the getter independently, without recursing into full method analysis.
            // A matching name or return type does not prove that it performs only this load.
            if (instructionSet.GetIsilFromMethod(getter).Where(value => value.OpCode != OpCode.Nop).ToArray() is not
                [
                    { OpCode: OpCode.Move, MemoryAccessSize: var width, Operands: [Register destination,
                        MemoryOperand { Base: Register { Name: "X0" }, Index: null, Scale: 0, Addend: var offset }] },
                    { OpCode: OpCode.Return, Operands: [Register returned] }
                ] || width != size || offset != field.Offset || destination != returned)
                continue;

            if (match != null)
                return null;
            match = getter;
        }

        return match;
    }
}
