using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests;

public sealed class WorkspaceHealthReporterTests
{
    [Fact]
    public void BuildHealthSection_missingObj_does_not_throw_and_notes_redirected()
    {
        // Regression (§2.4): a redirected `obj` (monorepo Directory.Build.props → out\) used to make
        // Directory.EnumerateFiles throw DirectoryNotFoundException and kill the whole health block.
        using var ctx = TempProject.Create();

        var text = WorkspaceHealthReporter.BuildHealthSection(ctx.SolutionPath, ctx.Workspace.CurrentSolution);

        Assert.Contains("Restore assets:", text, StringComparison.Ordinal);
        Assert.Contains("obj not found — redirected?", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHealthSection_withAssets_reports_ok()
    {
        using var ctx = TempProject.Create();
        var objDir = Path.Combine(ctx.Root, "obj");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), "{}");

        var text = WorkspaceHealthReporter.BuildHealthSection(ctx.SolutionPath, ctx.Workspace.CurrentSolution);

        Assert.Contains("ok (1/1 projects have obj/project.assets.json)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("obj not found", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHealthSection_workflowNote_uses_reload_and_config_not_load_workspace()
    {
        using var ctx = TempProject.Create();

        var text = WorkspaceHealthReporter.BuildHealthSection(ctx.SolutionPath, ctx.Workspace.CurrentSolution);

        // The workspace is lazy-loaded from config; the find_references alias does not exist.
        Assert.DoesNotContain("find_references", text, StringComparison.Ordinal);
        Assert.DoesNotContain("load_workspace", text, StringComparison.Ordinal);
        Assert.Contains("`reload`", text, StringComparison.Ordinal);
        Assert.Contains("RoslynMcp.jsonc", text, StringComparison.Ordinal);
    }

    private sealed class TempProject : IDisposable
    {
        private TempProject(AdhocWorkspace workspace, string root, string solutionPath)
        {
            Workspace = workspace;
            Root = root;
            SolutionPath = solutionPath;
        }

        public AdhocWorkspace Workspace { get; }
        public string Root { get; }
        public string SolutionPath { get; }

        /// <summary>Creates a project whose <c>.csproj</c> exists but has no <c>obj</c> directory.</summary>
        public static TempProject Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "RoslynMcpHealth_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var csproj = Path.Combine(root, "P.csproj");
            File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

            var workspace = new AdhocWorkspace();
            var projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "P",
                "P",
                LanguageNames.CSharp,
                filePath: csproj);
            var project = workspace.AddProject(projectInfo);
            var document = project.AddDocument("A.cs", SourceText.From("class A {}"), filePath: Path.Combine(root, "A.cs"));
            if (!workspace.TryApplyChanges(document.Project.Solution))
            {
                throw new InvalidOperationException("TryApplyChanges failed for test workspace.");
            }

            return new TempProject(workspace, root, csproj);
        }

        public void Dispose()
        {
            Workspace.Dispose();
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
