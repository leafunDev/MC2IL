using System;
using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace MC2IL.ILanguageDecompiler;

// Remove compiler-shaped scalar temporaries when a pure value is used once shortly after
// its declaration. This recovers expressions split into locals while keeping effectful
// calls and values that cross control-flow or mutation boundaries in their original place.
internal sealed class InlineArithmeticTemporaries : IAstTransform
{
    public void Run(AstNode root, TransformContext context)
    {
        foreach (var block in root.Descendants.OfType<BlockStatement>().ToArray())
        {
            bool changed;
            do
            {
                changed = false;
                var statements = block.Statements.ToArray();
                foreach (var declaration in statements.OfType<VariableDeclarationStatement>().Reverse())
                {
                    if (declaration.Variables.Count != 1)
                        continue;

                    var variable = declaration.Variables.Single();
                    if (variable.Initializer is not { } initializer
                        || initializer.GetResolveResult().Type.FullName is not ("System.Single" or "System.Double")
                        || !IsPureArithmetic(initializer))
                        continue;

                    var uses = block.Descendants.OfType<IdentifierExpression>()
                        .Where(identifier => identifier.Identifier == variable.Name)
                        .ToArray();
                    if (uses.Length != 1 || uses[0].Parent is DirectionExpression
                        || !ReferenceEquals(NearestBlock(uses[0]), block) || IsConditionallyEvaluated(uses[0])
                        || uses[0].Ancestors.Any(node => node.GetType().Name.Contains("Lambda", StringComparison.Ordinal)))
                        continue;

                    var useStatement = DirectChildOf(block, uses[0]);
                    var definitionIndex = Array.IndexOf(statements, declaration);
                    var useIndex = Array.IndexOf(statements, useStatement);
                    if (useIndex <= definitionIndex || !CanMoveAcross(statements, definitionIndex, useIndex))
                        continue;

                    uses[0].ReplaceWith(initializer.Clone());
                    declaration.Remove();
                    changed = true;
                    break;
                }
            } while (changed);
        }
    }

    private static bool CanMoveAcross(AstNode[] statements, int definitionIndex, int useIndex)
    {
        for (var i = definitionIndex + 1; i < useIndex; i++)
        {
            if (statements[i] is not VariableDeclarationStatement declaration
                || declaration.Variables.Any(variable => variable.Initializer is not { } initializer
                    || !IsPureArithmetic(initializer)))
                return false;
        }

        return true;
    }

    private static bool IsPureArithmetic(Expression expression) => expression switch
    {
        PrimitiveExpression => true,
        IdentifierExpression identifier => identifier.GetResolveResult() is ILVariableResolveResult or LocalResolveResult
            || identifier.GetResolveResult() is MemberResolveResult { Member: IField },
        ParenthesizedExpression parenthesized => IsPureArithmetic(parenthesized.Expression),
        CastExpression cast => cast.GetResolveResult() is ConversionResolveResult { Conversion.IsUserDefined: false }
            && (cast.GetResolveResult().Type.FullName is "System.Single" or "System.Double")
            && IsPureArithmetic(cast.Expression),
        UnaryOperatorExpression unary => unary.Operator is UnaryOperatorType.Plus or UnaryOperatorType.Minus
            && unary.GetResolveResult() is OperatorResolveResult { UserDefinedOperatorMethod: null }
            && IsPureArithmetic(unary.Expression),
        BinaryOperatorExpression { Left: { } left, Right: { } right } binary => binary.Operator is BinaryOperatorType.Add or BinaryOperatorType.Subtract
            or BinaryOperatorType.Multiply or BinaryOperatorType.Divide or BinaryOperatorType.Modulus
            && (binary.GetResolveResult().Type.FullName is "System.Single" or "System.Double")
            && binary.GetResolveResult() is OperatorResolveResult { UserDefinedOperatorMethod: null }
            && IsPureArithmetic(left) && IsPureArithmetic(right),
        MemberReferenceExpression member => IsPureMember(member),
        _ => false
    };

    private static bool IsPureMember(MemberReferenceExpression member)
    {
        var symbol = member.GetSymbol();
        if (symbol is IField field)
        {
            if (field.IsStatic)
                return field.DeclaringType.FullName == "UnityEngine.Vector3"
                    && field.Name is "oneVector" or "zeroVector";

            return member.Target.GetResolveResult().Type.Kind is TypeKind.Struct or TypeKind.Enum
                && IsPureArithmetic(member.Target);
        }

        if (symbol is IProperty property)
            return property.DeclaringType.FullName == "UnityEngine.Vector3"
                && property.Getter?.IsStatic == true
                && property.Name is "oneVector" or "zeroVector";

        return false;
    }

    private static bool IsConditionallyEvaluated(AstNode node)
    {
        foreach (var ancestor in node.Ancestors)
        {
            if (ancestor is ConditionalExpression)
                return true;
            if (ancestor is BinaryOperatorExpression { Operator: BinaryOperatorType.ConditionalAnd or BinaryOperatorType.ConditionalOr,
                    Right: { } right } && IsDescendantOf(node, right))
                return true;
        }

        return false;
    }

    private static bool IsDescendantOf(AstNode node, AstNode parent)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
            if (ReferenceEquals(current, parent))
                return true;
        return false;
    }

    private static BlockStatement? NearestBlock(AstNode node) => node.Ancestors.OfType<BlockStatement>().FirstOrDefault();

    private static AstNode? DirectChildOf(BlockStatement block, AstNode node)
    {
        while (node.Parent != null && !ReferenceEquals(node.Parent, block))
            node = node.Parent;
        return ReferenceEquals(node.Parent, block) ? node : null;
    }
}
