using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Model.CustomAttributes;

public class CustomAttributeField(FieldAnalysisContext field, BaseCustomAttributeParameter value)
{
    public readonly FieldAnalysisContext Field = field;
    public readonly BaseCustomAttributeParameter Value = value;

    public override string ToString()
    {
        return $"{Field.Name} = {Value}";
    }
}
