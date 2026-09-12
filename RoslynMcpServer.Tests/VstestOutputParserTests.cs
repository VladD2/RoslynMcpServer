using RoslynMcpServer.Diagnostics;
using Xunit;

namespace RoslynMcpServer.Tests;

public sealed class VstestOutputParserTests
{
    [Fact]
    public void Parse_ignores_msbuild_failed_to_load_prune_line()
    {
        const string output = """
            Failed to load prune package data from NuGet, please verify restore targets.
            Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 120 ms
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 0);
        Assert.Empty(result.Failures);
        Assert.True(result.HasRecognizedSummary);
        Assert.Equal(3, result.Summary?.Passed);
    }

    [Fact]
    public void Parse_recognizes_vstest_passed_line_with_fqn()
    {
        const string output = """
            Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1
              Passed BrqMover.Tests.WorkItemUrlParserTests.Parse_Valid [12 ms]
            """;
        var result = VstestOutputParser.Parse(output, 0);
        Assert.Single(result.PassedTestNames);
        Assert.Contains("WorkItemUrlParserTests", result.PassedTestNames[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_recognizes_vstest_passed_line_with_minute_second_duration()
    {
        const string output = """
            Test Run Successful.
            Total tests: 1
                 Passed: 1
              Passed Ns.NexwayOrderCompletedNotificationContext_ReceivedNotification_ExternalEventsPublished [1 m 28 s]
            """;
        var result = VstestOutputParser.Parse(output, 0);
        Assert.False(result.IsPartialSuccess);
        Assert.Single(result.PassedTestNames);
        Assert.Contains(
            "NexwayOrderCompletedNotificationContext_ReceivedNotification_ExternalEventsPublished",
            result.PassedTestNames[0],
            StringComparison.Ordinal);
        Assert.True(VstestOutputParser.FilterMatchedAnyTest(
            "FullyQualifiedName~NexwayOrderCompletedNotificationContext_ReceivedNotification_ExternalEventsPublished",
            output,
            result.PassedTestNames));
        var md = VstestOutputParser.BuildMarkdownReport(
            result,
            0,
            output,
            "FullyQualifiedName~NexwayOrderCompletedNotificationContext_ReceivedNotification_ExternalEventsPublished",
            "Name suffix",
            requireFilterMatch: true);
        Assert.Contains("Filtered tests passed", md, StringComparison.Ordinal);
        Assert.DoesNotContain("no matching tests", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_recognizes_vstest_passed_line_with_second_duration()
    {
        const string output = """
            Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1
              Passed Ns.SlowTests.TakesOneSecond [1 s]
            """;
        var result = VstestOutputParser.Parse(output, 0);
        Assert.Single(result.PassedTestNames);
        Assert.Equal("Ns.SlowTests.TakesOneSecond", result.PassedTestNames[0]);
    }

    [Fact]
    public void Parse_does_not_treat_bracket_noise_as_passed_duration()
    {
        const string output = """
            Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1
              Passed Ns.OtherTests.Other [SKIP]
              Passed Ns.OtherTests.Real [12 ms]
            """;
        var result = VstestOutputParser.Parse(output, 0);
        Assert.Single(result.PassedTestNames);
        Assert.Equal("Ns.OtherTests.Real", result.PassedTestNames[0]);
    }

    [Fact]
    public void Parse_recognizes_vstest_failed_line_with_minute_second_duration()
    {
        const string output = """
            Total tests: 1
                 Failed: 1
              Failed Ns.SlowTests.FailsAfterMinute [1 m 28 s]
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 1);
        Assert.Single(result.Failures);
        Assert.Equal("Ns.SlowTests.FailsAfterMinute", result.Failures[0].Name);
    }

    [Fact]
    public void Parse_partial_when_exit_zero_without_summary()
    {
        const string output = "Building test projects...\nDone.\n";
        var result = VstestOutputParser.Parse(output, 0);
        Assert.True(result.IsPartialSuccess);
        Assert.False(result.HasRecognizedSummary);
    }

    [Fact]
    public void FilterMatchedAnyTest_false_when_no_test_line_matches_class()
    {
        const string output = """
            Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1
              Passed Other.Namespace.OtherTests.Other [1 ms]
            """;
        var result = VstestOutputParser.Parse(output, 0);
        Assert.False(VstestOutputParser.FilterMatchedAnyTest(
            "FullyQualifiedName~WorkItemUrlParserTests",
            output,
            result.PassedTestNames));
    }

    [Fact]
    public void Parse_vstest_console_total_tests_and_passed_without_failed_line()
    {
        const string output = """
            Test Run Successful.
            Total tests: 4
                 Passed: 4
              Passed BrqMover.Tests.WorkItemUrlParserTests.A [17 ms]
            """;
        var result = VstestOutputParser.Parse(output, 0);
        Assert.False(result.IsPartialSuccess);
        Assert.True(result.HasRecognizedSummary);
        Assert.Equal(4, result.Summary?.Total);
        Assert.Equal(4, result.Summary?.Passed);
        Assert.Equal(0, result.Summary?.Failed);
    }

    [Fact]
    public void Parse_slnx_total_tests_and_failed_without_passed_line()
    {
        const string output = """
            Test Run Failed.
            Total tests: 1
                 Failed: 1
             Total time: 1,7379 Minutes
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 1);
        Assert.False(result.IsPartialSuccess);
        Assert.True(result.HasRecognizedSummary);
        Assert.Equal(1, result.Summary?.Total);
        Assert.Equal(0, result.Summary?.Passed);
        Assert.Equal(1, result.Summary?.Failed);

        var md = VstestOutputParser.BuildMarkdownReport(result, 1, output, null, null, false);
        Assert.Contains("1 Tests Failed", md, StringComparison.Ordinal);
        Assert.DoesNotContain("**Status:** partial", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_infers_passed_from_total_minus_failed_and_skipped()
    {
        const string output = """
            Total tests: 5
                 Failed: 2
                Skipped: 1
             Total time: 2.1 Seconds
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 1);
        Assert.Equal(5, result.Summary?.Total);
        Assert.Equal(2, result.Summary?.Passed);
        Assert.Equal(2, result.Summary?.Failed);
        Assert.Equal(1, result.Summary?.Skipped);
    }

    [Fact]
    public void Parse_total_tests_only_does_not_infer_all_passed()
    {
        const string output = """
            Total tests: 4
             Total time: 1.0 Seconds
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 1);
        Assert.True(result.HasRecognizedSummary);
        Assert.Null(result.Summary);
    }

    [Fact]
    public void Parse_aggregates_per_assembly_summary_lines_for_solution_run()
    {
        // A .sln run emits one end-summary line per test assembly — the report must sum them,
        // not pick a single line (which under-reported 252 tests as 1).
        const string output = """
            Test run for C:\w\bin\Debug\net8.0\WiWorkflowTests.dll (.NETCoreApp,Version=v8.0)
            VSTest version 17.11.1 (x64)

            Test run for C:\w\bin\Debug\net8.0\ParserTests.dll (.NETCoreApp,Version=v8.0)
            VSTest version 17.11.1 (x64)

            Test run for C:\w\bin\Debug\net8.0\RegexTests.dll (.NETCoreApp,Version=v8.0)
            VSTest version 17.11.1 (x64)

            Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 12 ms - WiWorkflowTests.dll (net8.0)
              Skipped RequiredSubruleNamesAreNotSpecified
              Skipped ShouldReportErrorForUndefinedRuleReference

            Passed!  - Failed:     0, Passed:   242, Skipped:     2, Total:   244, Duration: 112 ms - ParserTests.dll (net8.0)

            Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 512 ms - RegexTests.dll (net8.0)
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 0);
        Assert.NotNull(result.Summary);
        Assert.Equal(254, result.Summary!.Total);
        Assert.Equal(252, result.Summary.Passed);
        Assert.Equal(0, result.Summary.Failed);
        Assert.Equal(2, result.Summary.Skipped);
    }

    [Fact]
    public void Parse_aggregates_failed_assembly_into_solution_totals()
    {
        const string output = """
            Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 12 ms - A.dll (net8.0)
            Failed!  - Failed:     2, Passed:     3, Skipped:     1, Total:     6, Duration: 40 ms - B.dll (net8.0)
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 1);
        Assert.NotNull(result.Summary);
        Assert.Equal(7, result.Summary!.Total);
        Assert.Equal(4, result.Summary.Passed);
        Assert.Equal(2, result.Summary.Failed);
        Assert.Equal(1, result.Summary.Skipped);
    }

    [Fact]
    public void Parse_aggregates_total_tests_blocks_for_solution_run()
    {
        // With --logger console;verbosity=normal each assembly emits a "Total tests: N" block
        // (not a "Passed! - ..." line). The first block's totals must not be mixed with a
        // different block's Skipped/Failed count lines (reported 254 tests as "Total: 1, Skipped: 2").
        const string output = """
            Test run for C:\w\bin\Debug\net8.0\WiWorkflowTests.dll (.NETCoreApp,Version=v8.0)
            VSTest version 17.11.1 (x64)

            Test run for C:\w\bin\Debug\net8.0\ParserTests.dll (.NETCoreApp,Version=v8.0)
            VSTest version 17.11.1 (x64)

            Test run for C:\w\bin\Debug\net8.0\RegexTests.dll (.NETCoreApp,Version=v8.0)
            VSTest version 17.11.1 (x64)

            Starting test execution, please wait...
            A total of 1 test files matched the specified pattern.

            Test Run Successful.
            Total tests: 1
                 Passed: 1
              Total time: 0,3037 Seconds
                Passed Test_One [1 ms]

            Test Run Successful.
            Total tests: 244
                 Passed: 242
                Skipped: 2
              Total time: 0,3706 Seconds
                Passed Test_Two [3 ms]

            Test Run Successful.
            Total tests: 9
                 Passed: 9
              Total time: 0,7794 Seconds
                Passed Test_Three [2 ms]
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 0);
        Assert.NotNull(result.Summary);
        Assert.Equal(254, result.Summary!.Total);
        Assert.Equal(252, result.Summary.Passed);
        Assert.Equal(0, result.Summary.Failed);
        Assert.Equal(2, result.Summary.Skipped);
    }

    [Fact]
    public void Parse_total_tests_block_counts_do_not_leak_between_blocks()
    {
        // A small assembly followed by a large one: the next block's "Passed:" line must not be
        // counted into the previous block (window bleed).
        const string output = """
            Test Run Successful.
            Total tests: 1
                 Passed: 1
              Total time: 0,1000 Seconds
            Test run for C:\w\bin\Debug\net8.0\Big.dll (.NETCoreApp,Version=v8.0)
            Test Run Successful.
            Total tests: 100
                 Passed: 100
              Total time: 0,5000 Seconds
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 0);
        Assert.NotNull(result.Summary);
        Assert.Equal(101, result.Summary!.Total);
        Assert.Equal(101, result.Summary.Passed);
        Assert.Equal(0, result.Summary.Failed);
        Assert.Equal(0, result.Summary.Skipped);
    }

    [Fact]
    public void Parse_single_total_tests_block_still_parsed()
    {
        const string output = """
            Test Run Successful.
            Total tests: 5
                 Passed: 4
                Failed: 1
              Total time: 0,2000 Seconds
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 1);
        Assert.NotNull(result.Summary);
        Assert.Equal(5, result.Summary!.Total);
        Assert.Equal(4, result.Summary.Passed);
        Assert.Equal(1, result.Summary.Failed);
        Assert.Equal(0, result.Summary.Skipped);
    }

    [Fact]
    public void Parse_single_assembly_summary_still_parsed()
    {
        const string output = """
            Passed!  - Failed:     0, Passed:     3, Skipped:     1, Total:     4, Duration: 120 ms - A.dll (net8.0)
            """;
        var result = VstestOutputParser.Parse(output, exitCode: 0);
        Assert.NotNull(result.Summary);
        Assert.Equal(4, result.Summary!.Total);
        Assert.Equal(3, result.Summary.Passed);
        Assert.Equal(0, result.Summary.Failed);
        Assert.Equal(1, result.Summary.Skipped);
    }

    [Fact]
    public void DeduplicateNuGetAuditLines_removes_repeated_warning()
    {
        const string line = "warning NU1904: Package 'X' has a known vulnerability";
        var text = line + "\n" + line + "\nDone.";
        var deduped = VstestOutputParser.DeduplicateNuGetAuditLines(text);
        Assert.Equal(2, deduped.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void BuildMarkdownReport_includes_partial_status()
    {
        var parse = VstestOutputParser.Parse("noise only", 0);
        var md = VstestOutputParser.BuildMarkdownReport(parse, 0, "noise only", null, null, false);
        Assert.Contains("**Status:** partial", md, StringComparison.Ordinal);
        Assert.Contains("Tests completed (exit 0)", md, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMarkdownReport_no_matching_filter_emits_agent_signal()
    {
        const string output = """
            Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1
              Passed Other.Namespace.OtherTests.Other [1 ms]
            """;
        var parse = VstestOutputParser.Parse(output, 0);
        var md = VstestOutputParser.BuildMarkdownReport(
            parse,
            0,
            output,
            "FullyQualifiedName~.MissingTests.MissingMethod",
            "Name suffix `.MissingTests.MissingMethod`",
            requireFilterMatch: true);

        Assert.Contains("## Filtered test run — no matching tests", md, StringComparison.Ordinal);
        Assert.Contains("**Agent signal:**", md, StringComparison.Ordinal);
        Assert.Contains("**Match mode:** Name suffix", md, StringComparison.Ordinal);
    }
}
