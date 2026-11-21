using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

/// <summary>
/// Finds exception throw helper functions. These eventually look up the type they throw by name
/// (via <c>Class::FromName(corlib, "System", "NullReferenceException")</c>), so the type name appears
/// as a plain C string in the helper's body.
/// </summary>
public static class ThrowHelperRecovery
{
    private const int MaxDepth = 5;
    private const int MaxRaiserDepth = 3;
    private const int MaxStringLength = 64;

    public static TypeAnalysisContext? GetThrownException(ApplicationAnalysisContext appContext, ulong address)
    {
        if (!appContext.ThrowHelperNamesByAddress.TryGetValue(address, out var name))
        {
            name = ResolveName(appContext, address, 0, []);
            appContext.ThrowHelperNamesByAddress.TryAdd(address, name);
        }

        if (name == null)
            return null;

        var type = appContext.LibMC2ILContext.ReflectionCache.GetType(name);

        return type == null ? null : appContext.ResolveContextForType(type);
    }

    // Whether the provided method raises whatever exception it is handed
    // e.g. il2cpp_codegen_raise_exception, il2cpp_codegen_rethrow_exception, vm::Exception::Raise
    public static bool IsExceptionRaiser(ApplicationAnalysisContext appContext, ulong address)
    {
        if (address == 0)
            return false;

        if (appContext.ExceptionRaisersByAddress.TryGetValue(address, out var cached))
            return cached;

        var raise = appContext.GetOrCreateKeyFunctionAddresses().il2cpp_vm_exception_raise;

        if (raise == 0)
            return false;

        bool result;
        if (appContext.InstructionSet is ArmV7InstructionSet)
        {
            result = ReachesCall(appContext, address, raise, 0, []);
        }
        else
        {
            // Some helpers go directly to the native throw routine instead of Exception::Raise.
            var nativeThrow = appContext.InstructionSet.InspectPotentialThrowHelper(appContext, raise).CallTargets.LastOrDefault();
            result = nativeThrow != 0 && ReachesCall(appContext, address, nativeThrow, 0, []);
        }

        appContext.ExceptionRaisersByAddress[address] = result;
        return result;
    }

    internal static bool IsArm64InvalidCastRaiser(ApplicationAnalysisContext appContext, ulong address)
    {
        if (appContext.InstructionSet is not NewArmV8InstructionSet arm64)
            return false;

        var raise = appContext.GetOrCreateKeyFunctionAddresses().il2cpp_vm_exception_raise;
        if (raise == 0)
            return false;

        // The native helper calls an exception factory followed immediately by a raiser.
        // Looking for both avoids treating a factory that merely returns an exception as a throw.
        var calls = arm64.InspectPotentialThrowHelper(appContext, address, true).CallTargets;
        for (var i = 1; i < calls.Count; i++)
        {
            if (GetThrownException(appContext, calls[i - 1])?.FullName == "System.InvalidCastException"
                && (calls[i] == raise
                    || arm64.InspectPotentialThrowHelper(appContext, calls[i], true).CallTargets.Contains(raise)))
                return true;
        }

        return false;
    }

    private static bool ReachesCall(ApplicationAnalysisContext appContext, ulong address, ulong wanted, int depth, HashSet<ulong> visited)
    {
        if (address == wanted)
            return true;

        if (depth >= MaxRaiserDepth || !visited.Add(address))
            return false;

        var callTargets = appContext.InstructionSet is ArmV7InstructionSet
            ? ArmV7InstructionSet.GetStraightLineCallTargets(appContext, address)
            : appContext.InstructionSet.InspectPotentialThrowHelper(appContext, address).CallTargets;

        return callTargets.Any(target => ReachesCall(appContext, target, wanted, depth + 1, visited));
    }

    private static string? ResolveName(ApplicationAnalysisContext appContext, ulong address, int depth, HashSet<ulong> path)
    {
        if (address == 0 || depth >= MaxDepth || !path.Add(address))
            return null;

        var (dataReferences, callTargets) = appContext.InstructionSet is NewArmV8InstructionSet arm64
            ? arm64.InspectPotentialThrowHelper(appContext, address, true)
            : appContext.InstructionSet.InspectPotentialThrowHelper(appContext, address);

        var name = FindExceptionName(appContext, dataReferences);

        if (name == null)
        {
            foreach (var target in callTargets)
            {
                name = ResolveName(appContext, target, depth + 1, path);

                if (name != null)
                    break;
            }
        }

        path.Remove(address);
        return name;
    }

    private static string? FindExceptionName(ApplicationAnalysisContext appContext, IReadOnlyList<ulong> dataReferences)
    {
        foreach (var address in dataReferences)
            if (ReadCStringAtVirtualAddress(appContext, address) is { } text && text.EndsWith("Exception", StringComparison.Ordinal))
                return text;

        return null;
    }

    //TODO didn't we have a helper for this somewhere? Can't find it. Maybe got deleted. Maybe it's just too late
    internal static string? ReadCStringAtVirtualAddress(ApplicationAnalysisContext appContext, ulong address, int maxLength = MaxStringLength)
    {
        long offset;

        try
        {
            offset = appContext.Binary.MapVirtualAddressToRaw(address, false);
        }
        catch
        {
            return null;
        }

        if (offset <= 0)
            return null;

        var content = appContext.Binary.GetRawBinaryContent();
        var end = offset;

        while (end < content.Length && end - offset < maxLength && content[(int)end] != 0)
        {
            var c = content[(int)end];

            if (c < 32 || c >= 127)
                return null;

            end++;
        }

        if (end == offset || end >= content.Length || content[(int)end] != 0)
            return null;

        var characters = new char[end - offset];

        for (var i = 0; i < characters.Length; i++)
            characters[i] = (char)content[(int)offset + i];

        return new string(characters);
    }
}
