using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynMcpServer.Services;

public sealed record CallGraphNode(string DisplayName, string? FilePath, int? Line);

public sealed record CallGraphResult(
    string TargetDisplay,
    IReadOnlyList<CallGraphNode> Callers,
    IReadOnlyList<CallGraphNode> Callees);

public static class CallGraphHelper
{
    public static async Task<CallGraphResult> BuildCallGraphAsync(
        Solution solution,
        Document document,
        string className,
        string methodName,
        bool includeExternalCallees,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            throw new InvalidOperationException("Could not obtain syntax tree or semantic model.");
        }

        var classDecl = TypeSyntaxHelper.FindClassDeclaration(root, className.Trim())
            ?? throw new InvalidOperationException($"Class `{className}` not found in `{document.FilePath}`.");

        var methodDecl = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => string.Equals(m.Identifier.Text, methodName.Trim(), StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Method `{methodName}` not found in class `{className}`.");

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken) as IMethodSymbol
            ?? throw new InvalidOperationException($"Could not resolve symbol for `{className}.{methodName}`.");

        return await BuildGraphForMethodAsync(
            solution, methodSymbol, methodDecl, includeExternalCallees, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the call graph for the method resolved at a 1-based <paramref name="line"/>/<paramref name="column"/>
    /// position (LSP model) — on the declaration or an invocation (the declared symbol is used).
    /// </summary>
    public static async Task<CallGraphResult> BuildCallGraphAtPositionAsync(
        Solution solution,
        Document document,
        int line,
        int column,
        bool includeExternalCallees,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            throw new InvalidOperationException("Could not obtain syntax tree or semantic model.");
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var (offset, positionError) = SourcePositionHelper.ToOffset(text, line, column);
        if (positionError is not null)
        {
            throw new InvalidOperationException(positionError);
        }

        var symbolAtPosition = SourcePositionHelper.GetSymbolAtPosition(root, semanticModel, offset, cancellationToken);
        if (symbolAtPosition is null)
        {
            throw new InvalidOperationException($"No symbol found at line {line}, column {column} in `{document.FilePath}`.");
        }

        var methodSymbol = symbolAtPosition as IMethodSymbol
            ?? throw new InvalidOperationException(
                $"The symbol at line {line}, column {column} is not a method (it is a {symbolAtPosition.Kind}).");

        MethodDeclarationSyntax? methodDecl = null;
        foreach (var syntaxReference in methodSymbol.DeclaringSyntaxReferences)
        {
            var syntax = await syntaxReference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            if (syntax is MethodDeclarationSyntax method)
            {
                methodDecl = method;
                break;
            }
        }

        if (methodDecl is null)
        {
            throw new InvalidOperationException(
                $"Could not find the declaring syntax for `{methodSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}`.");
        }

        return await BuildGraphForMethodAsync(
            solution, methodSymbol, methodDecl, includeExternalCallees, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CallGraphResult> BuildGraphForMethodAsync(
        Solution solution,
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodDecl,
        bool includeExternalCallees,
        CancellationToken cancellationToken)
    {
        var targetDisplay = methodSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        var callers = await CollectCallersAsync(solution, methodSymbol, cancellationToken).ConfigureAwait(false);
        var declaringModel = await GetDeclaringSemanticModelAsync(solution, methodDecl, cancellationToken).ConfigureAwait(false);
        var callees = CollectCallees(declaringModel, methodDecl, solution, includeExternalCallees);

        return new CallGraphResult(
            targetDisplay,
            callers,
            callees);
    }

    private static async Task<SemanticModel> GetDeclaringSemanticModelAsync(
        Solution solution,
        MethodDeclarationSyntax methodDecl,
        CancellationToken cancellationToken)
    {
        var syntaxTree = methodDecl.SyntaxTree;
        var document = syntaxTree is not null ? solution.GetDocument(syntaxTree) : null;
        var semanticModel = document is not null
            ? await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return semanticModel ?? throw new InvalidOperationException("Could not obtain semantic model for the method declaration.");
    }

    public static string FormatMarkdown(CallGraphResult graph)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Call graph");
        sb.AppendLine();
        sb.AppendLine($"**Target:** `{graph.TargetDisplay}`");
        sb.AppendLine();

        sb.AppendLine("### Callers (who invokes this method)");
        if (graph.Callers.Count == 0)
        {
            sb.AppendLine("- (none found in loaded solution)");
        }
            else
            {
                foreach (var caller in graph.Callers)
                {
                    sb.AppendLine(FormatNode("- ", caller));
                }
            }

        sb.AppendLine();
        sb.AppendLine("### Callees (what this method calls)");
        if (graph.Callees.Count == 0)
        {
            sb.AppendLine("- (none found in method body)");
        }
        else
        {
            foreach (var callee in graph.Callees)
            {
                sb.AppendLine(FormatNode("- ", callee));
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatNode(string prefix, CallGraphNode node)
    {
        if (node.FilePath is not null && node.Line is not null)
        {
            return $"{prefix}`{node.DisplayName}` — `{node.FilePath}:{node.Line}`";
        }

        return $"{prefix}`{node.DisplayName}`";
    }

    private static async Task<List<CallGraphNode>> CollectCallersAsync(
        Solution solution,
        IMethodSymbol methodSymbol,
        CancellationToken cancellationToken)
    {
        var nodes = new List<CallGraphNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var callersEnumerable = await SymbolFinder.FindCallersAsync(methodSymbol, solution, cancellationToken).ConfigureAwait(false);
        foreach (var caller in callersEnumerable)
        {
            if (caller.CallingSymbol is null)
            {
                continue;
            }

            var display = caller.CallingSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            if (!seen.Add(display))
            {
                continue;
            }

            string? filePath = null;
            int? line = null;
            foreach (var sourceLocation in caller.Locations)
            {
                if (!sourceLocation.IsInSource)
                {
                    continue;
                }

                var span = sourceLocation.GetLineSpan();
                filePath = span.Path;
                line = span.StartLinePosition.Line + 1;
                break;
            }

            nodes.Add(new CallGraphNode(display, filePath, line));
        }

        return nodes;
    }

    private static List<CallGraphNode> CollectCallees(
        SemanticModel semanticModel,
        MethodDeclarationSyntax methodDecl,
        Solution solution,
        bool includeExternalCallees)
    {
        var nodes = new List<CallGraphNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var invocation in methodDecl.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation.Expression);
            if (symbolInfo.Symbol is not ISymbol symbol)
            {
                continue;
            }

            var method = symbol switch
            {
                IMethodSymbol m => m,
                IPropertySymbol { IsIndexer: true } p => p.GetMethod,
                _ => null
            };

            if (method is null)
            {
                continue;
            }

            if (!includeExternalCallees && !IsSymbolFromLoadedSolution(method, solution))
            {
                continue;
            }

            var display = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            if (!seen.Add(display))
            {
                continue;
            }

            string? filePath = null;
            int? line = null;
            var declRef = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (declRef?.SyntaxTree?.FilePath is { Length: > 0 } fp)
            {
                filePath = fp;
                line = declRef.Span.Start;
                var lineSpan = declRef.SyntaxTree.GetLineSpan(declRef.Span);
                line = lineSpan.StartLinePosition.Line + 1;
            }

            nodes.Add(new CallGraphNode(display, filePath, line));
        }

        return nodes.OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsSymbolFromLoadedSolution(ISymbol symbol, Solution solution)
    {
        if (symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath is not { } path)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        return solution.Projects
            .SelectMany(p => p.Documents)
            .Any(d => d.FilePath is not null && string.Equals(Path.GetFullPath(d.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
    }
}
