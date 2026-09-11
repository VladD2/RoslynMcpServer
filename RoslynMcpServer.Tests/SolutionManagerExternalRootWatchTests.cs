using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Integration tests for multi-root disk watching: a solution whose project lives OUTSIDE the
/// solution folder (<c>..\Outside\Lib\Lib.csproj</c>) must still get its on-disk <c>.cs</c> edits,
/// additions, and deletions synced into the snapshot without <c>reset_workspace</c>. Slow (real
/// MSBuild): one solution tree per class (<c>IClassFixture</c>), the same pattern as
/// <see cref="WorkspaceMixedProjectLoadTests"/>.
/// </summary>
public sealed class SolutionManagerExternalRootWatchTests : IClassFixture<SolutionManagerExternalRootWatchTests.ExternalRootFixture>
{
    private readonly ExternalRootFixture _fixture;

    public SolutionManagerExternalRootWatchTests(ExternalRootFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LoadAsync_starts_watchers_for_external_project_roots()
    {
        var manager = CreateManager();
        try
        {
            await manager.LoadAsync(_fixture.SolutionPath);

            var roots = manager.WatchRoots;
            Assert.Equal(2, roots.Count);
            Assert.Contains(roots, r => IsSameDirectory(r, _fixture.Root));
            Assert.Contains(roots, r => IsSameDirectory(r, _fixture.ExternalLibDirectory));
        }
        finally
        {
            await manager.ClearWorkspaceAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task External_disk_changes_sync_without_reset_and_clear_stops_all_watchers()
    {
        var manager = CreateManager();
        try
        {
            await manager.LoadAsync(_fixture.SolutionPath);

            // (a) external edit of an existing .cs outside the solution folder
            File.WriteAllText(
                _fixture.LibSourcePath,
                "namespace LibNs { public class LibThing { public const int ExternalChange = 42; } }");
            var edited = await PollUntilAsync(async () =>
            {
                var solution = await manager.GetCurrentSolutionAfterDiskSyncAsync();
                var text = await GetDocumentTextAsync(solution, _fixture.LibSourcePath);
                return text is not null && text.Contains("ExternalChange", StringComparison.Ordinal) ? text : null;
            });
            Assert.NotNull(edited);

            // (b) new .cs in the external directory appears in the snapshot
            var newFile = Path.Combine(_fixture.ExternalLibDirectory, "NewExternal.cs");
            File.WriteAllText(newFile, "namespace LibNs { public class NewExternalThing { } }");
            var added = await PollUntilAsync(async () =>
            {
                var solution = await manager.GetCurrentSolutionAfterDiskSyncAsync();
                var text = await GetDocumentTextAsync(solution, newFile);
                return text is not null && text.Contains("NewExternalThing", StringComparison.Ordinal) ? text : null;
            });
            Assert.NotNull(added);

            // (c) clear stops all watchers and drops the cached roots
            await manager.ClearWorkspaceAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(manager.WatchRoots);
        }
        finally
        {
            await manager.ClearWorkspaceAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private SolutionManager CreateManager()
    {
        var config = _fixture.CreateConfig(workspacePath: null);
        return new SolutionManager(NullLogger<SolutionManager>.Instance, config);
    }

    private static bool IsSameDirectory(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<string?> GetDocumentTextAsync(Solution? solution, string filePath)
    {
        if (solution is null)
        {
            return null;
        }

        var full = Path.GetFullPath(filePath);
        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(
                Path.GetFullPath(d.FilePath ?? string.Empty),
                full,
                StringComparison.OrdinalIgnoreCase));
        if (document is null)
        {
            return null;
        }

        return (await document.GetTextAsync()).ToString();
    }

    private static async Task<string?> PollUntilAsync(Func<Task<string?>> probe, int timeoutMs = 30000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await Task.Delay(250);
        }
    }

    /// <summary>
    /// One temporary tree: <c>Root\Root.sln</c> with <c>Root\App\App.csproj</c> (inside) and
    /// <c>Outside\Lib\Lib.csproj</c> (OUTSIDE the solution folder, referenced as
    /// <c>..\Outside\Lib\Lib.csproj</c>).
    /// </summary>
    public sealed class ExternalRootFixture : IDisposable
    {
        private const string CsprojTemplate = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """;

        private const string CSharpProjectTypeGuid = "9A19103F-16F7-4668-BE54-9A1E7A4F7556";

        private const string AppProjectGuid = "11111111-2222-3333-4444-555555555555";

        private const string LibProjectGuid = "22222222-3333-4444-5555-666666666666";

        public string Root { get; }

        public string SolutionPath { get; }

        public string ExternalLibDirectory { get; }

        public string LibSourcePath { get; }

        public ExternalRootFixture()
        {
            MsBuildTestRegistration.EnsureRegistered();

            var baseDirectory = Path.Combine(Path.GetTempPath(), "RoslynMcpExternalRoots_" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(baseDirectory, "Root");
            ExternalLibDirectory = Path.Combine(baseDirectory, "Outside", "Lib");
            Directory.CreateDirectory(Path.Combine(Root, "App"));
            Directory.CreateDirectory(ExternalLibDirectory);
            SolutionPath = Path.Combine(Root, "Root.sln");
            LibSourcePath = Path.Combine(ExternalLibDirectory, "Lib.cs");

            File.WriteAllText(Path.Combine(Root, "App", "App.csproj"), CsprojTemplate);
            File.WriteAllText(Path.Combine(Root, "App", "A.cs"), """
                namespace AppNs
                {
                    public class AppThing
                    {
                    }
                }
                """);

            File.WriteAllText(Path.Combine(ExternalLibDirectory, "Lib.csproj"), CsprojTemplate);
            File.WriteAllText(LibSourcePath, """
                namespace LibNs
                {
                    public class LibThing
                    {
                        public const int Original = 1;
                    }
                }
                """);

            var libProjectEntry = Path.Combine("..", "Outside", "Lib", "Lib.csproj");
            File.WriteAllText(SolutionPath, $"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                VisualStudioVersion = 17.0.31903.59
                MinimumVisualStudioVersion = 10.0.40219.1
                Project("{CSharpProjectTypeGuid}") = "App", "App\App.csproj", "{AppProjectGuid}"
                EndProject
                Project("{CSharpProjectTypeGuid}") = "Lib", "{libProjectEntry}", "{LibProjectGuid}"
                EndProject
                Global
                    GlobalSection(SolutionConfigurationPlatforms) = preSolution
                        Debug|Any CPU = Debug|Any CPU
                        Release|Any CPU = Release|Any CPU
                    EndGlobalSection
                    GlobalSection(ProjectConfigurationPlatforms) = postSolution
                        {AppProjectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                        {AppProjectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU
                        {AppProjectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU
                        {AppProjectGuid}.Release|Any CPU.Build.0 = Release|Any CPU
                        {LibProjectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                        {LibProjectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU
                        {LibProjectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU
                        {LibProjectGuid}.Release|Any CPU.Build.0 = Release|Any CPU
                    EndGlobalSection
                    GlobalSection(SolutionProperties) = preSolution
                        HideSolutionNode = FALSE
                    EndGlobalSection
                EndGlobal
                """);
        }

        public WorkspaceConfig CreateConfig(string? workspacePath)
        {
            var values = new Dictionary<string, string?>();
            if (workspacePath is not null)
            {
                values["workspace-path"] = workspacePath;
            }

            return new WorkspaceConfig(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        }

        public void Dispose()
        {
            try
            {
                var baseDirectory = Path.GetFullPath(Path.Combine(Root, ".."));
                if (Directory.Exists(baseDirectory))
                {
                    Directory.Delete(baseDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
