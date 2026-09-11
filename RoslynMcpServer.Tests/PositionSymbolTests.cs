using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Position-based symbol identity (plan §3.2 / §7 «PositionSymbolTests»): 1-based <c>line</c>/<c>column</c>
/// (on the declaration *or* a usage) resolves the exact declared symbol via the semantic model and disambiguates
/// overloads; <c>symbolName</c> without a position covers the extended declaration kinds
/// (property/field/event; the constructor is inherently ambiguous with the class name, which is asserted too);
/// <c>rename_symbol</c> with a position renames only the targeted overload (preview), and without a position
/// two same-named declarations produce a candidate-listing error.
/// File-scoped, so a minimal real project is loaded once per class via <see cref="MsBuildTestRegistration"/> +
/// <c>SolutionManager.LoadAsync</c> (the same pattern as <see cref="SearchOutputFormatTests"/>).
/// </summary>
public sealed class PositionSymbolTests : IClassFixture<PositionSymbolTests.Fixture>
{
    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    // Two overloads of `Work`, plus a property, a field, an event (explicit add/remove form —
    // EventDeclarationSyntax, which is the form the extended FindDeclarationMatches matches) and an
    // explicit constructor.
    private const string Source = """
        namespace PosNs
        {
            public class Service
            {
                private int _counter;

                public string Name { get; set; } = string.Empty;

                public event EventHandler? Ping
                {
                    add { }
                    remove { }
                }

                public Service()
                {
                }

                public void Work()
                {
                    _counter++;
                }

                public int Work(int x)
                {
                    return x;
                }
            }

            public class Caller
            {
                public void Use()
                {
                    var s = new Service();
                    s.Work();
                    s.Work(5);
                    s.Name = string.Empty;
                    s.Ping += (o, e) => { };
                }
            }
        }
        """;

    private readonly Fixture _fixture;

    public PositionSymbolTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FindSymbolReferences_position_on_usage_resolves_declared_symbol()
    {
        var usage = PositionOf(Source, "s.Work();", charOffset: 2);
        var otherUsage = PositionOf(Source, "s.Work(5);", charOffset: 2);

        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, line: usage.Line, column: usage.Column, cancellationToken: TestContext.Current.CancellationToken);

