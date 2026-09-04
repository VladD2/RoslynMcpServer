using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using RoslynMcpServer.Config;

namespace RoslynMcpServer.Services;

public static class McpServerInfoHelper
{
    public static string BuildInfoMarkdown(WorkspaceConfig workspaceConfig, SolutionManager solutionManager)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var exePath = Environment.ProcessPath ?? assembly.Location;
        var exeTime = File.Exists(exePath) ? File.GetLastWriteTime(exePath) : (DateTime?)null;
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        var latestLog = Directory.Exists(logDir)
            ? Directory.EnumerateFiles(logDir, "mcp-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;

        var toolCount = WorkspaceHealthReporter.CountRegisteredTools();
        var loadedSolution = solutionManager.GetCurrentSolution();

        var sb = new StringBuilder();
        sb.AppendLine("## Roslyn MCP server info");
        sb.AppendLine();
        sb.AppendLine($"- **Assembly:** `{assembly.GetName().Name}` v{assembly.GetName().Version}");
        sb.AppendLine($"- **Process path:** `{exePath}`");
        if (exeTime is not null)
        {
            sb.AppendLine($"- **Binary modified (local):** {exeTime:O}");
        }

        sb.AppendLine($"- **Base directory:** `{AppContext.BaseDirectory}`");
        sb.AppendLine($"- **Registered MCP tools:** {toolCount}");
        sb.AppendLine($"- **Latest log file:** {(latestLog is null ? "(none yet)" : $"`{latestLog}`")}");
        sb.AppendLine();

        // Config (RoslynMcp.jsonc): which files were merged (exe dir first, then cwd — later wins)
        // plus the workspace settings read from them.
        sb.AppendLine("### Config (RoslynMcp.jsonc)");
        var loadedFrom = workspaceConfig.LoadedFromPaths;
        sb.AppendLine($"- **Config loaded from:** {(loadedFrom.Count == 0 ? "(none found)" : string.Join(" + ", loadedFrom.Select(p => $"`{p}`")))
        }");
        sb.AppendLine($"- **workspace-path:** {(string.IsNullOrWhiteSpace(workspaceConfig.WorkspacePath) ? "(not set)" : $"`{workspaceConfig.WorkspacePath}`")}");
        sb.AppendLine($"- **configuration:** {(string.IsNullOrWhiteSpace(workspaceConfig.Configuration) ? "(not set)" : $"`{workspaceConfig.Configuration}`")}");
        sb.AppendLine($"- **platform:** {(string.IsNullOrWhiteSpace(workspaceConfig.Platform) ? "(not set)" : $"`{workspaceConfig.Platform}`")}");
        sb.AppendLine($"- **target-framework:** {(string.IsNullOrWhiteSpace(workspaceConfig.TargetFramework) ? "(not set)" : $"`{workspaceConfig.TargetFramework}`")}");
        sb.AppendLine();

        // Workspace state (loaded lazily from config on the first semantic call, or via `reload`).
        sb.AppendLine("### Workspace");
        if (loadedSolution is null)
        {
            sb.AppendLine("- **Workspace loaded:** no (loaded lazily from the config `workspace-path` on the first semantic call, or via `reload`)");
        }
        else
        {
            sb.AppendLine($"- **Workspace loaded:** yes ({loadedSolution.ProjectIds.Count} projects)");
            var loadedPath = solutionManager.GetLoadedWorkspacePath();
            sb.AppendLine($"- **Loaded path:** {(string.IsNullOrWhiteSpace(loadedPath) ? "(unknown)" : $"`{loadedPath}`")}");
            sb.AppendLine($"- **Loaded configuration:** {(string.IsNullOrWhiteSpace(solutionManager.LoadedConfiguration) ? "(default)" : $"`{solutionManager.LoadedConfiguration}`")}");
            sb.AppendLine($"- **Loaded platform:** {(string.IsNullOrWhiteSpace(solutionManager.LoadedPlatform) ? "(default)" : $"`{solutionManager.LoadedPlatform}`")}");
            sb.AppendLine($"- **Loaded target-framework:** {(string.IsNullOrWhiteSpace(solutionManager.LoadedTargetFramework) ? "(default)" : $"`{solutionManager.LoadedTargetFramework}`")}");
        }

        sb.AppendLine();
        sb.AppendLine("After code changes run `dotnet publish -c Release -r win-x64`, then Reload MCP in Cursor.");
        return sb.ToString().TrimEnd();
    }
}
