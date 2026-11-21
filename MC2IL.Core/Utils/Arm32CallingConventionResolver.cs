using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using LibMC2IL.Elf;

namespace MC2IL.Core.Utils;

/// <summary>
/// AAPCS32 argument and return placement for ARMv7. Android armeabi-v7a normally uses
/// softfp (float arguments in r0-r3); ELF files advertising the VFP PCS use s0-s15/d0-d7.
/// </summary>
public sealed class Arm32CallingConventionResolver : BaseCallingConventionResolver
{
    private const int PointerSize = 4;
    private static readonly string[] IntegerRegisters = ["R0", "R1", "R2", "R3"];
    private static readonly string[] VfpArgumentRegisters = Enumerable.Range(0, 16).Select(i => $"S{i}").ToArray();

    private static bool UsesHardFloat(ApplicationAnalysisContext context)
        => context.Binary is ElfFile { UsesArmHardFloatAbi: true };

    public override Register ReturnRegister(MethodAnalysisContext ctx)
    {
        if (UsesHardFloat(ctx.AppContext) && IsFloatingPoint(ctx.ReturnType))
            return new Register(null, ctx.ReturnType.FullName == "System.Double" ? "D0" : "S0");
        return new Register(null, "R0");
    }

    public override Register? HiddenReturnBufferRegister(MethodAnalysisContext ctx)
        => ReturnsViaHiddenBuffer(ctx) ? new Register(null, "R0") : null;

    public override bool ReturnsViaHiddenBuffer(MethodAnalysisContext ctx)
    {
        if (ctx.IsVoid || !ctx.ReturnType.IsValueType || IsFloatingPoint(ctx.ReturnType))
            return false;

        var size = TypeSizes.UnboxedSize(ctx.ReturnType, PointerSize);
        // AAPCS32 returns a composite of at most one word in r0; larger composites use caller storage.
        return size > PointerSize;
    }

    protected override (string[] Integer, string[] Float) RawRegisters(ApplicationAnalysisContext app)
        => (IntegerRegisters, UsesHardFloat(app) ? VfpArgumentRegisters : []);

    public override IOperand[] ResolveForManaged(MethodAnalysisContext ctx)
        => ResolveArguments(ctx);

    public override void RemapRawArguments(Instruction call, MethodAnalysisContext resolved)
    {
        var app = resolved.AppContext;
        if (!HasRawArgumentLayout(call, app))
            return;

        var (integerRegisters, floatRegisters) = RawRegisters(app);
        var argBase = ArgBase(call);
        var hardFloat = UsesHardFloat(app);
        var integerIndex = ReturnsViaHiddenBuffer(resolved) ? 1 : 0;
        var floatingSlot = 0;
        var stackOffset = 0;
        var operands = call.Operands.Take(argBase).ToList();

        void AddArgument(TypeAnalysisContext? type)
        {
            var size = type == null ? PointerSize : TypeSizes.UnboxedSize(type, PointerSize);
            var isFloat = hardFloat && type != null && IsFloatingPoint(type);
            if (isFloat)
            {
                var isDouble = type!.FullName == "System.Double";
                if (isDouble && (floatingSlot & 1) != 0)
                    floatingSlot++;

                var width = isDouble ? 2 : 1;
                if (floatingSlot + width <= floatRegisters.Length)
                {
                    operands.Add(new Register(null, isDouble ? $"D{floatingSlot / 2}" : $"S{floatingSlot}"));
                    floatingSlot += width;
                    return;
                }

                floatingSlot = floatRegisters.Length;
                if (isDouble)
                    stackOffset = (stackOffset + 7) & ~7;
                if (type != null)
                    operands.Add(new StackOffset(stackOffset));
                stackOffset += isDouble ? 8 : PointerSize;
                return;
            }

            var wordCount = size > PointerSize ? (int)((size + PointerSize - 1) / PointerSize) : 1;
            if (wordCount > 1 && (integerIndex & 1) != 0)
                integerIndex++;
            if (integerIndex + wordCount <= integerRegisters.Length)
            {
                operands.Add(call.Operands[argBase + integerIndex]);
                integerIndex += wordCount;
                return;
            }

            integerIndex = integerRegisters.Length;
            if (wordCount > 1)
                stackOffset = (stackOffset + 7) & ~7;
            if (type != null)
                operands.Add(new StackOffset(stackOffset));
            stackOffset += System.Math.Max(PointerSize, wordCount * PointerSize);
        }

        if (!resolved.IsStatic)
            AddArgument(resolved.DeclaringType);
        foreach (var parameter in resolved.Parameters)
            AddArgument(parameter.ParameterType);
        AddArgument(null); // IL2CPP MethodInfo* argument

        call.SetOperands(operands);
    }

    private IOperand[] ResolveArguments(MethodAnalysisContext ctx)
    {
        var args = new List<IOperand>();
        var hardFloat = UsesHardFloat(ctx.AppContext);
        var integerIndex = ReturnsViaHiddenBuffer(ctx) ? 1 : 0;
        var floatingSlot = 0;
        var stackOffset = 0;

        void AddArgument(TypeAnalysisContext? type)
        {
            var size = type == null ? PointerSize : TypeSizes.UnboxedSize(type, PointerSize);
            if (hardFloat && type != null && IsFloatingPoint(type))
            {
                var isDouble = type.FullName == "System.Double";
                if (isDouble && (floatingSlot & 1) != 0)
                    floatingSlot++;

                var width = isDouble ? 2 : 1;
                if (floatingSlot + width <= VfpArgumentRegisters.Length)
                {
                    args.Add(new Register(null, isDouble ? $"D{floatingSlot / 2}" : $"S{floatingSlot}"));
                    floatingSlot += width;
                    return;
                }

                floatingSlot = VfpArgumentRegisters.Length;
                if (isDouble)
                    stackOffset = (stackOffset + 7) & ~7;
                args.Add(new StackOffset(stackOffset));
                stackOffset += isDouble ? 8 : PointerSize;
                return;
            }

            var wordCount = size > PointerSize ? (int)((size + PointerSize - 1) / PointerSize) : 1;
            if (wordCount > 1 && (integerIndex & 1) != 0)
                integerIndex++;
            if (integerIndex + wordCount <= IntegerRegisters.Length)
            {
                args.Add(new Register(null, IntegerRegisters[integerIndex]));
                integerIndex += wordCount;
                return;
            }

            integerIndex = IntegerRegisters.Length;
            if (wordCount > 1)
                stackOffset = (stackOffset + 7) & ~7;
            args.Add(new StackOffset(stackOffset));
            stackOffset += System.Math.Max(PointerSize, wordCount * PointerSize);
        }

        if (!ctx.IsStatic)
            AddArgument(ctx.DeclaringType);
        foreach (var parameter in ctx.Parameters)
            AddArgument(parameter.ParameterType);
        AddArgument(null); // IL2CPP MethodInfo* argument
        return args.ToArray();
    }
}
