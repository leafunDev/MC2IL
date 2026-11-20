using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using MC2IL.Core.Logging;

namespace MC2IL.Core.OutputFormats;

/// <summary>
/// Reconstructs the managed cast around Delegate.Combine when IL2CPP lowers it
/// to exact-runtime-type checks and an invalid-cast throw.
/// </summary>
internal static class DelegateCombineRecovery
{
    private static readonly HashSet<CilCode> AllowedInstructions =
    [
        CilCode.Nop,
        CilCode.Ldarg_0,
        CilCode.Ldarg_1,
        CilCode.Ldarg,
        CilCode.Ldarg_S,
        CilCode.Ldloc_0,
        CilCode.Ldloc_1,
        CilCode.Ldloc_2,
        CilCode.Ldloc_3,
        CilCode.Ldfld,
        CilCode.Stfld,
        CilCode.Ldnull,
        CilCode.Stloc_0,
        CilCode.Stloc_1,
        CilCode.Stloc_2,
        CilCode.Stloc_3,
        CilCode.Stloc,
        CilCode.Stloc_S,
        CilCode.Ldloc,
        CilCode.Ldloc_S,
        CilCode.Call,
        CilCode.Callvirt,
        CilCode.Ldtoken,
        CilCode.Ceq,
        CilCode.Br,
        CilCode.Br_S,
        CilCode.Brtrue,
        CilCode.Brtrue_S,
        CilCode.Brfalse,
        CilCode.Brfalse_S,
        CilCode.Throw,
        CilCode.Ret
    ];

    public static void Run(IEnumerable<AssemblyDefinition> assemblies)
    {
        var recovered = 0;
        foreach (var method in EnumerateMethods(assemblies))
            if (TryRecover(method))
                recovered++;

        if (recovered > 0)
            Logger.InfoNewline($"Reconstructed {recovered} Delegate.Combine cast assignments.", "DelegateCombineRecovery");
    }

    private static bool TryRecover(MethodDefinition method)
    {
        if (method.IsStatic || method.Name?.ToString() is not { } name
            || !name.StartsWith("Add", StringComparison.Ordinal)
            || method.Signature is not { ParameterTypes.Count: 1 } methodSignature
            || methodSignature.ReturnType.FullName != "System.Void"
            || method.DeclaringType is not { } declaringType
            || method.CilMethodBody is not { ExceptionHandlers.Count: 0 } body
            || method.Parameters.Count != 1)
            return false;

        var parameters = methodSignature.ParameterTypes;
        if (parameters[0].FullName == "System.Delegate"
            || parameters[0].ToTypeDefOrRef() is not TypeDefinition delegateType
            || delegateType.BaseType?.FullName != "System.MulticastDelegate")
        {
            return false;
        }

        if (body.Instructions.Any(instruction => !AllowedInstructions.Contains(instruction.OpCode.Code)))
        {
            return false;
        }

        var combineCalls = body.Instructions
            .Where(instruction => instruction.OpCode.Code == CilCode.Call
                && instruction.Operand is IMethodDescriptor descriptor && descriptor.Name?.ToString() == "Combine"
                && IsDelegateCombine(descriptor))
            .ToArray();
        if (combineCalls.Length != 1 || combineCalls[0].Operand is not IMethodDescriptor combine)
        {
            return false;
        }

        var combineIndex = body.Instructions.IndexOf(combineCalls[0]);
        var significantBeforeCombine = body.Instructions
            .Take(combineIndex)
            .Where(instruction => instruction.OpCode.Code != CilCode.Nop)
            .TakeLast(3)
            .ToArray();
        if (significantBeforeCombine.Length != 3
            || significantBeforeCombine[0].OpCode.Code != CilCode.Ldarg_0
            || significantBeforeCombine[1].OpCode.Code != CilCode.Ldfld
            || significantBeforeCombine[1].Operand is not IFieldDescriptor { } sourceField
            || !IsParameterLoad(significantBeforeCombine[2], (object)method.Parameters[0]))
        {
            return false;
        }

        var targetField = declaringType.Fields.FirstOrDefault(field => SameField(sourceField, field));
        if (targetField is not { IsStatic: false, Signature: { } fieldSignature }
            || fieldSignature.FieldType.FullName != parameters[0].FullName
            || targetField.Name?.ToString() is not { } fieldName
            || !fieldName.StartsWith("m_", StringComparison.Ordinal))
        {
            return false;
        }

        var getTypeCalls = 0;
        var typeHandleCalls = 0;
        var typeTokens = 0;
        var exactChecks = 0;
        var fieldReads = 0;
        var fieldWrites = 0;
        var throws = 0;
        foreach (var instruction in body.Instructions)
        {
            switch (instruction.OpCode.Code)
            {
                case CilCode.Ldfld:
                    if (instruction.Operand is not IFieldDescriptor readField || !SameField(readField, targetField))
                        return false;
                    fieldReads++;
                    break;
                case CilCode.Stfld:
                    if (instruction.Operand is not IFieldDescriptor writtenField || !SameField(writtenField, targetField))
                        return false;
                    fieldWrites++;
                    break;
                case CilCode.Ldtoken:
                    if (instruction.Operand is not ITypeDefOrRef typeToken
                        || typeToken.FullName != delegateType.FullName)
                        return false;
                    typeTokens++;
                    break;
                case CilCode.Callvirt when instruction.Operand is IMethodDescriptor getType
                    && getType.Name?.ToString() == "GetType":
                    if (!IsObjectGetType(getType))
                        return false;
                    getTypeCalls++;
                    break;
                case CilCode.Call when instruction.Operand is IMethodDescriptor getTypeFromHandle
                    && getTypeFromHandle.Name?.ToString() == "GetTypeFromHandle":
                    if (!IsTypeGetTypeFromHandle(getTypeFromHandle))
                        return false;
                    typeHandleCalls++;
                    break;
                case CilCode.Ceq:
                    exactChecks++;
                    break;
                case CilCode.Throw:
                    throws++;
                    break;
                case CilCode.Call or CilCode.Callvirt:
                    // Delegate.Combine is the only other call allowed in this pattern.
                    if (!ReferenceEquals(instruction, combineCalls[0]))
                        return false;
                    break;
            }
        }

        if (getTypeCalls < 1
            || getTypeCalls != typeHandleCalls
            || getTypeCalls != typeTokens
            // Besides the exact type comparisons, the compiler emits one ceq
            // to test whether Delegate.Combine returned null.
            || exactChecks != getTypeCalls + 1
            || fieldReads != 1
            || fieldWrites < 1
            || throws != 1)
        {
            return false;
        }

        body.Instructions.Clear();
        body.LocalVariables.Clear();
        body.InitializeLocals = false;
        body.ComputeMaxStackOnBuild = false;
        body.MaxStack = 3;
        body.Instructions.Add(CilOpCodes.Ldarg_0);
        body.Instructions.Add(CilOpCodes.Ldarg_0);
        body.Instructions.Add(CilOpCodes.Ldfld, targetField);
        body.Instructions.Add(CilOpCodes.Ldarg_1);
        body.Instructions.Add(CilOpCodes.Call, combine);
        body.Instructions.Add(CilOpCodes.Castclass, targetField.Signature!.FieldType.ToTypeDefOrRef());
        body.Instructions.Add(CilOpCodes.Stfld, targetField);
        body.Instructions.Add(CilOpCodes.Ret);
        return true;
    }

