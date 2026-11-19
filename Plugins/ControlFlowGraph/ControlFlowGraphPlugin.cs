using MC2IL.Plugin.ControlFlowGraph;
using MC2IL.Core.Api;
using MC2IL.Core.Attributes;

[assembly: RegisterMC2ILPlugin(typeof(ControlFlowGraphPlugin))]

namespace MC2IL.Plugin.ControlFlowGraph;

public class ControlFlowGraphPlugin : MC2ILPlugin
{
    public override string Name => "Control Flow Graph Plugin";
    public override string Description => "Adds an output format which generates control flow graph dot files for methods";

    public override void OnLoad()
    {
        OutputFormatRegistry.Register<ControlFlowGraphOutputFormat>();
        Logger.VerboseNewline("Control Flow Graph Plugin loaded and output format registered.");
    }
}
