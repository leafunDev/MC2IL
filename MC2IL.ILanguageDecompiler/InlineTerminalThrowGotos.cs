using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;

namespace MC2IL.ILanguageDecompiler;

internal sealed class InlineTerminalThrowGotos : IAstTransform
{
    public void Run(AstNode root, TransformContext context)
    {
        foreach (var label in root.Descendants.OfType<LabelStatement>().ToArray())
        {
            var gotos = root.Descendants.OfType<GotoStatement>()
                .Where(statement => statement.Label == label.Label)
                .ToArray();

            if (label.Parent is not BlockStatement
                || label.PrevSibling is not ContinueStatement
                || label.NextSibling is not ThrowStatement { Expression: ObjectCreateExpression creation } throwStatement
                || creation.Arguments.Count != 0
                || !creation.Type.ToString().EndsWith("IndexOutOfRangeException", System.StringComparison.Ordinal))
                continue;

            if (gotos.Length == 0)
                continue;

            foreach (var statement in gotos)
                statement.ReplaceWith(new ThrowStatement { Expression = creation.Clone() });

            label.Remove();
            throwStatement.Remove();
        }
    }
}
