using System.Text;

namespace RoslynMcpServer.Diagnostics;

/// <summary>
/// Offloads verbose tool output to a unique temp .md file under
/// <c>%Temp%\RoslynMcpServer\&lt;project&gt;\&lt;guid&gt;\</c>. The model sees only a concise pointer
/// (path) inline; the full content is preserved on disk so nothing is lost (OS clears %Temp%).
/// </summary>
internal static class TempReportWriter
{
    private const string RootFolderName = "RoslynMcpServer";

    /// <summary>Derives a safe project name from a workspace path (solution/project file or directory).</summary>
    public static string GetProjectName(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return "unknown";
        }

        var fullPath = Path.GetFullPath(workspacePath);
        var name = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath).Name
            : Path.GetFileNameWithoutExtension(fullPath);

        return Sanitize(name);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to
    /// <c>%Temp%\RoslynMcpServer\&lt;project&gt;\&lt;guid&gt;\&lt;fileName&gt;</c> and returns the file path.
    /// </summary>
    public static string WriteToTempFile(string content, string projectName, string fileName)
    {
        var project = Sanitize(projectName);
        var guid = Guid.NewGuid().ToString("N")[..12];
        var dir = Path.Combine(Path.GetTempPath(), RootFolderName, project, guid);
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, fileName);
        File.WriteAllText(filePath, content);
        return filePath;
    }

    /// <summary>
    /// Dumps <paramref name="content"/> to a temp file and appends a one-line pointer (<c>label: `path`</c>)
    /// to <paramref name="sb"/>. No-op when the content is empty.
    /// </summary>
    public static void AppendPointer(StringBuilder sb, string content, string projectName, string fileName, string label)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var path = WriteToTempFile(content, projectName, fileName);
        sb.AppendLine();
        sb.AppendLine($"{label}: `{path}`");
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        var s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? "unknown" : s;
    }
}
