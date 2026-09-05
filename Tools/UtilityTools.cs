using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcpServer.Config;
using RoslynMcpServer.Diagnostics;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools;

public sealed class UtilityTools
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".git",
        ".vs"
    };

    private readonly ILogger<UtilityTools> _logger;
    private readonly SolutionManager _solutionManager;
    private readonly WorkspaceConfig _workspaceConfig;

    public UtilityTools(ILogger<UtilityTools> logger, SolutionManager solutionManager, WorkspaceConfig workspaceConfig)
    {
        _logger = logger;
        _solutionManager = solutionManager;
        _workspaceConfig = workspaceConfig;
    }

    [McpServerTool(Name = "get_method_body", Title = "GetMethodBody")]
    [Description(
        "Returns the source of the first method matching `methodName` inside `className` in a file "
        + "(no overload selection — first match wins; use `update_method_body` with `parameterTypes` when overloads matter). "
        + "Reads **disk** (like the host read tool), not the Roslyn index — unsaved editor buffers are not included. "
        + "Prefers this over reading the whole file for large sources.")]
    public async Task<string> GetMethodBody(
        [Description("Absolute path or workspace-relative path to the C# source file (same parameter name as the host read tool).")] string filePath,
        [Description("Class name containing the method")] string className,
        [Description("Method name to extract")] string methodName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return ToolTelemetry.TraceAndReturn(nameof(GetMethodBody), "Error: `filePath` is empty.");
            }

            if (string.IsNullOrWhiteSpace(className))
            {
                return ToolTelemetry.TraceAndReturn(nameof(GetMethodBody), "Class name is empty.");
            }

            if (string.IsNullOrWhiteSpace(methodName))
            {
                return ToolTelemetry.TraceAndReturn(nameof(GetMethodBody), "Method name is empty.");
            }

            var fullPath = _solutionManager.ResolvePathAgainstWorkspace(filePath);
            if (!File.Exists(fullPath))
            {
                return ToolTelemetry.TraceAndReturn(nameof(GetMethodBody), $"File not found: `{fullPath}`");
            }

            var source = await File.ReadAllTextAsync(fullPath, cancellationToken);
            var syntaxTree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
            var root = await syntaxTree.GetRootAsync(cancellationToken);

            var classNode = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => string.Equals(c.Identifier.Text, className, StringComparison.Ordinal));

            if (classNode is null)
            {
                return ToolTelemetry.TraceAndReturn(nameof(GetMethodBody), $"Class `{className}` not found in `{fullPath}`.");
            }

            var method = classNode.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => string.Equals(m.Identifier.Text, methodName, StringComparison.Ordinal));

            if (method is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(GetMethodBody),
                    $"Method `{methodName}` was not found in class `{className}` (`{fullPath}`).");
            }

            return ToolTelemetry.TraceAndReturn(nameof(GetMethodBody), method.ToFullString().Trim());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMethodBody failed for {FilePath} {ClassName}.{MethodName}", filePath, className, methodName);
            return ToolTelemetry.TraceAndReturn(
                nameof(GetMethodBody),
                $"Failed to extract `{className}.{methodName}` from `{filePath}`: {ex.Message}");
        }
    }

    [McpServerTool(Name = "search_code", Title = "SearchCode")]
    [Description(
        "Searches source files like a lightweight ripgrep for LLM workflows. Returns matching lines with file path and line number. " +
        "When the number of matches exceeds the cap, the full result (same markdown format) is written to a temp file and a short response (count + path + file summary) is returned — nothing is silently truncated. " +
        "When `directoryPath` is omitted, defaults to loaded workspace root (if available), otherwise current directory. By default scans only `.cs` files; override with `includeExtensions`. " +
        "Skips `bin`, `obj`, `.git`, and `.vs`. Default matching is case-insensitive; for leftover branding checks (e.g. exact `dupsFinder` after rename to `DupFinder`) set `caseSensitive=true`. " +
        "Relative `directoryPath` resolves against process CWD.")]
    public Task<string> SearchCode(
        [Description("Search pattern used to match lines. Interpreted as plain text when `useRegex=false`, or as a regular expression when `useRegex=true`.")] string pattern,
        [Description("Optional root directory to search. If null or empty, loaded workspace root is used when available; otherwise `Environment.CurrentDirectory`.")] string? directoryPath = null,
        [Description("Comma/semicolon/space-separated file extensions to scan (default: `.cs`). Example: `.cs,.csproj,.sln,.json`. Use `*` to scan all files.")] string? includeExtensions = ".cs",
        [Description("When true, interprets `pattern` as a .NET regular expression. When false, performs text search using Contains.")] bool useRegex = false,
        [Description("When false (default), matching is case-insensitive. When true, plain and regex matching are case-sensitive. Use true for leftover branding verification.")] bool caseSensitive = false,
        [Description("Cap on the number of matched lines (argument > config `max-results` > default 50). When exceeded, the full result is written to a temp file.")] int? maxResults = null,
        [Description("Maximum scan time in seconds. Default is 20; set 0 to disable timeout.")] int maxScanSeconds = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return Task.FromResult(ToolTelemetry.TraceAndReturn(nameof(SearchCode), "Error: `pattern` is empty."));
            }

            if (maxResults.HasValue && maxResults.Value <= 0)
            {
                return Task.FromResult(ToolTelemetry.TraceAndReturn(nameof(SearchCode), "Error: `maxResults` must be greater than 0."));
            }

            var rootDirectory = ResolveSearchRootDirectory(directoryPath);
            var extensionFilter = ParseExtensionFilter(includeExtensions);

            if (!Directory.Exists(rootDirectory))
            {
                return Task.FromResult(ToolTelemetry.TraceAndReturn(nameof(SearchCode), $"Error: Directory not found: `{rootDirectory}`"));
            }

            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            Regex? regex = null;
            if (useRegex)
            {
                try
                {
                    var regexOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                    if (!caseSensitive)
                    {
                        regexOptions |= RegexOptions.IgnoreCase;
                    }

                    regex = new Regex(pattern, regexOptions);
                }
                catch (ArgumentException ex)
                {
                    return Task.FromResult(ToolTelemetry.TraceAndReturn(nameof(SearchCode), $"Error: Invalid regex pattern: {ex.Message}"));
                }
            }

            var matches = new List<string>();
            var matchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filesScanned = 0;
            var directoriesStack = new Stack<string>();
            directoriesStack.Push(rootDirectory);
            var stopwatch = Stopwatch.StartNew();
            var scanTimeout = maxScanSeconds > 0 ? TimeSpan.FromSeconds(maxScanSeconds) : Timeout.InfiniteTimeSpan;
            var timedOut = false;

            _logger.LogInformation(
                "SearchCode started: pattern={Pattern} root={RootDirectory} useRegex={UseRegex} caseSensitive={CaseSensitive} maxResults={MaxResults} maxScanSeconds={MaxScanSeconds}",
                pattern,
                rootDirectory,
                useRegex,
                caseSensitive,
                maxResults,
                maxScanSeconds);
            _logger.LogInformation(
                "SearchCode filter: includeExtensions={IncludeExtensions}",
                extensionFilter.IncludeAll ? "*" : string.Join(",", extensionFilter.Extensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));

            while (directoriesStack.Count > 0 && !timedOut)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scanTimeout != Timeout.InfiniteTimeSpan && stopwatch.Elapsed >= scanTimeout)
                {
                    timedOut = true;
                    break;
                }

                var currentDirectory = directoriesStack.Pop();

                IEnumerable<string> subDirectories;
                try
                {
                    subDirectories = Directory.EnumerateDirectories(currentDirectory);
                }
                catch
                {
                    continue;
                }

                foreach (var subDirectory in subDirectories)
                {
                    var name = Path.GetFileName(subDirectory);
                    if (ExcludedDirectories.Contains(name))
                    {
                        continue;
                    }

                    directoriesStack.Push(subDirectory);
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(currentDirectory);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    if (timedOut)
                    {
                        break;
                    }

                    if (!extensionFilter.IncludeAll && !extensionFilter.Extensions.Contains(Path.GetExtension(file)))
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (scanTimeout != Timeout.InfiniteTimeSpan && stopwatch.Elapsed >= scanTimeout)
                    {
                        timedOut = true;
                        break;
                    }

                    filesScanned++;
                    if (filesScanned % 1000 == 0)
                    {
                        _logger.LogInformation(
                            "SearchCode progress: scanned={FilesScanned} matches={Matches} elapsedMs={ElapsedMs} root={RootDirectory}",
                            filesScanned,
                            matches.Count,
                            stopwatch.ElapsedMilliseconds,
                            rootDirectory);
                    }

                    int lineNumber = 0;
                    IEnumerable<string> lines;
                    try
                    {
                        lines = File.ReadLines(file);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var line in lines)
                    {
                        lineNumber++;
                        var isMatch = useRegex
                            ? regex!.IsMatch(line)
                            : line.Contains(pattern, comparison);

                        if (!isMatch)
                        {
                            continue;
                        }

                        matches.Add($"{file}:{lineNumber} | {line}");
                        matchedFiles.Add(file);
                    }
                }
            }

            if (matches.Count == 0)
            {
                if (timedOut)
                {
                    _logger.LogWarning(
                        "SearchCode timed out with no matches: pattern={Pattern} scanned={FilesScanned} elapsedMs={ElapsedMs} root={RootDirectory}",
                        pattern,
                        filesScanned,
                        stopwatch.ElapsedMilliseconds,
                        rootDirectory);
                    return Task.FromResult(ToolTelemetry.TraceAndReturn(
                        nameof(SearchCode),
                        $"No matches found for `{pattern}` in `{rootDirectory}` before timeout ({maxScanSeconds}s). Scanned files: {filesScanned}."));
                }

                return Task.FromResult(ToolTelemetry.TraceAndReturn(
                    nameof(SearchCode),
                    $"No matches found for `{pattern}` in `{rootDirectory}`."));
            }

            var cap = SearchOverflowHelper.ResolveMaxResults(maxResults, _workspaceConfig);

            var result = new StringBuilder();
            result.AppendLine($"Found {matches.Count} match(es) for `{pattern}` in `{rootDirectory}`.");
            result.AppendLine($"Scanned files: {filesScanned}.");
            if (timedOut)
            {
                result.AppendLine($"[!] Search timed out after {maxScanSeconds}s. Results are partial.");
            }

            result.AppendLine();
            foreach (var match in matches)
            {
                result.AppendLine(match);
            }

            _logger.LogInformation(
                "SearchCode completed: pattern={Pattern} root={RootDirectory} matches={Matches} scanned={FilesScanned} timedOut={TimedOut} elapsedMs={ElapsedMs}",
                pattern,
                rootDirectory,
                matches.Count,
                filesScanned,
                timedOut,
                stopwatch.ElapsedMilliseconds);

            var fullMarkdown = result.ToString().TrimEnd();
            var summary = BuildFileListSummary(matchedFiles);

            return Task.FromResult(ToolTelemetry.TraceAndReturn(
                nameof(SearchCode),
                SearchOverflowHelper.CapOrWriteToTempFile(matches.Count, cap, fullMarkdown, summary)));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(ToolTelemetry.TraceAndReturn(nameof(SearchCode), "SearchCode was cancelled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchCode failed for pattern {Pattern} in {DirectoryPath}", pattern, directoryPath);
            return Task.FromResult(ToolTelemetry.TraceAndReturn(nameof(SearchCode), $"Error: {ex.Message}"));
        }
    }

    private string ResolveSearchRootDirectory(string? directoryPath)
    {
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            return Path.GetFullPath(directoryPath);
        }

        var loadedWorkspaceDirectory = _solutionManager.GetLoadedWorkspaceDirectory();
        if (!string.IsNullOrWhiteSpace(loadedWorkspaceDirectory))
        {
            return loadedWorkspaceDirectory;
        }

        return Environment.CurrentDirectory;
    }

    private static (bool IncludeAll, HashSet<string> Extensions) ParseExtensionFilter(string? includeExtensions)
    {
        var raw = string.IsNullOrWhiteSpace(includeExtensions) ? ".cs" : includeExtensions.Trim();
        if (string.Equals(raw, "*", StringComparison.Ordinal))
        {
            return (true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var values = raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            values = [".cs"];
        }

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var normalized = value.Length > 0 && value[0] == '.' ? value : "." + value;
            extensions.Add(normalized);
        }

        return (false, extensions);
    }

    private static string BuildFileListSummary(IEnumerable<string> files)
    {
        var distinct = files
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0)
        {
            return "(no matching files)";
        }

        return string.Join(Environment.NewLine, distinct.Select(f => $"- `{f}`"));
    }

    [McpServerTool(Name = "run_format", Title = "RunFormat")]
    [Description(
        "Runs `dotnet format` to stabilize code style after edits. Supports verify-only mode (`--verify-no-changes`). "
        + "Fixed process timeout 300s. Prefer after bulk AST/patch edits. "
        + "When not `verifyOnly`, formatted `.cs` on disk are picked up by the next symbol search (disk-sync) automatically.")]
    public async Task<string> RunFormat(
        [Description("Path to a .sln, .slnx, .csproj, or directory — same parameter name as `run_dotnet_test` (directories allowed here; `run_dotnet_build` requires a file).")] string workspacePath,
        [Description("When true, checks formatting without changing files (`--verify-no-changes`).")] bool verifyOnly = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                return ToolTelemetry.TraceAndReturn(nameof(RunFormat), "Error: `workspacePath` is empty.");
            }

            var fullPath = Path.GetFullPath(workspacePath);
            var workingDirectory = Directory.Exists(fullPath)
                ? WorkspaceRootResolver.ResolveDotNetWorkingDirectory(
                    WorkspaceRootResolver.FindSolutionOrProjectInDirectory(fullPath) ?? fullPath)
                : WorkspaceRootResolver.ResolveDotNetWorkingDirectory(fullPath);

            if (!Directory.Exists(workingDirectory))
            {
                return ToolTelemetry.TraceAndReturn(nameof(RunFormat), $"Error: Working directory not found: `{workingDirectory}`");
            }

            var args = new StringBuilder("format ");
            args.Append('"').Append(fullPath).Append('"');
            if (verifyOnly)
            {
                args.Append(" --verify-no-changes");
            }

            var run = await DotNetCliRunner.RunWithMetadataAsync(
                args.ToString(),
                workingDirectory,
                cancellationToken,
                TimeSpan.FromSeconds(DotNetCliRunner.DefaultTimeoutSeconds)).ConfigureAwait(false);

            var stdout = run.CombinedOutput;
            var processExitCode = run.ExitCode;
            var result = new StringBuilder();
            result.AppendLine(run.RunMetadata);
            result.AppendLine($"ExitCode: {processExitCode}");
            result.AppendLine($"Mode: {(verifyOnly ? "verify-only" : "apply")}");
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                result.AppendLine().AppendLine("Output:").AppendLine(stdout);
            }

            return ToolTelemetry.TraceAndReturn(nameof(RunFormat), result.ToString().TrimEnd());
        }
        catch (OperationCanceledException)
        {
            return ToolTelemetry.TraceAndReturn(nameof(RunFormat), "RunFormat was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunFormat failed for {WorkspacePath}", workspacePath);
            return ToolTelemetry.TraceAndReturn(nameof(RunFormat), $"Error: {ex.Message}");
        }
    }

    [McpServerTool(Name = "rename_symbol", Title = "RenameSymbol")]
    [Description(
        "Performs semantic C# symbol rename using Roslyn (types, members, namespaces as symbols — not project folders or docs). " +
        "Can preview impacted locations before applying changes, and can scope updates to a project or entire solution. " +
        "Pass 1-based `line`/`column` (on the declaration or a usage) to select the exact symbol. " +
        "Without a position, several same-named declarations in the file produce an error listing the candidates (FQN + line:col) — no blind first match. " +
        "Applies **saved** `.cs` from disk before resolving the symbol. " +
        "For directory/.csproj/solution graph renames use `rename_project`. For README/rules/URLs use host Grep/edit. " +
        "Workspace is taken from the config (`RoslynMcp.jsonc` `workspace-path`) and loaded lazily; the first call after server start can take minutes (workspace load) — the host timeout should be ≥ 600000 ms.")]
    public async Task<string> RenameSymbol(
        [Description("Path to a C# file containing the target symbol declaration or usage.")] string filePath,
        [Description("Current symbol name to rename.")] string symbolName,
        [Description("New symbol name that should replace the current name.")] string newName,
        [Description("1-based line of the target symbol (declaration or usage); must be provided together with `column`.")] int? line = null,
        [Description("1-based column of the target symbol (declaration or usage); must be provided together with `line`.")] int? column = null,
        [Description("Rename scope: `project` (default) or `solution`.")] string scope = "project",
        [Description("When true, returns preview only and does not write any changes.")] bool previewOnly = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(symbolName) || string.IsNullOrWhiteSpace(newName))
            {
                _logger.LogWarning(
                    "RenameSymbol rejected: missing arguments (filePath empty={NoPath}, symbolName empty={NoSym}, newName empty={NoNew}).",
                    string.IsNullOrWhiteSpace(filePath),
                    string.IsNullOrWhiteSpace(symbolName),
                    string.IsNullOrWhiteSpace(newName));
                return ToolTelemetry.TraceAndReturn(nameof(RenameSymbol), "Error: `filePath`, `symbolName`, and `newName` are required.");
            }

            // Lazy ensure path: loads the configured workspace when nothing is loaded yet (config-based,
            // like all solution-wide methods); FindDocumentAsync below additionally covers walk-up.
            var solution = await _solutionManager.GetCurrentSolutionAfterDiskSyncAsync(cancellationToken);
            if (solution is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(RenameSymbol),
                    WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(
                        "Error: No workspace loaded.",
                        _solutionManager.ConfiguredWorkspacePath));
            }

            var normalizedScope = scope.Trim().ToLowerInvariant();
            if (normalizedScope is not ("project" or "solution"))
            {
                _logger.LogWarning(
                    "RenameSymbol rejected: invalid scope `{Scope}` for `{SymbolName}` -> `{NewName}` in `{FilePath}`.",
                    scope,
                    symbolName,
                    newName,
                    filePath);
                return ToolTelemetry.TraceAndReturn(nameof(RenameSymbol), "Error: `scope` must be either `project` or `solution`.");
            }

            var fullPath = _solutionManager.ResolvePathAgainstWorkspace(filePath);
            var document = await _solutionManager.FindDocumentAsync(fullPath, cancellationToken);
            if (document is null)
            {
                _logger.LogWarning(
                    "RenameSymbol: document not in workspace for `{SymbolName}` -> `{NewName}` (file `{FilePath}`).",
                    symbolName,
                    newName,
                    filePath);
                return ToolTelemetry.TraceAndReturn(nameof(RenameSymbol), $"Error: Document not found in workspace: `{fullPath}`");
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || semanticModel is null)
            {
                _logger.LogWarning(
                    "RenameSymbol: no syntax/semantic model for `{SymbolName}` -> `{NewName}` in `{FilePath}`.",
                    symbolName,
                    newName,
                    filePath);
                return ToolTelemetry.TraceAndReturn(nameof(RenameSymbol), "Error: Failed to obtain syntax root or semantic model.");
            }

            var hasLine = line.HasValue;
            var hasColumn = column.HasValue;
            if (hasLine != hasColumn)
            {
                return ToolTelemetry.TraceAndReturn(nameof(RenameSymbol), "Error: `line` and `column` must be provided together (both 1-based).");
            }

            ISymbol targetSymbol;
            if (hasLine)
            {
                var text = await document.GetTextAsync(cancellationToken);
                var (offset, positionError) = SourcePositionHelper.ToOffset(text, line!.Value, column!.Value);
                if (positionError is not null)
                {
                    return ToolTelemetry.TraceAndReturn(nameof(RenameSymbol), $"Error: {positionError}");
                }

                var resolvedSymbol = SourcePositionHelper.GetSymbolAtPosition(root, semanticModel, offset, cancellationToken);
                if (resolvedSymbol is null)
                {
                    _logger.LogWarning(
                        "RenameSymbol: no symbol at line {Line}, column {Column} in `{FilePath}` for rename to `{NewName}`.",
                        line!.Value,
                        column!.Value,
                        filePath,
                        newName);
                    return ToolTelemetry.TraceAndReturn(
                        nameof(RenameSymbol),
                        $"Error: No symbol found at line {line!.Value}, column {column!.Value} in `{fullPath}`.");
                }

                targetSymbol = resolvedSymbol;
            }
            else
            {
                var resolved = new List<(SyntaxNode Declaration, ISymbol Symbol)>();
                foreach (var (declaration, variable) in FindSymbolDeclarations(root, symbolName))
                {
                    var symbol = semanticModel.GetDeclaredSymbol(variable ?? declaration, cancellationToken);
                    if (symbol is not null)
                    {
                        resolved.Add((declaration, symbol));
                    }
                }

                if (resolved.Count == 0)
                {
                    _logger.LogWarning(
                        "RenameSymbol: symbol `{SymbolName}` not found for rename to `{NewName}` in `{FilePath}`.",
                        symbolName,
                        newName,
                        filePath);
                    return ToolTelemetry.TraceAndReturn(nameof(RenameSymbol), $"Error: Symbol `{symbolName}` not found.");
                }

                if (resolved.Count > 1)
                {
                    return ToolTelemetry.TraceAndReturn(
                        nameof(RenameSymbol),
                        BuildAmbiguousDeclarationsMessage(symbolName, fullPath, resolved));
                }

                targetSymbol = resolved[0].Symbol;
            }

            // The analyzer-sanitized solution: SymbolFinder crashes on the project checksum when any project
            // carries an UnresolvedAnalyzerReference stub (see WorkspaceAnalyzerSanitizer).
            var baseSolution = _solutionManager.GetSanitizedSolution() ?? document.Project.Solution;
            var (references, _) = await WorkspaceAnalyzerSanitizer.WithSanitizedRetryAsync(
                sol => SymbolFinder.FindReferencesAsync(targetSymbol, sol, cancellationToken),
                _solutionManager.GetSanitizedSolution,
                baseSolution,
                cancellationToken);
            var affectedLocations = references
                .SelectMany(r => r.Locations)
                .Where(l => l.Location.IsInSource)
                .ToList();

            var targetProjectId = document.Project.Id;
            if (normalizedScope == "project")
            {
                affectedLocations = affectedLocations
                    .Where(l => l.Document.Project.Id == targetProjectId)
                    .ToList();
            }

            if (previewOnly)
            {
                var preview = new StringBuilder();
                preview.AppendLine($"Symbol: `{symbolName}` -> `{newName}`");
                preview.AppendLine($"Scope: {normalizedScope}");
                preview.AppendLine($"Affected locations: {affectedLocations.Count}");
                foreach (var location in affectedLocations.Take(100))
                {
                    var lineNumber = location.Location.GetLineSpan().StartLinePosition.Line + 1;
                    preview.AppendLine($"- {location.Document.FilePath}:{lineNumber}");
                }
                if (affectedLocations.Count > 100)
                {
                    preview.AppendLine("[!] Showing first 100 locations only.");
                }
                return ToolTelemetry.TraceAndReturn(nameof(RenameSymbol), preview.ToString().TrimEnd());
            }

            var renameOptions = new SymbolRenameOptions();
            var renamedSolution = await Renamer.RenameSymbolAsync(
                baseSolution,
                targetSymbol,
                renameOptions,
                newName,
                cancellationToken);

            if (normalizedScope == "project")
            {
                foreach (var project in renamedSolution.Projects.Where(p => p.Id != targetProjectId))
                {
                    foreach (var doc in project.Documents)
                    {
                        var originalDoc = baseSolution.GetDocument(doc.Id);
                        if (originalDoc is null)
                        {
                            continue;
                        }

                        var originalText = await originalDoc.GetTextAsync(cancellationToken);
                        renamedSolution = renamedSolution.WithDocumentText(doc.Id, originalText);
                    }
                }
            }

            var changedDocs = new List<(Document Doc, string Text)>();
            foreach (var newProject in renamedSolution.Projects)
            {
                foreach (var newDoc in newProject.Documents)
                {
                    var oldDoc = baseSolution.GetDocument(newDoc.Id);
                    if (oldDoc is null || newDoc.FilePath is null)
                    {
                        continue;
                    }

                    var oldText = await oldDoc.GetTextAsync(cancellationToken);
                    var newText = await newDoc.GetTextAsync(cancellationToken);
                    if (!string.Equals(oldText.ToString(), newText.ToString(), StringComparison.Ordinal))
                    {
                        changedDocs.Add((newDoc, newText.ToString()));
                    }
                }
            }

            foreach (var (doc, text) in changedDocs)
            {
                _solutionManager.SuppressDiskWatchForPath(doc.FilePath!);
                await File.WriteAllTextAsync(doc.FilePath!, text, cancellationToken);
                await _solutionManager.UpdateDocumentInMemoryAsync(doc.FilePath!, text, cancellationToken);
            }

            return ToolTelemetry.TraceAndReturn(
                nameof(RenameSymbol),
                $"Rename applied: `{symbolName}` -> `{newName}`. Updated files: {changedDocs.Count}.");
        }
        catch (OperationCanceledException)
        {
            return ToolTelemetry.TraceAndReturn(nameof(RenameSymbol), "RenameSymbol was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameSymbol failed for {SymbolName} in {FilePath}", symbolName, filePath);
            return ToolTelemetry.TraceAndReturn(nameof(RenameSymbol), $"Error: {ex.Message}");
        }
    }

    [McpServerTool(Name = "list_projects", Title = "ListProjects")]
    [Description("Lists projects from the active workspace solution, including target frameworks, output type, and project references.")]
    public async Task<string> ListProjects(
        [Description("Optional path to a .sln, .slnx, or .csproj. When provided, workspace is loaded/reloaded before listing projects.")] string? workspacePath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(workspacePath))
            {
                await _solutionManager.LoadAsync(workspacePath, cancellationToken);
            }

            var solution = _solutionManager.GetCurrentSolution();
            if (solution is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(ListProjects),
                    WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(
                        "Error: No workspace loaded.",
                        _solutionManager.ConfiguredWorkspacePath));
            }

            var sb = new StringBuilder();
            foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var tfm = "(unknown)";
                var outputType = "(unknown)";
                var projectPath = project.FilePath ?? "(unknown path)";

                if (project.FilePath is not null && File.Exists(project.FilePath))
                {
                    try
                    {
                        var xml = await File.ReadAllTextAsync(project.FilePath, cancellationToken);
                        tfm = ExtractSimpleCsprojValue(xml, "TargetFramework")
                            ?? ExtractSimpleCsprojValue(xml, "TargetFrameworks")
                            ?? tfm;
                        outputType = ExtractSimpleCsprojValue(xml, "OutputType") ?? outputType;
                    }
                    catch
                    {
                        // keep unknown metadata if csproj parsing fails
                    }
                }

                var refs = project.ProjectReferences
                    .Select(r => solution.GetProject(r.ProjectId)?.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();

                sb.AppendLine($"- {project.Name}");
                sb.AppendLine($"  Path: {projectPath}");
                sb.AppendLine($"  TFM: {tfm}");
                sb.AppendLine($"  OutputType: {outputType}");
                sb.AppendLine($"  References: {(refs.Count == 0 ? "(none)" : string.Join(", ", refs))}");
            }

            return ToolTelemetry.TraceAndReturn(nameof(ListProjects), sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListProjects failed for {WorkspacePath}", workspacePath);
            return ToolTelemetry.TraceAndReturn(nameof(ListProjects), $"Error: {ex.Message}");
        }
    }

    [McpServerTool(Name = "get_project_graph", Title = "GetProjectGraph")]
    [Description("Builds a project-to-project dependency graph from the active workspace solution.")]
    public async Task<string> GetProjectGraph(
        [Description("Optional path to a .sln, .slnx, or .csproj. When provided, workspace is loaded/reloaded before building the graph.")] string? workspacePath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(workspacePath))
            {
                await _solutionManager.LoadAsync(workspacePath, cancellationToken);
            }

            var solution = _solutionManager.GetCurrentSolution();
            if (solution is null)
            {
                return ToolTelemetry.TraceAndReturn(
                    nameof(GetProjectGraph),
                    WorkspaceLoadGuidance.FormatNoWorkspaceLoadedMessage(
                        "Error: No workspace loaded.",
                        _solutionManager.ConfiguredWorkspacePath));
            }

            var sb = new StringBuilder();
            foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var deps = project.ProjectReferences
                    .Select(r => solution.GetProject(r.ProjectId)?.Name ?? r.ProjectId.ToString())
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                sb.AppendLine($"{project.Name} -> {(deps.Count == 0 ? "(none)" : string.Join(", ", deps))}");
            }

            return ToolTelemetry.TraceAndReturn(nameof(GetProjectGraph), sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetProjectGraph failed for {WorkspacePath}", workspacePath);
            return ToolTelemetry.TraceAndReturn(nameof(GetProjectGraph), $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds every declaration in <paramref name="root"/> whose name equals <paramref name="symbolName"/>
    /// (class/struct/interface/enum/method/property; a <c>FieldDeclarationSyntax</c> contributes one match per
    /// matching variable). Returns an empty list when nothing matches.
    /// </summary>
    private static List<(SyntaxNode Declaration, SyntaxNode? Variable)> FindSymbolDeclarations(SyntaxNode root, string symbolName)
    {
        var matches = new List<(SyntaxNode Declaration, SyntaxNode? Variable)>();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ClassDeclarationSyntax c when string.Equals(c.Identifier.Text, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case StructDeclarationSyntax s when string.Equals(s.Identifier.Text, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case InterfaceDeclarationSyntax i when string.Equals(i.Identifier.Text, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case EnumDeclarationSyntax e when string.Equals(e.Identifier.Text, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case MethodDeclarationSyntax m when string.Equals(m.Identifier.Text, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case PropertyDeclarationSyntax p when string.Equals(p.Identifier.Text, symbolName, StringComparison.Ordinal):
                    matches.Add((node, null));
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var variable in f.Declaration.Variables)
                    {
                        if (string.Equals(variable.Identifier.Text, symbolName, StringComparison.Ordinal))
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
        List<(SyntaxNode Declaration, ISymbol Symbol)> resolved)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Symbol `{symbolName}` matches {resolved.Count} declarations in `{fullPath}`. Provide 1-based `line`/`column` to select one:");
        sb.AppendLine();
        foreach (var (declaration, symbol) in resolved)
        {
            var pos = declaration.SyntaxTree.GetLineSpan(declaration.Span).StartLinePosition;
            sb.AppendLine($"- {symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} — {pos.Line + 1}:{pos.Character + 1}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string? ExtractSimpleCsprojValue(string xml, string elementName)
    {
        var openTag = $"<{elementName}>";
        var closeTag = $"</{elementName}>";
        var start = xml.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += openTag.Length;
        var end = xml.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0 || end <= start)
        {
            return null;
        }

        return xml[start..end].Trim();
    }
}
