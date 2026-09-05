using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Builds a <see cref="NavigationTools"/> over an in-memory <see cref="AdhocWorkspace"/> (no MSBuild load),
/// reusing the pattern from <see cref="FqnResolutionTests"/>: the solution is injected into
/// <see cref="SolutionManager"/> via reflection so the solution-wide search tools
/// (find_symbol_references / find_symbol_definition / find_implementations without filePath) resolve against it.
/// The source is also written to a unique temp file so documents carry a real file path
/// (the tools group positions by <c>Document.FilePath</c>).
/// </summary>
internal sealed class AdhocSearchTool : IDisposable
{
    private readonly AdhocWorkspace _workspace;
    private readonly bool _ownsRoot;
    private bool _disposed;

    public NavigationTools Tool { get; }

    public string Root { get; }

    public string SourcePath { get; }

    private AdhocSearchTool(NavigationTools tool, AdhocWorkspace workspace, string root, string sourcePath, bool ownsRoot)
    {
        Tool = tool;
        _workspace = workspace;
        Root = root;
        SourcePath = sourcePath;
        _ownsRoot = ownsRoot;
    }

    /// <summary>Creates a tool for <paramref name="source"/>. <paramref name="config"/> overrides the (default) WorkspaceConfig.</summary>
    public static AdhocSearchTool Create(string source, string fileName = "A.cs", WorkspaceConfig? config = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, fileName);
        File.WriteAllText(sourcePath, source);

        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "P", "P", LanguageNames.CSharp);
        var project = workspace.AddProject(projectInfo);
        project = project.WithMetadataReferences(new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        });
        var document = project.AddDocument(fileName, SourceText.From(source), filePath: sourcePath);
        if (!workspace.TryApplyChanges(document.Project.Solution))
        {
            throw new InvalidOperationException("TryApplyChanges failed for test workspace.");
        }

        return CreateFromWorkspace(workspace, config, root, sourcePath, ownsRoot: true);
    }

    private static AdhocSearchTool CreateFromWorkspace(
        AdhocWorkspace workspace,
        WorkspaceConfig? config,
        string root,
        string sourcePath,
        bool ownsRoot)
    {
        config ??= new WorkspaceConfig(new ConfigurationBuilder().Build());
        var manager = new SolutionManager(
            NullLogger<SolutionManager>.Instance,
            config);
        typeof(SolutionManager)
            .GetField("_solution", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, workspace.CurrentSolution);
        var tool = new NavigationTools(
            manager,
            config,
            NullLogger<NavigationTools>.Instance);
        return new AdhocSearchTool(tool, workspace, root, sourcePath, ownsRoot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workspace.Dispose();
        if (_ownsRoot)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
