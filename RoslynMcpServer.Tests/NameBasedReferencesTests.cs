using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// <c>find_symbol_references</c> without <c>filePath</c> (name-based, solution-wide): for every declaration
/// matching <c>symbolName</c> reports all in-source references (the former <c>find_usages</c> behavior and
/// output format); <c>directOnly</c>/<c>line</c>/<c>column</c> without <c>filePath</c> → validation errors.
/// </summary>
public sealed class NameBasedReferencesTests
{
    // `Widget` declared at 3:18, referenced at 12:25 and 13:25.
    private const string Source = """
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
                    var x = new Widget();
                    w.Spin();
                }
            }
        }
        """;

    [Fact]
    public async Task FindSymbolReferences_nameOnly_reports_declarations_and_references()
    {
        using var search = AdhocSearchTool.Create(Source);

        var result = await search.Tool.FindSymbolReferences(symbolName: "Widget");

        Assert.Contains("## Usages for `Widget`", result, StringComparison.Ordinal);
        Assert.Contains("`global::Ns1.Widget`", result, StringComparison.Ordinal);
        Assert.Contains("Found **2** reference location(s)", result, StringComparison.Ordinal);
        Assert.Contains("- 12:25", result, StringComparison.Ordinal);
        Assert.Contains("- 13:25", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolReferences_nameOnly_directOnly_error()
    {
        using var search = AdhocSearchTool.Create(Source);

        var result = await search.Tool.FindSymbolReferences(symbolName: "Widget", directOnly: true);

        Assert.Contains("Error: `directOnly` is only valid with `filePath`.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolReferences_nameOnly_lineProvided_error()
    {
        using var search = AdhocSearchTool.Create(Source);

        var result = await search.Tool.FindSymbolReferences(symbolName: "Widget", line: 3);

        Assert.Contains("Error: `line`/`column` require `filePath`. For name-based search, omit them.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolReferences_nameOnly_noName_error()
    {
        using var search = AdhocSearchTool.Create(Source);

        var result = await search.Tool.FindSymbolReferences();

        Assert.Contains("Error: provide `symbolName`.", result, StringComparison.Ordinal);
    }
}
