using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Regression for the FastGlobGrep case (plan §3.4): when several declarations share a simple name,
/// <c>find_symbol_references</c> without <c>filePath</c> (the former <c>find_usages</c>) must report all of
/// them — a summary table plus per-FQN sections with references grouped by fully-qualified name — instead of
/// blindly picking a "primary" symbol and dumping the rest under "Other candidates". A single declaration
/// keeps the plain (table-less) format. Two same-named <em>types</em> in different namespaces are used so the
/// FQNs are distinct (<c>global::Ns1.FastGlobGrep</c> vs <c>global::Ns2.FastGlobGrep</c>); an unused type
/// yields exactly 0 references (a type's own declaration is not counted as a reference by <c>SymbolFinder</c>).
/// </summary>
public sealed class FindSymbolReferencesGroupingTests
{
    // Ns1.FastGlobGrep is referenced twice (12:25, 13:25); Ns2.FastGlobGrep is never referenced.
    private const string TwoDeclarationsSource = """
        namespace Ns1
        {
            public class FastGlobGrep
            {
                public void Search() { }
            }

            public class ClientA
            {
                public void Use()
                {
                    var a = new FastGlobGrep();
                    var b = new FastGlobGrep();
                    a.Search();
                }
            }
        }

        namespace Ns2
        {
            public class FastGlobGrep
            {
                public void Search() { }
            }
        }
        """;

    // A single declaration `Widget` with one usage at 12:25.
    private const string OneDeclarationSource = """
        namespace Ns1
        {
            public class Widget
            {
                public void Spin() { }
            }

            public class Host
            {
                public void Go()
                {
                    var w = new Widget();
                }
            }
        }
        """;

    [Fact]
    public async Task FindSymbolReferences_nameOnly_two_declarations_produces_fqn_table_and_grouped_sections()
    {
        using var search = AdhocSearchTool.Create(TwoDeclarationsSource);

        var result = await search.Tool.FindSymbolReferences(symbolName: "FastGlobGrep", cancellationToken: TestContext.Current.CancellationToken);

        // Summary table with one row per declaration FQN (the First column disambiguates identical FQNs,
        // e.g. method overloads in the same type).
        Assert.Contains("2 declaration(s) match this name:", result, StringComparison.Ordinal);
        Assert.Contains("| FQN | References | First |", result, StringComparison.Ordinal);
        Assert.Contains("| global::Ns1.FastGlobGrep | 2 | 12:25 |", result, StringComparison.Ordinal);
        Assert.Contains("| global::Ns2.FastGlobGrep | 0 | — |", result, StringComparison.Ordinal);

        // Per-FQN sections, references grouped under the right FQN.
        const string nS1Header = "### `global::Ns1.FastGlobGrep`";
        const string nS2Header = "### `global::Ns2.FastGlobGrep`";
        var nS1Index = result.IndexOf(nS1Header, StringComparison.Ordinal);
        var nS2Index = result.IndexOf(nS2Header, StringComparison.Ordinal);
        Assert.True(nS1Index >= 0, $"missing section {nS1Header}:\n{result}");
        Assert.True(nS2Index > nS1Index, $"expected {nS2Header} after {nS1Header}:\n{result}");

        var nS1Section = result[nS1Index..nS2Index];
        Assert.Contains("- 12:25", nS1Section, StringComparison.Ordinal);
        Assert.Contains("- 13:25", nS1Section, StringComparison.Ordinal);

        var nS2Section = result[nS2Index..];
        Assert.Contains("(no in-source references)", nS2Section, StringComparison.Ordinal);

        // No blind "primary" pick, no "Other candidates" dump.
        Assert.DoesNotContain("Other candidates", result, StringComparison.Ordinal);
        Assert.DoesNotContain("primary", result, StringComparison.OrdinalIgnoreCase);
    }

    // T12a regression: two same-named METHODS in different classes (the real CodeBaseInfo/CppLspTool case).
    // CodeBaseInfo.FastGlobGrep is invoked twice; CppLspTool.FastGlobGrep once (a method's own declaration is
    // not counted as a reference, same as for types). With Roslyn 5.9 FullyQualifiedFormat (memberOptions = None)
    // both methods printed as bare `FastGlobGrep`, so the table rows and sections collided; the member FQN must
    // include the declaring type: `global::Mcp.Tools.CodeBaseInfo.FastGlobGrep` vs `global::Mcp.Tools.CppLspTool.FastGlobGrep`.
    private const string TwoMemberDeclarationsSource = """
        namespace Mcp.Tools
        {
            public class CodeBaseInfo
            {
                public string FastGlobGrep() => "db";
            }

            public class CppLspTool
            {
                public string FastGlobGrep() => "lsp";
            }

            public class Client
            {
                public void Use()
                {
                    var a = new CodeBaseInfo();
                    var b = new CppLspTool();
                    var s1 = a.FastGlobGrep();
                    var s2 = a.FastGlobGrep();
                    var s3 = b.FastGlobGrep();
                }
            }
        }
        """;

    [Fact]
    public async Task FindSymbolReferences_nameOnly_two_same_named_methods_in_different_classes_produce_distinct_fqns()
    {
        using var search = AdhocSearchTool.Create(TwoMemberDeclarationsSource);

        var result = await search.Tool.FindSymbolReferences(symbolName: "FastGlobGrep", cancellationToken: TestContext.Current.CancellationToken);

        // Table rows include the declaring type (owner), not the bare member name; the First column
        // carries the first reference position.
        Assert.Contains("2 declaration(s) match this name:", result, StringComparison.Ordinal);
        Assert.Contains("| global::Mcp.Tools.CodeBaseInfo.FastGlobGrep | 2 | 19:24 |", result, StringComparison.Ordinal);
        Assert.Contains("| global::Mcp.Tools.CppLspTool.FastGlobGrep | 1 | 21:24 |", result, StringComparison.Ordinal);
        Assert.DoesNotContain("| FastGlobGrep |", result, StringComparison.Ordinal);

        // Sections are distinct and carry their own references.
        const string codeBaseSection = "### `global::Mcp.Tools.CodeBaseInfo.FastGlobGrep`";
        const string cppLspSection = "### `global::Mcp.Tools.CppLspTool.FastGlobGrep`";
        var codeBaseIndex = result.IndexOf(codeBaseSection, StringComparison.Ordinal);
        var cppLspIndex = result.IndexOf(cppLspSection, StringComparison.Ordinal);
        Assert.True(codeBaseIndex >= 0, $"missing section {codeBaseSection}:\n{result}");
        Assert.True(cppLspIndex > codeBaseIndex, $"expected {cppLspSection} after {codeBaseSection}:\n{result}");

        Assert.Equal(2, CountPositionLines(result[codeBaseIndex..cppLspIndex]));
        Assert.Equal(1, CountPositionLines(result[cppLspIndex..]));
    }

    private static int CountPositionLines(string section) => section
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        .Count(l => l.StartsWith("- ", StringComparison.Ordinal));

    [Fact]
    public async Task FindSymbolReferences_nameOnly_single_declaration_has_no_table()
    {
        using var search = AdhocSearchTool.Create(OneDeclarationSource);

        var result = await search.Tool.FindSymbolReferences(symbolName: "Widget", cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("| FQN | References |", result, StringComparison.Ordinal);
        Assert.DoesNotContain("declaration(s) match this name", result, StringComparison.Ordinal);
        Assert.Contains("**Symbol:** `Widget`", result, StringComparison.Ordinal);
        Assert.Contains("- 12:25", result, StringComparison.Ordinal);
    }
}
