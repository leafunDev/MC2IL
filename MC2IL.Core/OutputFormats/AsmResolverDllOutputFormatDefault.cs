using AsmResolver.DotNet;
using AssetRipper.CIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.OutputFormats;

public class AsmResolverDllOutputFormatDefault : AsmResolverDllOutputFormat
{
    public override string OutputFormatId => "dll_default";

    public override string OutputFormatName => "DLL files with default method bodies";

    protected override void FillMethodBody(MethodDefinition methodDefinition, MethodAnalysisContext methodContext)
    {
        FillMethodBodyWithStub(methodDefinition);
    }
}
