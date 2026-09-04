using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

public sealed class WorkspaceLoadGuidanceTests
{
    [Fact]
    public void FormatNoWorkspaceLoadedMessage_no_config_suggests_config_or_load_workspace()
    {
        var root = CreateTempRoot();
        var originalEnv = Environment.GetEnvironmentVariable("ROSLYN_MCP_WORKSPACE");

        try
        {
            File.WriteAllText(Path.Combine(root, "App.sln"), string.Empty);
            File.WriteAllText(Path.Combine(root, "App.slnx"), "<Solution />");

            // Branch (a): no config — recommend the config (lazy load) or an explicit load_workspace.
            // The original cwd is captured AND restored inside the lock so the dirty window
            // (cwd = root) never leaks outside it for parallel tests to observe.
            string message;
            lock (TestEnvironmentLocks.Cwd)
            {
                var originalCwd = Environment.CurrentDirectory;
                try
                {
                    Environment.SetEnvironmentVariable("ROSLYN_MCP_WORKSPACE", null);
                    Environment.CurrentDirectory = root;
                    message = WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(
                        "Error: No active workspace.",
                        configuredPath: null);
                }
                finally
                {
                    Environment.CurrentDirectory = originalCwd;
                }
            }

            Assert.Contains("workspace-path", message, StringComparison.Ordinal);
            Assert.Contains("load_workspace", message, StringComparison.Ordinal);
            Assert.Contains("App.sln", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("App.slnx", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(".slnx", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("not** accepted", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Candidate solution files:", message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSLYN_MCP_WORKSPACE", originalEnv);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void FormatNoWorkspaceLoadedMessage_configured_path_exists_points_to_reload_not_load_workspace_first()
    {
        var root = CreateTempRoot();
        var configuredPath = Path.Combine(root, "App.sln");
        File.WriteAllText(configuredPath, string.Empty);

        try
        {
            // Branch (b): config is set and the file exists — primary recommendation is the config/reload,
            // load_workspace is only an option to open a different solution.
            var message = WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(
                "Error: No active workspace.",
                configuredPath: configuredPath);

            Assert.Contains(configuredPath, message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("reload", message, StringComparison.Ordinal);
            Assert.Contains("lazy load failed", message, StringComparison.Ordinal);
            // No imperative "Call `load_workspace` …" as the primary instruction.
            Assert.DoesNotContain("Call `load_workspace`", message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void FormatNoWorkspaceLoadedMessage_configured_path_missing_reports_not_found()
    {
        var root = CreateTempRoot();
        var configuredPath = Path.Combine(root, "Missing.sln");

        try
        {
            // Branch (c): config is set but the file does not exist (F3: broken workspace-path).
            var message = WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(
                "Error: No active workspace.",
                configuredPath: configuredPath);

            Assert.Contains(configuredPath, message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not found", message, StringComparison.Ordinal);
            Assert.Contains("workspace-path", message, StringComparison.Ordinal);
            Assert.Contains("reload", message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Agent_facing_workspace_strings_do_not_direct_Call_load_workspace()
    {
        // Sweep (regression guard against the guidance cascade): no agent-facing string directs
        // "Call `load_workspace` first" / "Call `load_workspace` with" as the primary action.
        var root = CreateTempRoot();
        var existingConfiguredPath = Path.Combine(root, "App.sln");
        File.WriteAllText(existingConfiguredPath, string.Empty);
        var missingConfiguredPath = Path.Combine(root, "Missing.sln");

        var config = new WorkspaceConfig(new ConfigurationBuilder().Build());
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        typeof(SolutionManager)
            .GetField("_projectGraphStale", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, true);

        var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);
        var resetResponse = await tools.ResetWorkspace();

        var strings = new[]
        {
            WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(null, configuredPath: null),
            WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(null, configuredPath: existingConfiguredPath),
            WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(null, configuredPath: missingConfiguredPath),
            WorkspaceLoadGuidance.FormatClientCancelledWorkspaceLoadMessage(@"C:\repo\Tests.sln"),
            WorkspaceLoadGuidance.FormatMissingTargetFrameworkWorkspaceLoadMessage(
                @"C:\repo\App.sln",
                new[]
                {
                    "Failure: Msbuild failed when processing the file 'C:\\repo\\Foo.csproj' with message: "
                    + "The \"ResolvePackageAssets\" task was not given a value for the required parameter \"TargetFramework\".",
                },
                configuration: null,
                platform: null),
            WorkspaceLoadGuidance.FormatMissingCompileTargetWorkspaceLoadMessage(
                @"C:\repo\App.sln",
                new[]
                {
                    "Failure: Msbuild failed when processing the file 'C:\\repo\\Foo.csproj' with message: "
                    + "Project does not contain 'Compile' target.",
                },
                configuration: null,
                platform: null,
                targetFramework: null),
            WorkspaceLoadGuidance.FormatRoslynMsBuildBuildHostFailureMessage(@"C:\app.sln"),
            WorkspaceLoadGuidance.FormatEmptyTestListMessage(@"C:\repo\Common\Common.csproj", projectCount: 1),
            WorkspaceLoadGuidance.FormatNoMatchingTestsAgentHint(
                loadedRoslynWorkspacePath: null,
                filterDescription: "Name suffix `.FooTests.Bar`",
                testTargetPath: @"C:\repo\Tests.sln"),
            manager.GetProjectGraphStaleHint()!,
            AssemblyReferenceResolver.Resolve(solution: null, assemblyName: "SomeAssembly", assemblyPath: null).ErrorMessage!,
            resetResponse,
        };

        try
        {
            foreach (var text in strings)
            {
                Assert.DoesNotContain("Call `load_workspace` first", text, StringComparison.Ordinal);
                Assert.DoesNotContain("Call `load_workspace` with", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// F9.1: `reload` without `workspacePath` and without a configured `workspace-path` returns the error
    /// immediately (no MSBuild load): the expected config keys (exe dir or cwd, cwd wins) plus the note
    /// that file-scoped semantic tools auto-load (config first, then walk-up) without `workspace-path`.
    /// </summary>
    [Fact]
    public async Task Reload_without_workspace_path_returns_config_keys_and_file_scoped_hint()
    {
        var config = new WorkspaceConfig(new ConfigurationBuilder().Build());
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);

        var result = await tools.Reload();

        Assert.StartsWith("Error: no workspace path", result, StringComparison.Ordinal);
        Assert.Contains("workspace-path", result, StringComparison.Ordinal);
        Assert.Contains("`configuration`/`platform`/`target-framework`", result, StringComparison.Ordinal);
        Assert.Contains("exe dir or cwd", result, StringComparison.Ordinal);
        Assert.Contains("File-scoped semantic tools", result, StringComparison.Ordinal);
        Assert.Contains("walk-up", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatClientCancelledWorkspaceLoadMessage_is_explicit_abort_not_msbuild()
    {
        var message = WorkspaceLoadGuidance.FormatClientCancelledWorkspaceLoadMessage(
            @"C:\repo\Tests.sln");

        Assert.Contains("Workspace Load Cancelled (client abort)", message, StringComparison.Ordinal);
        Assert.Contains("not an MSBuild", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timeout", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tests.sln", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Workspace Load Failed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMissingTargetFrameworkWorkspaceLoadMessage_is_not_nu1701_and_hints_bazel()
    {
        var diagnostics = new[]
        {
            "Failure: Msbuild failed when processing the file 'D:\\m\\src\\product\\kavkis\\Autotests\\_sln_kart\\generated\\Foo.csproj' with message: The \"ResolvePackageAssets\" task was not given a value for the required parameter \"TargetFramework\".",
        };

        var message = WorkspaceLoadGuidance.FormatMissingTargetFrameworkWorkspaceLoadMessage(
            @"D:\m\src\product\kavkis\Autotests\_sln_kart\ide_kart_m_src.sln",
            diagnostics,
            configuration: null,
            platform: null);

        Assert.Contains("empty TargetFramework", message, StringComparison.Ordinal);
        Assert.Contains("not NU1701", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("configuration", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bazel/generated", message, StringComparison.Ordinal);
        Assert.Contains("Foo.csproj", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Successfully loaded", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMissingCompileTargetWorkspaceLoadMessage_hints_inner_tfm()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "Directory.Build.props"),
                "<Project><TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks></Project>");
            var sln = Path.Combine(root, "Ecom.sln");
            File.WriteAllText(sln, string.Empty);

            var diagnostics = new[]
            {
                "Failure: Msbuild failed when processing the file 'C:\\src\\Contracts.csproj' with message: Project does not contain 'Compile' target.",
            };

            var message = WorkspaceLoadGuidance.FormatMissingCompileTargetWorkspaceLoadMessage(
                sln,
                diagnostics,
                configuration: null,
                platform: null,
                targetFramework: null);

            Assert.Contains("missing Compile target", message, StringComparison.Ordinal);
            Assert.Contains("CrossTargeting", message, StringComparison.Ordinal);
            Assert.Contains("targetFramework", message, StringComparison.Ordinal);
            Assert.Contains("net10.0", message, StringComparison.Ordinal);
            Assert.Contains("netstandard2.0", message, StringComparison.Ordinal);
            Assert.Contains("Contracts.csproj", message, StringComparison.Ordinal);
            Assert.Contains("get_code_skeleton", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Successfully loaded", message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void IsRoslynMsBuildBuildHostFailure_detects_remote_xmakeelements_message()
    {
        var ex = new InvalidOperationException(
            "An exception of type System.TypeInitializationException was thrown: "
            + "The type initializer for 'Microsoft.Build.Shared.XMakeElements' threw an exception.");

        Assert.True(WorkspaceLoadGuidance.IsRoslynMsBuildBuildHostFailure(ex));
        Assert.True(WorkspaceLoadGuidance.IsRoslynMsBuildBuildHostFailure(ex.Message));
    }

    [Fact]
    public void IsRoslynMsBuildBuildHostFailure_detects_type_initializer_exception()
    {
        var ex = new TypeInitializationException(
            "Microsoft.Build.Shared.XMakeElements",
            new InvalidOperationException("assembly mismatch"));

        Assert.True(WorkspaceLoadGuidance.IsRoslynMsBuildBuildHostFailure(ex));
    }

    [Fact]
    public void FormatRoslynMsBuildBuildHostFailureMessage_is_not_sdk_mismatch()
    {
        var message = WorkspaceLoadGuidance.FormatRoslynMsBuildBuildHostFailureMessage(
            @"J:\Proj\kav - GuiTestAppBl.sln");

        Assert.Contains("VS 2026 / MSBuild 18 BuildHost", message, StringComparison.Ordinal);
        Assert.Contains("XMakeElements", message, StringComparison.Ordinal);
        Assert.Contains("This is not", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MCP_MSBUILD_SDK_MISMATCH", message, StringComparison.Ordinal);
        Assert.Contains("GuiTestAppBl.sln", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.0.35", message, StringComparison.Ordinal);
        Assert.Contains("SDK-style", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Successfully loaded", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCaughtException_prefers_build_host_report()
    {
        var inner = new InvalidOperationException(
            "The type initializer for 'Microsoft.Build.Shared.XMakeElements' threw an exception.");
        var wrapped = new RoslynMsBuildBuildHostException(
            WorkspaceLoadGuidance.FormatRoslynMsBuildBuildHostFailureMessage(@"C:\app.sln"),
            inner);

        var formatted = WorkspaceLoadGuidance.FormatCaughtException(
            wrapped,
            fallback: "Failed to find references for `Foo`: boom");

        Assert.Contains("VS 2026 / MSBuild 18 BuildHost", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to find references", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatEmptyTestListMessage_flags_csproj_scope()
    {
        var message = WorkspaceLoadGuidance.FormatEmptyTestListMessage(
            @"C:\repo\Common\Common.csproj",
            projectCount: 1);

        Assert.Contains("No tests found", message, StringComparison.Ordinal);
        Assert.Contains("Agent signal", message, StringComparison.Ordinal);
        Assert.Contains("Common.csproj", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("single `.csproj`", message, StringComparison.Ordinal);
        Assert.Contains("Projects in workspace:** 1", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatNoMatchingTestsAgentHint_detects_path_mismatch_and_suffix_mode()
    {
        var message = WorkspaceLoadGuidance.FormatNoMatchingTestsAgentHint(
            loadedRoslynWorkspacePath: @"C:\repo\Common\Common.csproj",
            filterDescription: "Name suffix `.FooTests.Bar`",
            testTargetPath: @"C:\repo\Tests.sln");

        Assert.Contains("Agent diagnostics", message, StringComparison.Ordinal);
        Assert.Contains("Mismatch", message, StringComparison.Ordinal);
        Assert.Contains("name-suffix fallback", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tests.sln", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverSolutionCandidates_respects_maxCandidates()
    {
        var root = CreateTempRoot();
        var originalEnv = Environment.GetEnvironmentVariable("ROSLYN_MCP_WORKSPACE");

        try
        {
            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(root, $"S{i}.sln"), string.Empty);
            }

            Environment.SetEnvironmentVariable("ROSLYN_MCP_WORKSPACE", root);
            var found = WorkspaceLoadGuidance.DiscoverSolutionCandidates(maxCandidates: 2, maxDepth: 2);
            Assert.Equal(2, found.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSLYN_MCP_WORKSPACE", originalEnv);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynMcpWsGuide", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
