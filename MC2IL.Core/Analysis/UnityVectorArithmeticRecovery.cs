using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class UnityVectorArithmeticRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var graph = method.ControlFlowGraph!;
        var instructions = graph.Instructions;
        if (!instructions.Any(i => i.OpCode == OpCode.PackFloatAggregate && IsVector(i.Operands[1] as TypeAnalysisContext)))
            return;

        var definitions = new Dictionary<LocalVariable, Instruction?>();
        var writes = new Dictionary<LocalVariable, List<Instruction>>();
        var reads = instructions.SelectMany(DeadCodeEliminator.UsedLocals).ToLookup(local => local);
        var addressed = new HashSet<LocalVariable>();
        var homes = new Dictionary<Instruction, Block>();
        DominatorInfo? dominance = null;
        foreach (var block in graph.Blocks)
        foreach (var instruction in block.Instructions)
        {
            homes[instruction] = block;
            if (instruction.Destination is LocalVariable destination)
            {
                definitions[destination] = definitions.ContainsKey(destination) ? null : instruction;
                if (!writes.TryGetValue(destination, out var assigned))
                    writes.Add(destination, assigned = []);
                assigned.Add(instruction);
            }
            else if (instruction.Destination is FieldReference field)
                addressed.Add(field.Local);
            else if (instruction.Destination is MemoryOperand { Base: LocalVariable storage })
                addressed.Add(storage);
            foreach (var operand in instruction.Operands)
                if (operand is AddressOf { Target: LocalVariable escaped })
                    addressed.Add(escaped);
        }
        var fieldsByType = new Dictionary<TypeAnalysisContext, FieldAnalysisContext[]>();

        foreach (var block in graph.Blocks)
        {
            var operations = block.Instructions.Where(i => i.OpCode != OpCode.Nop).ToArray();
            for (var index = 0; index + 2 < operations.Length; index++)
            {
                var first = operations[index];
                if (first.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply))
                    continue;

                var lanes = new[] { first, operations[index + 1], operations[index + 2] };
                var outputs = new HashSet<LocalVariable>();
                if (lanes.Any(lane => lane.OpCode != first.OpCode
                        || lane.OperandWidth != null || lane.MemoryAccessSize != null
                        || lane.Operands is not [LocalVariable { Type.FullName: "System.Single" } dest, _, _,
                            FloatingPointPrecision { IsDouble: false }]
                        || !outputs.Add(dest) || addressed.Contains(dest))
                    || lanes.SelectMany(DeadCodeEliminator.UsedLocals).Any(outputs.Contains))
                    continue;

                var left = lanes.Select(lane => lane.Operands[1]).ToArray();
                var right = lanes.Select(lane => lane.Operands[2]).ToArray();
                var lhs = ReadVector(left, first);
                var rhs = first.OpCode == OpCode.Multiply ? null : ReadVector(right, first);
                if (lhs == null && rhs?.Local == null)
                    continue;

                var vectorType = lhs?.Type ?? rhs!.Value.Type;
                var merged = lhs == null ? ReadMergedVector(left, first, vectorType) : null;
                if (lhs == null && merged == null)
                    continue;
                LocalVariable? rightVector = null;
                IOperand scale = right[0];
                var operatorName = first.OpCode switch
                {
                    OpCode.Multiply => "op_Multiply",
                    OpCode.Add => "op_Addition",
                    _ => "op_Subtraction"
                };
                if (first.OpCode == OpCode.Multiply)
                {
                    if (!right.All(operand => SameScalar(operand, scale)))
                        continue;
                    if (lhs?.Local is { } radians && TryFoldEuler(operations, index, left, radians, scale, vectorType))
                    {
                        index += 4;
                        continue;
                    }
                }
                else if (rhs is { Local: { } vector } other && other.Type == vectorType)
                {
                    rightVector = vector;
                }
                else
                {
                    continue;
                }

                var vectorOperator = vectorType.Methods.FirstOrDefault(candidate => candidate.Name == operatorName
                    && candidate is { IsStatic: true, Visibility: MethodAttributes.Public, Parameters.Count: 2 }
                    && MetadataResolver.IsSameType(candidate.ReturnType, vectorType)
                    && MetadataResolver.IsSameType(candidate.Parameters[0].ParameterType, vectorType)
                    && (rightVector == null ? candidate.Parameters[1].ParameterType.FullName == "System.Single"
                        : MetadataResolver.IsSameType(candidate.Parameters[1].ParameterType, vectorType)));
                if (vectorOperator == null)
                    continue;

                var insertAt = block.Instructions.IndexOf(first);
                var source = lhs?.Local;
                if (merged != null)
                {
                    source = NewVector(vectorType);
                    foreach (var edge in merged)
                    {
                        edge.Block.Instructions.Insert(edge.Block.Instructions.IndexOf(edge.Stores[0]),
                            new Instruction(-1, OpCode.Move, source, edge.Source));
                        foreach (var store in edge.Stores)
                            Remove(store);
                    }
                }

                if (source == null)
                {
                    // Replace only a contiguous sequence of single-use reads of the
                    // readonly Unity constant, at their original type initialization point.
                    if (index < 3 || lhs?.Getter is not { } getter
                        || !left.OfType<LocalVariable>().Select(component => definitions[component])
                            .SequenceEqual(operations.Skip(index - 3).Take(3))
                        || left.OfType<LocalVariable>().Any(component => reads[component].Count() != 1))
                        continue;

                    source = NewVector(vectorType);
                    insertAt = block.Instructions.IndexOf(operations[index - 3]);
                    block.Instructions.Insert(insertAt++, new Instruction(-1, OpCode.Call, getter, source));
                    foreach (var component in left.OfType<LocalVariable>())
                        Remove(definitions[component]!);
                }

                var result = NewVector(vectorType);
                var operation = new Instruction(-1, OpCode.Call, vectorOperator, result, source,
                    rightVector is { } argument ? argument : scale);
                block.Instructions.Insert(insertAt, operation);
                definitions.Add(result, operation);
                homes.Add(operation, block);
                var fields = GetFields(vectorType);
                for (var lane = 0; lane < 3; lane++)
                {
                    var destination = (LocalVariable)lanes[lane].Operands[0];
                    lanes[lane].OpCode = OpCode.Move;
                    lanes[lane].MemoryAccessSize = null;
                    lanes[lane].OperandWidth = null;
                    lanes[lane].SetOperands(destination, new FieldReference(fields[lane], result, fields[lane].Offset));
                }
                index += 2;
            }
        }

        foreach (var pack in instructions)
        {
            if (pack is not { OpCode: OpCode.PackFloatAggregate,
                    Operands: [LocalVariable destination, TypeAnalysisContext type, var x, var y, var z] }
                || !IsVector(type)
                || ReadVector([x, y, z], pack) is not { Local: { } source } vector || vector.Type != type)
                continue;

            // If these were the only consumers of a newly recovered operator, let it
            // write the argument directly. This keeps the call expression standalone.
            var components = new[] { x, y, z }.OfType<LocalVariable>().ToArray();
            if (components.Length == 3 && components.All(component => reads[component].Count() == 1)
                && definitions.TryGetValue(source, out var operation) && operation is { IsCall: true }
                && source.Register.Name.StartsWith("vectorMath", StringComparison.Ordinal)
                && homes[operation] == homes[pack])
            {
                var block = homes[pack];
                var begin = block.Instructions.IndexOf(operation);
                var end = block.Instructions.IndexOf(pack);
                var projections = components.Select(component => definitions[component]!).ToArray();
                if (end > begin && block.Instructions.Skip(begin + 1).Take(end - begin - 1)
                        .All(i => i.OpCode == OpCode.Nop || projections.Contains(i))
                    && projections.All(i => block.Instructions.IndexOf(i) > begin)
                    && !graph.Instructions.Any(i => !projections.Contains(i)
                        && DeadCodeEliminator.UsedLocals(i).Contains(source)))
                {
                    operation.Destination = destination;
                    foreach (var projection in projections)
                        Remove(projection);
                    Remove(pack);
                    continue;
                }
            }
            pack.OpCode = OpCode.Move;
            pack.SetOperands(destination, source);
        }

        LocalVariable NewVector(TypeAnalysisContext type)
        {
            var name = $"vectorMath{method.Locals.Count}";
            var local = new LocalVariable(name, new Register(null, name), type);
            method.Locals.Add(local);
            return local;
        }

        FieldAnalysisContext[] GetFields(TypeAnalysisContext type)
        {
            if (!fieldsByType.TryGetValue(type, out var fields))
                fieldsByType.Add(type, fields = type.Fields.Where(field => !field.IsStatic)
                    .OrderBy(field => field.Offset).ToArray());
            return fields;
        }

        bool ComesBefore(Instruction producer, Instruction consumer)
        {
            var before = homes[producer];
            var after = homes[consumer];
            return before == after ? before.Instructions.IndexOf(producer) < after.Instructions.IndexOf(consumer)
                : (dominance ??= new DominatorInfo(graph)).Dominates(before, after);
        }

        List<(Block Block, Instruction[] Stores, LocalVariable Source)>? ReadMergedVector(IOperand[] operands,
            Instruction consumer, TypeAnalysisContext type)
        {
            // Match the copies left by lowering three related phis on exactly two edges.
            var join = homes[consumer];
            var visited = new HashSet<Block>();
            while (visited.Count < 12 && visited.Add(join) && join.Predecessors is [var previous]
                   && previous.Successors is [var next] && next == join)
                join = previous;
            if (join.Predecessors.Count != 2 || operands.OfType<LocalVariable>().Count() != 3)
                return null;

            var components = operands.Cast<LocalVariable>().ToArray();
            if (components.Distinct().Count() != 3 || components.Any(component => reads[component].Count() != 1
                    || !writes.TryGetValue(component, out var assigned) || assigned.Count != 2))
                return null;

            var edges = new List<(Block, Instruction[], LocalVariable)>();
            foreach (var predecessor in join.Predecessors)
            {
                if (visited.Contains(predecessor) || predecessor.Successors is not [var target] || target != join)
                    return null;
                var stores = new Instruction[3];
                var sources = new IOperand[3];
                for (var lane = 0; lane < 3; lane++)
                {
                    var assignments = writes[components[lane]];
                    if (assignments.Count(write => homes[write] == predecessor) != 1
                        || assignments.First(write => homes[write] == predecessor) is not
                            { OpCode: OpCode.Move, OperandWidth: null, Operands: [_, var value] } store)
                        return null;
                    stores[lane] = store;
                    sources[lane] = value;
                }
                var ordered = stores.OrderBy(store => predecessor.Instructions.IndexOf(store)).ToArray();
                if (ReadVector(sources, ordered[0]) is not { Local: { } source } vector || vector.Type != type)
                    return null;
                edges.Add((predecessor, ordered, source));
            }
            return edges;
        }

        bool TryFoldEuler(Instruction[] operations, int index, IOperand[] components, LocalVariable radians,
            IOperand scale, TypeAnalysisContext vectorType)
        {
            if (index < 4 || index + 4 >= operations.Length || scale is not FloatLiteral { Value: 57.29578f }
                || operations[index - 4] is not { OpCode: OpCode.Call, Operands:
                    [MethodAnalysisContext { Name: "Internal_ToEulerRad", IsStatic: true, DeclaringType: { } quaternionType },
                        LocalVariable produced, LocalVariable quaternion] } toRadians
                || produced != radians || quaternionType.FullName != "UnityEngine.Quaternion"
                || quaternion.Type != quaternionType || quaternionType.DeclaringAssembly != vectorType.DeclaringAssembly
                || reads[radians].Count() != 3 || components.OfType<LocalVariable>().Any(value => reads[value].Count() != 1)
                || !components.OfType<LocalVariable>().Select(value => definitions[value])
                    .SequenceEqual(operations.Skip(index - 3).Take(3))
                || operations[index + 3] is not { OpCode: OpCode.PackFloatAggregate, Operands:
                    [LocalVariable packed, TypeAnalysisContext packedType, var x, var y, var z] }
                || packedType != vectorType || reads[packed].Count() != 1
                || !ReferenceEquals(x, operations[index].Destination) || !ReferenceEquals(y, operations[index + 1].Destination)
                || !ReferenceEquals(z, operations[index + 2].Destination)
                || new[] { x, y, z }.OfType<LocalVariable>().Any(value => reads[value].Count() != 1)
                || operations[index + 4] is not { OpCode: OpCode.Call, Operands:
                    [MethodAnalysisContext { Name: "Internal_MakePositive", IsStatic: true, DeclaringType: var owner },
                        LocalVariable result, LocalVariable argument] }
                || owner != quaternionType || argument != packed || result.Type != vectorType)
                return false;

            var getter = quaternionType.Properties.FirstOrDefault(property => property.Name == "eulerAngles")?.Getter;
            if (getter is not { IsStatic: false, Visibility: MethodAttributes.Public, Parameters.Count: 0 }
                || getter.ReturnType != vectorType)
                return false;

            toRadians.SetOperands(getter, result, new AddressOf(quaternion));
            for (var i = index - 3; i <= index + 4; i++)
                Remove(operations[i]);
            return true;
        }

        (TypeAnalysisContext Type, LocalVariable? Local, MethodAnalysisContext? Getter)? ReadVector(IOperand[] operands,
            Instruction consumer)
        {
            LocalVariable? source = null;
            FieldAnalysisContext? staticSource = null;
            TypeAnalysisContext? vectorType = null;
            for (var lane = 0; lane < 3; lane++)
            {
                var at = consumer;
                var operand = operands[lane];
                if (operand is LocalVariable component && definitions.TryGetValue(component, out var definition)
                    && definition is { OpCode: OpCode.Move, OperandWidth: null, Operands: [_, FieldReference read] }
                    && ComesBefore(definition, consumer))
                {
                    operand = read;
                    at = definition;
                }
                if (operand is not FieldReference field || !IsVector(field.Field.DeclaringType))
                    return null;

                var type = field.Field.DeclaringType;
                var fields = GetFields(type);
                if (fields.Length != 3 || fields[lane] != field.Field || field.Field.FieldType.FullName != "System.Single"
                    || field.Field.Name != (lane == 0 ? "x" : lane == 1 ? "y" : "z")
                    || vectorType != null && vectorType != type)
                    return null;

                vectorType = type;
                if (field.Parent == null && field.Local.Type == type && !addressed.Contains(field.Local)
                    && (definitions.TryGetValue(field.Local, out var vectorDefinition)
                        ? vectorDefinition != null && ComesBefore(vectorDefinition, at)
                        : method.ParameterLocals.Contains(field.Local))
                    && (lane == 0 || field.Local == source))
                    source = field.Local;
                else if (field.Parent is { Parent: null, Field: var parent }
                    && parent.IsStatic && parent.Name == "oneVector" && parent.FieldType == type && parent.DeclaringType == type
                    && (parent.Attributes & FieldAttributes.InitOnly) != 0 && (lane == 0 || staticSource == parent))
                    staticSource = parent;
                else
                    return null;
            }

            if (source != null)
                return (vectorType!, source, null);

            var getter = vectorType!.Properties.FirstOrDefault(property => property.Name == "one")?.Getter;
            return getter is { IsStatic: true, Visibility: MethodAttributes.Public, Parameters.Count: 0 }
                && getter.ReturnType == vectorType ? (vectorType, null, getter) : null;
        }
    }

    private static void Remove(Instruction instruction)
    {
        instruction.OpCode = OpCode.Nop;
        instruction.SetOperands();
    }

    internal static bool IsVector(TypeAnalysisContext? type) =>
        type is { FullName: "UnityEngine.Vector3", IsValueType: true }
        && type.DeclaringAssembly.Name is "UnityEngine.CoreModule" or "UnityEngine";

    private static bool SameScalar(IOperand left, IOperand right) =>
        left is LocalVariable { Type.FullName: "System.Single" } && ReferenceEquals(left, right)
        || left is FloatLiteral x && right is FloatLiteral y
            && BitConverter.DoubleToInt64Bits(x.Value) == BitConverter.DoubleToInt64Bits(y.Value);
}
