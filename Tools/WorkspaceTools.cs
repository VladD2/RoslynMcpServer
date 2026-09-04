using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcpServer.Config;
using RoslynMcpServer.Diagnostics;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools;

public sealed class WorkspaceTools
{
    private readonly SolutionManager _solutionManager;
    private readonly WorkspaceConfig _workspaceConfig;
    private readonly ILogger<WorkspaceTools> _logger;
    private readonly LoadWorkspaceResponseBuilder _responseBuilder;

    public WorkspaceTools(
        SolutionManager solutionManager,
        WorkspaceConfig workspaceConfig,
        ILogger<WorkspaceTools> logger)
    {
        _solutionManager = solutionManager;
        _workspaceConfig = workspaceConfig;
        _logger = logger;
        _responseBuilder = new LoadWorkspaceResponseBuilder(solutionManager, logger);
    }

    [McpServerTool(Name = "reload", Title = "Reload C# workspace")]
    [Description(
        "Reloads the C# workspace: disposes the current MSBuildWorkspace and loads it again. "
        + "Path and MSBuild properties come from the `RoslynMcp.jsonc` config (`workspace-path`, `configuration`, `platform`, `target-framework`); "
        + "tool arguments override the config values. "
        + "Call after `dotnet build` (generated `obj`), after edits to `.csproj`/`.sln`/`Directory.Build.props`, or when switching projects. "
        + "Ordinary `.cs` saves do not require reload — they are picked up from disk automatically (disk-sync). "
        + "The first load of a large solution can take minutes — raise the host MCP timeout (e.g. OpenCode `timeout: 600000`); "
        + "a host abort mid-load returns **Workspace Load Cancelled (client abort)** (not an MSBuild failure). "
        + "`run_dotnet_build` / `run_dotnet_test` inherit the loaded configuration/platform when their own args are omitted.")]
    public Task<string> Reload(
        [Description("Optional absolute path to a `.sln`, `.slnx`, or `.csproj` file (not a directory). Omit to use config `workspace-path` (RoslynMcp.jsonc).")]
        string? workspacePath = null,
        [Description(
            "Optional MSBuild Configuration global property (e.g. `Debug`, `Release`, `Sit-Debug`). "
            + "Omit to use config `configuration` / SDK default.")]
        string? configuration = null,
        [Description(
            "Optional MSBuild Platform global property (e.g. `AnyCPU`, `x64`). `Any CPU` is normalized to `AnyCPU`. "
            + "Omit to use config `platform` / SDK default.")]
        string? platform = null,
        [Description(
            "Optional MSBuild TargetFramework global property (e.g. `net10.0`, `netstandard2.0`). "
            + "Omit to use config `target-framework` / SDK default.")]
        string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var path = NormalizeOptional(workspacePath) ?? _workspaceConfig.WorkspacePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(
                ToolTelemetry.TraceAndReturn(
                    nameof(Reload),
                    LoadWorkspaceResponseBuilder.FormatNoWorkspacePathMessage()));
        }

