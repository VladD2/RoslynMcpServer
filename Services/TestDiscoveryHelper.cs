using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcpServer.Services;

public static class TestDiscoveryHelper
{
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestMethod", "DataTestMethod", "TestCase"
    };

    public static async Task<string> ListTestsJsonAsync(Solution solution, int maxResults, CancellationToken cancellationToken)
    {
        maxResults = Math.Clamp(maxResults, 1, 500);
        var tests = new List<object>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is null || !document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || model is null)
                {
                    continue;
                }

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (!HasTestAttribute(method, model))
                    {
                        continue;
                    }

                    var symbol = model.GetDeclaredSymbol(method, cancellationToken);
                    if (symbol is null)
                    {
                        continue;
                    }

                    var className = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "?";
                    tests.Add(new
                    {
                        className,
                        methodName = symbol.Name,
                        fullyQualifiedName = BuildMethodFqn(symbol),
                        filePath = document.FilePath
                    });

                    if (tests.Count >= maxResults)
                    {
                        return Serialize(tests, truncated: true);
                    }
                }
            }
        }

        return Serialize(tests, truncated: false);
    }

    private static bool HasTestAttribute(MethodDeclarationSyntax method, SemanticModel model)
    {
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                // GetSymbolInfo on an AttributeSyntax yields the attribute's constructor symbol (".ctor"),
                // never the attribute class — GetTypeInfo(attr).Type resolves the attribute class itself.
                var attributeClass = model.GetTypeInfo(attr).Type;
                var name = attributeClass?.Name ?? attr.Name.ToString();
                if (name.EndsWith("Attribute", StringComparison.Ordinal))
                {
                    name = name[..^"Attribute".Length];
                }

                if (TestAttributes.Contains(name) || name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the VSTest-style fully qualified name (<c>Namespace.Class.Method</c>, no <c>global::</c>) for a
    /// test method. <c>IMethodSymbol.ToDisplayString(FullyQualifiedFormat)</c> is unreliable here — for methods
    /// obtained from <c>GetDeclaredSymbol</c> it returns only the method name, dropping the containing type —
    /// so the FQN is assembled from the containing type's (correct) FQN plus the method name.
    /// </summary>
    private static string BuildMethodFqn(ISymbol method)
    {
        var typeFqn = method.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty;
        if (typeFqn.StartsWith("global::", StringComparison.Ordinal))
        {
            typeFqn = typeFqn["global::".Length..];
        }

        return string.IsNullOrEmpty(typeFqn) ? method.Name : $"{typeFqn}.{method.Name}";
    }

    private static string Serialize(List<object> tests, bool truncated)
    {
        var payload = new Dictionary<string, object?>
        {
            ["count"] = tests.Count,
            ["truncated"] = truncated,
            ["tests"] = tests
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
