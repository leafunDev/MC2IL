using AsmResolver.DotNet;
using AssetRipper.CIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.OutputFormats;

public class AsmResolverDllOutputFormatEmpty : AsmResolverDllOutputFormat
{
    public override string OutputFormatId => "dll_empty";

    public override string OutputFormatName => "DLL files with empty method bodies";

    protected override void FillMethodBody(MethodDefinition methodDefinition, MethodAnalysisContext methodContext)
    {
        if (methodDefinition.IsManagedMethodWithBody())
            methodDefinition.CilMethodBody = new();
    }
}
