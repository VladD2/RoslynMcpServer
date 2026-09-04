using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Lazy workspace load by config (plan §2.2 / §2.3 / §7 «WorkspaceConfigTests»): <see cref="SolutionManager"/>
/// + <see cref="WorkspaceConfig"/> with <c>workspace-path</c> pointing at a minimal temporary project.
/// Slow (real MSBuild): one project tree per class (<c>IClassFixture</c>), 4 tests — solution-wide lazy load,
/// config &gt; walk-up priority for file-scoped requests, and <c>reload</c> with/without arguments.
/// </summary>
public sealed class WorkspaceConfigLazyLoadTests : IClassFixture<WorkspaceConfigLazyLoadTests.LazyFixture>
{
    private readonly LazyFixture _fixture;

    public WorkspaceConfigLazyLoadTests(LazyFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetCurrentSolutionAfterDiskSync_lazy_loads_configured_workspace()
    {
        var config = _fixture.CreateConfig(_fixture.ConfigProjectPath);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            var solution = await manager.GetCurrentSolutionAfterDiskSyncAsync();

            // Nothing was loaded explicitly: the solution-wide entry point loads the configured workspace itself.
            Assert.NotNull(solution);
            Assert.Contains(solution!.Projects, p => p.Name == "Config");
            Assert.Equal(Path.GetFullPath(_fixture.ConfigProjectPath), manager.GetLoadedWorkspacePath(), StringComparer.OrdinalIgnoreCase);

            // A solution-wide tool works right after the lazy load.
            var tool = new NavigationTools(manager, config, NullLogger<NavigationTools>.Instance);
            var result = await tool.FindSymbolDefinition("ConfigThing");
            Assert.Contains("global::ConfigNs.ConfigThing", result);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    [Fact]
    public async Task FileScoped_request_loads_config_project_instead_of_closest_csproj()
    {
        var config = _fixture.CreateConfig(_fixture.ConfigProjectPath);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            // The closest .csproj to the file is Deep.csproj (walk-up), but the config `workspace-path` wins.
            var document = await manager.FindDocumentAsync(_fixture.DeepSourcePath);

            Assert.Equal(Path.GetFullPath(_fixture.ConfigProjectPath), manager.GetLoadedWorkspacePath(), StringComparer.OrdinalIgnoreCase);
            var solution = manager.GetCurrentSolution();
            Assert.NotNull(solution);
            Assert.DoesNotContain(solution!.Projects, p => p.Name == "Deep");
            // The file is covered by the Config project's default compile glob, so the file-scoped request resolves.
            Assert.NotNull(document);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    [Fact]
    public async Task Reload_without_args_loads_workspace_from_config()
    {
        var config = _fixture.CreateConfig(_fixture.ConfigProjectPath);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);

            var result = await tools.Reload();

            Assert.Contains("Successfully loaded workspace", result);
            Assert.Contains("- Config [Library]", result);
            Assert.Equal(Path.GetFullPath(_fixture.ConfigProjectPath), manager.GetLoadedWorkspacePath(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    [Fact]
    public async Task Reload_with_workspacePath_overrides_config()
    {
        var config = _fixture.CreateConfig(_fixture.ConfigProjectPath);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);

            var result = await tools.Reload(workspacePath: _fixture.DeepProjectPath);

            Assert.Contains("Successfully loaded workspace", result);
            Assert.Contains("- Deep [Library]", result);
            Assert.Equal(Path.GetFullPath(_fixture.DeepProjectPath), manager.GetLoadedWorkspacePath(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    /// <summary>
    /// F2: a host timeout/abort mid lazy load must surface the preformatted client-abort report
    /// (<see cref="WorkspaceLoadCancelledException"/>) instead of being swallowed and reported as
    /// "no active workspace" (which sends the agent into a retry loop on the same host timeout).
    /// </summary>
    [Fact]
    public async Task GetCurrentSolutionAfterDiskSync_host_cancel_during_lazy_load_throws_cancelled_exception()
    {
        var config = _fixture.CreateConfig(_fixture.ConfigProjectPath);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            using var cts = new CancellationTokenSource();
            var loadTask = manager.GetCurrentSolutionAfterDiskSyncAsync(cts.Token);

            // A real MSBuild load takes seconds; wait until it is in flight (not yet complete).
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!loadTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            if (loadTask.IsCompleted)
            {
                // The load finished (or failed) before we could cancel — nothing to assert here.
                try
                {
                    await loadTask;
                }
                catch
                {
                    // A real load failure is not what this test asserts.
                }

                return;
            }

            cts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<WorkspaceLoadCancelledException>(() => loadTask);
            Assert.Contains("Workspace Load Cancelled (client abort)", ex.Message, StringComparison.Ordinal);
            Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    /// <summary>F3: a configured <c>workspace-path</c> that does not exist on disk falls back to walk-up for file-scoped requests.</summary>
    [Fact]
    public async Task FileScoped_request_with_broken_config_path_falls_back_to_walk_up()
    {
        // The config points at a path that does not exist; the closest .csproj to the file is Deep.csproj (walk-up).
        var config = _fixture.CreateConfig(Path.Combine(_fixture.Root, "DoesNotExist.sln"));
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            var document = await manager.FindDocumentAsync(_fixture.DeepSourcePath);

            Assert.NotNull(document);
            // The walk-up candidate (Deep.csproj) is loaded, not the broken config path.
            Assert.Equal(Path.GetFullPath(_fixture.DeepProjectPath), manager.GetLoadedWorkspacePath(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    /// <summary>
    /// F9.2: a relative config <c>workspace-path</c> resolves via <c>Path.GetFullPath</c> against the MCP
    /// process current directory (not the exe directory) — with cwd = fixture root, the lazy load opens
    /// the fixture's <c>Config/Config.csproj</c>.
    /// </summary>
    [Fact]
    public async Task Relative_workspace_path_resolves_against_process_cwd()
    {
        var config = _fixture.CreateConfig("Config/Config.csproj");
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            // Path.GetFullPath on the relative config path runs synchronously inside the first
            // GetCurrentSolutionAfterDiskSyncAsync call (before the first real await); start the task
            // inside the lock. The original cwd is captured AND restored inside the lock so the dirty
            // window (cwd = fixture root) never leaks outside it for parallel tests to observe.
            Task<Solution?> loadTask;
            lock (TestEnvironmentLocks.Cwd)
            {
                var originalCwd = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = _fixture.Root;
                    loadTask = manager.GetCurrentSolutionAfterDiskSyncAsync();
                }
                finally
                {
                    Environment.CurrentDirectory = originalCwd;
                }
            }

            var solution = await loadTask;

            Assert.NotNull(solution);
            Assert.Contains(solution!.Projects, p => p.Name == "Config");
            Assert.Equal(Path.GetFullPath(_fixture.ConfigProjectPath), manager.GetLoadedWorkspacePath(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    /// <summary>F3: a broken config path makes the solution-wide lazy load a no-op (null solution).</summary>
    [Fact]
    public async Task GetCurrentSolutionAfterDiskSync_with_broken_config_path_returns_null()
    {
        var config = _fixture.CreateConfig(Path.Combine(_fixture.Root, "DoesNotExist.sln"));
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            var solution = await manager.GetCurrentSolutionAfterDiskSyncAsync();

            Assert.Null(solution);
            Assert.Null(manager.GetLoadedWorkspacePath());
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    /// <summary>
    /// One temporary project tree: <c>Config/Config.csproj</c> (the config project) with
    /// <c>Config/Deep/Deep.csproj</c> nested inside (its own .csproj — the walk-up candidate for files in
    /// <c>Deep/</c>).
    /// </summary>
    public sealed class LazyFixture : IDisposable
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

        public string Root { get; }

        public string ConfigProjectPath { get; }

        public string DeepProjectPath { get; }

        public string ConfigSourcePath { get; }

        public string DeepSourcePath { get; }

        public LazyFixture()
        {
            MsBuildTestRegistration.EnsureRegistered();

            Root = Path.Combine(Path.GetTempPath(), "RoslynMcpLazyWorkspaceTests_" + Guid.NewGuid().ToString("N"));
            var configDir = Path.Combine(Root, "Config");
            var deepDir = Path.Combine(configDir, "Deep");
            Directory.CreateDirectory(deepDir);

            ConfigProjectPath = Path.Combine(configDir, "Config.csproj");
            DeepProjectPath = Path.Combine(deepDir, "Deep.csproj");
            ConfigSourcePath = Path.Combine(configDir, "A.cs");
            DeepSourcePath = Path.Combine(deepDir, "B.cs");

            File.WriteAllText(ConfigProjectPath, CsprojTemplate);
            File.WriteAllText(DeepProjectPath, CsprojTemplate);
            File.WriteAllText(ConfigSourcePath, """
                namespace ConfigNs
                {
                    public class ConfigThing
                    {
                        public void Run()
                        {
                        }
                    }
                }
                """);
            File.WriteAllText(DeepSourcePath, """
                namespace DeepNs
                {
                    public class DeepThing
                    {
                        public void Go()
                        {
                        }
                    }
                }
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
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
