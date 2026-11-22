using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Utils;

// integer args in X0-X7, fp args in V0-V7 (independent counters), rest on the stack.
// Oversized struct returns go via a pointer in X8, which is not an argument register.
public class Arm64CallingConventionResolver : BaseCallingConventionResolver
{
    internal const string AggregateParameterPrefix = "ARG_FLOAT_AGGREGATE";
    private const int PtrSize = 8;

    private static readonly string[] IntegerRegisters = ["X0", "X1", "X2", "X3", "X4", "X5", "X6", "X7"];
    private static readonly string[] FloatRegisters = ["V0", "V1", "V2", "V3", "V4", "V5", "V6", "V7"];

    public override Register ReturnRegister(MethodAnalysisContext ctx)
        => new(null, IsFloatingPoint(ctx.ReturnType) ? "V0" : "X0");

    public override Register? HiddenReturnBufferRegister(MethodAnalysisContext ctx)
        => ReturnsViaHiddenBuffer(ctx) ? new Register(null, "X8") : null;

    public override bool ReturnsViaHiddenBuffer(MethodAnalysisContext ctx)
    {
        if (ctx.IsVoid)
            return false;

        var returnType = ctx.ReturnType;
        if (!returnType.IsValueType || IsFloatingPoint(returnType) || GetFloatAggregateFields(returnType) != null)
            return false;

        var size = TypeSizes.UnboxedSize(returnType, PtrSize);
        if (size == 0)
            return false; // unknown size (e.g. generic), assume a register return

        return size > 16;
    }

    protected override (string[] Integer, string[] Float) RawRegisters(ApplicationAnalysisContext app)
        => (IntegerRegisters, FloatRegisters);

    protected override bool HiddenBufferConsumesArgumentSlot => false;

    public override IOperand[] ResolveForManaged(MethodAnalysisContext ctx)
        => ResolveForManagedEntry(ctx);

    internal IOperand[] ResolveForManagedEntry(MethodAnalysisContext ctx,
        Action<Register, FieldAnalysisContext[], IOperand[]>? bindLanes = null)
        => ResolveParameters(ctx, (_, fields, lanes, index) =>
        {
            var argument = new Register(null, $"{AggregateParameterPrefix}{index}");
            bindLanes?.Invoke(argument, fields, lanes);
            return argument;
        });

    public IOperand[] ResolveForManagedCall(MethodAnalysisContext ctx,
        Func<TypeAnalysisContext, IOperand[], IOperand> packAggregate)
        => ResolveParameters(ctx, (type, _, lanes, _) => packAggregate(type, lanes));

    // When the native address is shared, the selected signature is only a guess.
    internal IOperand[] ResolveForAmbiguousManagedCall(MethodAnalysisContext ctx)
        => ResolveParameters(ctx, null);

    public override void RemapRawArguments(Instruction call, MethodAnalysisContext resolved, MethodAnalysisContext caller)
    {
        if (!HasRawArgumentLayout(call, resolved.AppContext))
            return;

        var block = caller.ControlFlowGraph?.Blocks.FirstOrDefault(candidate => candidate.Instructions.Contains(call));
        if (block == null || !resolved.Parameters.Any(parameter => GetFloatAggregateFields(parameter.ParameterType) != null))
        {
            base.RemapRawArguments(call, resolved);
            return;
        }

        var argumentBase = ArgBase(call);
        var raw = call.Operands.ToArray();
        var packs = new List<Instruction>();
        var arguments = ResolveForManagedCall(resolved, (type, lanes) =>
        {
            // An unresolved call preserves register arguments, but not the outgoing stack.
            // Keep a typed missing value if the entire aggregate overflowed the SIMD bank.
            if (lanes.Any(lane => lane is not Register))
                return new UnrecoveredCallArgument(type);

            var fields = GetFloatAggregateFields(type)!;
            var result = new LocalVariable($"callAggregate{caller.Locals.Count}",
                new Register(null, $"TEMP_RAW_AGGREGATE{caller.Locals.Count}"), type);
            caller.Locals.Add(result);
            var pack = new Instruction(-1, OpCode.PackFloatAggregate, result, type);
            for (var lane = 0; lane < lanes.Length; lane++)
            {
                var value = Map(lanes[lane], fields[lane].FieldType);
                if (value is LocalVariable local)
                    local.Type ??= fields[lane].FieldType;
                pack.AddOperands([value]);
            }
            packs.Add(pack);
            return result;
        });

        var operands = raw.Take(argumentBase).ToList();
        for (var index = 0; index < arguments.Length; index++)
        {
            var parameterIndex = index - (resolved.IsStatic ? 0 : 1);
            var type = parameterIndex < 0 ? resolved.DeclaringType
                : parameterIndex < resolved.Parameters.Count ? resolved.Parameters[parameterIndex].ParameterType : null;
            if (arguments[index] is StackOffset && type == null)
                continue; // The hidden MethodInfo is not a managed argument.
            operands.Add(Map(arguments[index], type));
        }
        call.SetOperands(operands);
        block.Instructions.InsertRange(block.Instructions.IndexOf(call), packs);

        IOperand Map(IOperand location, TypeAnalysisContext? type)
        {
            if (location is Register register)
            {
                var integer = Array.IndexOf(IntegerRegisters, register.Name);
                if (integer >= 0)
                    return raw[argumentBase + integer];
                var floating = Array.IndexOf(FloatRegisters, register.Name);
                if (floating >= 0)
                    return raw[argumentBase + IntegerRegisters.Length + floating];
            }
            return location is StackOffset && type != null ? new UnrecoveredCallArgument(type) : location;
        }
    }

