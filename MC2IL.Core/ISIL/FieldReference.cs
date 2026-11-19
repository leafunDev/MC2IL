using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.ISIL;

public class FieldReference(FieldAnalysisContext field, LocalVariable local, int offset, FieldReference? parent = null) : IOperand
{
    public FieldAnalysisContext Field = field;
    public LocalVariable Local = local;
    public int Offset = offset;
    public FieldReference? Parent = parent;

    public override string ToString() => $"{Parent?.ToString() ?? Local.Name}.{Field.Name} ({Field.FieldType.FullName})";
}
