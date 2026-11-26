using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class Arm64FloatFieldAddressRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var instructions = method.ControlFlowGraph!.Instructions;
        if (!instructions.Any(instruction => instruction.OpCode == OpCode.Phi)
            || !instructions.Any(instruction => instruction is { OpCode: OpCode.Move, MemoryAccessSize: 4,
                Operands: [LocalVariable { Type.FullName: "System.Single" }, MemoryOperand
                    { Base: LocalVariable { Type: not ByRefTypeAnalysisContext }, Index: null, Scale: 0, Addend: 0 }] }))
            return;

        var definitions = instructions.Where(instruction => instruction.Destination is LocalVariable)
            .ToDictionary(instruction => (LocalVariable)instruction.Destination!);
        var uses = new Dictionary<LocalVariable, List<Instruction>>();
        foreach (var instruction in instructions)
        foreach (var local in DeadCodeEliminator.UsedLocals(instruction))
        {
            if (!uses.TryGetValue(local, out var readers))
                uses.Add(local, readers = []);
            readers.Add(instruction);
        }

        var single = method.AppContext.SystemTypes.SystemSingleType;
        var reference = new ByRefTypeAnalysisContext(single);
        foreach (var instruction in instructions)
        {
            if (instruction is not { OpCode: OpCode.Move, MemoryAccessSize: 4,
                    Operands: [LocalVariable result, MemoryOperand
                        { Base: LocalVariable selected, Index: null, Scale: 0, Addend: 0 }] }
                || result.Type?.FullName != "System.Single" || selected.Type is ByRefTypeAnalysisContext
                || !definitions.TryGetValue(selected, out var selection) || selection.OpCode != OpCode.Phi)
                continue;

            var replacements = new Dictionary<LocalVariable, (Instruction Definition, List<IOperand> Inputs)>();
            if (Plan(selected, []) == null)
                continue;

            var planned = new HashSet<Instruction>(replacements.Values.Select(value => value.Definition));
            if (replacements.Keys.Any(local => uses.TryGetValue(local, out var readers)
                    && readers.Any(use => !planned.Contains(use) && !IsScalarRead(use, local))))
                continue;

            foreach (var replacement in replacements)
            {
                var local = replacement.Key;
                var (definition, inputs) = replacement.Value;
                local.Type = reference;
                definition.OpCode = definition.OpCode == OpCode.Phi ? OpCode.Phi : OpCode.Move;
                definition.SetOperands([local, .. inputs]);
                definition.OperandWidth = null;
            }

            IOperand? Plan(IOperand value, HashSet<LocalVariable> visited)
            {
                if (value is StaticFieldStorageTypeAnalysisContext storageType)
                    return ResolveAddress(new LocalVariable("staticFields", new Register(null, "TEMP_STATIC_FIELD_ADDRESS"), storageType), 0);

                if (value is not LocalVariable local || !visited.Add(local))
                    return null;
                if (local.Type is StaticFieldStorageTypeAnalysisContext)
                    return ResolveAddress(local, 0);
                if (replacements.ContainsKey(local))
                    return local;
                if (!definitions.TryGetValue(local, out var definition))
                    return null;

                if (definition is { OpCode: OpCode.Add, OperandWidth: not 32,
                        Operands: [_, var owner, Immediate offset] })
                {
                    var addressBase = owner as LocalVariable;
                    if (owner is StaticFieldStorageTypeAnalysisContext staticType)
                        addressBase = new LocalVariable("staticFields", new Register(null, "TEMP_STATIC_FIELD_ADDRESS"), staticType);
                    if (addressBase == null || !addressBase.IsThis && addressBase.Type is not StaticFieldStorageTypeAnalysisContext
                        || ResolveAddress(addressBase, offset.Value) is not { } fieldAddress)
                        return null;
                    replacements[local] = (definition, [fieldAddress]);
                    return local;
                }

                if (definition.OpCode is not (OpCode.Move or OpCode.Phi) || definition.OperandWidth == 32)
                    return null;

                var inputs = new List<IOperand>();
                foreach (var input in definition.Operands.Skip(1))
                {
                    if (Plan(input, [.. visited]) is not { } plannedInput)
                        return null;
                    inputs.Add(plannedInput);
                }
                replacements[local] = (definition, inputs);
                return local;
            }
        }

        AddressOf? ResolveAddress(LocalVariable receiver, long offset)
        {
            var load = new Instruction(-1, OpCode.Move, new Register(null, "TEMP_FIELD_ADDRESS"),
                new MemoryOperand(receiver, addend: offset)) { MemoryAccessSize = 4 };
            return MetadataResolver.ResolveFieldOffsets(method, [load])
                && load.Operands[1] is FieldReference { Field.FieldType.FullName: "System.Single" } field
                ? new AddressOf(field) : null;
        }

        static bool IsScalarRead(Instruction instruction, LocalVariable address) =>
            instruction is { OpCode: OpCode.Move, MemoryAccessSize: 4,
                Operands: [LocalVariable { Type.FullName: "System.Single" }, MemoryOperand
                    { Base: LocalVariable source, Index: null, Scale: 0, Addend: 0 }] } && source == address;
    }
}
