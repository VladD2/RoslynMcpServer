namespace RoslynMcpServer.Services;

/// <summary>
/// Computes the minimal set of directories covering the given solution project folders.
/// A monorepo solution usually spans several trees (its <c>.sln</c> folder alone is not the code),
/// so the search scope is derived from where the projects actually live.
///
/// Bottom-up merge: a directory is merged into its parent when every child directory of the parent
/// (existing on disk) also contains a project folder — the parent is then a "fully active" component
/// boundary. Merging never goes above the lowest common ancestor (LCA) of the project folders, so
/// the result never balloons into the whole monorepo.
/// </summary>
public static class SearchScopeResolver
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static IReadOnlyList<string> ComputeSearchRoots(IReadOnlyList<string> projectDirectories)
    {
        if (projectDirectories.Count == 0)
        {
            return Array.Empty<string>();
        }

        var normalized = new HashSet<string>(
            projectDirectories.Select(Path.GetFullPath),
            PathComparer);

        var lca = GetLowestCommonAncestor(normalized);
        var active = new HashSet<string>(normalized, PathComparer);

        var maxDepth = normalized.Max(CountDepth);
        var lcaDepth = CountDepth(lca);
        for (var depth = maxDepth; depth > lcaDepth; depth--)
        {
            // Snapshot: parents added by a merge at this depth are processed when the loop reaches their depth.
            // Coverage is always checked against the original project directories (not the mutating
            // active set): a directory is merged when every child of its parent holds a project folder.
            foreach (var directory in active.Where(d => CountDepth(d) == depth).ToList())
            {
                // Every active directory is at or below the LCA, so its parent is at or below the LCA too —
                // merging can never cross the LCA boundary.
                var parent = Directory.GetParent(directory);
                if (parent is null || string.IsNullOrEmpty(parent.FullName))
                {
                    continue;
                }

                string[] children;
                try
                {
                    children = Directory.GetDirectories(parent.FullName);
                }
                catch
                {
                    continue;
                }

                if (children.Length < 2)
                {
                    continue;
                }

                if (children.All(child => IsCoveredByProjectDirectory(normalized, child)))
                {
                    active.Remove(directory);
                    active.Add(parent.FullName);
                }
            }
        }

        return active.OrderBy(d => d, PathComparer).ToList();
    }

    private static bool IsCoveredByProjectDirectory(HashSet<string> projectDirectories, string directory)
    {
        foreach (var candidate in projectDirectories)
        {
            if (PathComparer.Equals(candidate, directory))
            {
                return true;
            }

            // candidate is under directory: same prefix + separator right after it.
            if (candidate.Length > directory.Length
                && candidate[directory.Length] == Path.DirectorySeparatorChar
                && candidate.StartsWith(directory, PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountDepth(string path) => path.Count(c => c == Path.DirectorySeparatorChar);

    private static string GetLowestCommonAncestor(IEnumerable<string> paths)
    {
        var firstPath = paths.First();
        var partLists = paths
            .Select(p => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        var first = partLists[0];
        var commonCount = first.Length;
        foreach (var parts in partLists.Skip(1))
        {
            var i = 0;
            while (i < commonCount && i < parts.Length && PathComparer.Equals(first[i], parts[i]))
            {
                i++;
            }

            commonCount = i;
            if (commonCount == 0)
            {
                break;
            }
        }

        var common = string.Join(Path.DirectorySeparatorChar.ToString(), first.Take(commonCount));
        if (string.IsNullOrEmpty(common))
        {
            return Path.GetPathRoot(firstPath)!;
        }

        return common.EndsWith(Path.DirectorySeparatorChar) ? common : common + Path.DirectorySeparatorChar;
    }
}
