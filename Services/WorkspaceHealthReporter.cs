using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcpServer.Hosting;

namespace RoslynMcpServer.Services;

/// <summary>Compact workspace health block for <c>reload</c> / lazy config load / <c>load_workspace</c> responses.</summary>
public static class WorkspaceHealthReporter
{
    public static string BuildHealthSection(
        string workspacePath,
        Solution solution,
        string? configuration = null,
        string? platform = null,
        string? targetFramework = null,
        ILogger? logger = null)
    {
        var fullPath = Path.GetFullPath(workspacePath);
        var workDir = WorkspaceRootResolver.ResolveDotNetWorkingDirectory(fullPath);
        var globalJson = GlobalJsonSdkReader.FindGlobalJsonPath(workDir);
        var pinnedSdk = GlobalJsonSdkReader.TryGetPinnedSdkVersion(workDir);
        var sdkDir = GlobalJsonSdkReader.TryResolveSdkDirectory(workDir, prefer64Bit: Environment.Is64BitProcess);

        var sb = new StringBuilder();
        sb.AppendLine("### Workspace health");
        sb.AppendLine($"- **Solution/project:** `{fullPath}`");
        sb.AppendLine($"- **DotNet working directory:** `{workDir}`");
        sb.AppendLine($"- **Projects loaded:** {solution.Projects.Count()}");
        sb.AppendLine($"- **Analyzer references:** {DescribeAnalyzerReferences(solution)}");
        sb.AppendLine(
            $"- **MSBuild Configuration:** {(string.IsNullOrWhiteSpace(configuration) ? "(SDK/workspace default)" : $"`{configuration}`")}");
        sb.AppendLine(
            $"- **MSBuild Platform:** {(string.IsNullOrWhiteSpace(platform) ? "(SDK/workspace default)" : $"`{platform}`")}");
        sb.AppendLine(
            $"- **MSBuild TargetFramework:** {(string.IsNullOrWhiteSpace(targetFramework) ? "(SDK/workspace default)" : $"`{targetFramework}`")}");
        sb.AppendLine($"- **global.json:** {(globalJson is null ? "(not found)" : $"`{globalJson}`")}");
        sb.AppendLine($"- **Pinned SDK (global.json):** {(pinnedSdk ?? "(none)")}");
        sb.AppendLine($"- **Resolved SDK directory:** {(sdkDir ?? "(not resolved)")}");
        sb.AppendLine($"- **Restore assets:** {DescribeRestoreAssets(solution, logger)}");
        sb.AppendLine($"- **Registered MCP tools:** {CountRegisteredTools()} (use `get_mcp_server_info` for binary path)");
        sb.AppendLine();
        sb.AppendLine(
            "> **Workflow:** The workspace prewarms in the background from the `RoslynMcp.jsonc` config (`workspace-path`) right after the server starts; use `reload` to force a reload after `dotnet build` or `.csproj`/`.sln`/`Directory.Build.props` changes. "
            + "Build/test/run via `run_dotnet_build`, `run_dotnet_test`, `run_dotnet_run` — not raw shell `dotnet`. "
            + "Find usages: `find_usages` / `find_symbol_references`.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Reports unresolved analyzer references per project. An <see cref="UnresolvedAnalyzerReference"/> means
    /// MSBuildWorkspace could not resolve the analyzer assembly (typically: a custom analyzer not built for the
    /// current configuration in the monorepo). Roslyn 5.9.0 cannot compute a project checksum that contains such
    /// a stub, which breaks solution-wide symbol searches until the analyzer is built or removed from the
    /// <c>.csproj</c>; the server strips the stub in-memory (see <see cref="WorkspaceAnalyzerSanitizer"/>), so
    /// the line names the project to fix for a permanent cure.
    /// </summary>
    private static string DescribeAnalyzerReferences(Solution solution)
    {
        const int maxListed = 10;
        var offenders = solution.Projects
            .Select(p => (Project: p, Unresolved: p.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().ToList()))
            .Where(x => x.Unresolved.Count > 0)
            .ToList();

        if (offenders.Count == 0)
        {
            return "ok (0 unresolved)";
        }

        var total = offenders.Sum(x => x.Unresolved.Count);
        var entries = offenders
            .SelectMany(x => x.Unresolved.Select(r => $"`{x.Project.Name}` — `{r.FullPath}`"))
            .ToList();
        var details = string.Join("; ", entries.Take(maxListed));
        if (entries.Count > maxListed)
        {
            details += $"; … and {entries.Count - maxListed} more";
        }

        return $"{total} unresolved analyzer reference(s) in {offenders.Count} project(s): {details} "
            + "(build the missing analyzer for the current configuration or remove it from the `.csproj`; "
            + "the stub is stripped in-memory so symbol searches keep working)";
    }

    private static string DescribeRestoreAssets(Solution solution, ILogger? logger)
    {
        var projectPaths = solution.Projects
            .Select(p => p.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        if (projectPaths.Count == 0)
        {
            return "unknown (no project paths)";
        }

        var withAssets = 0;
        var missingObj = 0;
        foreach (var csproj in projectPaths)
        {
            var projectDir = Path.GetDirectoryName(csproj);
            if (projectDir is null)
            {
                continue;
            }

            var objDir = Path.Combine(projectDir, "obj");
            if (!Directory.Exists(objDir))
            {
                // obj may be redirected (e.g. Directory.Build.props in a monorepo) — not a restore failure.
                missingObj++;
                logger?.LogDebug(
                    "Restore assets: obj directory not found for {CsProj} (expected {ObjDir}); likely redirected — skipping asset check.",
                    csproj,
                    objDir);
                continue;
            }

            if (Directory.EnumerateFiles(objDir, "project.assets.json", SearchOption.AllDirectories).Any())
            {
                withAssets++;
            }
        }

        if (missingObj > 0)
        {
            logger?.LogInformation(
                "Restore assets: {MissingObj}/{Total} project(s) have no obj directory (possibly redirected).",
                missingObj,
                projectPaths.Count);
        }

        var missingObjNote = missingObj > 0 ? $", {missingObj} without obj (obj not found — redirected?)" : string.Empty;

        return withAssets + missingObj == projectPaths.Count
            ? $"ok ({withAssets}/{projectPaths.Count} projects have obj/project.assets.json{missingObjNote})"
            : $"incomplete ({withAssets}/{projectPaths.Count} have obj/project.assets.json{missingObjNote} — run `dotnet restore` at solution root, then `reload`)";
    }

    public static int CountRegisteredTools()
    {
        return McpToolRegistry.ToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Count(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);
    }
}
