using System.Text;
using RoslynMcpServer.Config;

namespace RoslynMcpServer.Services;

/// <summary>
/// Shared cap / overflow mechanism for the search methods. When the number of found elements exceeds the
/// cap, the full result (the same markdown format that would be returned inline) is written to a unique
/// subfolder under <c>%Temp%\roslyn-mcp\</c> and a short response (count + path + summary) is returned
/// instead. Nothing is silently truncated. The temporary files are not cleaned up (the OS clears %Temp%).
/// </summary>
public static class SearchOverflowHelper
{
    private const string TempRootFolderName = "roslyn-mcp";
    private const string ResultFileName = "result.md";

    /// <summary>Resolves the effective cap: tool argument &gt; config <c>max-results</c> &gt; default (50).</summary>
    public static int ResolveMaxResults(int? argument, WorkspaceConfig config) =>
        argument ?? config.MaxResults ?? WorkspaceConfig.DefaultMaxResults;

    /// <summary>
    /// Returns <paramref name="fullMarkdown"/> unchanged when <paramref name="totalCount"/> fits within
    /// <paramref name="cap"/>; otherwise writes it to a unique temp subfolder and returns a short response
    /// (count + file path + <paramref name="summaryMarkdown"/>).
    /// </summary>
    public static string CapOrWriteToTempFile(
        int totalCount,
        int cap,
        string fullMarkdown,
        string summaryMarkdown)
    {
        if (totalCount <= cap)
        {
            return fullMarkdown;
        }

        var filePath = WriteToTempFile(fullMarkdown);
        return BuildOverflowResponse(totalCount, cap, filePath, summaryMarkdown);
    }

    /// <summary>
    /// Writes <paramref name="fullMarkdown"/> to
    /// <c>%Temp%\roslyn-mcp\&lt;yyyyMMdd-HHmmss-&lt;short-guid&gt;&gt;\result.md</c> (one unique subfolder per
    /// result) and returns the file path.
    /// </summary>
    public static string WriteToTempFile(string fullMarkdown)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var shortGuid = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), TempRootFolderName, $"{stamp}-{shortGuid}");
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, ResultFileName);
        File.WriteAllText(filePath, fullMarkdown);
        return filePath;
    }

    private static string BuildOverflowResponse(
        int totalCount,
        int cap,
        string filePath,
        string summaryMarkdown)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Found **{totalCount}** element(s), exceeding the cap of {cap}.");
        sb.AppendLine();
        sb.AppendLine("Full result (same markdown format) written to:");
        sb.AppendLine();
        sb.AppendLine($"`{filePath}`");

        if (!string.IsNullOrWhiteSpace(summaryMarkdown))
        {
            sb.AppendLine();
            sb.AppendLine("Summary:");
            sb.AppendLine();
            sb.AppendLine(summaryMarkdown);
        }

        return sb.ToString().TrimEnd();
    }
}
