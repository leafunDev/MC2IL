using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Utils.AsmResolver;

internal static class IteratorReceiverInliner
{
    public static void Run(MethodAnalysisContext method, CilMethodBody body,
        Dictionary<Instruction, List<CilInstruction>> emitted)
    {
        if (method.DeclaringType is not { DeclaringType: { IsValueType: false } owner } stateMachine)
            return;

        var graph = method.ControlFlowGraph!;
        DominatorInfo? dominance = null;
        foreach (var block in graph.Blocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                if (instruction is not { OpCode: OpCode.Move, Operands:
                        [LocalVariable { Type: { } localType }, FieldReference { Parent: null, Local.IsThis: true,
                            Field: { Name: "<>4__this" or "$this", IsStatic: false } field }] }
                    || field.DeclaringType != stateMachine || field.FieldType != owner || localType != owner
                    || !emitted.TryGetValue(instruction, out var init)
                    || init is not [{ OpCode.Code: CilCode.Ldarg_0 },
                        { OpCode.Code: CilCode.Ldfld, Operand: IFieldDescriptor capture },
                        { OpCode.Code: CilCode.Stloc, Operand: CilLocalVariable slot } store])
                    continue;

                var instructions = body.Instructions;
                if (instructions.Any(il => il.OpCode.Code is CilCode.Stfld or CilCode.Ldflda
                        && il.Operand is IFieldDescriptor usedField && usedField.FullName == capture.FullName))
                    continue;

                var uses = instructions.Where(il => ReferenceEquals(il.Operand, slot) && !ReferenceEquals(il, store)).ToArray();
                if (uses.Length == 0 || uses.Any(il => il.OpCode.Code is not (CilCode.Ldloc or CilCode.Ldloc_S)))
                    continue;

                // Every load must already see the one copy read from the generated capture field.
                var remaining = uses.ToList();
                dominance ??= new DominatorInfo(graph);
                foreach (var useBlock in graph.Blocks)
                {
                    if (useBlock != block && !dominance.Dominates(block, useBlock))
                        continue;
                    var start = useBlock == block ? index + 1 : 0;
                    for (var i = start; i < useBlock.Instructions.Count && remaining.Count != 0; i++)
                        if (emitted.TryGetValue(useBlock.Instructions[i], out var il))
                            remaining.RemoveAll(load => il.Exists(candidate => ReferenceEquals(load, candidate)));
                }
                if (remaining.Count != 0)
                    continue;

                // ILSpy maps the generated capture field to the original instance during iterator recovery.
                for (var i = instructions.Count - 1; i >= 0; i--)
                {
                    var load = instructions[i];
                    if (!ReferenceEquals(load.Operand, slot) || ReferenceEquals(load, store))
                        continue;

                    load.OpCode = CilOpCodes.Ldarg_0;
                    load.Operand = null;
                    instructions.Insert(i + 1, new CilInstruction(CilOpCodes.Ldfld, capture));
                }
                foreach (var dead in init)
                {
                    dead.OpCode = CilOpCodes.Nop;
                    dead.Operand = null;
                }
            }
        }
    }
}