        // The position on the usage resolves to the declared overload `Work()`: its usage is reported,
        // the other overload's usage is not. (Roslyn 5.9 FindReferencesAsync reports usage locations only —
        // the declaration position itself is not listed.)
        Assert.Contains("References for `void Service.Work()`", result);
        Assert.Contains($"- {usage.Line}:{usage.Column}", result);
        Assert.DoesNotContain($"- {otherUsage.Line}:{otherUsage.Column}", result);
    }

    [Fact]
    public async Task FindSymbolReferences_position_on_declaration_resolves_same_symbol()
    {
        var declaration = PositionOf(Source, "void Work()", charOffset: 5);
        var usage = PositionOf(Source, "s.Work();", charOffset: 2);

        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, line: declaration.Line, column: declaration.Column, cancellationToken: TestContext.Current.CancellationToken);

        // A position on the declaration gives the same declared symbol as a position on its usage:
        // the same header (minimally qualified name) and the same reference set.
        Assert.Contains("References for `void Service.Work()`", result);
        Assert.Contains($"- {usage.Line}:{usage.Column}", result);
    }

    [Fact]
    public async Task FindSymbolReferences_position_disambiguates_overloads()
    {
        var declaration = PositionOf(Source, "int Work(", charOffset: 4);
        var usage = PositionOf(Source, "s.Work(5);", charOffset: 2);
        var otherUsage = PositionOf(Source, "s.Work();", charOffset: 2);

        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, line: declaration.Line, column: declaration.Column, cancellationToken: TestContext.Current.CancellationToken);

        // The two methods share the name `Work` on different lines; the position selects `Work(int)`.
        Assert.Contains("References for `int Service.Work(int x)`", result);
        Assert.Contains($"- {usage.Line}:{usage.Column}", result);
        Assert.DoesNotContain($"- {otherUsage.Line}:{otherUsage.Column}", result);
    }

    [Fact]
    public async Task FindSymbolReferences_line_and_column_are_one_based()
    {
        // The position is computed as 1-based line/column (LSP convention). If the tool treated the input as
        // 0-based, the offset would land on the next line (`s.Name = ...`) and a different symbol would be
        // reported (or none at all).
        var usage = PositionOf(Source, "s.Work(5);", charOffset: 2);

        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, line: usage.Line, column: usage.Column, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("References for `int Service.Work(int x)`", result);
        Assert.DoesNotContain("No symbol found", result);
        Assert.DoesNotContain("References for `Name`", result);
    }

    [Fact]
    public async Task FindSymbolReferences_symbolName_property_without_position()
    {
        var usage = PositionOf(Source, "s.Name", charOffset: 2);

        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, symbolName: "Name", cancellationToken: TestContext.Current.CancellationToken);

        // Extended FindDeclarationMatches: a property declaration is matched by name (and resolved to the
        // property symbol — the header carries the plain name, no `void Service.`-style method qualifier).
        Assert.Contains("References for `Name`", result);
        Assert.Contains($"- {usage.Line}:{usage.Column}", result);
    }

    [Fact]
    public async Task FindSymbolReferences_symbolName_field_without_position()
    {
        var usage = PositionOf(Source, "_counter++");

        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, symbolName: "_counter", cancellationToken: TestContext.Current.CancellationToken);

        // Extended FindDeclarationMatches: a field declaration (variable) is matched by name.
        Assert.Contains("References for `_counter`", result);
        Assert.Contains($"- {usage.Line}:{usage.Column}", result);
    }

    [Fact]
    public async Task FindSymbolReferences_symbolName_event_without_position()
    {
        var usage = PositionOf(Source, "s.Ping", charOffset: 2);

        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, symbolName: "Ping", cancellationToken: TestContext.Current.CancellationToken);

        // Extended FindDeclarationMatches: an event declaration (EventDeclarationSyntax) is matched by name.
        Assert.DoesNotContain("was not found as a declaration", result);
        Assert.Contains($"- {usage.Line}:{usage.Column}", result);
    }

    [Fact]
    public async Task FindSymbolReferences_position_on_constructor_declaration()
    {
        var declaration = PositionOf(Source, "public Service()", charOffset: 7);
        var usage = PositionOf(Source, "new Service()", charOffset: 4);

        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, line: declaration.Line, column: declaration.Column, cancellationToken: TestContext.Current.CancellationToken);

        // The position selects the constructor, not the same-named class declaration (the class header would
        // be a bare `Service`, without the `Service.Service()` constructor qualifier).
        Assert.Contains("References for `Service.Service()`", result);
        Assert.Contains($"- {usage.Line}:{usage.Column}", result);
    }

    [Fact]
    public async Task FindSymbolReferences_symbolName_constructor_lists_class_and_ctor_candidates()
    {
        // A constructor always shares its name with the class declaration, so without a position the extended
        // matcher (ClassDeclarationSyntax + ConstructorDeclarationSyntax) reports both as candidates —
        // proof that the constructor kind is matched.
        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, symbolName: "Service", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("matches 2 declarations", result);
        Assert.Contains("global::PosNs.Service —", result);
        Assert.Contains("global::PosNs.Service..ctor —", result);
    }

    [Fact]
    public async Task RenameSymbol_position_renames_only_targeted_overload()
    {
        var declaration = PositionOf(Source, "int Work(", charOffset: 4);
        var usage = PositionOf(Source, "s.Work(5);", charOffset: 2);
        var otherDeclaration = PositionOf(Source, "void Work()", charOffset: 5);
        var otherUsage = PositionOf(Source, "s.Work();", charOffset: 2);

        var result = await _fixture.Utility.RenameSymbol(
            _fixture.SourcePath, "Work", "WorkInt", line: declaration.Line, column: declaration.Column, previewOnly: true,
            cancellationToken: TestContext.Current.CancellationToken);

        // The preview lists the affected locations of the selected overload only (`Work(int)` usage; the
        // other overload's declaration/usage and this one's declaration are absent).
        var affected = ExtractPreviewLineNumbers(result);
        Assert.Contains(usage.Line, affected);
        Assert.DoesNotContain(otherUsage.Line, affected);
        Assert.DoesNotContain(otherDeclaration.Line, affected);
        Assert.DoesNotContain(declaration.Line, affected);
    }

    [Fact]
    public async Task RenameSymbol_without_position_two_same_named_declarations_error_lists_candidates()
    {
        var workOne = PositionOf(Source, "void Work()", charOffset: 5);
        var workTwo = PositionOf(Source, "int Work(", charOffset: 4);

        var result = await _fixture.Utility.RenameSymbol(_fixture.SourcePath, "Work", "WorkRenamed", previewOnly: true, cancellationToken: TestContext.Current.CancellationToken);

        // No blind first match: two `Work` declarations in the file → error with both candidates
        // (FQN display + line:col of each declaration).
        Assert.Contains("matches 2 declarations", result);
        Assert.Contains("Provide 1-based `line`/`column`", result);
        Assert.Equal(2, CountOccurrences(result, "- Work —"));
        Assert.Contains($"- Work — {workOne.Line}:", result);
        Assert.Contains($"- Work — {workTwo.Line}:", result);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>
    /// 1-based line/column of the <paramref name="anchor"/> occurrence in <paramref name="source"/>, shifted by
    /// <paramref name="charOffset"/> characters (to land on the identifier, not the anchor start).
    /// </summary>
    private static (int Line, int Column) PositionOf(string source, string anchor, int occurrence = 0, int charOffset = 0)
    {
        var index = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            index = source.IndexOf(anchor, index + 1, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException($"Anchor '{anchor}' (occurrence {occurrence}) not found in source.");
            }
        }

        index += charOffset;

        var line = 1;
        var lastBreak = -1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                lastBreak = i;
            }
        }

        return (line, index - lastBreak);
    }

    /// <summary>Extracts the 1-based line numbers from <c>rename_symbol</c> preview lines (<c>- &lt;file&gt;:N</c>).</summary>
    private static List<int> ExtractPreviewLineNumbers(string result)
    {
        var numbers = new List<int>();
        foreach (var rawLine in result.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            var colon = line.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(line[(colon + 1)..], out var number))
            {
                numbers.Add(number);
            }
        }

        return numbers;
    }

    /// <summary>One minimal real project (csproj + A.cs) loaded into a shared <see cref="SolutionManager"/>.</summary>
    public sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public string SourcePath { get; }

        public SolutionManager Manager { get; }

        public WorkspaceConfig Config { get; }

        public NavigationTools Navigation { get; }

        public UtilityTools Utility { get; }

        public Fixture()
        {
            MsBuildTestRegistration.EnsureRegistered();

            _root = Path.Combine(Path.GetTempPath(), "RoslynMcpPositionTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "T.csproj"), Csproj);
            SourcePath = Path.Combine(_root, "A.cs");
            File.WriteAllText(SourcePath, Source);

            Config = new WorkspaceConfig(new ConfigurationBuilder().Build());
            Manager = new SolutionManager(NullLogger<SolutionManager>.Instance, Config);
            try
            {
                Manager.LoadAsync(Path.Combine(_root, "T.csproj")).GetAwaiter().GetResult();
            }
            catch
            {
                Manager.ClearWorkspaceAsync().GetAwaiter().GetResult();
                throw;
            }

            Navigation = new NavigationTools(Manager, Config, NullLogger<NavigationTools>.Instance);
            Utility = new UtilityTools(NullLogger<UtilityTools>.Instance, Manager, Config);
        }

        public void Dispose()
        {
            try
            {
                Manager.ClearWorkspaceAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // best effort
            }

            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
