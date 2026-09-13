using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynMcpServer.Diagnostics;

/// <summary>Parses <c>dotnet test</c> console output (xUnit / VSTest / NUnit) and ignores MSBuild noise.</summary>
public static partial class VstestOutputParser
{
    private const int MaxFailedTestDetails = 5;
    private const int MaxStackTraceLinesPerFailure = 15;

    /// <summary>VSTest console duration: <c>[12 ms]</c>, <c>[1 s]</c>, <c>[1 m 28 s]</c>, <c>[1 h 2 m]</c>.</summary>
    private const string VstestDurationBracket =
        @"\[\d+(?:\.\d+)?\s*(?:ms|s|m|h)(?:\s+\d+(?:\.\d+)?\s*(?:ms|s|m|h))*\]";

    [GeneratedRegex(@"^\[xUnit\.net[^\]]*\]\s+(?<name>.+?)\s+\[FAIL\]\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RxXunitFailLine();

    [GeneratedRegex(@"^\s+Passed\s+(?<name>[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]+)+)\s+" + VstestDurationBracket + @"\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RxVstestPassedLine();

    [GeneratedRegex(@"^\s+Failed\s+(?<name>[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]+)+)(?:\s+" + VstestDurationBracket + @")?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RxVstestFailedLine();

    [GeneratedRegex(@"^\s*Failed\s*:\s*(?<name>[A-Za-z_][\w]+(?:\.[A-Za-z_][\w]+)*)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RxNunitFailedLine();

    [GeneratedRegex(@"(?<kind>Passed|Failed)!\s+-\s+Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+)(?:,\s*Skipped:\s*(?<skipped>\d+))?(?:,\s*Total:\s*(?<total>\d+))?", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RxEndSummaryLine();

    [GeneratedRegex(@"Passed!\s+-\s+Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RxEndSummaryLineAlt();

    [GeneratedRegex(@"Total tests:\s*(?<total>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RxTotalTests();

    [GeneratedRegex(@"^\s*Passed:\s*(?<n>\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RxPassedCountLine();

    [GeneratedRegex(@"^\s*Failed:\s*(?<n>\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RxFailedCountLine();

    [GeneratedRegex(@"^\s*Skipped:\s*(?<n>\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RxSkippedCountLine();

    [GeneratedRegex(@"Test Run Successful\.?", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RxTestRunSuccessful();

    /// <summary>VSTest console block: Total tests + Passed (Failed/Skipped optional). Fail-only .slnx blocks are parsed line-wise.</summary>
    [GeneratedRegex(@"Total tests:\s*(?<total>\d+)(?:[\s\S]{0,2000}?)\s+Passed:\s*(?<passed>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RxVstestTotalsBlock();

    /// <summary>VSTest per-assembly discovery noise: emitted when the filter matches no test in that assembly.</summary>
    [GeneratedRegex(@"No test matches the given testcase filter", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RxNoTestMatchesLine();

    public sealed record TestSummary(int Total, int Passed, int Failed, int Skipped);

    public sealed record FailedTestDetail(string Name, string Error, string Stack);

    public sealed record ParseResult(
        TestSummary? Summary,
        bool HasRecognizedSummary,
        bool IsPartialSuccess,
        IReadOnlyList<FailedTestDetail> Failures,
        IReadOnlyList<string> PassedTestNames);

    public static string DeduplicateNuGetAuditLines(string combinedOutput)
    {
        if (string.IsNullOrEmpty(combinedOutput))
        {
            return combinedOutput;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var raw in combinedOutput.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = raw;
            if (IsNuGetAuditLine(line))
            {
                var key = line.Trim();
                if (!seen.Add(key))
                {
                    continue;
                }
            }

            sb.AppendLine(raw);
        }

        return sb.ToString().TrimEnd();
    }

    public static ParseResult Parse(string combinedOutput, int exitCode)
    {
        combinedOutput = DeduplicateNuGetAuditLines(combinedOutput);
        var summary = TryParseTestSummary(combinedOutput);
        var hasSummary = summary is not null || HasSummaryMarkers(combinedOutput);
        var passedNames = CollectPassedTestNames(combinedOutput);
        var failures = ParseFailedTestBlocks(combinedOutput, MaxFailedTestDetails);
        var isPartial = exitCode == 0 && !hasSummary;

        return new ParseResult(summary, hasSummary, isPartial, failures, passedNames);
    }

    public static bool FilterMatchedAnyTest(string? filter, string combinedOutput, IReadOnlyList<string> passedNames)
    {
        var needle = ExtractFilterNeedle(filter);
        if (string.IsNullOrEmpty(needle))
        {
            return true;
        }

        foreach (var name in passedNames)
        {
            if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var name in CollectFailedTestNames(combinedOutput))
        {
            if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var line in combinedOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (RxVstestPassedLine().IsMatch(line.TrimEnd())
                && line.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Max chars of failure details kept inline before they are offloaded to a temp file.</summary>
    private const int InlineDetailsMaxChars = 1500;

    public static string BuildMarkdownReport(
        ParseResult parse,
        int exitCode,
        string combinedOutput,
        string? filter,
        string? filterDescription,
        bool requireFilterMatch,
        string projectName,
        bool verbose,
        string? runMetadata,
        string? extraMeta)
    {
        var sb = new StringBuilder();

        // A filter that actually executed tests (aggregate Total tests > 0) has matched by definition.
        // In a multi-project run, sibling assemblies that contain no matching test emit
        // "No test matches the given testcase filter ..." noise lines; those must NOT short-circuit
        // the whole run into a false "zero tests matched" discovery failure when another assembly
        // did execute the filtered test. Only report "no matching tests" when VSTest explicitly said
        // so AND no test actually ran.
        var anyTestExecuted = parse.Summary is { Total: > 0 };
        if (requireFilterMatch && !anyTestExecuted && RxNoTestMatchesLine().IsMatch(combinedOutput))
        {
            sb.AppendLine($"""
                ## Filtered test run — no matching tests

                Agent signal: zero tests matched the filter (build may still show `0 Error(s)`). Verify Roslyn workspace scope with `get_test_list` after loading the test `.sln`/`.slnx`.
                """);
            if (!string.IsNullOrWhiteSpace(filterDescription))
            {
                sb.AppendLine($"Match mode: {filterDescription}");
            }

            TempReportWriter.AppendPointer(sb, combinedOutput, projectName, "raw-output.md", "Raw output");
            AppendMetadata(sb, filter, runMetadata, extraMeta, verbose);
            return sb.ToString().TrimEnd();
        }

        if (parse.IsPartialSuccess)
        {
            sb.AppendLine($"""
                Status: partial

                Tests completed (exit 0); no VSTest/xUnit summary line was detected.
                """);
            TempReportWriter.AppendPointer(sb, combinedOutput, projectName, "raw-output.md", "Raw output");
            AppendMetadata(sb, filter, runMetadata, extraMeta, verbose);
            return sb.ToString().TrimEnd();
        }

        if (parse.Summary is null && !parse.HasRecognizedSummary)
        {
            var header = string.IsNullOrWhiteSpace(filter) ? "## Test run" : "## Filtered test run";
            sb.AppendLine($"""
                {header}

                No standard VSTest/xUnit summary line was detected (exit code `{exitCode}`).
                """);
            TempReportWriter.AppendPointer(sb, combinedOutput, projectName, "console-output.md", "Console output");
            AppendMetadata(sb, filter, runMetadata, extraMeta, verbose);
            return sb.ToString().TrimEnd();
        }

        var summary = parse.Summary ?? InferSummaryFromMarkers(combinedOutput);
        if (summary is null)
        {
            sb.AppendLine($"""
                Status: partial

                Tests completed; summary counts could not be parsed.
                """);
            TempReportWriter.AppendPointer(sb, combinedOutput, projectName, "raw-output.md", "Raw output");
            AppendMetadata(sb, filter, runMetadata, extraMeta, verbose);
            return sb.ToString().TrimEnd();
        }

        var (total, passed, failed, skipped) = summary;

        // Concise header: Match line (when filtered) + one-line totals. No section headers, no raw filter, no bold.
        if (!string.IsNullOrWhiteSpace(filterDescription))
        {
            sb.AppendLine($"Match: {filterDescription}");
        }

        var skippedPart = skipped > 0 ? $" · Skipped: {skipped}" : string.Empty;
        sb.AppendLine($"Total: {total} · Passed: {passed} · Failed: {failed}{skippedPart}");

        if (failed == 0)
        {
            if (exitCode != 0)
            {
                sb.AppendLine($"""

                _Exit code is non-zero but no test failed — sibling test projects reported `No test matches the given testcase filter` and are ignored. All executed tests passed._
                """);
            }

            AppendMetadata(sb, filter, runMetadata, extraMeta, verbose);
            return sb.ToString().TrimEnd();
        }

        // Fail case: Match + totals + WARNINGs (if any) + real error text/exceptions.
        var warnings = ExtractWarnings(runMetadata);
        if (!string.IsNullOrEmpty(warnings))
        {
            sb.AppendLine();
            sb.AppendLine(warnings);
        }

        sb.AppendLine($"""

        ### Error details:
        """);
        var details = BuildErrorDetails(parse.Failures);
        if (details.Length <= InlineDetailsMaxChars)
        {
            sb.AppendLine();
            sb.AppendLine(details);
        }
        else
        {
            TempReportWriter.AppendPointer(sb, details, projectName, "test-failures.md", "Full failure details");
        }

        AppendMetadata(sb, filter, runMetadata, extraMeta, verbose);
        return sb.ToString().TrimEnd();
    }

    private static string? ExtractWarnings(string? runMetadata)
    {
        if (string.IsNullOrWhiteSpace(runMetadata))
        {
            return null;
        }

        var warnings = runMetadata
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("**WARNING:**", StringComparison.Ordinal))
            .Select(l => l.Replace("**", string.Empty, StringComparison.Ordinal))
            .ToArray();
        return warnings.Length > 0 ? string.Join(Environment.NewLine, warnings) : null;
    }

    private static string BuildErrorDetails(IReadOnlyList<FailedTestDetail> failures)
    {
        if (failures.Count == 0)
        {
            return "(failure details could not be parsed from the log)";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < failures.Count; i++)
        {
            var f = failures[i];
            sb.AppendLine($"{i + 1}. {EscapeMdBackticks(f.Name)}");
            if (!string.IsNullOrEmpty(f.Error))
            {
                sb.AppendLine($"   Error: {f.Error}");
            }

            if (!string.IsNullOrEmpty(f.Stack))
            {
                sb.AppendLine($"   Stack: {f.Stack}");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendMetadata(
        StringBuilder sb,
        string? filter,
        string? runMetadata,
        string? extraMeta,
        bool verbose)
    {
        if (!verbose)
        {
            return;
        }

        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            sb.AppendLine($"Filter: `{EscapeMdBackticks(filter)}`");
        }

        if (!string.IsNullOrWhiteSpace(runMetadata))
        {
            sb.AppendLine();
            sb.AppendLine(runMetadata.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(extraMeta))
        {
            sb.AppendLine(extraMeta.TrimEnd());
        }
    }


    private static bool HasSummaryMarkers(string text) =>
        RxEndSummaryLine().IsMatch(text)
        || RxEndSummaryLineAlt().IsMatch(text)
        || RxTotalTests().IsMatch(text)
        || RxTestRunSuccessful().IsMatch(text)
        || RxVstestTotalsBlock().IsMatch(text);

    private static TestSummary? InferSummaryFromMarkers(string text)
    {
        var endSummary = TryParseEndSummaryLines(text);
        if (endSummary is not null)
        {
            return endSummary;
        }

        var alt = RxEndSummaryLineAlt().Match(text);
        if (alt.Success)
        {
            var passed = int.Parse(alt.Groups["passed"].Value, CultureInfo.InvariantCulture);
            var failed = int.Parse(alt.Groups["failed"].Value, CultureInfo.InvariantCulture);
            return new TestSummary(passed + failed, passed, failed, 0);
        }

        return TryParseTestSummary(text);
    }

    private static bool IsNuGetAuditLine(string line) =>
        line.Contains("NU190", StringComparison.OrdinalIgnoreCase)
        || line.Contains("NU160", StringComparison.OrdinalIgnoreCase)
        || (line.Contains("GHSA-", StringComparison.OrdinalIgnoreCase)
            && line.Contains("warning", StringComparison.OrdinalIgnoreCase));

    private static TestSummary? TryParseTestSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var endSummary = TryParseEndSummaryLines(text);
        if (endSummary is not null)
        {
            return endSummary;
        }

        var alt = RxEndSummaryLineAlt().Match(text);
        if (alt.Success)
        {
            var passed = int.Parse(alt.Groups["passed"].Value, CultureInfo.InvariantCulture);
            var failed = int.Parse(alt.Groups["failed"].Value, CultureInfo.InvariantCulture);
            return new TestSummary(passed + failed, passed, failed, 0);
        }

        // "Total tests: N" blocks (console;verbosity=normal): one block per test assembly — aggregate.
        return TryParseTotalTestsBlocks(text);
    }

    /// <summary>
    /// Parses the <c>Total tests: N</c> blocks (emitted per test assembly with
    /// <c>--logger console;verbosity=normal</c>) and aggregates them. A single-block run behaves
    /// exactly as before; a solution run sums every block instead of mixing the first block's totals
    /// with count lines of a different block. Returns <see langword="null"/> when no block carries any
    /// count lines (Total-only output, e.g. truncated) so the caller reports partial instead of inventing counts.
    /// </summary>
    private static TestSummary? TryParseTotalTestsBlocks(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.None);
        var total = 0;
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var found = false;
        var hasCounts = false;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!RxTotalTests().IsMatch(lines[i].Trim()))
            {
                continue;
            }

            found = true;
            var block = TryParseCountsNearTotalTestsLine(lines, i);
            if (block is null)
            {
                // Total-only block (no Passed/Failed/Skipped lines): count the total, keep counts as-is.
                var tm = RxTotalTests().Match(lines[i].Trim());
                total += int.Parse(tm.Groups["total"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            hasCounts = true;
            total += block.Total;
            passed += block.Passed;
            failed += block.Failed;
            skipped += block.Skipped;
        }

        // Every block was Total-only (truncated output): do not invent counts — report partial instead.
        return found && hasCounts ? new TestSummary(total, passed, failed, skipped) : null;
    }

    private static TestSummary? TryParseCountsNearTotalTestsLine(string[] lines, int totalTestsLineIndex)
    {
        var tm = RxTotalTests().Match(lines[totalTestsLineIndex].Trim());
        if (!tm.Success)
        {
            return null;
        }

        var total = int.Parse(tm.Groups["total"].Value, CultureInfo.InvariantCulture);
        int? passed = null;
        int? failed = null;
        int? skipped = null;

        for (var j = totalTestsLineIndex; j < Math.Min(totalTestsLineIndex + 24, lines.Length); j++)
        {
            if (j > totalTestsLineIndex && RxTotalTests().IsMatch(lines[j].Trim()))
            {
                // Next assembly's block starts — stop, otherwise its count lines leak into this block.
                break;
            }

            var line = lines[j].TrimEnd();
            var pm = RxPassedCountLine().Match(line);
            if (pm.Success)
            {
                passed = int.Parse(pm.Groups["n"].Value, CultureInfo.InvariantCulture);
            }

            var fm = RxFailedCountLine().Match(line);
            if (fm.Success)
            {
                failed = int.Parse(fm.Groups["n"].Value, CultureInfo.InvariantCulture);
            }

            var sm = RxSkippedCountLine().Match(line);
            if (sm.Success)
            {
                skipped = int.Parse(sm.Groups["n"].Value, CultureInfo.InvariantCulture);
            }
        }

        // .slnx / MSBuild VSTest target often omits Passed: when every test failed (and Failed: on all-pass).
        // Only infer Passed when Failed or Skipped was present — Total-only stays unparsed.
        if (passed is null)
        {
            if (failed is null && skipped is null)
            {
                return null;
            }

            passed = Math.Max(0, total - (failed ?? 0) - (skipped ?? 0));
        }

        failed ??= 0;
        skipped ??= 0;
        return new TestSummary(total, passed.Value, failed.Value, skipped.Value);
    }

    private static TestSummary SummaryFromEndMatch(Match m)
    {
        var passed = int.Parse(m.Groups["passed"].Value, CultureInfo.InvariantCulture);
        var failed = int.Parse(m.Groups["failed"].Value, CultureInfo.InvariantCulture);
        var skipped = m.Groups["skipped"].Success
            ? int.Parse(m.Groups["skipped"].Value, CultureInfo.InvariantCulture)
            : 0;
        var total = m.Groups["total"].Success
            ? int.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture)
            : passed + failed + skipped;
        return new TestSummary(total, passed, failed, skipped);
    }

    /// <summary>
    /// Parses the <c>Passed!/Failed! - Failed: N, Passed: N, ...</c> end-summary lines. A solution run
    /// emits one line per test assembly — aggregate all of them (picking a single line under-reports
    /// the total). Returns <see langword="null"/> when no such line is present.
    /// </summary>
    private static TestSummary? TryParseEndSummaryLines(string text)
    {
        var matches = RxEndSummaryLine().Matches(text);
        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count == 1)
        {
            return SummaryFromEndMatch(matches[0]);
        }

        var total = 0;
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        foreach (Match m in matches)
        {
            var p = int.Parse(m.Groups["passed"].Value, CultureInfo.InvariantCulture);
            var f = int.Parse(m.Groups["failed"].Value, CultureInfo.InvariantCulture);
            var s = m.Groups["skipped"].Success
                ? int.Parse(m.Groups["skipped"].Value, CultureInfo.InvariantCulture)
                : 0;
            var t = m.Groups["total"].Success
                ? int.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture)
                : p + f + s;
            passed += p;
            failed += f;
            skipped += s;
            total += t;
        }

        return new TestSummary(total, passed, failed, skipped);
    }

    private static IReadOnlyList<string> CollectPassedTestNames(string text)
    {
        var names = new List<string>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = RxVstestPassedLine().Match(line.TrimEnd());
            if (m.Success)
            {
                names.Add(m.Groups["name"].Value.Trim());
            }
        }

        return names;
    }

    private static IReadOnlyList<string> CollectFailedTestNames(string text)
    {
        var names = new List<string>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd();
            if (TryGetFailedTestName(trimmed, out var name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static IReadOnlyList<FailedTestDetail> ParseFailedTestBlocks(string text, int maxCount)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.None);
        var blocks = new List<(int StartLine, string Name)>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (TryGetFailedTestName(lines[i].TrimEnd(), out var name))
            {
                blocks.Add((i, name));
            }
        }

        var result = new List<FailedTestDetail>();
        for (var b = 0; b < blocks.Count && result.Count < maxCount; b++)
        {
            var start = blocks[b].StartLine;
            var name = blocks[b].Name;
            var end = b + 1 < blocks.Count ? blocks[b + 1].StartLine : lines.Length;
            var blockText = string.Join(Environment.NewLine, lines[start..end]);
            ExtractErrorAndStack(blockText, out var error, out var stack);
            result.Add(new FailedTestDetail(name, error, stack));
        }

        return result;
    }

    private static bool TryGetFailedTestName(string trimmed, out string name)
    {
        name = string.Empty;
        if (IsMsBuildNoiseLine(trimmed))
        {
            return false;
        }

        var xm = RxXunitFailLine().Match(trimmed);
        if (xm.Success)
        {
            name = xm.Groups["name"].Value.Trim();
            return IsPlausibleTestName(name);
        }

        var nm = RxNunitFailedLine().Match(trimmed);
        if (nm.Success)
        {
            name = nm.Groups["name"].Value.Trim();
            return IsPlausibleTestName(name);
        }

        var vm = RxVstestFailedLine().Match(trimmed);
        if (vm.Success)
        {
            name = vm.Groups["name"].Value.Trim();
            return IsPlausibleTestName(name);
        }

        return false;
    }

    private static bool IsMsBuildNoiseLine(string line) =>
        line.Contains("prune package", StringComparison.OrdinalIgnoreCase)
        || line.Contains("to load", StringComparison.OrdinalIgnoreCase)
        || line.Contains("MSBuild", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Done executing", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlausibleTestName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.StartsWith("to ", StringComparison.OrdinalIgnoreCase)
        && name.Contains('.', StringComparison.Ordinal);

    private static string? ExtractFilterNeedle(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var idx = filter.IndexOf('~');
        if (idx < 0)
        {
            idx = filter.IndexOf('=');
        }

        return idx >= 0 && idx < filter.Length - 1
            ? filter[(idx + 1)..].Trim()
            : filter.Trim();
    }

    private static void ExtractErrorAndStack(string block, out string error, out string stack)
    {
        error = string.Empty;
        stack = string.Empty;

        var emIdx = block.IndexOf("Error Message:", StringComparison.OrdinalIgnoreCase);
        var stIdx = block.IndexOf("Stack Trace:", StringComparison.OrdinalIgnoreCase);

        if (emIdx >= 0)
        {
            var bodyStart = emIdx + "Error Message:".Length;
            var bodyEnd = stIdx >= 0 ? stIdx : block.Length;
            error = NormalizeDetailBody(block.AsSpan(bodyStart, bodyEnd - bodyStart));
        }
        else if (stIdx > 0)
        {
            var firstNl = block.IndexOf('\n');
            if (firstNl >= 0 && firstNl + 1 < stIdx)
            {
                error = NormalizeDetailBody(block.AsSpan(firstNl + 1, stIdx - firstNl - 1));
            }
        }

        if (stIdx >= 0)
        {
            var after = block[(stIdx + "Stack Trace:".Length)..].TrimStart();
            stack = TrimStackTrace(after);
        }
    }

    private static string NormalizeDetailBody(ReadOnlySpan<char> span)
    {
        var s = span.ToString().Trim();
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var line in s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.Length == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(t);
        }

        return sb.Length > 512 ? sb.ToString(0, 509) + "..." : sb.ToString();
    }

    private static string TrimStackTrace(string stack)
    {
        if (string.IsNullOrWhiteSpace(stack))
        {
            return string.Empty;
        }

        var stackLines = stack
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .Take(MaxStackTraceLinesPerFailure)
            .ToArray();

        var joined = string.Join(Environment.NewLine, stackLines);
        return joined.Length > 1200 ? joined[..1197] + "..." : joined;
    }

    private static string EscapeMdBackticks(string s) => s.Replace('`', '\'');
}
