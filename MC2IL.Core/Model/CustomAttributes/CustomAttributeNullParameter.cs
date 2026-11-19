using System.IO;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Model.CustomAttributes;

public sealed class CustomAttributeNullParameter(
    AnalyzedCustomAttribute owner,
    CustomAttributeParameterKind kind,
    int index)
    : BaseCustomAttributeParameter(owner, kind, index)
{
    public override void ReadFromV29Blob(BinaryReader reader, ApplicationAnalysisContext context) => throw new System.NotSupportedException();
}
