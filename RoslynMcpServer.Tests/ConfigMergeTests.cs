using Microsoft.Extensions.Configuration;
using RoslynMcpServer.Config;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Config merge (plan §2.1 / §7 «ConfigMergeTests»): <c>RoslynMcp.jsonc</c> next to the exe + in cwd, the later
/// file (cwd) wins — the same <see cref="ConfigurationBuilder"/>/AddJsonFile mechanics as <c>Program.cs</c>
/// (exe first, then cwd; <c>optional: false</c> + <c>reloadOnChange: false</c> only when the file exists).
/// Plus <see cref="WorkspaceConfig"/> key parsing: flat kebab-case keys, defaults (max-results 50, preview
/// false), empty/whitespace string → null, and <c>.jsonc</c> comments.
/// </summary>
public sealed class ConfigMergeTests : IDisposable
{
    private readonly string _root;

    public ConfigMergeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RoslynMcpConfigMergeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public void Merge_cwd_overrides_exe_and_exe_only_keys_are_kept()
    {
        var exe = WriteConfig("exe.jsonc", """
            {
              "workspace-path": "F:/exe/Exe.sln",
              "configuration": "Debug",
              "platform": "AnyCPU",
              "max-results": 10
            }
            """);
        var cwd = WriteConfig("cwd.jsonc", """
            {
              "workspace-path": "F:/cwd/Cwd.sln",
              "configuration": "Sit-Debug",
              "preview": true
            }
            """);

        var config = BuildMergedConfig(exe, cwd);

        Assert.Equal("F:/cwd/Cwd.sln", config.WorkspacePath); // cwd wins
        Assert.Equal("Sit-Debug", config.Configuration); // cwd wins
        Assert.Equal("AnyCPU", config.Platform); // exe only
        Assert.Equal(10, config.MaxResults); // exe only
        Assert.True(config.Preview); // cwd only
        Assert.Null(config.TargetFramework);
    }

    [Fact]
    public void Merge_only_exe_file_provides_values()
    {
        var exe = WriteConfig("exe.jsonc", """
            {
              "workspace-path": "F:/exe/Exe.sln",
              "max-results": 25
            }
            """);

        var config = BuildMergedConfig(exe, Path.Combine(_root, "missing-cwd.jsonc"));

        Assert.Equal("F:/exe/Exe.sln", config.WorkspacePath);
        Assert.Equal(25, config.MaxResults);
        Assert.False(config.Preview);
    }

    [Fact]
    public void Merge_only_cwd_file_provides_values()
    {
        var cwd = WriteConfig("cwd.jsonc", """
            {
              "workspace-path": "F:/cwd/Cwd.sln",
              "preview": true
            }
            """);

        var config = BuildMergedConfig(Path.Combine(_root, "missing-exe.jsonc"), cwd);

        Assert.Equal("F:/cwd/Cwd.sln", config.WorkspacePath);
        Assert.True(config.Preview);
        Assert.Equal(WorkspaceConfig.DefaultMaxResults, config.MaxResults);
    }

    [Fact]
    public void No_config_files_gives_defaults()
    {
        var config = BuildMergedConfig(Path.Combine(_root, "missing-exe.jsonc"), Path.Combine(_root, "missing-cwd.jsonc"));

        Assert.Null(config.WorkspacePath);
        Assert.Null(config.Configuration);
        Assert.Null(config.Platform);
        Assert.Null(config.TargetFramework);
        Assert.Equal(WorkspaceConfig.DefaultMaxResults, config.MaxResults);
        Assert.False(config.Preview);
        Assert.Empty(config.LoadedFromPaths);
    }

    [Fact]
    public void WorkspaceConfig_parses_all_keys_and_jsonc_comments()
    {
        var path = WriteConfig("full.jsonc", """
            // RoslynMcp.jsonc — full key set with comments
            {
              // Workspace: path + MSBuild global properties
              "workspace-path": "F:/repos/product/Solution.sln",
              "configuration": "Sit-Debug",
              "platform": "AnyCPU",
              "target-framework": "net10.0",
              // Search output
              "max-results": 25,
              "preview": true
            }
            """);

        var config = new WorkspaceConfig(
            new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build());

        Assert.Equal("F:/repos/product/Solution.sln", config.WorkspacePath);
        Assert.Equal("Sit-Debug", config.Configuration);
        Assert.Equal("AnyCPU", config.Platform);
        Assert.Equal("net10.0", config.TargetFramework);
        Assert.Equal(25, config.MaxResults);
        Assert.True(config.Preview);
    }

    [Fact]
    public void WorkspaceConfig_empty_or_whitespace_string_becomes_null()
    {
        var path = WriteConfig("empty.jsonc", """
            {
              "workspace-path": "",
              "configuration": "   ",
              "platform": ""
            }
            """);

        var config = new WorkspaceConfig(
            new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build());

        Assert.Null(config.WorkspacePath);
        Assert.Null(config.Configuration);
        Assert.Null(config.Platform);
        Assert.Equal(WorkspaceConfig.DefaultMaxResults, config.MaxResults);
        Assert.False(config.Preview);
    }

    private string WriteConfig(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Mirrors <c>Program.cs</c>: exe config first, then cwd; the later file overrides the earlier one.</summary>
    private static WorkspaceConfig BuildMergedConfig(string exePath, string cwdPath)
    {
        var builder = new ConfigurationBuilder();
        if (File.Exists(exePath))
        {
            builder.AddJsonFile(exePath, optional: false, reloadOnChange: false);
        }

        if (File.Exists(cwdPath))
        {
            builder.AddJsonFile(cwdPath, optional: false, reloadOnChange: false);
        }

        return new WorkspaceConfig(builder.Build());
    }
}
