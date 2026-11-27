using System;
using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace MC2IL.ILanguageDecompiler;

/// <summary>
/// Rebuilds a call to a simple delegate-listener wrapper when IL2CPP inlined its body
/// into a caller and left the captured Action check and invocation behind.
/// </summary>
internal sealed class RestoreInlinedDelegateSend : IAstTransform
{
    public void Run(AstNode root, TransformContext context)
    {
        foreach (var block in root.Descendants.OfType<BlockStatement>().ToArray())
        {
            var statements = block.Statements.ToArray();
            for (var index = 0; index + 1 < statements.Length; index++)
            {
                if (statements[index] is not VariableDeclarationStatement
                    { Variables.Count: 1 } declaration
                    || declaration.Variables.Single() is not { Initializer: { } initializer } variable
                    || !TryGetListenerField(initializer, out var field, out var receiver)
                    || !TryGetSendMethod(field, receiver, out var send)
                    || statements[index + 1] is not IfElseStatement
                    { TrueStatement: { } trueStatement } conditional
                    || conditional.FalseStatement != null && conditional.FalseStatement.ToString().Length != 0
                    || !IsNonNullGuard(conditional.Condition, variable.Name)
                    || !ContainsOnlyInvocation(trueStatement, variable.Name)
                    || IsWrapperImplementation(conditional, send))
                    continue;

                var call = new ExpressionStatement(new InvocationExpression(
                    new MemberReferenceExpression(receiver.Clone(), send.Name)));
                conditional.ReplaceWith(call);
                declaration.Remove();
                index++;
            }
        }
    }

    private static bool TryGetListenerField(Expression initializer, out IField field, out Expression receiver)
    {
        if (initializer is MemberReferenceExpression { Target: { } target } member
            && member.GetSymbol() is IField instanceField)
        {
            field = instanceField;
            receiver = target;
            return !field.IsStatic;
        }

        if (initializer is IdentifierExpression identifier && identifier.GetSymbol() is IField implicitField
            && !implicitField.IsStatic && implicitField.DeclaringType != null)
        {
            field = implicitField;
            receiver = new ThisReferenceExpression();
            return true;
        }

        field = null!;
        receiver = null!;
        return false;
    }

    private static bool TryGetSendMethod(IField field, Expression receiver, out IMethod send)
    {
        var declaringType = field.DeclaringType;
        if (field.Name != "m_Listeners" || field.Type.FullName != "System.Action"
            || declaringType == null || receiver.GetResolveResult().Type.FullName != declaringType.FullName)
        {
            send = null!;
            return false;
        }

        send = declaringType.GetMembers(member => member.Name == "Send").OfType<IMethod>().FirstOrDefault(method =>
            method.Name == "Send" && !method.IsStatic && !method.IsVirtual && method.Parameters.Count == 0
            && method.ReturnType.FullName == "System.Void"
            && method.DeclaringType.FullName == declaringType.FullName)!;
        return send != null;
    }

    private static bool IsNonNullGuard(Expression condition, string variableName)
    {
        var normalized = string.Concat(condition.ToString().Where(character => !char.IsWhiteSpace(character)));
        return normalized == $"{variableName}isnotnull"
            || normalized == $"{variableName}!=null" || normalized == $"null!={variableName}";
    }

    private static bool ContainsOnlyInvocation(Statement statement, string variableName)
    {
        var expression = statement switch
        {
            BlockStatement { Statements.Count: 1 } block => block.Statements.Single() as ExpressionStatement,
            ExpressionStatement expressionStatement => expressionStatement,
            _ => null
        };

        return expression?.Expression is InvocationExpression
        {
            Target: IdentifierExpression target,
            Arguments.Count: 0
        } && target.Identifier == variableName;
    }

    private static bool IsWrapperImplementation(IfElseStatement conditional, IMethod send) =>
        conditional.Ancestors.OfType<MethodDeclaration>().FirstOrDefault()?.GetSymbol() is IMethod enclosing
        && enclosing.Name == send.Name
        && enclosing.DeclaringType.FullName == send.DeclaringType.FullName;
}
