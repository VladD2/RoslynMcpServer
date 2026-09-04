using System.Text;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// v1.3.1: the diagnostics list in load responses is capped at
/// <see cref="WorkspaceTools.LoadWorkspaceResponseBuilder.MaxDiagnosticsInResponse"/> lines (a mixed C++/C#
/// solution can produce 1000+ diagnostics, ~80k tokens); the hidden remainder is reported by one counter
/// line — the full list stays in the server log.
/// </summary>
public sealed class WorkspaceToolsDiagnosticsCapTests
{
    [Fact]
    public void AppendDiagnosticsList_caps_at_max_with_counter_line()
    {
        var diagnostics = Enumerable.Range(0, 45)
            .Select(i => $"Failure: diagnostic {i}")
            .ToList();

        var sb = new StringBuilder();
        WorkspaceTools.LoadWorkspaceResponseBuilder.AppendDiagnosticsList(sb, diagnostics);

        var lines = ToLines(sb.ToString());
        Assert.Equal(WorkspaceTools.LoadWorkspaceResponseBuilder.MaxDiagnosticsInResponse + 1, lines.Count);
        Assert.Equal("- Failure: diagnostic 0", lines[0]);
        Assert.Equal(
            "- Failure: diagnostic 19",
            lines[WorkspaceTools.LoadWorkspaceResponseBuilder.MaxDiagnosticsInResponse - 1]);
        Assert.Equal("- ... and 25 more diagnostic(s) (see server log)", lines[^1]);
    }

    [Fact]
    public void AppendDiagnosticsList_exactly_at_cap_has_no_counter_line()
    {
        var diagnostics = Enumerable.Range(0, WorkspaceTools.LoadWorkspaceResponseBuilder.MaxDiagnosticsInResponse)
            .Select(i => $"Failure: diagnostic {i}")
            .ToList();

        var sb = new StringBuilder();
        WorkspaceTools.LoadWorkspaceResponseBuilder.AppendDiagnosticsList(sb, diagnostics);

        var lines = ToLines(sb.ToString());
        Assert.Equal(WorkspaceTools.LoadWorkspaceResponseBuilder.MaxDiagnosticsInResponse, lines.Count);
        Assert.DoesNotContain(lines, l => l.Contains("more diagnostic(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void AppendDiagnosticsList_under_cap_has_no_counter_line()
    {
        var diagnostics = new List<string> { "Failure: only one" };

        var sb = new StringBuilder();
        WorkspaceTools.LoadWorkspaceResponseBuilder.AppendDiagnosticsList(sb, diagnostics);

        Assert.Equal(new[] { "- Failure: only one" }, ToLines(sb.ToString()));
    }

    private static List<string> ToLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
}
