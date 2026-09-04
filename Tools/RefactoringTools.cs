using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcpServer.Diagnostics;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools;

public sealed class RefactoringTools
{
    private readonly SolutionManager _solutionManager;
    private readonly ILogger<RefactoringTools> _logger;

    public RefactoringTools(SolutionManager solutionManager, ILogger<RefactoringTools> logger)
    {
        _solutionManager = solutionManager;
        _logger = logger;
    }

    [McpServerTool(Name = "extract_interface", Title = "Extract interface from class")]
    [Description(
        "Extracts a public interface from a class: generates method/property/event signatures, optionally in a new file, " +
        "and adds the interface to the class base list. Use instead of manually authoring interface boilerplate. " +
        "Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms. Applies **saved** `.cs` from disk before editing.")]
    public async Task<string> ExtractInterface(
        [Description("Absolute or workspace-relative path to the .cs file containing the class.")] string filePath,
        [Description("Name of the class to extract from (e.g. `OrderService`).")] string className,
        [Description("Interface name (default: `I` + className, e.g. `IOrderService`).")] string? interfaceName = null,
        [Description("When true (default), writes the interface to `{InterfaceName}.cs` in the same folder.")] bool createNewFile = true,
        [Description("When true, returns a preview without writing files.")] bool previewOnly = false,
        CancellationToken cancellationToken = default)
    {
        const string toolName = nameof(ExtractInterface);

        try
        {
            var resolvedPath = _solutionManager.ResolvePathAgainstWorkspace(filePath);
            var document = await ResolveDocumentAsync(filePath, cancellationToken);
            if (document is null)
            {
                return ToolTelemetry.TraceAndReturn(toolName, $"Document was not found in the active workspace: `{resolvedPath}`.");
            }

            var baseSolution = document.Project.Solution;
            var (newSolution, preview) = await StructuralRefactoringHelper.ExtractInterfaceAsync(
                document, className, interfaceName, createNewFile, cancellationToken);

            if (previewOnly)
            {
                return ToolTelemetry.TraceAndReturn(toolName, "Preview only — no files written." + Environment.NewLine + StructuralRefactoringHelper.FormatPreview(preview));
            }

            var writtenPaths = await _solutionManager.ApplySolutionChangesToDiskAsync(baseSolution, newSolution, cancellationToken);
            return ToolTelemetry.TraceAndReturn(
                toolName,
                $"Extract interface applied. Files touched: {writtenPaths.Count}{Environment.NewLine}{StructuralRefactoringHelper.FormatPreview(preview)}");
        }
        catch (OperationCanceledException)
        {
            return ToolTelemetry.TraceAndReturn(toolName, "extract_interface was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractInterface failed for {ClassName} in {FilePath}", className, filePath);
            return ToolTelemetry.TraceAndReturn(toolName, $"Failed to extract interface: {ex.Message}");
        }
    }

    private async Task<Microsoft.CodeAnalysis.Document?> ResolveDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("filePath is empty.");
        }

        var fullPath = _solutionManager.ResolvePathAgainstWorkspace(filePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Path must point to a .cs file: `{fullPath}`.");
        }

        return await _solutionManager.FindDocumentAsync(fullPath, cancellationToken);
    }
}
