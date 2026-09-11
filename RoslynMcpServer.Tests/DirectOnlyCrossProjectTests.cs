using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Regression tests for the <c>directOnly</c> filter of <c>find_symbol_references</c> across project
/// boundaries. The real-world failure: a call site in a project that consumes the declaring project's
/// **built output** sees the receiver as a <em>metadata</em> <see cref="INamedTypeSymbol"/>, while the
/// declaring type is a <em>source</em> symbol. <see cref="SymbolEqualityComparer.Default"/> does not match
/// a source symbol against its metadata counterpart, so <c>IsSameOrDerivedFrom</c> used to report every
/// cross-project call site as a virtual dispatch site and <c>directOnly: true</c> dropped all of them
/// ("No direct references found … all N reference(s) are virtual dispatch sites").
/// </summary>
public sealed class DirectOnlyCrossProjectTests
{
    private const string SourceA = """
        public class Base
        {
            public virtual void ApplyChanges() { }
        }

        public class Derived : Base
        {
            public override void ApplyChanges()
            {
                base.ApplyChanges();
            }
        }
        """;

    private const string SourceB = """
        public class Client
        {
            private Derived _derived;
            private Base _base;
        }
        """;

    /// <summary>
    /// The core regression: a <em>metadata</em> receiver type (from a project referencing the declaring
    /// project's built assembly) must be recognized as the same type as the <em>source</em> declaring
    /// type, so the call site is classified as direct. Fails before the fix (source/metadata mismatch).
    /// </summary>
    [Fact]
    public void IsSameOrDerivedFrom_metadataDerivedReceiver_matchesSourceDeclaringType()
    {
        var (sourceDerived, metaDerived, _) = BuildSourceAndMetadataTypes();

        Assert.True(
            NavigationTools.IsSameOrDerivedFrom(metaDerived, sourceDerived),
            "A metadata receiver of the declaring type must be treated as 'the same type' (direct).");
    }

    /// <summary>
    /// A metadata receiver of a <em>base</em> type must NOT be treated as the derived declaring type
    /// (it is a virtual dispatch site). Holds before and after the fix.
    /// </summary>
    [Fact]
    public void IsSameOrDerivedFrom_metadataBaseReceiver_doesNotMatchDerivedDeclaringType()
    {
        var (sourceDerived, _, metaBase) = BuildSourceAndMetadataTypes();

        Assert.False(
            NavigationTools.IsSameOrDerivedFrom(metaBase, sourceDerived),
            "A metadata receiver of the base type must remain a virtual dispatch site for the derived override.");
    }

    /// <summary>
    /// A metadata receiver of a <em>derived</em> type (one level below the declaring type) must be direct.
    /// Exercises the base-type-chain walk across the source/metadata boundary.
    /// </summary>
    [Fact]
    public void IsSameOrDerivedFrom_metadataDerivedOfDerivedReceiver_matchesSourceDeclaringType()
    {
        var (sourceDerived, _, _) = BuildSourceAndMetadataTypes();

        // GrandChild : Derived (metadata) derives from Derived (source) via the base chain.
        var grandChild = BuildMetadataSubtype("GrandChild : Derived");
        Assert.True(
            NavigationTools.IsSameOrDerivedFrom(grandChild, sourceDerived),
            "A metadata receiver that derives from the declaring type must be direct.");
    }

    /// <summary>
    /// Builds two compilations: A (source, defines Base/Derived) and B (references A's emitted assembly,
    /// so its field types are metadata symbols). Returns the source <c>Derived</c> (declaring type) and the
    /// metadata <c>Derived</c>/<c>Base</c> (receiver types as seen from B).
    /// </summary>
    private static (INamedTypeSymbol SourceDerived, INamedTypeSymbol MetaDerived, INamedTypeSymbol MetaBase)
        BuildSourceAndMetadataTypes()
    {
        var dllPath = EmitADll();
        try
        {
            var client = CreateCompilation("DirectOnlyB", SourceB, dllPath).GetTypeByMetadataName("Client")
                ?? throw new InvalidOperationException("Client not found in B compilation.");
            var metaDerived = (client.GetMembers("_derived").OfType<IFieldSymbol>().Single().Type as INamedTypeSymbol)
                ?? throw new InvalidOperationException("_derived type is not a named type.");
            var metaBase = (client.GetMembers("_base").OfType<IFieldSymbol>().Single().Type as INamedTypeSymbol)
                ?? throw new InvalidOperationException("_base type is not a named type.");

            // The source declaring type comes from a fresh source compilation of A (mirrors the
            // declaring project's compilation, where the override lives).
            var sourceDerived = CreateSourceCompilationA().GetTypeByMetadataName("Derived")
                ?? throw new InvalidOperationException("Derived not found in A compilation.");

            return (sourceDerived, metaDerived, metaBase);
        }
        finally
        {
            DeleteQuietly(dllPath);
        }
    }

    /// <summary>Builds a metadata subtype (in a compilation referencing A's emitted assembly).</summary>
    private static INamedTypeSymbol BuildMetadataSubtype(string classDecl)
    {
        var dllPath = EmitADll();
        try
        {
            var name = classDecl.Split(':')[0].Trim();
            var comp = CreateCompilation("DirectOnlySub", $"public class {classDecl} {{ }}", dllPath);
            return comp.GetTypeByMetadataName(name)
                ?? throw new InvalidOperationException($"{name} not found in subtype compilation.");
        }
        finally
        {
            DeleteQuietly(dllPath);
        }
    }

