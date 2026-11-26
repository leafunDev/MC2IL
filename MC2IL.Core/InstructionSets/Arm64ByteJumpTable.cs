using System.Collections.Generic;
using System.Linq;
using Disarm;
using Disarm.InternalDisassembly;
using LibMC2IL;
using LibMC2IL.Elf;

namespace MC2IL.Core.InstructionSets;

internal sealed record Arm64ByteJumpTable(Arm64Register Index, ulong[] Targets)
{
    // Clang emits byte offsets from an ADR anchor for small switches.
    // Only lift the sequence when its unsigned bounds guard and all targets are known.
    internal static Dictionary<ulong, Arm64ByteJumpTable?> Find(IReadOnlyList<Arm64Instruction> method,
        Il2CppBinary binary)
    {
        var tables = new Dictionary<ulong, Arm64ByteJumpTable?>();
        if (binary is not ElfFile elf || !method.Any(instruction => instruction.Mnemonic == Arm64Mnemonic.BR))
            return tables;

        var addresses = new Dictionary<ulong, int>();
        for (var i = 0; i < method.Count; i++)
            addresses[method[i].Address] = i;
        var branches = new HashSet<ulong>();
        foreach (var instruction in method)
            if (instruction.Mnemonic is Arm64Mnemonic.B or Arm64Mnemonic.BC or Arm64Mnemonic.CBZ
                or Arm64Mnemonic.CBNZ or Arm64Mnemonic.TBZ or Arm64Mnemonic.TBNZ)
                branches.Add(instruction.Mnemonic switch
                {
                    Arm64Mnemonic.B => instruction.BranchTarget,
                    Arm64Mnemonic.BC => unchecked(instruction.Address + instruction.Op0PcRelImm),
                    Arm64Mnemonic.CBZ or Arm64Mnemonic.CBNZ => unchecked((ulong)((long)instruction.Address + instruction.Op1Imm)),
                    _ => unchecked((ulong)((long)instruction.Address + instruction.Op2Imm)),
                });

        for (var i = 9; i < method.Count; i++)
        {
            var jump = method[i];
            var addOffset = method[i - 1];
            var load = method[i - 2];
            var anchor = method[i - 3];
            var addPage = method[i - 4];
            var extendIndex = method[i - 5];
            var page = method[i - 6];
            var guard = method[i - 7];
            var compareIndex = i - 8;
            if (method[compareIndex] is { Mnemonic: Arm64Mnemonic.FMOV, Op0Reg:
                    >= Arm64Register.S0 and <= Arm64Register.S31 or >= Arm64Register.D0 and <= Arm64Register.D31 })
                compareIndex--;
            var compare = method[compareIndex];
            if (compareIndex == 0)
                continue;
            var maskIndex = method[compareIndex - 1];

            if (jump.Mnemonic != Arm64Mnemonic.BR
                || guard is not { Mnemonic: Arm64Mnemonic.B, MnemonicConditionCode: Arm64ConditionCode.HI }
                || !addresses.ContainsKey(guard.BranchTarget) || guard.BranchTarget <= jump.Address
                || compare is not { Mnemonic: Arm64Mnemonic.CMP, Op1Kind: Arm64OperandKind.Immediate, Op1Imm: >= 1 and <= 63 }
                || maskIndex is not { Mnemonic: Arm64Mnemonic.AND, Op2Kind: Arm64OperandKind.Immediate, Op2Imm: 255 }
                || maskIndex.Op0Reg != compare.Op0Reg
                || maskIndex.Op0Reg is < Arm64Register.W0 or > Arm64Register.W17
                || maskIndex.Op1Reg is < Arm64Register.W0 or > Arm64Register.W30
                || extendIndex is not { Mnemonic: Arm64Mnemonic.AND, Op2Kind: Arm64OperandKind.Immediate, Op2Imm: 255 }
                || extendIndex.Op0Reg - Arm64Register.X0 != maskIndex.Op0Reg - Arm64Register.W0
                || extendIndex.Op1Reg - Arm64Register.X0 != maskIndex.Op1Reg - Arm64Register.W0
                || page.Mnemonic != Arm64Mnemonic.ADRP
                || addPage is not { Mnemonic: Arm64Mnemonic.ADD, Op2Kind: Arm64OperandKind.Immediate }
                || page.Op0Reg != addPage.Op0Reg || addPage.Op1Reg != addPage.Op0Reg
                || anchor.Mnemonic != Arm64Mnemonic.ADR
                || load is not { Mnemonic: Arm64Mnemonic.LDRB, MemOffset: 0, MemIndexMode: Arm64MemoryIndexMode.Offset }
                || load.MemBase != addPage.Op0Reg || load.MemAddendReg != extendIndex.Op0Reg
                || load.MemExtendOrShiftAmount != 0
                || addOffset is not { Mnemonic: Arm64Mnemonic.ADD, Op2Kind: Arm64OperandKind.Register, Op3Imm: 2 }
                || addOffset.FinalOpShiftType != Arm64ShiftType.LSL
                || addOffset.Op0Reg != anchor.Op0Reg || addOffset.Op1Reg != anchor.Op0Reg || jump.Op0Reg != anchor.Op0Reg
                || addOffset.Op2Reg - Arm64Register.X0 != load.Op0Reg - Arm64Register.W0)
                continue;

            var valid = true;
            for (var j = compareIndex; j <= i; j++)
                if (method[j].Address != method[j - 1].Address + 4 || branches.Contains(method[j].Address))
                    valid = false;
            if (!valid)
                continue;

            var tableAddress = unchecked((ulong)((long)(page.Address & ~0xFFFUL) + page.Op1Imm + addPage.Op2Imm));
            var anchorAddress = unchecked((ulong)((long)anchor.Address + anchor.Op1Imm));
            var targets = new ulong[(int)compare.Op1Imm + 1];
            for (var j = 0; j < targets.Length; j++)
            {
                if (!elf.TryReadReadOnlyScalar(tableAddress + (ulong)j, 1, out var offset))
                {
                    valid = false;
                    break;
                }
                targets[j] = anchorAddress + offset * 4;
                if (targets[j] <= jump.Address || !addresses.ContainsKey(targets[j]))
                {
                    valid = false;
                    break;
                }
            }
            var temporaries = new HashSet<int>
            {
                page.Op0Reg - Arm64Register.X0,
                anchor.Op0Reg - Arm64Register.X0,
                load.Op0Reg - Arm64Register.W0
            };
            if (!valid || temporaries.Count != 3 || temporaries.Any(register => register is < 9 or > 15)
                || temporaries.Contains(maskIndex.Op0Reg - Arm64Register.W0)
                || temporaries.Contains(maskIndex.Op1Reg - Arm64Register.W0)
                || !TempsAreUnused(method, addresses, targets, jump.Address, temporaries))
                continue;

            for (var j = i - 6; j < i; j++)
                tables[method[j].Address] = null;
            tables[jump.Address] = new Arm64ByteJumpTable(compare.Op0Reg, targets);
        }
        return tables;
    }