        return _responseBuilder.ReloadAsync(
            path,
            NormalizeOptional(configuration) ?? _workspaceConfig.Configuration,
            NormalizeOptional(platform) ?? _workspaceConfig.Platform,
            NormalizeOptional(targetFramework) ?? _workspaceConfig.TargetFramework,
            cancellationToken);
    }

    [McpServerTool(Name = "load_workspace", Title = "Load C# workspace explicitly")]
    [Description(
        "Explicitly loads a `.sln`/`.slnx`/`.csproj` into the semantic engine **without restarting the MCP server**. "
        + "Use when `workspace-path` is not set in `RoslynMcp.jsonc` (the configured workspace otherwise loads lazily and this tool is not needed), "
        + "to load a different solution than the config, or to override `configuration`/`platform`/`target-framework` "
        + "(same path + properties returns the cache unless the project graph is stale). "
        + "A different path or properties replaces the currently loaded workspace. "
        + "Large solutions can take minutes — host timeout ≥ 600000. "
        + "After a successful load, saved `.cs` sync from disk automatically; "
        + "a changed `.csproj`/`.sln` needs `reload` (or `reset_workspace` + `load_workspace`).")]
    public Task<string> LoadWorkspace(
        [Description("Absolute path to a `.sln`, `.slnx`, or `.csproj` file (not a directory). The config `workspace-path` is **not** substituted here — that is the role of `reload`.")]
        string workspacePath,
        [Description(
            "Optional MSBuild Configuration global property (e.g. `Debug`, `Release`, `Sit-Debug`). "
            + "Omit for the SDK/solution default.")]
        string? configuration = null,
        [Description(
            "Optional MSBuild Platform global property (e.g. `AnyCPU`, `x64`). `Any CPU` is normalized to `AnyCPU`. "
            + "Omit for the SDK/solution default.")]
        string? platform = null,
        [Description(
            "Optional MSBuild TargetFramework global property (e.g. `net10.0`, `netstandard2.0`). "
            + "Omit for the SDK/solution default.")]
        string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var path = NormalizeOptional(workspacePath);
        if (path is null)
        {
            return Task.FromResult(ToolTelemetry.TraceAndReturn(
                nameof(LoadWorkspace),
                "Error: `workspacePath` is required (absolute `.sln`/`.slnx`/`.csproj` path; the config `workspace-path` is not substituted — use `reload` for that)."));
        }

        return _responseBuilder.LoadWorkspaceAsync(
            path,
            NormalizeOptional(configuration),
            NormalizeOptional(platform),
            NormalizeOptional(targetFramework),
            cancellationToken);
    }

    [McpServerTool(Name = "reset_workspace", Title = "Reset C# workspace")]
    [Description(
        "Disposes the in-process MSBuildWorkspace and drops the cached solution (frees memory, clean state). "
        + "Use while developing this server, or before switching solutions/parameters via `load_workspace`. "
        + "Ordinary `.cs` edits and generated `obj` after build do **not** require reset — use `reload`. "
        + "Does not restart the MCP process — use `stop_mcp_server` if the server binary itself was rebuilt.")]
    public async Task<string> ResetWorkspace(CancellationToken cancellationToken = default)
    {
        await _solutionManager.ClearWorkspaceAsync(cancellationToken);
        return ToolTelemetry.TraceAndReturn(
            nameof(ResetWorkspace),
            "Workspace cleared. Set `workspace-path` in `RoslynMcp.jsonc` for lazy load, call `reload`, or call `load_workspace` with a `.sln`/`.slnx`/`.csproj` path.");
    }

    private static string? NormalizeOptional(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Shared load + response builder for <c>reload</c> (dispose + load) and <c>load_workspace</c>
    /// (explicit load by path): <c>LoadAsync</c> + all load-failure branches + the health block.
    /// </summary>
    private sealed class LoadWorkspaceResponseBuilder
    {
        private readonly SolutionManager _solutionManager;
        private readonly ILogger<WorkspaceTools> _logger;

        public LoadWorkspaceResponseBuilder(SolutionManager solutionManager, ILogger<WorkspaceTools> logger)
        {
            _solutionManager = solutionManager;
            _logger = logger;
        }

        public async Task<string> ReloadAsync(
            string path,
            string? configuration,
            string? platform,
            string? targetFramework,
            CancellationToken cancellationToken)
        {
            try
            {
                await _solutionManager.ClearWorkspaceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ToolTelemetry.TraceAndReturn(nameof(WorkspaceTools.Reload), "Reload was cancelled.");
            }

            return await LoadAndBuildResponseAsync(
                nameof(WorkspaceTools.Reload),
                path,
                configuration,
                platform,
                targetFramework,
                cancellationToken);
        }

        /// <summary>
        /// Explicit load by path (no <c>ClearWorkspaceAsync</c>): same path + properties returns the cache
        /// (unless the project graph is stale); a different path or properties replaces the current workspace.
        /// </summary>
        public Task<string> LoadWorkspaceAsync(
            string path,
            string? configuration,
            string? platform,
            string? targetFramework,
            CancellationToken cancellationToken)
        {
            return LoadAndBuildResponseAsync(
                nameof(WorkspaceTools.LoadWorkspace),
                path,
                configuration,
                platform,
                targetFramework,
                cancellationToken);
        }

        private async Task<string> LoadAndBuildResponseAsync(
            string toolName,
            string path,
            string? configuration,
            string? platform,
            string? targetFramework,
            CancellationToken cancellationToken)
        {
            Solution solution;
            try
            {
                solution = await _solutionManager.LoadAsync(
                    path,
                    cancellationToken,
                    configuration,
                    platform,
                    targetFramework);
            }
            catch (ArgumentException ex)
            {
                return ToolTelemetry.TraceAndReturn(toolName, $"Error: {ex.Message}");
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "{ToolName} cancelled by client for {Path}", toolName, path);
                return ToolTelemetry.TraceAndReturn(
                    toolName,
                    WorkspaceLoadGuidance.FormatClientCancelledWorkspaceLoadMessage(path)
                    + Environment.NewLine
                    + MsBuildEnvironmentInfo.FormatMarkdownSection());
            }
            catch (RoslynMsBuildBuildHostException ex)
            {
                _logger.LogError(ex, "Failed to load workspace from {Path} (VS 2026 / MSBuild 18 BuildHost)", path);
                return ToolTelemetry.TraceAndReturn(toolName, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load workspace from {Path}", path);
                if (WorkspaceLoadGuidance.IsRoslynMsBuildBuildHostFailure(ex))
                {
                    return ToolTelemetry.TraceAndReturn(
                        toolName,
                        WorkspaceLoadGuidance.FormatRoslynMsBuildBuildHostFailureMessage(path));
                }

                return ToolTelemetry.TraceAndReturn(toolName, BuildFailureReport(path, new[] { ex.Message }));
            }

            return BuildSuccessResponse(toolName, solution, path);
        }

        private string BuildSuccessResponse(string toolName, Solution solution, string path)
        {
            var projects = solution.Projects.ToList();
            var projectCount = projects.Count;
            var diagnostics = _solutionManager.LastDiagnostics
                .Select(d => WorkspaceDiagnosticFormatter.Format(d.Kind.ToString(), d.Message))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (projectCount == 0 || diagnostics.Any(WorkspaceDiagnosticFormatter.IsBlockingLoadFailure))
            {
                if (diagnostics.Any(WorkspaceDiagnosticFormatter.IsMissingTargetFrameworkEvaluation))
                {
                    return ToolTelemetry.TraceAndReturn(
                        toolName,
                        WorkspaceLoadGuidance.FormatMissingTargetFrameworkWorkspaceLoadMessage(
                            path,
                            diagnostics,
                            _solutionManager.LoadedConfiguration,
                            _solutionManager.LoadedPlatform));
                }

                if (diagnostics.Any(WorkspaceDiagnosticFormatter.IsMissingCompileTarget))
                {
                    return ToolTelemetry.TraceAndReturn(
                        toolName,
                        WorkspaceLoadGuidance.FormatMissingCompileTargetWorkspaceLoadMessage(
                            path,
                            diagnostics,
                            _solutionManager.LoadedConfiguration,
                            _solutionManager.LoadedPlatform,
                            _solutionManager.LoadedTargetFramework));
                }

                if (diagnostics.Any(WorkspaceLoadGuidance.IsRoslynMsBuildBuildHostFailure))
                {
                    return ToolTelemetry.TraceAndReturn(
                        toolName,
                        WorkspaceLoadGuidance.FormatRoslynMsBuildBuildHostFailureMessage(path));
                }

                return ToolTelemetry.TraceAndReturn(
                    toolName,
                    BuildFailureReport(
                        path,
                        diagnostics.Count > 0 ? diagnostics : new[] { "Workspace loaded with zero projects." }));
            }

            var sb = new StringBuilder();
            sb.AppendLine(
                WorkspaceHealthReporter.BuildHealthSection(
                    path,
                    solution,
                    _solutionManager.LoadedConfiguration,
                    _solutionManager.LoadedPlatform,
                    _solutionManager.LoadedTargetFramework,
                    _logger));
            sb.AppendLine();
            sb.AppendLine($"Successfully loaded workspace. Found {projectCount} projects:");
            foreach (var project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {project.Name} [{InferCompactProjectType(project)}]");
            }

            if (diagnostics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Workspace diagnostics:");
                foreach (var diagnostic in diagnostics)
                {
                    sb.AppendLine($"- {diagnostic}");
                }

                if (diagnostics.Any(static d =>
                        d.Contains("do not have a version specified", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.AppendLine();
                    sb.AppendLine(
                        "> **Note:** Design-time MSBuild can report missing `PackageReference` versions before `dotnet restore`, "
                        + "even when `Version=` is present in the `.csproj` on disk. Run `dotnet restore` at the solution root, "
                        + "then `reload`. Set MCP env `ROSLYN_MCP_WORKSPACE` to the repo root "
                        + "(where `global.json` lives) so MSBuild.Locator pins the same SDK as `run_dotnet_build`.");
                }

                if (diagnostics.Any(static d =>
                        d.Contains("NuGet audit", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.AppendLine();
                    sb.AppendLine(
                        "> **Note:** NuGet audit advisories (GHSA / NU1903) are shown as warnings here; `dotnet build` may still fail with `NU1904` if audit is treated as error. Use `run_dotnet_build` for the exact NU lines.");
                }

                if (diagnostics.Any(static d =>
                        d.Contains("NuGet prune", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.AppendLine();
                    sb.AppendLine(
                        "> **Note:** NuGet prune / unused `PackageReference` advisories are shown as warnings; the workspace is usable. Remove unused package references if you want a clean restore graph.");
                }

                if (diagnostics.Any(static d =>
                        d.Contains("NuGet compat", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.AppendLine();
                    sb.AppendLine(
                        "> **Note:** NuGet TFM-compat advisories (`NU1701`, netfx package in a netcore/net10 project) are shown as warnings; "
                        + "`dotnet build` / Visual Studio usually succeed. Use `run_dotnet_build` for the exact NU lines. "
                        + "A mis-targeted project may still have incomplete references in Roslyn — prefer fixing the TFM or package.");
                }

                if (diagnostics.Any(static d =>
                        d.Contains("MSBuild design-time", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.AppendLine();
                    sb.AppendLine(
                        "> **Note:** Design-time MSBuild warnings (ASP.NET/SDK deprecation, processor-architecture mismatch, "
                        + "analyzer project references) are shown as warnings; the workspace is usable. "
                        + "MSBuildWorkspace often wraps them as `Msbuild failed when processing the file` without a `warning XXXX` code. "
                        + "Use `run_dotnet_build` for real `error NU|MSB|NETSDK` lines.");
                }
            }

            return ToolTelemetry.TraceAndReturn(toolName, sb.ToString());
        }

        /// <summary>
        /// Guidance when neither the arguments nor the config provide a workspace path
        /// (same candidate discovery as the "No active workspace" errors).
        /// </summary>
        public static string FormatNoWorkspacePathMessage()
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "Error: no workspace path — set `workspace-path` (+ optional `configuration`/`platform`/`target-framework`) in `RoslynMcp.jsonc` "
                + "(exe dir or cwd, cwd wins), or pass `workspacePath` to `reload`.");
            sb.AppendLine();
            sb.AppendLine(
                "File-scoped semantic tools (`find_symbol_references`, `get_call_graph`, `get_class_skeleton`, `get_diagnostics_for_file`, AST tools) "
                + "auto-load the nearest workspace (config first, then walk-up from the file) — they work **without** `workspace-path`. "
                + "`workspace-path`/`reload` are required for solution-wide tools (`find_usages`, `find_symbol_definition`, `find_implementations`, `get_test_list`).");
            sb.AppendLine();

            var candidates = WorkspaceLoadGuidance.DiscoverSolutionCandidates();
            if (candidates.Count == 0)
            {
                sb.AppendLine("No `.sln`/`.slnx` candidates found under `ROSLYN_MCP_WORKSPACE` or the current directory.");
                sb.AppendLine("Set MCP env `ROSLYN_MCP_WORKSPACE` to the repo root, or pass an absolute solution path.");
            }
            else
            {
                sb.AppendLine("Candidate solution files:");
                foreach (var candidate in candidates)
                {
                    sb.AppendLine($"- `{candidate}`");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildFailureReport(string path, IEnumerable<string> errors)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Workspace Load Failed");
            sb.AppendLine();
            sb.AppendLine($"- **Path:** `{path}`");
            sb.AppendLine();
            sb.AppendLine("### Errors");
            foreach (var error in errors)
            {
                sb.AppendLine($"- {error}");
            }

            sb.Append(MsBuildEnvironmentInfo.FormatMarkdownSection());
            return sb.ToString();
        }

        private static string InferCompactProjectType(Project project)
        {
            var references = project.MetadataReferences
                .OfType<PortableExecutableReference>()
                .Select(r => r.Display ?? string.Empty)
                .Where(static d => !string.IsNullOrWhiteSpace(d))
                .ToArray();

            if (ContainsAny(references, "xunit", "nunit", "mstest", "microsoft.net.test.sdk")
                || ContainsAny(project.AssemblyName, ".tests", "tests"))
            {
                return "Test";
            }

            if (ContainsAny(references, "microsoft.aspnetcore.app"))
            {
                return "Web API";
            }

            if (ContainsAny(references, "microsoft.extensions.hosting"))
            {
                return "Worker";
            }

            return "Library";
        }

        private static bool ContainsAny(IEnumerable<string> values, params string[] markers)
        {
            foreach (var value in values)
            {
                if (ContainsAny(value, markers))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsAny(string? value, params string[] markers)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var marker in markers)
            {
                if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
