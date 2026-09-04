using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynMcpServer.Services;

public static class AstModificationHelper
{
    public static Task<Document> OrganizeUsingsAsync(Document document, bool removeUnused, CancellationToken cancellationToken) =>
        MutateAsync(document, async (doc, root, ct) =>
        {
            var compilationUnit = TypeSyntaxHelper.RequireCompilationUnit(root);
            var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Could not obtain semantic model.");

            var usings = compilationUnit.Usings.ToList();
            if (removeUnused)
            {
                usings = usings.Where(u => !IsUsingUnused(u, compilationUnit, model)).ToList();
            }

            usings = usings
                .OrderBy(u => u.Name?.ToString(), StringComparer.OrdinalIgnoreCase)
                .Distinct(UsingDirectiveComparer.Instance)
                .ToList();

            return doc.WithSyntaxRoot(compilationUnit.WithUsings(new SyntaxList<UsingDirectiveSyntax>(usings)));
        }, cancellationToken);

    public static Task<Document> UpdateMethodBodyAsync(
        Document document,
        string className,
        string methodName,
        string newBody,
        IReadOnlyList<string>? parameterTypes,
        CancellationToken cancellationToken) =>
        MutateAsync(document, async (doc, root, ct) =>
        {
            var classDecl = TypeSyntaxHelper.FindClassDeclaration(root, className.Trim())
                ?? throw new InvalidOperationException($"Class `{className}` not found in file.");

            var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            var method = MethodSyntaxHelper.FindMethod(classDecl, methodName.Trim(), parameterTypes, model);
            var newBlock = MethodSyntaxHelper.ParseNewMethodBody(newBody);

            var updatedMethod = method
                .WithBody(newBlock)
                .WithExpressionBody(null)
                .WithSemicolonToken(default);

            MethodSyntaxHelper.ThrowIfSyntaxErrors(updatedMethod, "Updated method body");

            var newRoot = root.ReplaceNode(method, updatedMethod);
            return doc.WithSyntaxRoot(newRoot);
        }, cancellationToken);

    public static Task<Document> AddMemberAsync(
        Document document,
        string className,
        string memberSource,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(memberSource))
        {
            throw new ArgumentException("memberSource is empty.");
        }

        var member = SyntaxFactory.ParseMemberDeclaration(memberSource.Trim())
            ?? throw new InvalidOperationException("Could not parse member source.");

        if (member is not (MethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax))
        {
            throw new InvalidOperationException(
                $"Unsupported member kind `{member.GetType().Name}`. "
                + "add_member supports method, property, or field declarations "
                + "(events, constructors, records, and nested types are not supported).");
        }

        return MutateAsync(document, async (doc, root, ct) =>
        {
            var classDecl = TypeSyntaxHelper.FindClassDeclaration(root, className.Trim())
                ?? throw new InvalidOperationException($"Class `{className}` not found in file.");

            var editor = await DocumentEditor.CreateAsync(doc, ct).ConfigureAwait(false);
            editor.AddMember(classDecl, member);
            return editor.GetChangedDocument();
        }, cancellationToken);
    }

    private static async Task<Document> MutateAsync(
        Document document,
        Func<Document, SyntaxNode, CancellationToken, Task<Document>> mutate,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not obtain syntax tree.");
        var updated = await mutate(document, root, cancellationToken).ConfigureAwait(false);
        return await Formatter.FormatAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool IsUsingUnused(UsingDirectiveSyntax usingDirective, CompilationUnitSyntax compilationUnit, SemanticModel model)
    {
        if (usingDirective.Name is null)
        {
            return false;
        }

        var symbolInfo = model.GetSymbolInfo(usingDirective.Name);
        if (symbolInfo.Symbol is not INamespaceSymbol namespaceSymbol)
        {
            return false;
        }

        foreach (var node in compilationUnit.Members.SelectMany(m => m.DescendantNodesAndSelf()))
        {
            if (node is UsingDirectiveSyntax)
            {
                continue;
            }

            if (node is IdentifierNameSyntax id)
            {
                var info = model.GetSymbolInfo(id);
                if (SymbolBelongsToNamespace(info.Symbol, namespaceSymbol))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SymbolBelongsToNamespace(ISymbol? symbol, INamespaceSymbol ns)
    {
        if (symbol is null)
        {
            return false;
        }

        var containing = symbol.ContainingNamespace;
        while (containing is not null && !containing.IsGlobalNamespace)
        {
            if (SymbolEqualityComparer.Default.Equals(containing, ns))
            {
                return true;
            }

            containing = containing.ContainingNamespace;
        }

        return false;
    }

    private sealed class UsingDirectiveComparer : IEqualityComparer<UsingDirectiveSyntax>
    {
        public static UsingDirectiveComparer Instance { get; } = new();

        public bool Equals(UsingDirectiveSyntax? x, UsingDirectiveSyntax? y) =>
            string.Equals(x?.Name?.ToString(), y?.Name?.ToString(), StringComparison.Ordinal);

        public int GetHashCode(UsingDirectiveSyntax obj) =>
            obj.Name?.ToString().GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
