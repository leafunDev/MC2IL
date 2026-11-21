using System.Collections.Generic;
using System.Linq;
using Gee.External.Capstone.Arm;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Il2CppApiFunctions;

public class ArmV7KeyFunctionAddresses : BaseKeyFunctionAddresses
{
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
        if (body.FirstOrDefault() is { Id: ArmInstructionId.ARM_INS_B, Details.ConditionCode: ArmConditionCode.ARM_CC_AL } branch)
            return GetTarget(branch, thunkPtr);
        return 0;
    }

    protected override int GetCallerCount(ulong toWhere) => 0;
}