    private static bool SameField(IFieldDescriptor left, FieldDefinition right) =>
        left.Name?.ToString() == right.Name?.ToString()
        && left.DeclaringType?.FullName == right.DeclaringType?.FullName
        && left.Signature?.FieldType.FullName == right.Signature?.FieldType.FullName;

    private static bool IsDelegateCombine(IMethodDescriptor method) =>
        method.DeclaringType?.FullName == "System.Delegate"
        && method.Signature is
        {
            HasThis: false,
            ParameterTypes.Count: 2,
            ReturnType.FullName: "System.Delegate"
        }
        && method.Signature.ParameterTypes.All(parameter => parameter.FullName == "System.Delegate");

    private static bool IsObjectGetType(IMethodDescriptor method) =>
        method.DeclaringType?.FullName == "System.Object"
        && method.Signature is { HasThis: true, ParameterTypes.Count: 0, ReturnType.FullName: "System.Type" };

    private static bool IsTypeGetTypeFromHandle(IMethodDescriptor method) =>
        method.DeclaringType?.FullName == "System.Type"
        && method.Signature is
        {
            HasThis: false,
            ParameterTypes.Count: 1,
            ParameterTypes: [var parameter],
            ReturnType.FullName: "System.Type"
        }
        && parameter.FullName == "System.RuntimeTypeHandle";

    private static bool IsParameterLoad(CilInstruction instruction, object parameter) =>
        instruction.OpCode.Code == CilCode.Ldarg_1
        || instruction.OpCode.Code is CilCode.Ldarg or CilCode.Ldarg_S
            && ReferenceEquals(instruction.Operand, parameter);

    private static IEnumerable<MethodDefinition> EnumerateMethods(IEnumerable<AssemblyDefinition> assemblies)
    {
        foreach (var assembly in assemblies)
        foreach (var module in assembly.Modules)
        foreach (var type in module.TopLevelTypes)
        foreach (var nestedType in EnumerateTypes(type))
        foreach (var method in nestedType.Methods)
            yield return method;
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
        foreach (var descendant in EnumerateTypes(nested))
            yield return descendant;
    }
}
