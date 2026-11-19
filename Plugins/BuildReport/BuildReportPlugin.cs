using MC2IL.Core.Api;
using MC2IL.Core.Attributes;
using MC2IL.Plugin.BuildReport;

[assembly: RegisterMC2ILPlugin(typeof(BuildReportPlugin))]

namespace MC2IL.Plugin.BuildReport;

public class BuildReportPlugin : MC2ILPlugin
{
    public override string Name => "Build Report Plugin";
    public override string Description => "Adds an output format which generates information useful to the developer about what is taking up space in the build process";

    public override void OnLoad()
    {
        OutputFormatRegistry.Register<BuildReportOutputFormat>();
        Logger.VerboseNewline("Build Report Plugin loaded and output format registered.");
    }
}
