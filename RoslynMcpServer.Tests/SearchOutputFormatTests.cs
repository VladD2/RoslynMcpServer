using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Diagnostics;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Output format of the four search tools (plan §3.1 / §4.1): 1-based <c>line:col</c> is always present
/// (independent of <c>preview</c>), the searched identifier's length is printed once in the header, and
/// <c>preview</c> only toggles the source-line text.
/// </summary>
public sealed class SearchOutputFormatTests
{
    // find_symbol_definition: `Guard` identifier at line 3, column 18 (1-based).
    private const string DefinitionSource = """
        namespace Ns1
        {
            public class Guard
            {
                public void Run() { }
            }
        }
        """;

    // find_usages: single declaration `Widget`, one usage at 12:25 with a distinctive line for the preview check.
    private const string UsagesSource = """
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
                    var w = new Widget();
                    w.Spin();
                }
            }
        }
        """;

    // find_implementations: `IGuard` implemented by `GuardImpl` (identifier at 8:18).
    private const string ImplementationsSource = """
        namespace Ns1
        {
            public interface IGuard
            {
                void Check();
            }

            public class GuardImpl : IGuard
            {
                public void Check() { }
            }
        }
        """;

    // find_symbol_references: `Target` referenced at 12:25 and 13:25.
    private const string ReferencesSource = """
        namespace RefNs
        {
            public class Target
            {
                public void Work() { }
            }

            public class Caller
            {
                public void Invoke()
                {
                    var t = new Target();
                    var u = new Target();
                    t.Work();
                }
            }
        }
        """;

    [Fact]
    public async Task FindSymbolDefinition_reports_fqn_line_and_col_1_based()
    {
        using var search = AdhocSearchTool.Create(DefinitionSource);

        var result = await search.Tool.FindSymbolDefinition("Guard");

        Assert.Contains("FQN: global::Ns1.Guard", result, StringComparison.Ordinal);
        Assert.Contains("Line: 3", result, StringComparison.Ordinal);
        Assert.Contains("Col: 18", result, StringComparison.Ordinal);
        // The identifier length is printed exactly once (in the header), not per position.
        Assert.Equal(1, CountOccurrences(result, "identifier length"));
    }

    [Fact]
    public async Task FindUsages_default_has_positions_without_line_text()
    {
        using var search = AdhocSearchTool.Create(UsagesSource);

        var result = await search.Tool.FindUsages("Widget");

        // 1-based line:col position, no source line text by default.
        Assert.Contains("- 12:25", result, StringComparison.Ordinal);
        Assert.DoesNotContain("var w = new Widget", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_preview_includes_trimmed_line_text()
    {
        using var search = AdhocSearchTool.Create(UsagesSource);

        var result = await search.Tool.FindUsages("Widget", preview: true);

        // Position is still present, plus the trimmed source line text.
        Assert.Contains("- 12:25", result, StringComparison.Ordinal);
        Assert.Contains("var w = new Widget();", result, StringComparison.Ordinal);
    }

    // Same shape as <see cref="UsagesSource"/> but the usage line is padded past 400 chars to exercise truncation.
    private const string LongLineTemplate = """
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
                    var w = new Widget(); // __LONG_COMMENT__
                }
            }
        }
        """;

    [Fact]
    public async Task FindUsages_preview_truncates_lines_longer_than_400_chars()
    {
        var source = LongLineTemplate.Replace("__LONG_COMMENT__", new string('x', 400));

        using var search = AdhocSearchTool.Create(source);

        var result = await search.Tool.FindUsages("Widget", preview: true);

        var previewText = ExtractPreviewText(result, "- 12:25");
        Assert.True(previewText.Length <= 401, $"preview text exceeds the 400-char cap: {previewText.Length}");
        Assert.EndsWith("…", previewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbolReferences_reports_file_and_positions_without_inside()
    {
        var workspace = await RealWorkspaceSearchTool.CreateAsync(ReferencesSource);
        try
        {
            var result = await workspace.Tool.FindSymbolReferences(workspace.SourcePath, symbolName: "Target");

            Assert.Contains("File:", result, StringComparison.Ordinal);
            Assert.Contains("- 12:25", result, StringComparison.Ordinal);
            Assert.Contains("- 13:25", result, StringComparison.Ordinal);
            Assert.DoesNotContain("Inside:", result, StringComparison.Ordinal);
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task FindImplementations_reports_path_line_col()
    {
        using var search = AdhocSearchTool.Create(ImplementationsSource);

        var result = await search.Tool.FindImplementations("IGuard");

        Assert.Contains("GuardImpl", result, StringComparison.Ordinal);
        // Each result is `path:line:col` (1-based).
        Assert.Contains(":8:18`", result, StringComparison.Ordinal);
    }

    /// <summary>Extracts the backtick-quoted preview text that follows a <c>- N:COL</c> position marker.</summary>
    private static string ExtractPreviewText(string result, string positionPrefix)
    {
        var positionIndex = result.IndexOf(positionPrefix, StringComparison.Ordinal);
        Assert.True(positionIndex >= 0, $"expected '{positionPrefix}' in result:\n{result}");
        var open = result.IndexOf('`', positionIndex + positionPrefix.Length);
        Assert.True(open >= 0, $"expected a backtick-quoted preview after '{positionPrefix}':\n{result}");
        var close = result.IndexOf('`', open + 1);
        Assert.True(close > open, $"expected a closing backtick for the preview:\n{result}");
        return result[(open + 1)..close];
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>
    /// A real <c>MSBuildWorkspace</c> is required for the file-scoped <c>find_symbol_references</c>
    /// (<c>SolutionManager.FindDocumentAsync</c> only resolves documents from the loaded workspace). The
    /// in-memory <c>AdhocWorkspace</c> pattern cannot be used here, so a minimal project is loaded via the
    /// production walk-up path. MSBuild is registered once per process through <see cref="MsBuildBootstrapper"/>
    /// (the same entry point <c>Program.cs</c> uses at startup).
    /// </summary>
    private sealed class RealWorkspaceSearchTool
    {
        public SolutionManager Manager { get; }

        public NavigationTools Tool { get; }

        public string Root { get; }

        public string SourcePath { get; }

        private RealWorkspaceSearchTool(SolutionManager manager, NavigationTools tool, string root, string sourcePath)
        {
            Manager = manager;
            Tool = tool;
            Root = root;
            SourcePath = sourcePath;
        }

        public static async Task<RealWorkspaceSearchTool> CreateAsync(string source, string fileName = "A.cs")
        {
            EnsureMsBuildRegistered();

            var root = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "T.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            var sourcePath = Path.Combine(root, fileName);
            File.WriteAllText(sourcePath, source);

            var config = new WorkspaceConfig(new ConfigurationBuilder().Build());
            var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
            await manager.LoadAsync(Path.Combine(root, "T.csproj"));
            var tool = new NavigationTools(manager, config, NullLogger<NavigationTools>.Instance);
            return new RealWorkspaceSearchTool(manager, tool, root, sourcePath);
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.ClearWorkspaceAsync();
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        private static void EnsureMsBuildRegistered()
        {
            MsBuildTestRegistration.EnsureRegistered();
        }
    }
}
