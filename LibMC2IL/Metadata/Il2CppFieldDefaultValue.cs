using LibMC2IL.BinaryStructures;

namespace LibMC2IL.Metadata;

public class Il2CppFieldDefaultValue : ReadableClass
{
    public Il2CppVariableWidthIndex<Il2CppFieldDefinition> fieldIndex;
    public Il2CppVariableWidthIndex<Il2CppType> typeIndex;
    public Il2CppVariableWidthIndex<Il2CppDefaultValueDataDummy> dataIndex;

    public object? Value => dataIndex.IsNull ? null : LibMC2ILUtils.GetDefaultValue(dataIndex, typeIndex, OwningContext);

    public override void Read(ClassReadingBinaryReader reader)
    {
        fieldIndex = Il2CppVariableWidthIndex<Il2CppFieldDefinition>.Read(reader);
        typeIndex = Il2CppVariableWidthIndex<Il2CppType>.Read(reader);
        dataIndex = Il2CppVariableWidthIndex<Il2CppDefaultValueDataDummy>.Read(reader);
    }
}
