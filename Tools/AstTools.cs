using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcpServer.Diagnostics;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools;

public sealed class AstTools
{
    private readonly SolutionManager _solutionManager;
    private readonly ILogger<AstTools> _logger;

    public AstTools(SolutionManager solutionManager, ILogger<AstTools> logger)
    {
        _solutionManager = solutionManager;
        _logger = logger;
    }

    [McpServerTool(Name = "organize_usings", Title = "Organize usings")]
    [Description("Sorts using directives and optionally removes unused usings via semantic analysis. Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public Task<string> OrganizeUsings(
        [Description("Absolute or workspace-relative path to the `.cs` file.")]
        string filePath,
        [Description("When true (default), removes usings with no referenced symbols in the file.")]
        bool removeUnused = true,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(nameof(OrganizeUsings), filePath,
            doc => AstModificationHelper.OrganizeUsingsAsync(doc, removeUnused, cancellationToken),
            "Organized usings.",
            cancellationToken);

    [McpServerTool(Name = "add_member", Title = "Add member to class")]
    [Description(
        "Inserts a parsed member declaration (method, property, or field) into a class at the correct position via Roslyn DocumentEditor. "
        + "Requires workspace (config `workspace-path`, loaded lazily).")]
    public Task<string> AddMember(
        [Description("Absolute or workspace-relative path to the `.cs` file.")]
        string filePath,
        [Description("Target class name (simple name).")]
        string className,
        [Description(
            "Full member declaration source: method (`public void Foo() { }`), property (`public string Name { get; set; }`), "
            + "or field (`private readonly ILogger _log;`). Events, constructors, and nested types are not supported.")]
        string memberSource,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(nameof(AddMember), filePath,
            doc => AstModificationHelper.AddMemberAsync(doc, className, memberSource, cancellationToken),
            $"Added member to class `{className}`.",
            cancellationToken);

    [McpServerTool(Name = "update_method_body", Title = "Update method body")]
    [Description("Replaces a method body via Roslyn AST with syntax validation before write. Prefer this over host edit/write for body-only edits (use host tools for full-file reads/writes). Pair with get_method_body. Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public Task<string> UpdateMethodBody(
        [Description("Absolute or workspace-relative path to the `.cs` file.")]
        string filePath,
        [Description("Class containing the method.")]
        string className,
        [Description("Method name to update.")]
        string methodName,
        [Description("New method body: statements only, or a full `{ ... }` block.")]
        string newBody,
        [Description("Parameter type names to disambiguate overloads, e.g. [\"string\", \"int\"]. Required when overloads exist.")]
        string[]? parameterTypes = null,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(nameof(UpdateMethodBody), filePath,
            doc => AstModificationHelper.UpdateMethodBodyAsync(
                doc, className, methodName, newBody, parameterTypes, cancellationToken),
            $"Updated body of `{className}.{methodName}`.",
            cancellationToken);

    [McpServerTool(Name = "implement_interface", Title = "Implement interface stubs")]
    [Description("Adds interface to class base list and generates NotImplemented stubs for missing members. Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public Task<string> ImplementInterface(
        [Description("Absolute or workspace-relative path to the `.cs` file.")]
        string filePath,
        [Description("Target class name (simple name).")]
        string className,
        [Description("Interface name to implement, e.g. `IDisposable`.")]
        string interfaceName,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(nameof(ImplementInterface), filePath,
            doc => InterfaceImplementationHelper.ImplementInterfaceAsync(doc, className, interfaceName, cancellationToken),
            $"Implemented interface `{interfaceName}` on class `{className}`.",
            cancellationToken);

    private async Task<string> ApplyAsync(
        string toolName,
        string filePath,
        Func<Microsoft.CodeAnalysis.Document, Task<Microsoft.CodeAnalysis.Document>> mutate,
        string successMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            var (document, baseSolution) = await ResolveDocumentAsync(filePath, cancellationToken);
            var newDocument = await mutate(document).ConfigureAwait(false);
            var writtenPaths = await _solutionManager.ApplySolutionChangesToDiskAsync(
                baseSolution, newDocument.Project.Solution, cancellationToken).ConfigureAwait(false);

            return ToolTelemetry.TraceAndReturn(
                toolName,
                $"{successMessage} File: `{_solutionManager.ResolvePathAgainstWorkspace(filePath)}`. Files touched: {writtenPaths.Count}.");
        }
        catch (OperationCanceledException)
        {
            return ToolTelemetry.TraceAndReturn(toolName, $"`{toolName}` was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ToolName} failed for {FilePath}", toolName, filePath);
            return ToolTelemetry.TraceAndReturn(toolName, $"Failed: {ex.Message}");
        }
    }

    private async Task<(Microsoft.CodeAnalysis.Document Document, Microsoft.CodeAnalysis.Solution BaseSolution)> ResolveDocumentAsync(
        string filePath,
        CancellationToken cancellationToken)
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

        var document = await _solutionManager.FindDocumentAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            throw new InvalidOperationException(
                $"Document not found in workspace: `{fullPath}`. "
                + "The workspace is loaded lazily from the config `workspace-path` (or via `reload`) — check the config or call `reload`.");
        }

        return (document, document.Project.Solution);
    }
}
