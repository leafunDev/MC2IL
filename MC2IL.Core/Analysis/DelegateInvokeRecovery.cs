using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

// Rebuild managed delegate dispatch before SSA removal, while the native target and arguments
// still point to their exact reaching definitions.
public static class DelegateInvokeRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (app.InstructionSet.CallingConventionResolver is not { } callingConventions)
            return;

        var cfg = method.ControlFlowGraph!;
        var instructions = cfg.Instructions;
        if (!instructions.Any(i => i.OpCode is OpCode.IndirectCall or OpCode.IndirectJump))
            return;

        var pointerSize = app.Binary.PointerSizeBytes;
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var discardableLoads = new HashSet<Instruction>();
        foreach (var block in cfg.Blocks)
        for (var i = 0; i < block.Instructions.Count; i++)
        {
            var call = block.Instructions[i];
            if (call.OpCode is not (OpCode.IndirectCall or OpCode.IndirectJump)
                || !callingConventions.HasRawArgumentLayout(call, app))
                continue;

            var target = FollowCopies(call.Operands[0], definitions);
            var targetLoad = target is LocalVariable local && definitions.TryGetValue(local, out var definition)
                && definition.OpCode == OpCode.Move ? definition : null;
            if (targetLoad?.MemoryAccessSize is { } size && size != pointerSize)
                continue;

            var nativeEntry = targetLoad?.Operands[1] ?? target;
            var receiver = DelegateFieldOwner(nativeEntry, "invoke_impl", 3 * pointerSize, definitions);
            var fromMethodInfo = receiver == null;
            MethodAnalysisContext? invoke;
            if (fromMethodInfo)
            {
                // Generic shared code can dispatch through MethodInfo::virtualMethodPointer.
                // That slot follows methodPointer in metadata v29 and later.
                if (app.MetadataVersion < 29
                    || nativeEntry is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable
                        { Type: RuntimeMethodInfoAnalysisContext { RepresentedMethod: var represented } }, Addend: var entryOffset }
                    || entryOffset != pointerSize
                    || represented is not { DefaultName: "Invoke", IsStatic: false }
                    || callingConventions.ReturnsViaHiddenBuffer(represented))
                    continue;

                receiver = FollowCopies(call.Operands[2], definitions) as LocalVariable;
                invoke = FindInvoke(receiver?.Type);
                if (invoke == null || !MetadataResolver.IsSameType(invoke.DeclaringType, represented.DeclaringType))
                    continue;
            }
            else
            {
                invoke = FindInvoke(receiver?.Type);
            }

            if (invoke == null || receiver == null
                || callingConventions is Arm64CallingConventionResolver && invoke.Parameters.Any(p =>
                    p.ParameterType.IsValueType
                    && (TypeSizes.UnboxedSize(p.ParameterType, pointerSize) > pointerSize
                        || Arm64CallingConventionResolver.GetFloatAggregateFields(p.ParameterType) != null)))
                continue;

            var isTailCall = call.OpCode == OpCode.IndirectJump;
            if (isTailCall && (block.BlockType != BlockType.TailCall || i != block.Instructions.Count - 1
                || !method.IsVoid && !MetadataResolver.IsSameType(method.ReturnType, invoke.ReturnType)))
                continue;

            // An unresolved non-tail call currently only defines the integer result register.
            // A tail call can return the managed result directly in any return bank.
            var resultRegister = callingConventions.ReturnRegister(invoke);
            if (!isTailCall && !invoke.IsVoid
                && (callingConventions.ReturnsViaHiddenBuffer(invoke)
                    || call.Operands[1] is not LocalVariable resultLocal
                    || resultLocal.Register.Name != resultRegister.Name))
                continue;

            // Remap before substituting the delegate, since the raw layout check needs the
            // original receiver register. Keep the runtime MethodInfo out of the managed call.
            var mapped = new Instruction(call.Index, call.OpCode, call.Operands.ToList());
            callingConventions.RemapRawArguments(mapped, invoke);
            const int firstArgument = 2;
            var managedOperandCount = firstArgument + 1 + invoke.Parameters.Count;
            if (mapped.Operands.Count < managedOperandCount)
                continue;

            if (fromMethodInfo
                && (mapped.Operands.Count <= managedOperandCount
                    || MethodInfoOf(mapped.Operands[managedOperandCount], definitions) is not { } hiddenMethod
                    || hiddenMethod.DefaultName != invoke.DefaultName
                    || !MetadataResolver.IsSameType(hiddenMethod.DeclaringType, invoke.DeclaringType)))
                continue;

            var operands = new List<IOperand> { invoke };
            LocalVariable? result = null;
            if (!invoke.IsVoid && (!isTailCall || !method.IsVoid))
            {
                result = isTailCall
                    ? new LocalVariable("delegateTailCallResult", resultRegister, invoke.ReturnType)
                    : (LocalVariable)call.Operands[1];
                result.Type ??= invoke.ReturnType;
                operands.Add(result);
            }
            operands.Add(receiver);
            for (var argumentIndex = 0; argumentIndex < invoke.Parameters.Count; argumentIndex++)
            {
                var argument = mapped.Operands[firstArgument + 1 + argumentIndex];
                if (fromMethodInfo && app.InstructionSet is NewArmV8InstructionSet)
                    argument = RestoreGenericArgument(method, invoke.Parameters[argumentIndex].ParameterType, argument, definitions);
                operands.Add(argument);
            }

            call.OpCode = result == null ? OpCode.CallVoid : OpCode.Call;
            call.SetOperands(operands);
            call.NullCheckReceiver = true;
            CollectDispatchLoads(block, i, receiver, pointerSize, targetLoad, fromMethodInfo, definitions, discardableLoads);

            if (isTailCall)
            {
                block.AddInstruction(new Instruction(-1, OpCode.Return, result == null ? [] : [result]));
                block.CalculateBlockType();
            }
        }

        if (discardableLoads.Count != 0)
            DeadCodeEliminator.Run(cfg, discardableLoads);
    }

    private static MethodAnalysisContext? FindInvoke(TypeAnalysisContext? type)
    {
        var generic = type as GenericInstanceTypeAnalysisContext;
        var definition = generic?.GenericType ?? type;
        if (definition is not { IsDelegate: true }
            || definition.Methods.FirstOrDefault(m => m.DefaultName == "Invoke" && !m.IsStatic) is not { } invoke)
            return null;

        return generic == null ? invoke : new ConcreteGenericMethodAnalysisContext(invoke, generic.GenericArguments, []);
    }

    private static IOperand FollowCopies(IOperand operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        while (operand is LocalVariable local && definitions.TryGetValue(local, out var definition)
               && definition is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] })
            operand = source;
        return operand;
    }

    private static MethodAnalysisContext? MethodInfoOf(IOperand operand, Dictionary<LocalVariable, Instruction> definitions)
        => FollowCopies(operand, definitions) switch
        {
            RuntimeMethodInfoAnalysisContext info => info.RepresentedMethod,
            LocalVariable { Type: RuntimeMethodInfoAnalysisContext info } => info.RepresentedMethod,
            _ => null
        };

    private static IOperand RestoreGenericArgument(MethodAnalysisContext method, TypeAnalysisContext parameterType,
        IOperand operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        // ARM64 normalizes a forwarded bool with AND Wn, Wn, #1. A generic body emitted
        // from that specialization must still forward the original managed T parameter.
        if (parameterType is GenericParameterTypeAnalysisContext
            && FollowCopies(operand, definitions) is LocalVariable argument
            && definitions.TryGetValue(argument, out var definition)
            && definition is { OpCode: OpCode.And, OperandWidth: 32,
                Operands: [_, LocalVariable source, Immediate { Value: 1 }] }
            && source.Register.Name == argument.Register.Name
            && method.ParameterLocals.Contains(source)
            && MetadataResolver.IsSameType(parameterType, source.Type)
            && IsBooleanSpecialization(method, source))
            return source;

        return operand;
    }

    private static bool IsBooleanSpecialization(MethodAnalysisContext method, LocalVariable source)
    {
        if (!method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var implementations))
            return false;

        var firstParameter = method.IsStatic ? 0 : 1;
        var parameterIndex = method.ParameterOperands.FindIndex(firstParameter, operand => operand is Register register
            && register == source.Register) - firstParameter;
        if (parameterIndex < 0 || parameterIndex >= method.Parameters.Count)
            return false;

        var variants = implementations.OfType<ConcreteGenericMethodAnalysisContext>()
            .Where(candidate => ReferenceEquals(candidate.BaseMethodContext, method)).ToList();
        return variants.Count != 0 && variants.All(candidate => candidate.Parameters[parameterIndex].ParameterType
            == method.AppContext.SystemTypes.SystemBooleanType);
    }

    private static LocalVariable? DelegateFieldOwner(IOperand operand, string fieldName, int offset,
        Dictionary<LocalVariable, Instruction> definitions)
    {
        var owner = operand switch
        {
            FieldReference { Parent: null, Field.IsStatic: false,
                Field.DeclaringType.DefaultFullName: "System.Delegate" } field when field.Field.DefaultName == fieldName => field.Local,
            MemoryOperand { Base: LocalVariable local, Index: null, Scale: 0 } memory when memory.Addend == offset => local,
            _ => null
        };
        return owner == null ? null : (LocalVariable)FollowCopies(owner, definitions);
    }

    private static void CollectDispatchLoads(Block block, int callIndex, LocalVariable receiver, int pointerSize,
        Instruction? targetLoad, bool fromMethodInfo, Dictionary<LocalVariable, Instruction> definitions,
        HashSet<Instruction> discardableLoads)
    {
        // The invoke now supplies the null check. Remove only loads in the same straight-line
        // argument setup, so we don't move a null check past an unrelated effect.
        for (var i = callIndex - 1; i >= 0; i--)
        {
            var instruction = block.Instructions[i];
            if (instruction.OpCode == OpCode.Nop
                || instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, LocalVariable or Immediate or RuntimeMethodInfoAnalysisContext] })
                continue;

            if (fromMethodInfo ? instruction == targetLoad : IsDelegateDispatchLoad(instruction, receiver, pointerSize, definitions))
                discardableLoads.Add(instruction);
            else
                break;
        }
    }

    private static bool IsDelegateDispatchLoad(Instruction instruction, LocalVariable receiver, int pointerSize,
        Dictionary<LocalVariable, Instruction> definitions)
        => instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, var source] }
            && (instruction.MemoryAccessSize == null || instruction.MemoryAccessSize == pointerSize)
            && (ReferenceEquals(DelegateFieldOwner(source, "invoke_impl", 3 * pointerSize, definitions), receiver)
                || ReferenceEquals(DelegateFieldOwner(source, "method", 5 * pointerSize, definitions), receiver)
                || ReferenceEquals(DelegateFieldOwner(source, "method_code", 8 * pointerSize, definitions), receiver)
                || ReferenceEquals(DelegateFieldOwner(source, "invoke_impl_this", 8 * pointerSize, definitions), receiver));
}
