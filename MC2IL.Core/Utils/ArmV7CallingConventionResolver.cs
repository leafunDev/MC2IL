using System;
using System.Collections.Generic;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Utils;

// Android armeabi-v7a uses base AAPCS for calls even when the body uses VFP.
internal sealed class ArmV7CallingConventionResolver : BaseCallingConventionResolver
{
    private static readonly string[] CoreRegisters = ["r0", "r1", "r2", "r3"];
    public override Register ReturnRegister(MethodAnalysisContext ctx) => new(null, "r0");
    public override Register? HiddenReturnBufferRegister(MethodAnalysisContext ctx) => ReturnsViaHiddenBuffer(ctx) ? ReturnRegister(ctx) : null;
    public override bool ReturnsViaHiddenBuffer(MethodAnalysisContext ctx) =>
        !ctx.IsVoid && ctx.ReturnType is { IsValueType: true, IsEnumType: false } type
        && !IsFloatingPoint(type) && type.FullName is not ("System.Int64" or "System.UInt64")
        && TypeSizes.UnboxedSize(type, 4) > 4;

    protected override (string[] Integer, string[] Float) RawRegisters(ApplicationAnalysisContext app) => (CoreRegisters, []);
    public override IOperand[] ResolveForManaged(MethodAnalysisContext ctx) => Resolve(ctx, incoming: true);
    internal IOperand[] ResolveForCall(MethodAnalysisContext ctx) => Resolve(ctx, incoming: false);

    private IOperand[] Resolve(MethodAnalysisContext ctx, bool incoming)
    {
        var result = new List<IOperand>();
        var next = ReturnsViaHiddenBuffer(ctx) ? 1 : 0;
        if (!ctx.IsStatic)
            Append(null);
        foreach (var parameter in ctx.Parameters)
            Append(parameter.ParameterType);
        Append(null); // MethodInfo*
        return result.ToArray();

        void Append(TypeAnalysisContext? type)
        {
            var paired = type?.FullName is "System.Double" or "System.Int64" or "System.UInt64";
            var bytes = paired ? 8 : type is { IsValueType: true, IsEnumType: false } ? TypeSizes.UnboxedSize(type, 4) : 4;
            if (paired)
                next = (next + 1) & ~1;
            var slot = next;
            next += (int)Math.Max(1, (bytes + 3) / 4);
            if (slot < CoreRegisters.Length)
                result.Add(new Register(null, CoreRegisters[slot]));
            else if (incoming)
                result.Add(new Register(null, $"stack_{(slot - CoreRegisters.Length) * 4:X}"));
            else
                result.Add(new StackOffset((slot - CoreRegisters.Length) * 4));
        }
    }
}
