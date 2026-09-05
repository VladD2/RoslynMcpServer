using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    /// <summary>
    /// Appended to a search result when the search had to be retried after re-sanitizing the workspace
    /// (an <c>UnresolvedAnalyzerReference</c> stub appeared after the last sanitization pass).
    /// </summary>
    private const string AnalyzerFallbackNote =
        "> **Note:** the search was retried after removing unresolved analyzer reference(s) from the workspace "
        + "(Roslyn 5.9.0 project checksum error `UnresolvedAnalyzerReference`); the result is complete.";

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
        + "Pass 1-based `line` (with optional `column`) on the declaration *or* a usage to select the exact symbol; when `column` is omitted it is computed from the first occurrence of `symbolName` on that line. "
        + "Without a position, `symbolName` is matched against declarations in the file (class/interface/method/property/field/event/constructor); "
        + "if several declarations share the name, an error lists the candidates (FQN + line:col) — no blind first match. "
        + "CRITICAL for safe refactoring and DI registration audits. "
        + "For a virtual/override/abstract method, Roslyn reports every call site in the whole virtual method family (any override, any receiver type): the result notes how many are virtual dispatch sites, and `directOnly: true` keeps only references whose static receiver type is the declaring type or a derived type. "
        + "Applies **saved** `.cs` from disk first (IDE/git/`dotnet format`); unsaved editor buffers are ignored. "
        + "By default: positions only (file:line:col), no line text; pass `preview=true` when you need the source line text. "
        + "Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public async Task<string> FindSymbolReferences(
        [Description("Path to a .cs file (same JSON key `filePath` as get_diagnostics_for_file).")]
        string filePath,
        [Description("Symbol name (class/interface/method/property/field/event/constructor). Used to locate the token on `line` when `column` is omitted.")]
        string? symbolName = null,
        [Description("1-based line of the symbol (declaration or usage); `column` may be omitted, in which case it is computed from `symbolName`.")]
        int? line = null,
        [Description("1-based column of the symbol (declaration or usage).")]
        int? column = null,
        [Description("Cap on the number of reference positions (argument > config `max-results` > default 50). When exceeded, the full result is written to a temp file.")]
        int? maxResults = null,
        [Description("When true, include the trimmed source line text at each reference.")]
        bool? preview = null,
        [Description("Only meaningful for virtual/override/abstract methods: when true, keeps only direct references — call sites whose static receiver type is the declaring type or a derived type (virtual dispatch sites on other types in the virtual method family are filtered out). Default false: all references, with a note counting the virtual dispatch sites.")]
        bool directOnly = false,
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
            if (hasLine && !hasColumn && string.IsNullOrWhiteSpace(symbolName))
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(FindSymbolReferences),
                    "Error: provide `symbolName` (to locate it on `line`) or pass 1-based `column`.");
            }

            if (!hasLine && string.IsNullOrWhiteSpace(symbolName))
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(FindSymbolReferences),
                    "Error: provide `symbolName`, or 1-based `line`.");
            }

            var fullPath = _solutionManager.ResolvePathAgainstWorkspace(filePath);
            var document = await _solutionManager.FindDocumentAsync(fullPath, cancellationToken);
            if (document is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(FindSymbolReferences),
                    $"Could not resolve Roslyn document for file: `{fullPath}`.");
            }

            // The analyzer-sanitized solution: an UnresolvedAnalyzerReference stub anywhere in the solution
            // makes Roslyn 5.9.0 throw on the project checksum inside SymbolFinder (see WorkspaceAnalyzerSanitizer).
            var solution = _solutionManager.GetSanitizedSolution() ?? document.Project.Solution;
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
                var (resolvedLine, resolvedColumn, resolveError) = ResolveSymbolPosition(syntaxRoot, text, line!.Value, column, nameForOutput);
                if (resolveError is not null)
                    return ToolTelemetry.TraceAndReturn(nameof(FindSymbolReferences), $"Error: {resolveError}");

                var (offset, positionError) = SourcePositionHelper.ToOffset(text, resolvedLine, resolvedColumn);
                if (positionError is not null)
                    return ToolTelemetry.TraceAndReturn(nameof(FindSymbolReferences), $"Error: {positionError}");

                var resolvedSymbol = SourcePositionHelper.GetSymbolAtPosition(syntaxRoot, semanticModel, offset, cancellationToken);
                if (resolvedSymbol is null)
                    return ToolTelemetry.TraceAndReturn(
                        nameof(FindSymbolReferences),
                        $"No symbol found at line {resolvedLine}, column {resolvedColumn} in `{fullPath}`.");

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

            var (references, analyzerRetry) = await WorkspaceAnalyzerSanitizer.WithSanitizedRetryAsync(
                sol => SymbolFinder.FindReferencesAsync(symbol, sol, cancellationToken),
                _solutionManager.GetSanitizedSolution,
                solution,
                cancellationToken);
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

            // For a virtual/override/abstract method, SymbolFinder returns every call site in the whole
            // virtual method family (any override, any receiver type) — VS 2022 "Find All References" does
            // the same. Classify the sites so `directOnly` can filter them and the default output can note
            // how many are virtual dispatch sites (see ClassifyVirtualReferencesAsync).
            var virtualMethod = GetVirtualFamilyMethod(symbol);
            var dispatchCount = 0;
            if (virtualMethod is not null)
            {
                var (isDirect, _, dispatch) = await ClassifyVirtualReferencesAsync(virtualMethod, locations, cancellationToken);
                dispatchCount = dispatch;
                if (directOnly)
                {
                    locations = locations.Where((_, i) => isDirect[i]).ToList();
                    if (locations.Count == 0)
                    {
                        return ToolTelemetry.TraceAndReturn(
                            nameof(FindSymbolReferences),
                            $"No direct references found for `{nameForOutput}`: all {dispatchCount} reference(s) are virtual dispatch sites (pass directOnly: false to include them).");
                    }
                }
            }

            var showPreview = ResolvePreview(preview);
            var cap = ResolveMaxResults(maxResults);
            var docByPath = BuildDocumentByPathMap(solution);
            var textByDocument = new Dictionary<DocumentId, SourceText>();

            var sb = new StringBuilder();
            sb.AppendLine($"## References for `{nameForOutput}` (identifier length: {nameForOutput.Length})");
            sb.AppendLine();
            if (virtualMethod is not null && directOnly && dispatchCount > 0)
            {
                sb.AppendLine($"Found **{locations.Count}** direct reference location(s) ({dispatchCount} virtual dispatch site(s) excluded).");
            }
            else
            {
                sb.AppendLine($"Found **{locations.Count}** reference location(s).");
            }
            sb.AppendLine();
            if (virtualMethod is not null && !directOnly && dispatchCount > 0)
            {
                sb.AppendLine($"> Note: symbol is virtual/override; {dispatchCount} of the {locations.Count} reference(s) are virtual dispatch sites (the receiver type is not the declaring type or a derived type). Pass `directOnly: true` to keep only direct references.");
                sb.AppendLine();
            }

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
            if (analyzerRetry)
            {
                fullMarkdown += Environment.NewLine + Environment.NewLine + AnalyzerFallbackNote;
            }

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
        "Reports where a symbol is **defined** (possibly in another file). Two modes: "
        + "without `filePath` — lists every **declaration** in the loaded solution whose name matches `symbolName` (case-insensitive; a name containing `.` is an exact FQN); "
        + "with `filePath` — resolves the symbol in that `.cs` file: pass `line` (+ optional `column`, computed from the first occurrence of `symbolName` on that line when omitted) on a usage *or* a declaration to select the exact symbol; "
        + "without a position, `symbolName` is matched against declarations in the file (class/interface/method/property/field/event/constructor); "
        + "if several declarations share the name, an error lists the candidates (FQN + line:col) — no blind first match. "
        + "For each definition it returns the symbol display string, the **fully-qualified name (FQN)**, the source file path, and the **1-based** line and column. "
        + "Applies **saved** `.cs` from disk first (IDE/git/`dotnet format`); unsaved editor buffers are ignored. "
        + "For finding *all usages/references* across the solution use `find_symbol_references` (by name or by position). "
        + "By default: positions only (file:line:col), no line text; pass `preview=true` when you need the source line text. "
        + "Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public async Task<string> FindSymbolDefinition(
        [Description("Path to a .cs file (same JSON key `filePath` as get_diagnostics_for_file). Optional: when omitted, all declarations matching `symbolName` across the whole solution are listed instead.")]
        string? filePath = null,
        [Description("Symbol name (class/interface/method/property/field/event/constructor; a name with `.` is an exact FQN). Required when `filePath` is omitted; otherwise used to locate the token on `line` when `column` is omitted.")]
        string? symbolName = null,
        [Description("1-based line of the symbol (declaration or usage); `column` may be omitted, in which case it is computed from `symbolName`. Requires `filePath`.")]
        int? line = null,
        [Description("1-based column of the symbol (declaration or usage). Requires `filePath`.")]
        int? column = null,
        [Description("Cap on the number of source locations (argument > config `max-results` > default 50). When exceeded, the full result is written to a temp file.")]
        int? maxResults = null,
        [Description("When true, include the trimmed source line text at each location.")]
        bool? preview = null,
        CancellationToken cancellationToken = default)
    {
        const string toolName = nameof(FindSymbolDefinition);

        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                if (string.IsNullOrWhiteSpace(symbolName))
                {
                    return ToolTelemetry.TraceAndReturn(toolName, "Error: provide `symbolName`.");
                }

                if (line.HasValue || column.HasValue)
                {
                    return ToolTelemetry.TraceAndReturn(
                        toolName,
                        "Error: `line`/`column` require `filePath`. For name-based search, omit them.");
                }

                return await FindDeclarationsByNameAsync(symbolName.Trim(), maxResults, preview, cancellationToken).ConfigureAwait(false);
            }

            var hasLine = line.HasValue;
            var hasColumn = column.HasValue;
            if (hasLine && !hasColumn && string.IsNullOrWhiteSpace(symbolName))
            {
                return ToolTelemetry.TraceAndReturn(toolName, "Error: provide `symbolName` (to locate it on `line`) or pass 1-based `column`.");
            }

            if (!hasLine && string.IsNullOrWhiteSpace(symbolName))
            {
                return ToolTelemetry.TraceAndReturn(toolName, "Error: provide `symbolName`, or 1-based `line`.");
            }

            var fullPath = _solutionManager.ResolvePathAgainstWorkspace(filePath);
            var document = await _solutionManager.FindDocumentAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    toolName,
                    $"Could not resolve Roslyn document for file: `{fullPath}`. The workspace loads lazily from the config `workspace-path` (or via `reload`) — check the config or call `reload`.");
            }

            var solution = document.Project.Solution;
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel is null || syntaxRoot is null)
            {
                return ToolTelemetry.TraceAndReturn(toolName, $"Could not build semantic model for file: `{fullPath}`.");
            }

            var nameForOutput = (symbolName ?? string.Empty).Trim();
            ISymbol symbol;
            if (hasLine)
            {
                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var (resolvedLine, resolvedColumn, resolveError) = ResolveSymbolPosition(syntaxRoot, text, line!.Value, column, nameForOutput);
                if (resolveError is not null)
                    return ToolTelemetry.TraceAndReturn(toolName, $"Error: {resolveError}");

                var (offset, positionError) = SourcePositionHelper.ToOffset(text, resolvedLine, resolvedColumn);
                if (positionError is not null)
                    return ToolTelemetry.TraceAndReturn(toolName, $"Error: {positionError}");

                var resolvedSymbol = SourcePositionHelper.GetSymbolAtPosition(syntaxRoot, semanticModel, offset, cancellationToken);
                if (resolvedSymbol is null)
                    return ToolTelemetry.TraceAndReturn(
                        toolName,
                        $"No symbol found at line {resolvedLine}, column {resolvedColumn} in `{fullPath}`.");

                symbol = resolvedSymbol;
                nameForOutput = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            }
            else
            {
                var matches = FindDeclarationMatches(syntaxRoot, nameForOutput);
                if (matches.Count == 0)
                {
                    return ToolTelemetry.TraceAndReturn(
                        toolName,
                        $"Symbol `{nameForOutput}` was not found as a declaration in `{fullPath}`.");
                }

                if (matches.Count > 1)
                {
                    return ToolTelemetry.TraceAndReturn(
                        toolName,
                        BuildAmbiguousDeclarationsMessage(nameForOutput, fullPath, matches, semanticModel, cancellationToken));
                }

                var (declaration, variable) = matches[0];
                var resolvedSymbol = semanticModel.GetDeclaredSymbol(variable ?? declaration, cancellationToken);
                if (resolvedSymbol is null)
                {
                    return ToolTelemetry.TraceAndReturn(
                        toolName,
                        $"Unable to resolve declared symbol for `{nameForOutput}` in `{fullPath}`.");
                }

                symbol = resolvedSymbol;
            }

            var sourceLocations = symbol.Locations
                .Where(l => l.IsInSource && l.SourceTree?.FilePath is not null)
                .ToList();

            var showPreview = ResolvePreview(preview);
            var cap = ResolveMaxResults(maxResults);
            var docByPath = BuildDocumentByPathMap(solution);
            var textByDocument = new Dictionary<DocumentId, SourceText>();

            var sb = new StringBuilder();
            sb.AppendLine($"## Definition for `{nameForOutput}` (identifier length: {nameForOutput.Length})");
            sb.AppendLine();
            sb.AppendLine(sourceLocations.Count == 0
                ? "The symbol has no in-source locations (metadata or implicit declaration only)."
                : $"Found **{sourceLocations.Count}** source location(s).");
            sb.AppendLine();

            foreach (var location in sourceLocations)
            {
                await AppendDefinitionEntryAsync(
                    sb, symbol, location, showPreview, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
            }

            var fullMarkdown = sb.ToString().TrimEnd();
            var summary = BuildFqnListSummary(new[] { symbol });

            return ToolTelemetry.TraceAndReturn(
                toolName,
                SearchOverflowHelper.CapOrWriteToTempFile(sourceLocations.Count, cap, fullMarkdown, summary));
        }
        catch (OperationCanceledException)
        {
            return ToolTelemetry.TraceAndReturn(toolName, "`find_symbol_definition` was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find definition for {SymbolName} in {FilePath}", symbolName, filePath);
            return ToolTelemetry.TraceAndReturn(
                toolName,
                WorkspaceLoadGuidance.FormatCaughtException(
                    ex,
                    $"Failed to find definition for `{symbolName}`: {ex.Message}",
                    filePath));
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
            var analyzerRetry = false;
            foreach (var symbol in symbols)
            {
                var (references, retried) = await WorkspaceAnalyzerSanitizer.WithSanitizedRetryAsync(
                    sol => SymbolFinder.FindReferencesAsync(symbol, sol, cancellationToken),
                    _solutionManager.GetSanitizedSolution,
                    solution,
                    cancellationToken).ConfigureAwait(false);
                analyzerRetry |= retried;
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
            if (analyzerRetry)
            {
                fullMarkdown += Environment.NewLine + Environment.NewLine + AnalyzerFallbackNote;
            }

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
            var analyzerRetry = false;
            foreach (var baseType in typeSymbols)
            {
                List<INamedTypeSymbol> related;
                string searchMode;
                switch (baseType.TypeKind)
                {
                    case TypeKind.Interface:
                        var (implementations, retriedImplementations) = await WorkspaceAnalyzerSanitizer.WithSanitizedRetryAsync(
                            sol => SymbolFinder.FindImplementationsAsync(baseType, sol, transitive, projects: null, cancellationToken),
                            _solutionManager.GetSanitizedSolution,
                            solution,
                            cancellationToken).ConfigureAwait(false);
                        analyzerRetry |= retriedImplementations;
                        related = implementations
                            .Distinct(SymbolEqualityComparer.Default)
                            .Cast<INamedTypeSymbol>()
                            .OrderBy(t => GetSymbolFqn(t), StringComparer.Ordinal)
                            .ToList();
                        searchMode = transitive ? "implementations (transitive)" : "direct implementations";
                        break;
                    case TypeKind.Class:
                    case TypeKind.Struct:
                        var (derived, retriedDerived) = await WorkspaceAnalyzerSanitizer.WithSanitizedRetryAsync(
                            sol => SymbolFinder.FindDerivedClassesAsync(baseType, sol, transitive, projects: null, cancellationToken),
                            _solutionManager.GetSanitizedSolution,
                            solution,
                            cancellationToken).ConfigureAwait(false);
                        analyzerRetry |= retriedDerived;
                        related = derived
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

            var fullMarkdown = sb.ToString().TrimEnd();
            if (analyzerRetry)
            {
                fullMarkdown += Environment.NewLine + Environment.NewLine + AnalyzerFallbackNote;
            }

            return ToolTelemetry.TraceAndReturn(
                toolName,
                SearchOverflowHelper.CapOrWriteToTempFile(totalTypes, cap, fullMarkdown, summary));
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

    /// <summary>
    /// Name-based mode of <c>find_symbol_definition</c> (no <c>filePath</c>): lists every declaration in the
    /// loaded solution whose name matches <paramref name="trimmedName"/> (case-insensitive; a name containing
    /// a dot is an exact FQN match via <see cref="ResolveDeclarationsAsync"/>). One declaration → the standard
    /// definition format; several → a summary table (FQN + file + line:col); none → a "no declarations" note.
    /// </summary>
    private async Task<string> FindDeclarationsByNameAsync(
        string trimmedName,
        int? maxResults,
        bool? preview,
        CancellationToken cancellationToken)
    {
        const string toolName = nameof(FindSymbolDefinition);

        var solution = await _solutionManager.GetCurrentSolutionAfterDiskSyncAsync(cancellationToken).ConfigureAwait(false);
        if (solution is null)
        {
            return ToolTelemetry.TraceAndReturn(
                toolName,
                WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(
                    "Error: No active workspace.",
                    _solutionManager.ConfiguredWorkspacePath));
        }

        var (symbols, fqnError) = await ResolveDeclarationsAsync(
            solution, trimmedName, SymbolFilter.Type | SymbolFilter.Member, cancellationToken).ConfigureAwait(false);
        if (fqnError is not null)
        {
            return ToolTelemetry.TraceAndReturn(toolName, _solutionManager.WithDiskSyncNotes(fqnError));
        }

        if (symbols.Count == 0)
        {
            return ToolTelemetry.TraceAndReturn(
                toolName,
                _solutionManager.WithDiskSyncNotes($"No declarations found for `{trimmedName}`."));
        }

        var showPreview = ResolvePreview(preview);
        var cap = ResolveMaxResults(maxResults);
        var docByPath = BuildDocumentByPathMap(solution);
        var textByDocument = new Dictionary<DocumentId, SourceText>();

        var sb = new StringBuilder();
        if (symbols.Count == 1)
        {
            var symbol = symbols[0];
            sb.AppendLine($"## Definition for `{trimmedName}` (identifier length: {trimmedName.Length})");
            sb.AppendLine();
            sb.AppendLine("Found **1** source location(s).");
            sb.AppendLine();

            var location = symbol.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree?.FilePath is not null);
            if (location is null)
            {
                sb.AppendLine("The symbol has no in-source locations (metadata or implicit declaration only).");
            }
            else
            {
                await AppendDefinitionEntryAsync(
                    sb, symbol, location, showPreview, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var ordered = symbols.OrderBy(s => GetSymbolFqn(s), StringComparer.Ordinal).ToList();
            sb.AppendLine($"## Definitions for `{trimmedName}` (identifier length: {trimmedName.Length})");
            sb.AppendLine();
            sb.AppendLine($"{ordered.Count} declaration(s) match `{trimmedName}`:");
            sb.AppendLine();
            sb.AppendLine(showPreview ? "| FQN | File | Line:Col | Source |" : "| FQN | File | Line:Col |");
            sb.AppendLine(showPreview ? "| --- | --- | --- | --- |" : "| --- | --- | --- |");
            foreach (var symbol in ordered)
            {
                var location = symbol.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree?.FilePath is not null);
                if (location is null)
                {
                    sb.AppendLine(showPreview
                        ? $"| {GetSymbolFqn(symbol)} | (no in-source location) | — | — |"
                        : $"| {GetSymbolFqn(symbol)} | (no in-source location) | — |");
                    continue;
                }

                var path = location.SourceTree!.FilePath!;
                var pos = location.GetLineSpan().StartLinePosition;
                if (!showPreview)
                {
                    sb.AppendLine($"| {GetSymbolFqn(symbol)} | {path} | {pos.Line + 1}:{pos.Character + 1} |");
                    continue;
                }

                var text = await GetSourceLinePreviewAsync(location, docByPath, textByDocument, cancellationToken).ConfigureAwait(false);
                sb.AppendLine(
                    $"| {GetSymbolFqn(symbol)} | {path} | {pos.Line + 1}:{pos.Character + 1} "
                    + $"| {EscapeMdBackticks(string.IsNullOrEmpty(text) ? "(source line unavailable)" : text)} |");
            }
        }

        var fullMarkdown = sb.ToString().TrimEnd();
        var summary = BuildFqnListSummary(symbols);

        return ToolTelemetry.TraceAndReturn(
            toolName,
            _solutionManager.WithDiskSyncNotes(
                SearchOverflowHelper.CapOrWriteToTempFile(symbols.Count, cap, fullMarkdown, summary)));
    }

    private int ResolveMaxResults(int? maxResults) =>
        SearchOverflowHelper.ResolveMaxResults(maxResults, _workspaceConfig);

    private bool ResolvePreview(bool? preview) =>
        preview ?? _workspaceConfig.Preview;

    /// <summary>
    /// The symbol when it is an instance method of a class that participates in virtual dispatch
    /// (virtual/abstract/override); otherwise null. For such methods <c>SymbolFinder.FindReferencesAsync</c>
    /// returns every call site in the virtual method family (any override, any receiver type), so
    /// <c>find_symbol_references</c> post-filters them (<c>directOnly</c>) and annotates the default output.
    /// </summary>
    private static IMethodSymbol? GetVirtualFamilyMethod(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol method || !(method.IsVirtual || method.IsOverride || method.IsAbstract))
            return null;
        if (method.ContainingType is not INamedTypeSymbol { TypeKind: TypeKind.Class })
            return null;
        return method;
    }

    /// <summary>
    /// Classifies the reference locations of a virtual/override/abstract method: a location is "direct" when
    /// its static receiver type is the declaring type or a derived type (including implicit-<c>this</c> calls
    /// inside the declaring type or a derived type); every other resolvable receiver type is a "virtual
    /// dispatch site" (a sibling override's receiver or a base-type variable). Locations whose receiver cannot
    /// be resolved (metadata, type parameters, unexpected syntax) are kept — treated as direct — so the filter
    /// never drops a reference silently. Returns a per-location flag array aligned with <paramref name="locations"/>.
    /// </summary>
    private static async Task<(bool[] IsDirect, int DirectCount, int DispatchCount)> ClassifyVirtualReferencesAsync(
        IMethodSymbol method,
        IReadOnlyList<ReferenceLocation> locations,
        CancellationToken cancellationToken)
    {
        var declaringType = method.ContainingType!;
        var isDirect = new bool[locations.Count];
        var directCount = 0;
        var rootsByDocument = new Dictionary<DocumentId, SyntaxNode>();
        var modelsByDocument = new Dictionary<DocumentId, SemanticModel>();

        for (var i = 0; i < locations.Count; i++)
        {
            var reference = locations[i];

            SyntaxNode? root;
            if (!rootsByDocument.TryGetValue(reference.Document.Id, out root))
            {
                root = await reference.Document.GetSyntaxRootAsync(cancellationToken);
                if (root is not null)
                    rootsByDocument[reference.Document.Id] = root;
            }

            SemanticModel? model;
            if (!modelsByDocument.TryGetValue(reference.Document.Id, out model))
            {
                model = await reference.Document.GetSemanticModelAsync(cancellationToken);
                if (model is not null)
                    modelsByDocument[reference.Document.Id] = model;
            }

            var direct = root is null || model is null
                || IsDirectVirtualReference(declaringType, reference.Location, root, model);
            isDirect[i] = direct;
            if (direct)
                directCount++;
        }

        return (isDirect, directCount, locations.Count - directCount);
    }

    /// <summary>
    /// True when the reference at <paramref name="location"/> is a direct reference to the queried method:
    /// the static receiver type at the call site (syntax + semantic model) is <paramref name="declaringType"/>
    /// or a derived type. Unresolvable receivers return true — the reference is kept.
    /// </summary>
    private static bool IsDirectVirtualReference(
        INamedTypeSymbol declaringType,
        Location location,
        SyntaxNode root,
        SemanticModel model)
    {
        var token = root.FindToken(location.SourceSpan.Start);
        var nameNode = token.Parent;
        if (nameNode is null)
            return true;

        var position = location.SourceSpan.Start;
        var receiverType = GetCallSiteReceiverType(nameNode, model, position);
        return receiverType is null || IsSameOrDerivedFrom(receiverType, declaringType);
    }

    /// <summary>
    /// The static receiver type of the call site whose member name is <paramref name="nameNode"/>. Walks up
    /// from the member name to the receiver-carrying node:
    /// <list type="bullet">
    /// <item><c>expr.M</c> / <c>this.M</c> / <c>base.M</c> / <c>Type.M</c> — <see cref="MemberAccessExpressionSyntax"/> (the direct parent);</item>
    /// <item><c>expr?.M</c> / <c>expr?.M()</c> — <see cref="ConditionalAccessExpressionSyntax"/> above the
    /// <see cref="MemberBindingExpressionSyntax"/> (and, in the invoked form, an <see cref="InvocationExpressionSyntax"/>);</item>
    /// <item><c>M</c> / <c>M()</c> (and the declaration site itself) — no receiver: implicit <c>this</c>,
    /// resolved to the enclosing type.</item>
    /// </list>
    /// Returns null when the receiver type cannot be resolved (metadata, type parameters, …).
    /// </summary>
    private static INamedTypeSymbol? GetCallSiteReceiverType(SyntaxNode nameNode, SemanticModel model, int position)
    {
        for (var node = nameNode; node is not null; node = node.Parent)
        {
            if (node is MemberAccessExpressionSyntax { Expression: { } receiver })
                return GetReceiverType(model, receiver, position);
            if (node is ConditionalAccessExpressionSyntax { Expression: { } target })
                return GetReceiverType(model, target, position);
            if (node is not InvocationExpressionSyntax and not MemberBindingExpressionSyntax
                and not IdentifierNameSyntax and not GenericNameSyntax)
                break;
        }

        return GetContainingTypeAt(model, position);
    }

    /// <summary>
    /// The static type of a call-site receiver expression. <c>this</c> / <c>base</c> resolve to the enclosing
    /// type (and its base) via the enclosing-symbol walk — the same source the implicit-<c>this</c> case uses.
    /// </summary>
    private static INamedTypeSymbol? GetReceiverType(SemanticModel model, ExpressionSyntax receiver, int position)
    {
        if (receiver is ThisExpressionSyntax)
            return GetContainingTypeAt(model, position);
        if (receiver is BaseExpressionSyntax)
            return GetContainingTypeAt(model, position)?.BaseType;
        var type = model.GetTypeInfo(receiver).Type as INamedTypeSymbol;
        // An error type (broken compilation) is unclassifiable — return null so the reference is kept.
        if (type is { TypeKind: TypeKind.Error })
            return null;
        return type;
    }

    /// <summary>
    /// The innermost named type enclosing <paramref name="position"/> (walks <see cref="ISymbol.ContainingSymbol"/>
    /// past locals/parameters/methods) — the implicit-<c>this</c> receiver type for unqualified calls.
    /// </summary>
    private static INamedTypeSymbol? GetContainingTypeAt(SemanticModel model, int position)
    {
        var symbol = model.GetEnclosingSymbol(position);
        while (symbol is not null && symbol is not INamedTypeSymbol)
            symbol = symbol.ContainingSymbol;
        return symbol as INamedTypeSymbol;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="source"/> itself or derives from it
    /// (base-type chain). Generic instantiations compare via <see cref="INamedTypeSymbol.ConstructedFrom"/>
    /// so a call on <c>T&lt;int&gt;</c> matches a method declared on <c>T&lt;T&gt;</c>.
    /// </summary>
    internal static bool IsSameOrDerivedFrom(INamedTypeSymbol candidate, INamedTypeSymbol source)
    {
        for (var type = candidate; type is not null; type = type.BaseType)
        {
            if (IsSameType(type, source)
                || IsSameType(type.ConstructedFrom, source.ConstructedFrom))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> denote the same type.
    /// <see cref="SymbolEqualityComparer.Default"/> matches symbols within one compilation but does
    /// NOT match a source symbol against its metadata counterpart in a referencing compilation
    /// (a call site in a project that consumes the declaring project's built output). A miss therefore
    /// falls back to canonical type identity — the fully-qualified name, which is stable across the
    /// source/metadata boundary. Erring toward a match keeps a reference rather than silently dropping it.
    /// </summary>
    internal static bool IsSameType(INamedTypeSymbol a, INamedTypeSymbol b)
    {
        if (SymbolEqualityComparer.Default.Equals(a, b))
            return true;

        return TypeIdentityKey(a) == TypeIdentityKey(b);
    }

    /// <summary>
    /// Canonical identity of a type: its fully-qualified name (including generic arguments). Stable across
    /// the source/metadata boundary and across compilations, unlike <see cref="SymbolEqualityComparer.Default"/>.
    /// </summary>
    private static string TypeIdentityKey(INamedTypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

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

    private static async Task<List<ISymbol>> FindDeclarationsInAllProjectsAsync(
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

        var declarations = await FindDeclarationsInAllProjectsAsync(solution, searchName, filter, cancellationToken).ConfigureAwait(false);
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
    /// Computes the 1-based <c>column</c> on <paramref name="line"/> of the last character of the first occurrence
    /// of <paramref name="symbolName"/> in the line (<c>index + symbolName.Length</c>, 1-based). Feeding that column
    /// into <see cref="SourcePositionHelper.ToOffset"/> lands the offset on the identifier token itself, from which
    /// <see cref="SourcePositionHelper.GetSymbolAtPosition"/> resolves the referenced/declared symbol. Landing one
    /// character past the token (e.g. the <c>(</c> of an invocation) does not resolve: <c>GetSymbolInfo</c> on the
    /// invocation node returns null. Used by <c>find_symbol_definition</c> / <c>find_symbol_references</c> when a
    /// caller passes <c>line</c> without <c>column</c>. Returns an error when the line is out of range or the name
    /// does not occur on that line.
    /// </summary>
    private static (int? Column, string? Error) ComputeColumnOnLine(SourceText text, int line, string symbolName)
    {
        if (line < 1)
        {
            return (null, $"`line` must be >= 1 (got {line}).");
        }

        if (line > text.Lines.Count)
        {
            return (null, $"`line` {line} is out of range (file has {text.Lines.Count} lines).");
        }

        var lineText = text.Lines[line - 1].ToString();
        var index = lineText.IndexOf(symbolName, StringComparison.Ordinal);
        if (index < 0)
        {
            return (null, $"Symbol `{symbolName}` was not found on line {line}.");
        }

        return (index + symbolName.Length, null);
    }

    /// <summary>
    /// Resolves a 1-based (line, column) for <paramref name="symbolName"/> starting from <paramref name="line"/>.
    /// When <paramref name="column"/> is provided it is used as-is. Otherwise the column is computed from the first
    /// occurrence of <paramref name="symbolName"/> on <paramref name="line"/>; if the name does not occur on that
    /// line, the nearest enclosing member's body is searched as a fallback.
    /// Returns (line, column, null) on success or (0, 0, error) on failure.
    /// </summary>
    private static (int Line, int Column, string? Error) ResolveSymbolPosition(
        SyntaxNode syntaxRoot, SourceText text, int line, int? column, string symbolName)
    {
        if (column.HasValue)
            return (line, column.Value, null);

        var (computedColumn, computeError) = ComputeColumnOnLine(text, line, symbolName);
        if (computedColumn.HasValue)
            return (line, computedColumn.Value, null);

        // Only attempt the member-body fallback when the line is in range.
        if (line >= 1 && line <= text.Lines.Count)
        {
            var (fbLine, fbCol, fbError) = FindSymbolInEnclosingMember(syntaxRoot, text, line, symbolName);
            if (fbError is null)
                return (fbLine!.Value, fbCol!.Value, null);

            return (0, 0, fbError);
        }

        return (0, 0, computeError);
    }

    /// <summary>
    /// Fallback for when <paramref name="symbolName"/> is not found on the exact <paramref name="line"/>:
    /// finds the nearest enclosing member declaration (method/constructor/property/operator) that spans that line
    /// and searches for <paramref name="symbolName"/> within the member's body.
    /// Returns the absolute (1-based line, 1-based column) of the first occurrence in the body, or an error.
    /// </summary>
    private static (int? Line, int? Column, string? Error) FindSymbolInEnclosingMember(
        SyntaxNode syntaxRoot, SourceText text, int line, string symbolName)
    {
        var position = text.Lines[line - 1].Start;
        var token = syntaxRoot.FindToken(position);

        SyntaxNode? enclosingMember = null;
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            if (node is MethodDeclarationSyntax
                || node is ConstructorDeclarationSyntax
                || node is DestructorDeclarationSyntax
                || node is PropertyDeclarationSyntax
                || node is EventDeclarationSyntax
                || node is OperatorDeclarationSyntax
                || node is ConversionOperatorDeclarationSyntax)
            {
                enclosingMember = node;
                break;
            }
        }

        if (enclosingMember is null)
            return (null, null, $"Symbol `{symbolName}` was not found on line {line} (no enclosing member).");

        var body = GetMemberBody(enclosingMember);
        if (body is null)
            return (null, null, $"Symbol `{symbolName}` was not found on line {line} (enclosing member has no body).");

        SyntaxToken? matchingToken = null;
        foreach (var t in body.DescendantTokens())
        {
            if (t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == symbolName)
            {
                matchingToken = t;
                break;
            }
        }

        if (matchingToken is null)
            return (null, null, $"Symbol `{symbolName}` was not found on line {line} or in the enclosing member body.");

        var absolutePosition = matchingToken.Value.SpanStart + symbolName.Length;
        var lp = text.Lines.GetLinePosition(absolutePosition);
        return (lp.Line + 1, lp.Character, null);
    }

    private static SyntaxNode? GetMemberBody(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? (SyntaxNode?)m.ExpressionBody?.Expression,
        ConstructorDeclarationSyntax c => (SyntaxNode?)c.Body ?? (SyntaxNode?)c.ExpressionBody?.Expression,
        DestructorDeclarationSyntax d => d.Body,
        PropertyDeclarationSyntax p => GetPropertyBody(p),
        EventDeclarationSyntax e => e.AccessorList?.Accessors
            .SelectMany(a => new SyntaxNode?[] { a.Body, a.ExpressionBody?.Expression })
            .FirstOrDefault(b => b is not null),
        OperatorDeclarationSyntax o => (SyntaxNode?)o.Body ?? (SyntaxNode?)o.ExpressionBody?.Expression,
        ConversionOperatorDeclarationSyntax co => (SyntaxNode?)co.Body ?? (SyntaxNode?)co.ExpressionBody?.Expression,
        _ => null
    };

    private static SyntaxNode? GetPropertyBody(PropertyDeclarationSyntax p)
    {
        if (p.AccessorList is null)
            return null;

        foreach (var accessor in p.AccessorList.Accessors)
        {
            if (accessor.Body is not null)
                return accessor.Body;

            if (accessor.ExpressionBody?.Expression is not null)
                return accessor.ExpressionBody.Expression;
        }

        return null;
    }

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

            // The analyzer-sanitized solution: FindCallersAsync goes through FindReferencesSearchEngine and
            // crashes on the project checksum when any project carries an UnresolvedAnalyzerReference stub
            // (see WorkspaceAnalyzerSanitizer).
            var solution = _solutionManager.GetSanitizedSolution() ?? document.Project.Solution;
            var (graph, analyzerRetry) = await WorkspaceAnalyzerSanitizer.WithSanitizedRetryAsync(
                sol => hasLine
                    ? CallGraphHelper.BuildCallGraphAtPositionAsync(
                        sol, document, line!.Value, column!.Value, includeExternalCallees, cancellationToken)
                    : CallGraphHelper.BuildCallGraphAsync(
                        sol, document, className!, methodName!, includeExternalCallees, cancellationToken),
                _solutionManager.GetSanitizedSolution,
                solution,
                cancellationToken).ConfigureAwait(false);

            var cap = Math.Clamp(maxNodes, 1, 100);
            var totalNodes = graph.Callers.Count + graph.Callees.Count;
            var summary = $"Callers: {graph.Callers.Count}, Callees: {graph.Callees.Count}";
            var markdown = CallGraphHelper.FormatMarkdown(graph);
            if (analyzerRetry)
            {
                markdown += Environment.NewLine + Environment.NewLine + AnalyzerFallbackNote;
            }

            return ToolTelemetry.TraceAndReturn(
                toolName,
                SearchOverflowHelper.CapOrWriteToTempFile(totalNodes, cap, markdown, summary));
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
