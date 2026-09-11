using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

public sealed class UtilityToolsSearchCodeTests
{
    [Fact]
    public async Task SearchCode_without_directory_uses_loaded_workspace_directory()
    {
        var workspaceRoot = CreateTempRoot();
        var externalRoot = CreateTempRoot();

        try
        {
            File.WriteAllText(Path.Combine(workspaceRoot, "App.sln"), string.Empty);
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
            File.WriteAllText(Path.Combine(workspaceRoot, "src", "workspace.cs"), "// next version in workspace");

            Directory.CreateDirectory(Path.Combine(externalRoot, "Downloads"));
            File.WriteAllText(Path.Combine(externalRoot, "Downloads", "external.csv"), "next version outside workspace");

            // Start the task inside the lock: SearchCode is fully synchronous (Task.FromResult), so its
            // whole body — including the cwd fallback read in ResolveSearchRootDirectory — runs here.
            // The original cwd is captured AND restored inside the lock: a dirty (cwd = externalRoot)
            // window outside the lock lets a parallel test capture externalRoot as its "original" cwd,
            // pin the process cwd on it, and block the Directory.Delete below.
            Task<string> searchTask;
            lock (TestEnvironmentLocks.Cwd)
            {
                var originalCwd = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = externalRoot;

                    var manager = CreateManagerWithLoadedPath(Path.Combine(workspaceRoot, "App.sln"));
                    var tool = new UtilityTools(NullLogger<UtilityTools>.Instance, manager, new WorkspaceConfig(new ConfigurationBuilder().Build()));
                    searchTask = tool.SearchCode("next version", cancellationToken: TestContext.Current.CancellationToken);
                }
                finally
                {
                    Environment.CurrentDirectory = originalCwd;
                }
            }

            var result = await searchTask;

            Assert.Contains($"in `{workspaceRoot}`", result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"in `{externalRoot}`", result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("workspace.cs", result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("external.csv", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }

            if (Directory.Exists(externalRoot))
            {
                Directory.Delete(externalRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SearchCode_timeout_reports_partial_results()
    {
        var workspaceRoot = CreateTempRoot();
        var manager = CreateManagerWithLoadedPath(Path.Combine(workspaceRoot, "App.sln"));
        var tool = new UtilityTools(NullLogger<UtilityTools>.Instance, manager, new WorkspaceConfig(new ConfigurationBuilder().Build()));

        try
        {
            File.WriteAllText(Path.Combine(workspaceRoot, "App.sln"), string.Empty);
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
            File.WriteAllText(Path.Combine(workspaceRoot, "src", "a.cs"), "// next version line");

            var result = await tool.SearchCode("next version", maxResults: 1, maxScanSeconds: 1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("Found 1 match(es)", result, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SearchCode_with_includeExtensions_can_search_non_cs_files()
    {
        var workspaceRoot = CreateTempRoot();
        var manager = CreateManagerWithLoadedPath(Path.Combine(workspaceRoot, "App.sln"));
        var tool = new UtilityTools(NullLogger<UtilityTools>.Instance, manager, new WorkspaceConfig(new ConfigurationBuilder().Build()));

        try
        {
            File.WriteAllText(Path.Combine(workspaceRoot, "App.sln"), string.Empty);
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
            File.WriteAllText(Path.Combine(workspaceRoot, "src", "a.csv"), "next version line");

            var defaultResult = await tool.SearchCode("next version", cancellationToken: TestContext.Current.CancellationToken);
            Assert.DoesNotContain("a.csv", defaultResult, StringComparison.OrdinalIgnoreCase);

            var csvResult = await tool.SearchCode("next version", includeExtensions: ".csv", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("a.csv", csvResult, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SearchCode_caseSensitive_true_does_not_match_different_casing()
    {
        var workspaceRoot = CreateTempRoot();
        var manager = CreateManagerWithLoadedPath(Path.Combine(workspaceRoot, "App.sln"));
        var tool = new UtilityTools(NullLogger<UtilityTools>.Instance, manager, new WorkspaceConfig(new ConfigurationBuilder().Build()));

        try
        {
            File.WriteAllText(Path.Combine(workspaceRoot, "App.sln"), string.Empty);
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
            File.WriteAllText(Path.Combine(workspaceRoot, "src", "a.cs"), "// DupFinder leftover check");

            var insensitive = await tool.SearchCode("dupFinder", caseSensitive: false, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("Found 1 match(es)", insensitive, StringComparison.Ordinal);

            var sensitive = await tool.SearchCode("dupFinder", caseSensitive: true, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("No matches found", sensitive, StringComparison.Ordinal);

            var exact = await tool.SearchCode("DupFinder", caseSensitive: true, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("Found 1 match(es)", exact, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SearchCode_caseSensitive_regex_respects_flag()
    {
        var workspaceRoot = CreateTempRoot();
        var manager = CreateManagerWithLoadedPath(Path.Combine(workspaceRoot, "App.sln"));
        var tool = new UtilityTools(NullLogger<UtilityTools>.Instance, manager, new WorkspaceConfig(new ConfigurationBuilder().Build()));

        try
        {
            File.WriteAllText(Path.Combine(workspaceRoot, "App.sln"), string.Empty);
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
            File.WriteAllText(Path.Combine(workspaceRoot, "src", "a.cs"), "// DupFinder");

            var insensitive = await tool.SearchCode("dupFinder", useRegex: true, caseSensitive: false, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("Found 1 match(es)", insensitive, StringComparison.Ordinal);

            var sensitive = await tool.SearchCode("dupFinder", useRegex: true, caseSensitive: true, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("No matches found", sensitive, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SearchCode_without_directory_covers_project_directories_outside_sln_folder()
    {
        // Regression: a monorepo solution spans several trees; the search scope must be the
        // solution's project folders, not just the .sln folder.
        var slnRoot = CreateTempRoot();
        var treeA = CreateTempRoot();
        var treeB = CreateTempRoot();

        try
        {
            File.WriteAllText(Path.Combine(slnRoot, "App.sln"), string.Empty);

            Directory.CreateDirectory(Path.Combine(treeA, "CompA"));
            File.WriteAllText(Path.Combine(treeA, "CompA", "A.csproj"), string.Empty);
            File.WriteAllText(Path.Combine(treeA, "CompA", "a.cs"), "// marker only in tree A");

            Directory.CreateDirectory(Path.Combine(treeB, "CompB"));
            File.WriteAllText(Path.Combine(treeB, "CompB", "B.csproj"), string.Empty);
            File.WriteAllText(Path.Combine(treeB, "CompB", "b.cs"), "// marker only in tree B");

            var manager = CreateManagerWithSolution(
                Path.Combine(slnRoot, "App.sln"),
                Path.Combine(treeA, "CompA", "A.csproj"),
                Path.Combine(treeB, "CompB", "B.csproj"));
            var tool = new UtilityTools(NullLogger<UtilityTools>.Instance, manager, new WorkspaceConfig(new ConfigurationBuilder().Build()));

            var resultA = await tool.SearchCode("marker only in tree A", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("a.cs", resultA, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("b.cs", resultA, StringComparison.OrdinalIgnoreCase);

            // tree B lives outside the .sln folder — the original bug missed it.
            var resultB = await tool.SearchCode("marker only in tree B", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("b.cs", resultB, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var root in new[] { slnRoot, treeA, treeB })
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void ComputeSearchRoots_merges_fully_active_component_and_keeps_partial_one()
    {
        var baseDir = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(baseDir, "comp", "proj1"));
            Directory.CreateDirectory(Path.Combine(baseDir, "comp", "proj2"));
            Directory.CreateDirectory(Path.Combine(baseDir, "other", "proj3"));
            Directory.CreateDirectory(Path.Combine(baseDir, "empty")); // inactive sibling blocks the merge at baseDir

            var roots = SearchScopeResolver.ComputeSearchRoots(new[]
            {
                Path.Combine(baseDir, "comp", "proj1"),
                Path.Combine(baseDir, "comp", "proj2"),
                Path.Combine(baseDir, "other", "proj3"),
            });

            Assert.Equal(
                new[]
                {
                    Path.Combine(baseDir, "comp"), // proj1 + proj2 merged: all comp children active
                    Path.Combine(baseDir, "other", "proj3"), // no merge: only child
                },
                roots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
    }

    private static SolutionManager CreateManagerWithLoadedPath(string loadedPath)
    {
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, new WorkspaceConfig(new ConfigurationBuilder().Build()));
        typeof(SolutionManager)
            .GetField("_loadedPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, loadedPath);
        return manager;
    }

    private static SolutionManager CreateManagerWithSolution(string loadedPath, params string[] projectPaths)
    {
        var workspace = new AdhocWorkspace();
        foreach (var projectPath in projectPaths)
        {
            var name = Path.GetFileNameWithoutExtension(projectPath);
            var projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                name,
                name,
                LanguageNames.CSharp,
                filePath: projectPath);
            workspace.AddProject(projectInfo);
        }

        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, new WorkspaceConfig(new ConfigurationBuilder().Build()));
        typeof(SolutionManager)
            .GetField("_loadedPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, loadedPath);
        typeof(SolutionManager)
            .GetField("_solution", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, workspace.CurrentSolution);
        return manager;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
