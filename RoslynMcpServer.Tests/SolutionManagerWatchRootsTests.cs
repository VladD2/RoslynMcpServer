using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Unit tests for <see cref="SolutionManager.ComputeWatchRoots"/> — the pure function that computes
/// the minimal non-nested set of directories to watch (solution folder + every project's directory).
/// </summary>
public sealed class SolutionManagerWatchRootsTests
{
    private static readonly IEqualityComparer<string> Comparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Full(params string[] segments) =>
        Path.GetFullPath(Path.Combine([Path.GetTempPath(), ..segments]));

    [Fact]
    public void ComputeWatchRoots_all_projects_under_root_returns_single_root()
    {
        var roots = SolutionManager.ComputeWatchRoots(
            Full("Repo", "Root.sln"),
            new[]
            {
                Full("Repo", "App", "App.csproj"),
                Full("Repo", "Lib", "Lib.csproj"),
                Full("Repo", "App", "Sub", "Deep.csproj"),
            },
            Comparer);

        Assert.Equal(new[] { Full("Repo") }, roots);
    }

    [Fact]
    public void ComputeWatchRoots_project_outside_root_returns_two_roots()
    {
        var roots = SolutionManager.ComputeWatchRoots(
            Full("Repo", "Root.sln"),
            new[]
            {
                Full("Repo", "App", "App.csproj"),
                Full("Outside", "Lib", "Lib.csproj"),
            },
            Comparer);

        Assert.Equal(new[] { Full("Repo"), Full("Outside", "Lib") }, roots);
    }

    [Fact]
    public void ComputeWatchRoots_sibling_external_projects_not_merged_nested_not_duplicated()
    {
        var roots = SolutionManager.ComputeWatchRoots(
            Full("Repo", "Root.sln"),
            new[]
            {
                Full("Shared", "A", "A.csproj"),
                Full("Shared", "B", "B.csproj"),
                Full("Shared", "A", "Sub", "C.csproj"),
            },
            Comparer);

        Assert.Equal(
            new[] { Full("Repo"), Full("Shared", "A"), Full("Shared", "B") },
            roots);
    }

    [Fact]
    public void ComputeWatchRoots_parent_project_added_last_still_yields_minimal_set()
    {
        var roots = SolutionManager.ComputeWatchRoots(
            Full("Repo", "Root.sln"),
            new[]
            {
                Full("Shared", "Lib", "Sub", "A.csproj"),
                Full("Shared", "Lib", "B.csproj"),
            },
            Comparer);

        Assert.Equal(
            new[] { Full("Repo"), Full("Shared", "Lib") },
            roots);
    }

    [Fact]
    public void ComputeWatchRoots_project_without_file_path_is_skipped()
    {
        var roots = SolutionManager.ComputeWatchRoots(
            Full("Repo", "Root.sln"),
            new[] { null, string.Empty, "   ", Full("Repo", "App", "App.csproj") },
            Comparer);

        Assert.Equal(new[] { Full("Repo") }, roots);
    }

    [Fact]
    public void ComputeWatchRoots_deduplicates_case_insensitive_paths()
    {
        var roots = SolutionManager.ComputeWatchRoots(
            Full("Repo", "Root.sln"),
            new[] { Path.Combine(Full("REPO"), "App", "App.csproj") },
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            new[] { Full("Repo") },
            roots,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeWatchRoots_does_not_treat_prefix_sibling_as_nested()
    {
        var roots = SolutionManager.ComputeWatchRoots(
            Full("Repo", "Root.sln"),
            new[] { Full("RepoAB", "Lib", "Lib.csproj") },
            Comparer);

        Assert.Equal(
            new[] { Full("Repo"), Full("RepoAB", "Lib") },
            roots);
    }
}
