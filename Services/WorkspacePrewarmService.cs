using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Config;

namespace RoslynMcpServer.Services;

/// <summary>
/// Starts the configured workspace load (<c>workspace-path</c> from <c>RoslynMcp.jsonc</c>) in the
/// background right after the MCP server starts, so that by the time the agent issues its first
/// semantic call (or an explicit <c>load_workspace</c> / <c>reload</c>) the load is already in flight
/// or done — "opening" the project no longer blocks for the full load time.
/// The prewarm is never a prerequisite for serving: the stdio transport answers immediately, and a
/// request arriving mid-prewarm simply waits on the same <see cref="SolutionManager"/> lock (no
/// double load). A failed prewarm keeps the lazy-load contract: a logged warning and the usual
/// "no active workspace" guidance on the next semantic call.
/// </summary>
public sealed class WorkspacePrewarmService : IHostedService
{
    private const int StopWaitTimeoutMs = 30_000;

    private readonly SolutionManager _solutionManager;
    private readonly WorkspaceConfig _workspaceConfig;
    private readonly ILogger<WorkspacePrewarmService> _logger;
    private CancellationTokenSource? _stopping;
    private Task? _prewarmTask;

    public WorkspacePrewarmService(
        SolutionManager solutionManager,
        WorkspaceConfig workspaceConfig,
        ILogger<WorkspacePrewarmService> logger)
    {
        _solutionManager = solutionManager;
        _workspaceConfig = workspaceConfig;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_workspaceConfig.WorkspacePath))
        {
            return Task.CompletedTask;
        }

        _stopping = new CancellationTokenSource();
        _logger.LogInformation(
            "Prewarming the configured workspace in the background: {Path} (Configuration={Configuration}, Platform={Platform}, TargetFramework={TargetFramework})",
            _workspaceConfig.WorkspacePath,
            _workspaceConfig.Configuration ?? "(default)",
            _workspaceConfig.Platform ?? "(default)",
            _workspaceConfig.TargetFramework ?? "(default)");

        _prewarmTask = Task.Run(() => PrewarmAsync(_stopping.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is null)
        {
            return;
        }

        _stopping.Cancel();
        if (_prewarmTask is not null)
        {
            var finished = await Task.WhenAny(_prewarmTask, Task.Delay(StopWaitTimeoutMs, CancellationToken.None));
            if (!ReferenceEquals(finished, _prewarmTask))
            {
                _logger.LogDebug(
                    "Workspace prewarm still running {TimeoutMs}ms after the stop request; it will end via its own cancellation.",
                    StopWaitTimeoutMs);
            }
        }

        _stopping.Dispose();
    }

    private async Task PrewarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            var solution = await _solutionManager.GetCurrentSolutionAfterDiskSyncAsync(cancellationToken);
            if (solution is null)
            {
                _logger.LogWarning(
                    "Workspace prewarm finished with no loaded workspace (see the lazy-load warning above); semantic tools will report no active workspace.");
            }
            else
            {
                _logger.LogInformation(
                    "Workspace prewarm finished: {ProjectCount} projects loaded from {Path}.",
                    solution.ProjectIds.Count,
                    _solutionManager.GetLoadedWorkspacePath() ?? _workspaceConfig.WorkspacePath);
            }
        }
        catch (WorkspaceLoadCancelledException)
        {
            _logger.LogInformation("Workspace prewarm cancelled (server shutdown).");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Workspace prewarm cancelled (server shutdown).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace prewarm failed: {Message}", ex.Message);
        }
    }
}
