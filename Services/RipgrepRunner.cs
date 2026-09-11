using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RoslynMcpServer.Services;

/// <summary>
/// Runs ripgrep (<c>rg.exe</c>) to search a set of root directories and parses the
/// <c>path:line:content</c> output. rg provides fast (SIMD-accelerated) literal/regex matching,
/// skips hidden directories (<c>.git</c>, <c>.vs</c>), and respects <c>.gitignore</c> when present.
/// The managed line-scan fallback in <see cref="Tools.UtilityTools"/> is used only when rg is
/// unavailable on the machine.
/// </summary>
public static partial class RipgrepRunner
{
    /// <summary>
    /// Windows <c>CreateProcess</c> command-line limit is 32767 chars; keep headroom for the
    /// pattern and fixed arguments when packing root paths into a single invocation.
    /// </summary>
    private const int MaxCommandLineChars = 24_000;

    /// <summary><c>--stats</c> line reporting how many files were searched (printed to stdout).</summary>
    [GeneratedRegex(@"^(\d+) files searched$", RegexOptions.Compiled)]
    private static partial Regex FilesSearchedRegex();

    /// <summary>
    /// Match line <c>path:line:content</c>. Non-greedy path: Windows paths contain at most the
    /// drive-letter colon (a single character followed by a separator, never <c>:digits:</c>),
    /// so the first <c>:digits:</c> boundary is the line number.
    /// </summary>
    [GeneratedRegex(@"^(.*?):(\d+):(.*)$", RegexOptions.Compiled)]
    private static partial Regex MatchLineRegex();

    /// <summary>One matched line: file path, 1-based line number, and the full line text.</summary>
    public sealed record Match(string FilePath, int LineNumber, string LineText);

    /// <summary>
    /// Outcome of an rg search. <see cref="Succeeded"/> is false only on rg errors (exit code 2,
    /// e.g. an invalid regex); "no matches" is a successful result with an empty <see cref="Matches"/>.
    /// </summary>
    public sealed record Result(
        bool Succeeded,
        bool TimedOut,
        string? Error,
        IReadOnlyList<Match> Matches,
        int FilesSearched);

