using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Part 0 tests (plan §0.3): Roslyn 5.9.0 cannot compute a project checksum that contains an
/// <see cref="UnresolvedAnalyzerReference"/> stub (<c>SerializerService.CreateChecksum</c> throws
/// <c>InvalidOperationException</c>), which breaks every solution-wide SymbolFinder search that builds the
/// dependent-type index (find_usages / find_symbol_references / get_call_graph / find_implementations /
/// rename_symbol) for the whole solution. The sanitizer strips the stub; the retry helper covers a stub that
/// appears after the last sanitization pass.
/// </summary>
public sealed class WorkspaceAnalyzerSanitizerTests
{
    private const string UnresolvedAnalyzerPath = @"C:\missing\Analyzers\SomeCustomAnalyzer.dll";

    [Fact]
    public async Task FindDerivedClasses_throws_on_unresolved_analyzer_and_works_after_sanitization()
    {
        using var ctx = TwoProjectWorkspace.Create();
        var raw = ctx.WithUnresolvedAnalyzer();
        var baseType = ctx.BaseType(raw);

        // Repro of the bug: the project-checksum crash names the unexpected value type.
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => SymbolFinder.FindDerivedClassesAsync(baseType, raw, transitive: true, projects: null, CancellationToken.None));
        Assert.True(WorkspaceAnalyzerSanitizer.IsUnresolvedAnalyzerError(ex), $"unexpected message: {ex.Message}");

        // Sanitization removes the stub...
        var (sanitized, removed) = WorkspaceAnalyzerSanitizer.RemoveUnresolvedAnalyzers(raw);
        Assert.Equal(1, removed);
        Assert.DoesNotContain(sanitized.Projects.SelectMany(p => p.AnalyzerReferences), r => r is UnresolvedAnalyzerReference);

