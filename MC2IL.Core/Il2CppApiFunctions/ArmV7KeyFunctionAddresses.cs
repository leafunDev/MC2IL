using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Gee.External.Capstone.Arm;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Il2CppApiFunctions;

public class ArmV7KeyFunctionAddresses : BaseKeyFunctionAddresses
{
    private readonly ConcurrentDictionary<ulong, bool> _classInitThunks = new();

    internal bool IsClassInitThunk(ulong address) => il2cpp_runtime_class_init_actual != 0
        && _classInitThunks.GetOrAdd(address, IsClassInitBranch);

    private bool IsClassInitBranch(ulong address) =>
        FindFunctionThisIsAThunkOf(address) == il2cpp_runtime_class_init_actual;

    protected override ulong FindFirstCallTargetInMethod(ulong methodVa)
    {
        if (methodVa == 0)
            return 0;
        var first = ArmV7Utils.GetArmV7MethodBodyAtVirtualAddress(_appContext, methodVa, count: 32)
            .FirstOrDefault(i => i.Id is ArmInstructionId.ARM_INS_BL or ArmInstructionId.ARM_INS_BLX);
        return first != null ? GetTarget(first, methodVa) : 0;
    }

    private static ulong GetTarget(ArmInstruction instruction, ulong source)
    {
        if (instruction.Details.Operands.FirstOrDefault() is not { Type: ArmOperandType.Immediate } operand)
            return 0;
        var mode = instruction.Id == ArmInstructionId.ARM_INS_BLX ? (source & 1UL) ^ 1UL : source & 1UL;
        return (unchecked((uint)operand.Immediate) & ~1U) | mode;
    }

    protected override ulong GetObjectIsInstFromSystemType() => 0;

    protected override IEnumerable<ulong> FindAllThunkFunctions(ulong addr, uint maxBytesBack = 0, params ulong[] addressesToIgnore) => [];

    protected override ulong FindFunctionThisIsAThunkOf(ulong thunkPtr, bool prioritiseCall = false)
    {
        if (thunkPtr == 0)
            return 0;
        var body = ArmV7Utils.GetArmV7MethodBodyAtVirtualAddress(_appContext, thunkPtr, managed: false, count: 6);
        var branchIndex = 0;
        // il2cpp_raise_exception sets Exception::Raise's optional second argument to null.
        if (prioritiseCall && body.FirstOrDefault() is
            { Id: ArmInstructionId.ARM_INS_MOV, Details: { ConditionCode: ArmConditionCode.ARM_CC_AL,
                Operands: [{ Type: ArmOperandType.Register, Register.Name: "r1" },
                    { Type: ArmOperandType.Immediate, Immediate: 0 }] } })
            branchIndex = 1;

        if (body.ElementAtOrDefault(branchIndex) is
            { Id: ArmInstructionId.ARM_INS_B, Details.ConditionCode: ArmConditionCode.ARM_CC_AL } branch)
            return GetTarget(branch, thunkPtr);
        return 0;
    }

    protected override int GetCallerCount(ulong toWhere) => 0;
}
