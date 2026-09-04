using Microsoft.Extensions.Configuration;

namespace RoslynMcpServer.Config;

/// <summary>
/// Workspace and search-output settings read from the <c>RoslynMcp.jsonc</c> config
/// (merged exe + cwd, cwd wins; flat kebab-case keys, <see cref="IConfiguration"/>-based).
/// </summary>
public sealed class WorkspaceConfig
{
    /// <summary>Default cap for search methods when <c>max-results</c> is absent from the config.</summary>
    public const int DefaultMaxResults = 50;

    /// <summary><c>workspace-path</c>: solution/project to load lazily (.sln / .slnx / .csproj).</summary>
    public string? WorkspacePath { get; }

    /// <summary><c>configuration</c>: MSBuild <c>Configuration</c> global property (e.g. "Sit-Debug").</summary>
    public string? Configuration { get; }

    /// <summary><c>platform</c>: MSBuild <c>Platform</c> global property (e.g. "AnyCPU").</summary>
    public string? Platform { get; }

    /// <summary><c>target-framework</c>: MSBuild <c>TargetFramework</c> global property (e.g. "net10.0").</summary>
    public string? TargetFramework { get; }

    /// <summary><c>max-results</c>: unified cap for search methods; default <see cref="DefaultMaxResults"/> (50).</summary>
    public int? MaxResults { get; }

    /// <summary><c>preview</c>: include source line text in search results; default false (the model decides).</summary>
    public bool Preview { get; }

    /// <summary>
    /// Absolute paths of the <c>RoslynMcp.jsonc</c> files found and loaded (exe dir first, then cwd;
    /// later files win). Empty when no config file was found.
    /// </summary>
    public IReadOnlyList<string> LoadedFromPaths { get; }

    public WorkspaceConfig(IConfiguration configuration, IReadOnlyList<string>? loadedFromPaths = null)
    {
        WorkspacePath = ReadString(configuration, "workspace-path");
        Configuration = ReadString(configuration, "configuration");
        Platform = ReadString(configuration, "platform");
        TargetFramework = ReadString(configuration, "target-framework");
        MaxResults = configuration.GetValue<int?>("max-results") ?? DefaultMaxResults;
        Preview = configuration.GetValue<bool?>("preview") ?? false;
        LoadedFromPaths = loadedFromPaths ?? Array.Empty<string>();
    }

    private static string? ReadString(IConfiguration configuration, string key)
    {
        var value = configuration[key]?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
