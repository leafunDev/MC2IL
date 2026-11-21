using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

// Some ARM64 value types are passed and returned as a word in a general-purpose register.
// Model the bytes as fields at the managed boundary instead of casting a struct to an int.
internal static class PackedByteAggregateRecovery
{
    internal static bool RecoverParameters(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        if (method.Parameters.Count == 0 || !SupportsPackedWords(method) || graph.EntryBlock.Successors is not [var entry]
            || entry.Predecessors is not [var predecessor] || predecessor != graph.EntryBlock)
            return false;

        var offset = method.IsStatic ? 0 : 1;
        List<Instruction>? instructions = null;
        var initializers = new List<Instruction>();
        var types = method.AppContext.SystemTypes;
        for (var index = 0; index < method.Parameters.Count && index + offset < method.ParameterOperands.Count; index++)
        {
            if (method.ParameterOperands[index + offset] is not Register { Name: { } name } register || !name.StartsWith("X", System.StringComparison.Ordinal)
                || GetByteFields(method.Parameters[index].ParameterType) is not { } fields
                || method.ParameterLocals.FirstOrDefault(local => local.Register == register && !local.IsThis && !local.IsMethodInfo) is not { } parameter)
                continue;

            LocalVariable? word = null;
            foreach (var instruction in instructions ??= graph.Instructions)
            {
                for (var operandIndex = 1; operandIndex < instruction.Operands.Count; operandIndex++)
                {
                    if (!ReferenceEquals(instruction.Operands[operandIndex], parameter)
                        || !UsesPackedWord(instruction, operandIndex, types))
                        continue;
                    word ??= MakePackedWord();
                    instruction.SetOperand(operandIndex, word);
                }
                if (instruction is { OpCode: OpCode.Return, Operands: [var value] }
                    && ReferenceEquals(value, parameter) && IsWordType(method.ReturnType))
                {
                    word ??= MakePackedWord();
                    instruction.SetOperand(0, word);
                }
            }

            LocalVariable MakePackedWord()
            {
                var result = CreateLocal(method, $"packedArgument{index}", types.SystemUInt32Type);
                IOperand accumulated = new FieldReference(fields[0], parameter, fields[0].Offset);
                for (var lane = 1; lane < fields.Length; lane++)
                {
                    var shifted = CreateLocal(method, $"packedLane{index}_{lane}", types.SystemUInt32Type);
                    var field = new FieldReference(fields[lane], parameter, fields[lane].Offset);
                    initializers.Add(new Instruction(-1, OpCode.ShiftLeft, [shifted, field, new Immediate(lane * 8)]));
                    var merged = lane == fields.Length - 1 ? result
                        : CreateLocal(method, $"packedBytes{index}_{lane}", types.SystemUInt32Type);
                    initializers.Add(new Instruction(-1, OpCode.Or, [merged, accumulated, shifted]));
                    accumulated = merged;
                }
                return result;
            }
        }
        entry.Instructions.InsertRange(0, initializers);
        return initializers.Count != 0;
    }

