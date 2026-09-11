using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Post-filtering of <c>find_symbol_references</c> for virtual/override/abstract methods.
/// <c>SymbolFinder.FindReferencesAsync</c> (like VS 2022 "Find All References") returns every call site in the
/// whole virtual method family — any override, any receiver type. For a derived <em>override</em> that floods the
/// result with sibling/base receivers that never dispatch to the queried override. The tool now (a) notes how
/// many references are virtual dispatch sites by default and (b) with <c>directOnly: true</c> keeps only sites
/// whose static receiver type is the declaring type or a derived type.
/// </summary>
public sealed class VirtualReferencesTests : IClassFixture<VirtualReferencesTests.Fixture>
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

    // Line map (1-based):
    //   3  Base.ApplyChanges (virtual)
    //   8  Derived.ApplyChanges (override)
    //  10   this.ApplyChanges()            -> receiver Derived  (direct for Derived)
    //  27   b.ApplyChanges()  (b: Base)    -> receiver Base      (dispatch for Derived)
    //  28   d.ApplyChanges()  (d: Derived) -> receiver Derived  (direct for Derived)
    //  29   s.ApplyChanges()  (s: Sibling) -> receiver Sibling  (dispatch for Derived)
    //  30   g.ApplyChanges()  (g: GrandChild) -> receiver GrandChild (direct for Derived: derived type)
    //  31   d?.ApplyChanges() (d: Derived) -> receiver Derived  (direct for Derived)
    private const string Source = """
        public class Base
        {
            public virtual void ApplyChanges() { }
        }

        public class Derived : Base
        {
            public override void ApplyChanges()
            {
                this.ApplyChanges();
            }
        }

        public class Sibling : Base
        {
            public override void ApplyChanges() { }
        }

        public class GrandChild : Derived
        {
        }

        public class Client
        {
            public void Use(Base b, Derived d, Sibling s, GrandChild g)
            {
                b.ApplyChanges();
                d.ApplyChanges();
                s.ApplyChanges();
                g.ApplyChanges();
                d?.ApplyChanges();
            }
        }

        public class Caller
        {
            public void Go()
            {
                new Client().Use(null!, null!, null!, null!);
            }
        }
        """;

    private readonly Fixture _fixture;

    public VirtualReferencesTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DerivedOverride_default_notes_virtual_dispatch_sites()
    {
        var declaration = PositionOf(Source, "public override void ApplyChanges()");

        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, symbolName: "ApplyChanges", line: declaration.Line, cancellationToken: TestContext.Current.CancellationToken);

        // All 6 family call sites are still reported (default behaviour unchanged), plus the dispatch note.
        Assert.Contains("Found **6** reference location(s).", result);
        Assert.Contains(
            "> Note: symbol is virtual/override; 2 of the 6 reference(s) are virtual dispatch sites",
            result,
            StringComparison.Ordinal);
        Assert.Contains("Pass `directOnly: true` to keep only direct references.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("direct reference location(s)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DerivedOverride_directOnly_keeps_same_and_derived_receiver_sites_only()
    {
        var declaration = PositionOf(Source, "public override void ApplyChanges()");

        var result = await _fixture.Navigation.FindSymbolReferences(
            _fixture.SourcePath, symbolName: "ApplyChanges", line: declaration.Line, directOnly: true,
            cancellationToken: TestContext.Current.CancellationToken);

        // 4 direct (this/Derived/GrandChild/Derived?), 2 excluded (Base receiver, Sibling receiver).
        Assert.Contains("Found **4** direct reference location(s) (2 virtual dispatch site(s) excluded).", result);

        // Kept: this.ApplyChanges (10), d.ApplyChanges (28), g.ApplyChanges (30), d?.ApplyChanges (31).
        Assert.Contains("- 10:14", result, StringComparison.Ordinal);
        Assert.Contains("- 28:11", result, StringComparison.Ordinal);
        Assert.Contains("- 30:11", result, StringComparison.Ordinal);
        Assert.Contains("- 31:12", result, StringComparison.Ordinal);

        // Excluded: b.ApplyChanges (Base receiver, 27) and s.ApplyChanges (Sibling receiver, 29).
        Assert.DoesNotContain("- 27:11", result, StringComparison.Ordinal);
        Assert.DoesNotContain("- 29:11", result, StringComparison.Ordinal);
        Assert.DoesNotContain("virtual dispatch sites (the receiver", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaseVirtual_default_has_no_dispatch_note()
    {
        var declaration = PositionOf(Source, "public virtual void ApplyChanges()");

        var result = await _fixture.Navigation.FindSymbolReferences(_fixture.SourcePath, symbolName: "ApplyChanges", line: declaration.Line, cancellationToken: TestContext.Current.CancellationToken);

        // Every family receiver (Base/Derived/Sibling/GrandChild) is the base type or a derived type, so
        // nothing is a dispatch site and no note is emitted.
        Assert.Contains("Found **6** reference location(s).", result);
        Assert.DoesNotContain("virtual dispatch site", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaseVirtual_directOnly_keeps_all_sites()
    {
        var declaration = PositionOf(Source, "public virtual void ApplyChanges()");

        var result = await _fixture.Navigation.FindSymbolReferences(
            _fixture.SourcePath, symbolName: "ApplyChanges", line: declaration.Line, directOnly: true,
            cancellationToken: TestContext.Current.CancellationToken);

        // directOnly is a no-op for the base virtual method (0 dispatch sites) — plain (non-direct) header.
        Assert.Contains("Found **6** reference location(s).", result);
        Assert.DoesNotContain("direct reference location(s)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonVirtualMethod_directOnly_is_a_noop()
    {
        // `Use` is a plain instance method: no virtual family, so directOnly must not alter the output.
        var declaration = PositionOf(Source, "public void Use(");
        var usage = PositionOf(Source, "new Client().Use(", charOffset: 13);

        var result = await _fixture.Navigation.FindSymbolReferences(
            _fixture.SourcePath, symbolName: "Use", line: declaration.Line, directOnly: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Found **1** reference location(s).", result);
        Assert.Contains($"- {usage.Line}:{usage.Column}", result, StringComparison.Ordinal);
        Assert.DoesNotContain("virtual dispatch site", result, StringComparison.Ordinal);
        Assert.DoesNotContain("direct reference location(s)", result, StringComparison.Ordinal);
    }

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

    /// <summary>One minimal real project (csproj + A.cs) loaded into a shared <see cref="SolutionManager"/>.</summary>
    public sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly SolutionManager _manager;

        public string SourcePath { get; }

        public NavigationTools Navigation { get; }

        public Fixture()
        {
            MsBuildTestRegistration.EnsureRegistered();

            _root = Path.Combine(Path.GetTempPath(), "RoslynMcpVirtualRefTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "T.csproj"), Csproj);
            SourcePath = Path.Combine(_root, "A.cs");
            File.WriteAllText(SourcePath, Source);

            var config = new WorkspaceConfig(new ConfigurationBuilder().Build());
            _manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
            try
            {
                _manager.LoadAsync(Path.Combine(_root, "T.csproj")).GetAwaiter().GetResult();
            }
            catch
            {
                _manager.ClearWorkspaceAsync().GetAwaiter().GetResult();
                throw;
            }

            Navigation = new NavigationTools(_manager, config, NullLogger<NavigationTools>.Instance);
        }

        public void Dispose()
        {
            try
            {
                _manager.ClearWorkspaceAsync().GetAwaiter().GetResult();
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
            }
        }
    }
}
