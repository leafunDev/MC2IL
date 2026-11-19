using MC2IL.Core.Api;
using MC2IL.Core.Attributes;
using MC2IL.Plugin.Pdb;

[assembly: RegisterMC2ILPlugin(typeof(PdbOutputPlugin))]

namespace MC2IL.Plugin.Pdb;

public class PdbOutputPlugin : MC2ILPlugin
{
    public override string Name => "PDB Output Plugin";

    public override string Description => "Adds an output format which generates debug symbols";

    public override void OnLoad()
    {
        OutputFormatRegistry.Register<PdbOutputFormat>();
    }
}
