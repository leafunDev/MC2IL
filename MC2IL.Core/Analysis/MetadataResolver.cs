using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using MC2IL.Core.Extensions;
using MC2IL.Core.Graphs;
using MC2IL.Core.Il2CppApiFunctions;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;
using LibMC2IL;

namespace MC2IL.Core.Analysis;

public static class MetadataResolver
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private const int MaxNativeStringByteLength = 1024 * 1024;

    public static void ResolveAll(MethodAnalysisContext method)
    {
        ResolveStringLiteralAccessors(method);
        ResolveCalls(method);
        ResolveGetter(method);
        ResolveIndirectElfMetadataUsages(method);
        ResolveMetadataUsages(method);
        // Some string wrapper calls are identified by ResolveCalls as key functions first, and
        // their literal arguments only become obvious once metadata-usage loads are rewritten.
        ResolveStringLiteralAccessors(method);
        ResolveStaticAutoPropertyAccessors(method);
    }

    private static void ResolveStringLiteralAccessors(MethodAnalysisContext method)
    {
        var libContext = method.AppContext.LibMC2ILContext;
        var instructions = method.ControlFlowGraph!.Instructions;
        var keyFunctions = method.AppContext.GetOrCreateKeyFunctionAddresses();
        var stringAccessorAddresses = new HashSet<ulong>
        {
            keyFunctions.il2cpp_string_new_wrapper,
            keyFunctions.il2cpp_vm_string_newWrapper,
            keyFunctions.il2cpp_codegen_string_new_wrapper,
        };
        stringAccessorAddresses.Remove(0);
        var stringAccessorNames = new HashSet<string>
        {
            nameof(keyFunctions.il2cpp_string_new_wrapper),
            nameof(keyFunctions.il2cpp_vm_string_newWrapper),
            nameof(keyFunctions.il2cpp_codegen_string_new_wrapper),
        };

        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        foreach (var instruction in instructions)
        {
            if (instruction is not { OpCode: OpCode.Call, Operands: [var targetOperand, LocalVariable result, ..] }
                || !IsStringAccessor(targetOperand))
                continue;

            for (var i = 2; i < instruction.Operands.Count; i++)
            {
                if (ResolveStringLiteral(instruction.Operands[i], definitions, new HashSet<LocalVariable>()) is not { } literal)
                    continue;

                instruction.OpCode = OpCode.Move;
                instruction.SetOperands(result, new StringLiteral(literal));
                break;
            }
        }

        bool IsStringAccessor(IOperand target) => target switch
        {
            Immediate address => stringAccessorAddresses.Contains(address.UnsignedValue),
            StringLiteral name => stringAccessorNames.Contains(name.Value),
            _ => false,
        };

        string? ResolveStringLiteral(IOperand operand, Dictionary<LocalVariable, Instruction> localDefinitions,
            HashSet<LocalVariable> visited) => operand switch
        {
            StringLiteral literal => literal.Value,
            Immediate address => ResolveStringAtAddress(libContext, address.UnsignedValue),
            MemoryOperand { Base: null, Index: null, Scale: 0 } memory
                => ResolveStringAtAddress(libContext, unchecked((ulong)memory.Addend)),
            LocalVariable local when visited.Add(local)
                && localDefinitions.TryGetValue(local, out var definition)
                && definition is { OpCode: OpCode.Move, Operands: [_, var source] }
                => ResolveStringLiteral(source, localDefinitions, visited),
            _ => null,
        };

    }

    private static string? ResolveStringAtAddress(LibMC2ILContext libContext, ulong address)
        => ResolveMetadataGlobalAtAddress(libContext, address) is { Type: MetadataUsageType.StringLiteral, IsValid: true } literal
            ? literal.AsLiteral()
            : ReadNativeUtf8String(libContext, address);

    private static string? ReadNativeUtf8String(LibMC2ILContext libContext, ulong address)
    {
        var binary = libContext.Binary;
        if (!binary.TryMapVirtualAddressToRaw(address, out var rawAddress) || rawAddress < 0 || rawAddress >= binary.RawLength)
            return null;

        var rawBinary = binary.GetRawBinaryContent();
        if (rawAddress >= rawBinary.Length)
            return null;

        var remainingLength = rawBinary.Length - (int)rawAddress;
        var scanLength = Math.Min(remainingLength, MaxNativeStringByteLength + 1);
        var bytes = rawBinary.Slice((int)rawAddress, scanLength);
        var terminator = bytes.IndexOf((byte)0);
        if (terminator < 0 || terminator > MaxNativeStringByteLength)
            return null;

        try
        {
            return StrictUtf8.GetString(bytes.Slice(0, terminator).ToArray());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves <c>Move local, [absoluteAddress]</c> loads, direct string call arguments, and
    /// delegate method-pointer arguments that refer to IL2CPP metadata-usage globals into typed operands: a
    /// string literal, a <see cref="TypeAnalysisContext"/> (an Il2CppType*/
    /// Il2CppClass* usage) or, for a MethodInfo* usage, a <see cref="RuntimeMethodInfoAnalysisContext"/>
    /// naming the method it refers to (also used to type the local - see <see cref="LocalVariables"/>),
    /// or likewise a <see cref="RuntimeFieldInfoAnalysisContext"/> for a FieldInfo* usage.
    /// </summary>
    private static void ResolveMetadataUsages(MethodAnalysisContext method)
    {
        var libContext = method.AppContext.LibMC2ILContext;
        var instructions = method.ControlFlowGraph!.Instructions;
        var delegateMethodPointerLocals = new HashSet<LocalVariable>();
        var stringArgumentLocals = FindStringArgumentLocals(instructions);

        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
                if (IsDelegateMethodPointerArgument(instruction, i)
                    && instruction.Operands[i] is LocalVariable local)
                    delegateMethodPointerLocals.Add(local);
        }

        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                // The operand-zero slot of a Move is its destination, not a memory load.
                if (instruction.OpCode == OpCode.Move && i == 0)
                    continue;

                var isLocalMoveSource = instruction.OpCode == OpCode.Move
                    && i == 1
                    && instruction.Operands[0] is LocalVariable;
                var isDelegateMethodPointer = IsDelegateMethodPointerArgument(instruction, i)
                    || isLocalMoveSource
                    && instruction.Operands[0] is LocalVariable destination
                    && delegateMethodPointerLocals.Contains(destination);
                if (!isLocalMoveSource && !isDelegateMethodPointer)
                    continue;

                var operand = instruction.Operands[i];
                var address = operand switch
                {
                    MemoryOperand { Base: null, Index: null, Scale: 0 } memory => (ulong)memory.Addend,
                    // An immediate metadata address is meaningful only as a Move source. Other
                    // immediates are ordinary numbers and must not be reinterpreted as pointers.
                    Immediate immediate when isLocalMoveSource || isDelegateMethodPointer => immediate.UnsignedValue,
                    _ => 0ul,
                };

                if (address == 0)
                    continue;

                // Writing an immediate to Wn constructs a 32-bit value, often the first
                // fragment of a MOVZ/MOVK sequence. An incidental match in the binary must
                // not replace that fragment with a metadata handle before constant folding.
                // Explicit string and delegate pointer uses still have their own resolution.
                var isArm64WordLiteral = operand is Immediate && isLocalMoveSource
                    && instruction.OperandWidth == 32
                    && method.AppContext.InstructionSet is NewArmV8InstructionSet;

                // ARM64 ELF commonly reaches metadata usage slots through a GOT entry. Look
                // through exactly one pointer, and only accept it when the target independently
                // decodes as a valid metadata usage.
                var metadataGlobal = isArm64WordLiteral
                    ? null
                    : ResolveMetadataGlobalAtAddress(libContext, address, isLocalMoveSource);

                // String/type/field globals are handled for the established Move-local pattern.
                // A direct delegate pointer argument is only meaningful as a method metadata global.
                if (isLocalMoveSource)
                {
                    var stringLiteral = metadataGlobal is { Type: MetadataUsageType.StringLiteral, IsValid: true } stringUsage
                        ? stringUsage.AsLiteral()
                        : null;
                    if (stringLiteral == null
                        && instruction.Operands[0] is LocalVariable stringDestination
                        && stringArgumentLocals.Contains(stringDestination))
                        stringLiteral = ReadNativeUtf8String(libContext, address);

                    if (stringLiteral != null)
                    {
                        instruction.SetOperand(i, new StringLiteral(stringLiteral));
                        continue;
                    }

                    // A type from a GOT slot requires a second dereference. Resolve that
                    // instruction separately; assigning the class type here loses one level.
                    if (!isArm64WordLiteral && libContext.GetTypeGlobalByAddress(address) is { } typeGlobal)
                    {
                        instruction.SetOperand(i, method.AppContext.ResolveIl2CppType(typeGlobal));
                        continue;
                    }
                }

                // Method metadata usage (MethodInfo*). On metadata v27+ GetMethodGlobalByAddress can return
                // any global, so confirm it is actually a method before resolving - the resolver's switch
                // throws on other usage kinds.
                var methodUsage = metadataGlobal;
                if (methodUsage?.Type is MetadataUsageType.MethodDef or MetadataUsageType.MethodRef
                    && method.AppContext.ResolveContextForMethod(methodUsage) is { DeclaringType: { } methodDeclaringType } methodContext)
                {
                    instruction.SetOperand(i, new RuntimeMethodInfoAnalysisContext(methodContext, methodDeclaringType.DeclaringAssembly));
                    continue;
                }

                // Some IL2CPP versions pass a raw native method pointer to Delegate::.ctor instead
                // of a metadata-usage global. Resolve a direct pointer or one/two pointer indirections
                // only for the delegate's IntPtr argument, and accept it only when it maps uniquely
                // to a known managed method.
                if (isDelegateMethodPointer
                    && ResolveDelegateMethodPointer(method, address) is { DeclaringType: { } targetDeclaringType } targetMethod)
                {
                    instruction.SetOperand(i, new RuntimeMethodInfoAnalysisContext(targetMethod, targetDeclaringType.DeclaringAssembly));
                    continue;
                }

                // Field metadata usage (FieldInfo*), e.g. the RuntimeFieldHandle passed to InitializeArray.
                if (isLocalMoveSource
                    && metadataGlobal is { Type: MetadataUsageType.FieldInfo, IsValid: true } fieldUsage
                    && method.AppContext.ResolveContextForField(fieldUsage.AsField()) is { DeclaringType.DeclaringAssembly: { } fieldAssembly } fieldContext)
                    instruction.SetOperand(i, new RuntimeFieldInfoAnalysisContext(fieldContext, fieldAssembly));
            }
        }

        HashSet<LocalVariable> FindStringArgumentLocals(List<Instruction> allInstructions)
        {
            var definitions = new Dictionary<LocalVariable, Instruction>();
            foreach (var candidate in allInstructions)
                if (candidate.Destination is LocalVariable destination)
                    definitions[destination] = candidate;

            var stringLocals = new HashSet<LocalVariable>();
            var pending = new Queue<LocalVariable>();
            foreach (var candidate in allInstructions)
            {
                if (!candidate.IsCall || candidate.Operands[0] is not MethodAnalysisContext target)
                    continue;

                var parameterStart = candidate.OpCode == OpCode.Call
                    ? (target.IsStatic ? 2 : 3)
                    : (target.IsStatic ? 1 : 2);
                for (var parameterIndex = 0; parameterIndex < target.Parameters.Count; parameterIndex++)
                {
                    if (target.Parameters[parameterIndex].ParameterType.FullName != "System.String")
                        continue;

                    var operandIndex = parameterStart + parameterIndex;
                    if (operandIndex >= candidate.Operands.Count)
                        continue;

                    if (candidate.Operands[operandIndex] is LocalVariable argument)
                    {
                        if (stringLocals.Add(argument))
                            pending.Enqueue(argument);
                    }
                    else if (TryGetAbsoluteAddress(candidate.Operands[operandIndex], out var address)
                        && ResolveStringAtAddress(libContext, address) is { } literal)
                    {
                        candidate.SetOperand(operandIndex, new StringLiteral(literal));
                    }
                }
            }

            while (pending.Count > 0)
            {
                var local = pending.Dequeue();
                if (!definitions.TryGetValue(local, out var definition))
                    continue;

                if (definition.OpCode == OpCode.Move && definition.Operands.Count > 1
                    && definition.Operands[1] is LocalVariable moveSource && stringLocals.Add(moveSource))
                    pending.Enqueue(moveSource);

                if (definition.OpCode == OpCode.Phi)
                    foreach (var phiSource in definition.Operands.Skip(1).OfType<LocalVariable>())
                        if (stringLocals.Add(phiSource))
                            pending.Enqueue(phiSource);
            }

            return stringLocals;
        }

        static bool TryGetAbsoluteAddress(IOperand operand, out ulong address)
        {
            switch (operand)
            {
                case MemoryOperand { Base: null, Index: null, Scale: 0 } memory:
                    address = unchecked((ulong)memory.Addend);
                    return address != 0;
                case Immediate immediate when immediate.UnsignedValue != 0:
                    address = immediate.UnsignedValue;
                    return true;
                default:
                    address = 0;
                    return false;
            }
        }
    }

    private static MetadataUsage? ResolveMetadataGlobalAtAddress(LibMC2ILContext libContext, ulong address, bool followIndirection = true)
    {
        var direct = libContext.GetAnyGlobalByAddress(address);
        if (direct != null && IsValidMetadataUsage(libContext, direct))
            return direct;

        if (!followIndirection)
            return null;

        // A GOT slot can contain the address of the actual metadata usage slot. Arbitrary native
        // pointers remain untouched because the pointed-to address must decode as a valid usage.
        var binary = libContext.Binary;
        if (!binary.TryMapVirtualAddressToRaw(address, out var rawAddress)
            || rawAddress < 0
            || rawAddress + binary.PointerSizeBytes > binary.RawLength)
            return null;

        var indirectAddress = binary.ReadPointerAtVirtualAddress(address);
        if (indirectAddress == 0 || indirectAddress == address)
            return null;

        var indirect = libContext.GetAnyGlobalByAddress(indirectAddress);
        return indirect != null && IsValidMetadataUsage(libContext, indirect) ? indirect : null;
    }

    private static bool IsValidMetadataUsage(LibMC2ILContext libContext, MetadataUsage usage)
        => usage.IsValid
            || usage.Type == MetadataUsageType.MethodRef
            && usage.RawValue < libContext.Metadata.AllGenericMethodSpecs.Length;

    private static void ResolveIndirectElfMetadataUsages(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not LibMC2IL.Elf.ElfFile binary
            || method.AppContext.LibMC2ILContext is not { Metadata.MetadataVersion: >= 27 } libContext)
            return;

        // An ELF GOT load returns the address of the metadata slot. Resolve its second load
        // before the absolute-load pass, while the original slot address is still available.
        // This gives MethodInfo::klass the loaded MethodInfo, rather than the address of its slot.
        var instructions = method.ControlFlowGraph!.Instructions;
        var slots = new Dictionary<LocalVariable, ulong>();
        foreach (var instruction in instructions)
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable result,
                    MemoryOperand { IsConstant: true, Addend: > 0 } memory] })
                slots[result] = (ulong)memory.Addend;

        foreach (var instruction in instructions)
        {
            if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable,
                    MemoryOperand { Base: LocalVariable slot, Index: null, Scale: 0, Addend: 0 }] }
                || !slots.TryGetValue(slot, out var address)
                || !IsReadablePointer(address))
                continue;

            var pointer = binary.ReadPointerAtVirtualAddress(address);
            if (pointer == 0 || !IsReadablePointer(pointer))
                continue;

            var usage = libContext.GetAnyGlobalByAddress(pointer);
            if (usage is { Type: MetadataUsageType.StringLiteral, IsValid: true })
                instruction.SetOperand(1, new StringLiteral(usage.AsLiteral()));
            else if (usage is { Type: MetadataUsageType.TypeInfo or MetadataUsageType.Type, IsValid: true })
                instruction.SetOperand(1, method.AppContext.ResolveIl2CppType(usage.AsType()));
            else if (usage is { Type: MetadataUsageType.MethodDef or MetadataUsageType.MethodRef }
                && IsValidMetadataUsage(libContext, usage)
                && instruction.MemoryAccessSize == binary.PointerSizeBytes
                && method.AppContext.ResolveContextForMethod(usage) is { DeclaringType: { } declaringType } target)
                instruction.SetOperand(1, new RuntimeMethodInfoAnalysisContext(target, declaringType.DeclaringAssembly));
        }

        bool IsReadablePointer(ulong address) => address % (ulong)binary.PointerSizeBytes == 0
            && binary.TryMapVirtualAddressToRaw(address, out var raw)
            && raw >= 0 && raw <= binary.RawLength - binary.PointerSizeBytes;
    }

    private static bool IsDelegateMethodPointerArgument(Instruction instruction, int operandIndex)
    {
        if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid)
            || instruction.Operands[0] is not MethodAnalysisContext
            {
                Name: ".ctor",
                DeclaringType: { } delegateType
            } constructor
            || !(delegateType.IsDelegate || delegateType.BaseType?.FullName == "System.MulticastDelegate"))
            return false;

        var receiverIndex = instruction.OpCode == OpCode.CallVoid ? 1 : 2;
        var parameterIndex = operandIndex - receiverIndex - 1;
        return parameterIndex >= 0
            && parameterIndex < constructor.Parameters.Count
            && constructor.Parameters[parameterIndex].ParameterType.FullName == "System.IntPtr";
    }

    private static MethodAnalysisContext? ResolveDelegateMethodPointer(MethodAnalysisContext method, ulong address)
    {
        var appContext = method.AppContext;
        var binary = appContext.Binary;
        if (FindUniqueMethod(address) is { } directMethod)
            return directMethod;
        if (FindUniqueMethod(address & ~1ul) is { } thumbMethod)
            return thumbMethod;

        var pointerLocation = address;
        var visited = new HashSet<ulong>();
        for (var depth = 0; depth < 2; depth++)
        {
            if (!visited.Add(pointerLocation)
                || !binary.TryMapVirtualAddressToRaw(pointerLocation, out var rawAddress)
                || rawAddress < 0
                || rawAddress + binary.PointerSizeBytes > binary.RawLength)
                return null;

            var pointer = binary.ReadPointerAtVirtualAddress(pointerLocation);
            if (pointer == 0)
                return null;

            if (FindUniqueMethod(pointer) is { } resolvedMethod)
                return resolvedMethod;
            if (FindUniqueMethod(pointer & ~1ul) is { } resolvedThumbMethod)
                return resolvedThumbMethod;

            pointerLocation = pointer;
        }

        return null;

        MethodAnalysisContext? FindUniqueMethod(ulong pointer)
        {
            if (pointer == 0
                || !appContext.MethodsByAddress.TryGetValue(pointer, out var candidates))
                return null;

            var uniqueCandidates = candidates.Distinct().Take(2).ToList();
            return uniqueCandidates.Count == 1 ? uniqueCandidates[0] : null;
        }
    }

    /// <summary>
    /// Replaces every <c>[base + addend]</c> memory operand whose base is a typed local with a
    /// <see cref="FieldReference"/> to the field at that offset. Returns whether any operand was
    /// resolved this pass, so the type/field fixpoint can detect convergence: as more bases become
    /// typed (a field load types its result, which is the base of the next load), more offsets
    /// resolve, so this is re-run until it stops finding new fields.
    /// </summary>
    public static bool ResolveFieldOffsets(MethodAnalysisContext method)
        => ResolveFieldOffsets(method, method.ControlFlowGraph!.Instructions);

    internal static bool ResolveFieldOffsets(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var changed = false;

        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (operand is not MemoryOperand memory)
                    continue;

                // Has to be [base (local) + addend (field offset)]
                if (memory.Index != null || memory.Scale != 0)
                    continue;

                if (memory.Base is not LocalVariable local || local?.Type == null)
                    continue;

                // check if static field access
                var staticOwner = (local.Type as StaticFieldStorageTypeAnalysisContext)?.OwnerType;
                var owner = staticOwner ?? local.Type;
                var pointerSize = method.AppContext.Binary.PointerSizeBytes;
                var lookupOffset = memory.Addend;
                var valueTypeBase = staticOwner == null && owner.IsValueType;

                // A by-ref value-type parameter points at the struct's first data byte; its
                // metadata offsets still describe fields on the value type itself. A by-ref
                // reference-type parameter points at a reference slot, so do not treat it as an
                // object base here.
                if (staticOwner == null && owner is ByRefTypeAnalysisContext byRefOwner)
                {
                    if (!byRefOwner.ElementType.IsValueType)
                        continue;

                    owner = byRefOwner.ElementType;
                    valueTypeBase = true;
                }

                var genericOwner = owner as GenericInstanceTypeAnalysisContext;
                // The reflection layer already unboxes metadata offsets of value-type
                // instance fields. Only the reconstructed generic layout uses boxed offsets.
                var genericLookupOffset = valueTypeBase ? lookupOffset + 2L * pointerSize : lookupOffset;

                FieldAnalysisContext? field;
                if (genericOwner != null && staticOwner == null)
                {
                    // Metadata has all-0 offsets for generic definitions, so recompute layout
                    // using the closed instance where the generic arguments are known.
                    field = GenericInstanceFieldLayout.FindFieldAtOffset(genericOwner, genericLookupOffset);
                }
                else if (staticOwner == null && owner.GenericParameters.Count > 0)
                {
                    field = GenericInstanceFieldLayout.FindFieldAtOffset(owner, genericLookupOffset);
                }
                else
                {
                    // an inherited field exists on the base type but sits at the same offset in the
                    // derived layout, so the whole chain is searched
                    field = null;
                    for (var candidateOwner = genericOwner?.GenericType ?? owner; candidateOwner != null && field == null; candidateOwner = candidateOwner.BaseType)
                    {
                        if (staticOwner == null && candidateOwner is GenericInstanceTypeAnalysisContext genericBase)
                        {
                            // A derived non-generic type can inherit fields from a closed generic
                            // base. Its generic definition has erased field offsets, so resolve the
                            // access using the concrete base layout instead of scanning that definition.
                            field = GenericInstanceFieldLayout.FindFieldAtOffset(genericBase, lookupOffset);
                        }
                        else
                        {
                            field = candidateOwner.Fields.FirstOrDefault(f => f.IsStatic == (staticOwner != null)
                                && (f.Attributes & FieldAttributes.Literal) == 0 // consts have no storage but their metadata offset is 0, which would match
                                && f.BackingData?.FieldOffset == lookupOffset);
                        }
                    }
                }

                List<FieldAnalysisContext>? fieldPath;
                if (field != null)
                {
                    fieldPath = [field];
                    if (instruction.MemoryAccessSize is { } accessSize
                        && field.FieldType.IsValueType
                        && accessSize < TypeSizes.UnboxedSize(field.FieldType, pointerSize)
                        && field.BackingData?.FieldOffset is { } fieldOffset
                        && FindInlineValueFieldPath(field.FieldType, lookupOffset - fieldOffset, pointerSize,
                            new HashSet<TypeAnalysisContext>()) is { } nestedPath
                        && TypeSizes.UnboxedSize(nestedPath[^1].FieldType, pointerSize) == accessSize)
                    {
                        fieldPath.AddRange(nestedPath);
                    }
                }
                else if (staticOwner != null
                         && instruction.MemoryAccessSize is { } staticAccessSize
                         && FindNestedStaticFieldPath(owner, lookupOffset, staticAccessSize, pointerSize) is { } staticPath)
                {
                    fieldPath = staticPath;
                }
                else
                {
                    fieldPath = staticOwner == null && genericOwner == null
                        ? FindNestedFieldPath(owner, lookupOffset, pointerSize)
                        : null;
                }

                if (fieldPath == null)
                    continue;

                // Make sure we have a full GIT for field access. Open generic fields are not valid
                // descriptors. Generic value-type layouts remain unresolved because their field
                // offsets cannot be reconstructed safely from metadata alone.
                if (genericOwner != null
                    && fieldPath[0] is not ConcreteGenericFieldAnalysisContext
                    && ReferenceEquals(fieldPath[0].DeclaringType, genericOwner.GenericType))
                    fieldPath[0] = new ConcreteGenericFieldAnalysisContext(fieldPath[0], genericOwner);

                FieldReference? resolved = null;
                for (var pathIndex = 0; pathIndex < fieldPath.Count; pathIndex++)
                {
                    var pathField = fieldPath[pathIndex];
                    var pathOffset = pathIndex == fieldPath.Count - 1 ? (int)memory.Addend : pathField.Offset;
                    resolved = new FieldReference(pathField, local, pathOffset, resolved);
                }

                instruction.SetOperand(i, resolved!);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Finds a field inside an inline value-type field, returning the complete path from the
    /// original reference type to the innermost field. BackingData.FieldOffset is already
    /// relative to the unboxed data for value-type instance fields.
    /// </summary>
    private static List<FieldAnalysisContext>? FindNestedFieldPath(TypeAnalysisContext owner, long targetOffset, int pointerSize)
    {
        List<FieldAnalysisContext>? match = null;

        for (var candidateOwner = owner; candidateOwner != null; candidateOwner = candidateOwner.BaseType)
        {
            foreach (var field in candidateOwner.Fields)
            {
                if (field.IsStatic || (field.Attributes & FieldAttributes.Literal) != 0
                    || field.BackingData?.FieldOffset is not { } fieldOffset
                    || !field.FieldType.IsValueType
                    || targetOffset <= fieldOffset)
                    continue;

                var nestedOffset = targetOffset - fieldOffset;
                if (FindInlineValueFieldPath(field.FieldType, nestedOffset, pointerSize,
                        new HashSet<TypeAnalysisContext>()) is not { } nestedPath)
                    continue;

                nestedPath.Insert(0, field);

                // An explicit-layout union may overlap inline structs too. Keep the access
                // unresolved when more than one source-level path fits the same byte offset.
                if (match != null)
                    return null;

                match = nestedPath;
            }
        }

        return match;
    }

    private static List<FieldAnalysisContext>? FindNestedStaticFieldPath(TypeAnalysisContext owner,
        long targetOffset, int accessSize, int pointerSize)
    {
        List<FieldAnalysisContext>? match = null;
        foreach (var candidateOwner in EnumerateTypeHierarchy(owner))
        foreach (var field in candidateOwner.Fields)
        {
            if (!field.IsStatic || (field.Attributes & FieldAttributes.Literal) != 0
                || field.BackingData?.FieldOffset is not { } fieldOffset
                || !field.FieldType.IsValueType || targetOffset < fieldOffset)
                continue;

            var nestedOffset = targetOffset - fieldOffset;
            if (FindInlineValueFieldPath(field.FieldType, nestedOffset, pointerSize,
                    new HashSet<TypeAnalysisContext>()) is not { } nestedPath
                || TypeSizes.UnboxedSize(nestedPath[^1].FieldType, pointerSize) != accessSize)
                continue;

            nestedPath.Insert(0, field);
            if (match != null)
                return null;
            match = nestedPath;
        }

        return match;
    }

    private static IEnumerable<TypeAnalysisContext> EnumerateTypeHierarchy(TypeAnalysisContext owner)
    {
        for (var current = owner; current != null; current = current.BaseType)
            yield return current;
    }

    private static List<FieldAnalysisContext>? FindInlineValueFieldPath(TypeAnalysisContext valueType, long targetOffset,
        int pointerSize, HashSet<TypeAnalysisContext> visited)
    {
        var valueSize = TypeSizes.UnboxedSize(valueType, pointerSize);
        if (!visited.Add(valueType) || valueSize <= 0 || targetOffset < 0 || targetOffset >= valueSize)
            return null;

        List<FieldAnalysisContext>? match = null;

        for (var candidateOwner = valueType; candidateOwner != null; candidateOwner = candidateOwner.BaseType)
        {
            foreach (var field in candidateOwner.Fields)
            {
                if (field.IsStatic || (field.Attributes & FieldAttributes.Literal) != 0
                    || field.BackingData?.FieldOffset is not { } fieldOffset)
                    continue;

                if (fieldOffset < 0 || targetOffset < fieldOffset)
                    continue;

                List<FieldAnalysisContext>? candidatePath;
                if (targetOffset == fieldOffset)
                {
                    candidatePath = [field];
                }
                else if (field.FieldType.IsValueType
                         && FindInlineValueFieldPath(field.FieldType, targetOffset - fieldOffset, pointerSize,
                             new HashSet<TypeAnalysisContext>(visited)) is { } nestedPath)
                {
                    nestedPath.Insert(0, field);
                    candidatePath = nestedPath;
                }
                else
                {
                    continue;
                }

                // Explicit-layout unions can have multiple fields at the same offset. Leave those
                // ambiguous rather than choosing a misleading source-level field.
                if (match != null)
                    return null;

                match = candidatePath;
            }
        }

        return match;
    }

    private static void ResolveCalls(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        List<Instruction>? instructions = null;
        var throwTopologyChanged = false;

        foreach (var block in cfg.Blocks)
        {
            if (block.BlockType != BlockType.Call && block.BlockType != BlockType.TailCall)
                continue;

            var callInstruction = block.Instructions[^1];
            if (callInstruction.Operands[0] is not Immediate dest)
                continue;

            var target = dest.UnsignedValue;

            var keyFunctionAddresses = method.AppContext.GetOrCreateKeyFunctionAddresses();

            if (keyFunctionAddresses.IsKeyFunctionAddress(target))
            {
                HandleKeyFunction(method.AppContext, callInstruction, target, keyFunctionAddresses);

                if (target == keyFunctionAddresses.il2cpp_codegen_initialize_runtime_metadata_inline
                    && callInstruction is { OpCode: OpCode.Call, Operands: [_, var initResult, var handle, ..] })
                {
                    callInstruction.OpCode = OpCode.Move;
                    callInstruction.SetOperands(initResult, handle);
                }

                continue;
            }

            //Non-key function call. Try to find a single match
            if (!method.AppContext.MethodsByAddress.TryGetValue(target, out var targetMethods))
            {
                // Not a managed method at all. Exception-name strings are only a hint: native helpers
                // can reference exception types without throwing them, so require either a proven
                // exception-raiser call graph or a returned exception object that is actually thrown.
                if (ThrowHelperRecovery.GetThrownException(method.AppContext, target) is { } thrown)
                {
                    // IL2CPP emits the null and bounds stubs next to one another. An apparent
                    // use of X0 by the next stub is a fallthrough artefact if the first itself
                    // reaches the exception raiser.
                    if (thrown.FullName is "System.NullReferenceException" or "System.IndexOutOfRangeException"
                        && ThrowHelperRecovery.IsExceptionRaiser(method.AppContext, target))
                    {
                        callInstruction.OpCode = OpCode.Throw;
                        callInstruction.SetOperands(thrown);
                        cfg.MarkThrowAsTerminator(callInstruction);
                        throwTopologyChanged = true;
                        continue;
                    }

                    var produced = callInstruction.Destination as LocalVariable;
                    var hasUsedResult = produced != null
                        && (instructions ??= cfg.Instructions).Any(i => i.Sources.Any(s => ReferenceEquals(s, produced)));

                    if (hasUsedResult
                        && IsUsedAsThrownException(method.AppContext, produced!, instructions!, []))
                    {
                        callInstruction.OpCode = OpCode.Newobj;
                        callInstruction.SetOperands(produced!, thrown);
                        continue;
                    }

                    if (!hasUsedResult && ThrowHelperRecovery.IsExceptionRaiser(method.AppContext, target))
                    {
                        callInstruction.OpCode = OpCode.Throw;
                        callInstruction.SetOperands(thrown);
                        cfg.MarkThrowAsTerminator(callInstruction);
                        throwTopologyChanged = true;
                        continue;
                    }
                }

                // Otherwise it may be one of the raisers, which throw the exception they are given
                var raisedIndex = callInstruction.OpCode == OpCode.CallVoid ? 1 : 2;

                if (callInstruction.Operands.Count > raisedIndex && ThrowHelperRecovery.IsExceptionRaiser(method.AppContext, target))
                {
                    var raised = callInstruction.Operands[raisedIndex];

                    callInstruction.OpCode = OpCode.Throw;
                    callInstruction.SetOperands(raised);
                    cfg.MarkThrowAsTerminator(callInstruction);
                    throwTopologyChanged = true;
                }

                continue;
            }

            // Duplicated/Shared method bodies are resolved later in ResolveCallsViaMethodInfo/ResolveAmbiguousCalls.
            if (targetMethods is not [{ } singleTargetMethod])
                continue;

            callInstruction.SetOperand(0, singleTargetMethod);
            singleTargetMethod.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(callInstruction, singleTargetMethod);
        }

        if (throwTopologyChanged)
            cfg.RemoveUnreachableBlocks();
        cfg.MergeCallBlocks();
    }

    private static bool IsUsedAsThrownException(ApplicationAnalysisContext appContext, LocalVariable value,
        IReadOnlyList<Instruction> instructions, HashSet<LocalVariable> visited)
    {
        if (!visited.Add(value))
            return false;

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode == OpCode.Throw && instruction.Operands is [var exception]
                && ReferenceEquals(exception, value))
                return true;

            if (instruction.IsCall
                && instruction.Operands[0] is Immediate { UnsignedValue: var target }
                && instruction.Operands.Count > (instruction.OpCode == OpCode.CallVoid ? 1 : 2)
                && ReferenceEquals(instruction.Operands[instruction.OpCode == OpCode.CallVoid ? 1 : 2], value)
                && ThrowHelperRecovery.GetThrownException(appContext, target) == null
                && ThrowHelperRecovery.IsExceptionRaiser(appContext, target))
                return true;

            if (instruction.Destination is not LocalVariable destination)
                continue;

            var forwardsValue = instruction.OpCode switch
            {
                OpCode.Move => instruction.Operands.Count > 1 && ReferenceEquals(instruction.Operands[1], value),
                OpCode.Phi => instruction.Operands.Skip(1).Any(input => ReferenceEquals(input, value)),
                _ => false,
            };

            if (forwardsValue && IsUsedAsThrownException(appContext, destination, instructions, visited))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves calls whose address maps to more than one method by matching the receiver's known
    /// type against the candidates' declaring types. Runs inside the type/field fixpoint and so
    /// re-fires as receivers become typed - a resolved call types its return value, which can type
    /// the receiver of a further call. Returns whether any call was resolved this pass.
    ///
    /// Conservative by design: it commits only when exactly one non-static candidate's declaring
    /// type matches the receiver's type. Anything still untyped or ambiguous is left for a later
    /// pass, or left unresolved - it never guesses.
    /// </summary>
    public static bool ResolveAmbiguousCalls(MethodAnalysisContext method)
        => ResolveAmbiguousCalls(method, method.ControlFlowGraph!.Instructions);

    internal static bool ResolveAmbiguousCalls(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var changed = false;

        foreach (var instruction in instructions)
        {
            if (!instruction.IsCall)
                continue;

            // A resolved call's target is a method/key-function name; only unresolved ones are still numeric.
            if (instruction.Operands[0] is not Immediate target)
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue(target.UnsignedValue, out var candidates) || candidates.Count < 2)
                continue;

            // e.g. string.Equals and string.op_Equality, identical params, instance type, and bodies are shared
            // we can't differentiate which is being called but it doesn't matter
            if (AreInterchangeable(candidates))
            {
                var preferred = PreferredOf(candidates);
                instruction.SetOperand(0, preferred);
                preferred.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, preferred);
                changed = true;
                continue;
            }

            if (GetReceiver(instruction) is not { Type: { } receiverType } receiver)
                continue;

            // Prefer picking base ctor if we are a ctor
            var callerIsCtor = method.Name == ".ctor" && receiver.IsThis;

            // Handle methods with shared bodies
            var match = default(MethodAnalysisContext);

            for (var type = receiverType; type != null && match == null; type = type.BaseType)
            {
                var matches = candidates.Where(c => !c.IsStatic && IsSameType(c.DeclaringType, type)).ToList();

                if (matches.Count > 1 && callerIsCtor)
                    matches = matches.Where(c => c.Name == ".ctor").ToList();

                if (matches.Count > 1)
                    break;

                match = matches.SingleOrDefault();
            }

            if (match == null)
                continue;

            instruction.SetOperand(0, match);
            match.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, match);
            changed = true;
        }

        return changed;
    }

    private static bool AreInterchangeable(List<MethodAnalysisContext> candidates)
    {
        var first = candidates[0];

        return candidates.All(c => c.IsStatic == first.IsStatic
            && ReferenceEquals(c.DeclaringType, first.DeclaringType)
            && ReferenceEquals(c.ReturnType, first.ReturnType)
            && c.Parameters.Count == first.Parameters.Count
            && SameParameterTypes(c, first));
    }

    internal static bool ResolveUnmappedGetComponentCalls(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var changed = false;

        foreach (var instruction in instructions)
        {
            if (!instruction.IsCall
                || instruction.Operands[0] is not Immediate
                || instruction.Destination is not LocalVariable { Type: { } resultType }
                || !IsUnityComponentType(resultType)
                || GetReceiver(instruction) is not { Type: { } receiverType })
                continue;

            // Generic Unity component lookups are commonly shared by one native implementation,
            // so their call address may not map to a unique managed method. Resolve only the
            // zero-parameter GetComponent<T>() form when both receiver hierarchy and result type
            // prove the constructed generic method.
            var firstArgument = instruction.OpCode == OpCode.Call ? 3 : 2;
            if (instruction.Operands.Count - firstArgument > 1) // the optional extra slot is MethodInfo*
                continue;

            for (var currentType = receiverType; currentType != null; currentType = currentType.BaseType)
            {
                var definition = currentType is GenericInstanceTypeAnalysisContext genericType
                    ? genericType.GenericType
                    : currentType;
                var candidates = definition.Methods.Where(candidate =>
                    candidate is { Name: "GetComponent", IsStatic: false, Parameters.Count: 0, GenericParameters.Count: 1 }
                    && candidate.ReturnType is GenericParameterTypeAnalysisContext { Index: 0 }).ToList();

                if (candidates.Count == 0)
                    continue;
                if (candidates.Count != 1)
                    break;

                var typeArguments = currentType is GenericInstanceTypeAnalysisContext instance
                    ? instance.GenericArguments
                    : [];
                var getComponent = new ConcreteGenericMethodAnalysisContext(candidates[0], typeArguments, [resultType]);
                if (getComponent.ReturnType.FullName != resultType.FullName)
                    break;

                instruction.SetOperand(0, getComponent);
                getComponent.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, getComponent);
                changed = true;
                break;
            }
        }

        return changed;
    }

    private static bool IsUnityComponentType(TypeAnalysisContext type)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (current.FullName == "UnityEngine.Component")
                return true;
        return false;
    }

    private static bool SameParameterTypes(MethodAnalysisContext a, MethodAnalysisContext b)
    {
        for (var i = 0; i < a.Parameters.Count; i++)
        {
            if (!ReferenceEquals(a.Parameters[i].ParameterType, b.Parameters[i].ParameterType))
                return false;
        }

        return true;
    }

    // Prefer operators if possible
    private static MethodAnalysisContext PreferredOf(List<MethodAnalysisContext> candidates) =>
        candidates.FirstOrDefault(c => c.Name.StartsWith("op_")) ?? candidates[0];

    // The receiver ('this') of a call is the first integer-slot argument: operand 1 for CallVoid
    // (after the target), operand 2 for Call (after the target and the return value).
    // A value type receiver is passed byref, so it arrives as an AddressOf over the local.
    private static LocalVariable? GetReceiver(Instruction call)
    {
        var index = call.OpCode == OpCode.CallVoid ? 1 : 2;

        return index < call.Operands.Count
            ? call.Operands[index] switch
            {
                LocalVariable local => local,
                AddressOf { Target: LocalVariable addressed } => addressed,
                _ => null
            }
            : null;
    }

    // Concrete generic method contexts build their declaring type fresh rather than via the
    // GetOrCreate cache, so generic instances also need comparing structurally.
    // TODO Fix this, concrete generic methods should use GetOrCreate
    internal static bool IsSameType(TypeAnalysisContext? a, TypeAnalysisContext? b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a is not GenericInstanceTypeAnalysisContext leftInstance
            || b is not GenericInstanceTypeAnalysisContext rightInstance
            || !ReferenceEquals(leftInstance.GenericType, rightInstance.GenericType)
            || leftInstance.GenericArguments.Count != rightInstance.GenericArguments.Count)
            return false;

        for (var i = 0; i < leftInstance.GenericArguments.Count; i++)
        {
            if (!IsSameType(leftInstance.GenericArguments[i], rightInstance.GenericArguments[i]))
                return false;
        }

        return true;
    }

    private static bool IsSameMethodInstantiation(MethodAnalysisContext left, MethodAnalysisContext right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is not ConcreteGenericMethodAnalysisContext leftGeneric
            || right is not ConcreteGenericMethodAnalysisContext rightGeneric
            || !ReferenceEquals(leftGeneric.BaseMethodContext, rightGeneric.BaseMethodContext)
            || !IsSameType(leftGeneric.DeclaringType, rightGeneric.DeclaringType)
            || leftGeneric.MethodGenericParameters.Count != rightGeneric.MethodGenericParameters.Count)
            return false;

        for (var i = 0; i < leftGeneric.MethodGenericParameters.Count; i++)
            if (!IsSameType(leftGeneric.MethodGenericParameters[i], rightGeneric.MethodGenericParameters[i]))
                return false;
        return true;
    }

    /// <summary>
    /// Resolves any Call (theoretically should always be a CallVoid) target directly after a Newobj to a constructor call.
    /// </summary>
    public static bool ResolveConstructorCalls(MethodAnalysisContext method)
        => ResolveConstructorCalls(method, method.ControlFlowGraph!.Instructions);

    internal static bool ResolveConstructorCalls(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
            if (instruction.Destination is LocalVariable definition)
                definitions[definition] = instruction;

        var changed = false;

        foreach (var instruction in instructions)
        {
            if (!instruction.IsCall)
                continue;

            if (instruction.Operands[0] is ConcreteGenericMethodAnalysisContext { Name: ".ctor" } sharedConstructor
                && GetReceiver(instruction) is { } allocatedReceiver
                && AllocatedType(allocatedReceiver, definitions) is GenericInstanceTypeAnalysisContext instance
                && ReferenceEquals(sharedConstructor.BaseMethodContext.DeclaringType, instance.GenericType)
                && sharedConstructor.DeclaringType?.FullName != instance.FullName)
            {
                var closedConstructor = new ConcreteGenericMethodAnalysisContext(sharedConstructor.BaseMethodContext, instance.GenericArguments, []);
                instruction.SetOperand(0, closedConstructor);
                changed = true;
                continue;
            }

            if (instruction.Operands[0] is not Immediate callTarget)
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue(callTarget.UnsignedValue, out var candidates))
                continue;

            if (GetReceiver(instruction) is not { } receiver || AllocatedType(receiver, definitions) is not { } allocatedType)
                continue;

            var constructor = candidates.FirstOrDefault(c => !c.IsStatic && c.Name == ".ctor" && ReferenceEquals(c.DeclaringType, allocatedType))
                              ?? FindConstructorForSharedBody(allocatedType, candidates);
            if (constructor == null)
                continue;

            instruction.SetOperand(0, constructor);
            constructor.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, constructor);
            changed = true;
        }

        return changed;
    }

    private static MethodAnalysisContext? FindConstructorForSharedBody(TypeAnalysisContext allocatedType, List<MethodAnalysisContext> candidates)
    {
        var candidateParamCounts = new HashSet<int>(candidates
            .Where(c => c is { IsStatic: false, Name: ".ctor" })
            .Select(c => c.Parameters.Count));

        if (candidateParamCounts.Count == 0)
            return null;

        var definition = allocatedType is GenericInstanceTypeAnalysisContext genericInstance ? genericInstance.GenericType : allocatedType;
        var matches = definition.Methods
            .Where(m => m is { IsStatic: false, Name: ".ctor" } && candidateParamCounts.Contains(m.Parameters.Count))
            .ToList();

        if (matches is not [{ } match])
            return null;

        return allocatedType is GenericInstanceTypeAnalysisContext instance
            ? new ConcreteGenericMethodAnalysisContext(match, instance.GenericArguments, [])
            : match;
    }

    // Follow SSA copies from a local back to the Newobj that produced the value
    private static TypeAnalysisContext? AllocatedType(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();

        while (visited.Add(local) && definitions.TryGetValue(local, out var definition))
        {
            switch (definition.OpCode)
            {
                case OpCode.Newobj:
                    return (definition.Operands[0] as LocalVariable)?.Type;
                case OpCode.Move when definition.Operands[1] is LocalVariable source:
                    local = source;
                    continue;
            }

            break;
        }

        return null;
    }

    /// <summary>
    /// Resolves shared or otherwise unmapped direct calls from the concrete hidden
    /// <c>MethodInfo*</c> argument passed by IL2CPP.
    /// </summary>
    public static bool ResolveCallsViaMethodInfo(MethodAnalysisContext method)
        => ResolveCallsViaMethodInfo(method, method.ControlFlowGraph!.Instructions);

    internal static bool ResolveCallsViaMethodInfo(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var changed = false;
        var discardedEntryLoads = new HashSet<Instruction>();
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        foreach (var instruction in instructions)
        {
            var isIndirect = instruction.OpCode == OpCode.IndirectCall;
            if (!instruction.IsCall && !isIndirect)
                continue;

            var callTarget = instruction.Operands[0];
            if (!isIndirect && callTarget is not (Immediate or ConcreteGenericMethodAnalysisContext))
                continue;

            Instruction? entryLoad = null;
            RuntimeMethodInfoAnalysisContext? methodInfo;
            if (isIndirect)
            {
                if (method.AppContext.InstructionSet is not NewArmV8InstructionSet
                    || method.AppContext.MetadataVersion != 29
                    || callTarget is not LocalVariable entry
                    || !definitions.TryGetValue(entry, out entryLoad)
                    || entryLoad is not { OpCode: OpCode.Move, MemoryAccessSize: 8, Operands: [_, MemoryOperand
                        { Base: LocalVariable info, Index: null, Scale: 0, Addend: 0 or 8 }] })
                    continue;

                // The static entry and the hidden final argument must identify the same method.
                methodInfo = ResolveMethodInfo(info, definitions, []);
                if (methodInfo is not { RepresentedMethod.IsStatic: true })
                    continue;
            }
            else
            {
                methodInfo = GetMethodInfoArgument(instruction, definitions);
            }

            if (methodInfo is not { RepresentedMethod: { } representedMethod })
                continue;

            // A recognized MethodInfo can appear elsewhere among raw register arguments. Only
            // trust it when it occupies the hidden final-parameter slot implied by its own
            // signature; that makes it authoritative even when the native body is shared by
            // several generic instantiations or omitted from the address map.
            if (ReferenceEquals(representedMethod, method))
                continue;

            if (callTarget is ConcreteGenericMethodAnalysisContext current
                && (representedMethod is not ConcreteGenericMethodAnalysisContext candidate
                    || !ReferenceEquals(current.BaseMethodContext, candidate.BaseMethodContext)
                    || current.FullName == candidate.FullName))
                continue;

            var firstArg = instruction.OpCode == OpCode.CallVoid ? 1 : 2;
            var hiddenParamIndex = firstArg
                + (representedMethod.AppContext.InstructionSet.CallingConventionResolver?.ReturnsViaHiddenBuffer(representedMethod) == true ? 1 : 0)
                + (representedMethod.IsStatic ? 0 : 1) + representedMethod.Parameters.Count;

            if (hiddenParamIndex >= instruction.Operands.Count
                || ResolveMethodInfo(instruction.Operands[hiddenParamIndex], definitions, []) is not { RepresentedMethod: { } hiddenMethod }
                || !(isIndirect ? IsSameMethodInstantiation(representedMethod, hiddenMethod) : ReferenceEquals(hiddenMethod, representedMethod)))
                continue;

            if (entryLoad != null)
            {
                instruction.OpCode = OpCode.Call;
                discardedEntryLoads.Add(entryLoad);
            }

            instruction.SetOperand(0, representedMethod);
            representedMethod.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, representedMethod);
            changed = true;
        }

        if (discardedEntryLoads.Count != 0)
            DeadCodeEliminator.Run(method.ControlFlowGraph!, discardedEntryLoads);

        return changed;
    }

    // 32-bit Il2CppClass::vtable offset. The 64-bit offset depends on metadata version.
    private const long VTableOffset32 = 0xC0;

    // Resolves virtual dispatch through <c>[klass + vtableOffset + slot * sizeof(VirtualInvokeData)]</c>
    // as long as the klass local's represented type is known.
    public static bool ResolveVirtualCalls(MethodAnalysisContext method)
        => ResolveVirtualCalls(method, method.ControlFlowGraph!.Instructions);

    internal static bool ResolveVirtualCalls(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        var vtableOffset = pointerSize == 8
            ? Il2CppClassUsefulOffsets.GetVtableOffset(method.AppContext.MetadataVersion, is32Bit: false)
            : VTableOffset32;
        var invokeDataSize = 2L * pointerSize;
        var changed = false;
        var homeBlocks = new Dictionary<Instruction, Block>();
        foreach (var block in method.ControlFlowGraph!.Blocks)
            foreach (var instruction in block.Instructions)
                homeBlocks[instruction] = block;

        var loads = new Dictionary<LocalVariable, MemoryOperand>();
        var definitions = new Dictionary<LocalVariable, Instruction>();
        var discardableDispatchLoads = new HashSet<Instruction>();
        foreach (var instruction in instructions)
        {
            if (instruction.Destination is not LocalVariable destination)
                continue;

            definitions[destination] = instruction;
            if (instruction is { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0 } load] })
                loads[destination] = load;
        }

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode is not (OpCode.IndirectCall or OpCode.IndirectJump))
                continue;

            var isTailCall = instruction.OpCode == OpCode.IndirectJump;

            if (SlotLoad(instruction.Operands[0]) is not { } target)
                continue;

            // A generic method RGCTX entry can provide the exact MethodInfo. Its method pointer
            // is the first pointer-sized field, so only resolve a plain +0 load from a typed
            // MethodInfo local; nearby fields (for example the invoker at +8) are different calls.
            if (target is { Index: null, Scale: 0, Addend: 0,
                    Base: LocalVariable { Type: RuntimeMethodInfoAnalysisContext { RepresentedMethod: { } representedMethod } } })
            {
                var callingConventions = representedMethod.AppContext.InstructionSet.CallingConventionResolver;
                if (callingConventions == null || !callingConventions.HasRawArgumentLayout(instruction, representedMethod.AppContext))
                    continue;

                if (isTailCall)
                {
                    RewriteTailCall(instruction, representedMethod);
                    changed = true;
                    continue;
                }

                if (representedMethod.IsVoid)
                    instruction.RemoveOperandAt(1); // discard the raw return-register placeholder

                instruction.OpCode = representedMethod.IsVoid ? OpCode.CallVoid : OpCode.Call;
                instruction.SetOperand(0, representedMethod);
                callingConventions.RemapRawArguments(instruction, representedMethod);
                changed = true;
                continue;
            }

            if (target.Base is not LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: { } receiverType } } klassLocal)
                continue;

            var offset = target.Addend - vtableOffset;
            if (offset < 0 || offset % invokeDataSize != 0)
                continue;

            var slot = (int)(offset / invokeDataSize);
            if (ResolveVTableSlot(method.AppContext, receiverType, slot) is not { } resolved)
                continue;

            var targetOperand = instruction.Operands[0];
            var assembly = resolved.DeclaringType?.DeclaringAssembly ?? method.DeclaringType?.DeclaringAssembly;

            if (isTailCall)
            {
                var callingConventions = resolved.AppContext.InstructionSet.CallingConventionResolver;
                if (callingConventions == null || !callingConventions.HasRawArgumentLayout(instruction, resolved.AppContext))
                    continue;

                RewriteTailCall(instruction, resolved);
            }
            else
            {
                instruction.OpCode = OpCode.Call; // same operand layout as IndirectCall, and we've resolved it now
                instruction.SetOperand(0, resolved);
                resolved.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(instruction, resolved);
            }

            Instruction? methodInfoRead = null;
            // the MethodInfo field is also the same method, name it, for cleanliness and so it can
            // serve as a hidden final parameter if needed
            for (var i = 1; i < instruction.Operands.Count && assembly != null; i++)
            {
                if (SlotLoad(instruction.Operands[i]) is { } methodInfoLoad
                    && ReferenceEquals(methodInfoLoad.Base, klassLocal)
                    && methodInfoLoad.Addend == target.Addend + pointerSize)
                {
                    if (instruction.Operands[i] is LocalVariable info)
                        definitions.TryGetValue(info, out methodInfoRead);
                    instruction.SetOperand(i, new RuntimeMethodInfoAnalysisContext(resolved, assembly));
                }
            }

            // The native call dispatched through this object's vtable. A reference-type
            // callvirt preserves dispatch and the null check performed by the klass load.
            var receiverIndex = instruction.OpCode == OpCode.CallVoid ? 1 : 2;
            if (!receiverType.IsValueType && resolved is { IsVirtual: true, IsStatic: false, DeclaringType.IsValueType: false }
                && instruction.Operands.Count > receiverIndex
                && loads.TryGetValue(klassLocal, out var klassLoad)
                && klassLoad is { Addend: 0, Base: LocalVariable klassReceiver }
                && ReferenceEquals(FollowCopies(instruction.Operands[receiverIndex]), FollowCopies(klassReceiver)))
            {
                instruction.NullCheckReceiver = true;
                var targetRead = targetOperand is LocalVariable pointer
                    && definitions.TryGetValue(pointer, out var pointerRead) ? pointerRead : null;
                if (instruction.Operands.Skip(receiverIndex + 1).Take(resolved.Parameters.Count)
                    .All(argument => argument is LocalVariable or Immediate or StringLiteral or FloatLiteral or DoubleLiteral))
                    CollectDispatchLoads(instruction, definitions[klassLocal], targetRead, methodInfoRead);
            }

            changed = true;
        }

        if (discardableDispatchLoads.Count > 0)
            DeadCodeEliminator.Run(method.ControlFlowGraph!, discardableDispatchLoads);

        return changed;

        IOperand FollowCopies(IOperand value)
        {
            var seen = new HashSet<LocalVariable>();
            while (value is LocalVariable local && seen.Add(local)
                   && definitions.TryGetValue(local, out var definition)
                   && definition is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] })
                value = source;
            return value;
        }

        void CollectDispatchLoads(Instruction call, Instruction klassRead, Instruction? targetRead, Instruction? methodInfoRead)
        {
            // Only discard the raw reads if the call can perform the check at the same
            // point in the observable sequence. Reads with other consumers remain live.
            var block = homeBlocks[call];
            var first = block.Instructions.IndexOf(klassRead);
            var last = block.Instructions.IndexOf(call);
            if (first < 0 || first >= last)
                return;

            var checkedReceiver = klassRead.Operands[1] is MemoryOperand { Base: LocalVariable receiver }
                && HasDirectNonNullEdge(block, receiver);
            for (var i = first + 1; i < last; i++)
            {
                var candidate = block.Instructions[i];
                if (candidate == targetRead || candidate == methodInfoRead || candidate.OpCode == OpCode.Nop
                    || candidate is { OpCode: OpCode.Move, Operands: [LocalVariable, LocalVariable or Immediate] }
                    || checkedReceiver && candidate is { OpCode: OpCode.Move, Operands: [LocalVariable,
                        FieldReference { Field.IsStatic: false, Field.FieldType: not CustomModifierTypeAnalysisContext }] })
                    continue;
                return;
            }

            discardableDispatchLoads.Add(klassRead);
            if (targetRead != null && homeBlocks.TryGetValue(targetRead, out var targetBlock) && targetBlock == block)
                discardableDispatchLoads.Add(targetRead);
            if (methodInfoRead != null && homeBlocks.TryGetValue(methodInfoRead, out var methodInfoBlock) && methodInfoBlock == block)
                discardableDispatchLoads.Add(methodInfoRead);
        }

        bool HasDirectNonNullEdge(Block block, LocalVariable receiver)
        {
            if (block.Predecessors is not [var guard] || guard.Successors.Count != 2
                || guard.Instructions.LastOrDefault() is not { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable predicate] }
                || !definitions.TryGetValue(predicate, out var comparison)
                || comparison is not { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, Operands: [_, var lhs, var rhs] }
                || !(lhs is Immediate { Value: 0 } && ReferenceEquals(rhs, receiver)
                    || rhs is Immediate { Value: 0 } && ReferenceEquals(lhs, receiver)))
                return false;
            return (comparison.OpCode == OpCode.CheckNotEqual) == (taken == block);
        }

        void RewriteTailCall(Instruction tailCall, MethodAnalysisContext targetMethod)
        {
            var callingConventions = targetMethod.AppContext.InstructionSet.CallingConventionResolver;
            var operands = new List<IOperand> { targetMethod };
            LocalVariable? result = null;

            // The raw X0 return-register placeholder is not a destination until the tail target
            // has been identified. Rebuild the ordinary call layout and carry its result to RET.
            if (!targetMethod.IsVoid)
            {
                result = new LocalVariable("virtualTailCallResult",
                    callingConventions?.ReturnRegister(targetMethod) ?? new Register(null, "X0"),
                    targetMethod.ReturnType);
                operands.Add(result);
            }

            operands.AddRange(tailCall.Operands.Skip(2));
            tailCall.SetOperands(operands);
            tailCall.OpCode = targetMethod.IsVoid ? OpCode.CallVoid : OpCode.Call;
            callingConventions?.RemapRawArguments(tailCall, targetMethod);

            if (!homeBlocks.TryGetValue(tailCall, out var block))
                return;

            List<IOperand> returnOperands = !method.IsVoid && result is { } returnValue
                ? [returnValue]
                : [];
            block.AddInstruction(new Instruction(-1, OpCode.Return, returnOperands));
            block.CalculateBlockType();
        }

        MemoryOperand? SlotLoad(IOperand operand) => operand switch
        {
            MemoryOperand { Index: null, Scale: 0 } inlined => inlined,
            LocalVariable local when loads.TryGetValue(local, out var load) => load,
            _ => null
        };
    }

    private static MethodAnalysisContext? ResolveVTableSlot(ApplicationAnalysisContext appContext, TypeAnalysisContext type, int slot)
    {
        var definition = (type as GenericInstanceTypeAnalysisContext)?.GenericType.Definition ?? type.Definition;

        if (definition == null || slot >= definition.VtableCount)
            return null;

        if (appContext.ResolveContextForMethod(definition.VTable[slot]) is { } implementation)
            return implementation;

        // an abstract method has no implementation, try to resolve it
        for (var declarer = type; declarer != null; declarer = declarer.BaseType)
        {
            if (declarer.Methods.FirstOrDefault(m => m.Definition?.slot == slot) is { } declaration)
                return declaration;
        }

        return null;
    }

    private static RuntimeMethodInfoAnalysisContext? GetMethodInfoArgument(Instruction call,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var firstArg = call.OpCode == OpCode.CallVoid ? 1 : 2;

        for (var i = call.Operands.Count - 1; i >= firstArg; i--)
        {
            if (ResolveMethodInfo(call.Operands[i], definitions, []) is { } methodInfo)
                return methodInfo;
        }

        return null;
    }

    internal static void RecoverDelegateTargets(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        foreach (var call in instructions)
        {
            if (!call.IsCall || call.Operands[0] is not MethodAnalysisContext { Name: ".ctor" })
                continue;

            var pointerIndex = call.OpCode == OpCode.CallVoid ? 3 : 4;
            if (call.Operands.Count <= pointerIndex || !IsDelegateMethodPointerArgument(call, pointerIndex))
                continue;
            var pointer = call.Operands[pointerIndex];
            var producerChain = new List<(LocalVariable Local, Instruction Definition)>();
            if (pointer is RuntimeMethodInfoAnalysisContext)
                continue;

            var target = IlGenerator.ResolveDelegateMethodOperand(pointer,
                local => definitions.TryGetValue(local, out var producer) ? producer : null, producerChain);
            if (target == null)
            {
                producerChain.Clear();
                if (ResolveVirtualDelegateTarget(call.Operands[pointerIndex - 1], pointer, method,
                        definitions, producerChain) is not { } virtualTarget)
                    continue;
                target = virtualTarget;
            }

            // Leave metadata reads that are still used elsewhere intact.
            call.SetOperand(pointerIndex, target);
            foreach (var (local, producer) in producerChain)
            {
                if (instructions.Any(instruction => !ReferenceEquals(instruction, producer)
                        && instruction.Operands.Any(operand => ReferencesLocal(operand, local))))
                    break;

                producer.OpCode = OpCode.Nop;
                producer.SetOperands();
            }
        }
    }

    private static RuntimeMethodInfoAnalysisContext? ResolveVirtualDelegateTarget(IOperand receiver, IOperand pointer,
        MethodAnalysisContext method, Dictionary<LocalVariable, Instruction> definitions,
        List<(LocalVariable Local, Instruction Definition)> producerChain)
    {
        var boundReceiver = FollowCopies(receiver);
        if (boundReceiver is not LocalVariable { IsThis: true })
            return null;

        // IL2CPP can pass the MethodInfo or the native function pointer from the same
        // VirtualInvokeData entry. Resolve only a slot loaded through the delegate's own receiver.
        var seen = new HashSet<LocalVariable>();
        while (pointer is LocalVariable local)
        {
            if (!seen.Add(local) || !definitions.TryGetValue(local, out var producer)
                || producer is not { OpCode: OpCode.Move, Operands: [_, var source] })
                return null;
            producerChain.Add((local, producer));
            pointer = source;
        }

        if (pointer is not MemoryOperand { Base: LocalVariable klass, Index: null, Scale: 0, Addend: var offset }
            || klass.Type is not RuntimeClassTypeAnalysisContext { RepresentedType: { IsValueType: false } type }
            || !definitions.TryGetValue(klass, out var klassLoad)
            || klassLoad is not { OpCode: OpCode.Move,
                Operands: [_, MemoryOperand { Base: var klassReceiver, Addend: 0, Index: null, Scale: 0 }] }
            || klassReceiver == null || !ReferenceEquals(FollowCopies(klassReceiver), boundReceiver))
            return null;

        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        var vtableOffset = pointerSize == 8
            ? Il2CppClassUsefulOffsets.GetVtableOffset(method.AppContext.MetadataVersion, is32Bit: false)
            : VTableOffset32;
        if (offset < vtableOffset)
            return null;

        var entrySize = 2L * pointerSize;
        var displacement = offset - vtableOffset;
        var slot = displacement / entrySize;
        var memberOffset = displacement % entrySize;
        if ((memberOffset != 0 && memberOffset != pointerSize) || slot > int.MaxValue
            || ResolveVTableSlot(method.AppContext, type, (int)slot) is not
                { IsVirtual: true, IsStatic: false, DeclaringType: { IsValueType: false } declaringType } target)
            return null;

        producerChain.Add((klass, klassLoad));
        return new RuntimeMethodInfoAnalysisContext(target, declaringType.DeclaringAssembly) { IsVirtualDelegateTarget = true };

        IOperand FollowCopies(IOperand value)
        {
            var visited = new HashSet<LocalVariable>();
            while (value is LocalVariable local && !local.IsThis && visited.Add(local)
                   && definitions.TryGetValue(local, out var producer)
                   && producer is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] })
                value = source;
            return value;
        }
    }

    private static bool ReferencesLocal(IOperand operand, LocalVariable local) => operand switch
    {
        MemoryOperand memory => ReferenceEquals(memory.Base, local) || ReferenceEquals(memory.Index, local),
        FieldReference field => ReferenceEquals(field.Local, local),
        AddressOf address => ReferencesLocal(address.Target, local),
        ArrayAccess array => ReferenceEquals(array.Array, local) || ReferenceEquals(array.Index, local),
        ArrayLength length => ReferenceEquals(length.Array, local),
        _ => ReferenceEquals(operand, local),
    };

    private static RuntimeMethodInfoAnalysisContext? ResolveMethodInfo(IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions, HashSet<LocalVariable> visited)
    {
        if (AsMethodInfo(operand) is { } direct)
            return direct;

        // On ARM64 ELF, the metadata slot address can itself be carried in a local that has
        // already been named from MethodRef metadata. The following zero-offset load reads the
        // initialized MethodInfo* stored in that slot; when used in a call's hidden MethodInfo
        // position it denotes the same represented method.
        if (operand is MemoryOperand
            {
                Index: null,
                Scale: 0,
                Addend: 0,
                Base: LocalVariable { Type: RuntimeMethodInfoAnalysisContext methodInfo }
            })
            return methodInfo;

        if (operand is not LocalVariable local || !visited.Add(local)
            || !definitions.TryGetValue(local, out var definition)
            || definition is not { OpCode: OpCode.Move, Operands: [_, var source] })
            return null;

        return ResolveMethodInfo(source, definitions, visited);
    }

    private static RuntimeMethodInfoAnalysisContext? AsMethodInfo(IOperand operand) =>
        operand switch
        {
            RuntimeMethodInfoAnalysisContext methodInfo => methodInfo,
            LocalVariable { Type: RuntimeMethodInfoAnalysisContext methodInfoLocal } => methodInfoLocal,
            _ => null
        };

    // A hidden MethodInfo argument can survive as a native load even after its
    // call was recovered or replaced. Only allow DCE to discard reads from an
    // explicit metadata handle, not from an arbitrary pointer typed as MethodInfo.
    internal static HashSet<Instruction>? FindMethodMetadataReads(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        HashSet<Instruction>? reads = null;
        Dictionary<LocalVariable, Instruction?>? definitions = null;
        foreach (var instruction in instructions)
        {
            if (instruction is not { OpCode: OpCode.Move,
                Operands: [LocalVariable, MemoryOperand { Base: LocalVariable { Type: RuntimeMethodInfoAnalysisContext } slot,
                    Index: null, Scale: 0, Addend: 0 }] }
                || instruction.MemoryAccessSize is { } accessSize && accessSize != pointerSize
                || instruction.OperandWidth is { } width && width != pointerSize * 8)
                continue;

            if (definitions == null)
            {
                definitions = new();
                // This runs after SSA lowering. A local defined along multiple paths could
                // hold a different address on another path.
                foreach (var candidate in instructions)
                {
                    if (candidate.Destination is LocalVariable destination)
                        definitions[destination] = definitions.ContainsKey(destination) ? null : candidate;
                    foreach (var operand in candidate.Operands)
                        if (operand is AddressOf { Target: LocalVariable addressed })
                            definitions[addressed] = null;
                }
            }

            if (!slot.IsMethodInfo && definitions.TryGetValue(slot, out var definition)
                && definition is { OpCode: OpCode.Move, OperandWidth: null,
                    Operands: [_, RuntimeMethodInfoAnalysisContext] })
                (reads ??= []).Add(instruction);
        }

        return reads;
    }

    private static void HandleKeyFunction(ApplicationAnalysisContext appContext, Instruction instruction, ulong target, BaseKeyFunctionAddresses kFA)
    {
        var method = "";
        if (target == kFA.il2cpp_codegen_initialize_method || target == kFA.il2cpp_codegen_initialize_runtime_metadata)
        {
            if (appContext.MetadataVersion < 27)
            {
                method = nameof(kFA.il2cpp_codegen_initialize_method);
            }
            else
            {
                method = nameof(kFA.il2cpp_codegen_initialize_runtime_metadata);
            }
        }
        else
        {
            var pairs = kFA.Pairs.ToList();
            var key = pairs.FirstOrDefault(pair => pair.Value == target).Key;
            if (key == null)
                return;
            method = key;
        }

        if (method != "")
        {
            instruction.SetOperand(0, new StringLiteral(method));
        }
    }

    // Because of il2cpp fields (like cctor_finished_or_no_cctor) [local @ reg+offset] sometimes can't be resolved, but this works for now
    private static void ResolveGetter(MethodAnalysisContext method)
    {
        if (!method.Name.StartsWith("get_"))
            return;

        // Default get: Return [this @ reg+offset]
        var instructions = method.ControlFlowGraph!.Instructions;
        if (instructions.Count == 1)
        {
            var instr = instructions[0];

            if (instr.OpCode != OpCode.Return
                || instr.Operands.Count < 1
                || instr.Operands[0] is not MemoryOperand memory
                || memory.Index != null || memory.Scale != 0
                || memory.Base is not LocalVariable local)
                return;

            var fieldName = $"<{method.Name[4..]}>k__BackingField";

            var field = method.DeclaringType!.Fields.Find(f => f.Name == fieldName);
            if (field == null)
                return;

            instr.SetOperand(0, new FieldReference(field, local, (int)memory.Addend));
        }
    }

    /// <summary>
    /// Recovers simple struct-valued property getters whose native ABI expands the field into
    /// component loads and returns the hidden destination pointer. The managed getter is exactly
    /// the matching backing/internal field read; only straight-line Move/Return bodies qualify.
    /// </summary>
    public static bool ResolveSimpleStructGetter(MethodAnalysisContext method)
    {
        if (!method.Name.StartsWith("get_")
            || method.IsStatic
            || !method.ReturnType.IsValueType
            || method.DeclaringType is not { } declaringType)
            return false;

        var propertyName = method.Name[4..];
        if (declaringType.Properties.FirstOrDefault(property => property.Name == propertyName
                && property.PropertyType.FullName == method.ReturnType.FullName) == null)
            return false;

        var instructions = method.ControlFlowGraph!.Instructions;
        var returnInstructions = instructions.Where(instruction => instruction.OpCode == OpCode.Return).ToList();
        if (returnInstructions.Count != 1
            || returnInstructions[0].Operands.Count != 1
            || instructions.Any(instruction => instruction.OpCode is not (OpCode.Move or OpCode.Return or OpCode.Nop))
            || instructions.Any(instruction => instruction.OpCode == OpCode.Move
                && instruction.Operands[0] is not LocalVariable))
            return false;

        var candidateFields = instructions
            .Where(instruction => instruction.OpCode == OpCode.Move
                && instruction.Operands.Count > 1
                && instruction.Operands[1] is FieldReference
                {
                    Parent: null,
                    Local.IsThis: true
                } fieldReference
                && fieldReference.Field.FieldType.FullName == method.ReturnType.FullName
                && (fieldReference.Field.Name == propertyName
                    || fieldReference.Field.Name == propertyName + "Internal"
                    || fieldReference.Field.Name == $"<{propertyName}>k__BackingField"))
            .Select(instruction => (FieldReference)instruction.Operands[1])
            .ToList();
        if (candidateFields.Count == 0
            || candidateFields.Select(fieldReference => fieldReference.Field).Distinct().Count() != 1)
            return false;

        foreach (var instruction in instructions)
        {
            if (ReferenceEquals(instruction, returnInstructions[0]))
                continue;

            instruction.OpCode = OpCode.Nop;
            instruction.SetOperands();
        }

        returnInstructions[0].SetOperand(0, candidateFields[0]);
        return true;
    }

    /// <summary>
    /// Resolves static auto-property backing-field accesses even when the IL2CPP class-info global
    /// itself was not recognized as a metadata usage. Such accessors load static_fields through
    /// Il2CppClass::static_fields, then read or write the backing field at its metadata offset.
    /// </summary>
    private static void ResolveStaticAutoPropertyAccessors(MethodAnalysisContext method)
    {
        var declaringType = method.DeclaringType;
        if (declaringType == null)
            return;

        var property = declaringType.Properties.FirstOrDefault(candidate => candidate.IsStatic
            && (ReferenceEquals(candidate.Getter, method) || ReferenceEquals(candidate.Setter, method)));
        if (property == null)
            return;

        var isGetter = ReferenceEquals(property.Getter, method);
        var backingField = declaringType.Fields.FirstOrDefault(field => field.IsStatic
            && field.Name == $"<{property.Name}>k__BackingField");
        if (backingField?.BackingData?.FieldOffset is not { } fieldOffset || fieldOffset < 0)
            return;
        if (property.PropertyType.FullName != backingField.FieldType.FullName
            || !isGetter && (method.Parameters.Count != 1
                || method.Parameters[0].ParameterType.FullName != backingField.FieldType.FullName))
            return;

        var instructions = method.ControlFlowGraph!.Instructions;
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        bool IsClassInfoPointer(LocalVariable local)
        {
            if (local.Type is RuntimeClassTypeAnalysisContext runtimeClass)
                return runtimeClass.RepresentedType.FullName == declaringType.FullName;

            if (!definitions.TryGetValue(local, out var definition)
                || definition.OpCode != OpCode.Move
                || definition.Operands.Count < 2)
                return false;

            return definition.Operands[1] switch
            {
                MemoryOperand { IsConstant: true } => true,
                TypeAnalysisContext and not (RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext) => true,
                _ => false,
            };
        }

        bool IsStaticStorage(LocalVariable local)
        {
            if (!definitions.TryGetValue(local, out var definition)
                || definition is not { OpCode: OpCode.Move, Operands: [_, MemoryOperand
                    {
                        Index: null,
                        Scale: 0,
                        Base: LocalVariable classInfo
                    } classInfoLoad] }
                || classInfoLoad.Addend < 0
                || classInfoLoad.Addend > uint.MaxValue
                || !Il2CppClassUsefulOffsets.IsStaticFieldsPtr((uint)classInfoLoad.Addend, method.AppContext.Binary.is32Bit))
                return false;

            return IsClassInfoPointer(classInfo);
        }

        bool FlowsToReturn(LocalVariable value, HashSet<LocalVariable> visited)
        {
            if (!visited.Add(value))
                return false;

            foreach (var instruction in instructions)
            {
                if (instruction.OpCode == OpCode.Return && instruction.Operands.Count == 1
                    && ReferenceEquals(instruction.Operands[0], value))
                    return true;

                if (instruction.Destination is not LocalVariable destination)
                    continue;

                if (instruction.OpCode == OpCode.Move
                    && instruction.Operands.Count > 1
                    && ReferenceEquals(instruction.Operands[1], value)
                    && FlowsToReturn(destination, visited))
                    return true;

                if (instruction.OpCode == OpCode.Phi
                    && instruction.Operands.Skip(1).Any(input => ReferenceEquals(input, value))
                    && FlowsToReturn(destination, visited))
                    return true;
            }

            return false;
        }

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Move)
                continue;

            if (isGetter
                && instruction.Operands is [LocalVariable destination, MemoryOperand
                    {
                        Index: null,
                        Scale: 0,
                    Base: LocalVariable getterStorage
                    } load]
                && load.Addend == fieldOffset
                && IsStaticStorage(getterStorage)
                && FlowsToReturn(destination, new HashSet<LocalVariable>()))
            {
                instruction.SetOperand(1, new FieldReference(backingField, getterStorage, fieldOffset));
            }
            else if (!isGetter
                && instruction.Operands is [MemoryOperand
                    {
                        Index: null,
                        Scale: 0,
                    Base: LocalVariable setterStorage
                    } store, _]
                && store.Addend == fieldOffset
                && IsStaticStorage(setterStorage))
            {
                instruction.SetOperand(0, new FieldReference(backingField, setterStorage, fieldOffset));
            }
        }
    }

}
