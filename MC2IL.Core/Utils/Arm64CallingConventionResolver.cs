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
            .OrderBy(field => field.BackingData?.FieldOffset).ToArray();
        if (fields.Length is < 1 or > 4 || !IsFloatingPoint(fields[0].FieldType))
            return null;

        var precision = fields[0].FieldType;
        var stride = precision.FullName == "System.Double" ? 8 : 4;
        if (TypeSizes.UnboxedSize(type, PtrSize) != fields.Length * stride)
            return null;

        for (var i = 0; i < fields.Length; i++)
            if (fields[i].FieldType != precision || fields[i].BackingData?.FieldOffset != i * stride)
                return null;

        return fields;
    }
}
