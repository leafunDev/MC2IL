using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using ILFunction = ICSharpCode.Decompiler.IL.ILFunction;
using ILFunctionKind = ICSharpCode.Decompiler.IL.ILFunctionKind;

namespace MC2IL.ILanguageDecompiler;

internal sealed class SimplifyReferenceNullChecks : IAstTransform
{
    public void Run(AstNode root, TransformContext context)
    {
        if (context.CurrentModule is not { } module)
            return;

        var resolver = new CSharpResolver(module.Compilation);
        foreach (var invocation in root.Descendants.OfType<InvocationExpression>().Reverse().ToArray())
        {
            if (invocation.GetSymbol() is IMethod { Name: "ReferenceEquals", IsStatic: true, Parameters.Count: 2 } method
                && method.DeclaringType?.IsKnownType(KnownTypeCode.Object) == true
                && invocation.Arguments.Count == 2
                && GetComparedValue(invocation.Arguments.First(), invocation.Arguments.Last()) is { } value)
                Simplify(invocation, value, isNotNull: false, resolver);
        }

        foreach (var comparison in root.Descendants.OfType<BinaryOperatorExpression>().Reverse().ToArray())
        {
            if (comparison.Operator is BinaryOperatorType.Equality or BinaryOperatorType.InEquality
                && GetComparedValue(comparison.Left, comparison.Right) is CastExpression
                    { Type: PrimitiveType { Keyword: "object" }, Expression: { } value })
                Simplify(comparison, value, comparison.Operator == BinaryOperatorType.InEquality, resolver);
        }
    }

    private static Expression? GetComparedValue(Expression? left, Expression? right) =>
        left is NullReferenceExpression ? right : right is NullReferenceExpression ? left : null;

    private static void Simplify(Expression original, Expression value, bool isNotNull, CSharpResolver resolver)
    {
        if (value is NullReferenceExpression || value.GetResolveResult().Type is not { IsReferenceType: true } type)
            return;

        // Expression trees preserve the operation as data and cannot contain a null pattern.
        if (original.Ancestors.Any(node => node.Annotations.OfType<ILFunction>()
                .Any(function => function.Kind == ILFunctionKind.ExpressionTree)))
            return;

        if (original.Parent is UnaryOperatorExpression { Operator: UnaryOperatorType.Not } negation)
        {
            original = negation;
            isNotNull = !isNotNull;
        }

        var comparison = isNotNull ? BinaryOperatorType.InEquality : BinaryOperatorType.Equality;
        var nullLiteral = new NullReferenceExpression();

        // UnityEngine.Object and other overloaded operators can treat a live CLR
        // reference as null. Use a pattern when == or != could invoke one of them.
        var canUseOperator = type.Kind is TypeKind.Class or TypeKind.Interface or TypeKind.Array or TypeKind.Delegate
            && type.GetAllBaseTypes().All(baseType => baseType.Kind != TypeKind.Unknown)
            && resolver.ResolveBinaryOperator(comparison, new ResolveResult(type),
                new ConstantResolveResult(SpecialType.NullType, null))
                is OperatorResolveResult { IsError: false, UserDefinedOperatorMethod: null };

        original.ReplaceWith(canUseOperator
            ? new BinaryOperatorExpression(value.Clone(), comparison, nullLiteral)
            : new BinaryOperatorExpression(value.Clone(), BinaryOperatorType.IsPattern,
                isNotNull ? new UnaryOperatorExpression(UnaryOperatorType.PatternNot, nullLiteral) : nullLiteral));
    }
}
