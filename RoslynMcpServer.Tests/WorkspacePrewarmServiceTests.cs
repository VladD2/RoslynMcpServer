using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// v1.4.0: when the config sets <c>workspace-path</c>, <see cref="WorkspacePrewarmService"/> starts the
/// workspace load in the background right after the server starts — no explicit load call is made, and a
/// subsequent explicit <c>load_workspace</c> with the same path + properties hits the load cache (no second
/// real load). Slow (real MSBuild): one project tree per class (<c>IClassFixture</c>), the same pattern as
/// <see cref="WorkspaceToolsRestoreTests"/>.
/// </summary>
public sealed class WorkspacePrewarmServiceTests : IClassFixture<WorkspacePrewarmServiceTests.PrewarmFixture>
{
    private readonly PrewarmFixture _fixture;

    public WorkspacePrewarmServiceTests(PrewarmFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Prewarm_starts_configured_load_at_service_start_and_explicit_load_hits_cache()
    {
        var config = _fixture.CreateConfig(_fixture.ProjectPath);
        var managerLogger = new CapturingLogger<SolutionManager>();
        var serviceLogger = new CapturingLogger<WorkspacePrewarmService>();
        var manager = new SolutionManager(managerLogger, config);
        var service = new WorkspacePrewarmService(manager, config, serviceLogger);
        try
        {
            await service.StartAsync(CancellationToken.None);

            // Nothing was loaded explicitly: the prewarm task loads the configured workspace in the background.
            var deadline = DateTime.UtcNow.AddSeconds(180);
            while (manager.GetCurrentSolution() is null)
            {
                Assert.True(DateTime.UtcNow < deadline, "Workspace prewarm did not finish within 180s.");
                await Task.Delay(250);
            }

            var solution = manager.GetCurrentSolution();
            Assert.NotNull(solution);
            Assert.Contains(solution!.Projects, p => p.Name == "Config");
            Assert.False(manager.IsLoadInProgress);
            Assert.Contains(serviceLogger.Entries, e => e.Contains("Prewarming the configured workspace", StringComparison.Ordinal));
            Assert.Contains(serviceLogger.Entries, e => e.Contains("Workspace prewarm finished", StringComparison.Ordinal));

            // An explicit load with the same path + properties hits the load cache (no second real load).
            var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);
            var result = await tools.LoadWorkspace(_fixture.ProjectPath);
            Assert.Contains("Successfully loaded workspace", result, StringComparison.Ordinal);
            Assert.Contains("- Config [Library]", result, StringComparison.Ordinal);
            Assert.Equal(
                1,
                managerLogger.Entries.Count(e => e.Contains("Loaded Roslyn workspace from", StringComparison.Ordinal)));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await manager.ClearWorkspaceAsync();
        }
    }

    [Fact]
    public async Task Prewarm_without_configured_workspace_path_is_noop()
    {
        var config = _fixture.CreateConfig(workspacePath: null);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        var service = new WorkspacePrewarmService(manager, config, NullLogger<WorkspacePrewarmService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(1000);

        Assert.Null(manager.GetCurrentSolution());
        Assert.False(manager.IsLoadInProgress);

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>One temporary project: <c>Config/Config.csproj</c> (net10.0 SDK) with a single source file.</summary>
    public sealed class PrewarmFixture : IDisposable
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

        public string ProjectPath { get; }

        public PrewarmFixture()
        {
            MsBuildTestRegistration.EnsureRegistered();

            Root = Path.Combine(Path.GetTempPath(), "RoslynMcpPrewarmTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ProjectPath = Path.Combine(Root, "Config.csproj");

            File.WriteAllText(ProjectPath, CsprojTemplate);
            File.WriteAllText(Path.Combine(Root, "A.cs"), """
                namespace ConfigNs
                {
                    public class ConfigThing
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

    /// <summary>Captures formatted <see cref="ILogger{T}"/> entries to assert log lines.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
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
