using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcpServer.Config;
using RoslynMcpServer.Diagnostics;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools;

public sealed class NavigationTools
{
    private const int MaxFindUsagesSourceLineChars = 400;

    private readonly SolutionManager _solutionManager;
    private readonly WorkspaceConfig _workspaceConfig;
    private readonly ILogger<NavigationTools> _logger;

    public NavigationTools(SolutionManager solutionManager, WorkspaceConfig workspaceConfig, ILogger<NavigationTools> logger)
    {
        _solutionManager = solutionManager;
        _workspaceConfig = workspaceConfig;
        _logger = logger;
    }

    [McpServerTool(Name = "find_symbol_references", Title = "Find symbol references")]
    [Description(
        "Finds all semantic references to a class, interface, or method when you know the declaring `.cs` file "
        + "(file-scoped SymbolFinder). For solution-wide search by simple name use `find_usages`. "
        + "Returns **1-based line:column** for each reference, grouped by file. "
        + "Pass 1-based `line`/`column` (on the declaration *or* a usage) to select the exact symbol — `symbolName` is then ignored. "
        + "Without a position, `symbolName` is matched against declarations in the file (class/interface/method/property/field/event/constructor); "
        + "if several declarations share the name, an error lists the candidates (FQN + line:col) — no blind first match. "
        + "CRITICAL for safe refactoring and DI registration audits. "
        + "Applies **saved** `.cs` from disk first (IDE/git/`dotnet format`); unsaved editor buffers are ignored. "
        + "By default: positions only (file:line:col), no line text; pass `preview=true` when you need the source line text. "
        + "Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public async Task<string> FindSymbolReferences(
        [Description("Path to a .cs file (same JSON key `filePath` as get_diagnostics_for_file).")]
        string filePath,
        [Description("Symbol name (class/interface/method/property/field/event/constructor). Ignored when `line`/`column` are provided.")]
        string? symbolName = null,
        [Description("1-based line of the symbol (declaration or usage); must be provided together with `column`.")]
        int? line = null,
        [Description("1-based column of the symbol (declaration or usage); must be provided together with `line`.")]
        int? column = null,
        [Description("Cap on the number of reference positions (argument > config `max-results` > default 50). When exceeded, the full result is written to a temp file.")]
        int? maxResults = null,
        [Description("When true, include the trimmed source line text at each reference.")]
        bool? preview = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return ToolTelemetry.TraceAndReturn(nameof(FindSymbolReferences), "Error: `filePath` is empty.");
            }

            var hasLine = line.HasValue;
            var hasColumn = column.HasValue;
            if (hasLine != hasColumn)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(FindSymbolReferences),
                    "Error: `line` and `column` must be provided together (both 1-based).");
            }

            if (!hasLine && string.IsNullOrWhiteSpace(symbolName))
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(FindSymbolReferences),
                    "Error: provide `symbolName`, or both 1-based `line` and `column`.");
            }

            var fullPath = _solutionManager.ResolvePathAgainstWorkspace(filePath);
            var document = await _solutionManager.FindDocumentAsync(fullPath, cancellationToken);
            if (document is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(FindSymbolReferences),
                    $"Could not resolve Roslyn document for file: `{fullPath}`.");
            }

            var solution = _solutionManager.GetCurrentSolution() ?? document.Project.Solution;
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
            if (semanticModel is null || syntaxRoot is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(FindSymbolReferences),
                    $"Could not build semantic model for file: `{fullPath}`.");
            }

            var nameForOutput = (symbolName ?? string.Empty).Trim();
            ISymbol symbol;
            if (hasLine)
            {
                var text = await document.GetTextAsync(cancellationToken);
                var (offset, positionError) = SourcePositionHelper.ToOffset(text, line!.Value, column!.Value);
                if (positionError is not null)
                {
                    return ToolTelemetry.TraceAndReturn(nameof(FindSymbolReferences), $"Error: {positionError}");
                }

                var resolvedSymbol = SourcePositionHelper.GetSymbolAtPosition(syntaxRoot, semanticModel, offset, cancellationToken);
                if (resolvedSymbol is null)
                {
                    return ToolTelemetry.TraceAndReturn(
                        nameof(FindSymbolReferences),
                        $"No symbol found at line {line!.Value}, column {column!.Value} in `{fullPath}`.");
                }

                symbol = resolvedSymbol;

                nameForOutput = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            }
            else
            {
                var matches = FindDeclarationMatches(syntaxRoot, nameForOutput);
                if (matches.Count == 0)
                {
                    return ToolTelemetry.TraceAndReturn(
                        nameof(FindSymbolReferences),
                        $"Symbol `{nameForOutput}` was not found as a declaration in `{fullPath}`.");
                }

                if (matches.Count > 1)
                {
                    return ToolTelemetry.TraceAndReturn(
                        nameof(FindSymbolReferences),
                        BuildAmbiguousDeclarationsMessage(
                            nameForOutput, fullPath, matches, semanticModel, cancellationToken));
                }

                var (declaration, variable) = matches[0];
                var resolvedSymbol = semanticModel.GetDeclaredSymbol(variable ?? declaration, cancellationToken);
                if (resolvedSymbol is null)
                {
                    return ToolTelemetry.TraceAndReturn(
                        nameof(FindSymbolReferences),
                        $"Unable to resolve declared symbol for `{nameForOutput}` in `{fullPath}`.");
                }

                symbol = resolvedSymbol;
            }

            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            var locations = references
                .SelectMany(r => r.Locations)
                .Where(l => l.Location.IsInSource)
                .OrderBy(l => l.Document.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.Location.GetLineSpan().StartLinePosition.Line)
                .ThenBy(l => l.Location.SourceSpan.Start)
                .ToList();

            if (locations.Count == 0)
            {
                return ToolTelemetry.TraceAndReturn(nameof(FindSymbolReferences), $"No usages found for `{nameForOutput}`.");
            }

            var showPreview = ResolvePreview(preview);
            var cap = ResolveMaxResults(maxResults);
            var docByPath = BuildDocumentByPathMap(solution);
            var textByDocument = new Dictionary<DocumentId, SourceText>();

            var sb = new StringBuilder();
            sb.AppendLine($"## References for `{nameForOutput}` (identifier length: {nameForOutput.Length})");
            sb.AppendLine();
            sb.AppendLine($"Found **{locations.Count}** reference location(s).");
            sb.AppendLine();

            foreach (var docGroup in locations.GroupBy(l => l.Document.FilePath ?? "(unknown file)", StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"File: {docGroup.Key}");
                sb.AppendLine();

                foreach (var refLoc in docGroup)
                {
                    await AppendReferencePositionAsync(
                        sb, refLoc.Location, showPreview, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
                }

                sb.AppendLine();
            }

            var fullMarkdown = sb.ToString().TrimEnd();
            var summary = BuildFileListSummary(locations.Select(l => l.Document.FilePath));

            return ToolTelemetry.TraceAndReturn(
                nameof(FindSymbolReferences),
                SearchOverflowHelper.CapOrWriteToTempFile(locations.Count, cap, fullMarkdown, summary));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find references for {SymbolName} in {FilePath}", symbolName, filePath);
            return ToolTelemetry.TraceAndReturn(
                nameof(FindSymbolReferences),
                WorkspaceLoadGuidance.FormatCaughtException(
                    ex,
                    $"Failed to find references for `{symbolName}`: {ex.Message}",
                    filePath));
        }
    }

    [McpServerTool(Name = "find_symbol_definition", Title = "Find symbol definitions in workspace")]
    [Description(
        "Searches the **currently loaded Roslyn solution** (semantic workspace index) for declarations whose name matches `symbolName` (case-insensitive). "
        + "Before search, **saved** `.cs` on disk (IDE save, git, `dotnet format`) are merged into that index; unsaved editor buffers are not. "
        + "For each match it returns the symbol display string, the **fully-qualified name (FQN)**, every **source** definition file path, and the **1-based** starting line and column. "
        + "A `symbolName` containing `.` is treated as a **fully-qualified name (exact match)**; if it matches no declaration, the error lists the candidate FQNs (no fallback to the simple name). "
        + "FQN does not distinguish method overloads (all overloads with the same name in the same type are reported); to select one overload use 1-based `line`/`column` in `find_symbol_references` or `rename_symbol`. "
        + "Use this when you need to know **where a C# type or member is defined** (class, interface, struct, enum, method, property, etc.). "
        + "**Do not** answer “where is it **declared**?” with plain-text search or by running grep/findstr/Select-String from a terminal over the tree—those walk `bin/`, `obj/`, and generated trees, are easy to mis-read, and can trigger access violations or lock contention. "
        + "For arbitrary text search across files, use your environment’s built-in **`grep`** tool (not `bash`/`PowerShell` pipelines). "
        + "By default: positions only (file:line:col), no line text; pass `preview=true` when you need the source line text. "
        + "For finding *usages*, use `find_usages` (solution-wide by simple name) or `find_symbol_references` when you already know the declaring `.cs` file. "
        + "Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public async Task<string> FindSymbolDefinition(
        [Description("Exact identifier of the type or member to locate (e.g. `IRunAvpCommand`).")]
        string symbolName,
        [Description("Cap on the number of source locations (argument > config `max-results` > default 50). When exceeded, the full result is written to a temp file.")]
        int? maxResults = null,
        [Description("When true, include the trimmed source line text at each location.")]
        bool? preview = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(symbolName))
            {
                return ToolTelemetry.TraceAndReturn(nameof(FindSymbolDefinition), "Error: `symbolName` is empty.");
            }

            var solution = await _solutionManager.GetCurrentSolutionAfterDiskSyncAsync(cancellationToken).ConfigureAwait(false);
            if (solution is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(FindSymbolDefinition),
                    WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(
                        "Error: No active workspace.",
                        _solutionManager.ConfiguredWorkspacePath));
            }

            var trimmedName = symbolName.Trim();
            var (symbols, fqnError) = await ResolveDeclarationsAsync(
                solution, trimmedName, SymbolFilter.Type | SymbolFilter.Member, cancellationToken).ConfigureAwait(false);
            if (fqnError is not null)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(FindSymbolDefinition),
                    _solutionManager.WithDiskSyncNotes(fqnError));
            }

            if (symbols.Count == 0)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(FindSymbolDefinition),
                    _solutionManager.WithDiskSyncNotes(
                        $"Symbol `{trimmedName}` was not found in the current solution (no matching type or member declarations)."));
            }

            var showPreview = ResolvePreview(preview);
            var cap = ResolveMaxResults(maxResults);

            // Collect every in-source location (element count for the cap); remember symbols with none.
            var entries = new List<(ISymbol Symbol, Location Location)>();
            var symbolsWithoutLocations = new List<ISymbol>();
            foreach (var symbol in symbols)
            {
                var sourceLocations = symbol.Locations
                    .Where(l => l.IsInSource && l.SourceTree?.FilePath is not null)
                    .ToList();
                if (sourceLocations.Count == 0)
                {
                    symbolsWithoutLocations.Add(symbol);
                    continue;
                }

                foreach (var location in sourceLocations)
                {
                    entries.Add((symbol, location));
                }
            }

            var totalLocations = entries.Count;
            var docByPath = BuildDocumentByPathMap(solution);
            var textByDocument = new Dictionary<DocumentId, SourceText>();

            var sb = new StringBuilder();
            sb.AppendLine($"## Definitions for `{trimmedName}` (identifier length: {trimmedName.Length})");
            sb.AppendLine();
            sb.AppendLine($"Found **{symbols.Count}** symbol(s), **{totalLocations}** source location(s).");
            sb.AppendLine();

            foreach (var (symbol, location) in entries)
            {
                await AppendDefinitionEntryAsync(
                    sb, symbol, location, showPreview, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
            }

            foreach (var symbol in symbolsWithoutLocations)
            {
                sb.AppendLine($"Symbol: {symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                sb.AppendLine($"FQN: {GetSymbolFqn(symbol)}");
                sb.AppendLine("  (no in-source locations — metadata or implicit declaration only)");
                sb.AppendLine();
            }

            var fullMarkdown = sb.ToString().TrimEnd();
            var summary = BuildFqnListSummary(symbols);

            return ToolTelemetry.TraceAndReturn(
                nameof(FindSymbolDefinition),
                _solutionManager.WithDiskSyncNotes(
                    SearchOverflowHelper.CapOrWriteToTempFile(totalLocations, cap, fullMarkdown, summary)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find definition locations for {SymbolName}", symbolName);
            return ToolTelemetry.TraceAndReturn(
                nameof(FindSymbolDefinition),
                WorkspaceLoadGuidance.FormatCaughtException(
                    ex,
                    $"Failed to find definitions for `{symbolName}`: {ex.Message}"));
        }
    }

    [McpServerTool(Name = "find_usages", Title = "Find symbol usages across solution")]
    [Description(
        "Semantically searches the **entire loaded Roslyn solution** for references and invocations of every symbol whose declared name matches `symbolName` (case-insensitive). "
        + "Applies **saved** `.cs` from disk first; unsaved editor buffers are ignored. "
        + "Returns **1-based line:column** for each reference, grouped by file. "
        + "If several declarations share the same simple name, all of them are reported (a summary table groups references by fully-qualified name); "
        + "narrow `symbolName` (or pass an FQN) to disambiguate, or use `find_symbol_definition` / `find_symbol_references` with a known file. "
        + "A `symbolName` containing `.` is treated as a **fully-qualified name (exact match)**; if it matches no declaration, the error lists the candidate FQNs (no fallback to the simple name). "
        + "FQN does not distinguish method overloads (all overloads with the same name in the same type are reported); to select one overload use 1-based `line`/`column` in `find_symbol_references` or `rename_symbol`. "
        + "By default: positions only (file:line:col), no line text; pass `preview=true` when you need the source line text. "
        + "Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public async Task<string> FindUsages(
        [Description("Declared name of the type or member whose references to find (e.g. `Guard`, `JsonExtensions`, `Format`).")]
        string symbolName,
        [Description("Cap on the total number of reference positions (argument > config `max-results` > default 50). When exceeded, the full result is written to a temp file.")]
        int? maxResults = null,
        [Description("When true, include the trimmed source line text at each reference.")]
        bool? preview = null,
        CancellationToken cancellationToken = default)
    {
        const string toolName = nameof(FindUsages);

        try
        {
            if (string.IsNullOrWhiteSpace(symbolName))
            {
                return ToolTelemetry.TraceAndReturn(toolName, "Error: `symbolName` is empty.");
            }

            var solution = await _solutionManager.GetCurrentSolutionAfterDiskSyncAsync(cancellationToken).ConfigureAwait(false);
            if (solution is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    toolName,
                    WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(
                        "Error: No active workspace.",
                        _solutionManager.ConfiguredWorkspacePath));
            }

            var trimmedName = symbolName.Trim();
            var (symbols, fqnError) = await ResolveDeclarationsAsync(
                solution, trimmedName, SymbolFilter.Type | SymbolFilter.Member, cancellationToken).ConfigureAwait(false);
            if (fqnError is not null)
            {
                return ToolTelemetry.TraceAndReturn(
                    toolName,
                    _solutionManager.WithDiskSyncNotes(fqnError));
            }

            if (symbols.Count == 0)
            {
                return ToolTelemetry.TraceAndReturn(
                    toolName,
                    _solutionManager.WithDiskSyncNotes(
                        $"No declarations named `{trimmedName}` were found in the current solution."));
            }

            var showPreview = ResolvePreview(preview);
            var cap = ResolveMaxResults(maxResults);

            // Find references for every matching declaration (no blind "primary" pick).
            var perSymbol = new List<(ISymbol Symbol, List<ReferenceLocation> Locations)>();
            foreach (var symbol in symbols)
            {
                var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken)
                    .ConfigureAwait(false);
                var locations = references
                    .SelectMany(r => r.Locations)
                    .Where(l => l.Location.IsInSource && l.Document.FilePath is not null)
                    .OrderBy(l => l.Document.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(l => l.Location.GetLineSpan().StartLinePosition.Line)
                    .ThenBy(l => l.Location.SourceSpan.Start)
                    .ToList();
                perSymbol.Add((symbol, locations));
            }

            var totalPositions = perSymbol.Sum(p => p.Locations.Count);
            var docByPath = BuildDocumentByPathMap(solution);
            var textByDocument = new Dictionary<DocumentId, SourceText>();

            var sb = new StringBuilder();
            sb.AppendLine($"## Usages for `{trimmedName}` (identifier length: {trimmedName.Length})");
            sb.AppendLine();

            string summary;
            if (perSymbol.Count == 1)
            {
                var (symbol, locations) = perSymbol[0];
                sb.AppendLine($"**Symbol:** `{symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}`");
                sb.AppendLine($"`{GetSymbolFqn(symbol)}`");
                sb.AppendLine();

                if (locations.Count == 0)
                {
                    sb.AppendLine("No in-source references were returned for this symbol.");
                }
                else
                {
                    sb.AppendLine($"Found **{locations.Count}** reference location(s).");
                    sb.AppendLine();
                    await AppendPositionGroupsAsync(
                        sb, locations, showPreview, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
                }

                summary = BuildFileListSummary(locations.Select(l => l.Document.FilePath));
            }
            else
            {
                var ordered = perSymbol
                    .OrderBy(p => GetSymbolFqn(p.Symbol), StringComparer.Ordinal)
                    .ToList();

                sb.AppendLine($"{ordered.Count} declaration(s) match this name:");
                sb.AppendLine();
                sb.AppendLine("| FQN | References | First |");
                sb.AppendLine("| --- | --- | --- |");
                foreach (var (symbol, locations) in ordered)
                {
                    string firstPosition;
                    if (locations.Count == 0)
                    {
                        firstPosition = "—";
                    }
                    else
                    {
                        var firstPos = locations[0].Location.GetLineSpan().StartLinePosition;
                        firstPosition = $"{firstPos.Line + 1}:{firstPos.Character + 1}";
                    }

                    sb.AppendLine($"| {GetSymbolFqn(symbol)} | {locations.Count} | {firstPosition} |");
                }

                sb.AppendLine();
                foreach (var (symbol, locations) in ordered)
                {
                    sb.AppendLine($"### `{GetSymbolFqn(symbol)}`");
                    sb.AppendLine();
                    if (locations.Count == 0)
                    {
                        sb.AppendLine("(no in-source references)");
                        sb.AppendLine();
                    }
                    else
                    {
                        await AppendPositionGroupsAsync(
                            sb, locations, showPreview, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
                    }
                }

                summary = string.Join(
                    Environment.NewLine,
                    ordered.Select(p => $"- {GetSymbolFqn(p.Symbol)}: {p.Locations.Count}"));
            }

            var fullMarkdown = sb.ToString().TrimEnd();

            return ToolTelemetry.TraceAndReturn(
                toolName,
                _solutionManager.WithDiskSyncNotes(
                    SearchOverflowHelper.CapOrWriteToTempFile(totalPositions, cap, fullMarkdown, summary)));
        }
        catch (OperationCanceledException)
        {
            return ToolTelemetry.TraceAndReturn(toolName, "`find_usages` was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindUsages failed for {SymbolName}", symbolName);
            return ToolTelemetry.TraceAndReturn(
                toolName,
                WorkspaceLoadGuidance.FormatCaughtException(
                    ex,
                    $"Failed to find usages for `{symbolName}`: {ex.Message}"));
        }
    }

    [McpServerTool(Name = "find_implementations", Title = "Find interface implementations or derived types")]
    [Description(
        "Semantically finds all types that **implement** an interface or **derive from** a base class/struct in the loaded solution. "
        + "Applies **saved** `.cs` from disk first; unsaved editor buffers are ignored. "
        + "Use for questions like \"which classes implement `IRepository`?\" or \"what inherits from `BaseController`?\". "
        + "Do not use text search or `find_usages` for this — they miss indirect hierarchies and match unrelated identifiers. "
        + "For interface symbols uses Roslyn FindImplementations; for classes/structs uses FindDerivedClasses. "
        + "A `symbolName` containing `.` is treated as a **fully-qualified name (exact match)**; if it matches no declaration, the error lists the candidate FQNs (no fallback to the simple name). "
        + "Each result is reported as `path:line:col`. "
        + "By default: positions only (path:line:col), no line text; pass `preview=true` when you need the source line text. "
        + "Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public async Task<string> FindImplementations(
        [Description("Name of the interface or base class (e.g. `IRepository`, `BaseController`). Case-insensitive.")]
        string symbolName,
        [Description("When true (default), includes indirect implementations / derived types (transitive hierarchy).")]
        bool transitive = true,
        [Description("Cap on the total number of found types (argument > config `max-results` > default 50). When exceeded, the full result is written to a temp file.")]
        int? maxResults = null,
        [Description("When true, include the trimmed source line text at each type location.")]
        bool? preview = null,
        CancellationToken cancellationToken = default)
    {
        const string toolName = nameof(FindImplementations);

        try
        {
            if (string.IsNullOrWhiteSpace(symbolName))
            {
                return ToolTelemetry.TraceAndReturn(toolName, "Error: `symbolName` is empty.");
            }

            var solution = await _solutionManager.GetCurrentSolutionAfterDiskSyncAsync(cancellationToken).ConfigureAwait(false);
            if (solution is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    toolName,
                    WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(
                        "Error: No active workspace.",
                        _solutionManager.ConfiguredWorkspacePath));
            }

            var trimmedName = symbolName.Trim();
            var (symbolMatches, fqnError) = await ResolveDeclarationsAsync(
                solution, trimmedName, SymbolFilter.Type, cancellationToken).ConfigureAwait(false);
            if (fqnError is not null)
            {
                return ToolTelemetry.TraceAndReturn(toolName, _solutionManager.WithDiskSyncNotes(fqnError));
            }

            var typeSymbols = symbolMatches
                .OfType<INamedTypeSymbol>()
                .Distinct(SymbolEqualityComparer.Default)
                .Cast<INamedTypeSymbol>()
                .ToList();
            if (typeSymbols.Count == 0)
            {
                return ToolTelemetry.TraceAndReturn(
                    toolName,
                    _solutionManager.WithDiskSyncNotes(
                        $"No type declaration named `{trimmedName}` was found in the current solution."));
            }

            var showPreview = ResolvePreview(preview);
            var cap = ResolveMaxResults(maxResults);

            // Find implementations/derived types for every matching base type (no blind "primary" pick).
            var perBase = new List<(INamedTypeSymbol Base, List<INamedTypeSymbol> Types, string SearchMode)>();
            foreach (var baseType in typeSymbols)
            {
                List<INamedTypeSymbol> related;
                string searchMode;
                switch (baseType.TypeKind)
                {
                    case TypeKind.Interface:
                        related = (await SymbolFinder.FindImplementationsAsync(
                                      baseType, solution, transitive, projects: null, cancellationToken).ConfigureAwait(false))
                            .Distinct(SymbolEqualityComparer.Default)
                            .Cast<INamedTypeSymbol>()
                            .OrderBy(t => GetSymbolFqn(t), StringComparer.Ordinal)
                            .ToList();
                        searchMode = transitive ? "implementations (transitive)" : "direct implementations";
                        break;
                    case TypeKind.Class:
                    case TypeKind.Struct:
                        related = (await SymbolFinder.FindDerivedClassesAsync(
                                      baseType, solution, transitive, projects: null, cancellationToken).ConfigureAwait(false))
                            .Distinct(SymbolEqualityComparer.Default)
                            .Cast<INamedTypeSymbol>()
                            .OrderBy(t => GetSymbolFqn(t), StringComparer.Ordinal)
                            .ToList();
                        searchMode = transitive ? "derived types (transitive)" : "direct derived types";
                        break;
                    default:
                        return ToolTelemetry.TraceAndReturn(
                            toolName,
                            $"Symbol `{trimmedName}` is a {baseType.TypeKind}; only interfaces, classes, and structs are supported.");
                }

                perBase.Add((baseType, related, searchMode));
            }

            var totalTypes = perBase.Sum(p => p.Types.Count);
            var docByPath = BuildDocumentByPathMap(solution);
            var textByDocument = new Dictionary<DocumentId, SourceText>();

            var sb = new StringBuilder();
            var headerModes = perBase.Select(p => p.SearchMode).Distinct().ToList();
            var headerMode = headerModes.Count == 1 ? headerModes[0] : "implementations / derived types";
            sb.AppendLine($"## {headerMode} for `{trimmedName}` (identifier length: {trimmedName.Length})");
            sb.AppendLine();

            string summary;
            if (perBase.Count == 1)
            {
                var (baseType, types, searchMode) = perBase[0];
                sb.AppendLine($"**Base symbol:** `{baseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}` ({GetTypeKindLabel(baseType)})");
                sb.AppendLine($"`{GetSymbolFqn(baseType)}`");
                sb.AppendLine();
                sb.AppendLine($"Found **{types.Count}** type(s) ({searchMode}).");
                sb.AppendLine();

                var index = 1;
                foreach (var type in types)
                {
                    await AppendImplementationEntryAsync(
                        sb, index, type, showPreview, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
                    index++;
                }

                summary = BuildFileListSummary(types.Select(t => GetFirstSourceLocation(t)?.SourceTree?.FilePath));
            }
            else
            {
                var ordered = perBase
                    .OrderBy(p => GetSymbolFqn(p.Base), StringComparer.Ordinal)
                    .ToList();

                sb.AppendLine($"{ordered.Count} base type(s) match this name:");
                sb.AppendLine();
                sb.AppendLine("| FQN | Kind | Types |");
                sb.AppendLine("| --- | --- | --- |");
                foreach (var (baseType, types, _) in ordered)
                {
                    sb.AppendLine($"| {GetSymbolFqn(baseType)} | {GetTypeKindLabel(baseType)} | {types.Count} |");
                }

                sb.AppendLine();
                foreach (var (baseType, types, searchMode) in ordered)
                {
                    sb.AppendLine($"### `{GetSymbolFqn(baseType)}` ({GetTypeKindLabel(baseType)})");
                    sb.AppendLine();
                    if (types.Count == 0)
                    {
                        sb.AppendLine($"No {searchMode} were found.");
                        sb.AppendLine();
                        continue;
                    }

                    var index = 1;
                    foreach (var type in types)
                    {
                        await AppendImplementationEntryAsync(
                            sb, index, type, showPreview, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
                        index++;
                    }
                }

                summary = string.Join(
                    Environment.NewLine,
                    ordered.Select(p => $"- {GetSymbolFqn(p.Base)}: {p.Types.Count}"));
            }

            return ToolTelemetry.TraceAndReturn(
                toolName,
                SearchOverflowHelper.CapOrWriteToTempFile(totalTypes, cap, sb.ToString().TrimEnd(), summary));
        }
        catch (OperationCanceledException)
        {
            return ToolTelemetry.TraceAndReturn(toolName, "`find_implementations` was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindImplementations failed for {SymbolName}", symbolName);
            return ToolTelemetry.TraceAndReturn(
                toolName,
                WorkspaceLoadGuidance.FormatCaughtException(
                    ex,
                    $"Failed to find implementations for `{symbolName}`: {ex.Message}"));
        }
    }

    private int ResolveMaxResults(int? maxResults) =>
        SearchOverflowHelper.ResolveMaxResults(maxResults, _workspaceConfig);

    private bool ResolvePreview(bool? preview) =>
        preview ?? _workspaceConfig.Preview;

    private static Dictionary<string, Document> BuildDocumentByPathMap(Solution solution)
    {
        var map = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (!string.IsNullOrEmpty(document.FilePath) && !map.ContainsKey(document.FilePath))
                {
                    map[document.FilePath] = document;
                }
            }
        }

        return map;
    }

    private static async Task<List<ISymbol>> FindDeclarationsByNameAsync(
        Solution solution,
        string trimmedName,
        SymbolFilter filter,
        CancellationToken cancellationToken)
    {
        var declarations = new List<ISymbol>();
        foreach (var projectId in solution.ProjectIds)
        {
            var project = solution.GetProject(projectId);
            if (project is null)
            {
                continue;
            }

            var found = await SymbolFinder.FindDeclarationsAsync(
                project,
                trimmedName,
                ignoreCase: true,
                filter,
                cancellationToken).ConfigureAwait(false);
            declarations.AddRange(found);
        }

        return declarations;
    }

    private const string GlobalNamespacePrefix = "global::";

    /// <summary>
    /// Computes the fully-qualified name used for display, grouping and FQN matching.
    /// For types — <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> (with <c>global::</c>);
    /// for members (method/property/field/event) — the FQN of the declaring type (recursively, including nested
    /// types) + <c>.</c> + the member name, without signature/parameters (e.g. <c>global::Mcp.Tools.CodeBaseInfo.FastGlobGrep</c>).
    /// In Roslyn 5.9 <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> omits the declaring type for members,
    /// so two same-named members in different classes would produce identical FQNs.
    /// </summary>
    internal static string GetSymbolFqn(ISymbol symbol)
    {
        if (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
            && symbol.ContainingType is { } containingType)
        {
            return GetSymbolFqn(containingType) + "." + symbol.Name;
        }

        return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    /// <summary>
    /// Strips the leading <c>global::</c> prefix so both FQN forms (with and without the prefix) compare equal.
    /// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> prints the prefix for top-level types, but users
    /// typically type FQNs without it.
    /// </summary>
    internal static string NormalizeFqn(string fqn) =>
        fqn.StartsWith(GlobalNamespacePrefix, StringComparison.Ordinal)
            ? fqn[GlobalNamespacePrefix.Length..]
            : fqn;

    /// <summary>
    /// Resolves declarations for <paramref name="requestedName"/>. A name containing a dot is treated as a
    /// fully-qualified name (exact match): declarations are searched by the last segment (simple name) and filtered
    /// to symbols whose <see cref="GetSymbolFqn"/> equals the requested name (Ordinal,
    /// after stripping a leading <c>global::</c> from both sides).
    /// When the FQN matches nothing, returns an empty list and an error listing the candidate FQNs — no silent
    /// fallback to the simple name. Otherwise returns all declarations unchanged.
    /// Note: <see cref="GetSymbolFqn"/> does not distinguish method overloads — all overloads with the same name
    /// in the same type produce the same FQN and are all reported; overload selection is by 1-based
    /// <c>line</c>/<c>column</c> (<c>find_symbol_references</c> / <c>rename_symbol</c>).
    /// </summary>
    internal static async Task<(List<ISymbol> Matches, string? Error)> ResolveDeclarationsAsync(
        Solution solution,
        string requestedName,
        SymbolFilter filter,
        CancellationToken cancellationToken)
    {
        var dotIndex = requestedName.LastIndexOf('.');
        var searchName = dotIndex >= 0 ? requestedName[(dotIndex + 1)..] : requestedName;

        var declarations = await FindDeclarationsByNameAsync(solution, searchName, filter, cancellationToken).ConfigureAwait(false);
        var symbols = declarations.Distinct(SymbolEqualityComparer.Default).ToList();
        if (dotIndex < 0)
        {
            return (symbols, null);
        }

        var normalizedRequested = NormalizeFqn(requestedName);
        var matches = symbols
            .Where(s => string.Equals(
                NormalizeFqn(GetSymbolFqn(s)),
                normalizedRequested,
                StringComparison.Ordinal))
            .ToList();
        if (matches.Count > 0)
        {
            return (matches, null);
        }

        var candidateFqns = symbols
            .Select(GetSymbolFqn)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal);
        var candidates = string.Join(", ", candidateFqns.Select(f => $"`{f}`"));
        var error = candidates.Length > 0
            ? $"FQN `{requestedName}` was not found in the current solution. "
              + $"{symbols.Count} declaration(s) with the simple name `{searchName}`: {candidates}"
            : $"FQN `{requestedName}` was not found in the current solution (no declarations with the simple name `{searchName}` either).";
        return (new List<ISymbol>(), error);
    }

    private static async Task AppendPositionGroupsAsync(
        StringBuilder sb,
        IEnumerable<ReferenceLocation> locations,
        bool preview,
        IReadOnlyDictionary<string, Document> docByPath,
        Dictionary<DocumentId, SourceText> textByDocument,
        CancellationToken cancellationToken)
    {
        foreach (var group in locations.GroupBy(l => l.Document.FilePath!, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"### `{group.Key}`");
            sb.AppendLine();
            foreach (var refLoc in group)
            {
                await AppendReferencePositionAsync(
                    sb, refLoc.Location, preview, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
            }

            sb.AppendLine();
        }
    }

    private static async Task AppendReferencePositionAsync(
        StringBuilder sb,
        Location location,
        bool preview,
        IReadOnlyDictionary<string, Document> docByPath,
        Dictionary<DocumentId, SourceText> textByDocument,
        CancellationToken cancellationToken)
    {
        var pos = location.GetLineSpan().StartLinePosition;
        if (!preview)
        {
            sb.AppendLine($"- {pos.Line + 1}:{pos.Character + 1}");
            return;
        }

        var text = await GetSourceLinePreviewAsync(location, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
        sb.AppendLine($"- {pos.Line + 1}:{pos.Character + 1} `{EscapeMdBackticks(string.IsNullOrEmpty(text) ? "(source line unavailable)" : text)}`");
    }

    private static async Task AppendDefinitionEntryAsync(
        StringBuilder sb,
        ISymbol symbol,
        Location location,
        bool preview,
        IReadOnlyDictionary<string, Document> docByPath,
        Dictionary<DocumentId, SourceText> textByDocument,
        CancellationToken cancellationToken)
    {
        var pos = location.GetLineSpan().StartLinePosition;
        sb.AppendLine($"Symbol: {symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
        sb.AppendLine($"FQN: {GetSymbolFqn(symbol)}");
        sb.AppendLine($"  File: {location.SourceTree!.FilePath!}");
        sb.AppendLine($"  Line: {pos.Line + 1}");
        sb.AppendLine($"  Col: {pos.Character + 1}");
        if (preview)
        {
            var text = await GetSourceLinePreviewAsync(location, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
            sb.AppendLine($"  Source: `{EscapeMdBackticks(string.IsNullOrEmpty(text) ? "(source line unavailable)" : text)}`");
        }

        sb.AppendLine();
    }

    private static async Task AppendImplementationEntryAsync(
        StringBuilder sb,
        int index,
        INamedTypeSymbol type,
        bool preview,
        IReadOnlyDictionary<string, Document> docByPath,
        Dictionary<DocumentId, SourceText> textByDocument,
        CancellationToken cancellationToken)
    {
        var display = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var location = GetFirstSourceLocation(type);
        if (location is null)
        {
            sb.AppendLine($"{index}. `{display}` — (no in-source location)");
            return;
        }

        var path = location.SourceTree!.FilePath!;
        var pos = location.GetLineSpan().StartLinePosition;
        if (!preview)
        {
            sb.AppendLine($"{index}. `{display}` — `{path}:{pos.Line + 1}:{pos.Character + 1}`");
            return;
        }

        var text = await GetSourceLinePreviewAsync(location, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
        sb.AppendLine(
            $"{index}. `{display}` — `{path}:{pos.Line + 1}:{pos.Character + 1}` "
            + $"`{EscapeMdBackticks(string.IsNullOrEmpty(text) ? "(source line unavailable)" : text)}`");
    }

    private static Location? GetFirstSourceLocation(INamedTypeSymbol type) =>
        type.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree?.FilePath is not null);

    private static async Task<string> GetSourceLinePreviewAsync(
        Location location,
        IReadOnlyDictionary<string, Document> docByPath,
        Dictionary<DocumentId, SourceText> textByDocument,
        CancellationToken cancellationToken)
    {
        if (location.SourceTree?.FilePath is not { } path || !docByPath.TryGetValue(path, out var document))
        {
            return string.Empty;
        }

        if (!textByDocument.TryGetValue(document.Id, out var text))
        {
            text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            textByDocument[document.Id] = text;
        }

        var lineIndex = location.GetLineSpan().StartLinePosition.Line;
        if (lineIndex < 0 || lineIndex >= text.Lines.Count)
        {
            return string.Empty;
        }

        return TruncateLine(text.Lines[lineIndex].ToString().Trim());
    }

    private static string TruncateLine(string raw)
    {
        return raw.Length > MaxFindUsagesSourceLineChars ? raw[..MaxFindUsagesSourceLineChars] + "…" : raw;
    }

    private static string BuildFileListSummary(IEnumerable<string?> files)
    {
        var distinct = files
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0)
        {
            return "(no in-source locations)";
        }

        return string.Join(Environment.NewLine, distinct.Select(f => $"- `{f}`"));
    }

    private static string BuildFqnListSummary(IEnumerable<ISymbol> symbols) =>
        string.Join(
            Environment.NewLine,
            symbols.Select(s => $"- {GetSymbolFqn(s)}"));

    private static string EscapeMdBackticks(string s) => s.Replace('`', '\'');

    /// <summary>
    /// Finds every declaration in <paramref name="root"/> whose name equals <paramref name="symbolName"/>
    /// (class/interface/method/property/event/constructor; a <c>FieldDeclarationSyntax</c> contributes one
    /// match per matching variable). Returns an empty list when nothing matches.
    /// </summary>
    private static List<(SyntaxNode Declaration, SyntaxNode? Variable)> FindDeclarationMatches(SyntaxNode root, string symbolName)
    {
        var matches = new List<(SyntaxNode Declaration, SyntaxNode? Variable)>();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ClassDeclarationSyntax c when string.Equals(c.Identifier.ValueText, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case InterfaceDeclarationSyntax i when string.Equals(i.Identifier.ValueText, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case MethodDeclarationSyntax m when string.Equals(m.Identifier.ValueText, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case PropertyDeclarationSyntax p when string.Equals(p.Identifier.ValueText, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case EventDeclarationSyntax e when string.Equals(e.Identifier.ValueText, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case ConstructorDeclarationSyntax k when string.Equals(k.Identifier.ValueText, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var variable in f.Declaration.Variables)
                    {
                        if (string.Equals(variable.Identifier.ValueText, symbolName, StringComparison.Ordinal))
                        {
                            matches.Add((node, variable));
                        }
                    }

                    break;
            }
        }

        return matches;
    }

    private static string BuildAmbiguousDeclarationsMessage(
        string symbolName,
        string fullPath,
        List<(SyntaxNode Declaration, SyntaxNode? Variable)> matches,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Symbol `{symbolName}` matches {matches.Count} declarations in `{fullPath}`. Provide 1-based `line`/`column` to select one:");
        sb.AppendLine();
        foreach (var (declaration, variable) in matches)
        {
            var symbol = semanticModel.GetDeclaredSymbol(variable ?? declaration, cancellationToken);
            var fqn = symbol is null
                ? "(unresolvable)"
                : GetSymbolFqn(symbol);
            var pos = declaration.SyntaxTree.GetLineSpan(declaration.Span).StartLinePosition;
            sb.AppendLine($"- {fqn} — {pos.Line + 1}:{pos.Character + 1}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string GetTypeKindLabel(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Struct => "struct",
        TypeKind.Class when type.IsRecord => "record",
        TypeKind.Class => type.IsAbstract ? "abstract class" : "class",
        _ => type.TypeKind.ToString().ToLowerInvariant()
    };

    [McpServerTool(Name = "get_call_graph", Title = "Get method call graph")]
    [Description(
        "Builds callers and callees for a method in the loaded workspace (after applying **saved** `.cs` from disk). Use for bug investigation instead of loading many method bodies. "
        + "Target the method by `className`+`methodName`, or by 1-based `line`/`column` on the method declaration or an invocation (the position wins; `className`/`methodName` are then ignored). "
        + "When the total number of nodes exceeds `maxNodes` (default 25), the full graph (same markdown) is written to a temp file and a short response (node count + path) is returned. "
        + "Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public async Task<string> GetCallGraph(
        [Description("Path to the .cs file containing the method (or the file with the `line`/`column` position).")] string filePath,
        [Description("Class name containing the method (ignored when `line`/`column` are provided).")] string? className = null,
        [Description("Method name (ignored when `line`/`column` are provided).")] string? methodName = null,
        [Description("1-based line of the method (declaration or invocation); must be provided together with `column`.")] int? line = null,
        [Description("1-based column of the method (declaration or invocation); must be provided together with `line`.")] int? column = null,
        [Description("Cap on the **total** number of nodes (callers + callees; default 25). When exceeded, the full graph (same markdown) is written to a temp file.")] int maxNodes = 25,
        [Description("When true, includes callees outside the loaded solution (e.g. BCL).")] bool includeExternalCallees = false,
        CancellationToken cancellationToken = default)
    {
        const string toolName = nameof(GetCallGraph);
        try
        {
            var hasLine = line.HasValue;
            var hasColumn = column.HasValue;
            if (hasLine != hasColumn)
            {
                return ToolTelemetry.TraceAndReturn(toolName, "Error: `line` and `column` must be provided together (both 1-based).");
            }

            if (!hasLine && (string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(methodName)))
            {
                return ToolTelemetry.TraceAndReturn(toolName, "Error: provide `className`+`methodName`, or both 1-based `line` and `column`.");
            }

            var fullPath = _solutionManager.ResolvePathAgainstWorkspace(filePath);
            var document = await _solutionManager.FindDocumentAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return ToolTelemetry.TraceAndReturn(toolName, $"Document not in workspace: `{fullPath}`. The workspace loads lazily from the config `workspace-path` (or via `reload`) — check the config or call `reload`.");
            }

            var solution = document.Project.Solution;
            var graph = hasLine
                ? await CallGraphHelper.BuildCallGraphAtPositionAsync(
                    solution, document, line!.Value, column!.Value, includeExternalCallees, cancellationToken)
                    .ConfigureAwait(false)
                : await CallGraphHelper.BuildCallGraphAsync(
                    solution, document, className!, methodName!, includeExternalCallees, cancellationToken)
                    .ConfigureAwait(false);

            var cap = Math.Clamp(maxNodes, 1, 100);
            var totalNodes = graph.Callers.Count + graph.Callees.Count;
            var summary = $"Callers: {graph.Callers.Count}, Callees: {graph.Callees.Count}";

            return ToolTelemetry.TraceAndReturn(
                toolName,
                SearchOverflowHelper.CapOrWriteToTempFile(totalNodes, cap, CallGraphHelper.FormatMarkdown(graph), summary));
        }
        catch (OperationCanceledException)
        {
            return ToolTelemetry.TraceAndReturn(toolName, "`get_call_graph` was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCallGraph failed for {ClassName}.{MethodName}", className, methodName);
            return ToolTelemetry.TraceAndReturn(
                toolName,
                WorkspaceLoadGuidance.FormatCaughtException(ex, $"Failed: {ex.Message}"));
        }
    }
}
