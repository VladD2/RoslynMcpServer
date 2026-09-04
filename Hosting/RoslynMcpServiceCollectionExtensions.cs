using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using RoslynMcpServer.Config;
using RoslynMcpServer.Diagnostics;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;

namespace RoslynMcpServer.Hosting;

public static class RoslynMcpServiceCollectionExtensions
{
    /// <summary>
    /// Registers shared services required by MCP tool types (e.g. <see cref="SolutionManager"/> and tool singletons).
    /// MCP <c>WithTools</c> activates tools via <see cref="ActivatorUtilities"/>; any constructor dependency must be resolvable here.
    /// </summary>
    public static IServiceCollection AddRoslynMcpCoreServices(this IServiceCollection services)
    {
        // Production Program.cs registers the merged-config WorkspaceConfig instance before this call;
        // the fallback covers hosts that do not (tests) — it binds to the host's (possibly empty) IConfiguration.
        if (!services.Any(d => d.ServiceType == typeof(WorkspaceConfig)))
        {
            services.AddSingleton<WorkspaceConfig>();
        }

        services.AddSingleton<SolutionManager>();

        // Registered before the MCP transport hosted service (AddMcpServer below): the configured
        // workspace load starts in the background as early as possible after host start.
        services.AddHostedService<WorkspacePrewarmService>();

        foreach (var toolType in McpToolRegistry.ToolTypes)
        {
            services.AddSingleton(toolType);
        }

        return services;
    }

    /// <summary>Registers stdio MCP transport and all Roslyn MCP tools (same set as production <c>Program.cs</c>).</summary>
    public static IMcpServerBuilder AddRoslynMcpServerTools(this IServiceCollection services)
    {
        services.AddRoslynMcpCoreServices();

        return services
            .AddMcpServer(o => McpInboundProtocolLogger.Register(o))
            .WithStdioServerTransport()
            .WithTools<WorkspaceTools>()
            .WithTools<CodeAnalysisTools>()
            .WithTools<CodeFixTools>()
            .WithTools<CodeSkeletonTools>()
            .WithTools<NavigationTools>()
            .WithTools<RefactoringTools>()
            .WithTools<AstTools>()
            .WithTools<BuildTools>()
            .WithTools<RunTools>()
            .WithTools<TestTools>()
            .WithTools<NuGetTools>()
            .WithTools<ProjectTools>()
            .WithTools<UtilityTools>()
            .WithTools<ServerLifecycleTools>();
    }
}