    private static bool TempsAreUnused(IReadOnlyList<Arm64Instruction> method, Dictionary<ulong, int> addresses,
        ulong[] targets, ulong dispatch, HashSet<int> registers)
    {
        var pending = new Queue<ulong>(targets);
        var visited = new HashSet<ulong>();
        while (pending.Count != 0)
        {
            var address = pending.Dequeue();
            if (!visited.Add(address))
                continue;
            if (address <= dispatch || !addresses.TryGetValue(address, out var index))
                return false;
            var instruction = method[index];
            if (Touches(instruction.Op0Reg) || Touches(instruction.Op1Reg) || Touches(instruction.Op2Reg)
                || Touches(instruction.Op3Reg) || Touches(instruction.MemBase) || Touches(instruction.MemAddendReg))
                return false;

            switch (instruction.Mnemonic)
            {
                case Arm64Mnemonic.RET or Arm64Mnemonic.RETAA or Arm64Mnemonic.RETAB:
                    continue;
                case Arm64Mnemonic.BR or Arm64Mnemonic.BLR:
                    return false;
                case Arm64Mnemonic.B:
                    pending.Enqueue(instruction.BranchTarget);
                    if (instruction.MnemonicConditionCode is Arm64ConditionCode.NONE or Arm64ConditionCode.AL)
                        continue;
                    break;
                case Arm64Mnemonic.BC:
                    pending.Enqueue(unchecked(address + instruction.Op0PcRelImm));
                    break;
                case Arm64Mnemonic.CBZ or Arm64Mnemonic.CBNZ:
                    pending.Enqueue(unchecked((ulong)((long)address + instruction.Op1Imm)));
                    break;
                case Arm64Mnemonic.TBZ or Arm64Mnemonic.TBNZ:
                    pending.Enqueue(unchecked((ulong)((long)address + instruction.Op2Imm)));
                    break;
            }
            pending.Enqueue(address + 4);
        }
        return true;

        bool Touches(Arm64Register register) => register switch
        {
            >= Arm64Register.W0 and <= Arm64Register.W31 => registers.Contains(register - Arm64Register.W0),
            >= Arm64Register.X0 and <= Arm64Register.X31 => registers.Contains(register - Arm64Register.X0),
            _ => false
        };
    }
}
