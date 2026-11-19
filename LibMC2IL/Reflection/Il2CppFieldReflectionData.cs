using System.Reflection;
using LibMC2IL.Metadata;

namespace LibMC2IL.Reflection;

public class Il2CppFieldReflectionData(
    Il2CppFieldDefinition field,
    FieldAttributes attributes,
    object? defaultValue,
    int indexInParent,
    int fieldOffset)
{
    public Il2CppFieldDefinition Field = field;
    public FieldAttributes Attributes = attributes;
    public object? DefaultValue = defaultValue;
    public int IndexInParent = indexInParent;
    public int FieldOffset = fieldOffset;
}
