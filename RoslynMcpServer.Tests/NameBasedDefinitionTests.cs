using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// <c>find_symbol_definition</c> without <c>filePath</c> (name-based, solution-wide): lists every
/// declaration matching <c>symbolName</c> — one declaration → the standard definition format,
/// several → a summary table (FQN + file + 1-based line:col), a dotted name → exact FQN match,
/// <c>line</c>/<c>column</c> without <c>filePath</c> → validation error.
/// </summary>
public sealed class NameBasedDefinitionTests
{
    // `public class Widget` at 3:18.
    private const string SingleSource = """
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

    // `public class Guard` at 3:18 (Ns1) and 11:18 (Ns2).
    private const string MultipleSource = """
        namespace Ns1
        {
            public class Guard
            {
                public void Run() { }
            }
        }

        namespace Ns2
        {
            public class Guard
            {
                public void Run() { }
            }
        }
        """;

    [Fact]
    public async Task FindSymbolDefinition_nameOnly_singleDeclaration()
    {
        using var search = AdhocSearchTool.Create(SingleSource);

        var result = await search.Tool.FindSymbolDefinition(symbolName: "Widget");

        Assert.Contains("## Definition for `Widget`", result, StringComparison.Ordinal);
        Assert.Contains("Found **1** source location(s)", result, StringComparison.Ordinal);
        Assert.Contains("FQN: global::Ns1.Widget", result, StringComparison.Ordinal);
        Assert.Contains("Line: 3", result, StringComparison.Ordinal);
        Assert.Contains("Col: 18", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolDefinition_nameOnly_multipleDeclarations()
    {
        using var search = AdhocSearchTool.Create(MultipleSource);

        var result = await search.Tool.FindSymbolDefinition(symbolName: "Guard");

        Assert.Contains("2 declaration(s) match `Guard`", result, StringComparison.Ordinal);
        Assert.Contains("| FQN | File | Line:Col |", result, StringComparison.Ordinal);
        Assert.Contains("| global::Ns1.Guard |", result, StringComparison.Ordinal);
        Assert.Contains("| global::Ns2.Guard |", result, StringComparison.Ordinal);
        Assert.Contains("3:18 |", result, StringComparison.Ordinal);
        Assert.Contains("11:18 |", result, StringComparison.Ordinal);
        // Not the single-declaration format.
        Assert.DoesNotContain("Found **1** source location(s)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolDefinition_nameOnly_fqn_selects_exact_declaration_only()
    {
        using var search = AdhocSearchTool.Create(MultipleSource);

        var result = await search.Tool.FindSymbolDefinition(symbolName: "Ns1.Guard");

        Assert.Contains("## Definition for `Ns1.Guard`", result, StringComparison.Ordinal);
        Assert.Contains("FQN: global::Ns1.Guard", result, StringComparison.Ordinal);
        Assert.DoesNotContain("global::Ns2.Guard", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolDefinition_nameOnly_noDeclarations()
    {
        using var search = AdhocSearchTool.Create(SingleSource);

        var result = await search.Tool.FindSymbolDefinition(symbolName: "Nope");

        Assert.Contains("No declarations found for `Nope`", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolDefinition_nameOnly_lineProvided_error()
    {
        using var search = AdhocSearchTool.Create(MultipleSource);

        var result = await search.Tool.FindSymbolDefinition(symbolName: "Guard", line: 3);

        Assert.Contains("Error: `line`/`column` require `filePath`. For name-based search, omit them.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolDefinition_nameOnly_noName_error()
    {
        using var search = AdhocSearchTool.Create(MultipleSource);

        var result = await search.Tool.FindSymbolDefinition();

        Assert.Contains("Error: provide `symbolName`.", result, StringComparison.Ordinal);
    }
}
