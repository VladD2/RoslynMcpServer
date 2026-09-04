using Microsoft.Extensions.Configuration;
using RoslynMcpServer.Config;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Unified cap / overflow for the search tools (plan §4.2): when the number of found elements exceeds the
/// cap (argument &gt; config <c>max-results</c> &gt; default 50), the full result is written to a unique
/// subfolder under <c>%Temp%\roslyn-mcp\</c> and a short response (count + path) is returned — nothing is
/// silently truncated. Below the cap the result is returned inline and no temp file is created.
/// Created <c>%Temp%\roslyn-mcp\&lt;subdir&gt;</c> folders are deleted in <c>finally</c>.
/// </summary>
public sealed class SearchOverflowTests
{
    // `Widget` referenced 3 times (12:25, 13:25, 14:25) — enough to exceed a cap of 2.
    private const string Source = """
        namespace Ns1
        {
            public class Widget
            {
                public void Spin() { }
            }

            public class Host
            {
                public void Go()
                {
                    var a = new Widget();
                    var b = new Widget();
                    var c = new Widget();
                    a.Spin();
                }
            }
        }
        """;

    private static string OverflowRoot => Path.Combine(Path.GetTempPath(), "roslyn-mcp");

    [Fact]
    public async Task FindUsages_over_cap_writes_full_result_to_temp_file()
    {
        string? overflowDir = null;
        using var search = AdhocSearchTool.Create(Source);
        try
        {
            var result = await search.Tool.FindUsages("Widget", maxResults: 2);

            // Short response: count + path, not the full result.
            Assert.Contains("Found **3** element(s)", result, StringComparison.Ordinal);
            Assert.Contains("exceeding the cap of 2", result, StringComparison.Ordinal);
            var filePath = ExtractOverflowFilePath(result);
            Assert.True(File.Exists(filePath), $"overflow file not found: {filePath}");
            Assert.StartsWith(OverflowRoot + Path.DirectorySeparatorChar, filePath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("result.md", filePath, StringComparison.OrdinalIgnoreCase);
            overflowDir = Path.GetDirectoryName(filePath);

            // The temp file holds the FULL result — every position, no truncation.
            var content = File.ReadAllText(filePath);
            Assert.Contains("- 12:25", content, StringComparison.Ordinal);
            Assert.Contains("- 13:25", content, StringComparison.Ordinal);
            Assert.Contains("- 14:25", content, StringComparison.Ordinal);
        }
        finally
        {
            DeleteOverflowDir(overflowDir);
        }
    }

    [Fact]
    public async Task FindUsages_over_cap_from_config_writes_full_result_to_temp_file()
    {
        var config = new WorkspaceConfig(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["max-results"] = "2",
                })
                .Build());

        string? overflowDir = null;
        using var search = AdhocSearchTool.Create(Source, config: config);
        try
        {
            // No maxResults argument — the cap comes from the config `max-results`.
            var result = await search.Tool.FindUsages("Widget");

            Assert.Contains("Found **3** element(s)", result, StringComparison.Ordinal);
            Assert.Contains("exceeding the cap of 2", result, StringComparison.Ordinal);
            var filePath = ExtractOverflowFilePath(result);
            Assert.True(File.Exists(filePath), $"overflow file not found: {filePath}");
            overflowDir = Path.GetDirectoryName(filePath);

            var content = File.ReadAllText(filePath);
            Assert.Contains("- 12:25", content, StringComparison.Ordinal);
            Assert.Contains("- 13:25", content, StringComparison.Ordinal);
            Assert.Contains("- 14:25", content, StringComparison.Ordinal);
        }
        finally
        {
            DeleteOverflowDir(overflowDir);
        }
    }

    [Fact]
    public async Task FindUsages_under_cap_returns_inline_and_creates_no_file()
    {
        using var search = AdhocSearchTool.Create(Source);
        var before = SnapshotOverflowDirs();

        var result = await search.Tool.FindUsages("Widget", maxResults: 10);

        // Inline (non-overflow) output with all positions.
        Assert.DoesNotContain("exceeding the cap", result, StringComparison.Ordinal);
        Assert.DoesNotContain("written to:", result, StringComparison.Ordinal);
        Assert.Contains("- 12:25", result, StringComparison.Ordinal);
        Assert.Contains("- 13:25", result, StringComparison.Ordinal);
        Assert.Contains("- 14:25", result, StringComparison.Ordinal);

        // No new %Temp%\roslyn-mcp subfolder was created by this call.
        var after = SnapshotOverflowDirs();
        Assert.Empty(after.Except(before));
    }

    private static string ExtractOverflowFilePath(string response)
    {
        const string marker = "written to:";
        var markerIndex = response.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"expected '{marker}' in overflow response:\n{response}");
        var rest = response[(markerIndex + marker.Length)..];
        var open = rest.IndexOf('`');
        Assert.True(open >= 0, $"expected a backtick-quoted path after '{marker}':\n{rest}");
        var close = rest.IndexOf('`', open + 1);
        Assert.True(close > open, $"expected a closing backtick for the path:\n{rest}");
        return rest[(open + 1)..close];
    }

    private static HashSet<string> SnapshotOverflowDirs()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(OverflowRoot))
        {
            foreach (var dir in Directory.GetDirectories(OverflowRoot))
            {
                dirs.Add(dir);
            }
        }

        return dirs;
    }

    private static void DeleteOverflowDir(string? dir)
    {
        if (dir is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
