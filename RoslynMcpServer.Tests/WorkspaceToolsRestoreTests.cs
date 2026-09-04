using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// v1.3.0 restore of <c>load_workspace</c> / <c>reset_workspace</c> (fix-plan F1.4.2): explicit load by path
/// (no config), cache for the same path + properties, replacement on a different path, and reset + reload
/// from the config. Slow (real MSBuild): one project tree per class (<c>IClassFixture</c>), the same pattern
/// as <see cref="WorkspaceConfigLazyLoadTests"/>.
/// </summary>
public sealed class WorkspaceToolsRestoreTests : IClassFixture<WorkspaceToolsRestoreTests.RestoreFixture>
{
    private readonly RestoreFixture _fixture;

    public WorkspaceToolsRestoreTests(RestoreFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LoadWorkspace_explicit_path_without_config_loads_workspace()
    {
        var config = _fixture.CreateConfig(workspacePath: null);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);

            var result = await tools.LoadWorkspace(_fixture.ConfigProjectPath);

            Assert.Contains("Successfully loaded workspace", result, StringComparison.Ordinal);
            Assert.Contains("Workspace health", result, StringComparison.Ordinal);
            Assert.Contains("- Config [Library]", result, StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(_fixture.ConfigProjectPath), manager.GetLoadedWorkspacePath(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    [Fact]
    public async Task LoadWorkspace_same_path_and_properties_uses_cache()
    {
        var config = _fixture.CreateConfig(workspacePath: null);
        var logger = new CapturingLogger();
        var manager = new SolutionManager(logger, config);
        try
        {
            var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);

            var first = await tools.LoadWorkspace(_fixture.ConfigProjectPath);
            var second = await tools.LoadWorkspace(_fixture.ConfigProjectPath);

            Assert.Contains("Successfully loaded workspace", first, StringComparison.Ordinal);
            Assert.Contains("Successfully loaded workspace", second, StringComparison.Ordinal);
            // The second load with the same path + properties hits the load cache (no re-open from disk).
            Assert.Equal(
                1,
                logger.Entries.Count(e => e.Contains("Loaded Roslyn workspace from", StringComparison.Ordinal)));
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    [Fact]
    public async Task LoadWorkspace_different_path_replaces_workspace()
    {
        var config = _fixture.CreateConfig(workspacePath: null);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);

            var first = await tools.LoadWorkspace(_fixture.ConfigProjectPath);
            var second = await tools.LoadWorkspace(_fixture.DeepProjectPath);

            Assert.Contains("- Config [Library]", first, StringComparison.Ordinal);
            Assert.Contains("- Deep [Library]", second, StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(_fixture.DeepProjectPath), manager.GetLoadedWorkspacePath(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    [Fact]
    public async Task ResetWorkspace_clears_and_reload_uses_config_again()
    {
        var config = _fixture.CreateConfig(_fixture.ConfigProjectPath);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);

            var loaded = await tools.LoadWorkspace(_fixture.DeepProjectPath);
            Assert.Contains("Successfully loaded workspace", loaded, StringComparison.Ordinal);

            var reset = await tools.ResetWorkspace();
            Assert.Contains("Workspace cleared", reset, StringComparison.Ordinal);
            Assert.Null(manager.GetCurrentSolution());
            Assert.Null(manager.GetLoadedWorkspacePath());

            // A second reset on an already-empty workspace is a no-op, not an error.
            var resetAgain = await tools.ResetWorkspace();
            Assert.Contains("Workspace cleared", resetAgain, StringComparison.Ordinal);

            // After the reset, reload loads the configured workspace from the config again.
            var reloaded = await tools.Reload();
            Assert.Contains("Successfully loaded workspace", reloaded, StringComparison.Ordinal);
            Assert.Contains("- Config [Library]", reloaded, StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(_fixture.ConfigProjectPath), manager.GetLoadedWorkspacePath(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    [Fact]
    public async Task LoadWorkspace_missing_path_reports_not_found()
    {
        var config = _fixture.CreateConfig(workspacePath: null);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);

            var result = await tools.LoadWorkspace(Path.Combine(_fixture.Root, "DoesNotExist.sln"));

            Assert.Contains("Solution or project file not found", result, StringComparison.Ordinal);
            Assert.Null(manager.GetCurrentSolution());
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    /// <summary>One temporary project tree: <c>Config/Config.csproj</c> with <c>Config/Deep/Deep.csproj</c> nested inside.</summary>
    public sealed class RestoreFixture : IDisposable
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

        public RestoreFixture()
        {
            MsBuildTestRegistration.EnsureRegistered();

            Root = Path.Combine(Path.GetTempPath(), "RoslynMcpRestoreTests_" + Guid.NewGuid().ToString("N"));
            var configDir = Path.Combine(Root, "Config");
            var deepDir = Path.Combine(configDir, "Deep");
            Directory.CreateDirectory(deepDir);

            ConfigProjectPath = Path.Combine(configDir, "Config.csproj");
            DeepProjectPath = Path.Combine(deepDir, "Deep.csproj");

            File.WriteAllText(ConfigProjectPath, CsprojTemplate);
            File.WriteAllText(DeepProjectPath, CsprojTemplate);
            File.WriteAllText(Path.Combine(configDir, "A.cs"), """
                namespace ConfigNs
                {
                    public class ConfigThing
                    {
                    }
                }
                """);
            File.WriteAllText(Path.Combine(deepDir, "B.cs"), """
                namespace DeepNs
                {
                    public class DeepThing
                    {
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

    /// <summary>Captures formatted <see cref="ILogger{SolutionManager}"/> entries to assert load-cache hits.</summary>
    private sealed class CapturingLogger : ILogger<SolutionManager>
    {
        private readonly List<string> _entries = new();

        public IReadOnlyList<string> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(formatter(state, exception));
        }
    }
}
