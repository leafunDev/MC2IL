using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Model.CustomAttributes;

public class CustomAttributeProperty(PropertyAnalysisContext property, BaseCustomAttributeParameter value)
{
    public readonly PropertyAnalysisContext Property = property;
    public readonly BaseCustomAttributeParameter Value = value;

    public override string ToString()
    {
        return $"{Property.Name} = {Value}";
    }
}