    /// <summary>
    /// Resolves the ripgrep executable: an explicit <paramref name="configuredPath"/> (when the file
    /// exists), otherwise the first <c>rg</c>/<c>rg.exe</c> found on <c>PATH</c>. Returns
    /// <see langword="null"/> when not found.
    /// </summary>
    public static string? ResolvePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), OperatingSystem.IsWindows() ? "rg.exe" : "rg");
            }
            catch (ArgumentException)
            {
                continue; // malformed PATH entry
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Searches <paramref name="roots"/> with ripgrep. Roots are packed into one or more invocations
    /// (the Windows command line cannot hold thousands of paths). <paramref name="includeExtensions"/>
    /// is empty to scan all files. Returns a failed <see cref="Result"/> on rg errors; a
    /// <see cref="Result.TimedOut"/> result with partial matches when the budget runs out.
    /// </summary>
    public static async Task<Result> SearchAsync(
        string ripgrepPath,
        IReadOnlyList<string> roots,
        string pattern,
        bool useRegex,
        bool caseSensitive,
        IReadOnlyCollection<string> includeExtensions,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var matches = new List<Match>();
        var filesSearched = 0;
        var stopwatch = Stopwatch.StartNew();
        var deadline = timeout is null ? (TimeSpan?)null : stopwatch.Elapsed + timeout.Value;

        foreach (var batch in BuildBatches(roots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline is not null && stopwatch.Elapsed >= deadline.Value)
            {
                return new Result(true, TimedOut: true, null, matches, filesSearched);
            }

            var (succeeded, timedOut, error, batchMatches, batchFiles) = await RunBatchAsync(
                ripgrepPath,
                batch,
                pattern,
                useRegex,
                caseSensitive,
                includeExtensions,
                deadline is null ? (TimeSpan?)null : deadline.Value - stopwatch.Elapsed,
                cancellationToken).ConfigureAwait(false);

            if (!succeeded)
            {
                return new Result(false, false, error, matches, filesSearched);
            }

            matches.AddRange(batchMatches);
            filesSearched += batchFiles;
            if (timedOut)
            {
                return new Result(true, TimedOut: true, null, matches, filesSearched);
            }
        }

        return new Result(true, false, null, matches, filesSearched);
    }

    private static async Task<(bool Succeeded, bool TimedOut, string? Error, List<Match> Matches, int FilesSearched)>
        RunBatchAsync(
            string ripgrepPath,
            IReadOnlyList<string> roots,
            string pattern,
            bool useRegex,
            bool caseSensitive,
            IReadOnlyCollection<string> includeExtensions,
            TimeSpan? remainingTimeout,
            CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ripgrepPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--color");
        psi.ArgumentList.Add("never");
        psi.ArgumentList.Add("--stats");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("-M");
        psi.ArgumentList.Add("0"); // never omit long lines
        if (!useRegex)
        {
            psi.ArgumentList.Add("-F"); // fixed string
        }

        if (!caseSensitive)
        {
            psi.ArgumentList.Add("-i");
        }

        psi.ArgumentList.Add(pattern);

        foreach (var extension in includeExtensions)
        {
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add("*" + extension);
        }

        // Hidden directories (.git, .vs, ...) are already skipped by rg defaults.
        psi.ArgumentList.Add("-g");
        psi.ArgumentList.Add("!bin");
        psi.ArgumentList.Add("-g");
        psi.ArgumentList.Add("!obj");

        foreach (var root in roots)
        {
            psi.ArgumentList.Add(root);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = ReadAllLinesAsync(process.StandardOutput);
        var stderrTask = ReadAllLinesAsync(process.StandardError);
        var exitTask = process.WaitForExitAsync(cancellationToken);

        try
        {
            if (remainingTimeout is not null)
            {
                var delay = Task.Delay(remainingTimeout.Value, cancellationToken);
                var completed = await Task.WhenAny(exitTask, delay).ConfigureAwait(false);
                if (completed != exitTask)
                {
                    KillProcessTree(process);
                    await exitTask.ConfigureAwait(false);
                    var (timeoutLines, _) = await CollectOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                    return (true, TimedOut: true, Error: null, Matches: ParseMatches(timeoutLines), FilesSearched: CountFilesSearched(timeoutLines));
                }
            }
            else
            {
                await exitTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }

        var (lines, stderr) = await CollectOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
        var exitCode = process.ExitCode;
        if (exitCode == 2)
        {
            return (false, false, string.Join(" ", stderr).Trim(), new List<Match>(), 0);
        }

        // exit 0 = matches found, 1 = no matches — both are successful runs.
        return (true, false, null, ParseMatches(lines), CountFilesSearched(lines));
    }

    private static List<List<string>> BuildBatches(IReadOnlyList<string> roots)
    {
        var batches = new List<List<string>>();
        var current = new List<string>();
        var length = 0;
        foreach (var root in roots)
        {
            if (current.Count > 0 && length + root.Length + 1 > MaxCommandLineChars)
            {
                batches.Add(current);
                current = new List<string>();
                length = 0;
            }

            current.Add(root);
            length += root.Length + 1;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    private static List<Match> ParseMatches(IReadOnlyList<string> stdoutLines)
    {
        var matches = new List<Match>();
        foreach (var line in stdoutLines)
        {
            if (FilesSearchedRegex().IsMatch(line))
            {
                continue; // --stats line
            }

            var match = MatchLineRegex().Match(line);
            if (!match.Success)
            {
                continue; // other --stats lines (matched lines, bytes searched, elapsed, ...)
            }

            matches.Add(new Match(match.Groups[1].Value, int.Parse(match.Groups[2].Value), match.Groups[3].Value));
        }

        return matches;
    }

    private static int CountFilesSearched(IReadOnlyList<string> stdoutLines)
    {
        foreach (var line in stdoutLines)
        {
            var match = FilesSearchedRegex().Match(line);
            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value);
            }
        }

        return 0;
    }

    private static async Task<(List<string> Stdout, List<string> Stderr)> CollectOutputAsync(
        Task<List<string>> stdoutTask,
        Task<List<string>> stderrTask)
    {
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (stdout, stderr);
    }

    private static async Task<List<string>> ReadAllLinesAsync(System.IO.StreamReader reader)
    {
        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort: the process may already be gone
        }
    }
}