    internal static void RecoverReturns(MethodAnalysisContext method)
    {
        if (!SupportsPackedWords(method) || GetByteFields(method.ReturnType) is not { } fields)
            return;

        var graph = method.ControlFlowGraph!;
        var definitions = graph.Instructions.Where(i => i.Destination is LocalVariable).ToLookup(i => (LocalVariable)i.Destination!);
        var uses = graph.Instructions.SelectMany(DeadCodeEliminator.UsedLocals).ToLookup(local => local);
        foreach (var block in graph.Blocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                if (block.Instructions[index] is not { OpCode: OpCode.Return, Operands: [LocalVariable bits] } ret)
                    continue;

                // A return hint can have typed the producer as the struct, even when its
                // actual instruction is a scalar bitwise operation or an FMOV of a float.
                if (bits.Type == method.ReturnType && uses[bits].Count() == 1
                    && definitions[bits].Count() == 1 && IsWordProducer(definitions[bits].Single()))
                    bits.Type = method.AppContext.SystemTypes.SystemInt32Type;
                if (!IsWordType(bits.Type))
                    continue;

                var value = CreateLocal(method, "unpackedReturn", method.ReturnType);
                var materialize = new List<Instruction> {
                    new(-1, OpCode.Move, [value, new DefaultValue(method.ReturnType)])
                };
                for (var lane = 0; lane < fields.Length; lane++)
                {
                    IOperand source = bits;
                    if (lane != 0)
                    {
                        var shifted = CreateLocal(method, $"returnByte{lane}", method.AppContext.SystemTypes.SystemUInt32Type);
                        materialize.Add(new Instruction(-1, OpCode.ShiftRightUnsigned, [shifted, bits, new Immediate(lane * 8)]));
                        source = shifted;
                    }
                    materialize.Add(new Instruction(-1, OpCode.Move, [new FieldReference(fields[lane], value, fields[lane].Offset), source]));
                }
                block.Instructions.InsertRange(index, materialize);
                index += materialize.Count;
                ret.SetOperand(0, value);
            }
        }
    }

    private static bool UsesPackedWord(Instruction instruction, int index, SystemTypesContext types) =>
        instruction.OpCode is OpCode.And or OpCode.Or or OpCode.Xor or OpCode.Not
        || index == 1 && (instruction.OpCode is OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned
            || instruction is { OpCode: OpCode.Convert, Operands: [_, _, NumericConversion conversion] }
                && IsWordType(NumericConversionTypes.Get(conversion.Kind, types).Source));

    private static bool IsWordProducer(Instruction instruction) =>
        instruction is { OpCode: OpCode.Convert, Operands: [_, _, NumericConversion { Kind: NumericConversionKind.Float32ReinterpretI32 }] }
        || instruction is { OpCode: OpCode.Or or OpCode.And or OpCode.Xor, Operands: [_, var left, var right] }
            && IsWordValue(left) && IsWordValue(right);

    private static bool IsWordValue(IOperand operand) =>
        operand is Immediate || operand is LocalVariable local && IsWordType(local.Type);

    private static bool IsWordType(TypeAnalysisContext? type) => type?.FullName is
        "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32";

    private static bool SupportsPackedWords(MethodAnalysisContext method) =>
        method.AppContext.InstructionSet is NewArmV8InstructionSet && method.AppContext.Binary.IsLittleEndian;

    private static FieldAnalysisContext[]? GetByteFields(TypeAnalysisContext type)
    {
        if (!type.IsValueType || type.IsEnumType || type.Definition == null
            || (type.Attributes & TypeAttributes.LayoutMask) is not (TypeAttributes.SequentialLayout or TypeAttributes.ExplicitLayout)
            || TypeSizes.UnboxedSize(type, type.AppContext.Binary.PointerSizeBytes) != 4)
            return null;

        var fields = new FieldAnalysisContext[4];
        foreach (var field in type.Fields.Where(field => !field.IsStatic))
        {
            // Explicit layouts can expose an overlapping int alias in addition to the bytes.
            if ((type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout
                && field.Offset == 0 && field.FieldType.FullName is "System.Int32" or "System.UInt32")
                continue;
            if (field.Offset is < 0 or > 3 || field.FieldType.FullName != "System.Byte"
                || (field.Attributes & (FieldAttributes.FieldAccessMask | FieldAttributes.InitOnly)) != FieldAttributes.Public
                || fields[field.Offset] != null)
                return null;
            fields[field.Offset] = field;
        }
        return fields.All(field => field != null) ? fields : null;
    }

    private static LocalVariable CreateLocal(MethodAnalysisContext method, string name, TypeAnalysisContext type)
    {
        var local = new LocalVariable(name, new Register(null, $"TEMP_PACKED_{method.Locals.Count}"), type);
        method.Locals.Add(local);
        return local;
    }
}
