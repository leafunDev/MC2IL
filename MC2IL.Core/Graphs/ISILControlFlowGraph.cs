using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.ISIL;

namespace MC2IL.Core.Graphs;

public class ISILControlFlowGraph
{
    private const int OperandSetUpgradeThreshold = 8;
    private const int BlockRemovalHashSetThreshold = 8;

    public Block EntryBlock;
    public Block ExitBlock;
    public int Count => Blocks.Count;
    public List<Block> Blocks;

    public List<Instruction> Instructions
    {
        get
        {
            // BFS search
            var visited = new HashSet<Block> { EntryBlock };
            var queue = new Queue<Block>();
            var result = new List<Instruction>();

            queue.Enqueue(EntryBlock);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                result.AddRange(current.Instructions);

                foreach (var successor in current.Successors)
                {
                    if (visited.Add(successor))
                        queue.Enqueue(successor);
                }
            }

            return result; // Should this be cached?
        }
    }

    private int idCounter;

    // Used while building the CFG, when many branch targets need to be resolved before
    // the graph is stable. Kept local to Build's lifetime because later passes can mutate it.
    private Dictionary<Instruction, Block>? _instructionBlockIndex;

    public ISILControlFlowGraph(List<Instruction> instructions)
    {
        EntryBlock = new Block
        {
            ID = idCounter++,
            BlockType = BlockType.Entry
        };

        ExitBlock = new Block
        {
            ID = idCounter++,
            BlockType = BlockType.Exit
        };

        Blocks =
        [
            EntryBlock,
            ExitBlock
        ];

        Build(instructions);
    }

    private bool TryGetTargetJumpInstructionIndex(Instruction instruction, out int jumpInstructionIndex)
    {
        jumpInstructionIndex = 0;
        try
        {
            jumpInstructionIndex = ((Instruction)instruction.Operands[0]).Index;
            return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public void RemoveUnreachableBlocks()
    {
        if (Blocks.Count == 0)
            return;

        // Get blocks reachable from entry
        var visited = new HashSet<Block> { EntryBlock };
        var pending = new Queue<Block>();
        pending.Enqueue(EntryBlock);

        while (pending.Count > 0)
        {
            var block = pending.Dequeue();

            foreach (var successor in block.Successors)
            {
                if (!visited.Add(successor))
                    continue;
                pending.Enqueue(successor);
            }
        }

        // Get unreachable blocks
        var unreachable = Blocks.Where(block => !visited.Contains(block)).ToList();

        // Remove those
        foreach (var block in unreachable)
        {
            // Don't remove entry or exit
            if (block == EntryBlock || block == ExitBlock)
                continue;

            // Fully detach the block so no remaining block keeps a dangling reference to it.
            // (A reachable block can have an unreachable predecessor; leaving that reference
            // behind makes later passes such as dominator computation throw.)
            foreach (var successor in block.Successors)
                successor.Predecessors.Remove(block);
            foreach (var predecessor in block.Predecessors)
                predecessor.Successors.Remove(block);

            block.Successors.Clear();
            block.Predecessors.Clear();
        }

        // Remove in one pass instead of shifting the list once per unreachable block.
        if (unreachable.Count > 0)
            Blocks.RemoveAll(block => block != EntryBlock && block != ExitBlock && !visited.Contains(block));
    }

    public void RemoveNops()
    {
        var usedAsTarget = new HashSet<Instruction>();

        // Get all instructions used as branch targets
        foreach (var block in Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                foreach (var operand in instr.Operands)
                {
                    if (operand is Instruction target)
                        usedAsTarget.Add(target);
                }
            }
        }

        // Build replacement map for NOPs that are safe to replace
        var instructionReplacement = new Dictionary<Instruction, Instruction>();
        foreach (var block in Blocks)
        {
            Instruction? replacement = null;
            for (var i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = block.Instructions[i];
                if (instr.OpCode == OpCode.Nop)
                {
                    if (replacement != null && !usedAsTarget.Contains(instr))
                        instructionReplacement[instr] = replacement;
                }
                else
                {
                    replacement = instr;
                }
            }
        }

        // Update operands
        foreach (var block in Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                for (var i = 0; i < instr.Operands.Count; i++)
                {
                    if (instr.Operands[i] is Instruction target && instructionReplacement.TryGetValue(target, out var newTarget))
                        instr.SetOperand(i, newTarget);
                }
            }
        }

        // Remove NOPs
        foreach (var block in Blocks)
        {
            block.Instructions.RemoveAll(i => i.OpCode == OpCode.Nop && !usedAsTarget.Contains(i));
        }
    }

    public void RemoveEmptyBlocks()
    {
        var toRemove = new List<Block>();

        foreach (var block in Blocks)
        {
            if (block == EntryBlock || block == ExitBlock)
                continue;

            if (block.Instructions.Count == 0)
            {
                // jumps into the removed block must be retargeted, which needs an unambiguous successor
                if (block.Successors.Count != 1 && HasJumpOperandTo(block))
                    continue;

                var jumpTarget = block.Successors.Count == 1 ? block.Successors[0] : null;

                // Redirect predecessors to successors
                foreach (var pred in block.Predecessors)
                {
                    if (pred.Instructions.Count > 0
                        && pred.Instructions[^1] is { OpCode: OpCode.Jump or OpCode.ConditionalJump } jump
                        && ReferenceEquals(jump.Operands[0], block))
                        jump.SetOperand(0, jumpTarget!);

                    pred.Successors.Remove(block);
                    foreach (var succ in block.Successors)
                    {
                        if (!pred.Successors.Contains(succ))
                            pred.Successors.Add(succ);
                    }
                }

                // Redirect successors to predecessors
                foreach (var succ in block.Successors)
                {
                    succ.Predecessors.Remove(block);
                    foreach (var pred in block.Predecessors)
                    {
                        if (!succ.Predecessors.Contains(pred))
                            succ.Predecessors.Add(pred);
                    }
                }

                toRemove.Add(block);
            }
        }

        RemoveBlocks(toRemove);
    }

    private bool HasJumpOperandTo(Block block) =>
        block.Predecessors.Any(pred => pred.Instructions.Count > 0
            && pred.Instructions[^1] is { OpCode: OpCode.Jump or OpCode.ConditionalJump } jump
            && ReferenceEquals(jump.Operands[0], block));

    private void RemoveBlocks(List<Block> toRemove)
    {
        if (toRemove.Count == 0)
            return;

        if (toRemove.Count >= BlockRemovalHashSetThreshold)
        {
            var blocksToRemove = new HashSet<Block>(toRemove);
            Blocks.RemoveAll(blocksToRemove.Contains);
        }
        else
        {
            Blocks.RemoveAll(toRemove.Contains);
        }
    }

    public void BuildUseDefLists(HashSet<Instruction>? clobberingAddressTakes = null)
    {
        foreach (var block in Blocks)
        {
            var use = new List<IOperand>();
            var def = new List<IOperand>();
            HashSet<IOperand>? useIndex = null;
            HashSet<IOperand>? defIndex = null;

            foreach (var instruction in block.Instructions)
            {
                foreach (var operand in instruction.Sources)
                    AddOperand(use, ref useIndex, operand);

                if (instruction.Destination != null)
                    AddOperand(def, ref defIndex, instruction.Destination);

                if (instruction.ImplicitDefinition is { } clobbered)
                    AddOperand(def, ref defIndex, clobbered);

                if (clobberingAddressTakes?.Contains(instruction) == true)
                {
                    foreach (var operand in instruction.Operands)
                        if (operand is AddressOf { Target: { } addressed })
                            AddOperand(def, ref defIndex, addressed);
                }
            }

            block.Use = use;
            block.Def = def;
        }
    }

    private static void AddOperand(List<IOperand> operands, ref HashSet<IOperand>? index, IOperand operand)
    {
        var added = index is null ? !operands.Contains(operand) : index.Add(operand);
        if (!added)
            return;

        operands.Add(operand);

        // Keep allocation-free linear checks for the small use/def lists found in most blocks.
        if (index is null && operands.Count == OperandSetUpgradeThreshold)
            index = new HashSet<IOperand>(operands);
    }

    public void MergeCallBlocks()
    {
        var toRemove = new List<Block>();

        for (var i = 0; i < Blocks.Count; i++)
        {
            var block = Blocks[i];
            if (block.BlockType != BlockType.Call) continue;

            if (block.Successors.Count != 1)
                continue;

            var nextBlock = block.Successors[0];

            // make sure that the next block only has one predecessor (this)
            if (nextBlock.Predecessors.Count != 1 || nextBlock.Predecessors[0] != block)
                continue;

            // merge instructions
            block.Instructions.AddRange(nextBlock.Instructions);
            block.Successors = nextBlock.Successors;

            // fix up successors predecessors
            foreach (var successor in nextBlock.Successors)
            {
                for (var j = 0; j < successor.Predecessors.Count; j++)
                {
                    if (successor.Predecessors[j] == nextBlock)
                        successor.Predecessors[j] = block;
                }
            }

            toRemove.Add(nextBlock);
        }

        // Remove all merged blocks
        RemoveBlocks(toRemove);

        foreach (var block in Blocks)
            block.CalculateBlockType();
    }

    private void Build(List<Instruction> instructions)
    {
        if (instructions == null)
            throw new ArgumentNullException(nameof(instructions));

        var currentBlock = new Block() { ID = idCounter++ };
        AddBlock(currentBlock);
        AddDirectedEdge(EntryBlock, currentBlock);

        for (var i = 0; i < instructions.Count; i++)
        {
            var isLast = i == instructions.Count - 1;
            Block newBlock;

            switch (instructions[i].OpCode)
            {
                case OpCode.Jump:
                case OpCode.ConditionalJump:
                case OpCode.IndirectJump:
                    currentBlock.AddInstruction(instructions[i]);

                    if (!isLast)
                    {
                        newBlock = new Block() { ID = idCounter++ };
                        AddBlock(newBlock);

                        if (instructions[i].OpCode is OpCode.Jump or OpCode.IndirectJump)
                        {
                            if (TryGetTargetJumpInstructionIndex(instructions[i], out int jumpTargetIndex))
                                currentBlock.Dirty = true;
                            else
                                AddDirectedEdge(currentBlock, ExitBlock);
                        }
                        else
                        {
                            AddDirectedEdge(currentBlock, newBlock);
                            currentBlock.Dirty = true;
                        }

                        currentBlock.CalculateBlockType();
                        currentBlock = newBlock;
                    }
                    else
                    {
                        var opcode = instructions[i].OpCode;
                        var hasDirectTarget = (opcode is OpCode.Jump or OpCode.IndirectJump)
                            && TryGetTargetJumpInstructionIndex(instructions[i], out _);
                        if (!hasDirectTarget)
                            AddDirectedEdge(currentBlock, ExitBlock);
                        currentBlock.Dirty = hasDirectTarget || opcode == OpCode.ConditionalJump;
                        currentBlock.CalculateBlockType();
                    }

                    break;

                case OpCode.Call:
                case OpCode.CallVoid:
                case OpCode.Return:
                case OpCode.Throw:
                    var isTerminator = instructions[i].OpCode is OpCode.Return or OpCode.Throw;

                    currentBlock.AddInstruction(instructions[i]);

                    if (!isLast)
                    {
                        newBlock = new Block() { ID = idCounter++ };
                        AddBlock(newBlock);
                        AddDirectedEdge(currentBlock, isTerminator ? ExitBlock : newBlock);
                        currentBlock.CalculateBlockType();
                        currentBlock = newBlock;
                    }
                    else
                    {
                        AddDirectedEdge(currentBlock, ExitBlock);
                        currentBlock.CalculateBlockType();
                    }

                    break;

                default:
                    currentBlock.AddInstruction(instructions[i]);
                    if (isLast)
                    {
                        AddDirectedEdge(currentBlock, ExitBlock);
                        currentBlock.CalculateBlockType();
                    }
                    break;
            }
        }

        _instructionBlockIndex = new Dictionary<Instruction, Block>(instructions.Count);
        foreach (var block in Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (!_instructionBlockIndex.TryGetValue(instruction, out _))
                    _instructionBlockIndex.Add(instruction, block);
            }
        }

        try
        {
            for (var index = 0; index < Blocks.Count; index++)
            {
                var node = Blocks[index];
                if (node.Dirty)
                    FixBlock(node);
            }

            // Connect blocks without successors to exit
            foreach (var block in Blocks)
            {
                if (block.Successors.Count == 0 && block != EntryBlock && block != ExitBlock)
                    AddDirectedEdge(block, ExitBlock);
            }

            // Change branch targets to blocks
            foreach (var instruction in Blocks.SelectMany(block => block.Instructions))
            {
                if (instruction.Operands.Count > 0 && instruction.Operands[0] is Instruction target)
                    instruction.SetOperand(0, FindBlockByInstruction(target)!);
            }
        }
        finally
        {
            _instructionBlockIndex = null;
        }
    }

    private void FixBlock(Block block, bool removeJmp = false)
    {
        if (block.BlockType is BlockType.Fall)
            return;

        var jump = block.Instructions.Last();

        var targetInstruction = jump.Operands[0] as Instruction;

        var destination = FindBlockByInstruction(targetInstruction);

        if (destination == null)
        {
            //We assume that we're tail calling another method somewhere. Need to verify if this breaks anywhere but it shouldn't in general
            block.BlockType = BlockType.TailCall;
            return;
        }


        int index = destination.Instructions.FindIndex(instruction => instruction == targetInstruction);

        var targetNode = SplitAndCreate(destination, index);

        AddDirectedEdge(block, targetNode);
        block.Dirty = false;

        if (removeJmp)
            block.Instructions.Remove(jump);
    }

    public Block? FindBlockByInstruction(Instruction? instruction)
    {
        if (instruction == null)
            return null;

        if (_instructionBlockIndex is { } instructionBlockIndex)
            return instructionBlockIndex.TryGetValue(instruction, out var indexedBlock) ? indexedBlock : null;

        for (var i = 0; i < Blocks.Count; i++)
        {
            var block = Blocks[i];
            for (var j = 0; j < block.Instructions.Count; j++)
            {
                var instr = block.Instructions[j];
                if (instr == instruction)
                {
                    return block;
                }
            }
        }

        return null;
    }

    private Block SplitAndCreate(Block target, int index)
    {
        if (index < 0 || index >= target.Instructions.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        // Don't need to split...
        if (index == 0)
            return target;

        var newBlock = new Block() { ID = idCounter++ };

        // target split in two
        // targetFirstPart -> targetSecondPart aka newNode

        // Take the instructions for the secondPart
        var instructions = target.Instructions.GetRange(index, target.Instructions.Count - index);
        target.Instructions.RemoveRange(index, target.Instructions.Count - index);

        // Add those to the newNode
        newBlock.Instructions.AddRange(instructions);

        if (_instructionBlockIndex is { } instructionBlockIndex)
        {
            foreach (var instruction in instructions)
                instructionBlockIndex[instruction] = newBlock;
        }

        // Transfer control flow
        newBlock.BlockType = target.BlockType;
        target.BlockType = BlockType.Fall;

        // Transfer successors
        newBlock.Successors = target.Successors;
        if (target.Dirty)
            newBlock.Dirty = true;
        target.Dirty = false;
        target.Successors = [];

        // Correct the predecessors for all the successors
        foreach (var successor in newBlock.Successors)
        {
            for (int i = 0; i < successor.Predecessors.Count; i++)
            {
                if (successor.Predecessors[i].ID == target.ID)
                    successor.Predecessors[i] = newBlock;
            }
        }

        // Add newNode and connect it
        AddBlock(newBlock);
        AddDirectedEdge(target, newBlock);

        return newBlock;
    }

    private void AddDirectedEdge(Block from, Block to)
    {
        // The graph models predecessor/successor blocks, not parallel edges. A conditional
        // branch may target its fallthrough block, in which case both paths share one edge.
        if (!from.Successors.Contains(to))
            from.Successors.Add(to);
        if (!to.Predecessors.Contains(from))
            to.Predecessors.Add(from);
    }

    // Some analysis passes turn existing call blocks into Throw after Build has connected their
    // normal fall-through successor. Remove that edge and refresh the block classification.
    public void MarkThrowAsTerminator(Instruction instruction)
    {
        if (instruction.OpCode != OpCode.Throw)
            return;

        var block = FindBlockByInstruction(instruction);
        if (block == null)
            return;

        var instructionIndex = block.Instructions.IndexOf(instruction);
        if (instructionIndex < 0)
            return;

        // Call-block merging may have placed later instructions after a call that is only now
        // recognized as non-returning. Keep those instructions in a separate block first.
        if (instructionIndex + 1 < block.Instructions.Count)
            SplitAndCreate(block, instructionIndex + 1);

        foreach (var successor in block.Successors)
            successor.Predecessors.Remove(block);
        block.Successors.Clear();

        AddDirectedEdge(block, ExitBlock);
        block.CalculateBlockType();
    }

    protected void AddBlock(Block block) => Blocks.Add(block);
}