    private static CSharpCompilation CreateSourceCompilationA() =>
        CSharpCompilation.Create(
            "DirectOnlyA",
            new[] { CSharpSyntaxTree.ParseText(SourceA) },
            DefaultReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static CSharpCompilation CreateCompilation(string name, string source, string? extraDllPath = null)
    {
        var references = DefaultReferences();
        if (extraDllPath is not null)
            references = references.Append(MetadataReference.CreateFromFile(extraDllPath));
        return CSharpCompilation.Create(
            name,
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static IEnumerable<MetadataReference> DefaultReferences() => new[]
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
    };

    private static string EmitADll()
    {
        var dllPath = Path.Combine(Path.GetTempPath(), "DirectOnlyCrossProject_" + Guid.NewGuid().ToString("N") + ".dll");
        EmitAssembly(CreateSourceCompilationA(), dllPath);
        return dllPath;
    }

    private static void EmitAssembly(CSharpCompilation compilation, string dllPath)
    {
        using var stream = File.Create(dllPath);
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                "Failed to emit " + compilation.AssemblyName + ".dll: "
                + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

}

/// <summary>
/// End-to-end guard for <c>find_symbol_references</c> <c>directOnly</c> in the common <em>source-linked</em>
/// case (a project reference resolved to the referenced project's source compilation). Verifies the filter
/// keeps the derived-receiver call and excludes the base-receiver and <c>base.</c> calls.
/// </summary>
public sealed class DirectOnlyFindReferencesTests : IClassFixture<DirectOnlyFindReferencesTests.Fixture>
{
    private const string ProjectACsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>DirectOnlyA</AssemblyName>
          </PropertyGroup>
        </Project>
        """;

    private const string ProjectBCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>DirectOnlyB</AssemblyName>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="..\A\A.csproj" />
          </ItemGroup>
        </Project>
        """;

    // Project A. Line map (1-based):
    //   3  Base.ApplyChanges (virtual)
    //   8  Derived.ApplyChanges (override)
    //  10   base.ApplyChanges()   -> receiver Base (dispatch for Derived)
    private const string SourceA = """
        public class Base
        {
            public virtual void ApplyChanges() { }
        }

        public class Derived : Base
        {
            public override void ApplyChanges()
            {
                base.ApplyChanges();
            }
        }
        """;

    // Project B (references A). Line map (1-based):
    //   7   _derived.ApplyChanges() -> receiver Derived (direct for Derived)
    //  17   _base.ApplyChanges()    -> receiver Base    (dispatch for Derived)
    private const string SourceB = """
        public class Client
        {
            private Derived _derived;

            public void Test()
            {
                _derived.ApplyChanges();
            }
        }

        public class OtherClient
        {
            private Base _base;

            public void Test()
            {
                _base.ApplyChanges();
            }
        }
        """;

    private readonly Fixture _fixture;

    public DirectOnlyFindReferencesTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DerivedOverride_directOnly_keeps_cross_project_derived_receiver_call_only()
    {
        var declaration = PositionOf(SourceA, "public override void ApplyChanges()");

        var result = await _fixture.Navigation.FindSymbolReferences(
            _fixture.SourcePathA, symbolName: "ApplyChanges", line: declaration.Line, directOnly: true,
            cancellationToken: TestContext.Current.CancellationToken);

        // Only the cross-project call whose receiver type is Derived (B.cs:7) is direct;
        // `base.ApplyChanges()` inside the override (A.cs:10) and the Base-receiver call (B.cs:17)
        // are virtual dispatch sites.
        Assert.Contains("Found **1** direct reference location(s) (2 virtual dispatch site(s) excluded).", result);
        Assert.Contains("- 7:18", result, StringComparison.Ordinal);
        Assert.DoesNotContain("- 10:14", result, StringComparison.Ordinal);
        Assert.DoesNotContain("- 17:15", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DerivedOverride_default_notes_cross_project_dispatch_sites()
    {
        var declaration = PositionOf(SourceA, "public override void ApplyChanges()");

        var result = await _fixture.Navigation.FindSymbolReferences(
            _fixture.SourcePathA, symbolName: "ApplyChanges", line: declaration.Line,
            cancellationToken: TestContext.Current.CancellationToken);

        // All 3 family call sites are reported; exactly 2 are dispatch sites (Base receivers).
        Assert.Contains("Found **3** reference location(s).", result);
        Assert.Contains(
            "> Note: symbol is virtual/override; 2 of the 3 reference(s) are virtual dispatch sites",
            result,
            StringComparison.Ordinal);
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

    /// <summary>Two minimal real projects (A referenced by B) loaded into a shared <see cref="SolutionManager"/>.</summary>
    public sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly SolutionManager _manager;

        public string SourcePathA { get; }

        public NavigationTools Navigation { get; }

        public Fixture()
        {
            MsBuildTestRegistration.EnsureRegistered();

            _root = Path.Combine(Path.GetTempPath(), "RoslynMcpDirectOnlyFindRef_" + Guid.NewGuid().ToString("N"));
            var dirA = Path.Combine(_root, "A");
            var dirB = Path.Combine(_root, "B");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            File.WriteAllText(Path.Combine(dirA, "A.csproj"), ProjectACsproj);
            SourcePathA = Path.Combine(dirA, "A.cs");
            File.WriteAllText(SourcePathA, SourceA);
            File.WriteAllText(Path.Combine(dirB, "B.csproj"), ProjectBCsproj);
            File.WriteAllText(Path.Combine(dirB, "B.cs"), SourceB);

            var config = new WorkspaceConfig(new ConfigurationBuilder().Build());
            _manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
            try
            {
                _manager.LoadAsync(Path.Combine(dirB, "B.csproj")).GetAwaiter().GetResult();
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
