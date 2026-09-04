using Microsoft.CodeAnalysis.Text;
using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// F7: <see cref="SourcePositionHelper.ToOffset"/> validates the 1-based <c>column</c> against the visible
/// line length (an explicit error is preferred over a silent clamp — a position past the end of the line is
/// not a valid LSP position). A column at the end of the line (length + 1) is a valid offset.
/// </summary>
public sealed class SourcePositionHelperTests
{
    [Fact]
    public void ToOffset_column_past_end_of_line_returns_error()
    {
        var text = SourceText.From("abc\ndef\n");

        // Line 1 is "abc" (3 characters); column 5 is past the end.
        var (offset, error) = SourcePositionHelper.ToOffset(text, line: 1, column: 5);

        Assert.Equal(0, offset);
        Assert.NotNull(error);
        Assert.Contains("`column` 5 is out of range", error!, StringComparison.Ordinal);
        Assert.Contains("line 1 has 3 characters", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ToOffset_column_at_end_of_line_is_valid()
    {
        var text = SourceText.From("abc\ndef\n");

        // Line 1 is "abc" (3 characters); column 4 == length + 1 is the end-of-line position.
        var (offset, error) = SourcePositionHelper.ToOffset(text, line: 1, column: 4);

        Assert.Null(error);
        Assert.Equal(3, offset);
    }
}
