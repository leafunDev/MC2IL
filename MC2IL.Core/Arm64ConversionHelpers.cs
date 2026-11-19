using System;
using System.Linq;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using MC2IL.Core.ISIL;

namespace MC2IL.Core;

// Keep FCVT saturation in the recovered assembly so its callers decompile into one expression.
internal sealed class Arm64ConversionHelpers
{
    private const string HelperNamespace = "MC2ILInjected";
    private const string HelperTypeName = "Arm64Conversions";
    private static readonly ConditionalWeakTable<ModuleDefinition, Arm64ConversionHelpers> Modules = new();

    private TypeDefinition? _type;

    public static MethodDefinition GetConversion(ModuleDefinition module, NumericConversion conversion)
    {
        var factory = module.CorLibTypeFactory;
        var (resultType, cast) = conversion.Kind switch
        {
            NumericConversionKind.Float32ToInt32Signed or NumericConversionKind.Float64ToInt32Signed => (factory.Int32, CilOpCodes.Conv_I4),
            NumericConversionKind.Float32ToInt32Unsigned or NumericConversionKind.Float64ToInt32Unsigned => (factory.UInt32, CilOpCodes.Conv_U4),
            NumericConversionKind.Float32ToInt64Signed or NumericConversionKind.Float64ToInt64Signed => (factory.Int64, CilOpCodes.Conv_I8),
            NumericConversionKind.Float32ToInt64Unsigned or NumericConversionKind.Float64ToInt64Unsigned => (factory.UInt64, CilOpCodes.Conv_U8),
            _ => throw new ArgumentOutOfRangeException(nameof(conversion), conversion.Kind, "Expected a float-to-integer conversion.")
        };

        var operation = conversion.Rounding switch
        {
            NumericConversionRounding.TowardZero => "Truncate",
            NumericConversionRounding.TowardNegativeInfinity => "Floor",
            NumericConversionRounding.NearestTiesToEven => "RoundToEven",
            NumericConversionRounding.TowardPositiveInfinity => "Ceiling",
            NumericConversionRounding.NearestTiesAwayFromZero => "RoundAwayFromZero",
            _ => throw new ArgumentOutOfRangeException(nameof(conversion), conversion.Rounding, "Unknown ARM64 rounding mode.")
        };
        var name = operation + "To" + resultType.Name;

        // Method bodies from the same module are emitted in parallel.
        var helpers = Modules.GetValue(module, _ => new Arm64ConversionHelpers());
        lock (helpers)
        {
            var type = helpers._type ??= AddHelperType(module);
            var index = 0;
            while (index < type.Methods.Count)
            {
                var order = string.CompareOrdinal(type.Methods[index].Name, name);
                if (order == 0)
                    return type.Methods[index];
                if (order > 0)
                    break;
                index++;
            }

            var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodSignature.CreateStatic(resultType, [factory.Double]));
            method.ParameterDefinitions.Add(new ParameterDefinition(1, "value", 0));
            EmitBody(module, method, conversion.Rounding, cast);
            type.Methods.Insert(index, method);
            return method;
        }
    }

    private static TypeDefinition AddHelperType(ModuleDefinition module)
    {
        var name = HelperTypeName;
        var suffix = 0;
        // Avoid colliding with a type already present in the input metadata.
        while (module.TopLevelTypes.Any(type => type.Namespace == HelperNamespace && type.Name == name))
            name = HelperTypeName + ++suffix;

        var helper = new TypeDefinition(HelperNamespace, name,
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.CorLibTypeFactory.Object.ToTypeDefOrRef());
        module.TopLevelTypes.Add(helper);
        return helper;
    }

    private static void EmitBody(ModuleDefinition module, MethodDefinition method, NumericConversionRounding rounding, CilOpCode cast)
    {
        var factory = module.CorLibTypeFactory;
        var doubleType = factory.Double;
        var body = new CilMethodBody();
        method.CilMethodBody = body;
        var instructions = body.Instructions;

        var nan = new CilInstructionLabel();
        var belowRange = new CilInstructionLabel();
        var aboveRange = new CilInstructionLabel();
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Call, doubleType.ToTypeDefOrRef().CreateMemberReference(
            "IsNaN", MethodSignature.CreateStatic(factory.Boolean, [doubleType])));
        instructions.Add(CilOpCodes.Brtrue, nan);

        var mathMethod = rounding switch
        {
            NumericConversionRounding.TowardNegativeInfinity => "Floor",
            NumericConversionRounding.TowardPositiveInfinity => "Ceiling",
            NumericConversionRounding.NearestTiesToEven or NumericConversionRounding.NearestTiesAwayFromZero => "Round",
            _ => null
        };

        CilLocalVariable? rounded = null;
        if (mathMethod != null)
        {
            var mathType = factory.CorLibScope.CreateTypeReference("System", "Math");
            var signature = MethodSignature.CreateStatic(doubleType, [doubleType]);
            instructions.Add(CilOpCodes.Ldarg_0);
            if (rounding == NumericConversionRounding.NearestTiesAwayFromZero)
            {
                var midpointRounding = factory.CorLibScope.CreateTypeReference("System", "MidpointRounding").ToTypeSignature(true);
                signature.ParameterTypes.Add(midpointRounding);
                instructions.Add(CilOpCodes.Ldc_I4_1); // MidpointRounding.AwayFromZero
            }

            instructions.Add(CilOpCodes.Call, mathType.CreateMemberReference(mathMethod, signature));
            rounded = new CilLocalVariable(doubleType);
            body.LocalVariables.Add(rounded);
            instructions.Add(CilOpCodes.Stloc, rounded);
        }

        var (lowerInclusive, upperExclusive) = cast.Code switch
        {
            CilCode.Conv_I4 => (-2147483648d, 2147483648d),
            CilCode.Conv_U4 => (0d, 4294967296d),
            CilCode.Conv_I8 => (-9223372036854775808d, 9223372036854775808d),
            CilCode.Conv_U8 => (0d, 18446744073709551616d),
            _ => throw new ArgumentOutOfRangeException(nameof(cast))
        };

        // These exact power-of-two thresholds are representable as doubles even for Int64 and
        // UInt64. CIL only receives values for which truncation is representable.
        LoadValue();
        instructions.Add(CilOpCodes.Ldc_R8, upperExclusive);
        instructions.Add(CilOpCodes.Bge, aboveRange);
        LoadValue();
        instructions.Add(CilOpCodes.Ldc_R8, lowerInclusive);
        instructions.Add(CilOpCodes.Blt, belowRange);
        LoadValue();
        instructions.Add(cast);
        instructions.Add(CilOpCodes.Ret);

        aboveRange.Instruction = cast.Code switch
        {
            CilCode.Conv_I4 => new CilInstruction(CilOpCodes.Ldc_I4, int.MaxValue),
            CilCode.Conv_U4 => new CilInstruction(CilOpCodes.Ldc_I4_M1),
            CilCode.Conv_I8 => new CilInstruction(CilOpCodes.Ldc_I8, long.MaxValue),
            _ => new CilInstruction(CilOpCodes.Ldc_I8, -1L) // ulong.MaxValue
        };
        instructions.Add(aboveRange.Instruction);
        instructions.Add(CilOpCodes.Ret);

        belowRange.Instruction = cast.Code switch
        {
            CilCode.Conv_I4 => new CilInstruction(CilOpCodes.Ldc_I4, int.MinValue),
            CilCode.Conv_I8 => new CilInstruction(CilOpCodes.Ldc_I8, long.MinValue),
            CilCode.Conv_U4 => new CilInstruction(CilOpCodes.Ldc_I4_0),
            _ => new CilInstruction(CilOpCodes.Ldc_I8, 0L)
        };
        instructions.Add(belowRange.Instruction);
        instructions.Add(CilOpCodes.Ret);

        nan.Instruction = cast.Code is CilCode.Conv_I8 or CilCode.Conv_U8
            ? new CilInstruction(CilOpCodes.Ldc_I8, 0L)
            : new CilInstruction(CilOpCodes.Ldc_I4_0);
        instructions.Add(nan.Instruction);
        instructions.Add(CilOpCodes.Ret);

        void LoadValue()
        {
            if (rounded == null)
                instructions.Add(CilOpCodes.Ldarg_0);
            else
                instructions.Add(CilOpCodes.Ldloc, rounded);
        }
    }
}
