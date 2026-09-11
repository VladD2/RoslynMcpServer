using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// FQN input for find_symbol_references (name-based) / find_symbol_definition / find_implementations
/// (plan §3.3): a name containing a dot is an exact FQN match (Ordinal); when it matches nothing the error
/// lists the candidate FQNs and there is no silent fallback to the simple name.
/// Note: <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> includes the <c>global::</c> prefix for top-level
/// types (GlobalNamespaceStyle.Included), so both FQN forms are accepted: with the prefix
/// (<c>global::Ns1.Guard</c> — the same string the tools print in their output) and without it
/// (<c>Ns1.Guard</c>) — the leading <c>global::</c> is stripped from both sides before the Ordinal comparison.
/// </summary>
public sealed class FqnResolutionTests : IDisposable
{
    private const string Source = """
        namespace Ns1
        {
            public class Guard
            {
                public void Run() { }
            }

            public class Client
            {
                public void Use()
                {
                    var g = new Guard();
                    g.Run();
                }
            }

            public interface IGuard
            {
                void Check();
            }

            public class GuardImpl : IGuard
            {
                public void Check() { }
            }
        }

        namespace Ns2
        {
            public class Guard
            {
                public void Run() { }
            }

            public class Client
            {
                public void Use()
                {
                    var g = new Guard();
                    g.Run();
                }
            }

            public interface IGuard
            {
                void Check();
            }

            public class GuardImpl : IGuard
            {
                public void Check() { }
            }
        }
        """;

    private readonly AdhocWorkspace _workspace;
    private readonly string _root;

    public FqnResolutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RoslynMcpFqnTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "A.cs");
        File.WriteAllText(sourcePath, Source);

        _workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "P", "P", LanguageNames.CSharp);
        var project = _workspace.AddProject(projectInfo);
        project = project.WithMetadataReferences(new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        });
        var document = project.AddDocument("A.cs", SourceText.From(Source), filePath: sourcePath);
        if (!_workspace.TryApplyChanges(document.Project.Solution))
        {
            throw new InvalidOperationException("TryApplyChanges failed for test workspace.");
        }
    }

    public void Dispose()
    {
        _workspace.Dispose();
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

    [Fact]
    public async Task FindSymbolReferences_nameOnly_fqn_selects_exact_declaration_only()
    {
        var tool = CreateTool();

        var result = await tool.FindSymbolReferences(symbolName: "global::Ns1.Guard", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("`global::Ns1.Guard`", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Ns2.Guard", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolReferences_nameOnly_fqn_not_found_lists_candidates_without_fallback()
    {
        var tool = CreateTool();

        var result = await tool.FindSymbolReferences(symbolName: "Ns9.Guard", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("FQN `Ns9.Guard` was not found", result, StringComparison.Ordinal);
        Assert.Contains("simple name `Guard`", result, StringComparison.Ordinal);
        Assert.Contains("`global::Ns1.Guard`", result, StringComparison.Ordinal);
        Assert.Contains("`global::Ns2.Guard`", result, StringComparison.Ordinal);
        Assert.DoesNotContain("reference location(s)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolReferences_nameOnly_simple_name_still_reports_all_declarations()
    {
        var tool = CreateTool();

        var result = await tool.FindSymbolReferences(symbolName: "Guard", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Ns1.Guard", result, StringComparison.Ordinal);
        Assert.Contains("Ns2.Guard", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindImplementations_fqn_selects_exact_base_type_only()
    {
        var tool = CreateTool();

        var result = await tool.FindImplementations("global::Ns1.IGuard", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("`global::Ns1.IGuard`", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Ns2.IGuard", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindImplementations_fqn_not_found_lists_candidates()
    {
        var tool = CreateTool();

        var result = await tool.FindImplementations("Ns9.IGuard", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("FQN `Ns9.IGuard` was not found", result, StringComparison.Ordinal);
        Assert.Contains("`global::Ns1.IGuard`", result, StringComparison.Ordinal);
        Assert.Contains("`global::Ns2.IGuard`", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolReferences_nameOnly_member_fqn_selects_exact_method_only()
    {
        var tool = CreateTool();

        var result = await tool.FindSymbolReferences(symbolName: "Ns1.Guard.Run", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("`global::Ns1.Guard.Run`", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Ns2.Guard.Run", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolReferences_nameOnly_member_fqn_not_found_lists_candidates_without_fallback()
    {
        var tool = CreateTool();

        var result = await tool.FindSymbolReferences(symbolName: "Ns9.Guard.Run", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("FQN `Ns9.Guard.Run` was not found", result, StringComparison.Ordinal);
        Assert.Contains("simple name `Run`", result, StringComparison.Ordinal);
        Assert.Contains("`global::Ns1.Guard.Run`", result, StringComparison.Ordinal);
        Assert.Contains("`global::Ns2.Guard.Run`", result, StringComparison.Ordinal);
        Assert.DoesNotContain("reference location(s)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolReferences_nameOnly_fqn_without_global_prefix_resolves()
    {
        var tool = CreateTool();

        var result = await tool.FindSymbolReferences(symbolName: "Ns1.Guard", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("`global::Ns1.Guard`", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Ns2.Guard", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindImplementations_fqn_without_global_prefix_resolves()
    {
        var tool = CreateTool();

        var result = await tool.FindImplementations("Ns1.IGuard", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("`global::Ns1.IGuard`", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Ns2.IGuard", result, StringComparison.Ordinal);
    }

    private NavigationTools CreateTool()
    {
        var manager = new SolutionManager(
            NullLogger<SolutionManager>.Instance,
            new WorkspaceConfig(new ConfigurationBuilder().Build()));
        typeof(SolutionManager)
            .GetField("_solution", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, _workspace.CurrentSolution);
        return new NavigationTools(
            manager,
            new WorkspaceConfig(new ConfigurationBuilder().Build()),
            NullLogger<NavigationTools>.Instance);
    }
}