        // ...and the search works again, finding the cross-project derived type.
        var derived = await SymbolFinder.FindDerivedClassesAsync(baseType, sanitized, transitive: true, projects: null, CancellationToken.None);
        Assert.Contains(derived, t => t.Name == "Derived");
    }

    [Fact]
    public void RemoveUnresolvedAnalyzers_clean_solution_returns_same_instance()
    {
        using var ctx = TwoProjectWorkspace.Create();
        var solution = ctx.Solution;

        var (sanitized, removed) = WorkspaceAnalyzerSanitizer.RemoveUnresolvedAnalyzers(solution);

        Assert.Equal(0, removed);
        Assert.Same(solution, sanitized);
    }

    [Fact]
    public void IsUnresolvedAnalyzerError_distinguishes_checksum_failure()
    {
        Assert.True(WorkspaceAnalyzerSanitizer.IsUnresolvedAnalyzerError(
            new InvalidOperationException(
                "Unexpected value 'Microsoft.CodeAnalysis.Diagnostics.UnresolvedAnalyzerReference' "
                + "of type 'Microsoft.CodeAnalysis.Diagnostics.UnresolvedAnalyzerReference'")));
        Assert.False(WorkspaceAnalyzerSanitizer.IsUnresolvedAnalyzerError(new InvalidOperationException("boom")));
        Assert.False(WorkspaceAnalyzerSanitizer.IsUnresolvedAnalyzerError(
            new InvalidOperationException("Unexpected value 'Microsoft.CodeAnalysis.Diagnostics.SomeOtherReference'")));
    }

    [Fact]
    public async Task WithSanitizedRetryAsync_retries_once_on_sanitized_solution()
    {
        using var ctx = TwoProjectWorkspace.Create();
        var raw = ctx.WithUnresolvedAnalyzer();
        var calls = 0;
        Func<Solution, Task<int>> search = sol =>
        {
            calls++;
            if (sol.Projects.Any(p => p.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().Any()))
            {
                throw new InvalidOperationException("Unexpected value 'Microsoft.CodeAnalysis.Diagnostics.UnresolvedAnalyzerReference'");
            }

            return Task.FromResult(calls);
        };

        var (value, retried) = await WorkspaceAnalyzerSanitizer.WithSanitizedRetryAsync(
            search,
            () => WorkspaceAnalyzerSanitizer.RemoveUnresolvedAnalyzers(raw).Solution,
            raw,
            CancellationToken.None);

        Assert.True(retried);
        Assert.Equal(2, value);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WithSanitizedRetryAsync_rethrows_when_resanitize_returns_same_solution()
    {
        using var ctx = TwoProjectWorkspace.Create();
        var solution = ctx.Solution;
        var original = new InvalidOperationException("Unexpected value 'Microsoft.CodeAnalysis.Diagnostics.UnresolvedAnalyzerReference'");
        Func<Solution, Task<int>> search = _ => throw original;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WorkspaceAnalyzerSanitizer.WithSanitizedRetryAsync(search, () => solution, solution, CancellationToken.None));

        Assert.Same(original, ex);
    }

    /// <summary>
    /// Two-project in-memory workspace: <c>Base</c> in P1, <c>Derived : Base</c> in P2 (project reference via
    /// the P1 compilation) — the minimum setup for the cross-project dependent-type search that computes the
    /// project checksums.
    /// </summary>
    private sealed class TwoProjectWorkspace : IDisposable
    {
        private TwoProjectWorkspace(AdhocWorkspace workspace)
        {
            Workspace = workspace;
        }

        public AdhocWorkspace Workspace { get; }

        public Solution Solution => Workspace.CurrentSolution;

        public static TwoProjectWorkspace Create()
        {
            var workspace = new AdhocWorkspace();
            var coreReference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

            var p1 = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "P1", "P1", LanguageNames.CSharp));
            p1 = p1.WithMetadataReferences(new[] { coreReference });
            var baseDocument = p1.AddDocument("Base.cs", SourceText.From("public class Base { }"));

            // AdhocWorkspace only sees changes applied via TryApplyChanges: apply P1 first, then branch P2
            // from the workspace's current (document-bearing) solution state.
            if (!workspace.TryApplyChanges(baseDocument.Project.Solution))
            {
                throw new InvalidOperationException("TryApplyChanges failed for P1.");
            }

            var p2 = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "P2", "P2", LanguageNames.CSharp));
            p2 = p2.WithMetadataReferences(new[] { coreReference });
            p2 = p2.AddProjectReference(new ProjectReference(p1.Id));
            var derivedDocument = p2.AddDocument("Derived.cs", SourceText.From("public class Derived : Base { }"));

            if (!workspace.TryApplyChanges(derivedDocument.Project.Solution))
            {
                throw new InvalidOperationException("TryApplyChanges failed for P2.");
            }

            return new TwoProjectWorkspace(workspace);
        }

        /// <summary>Adds an <see cref="UnresolvedAnalyzerReference"/> stub to the first project and returns the updated solution.</summary>
        public Solution WithUnresolvedAnalyzer()
        {
            var project = Solution.Projects.First();
            var updated = project.AddAnalyzerReference(new UnresolvedAnalyzerReference(UnresolvedAnalyzerPath));
            if (!Workspace.TryApplyChanges(updated.Solution))
            {
                throw new InvalidOperationException("TryApplyChanges failed while adding the analyzer reference.");
            }

            return Workspace.CurrentSolution;
        }

        public INamedTypeSymbol BaseType(Solution solution)
        {
            var document = solution.Projects.First().Documents.First(d => (d.Name ?? string.Empty).EndsWith("Base.cs"));
            var model = document.GetSemanticModelAsync().GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("Could not build the Base.cs semantic model.");
            var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("Could not build the Base.cs syntax root.");
            var classDeclaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
            return model.GetDeclaredSymbol(classDeclaration)
                ?? throw new InvalidOperationException("Base type not found in the test solution.");
        }

        public void Dispose()
        {
            Workspace.Dispose();
        }
    }
}
