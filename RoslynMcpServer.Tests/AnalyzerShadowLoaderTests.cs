using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Unit tests for the pure path-classification helpers of <see cref="AnalyzerShadowLoader"/>: which analyzer
/// DLLs are workspace build outputs (and must be shadow-copied so the original stays writable) versus SDK/NuGet
/// analyzers (outside the workspace, never overwritten by a build, and therefore left alone).
/// </summary>
public sealed class AnalyzerShadowLoaderTests
{
    [Theory]
    [InlineData(@"C:\repo\bin\Debug\netstandard2.0\Gen.dll", @"C:\repo", true)]
    [InlineData(@"C:\repo\Gen.dll", @"C:\repo", true)]
    [InlineData(@"C:\repo\sub\Gen.dll", @"C:\repo", true)]
    [InlineData(@"C:\other\Gen.dll", @"C:\repo", false)]
    [InlineData(@"C:\repo\Gen.dll", @"C:\other", false)]
    [InlineData(@"C:\repo\Gen.dll", @"C:\repo2", false)]
    [InlineData(@"C:\repo", @"C:\repo", false)]
    public void IsUnderDirectory_detects_strict_containment(string path, string directory, bool expected)
    {
        Assert.Equal(expected, AnalyzerShadowLoader.IsUnderDirectory(path, directory));
    }

    [Fact]
    public void IsUnderDirectory_null_or_empty_directory_is_false()
    {
        Assert.False(AnalyzerShadowLoader.IsUnderDirectory(@"C:\repo\Gen.dll", null));
        Assert.False(AnalyzerShadowLoader.IsUnderDirectory(@"C:\repo\Gen.dll", string.Empty));
    }

    [Theory]
    [InlineData(@"C:\repo\bin\Debug\netstandard2.0\Gen.dll", true)]
    [InlineData(@"C:\repo\obj\Debug\Gen.dll", true)]
    [InlineData(@"C:\repo\BIN\Gen.dll", true)]
    [InlineData(@"C:\repo\Obj\Gen.dll", true)]
    [InlineData(@"C:\repo\lib\Gen.dll", false)]
    [InlineData(@"C:\repo\Gen.dll", false)]
    [InlineData(@"C:\repo\binaries\Gen.dll", false)]
    [InlineData(@"C:\repo\mybin\Gen.dll", false)]
    public void IsBuildOutputPath_matches_bin_or_obj_component_exactly(string path, bool expected)
    {
        Assert.Equal(expected, AnalyzerShadowLoader.IsBuildOutputPath(path));
    }
}