    private static IOperand[] ResolveParameters(MethodAnalysisContext ctx,
        Func<TypeAnalysisContext, FieldAnalysisContext[], IOperand[], int, IOperand>? bindAggregate)
    {
        var args = new List<IOperand>();

        var integer = 0;
        var floating = 0;
        var stack = 0;

        void AddParameter(ParameterAnalysisContext? par)
        {
            if (bindAggregate != null && par != null && GetFloatAggregateFields(par.ParameterType) is { } fields)
            {
                var lanes = new IOperand[fields.Length];
                if (floating <= FloatRegisters.Length - fields.Length)
                {
                    for (var i = 0; i < lanes.Length; i++)
                        lanes[i] = new Register(null, FloatRegisters[floating++]);
                }
                else
                {
                    // An aggregate which does not fit in the SIMD bank goes on the stack as a unit.
                    floating = FloatRegisters.Length;
                    var laneSize = fields[0].FieldType.FullName == "System.Double" ? 8 : 4;
                    for (var i = 0; i < lanes.Length; i++)
                        lanes[i] = new StackOffset(stack + i * laneSize);
                    stack += (lanes.Length * laneSize + PtrSize - 1) & ~(PtrSize - 1);
                }
                args.Add(bindAggregate(par.ParameterType, fields, lanes, args.Count));
                return;
            }

            if (par != null && IsFloatingPoint(par))
            {
                if (floating < FloatRegisters.Length)
                {
                    args.Add(new Register(null, FloatRegisters[floating++]));
                    return;
                }
            }
            else if (integer < IntegerRegisters.Length)
            {
                args.Add(new Register(null, IntegerRegisters[integer++]));
                return;
            }

            args.Add(new StackOffset(stack));
            stack += PtrSize;
        }

        if (!ctx.IsStatic)
            AddParameter(null);

        foreach (var par in ctx.Parameters)
            AddParameter(par);

        AddParameter(null); // The MethodInfo argument

        return args.ToArray();
    }

    internal static FieldAnalysisContext[]? GetFloatAggregateFields(TypeAnalysisContext type)
    {
        if (!type.IsValueType || type.IsEnumType || type.Definition == null || IsFloatingPoint(type))
            return null;

        var fields = type.Fields.Where(field => !field.IsStatic)
            .OrderBy(field => field.Offset).ToArray();
        if (fields.Length is < 1 or > 4 || !IsFloatingPoint(fields[0].FieldType))
            return null;

        var precision = fields[0].FieldType;
        var stride = precision.FullName == "System.Double" ? 8 : 4;
        if (TypeSizes.UnboxedSize(type, PtrSize) != fields.Length * stride)
            return null;

        for (var i = 0; i < fields.Length; i++)
            if (fields[i].FieldType != precision || fields[i].Offset != i * stride)
                return null;

        return fields;
    }
}
