using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Config;
using RoslynMcpServer.Diagnostics;
using RoslynMcpServer.Hosting;
using RoslynMcpServer.Tools;
using Serilog;
using System.Reflection;

// MSBuild path must match process bitness (64-bit MCP + x86 SDK → BadImageFormatException).
MsBuildBootstrapper.Register();

// Force-load C# language / workspace assemblies before any Roslyn workspace use
try
{
    // CSharp.Workspaces + Microsoft.CodeAnalysis.CSharp (LanguageNames lives in Workspaces, not CSharp.*)
    _ = typeof(CSharpFormattingOptions).Assembly;
    _ = typeof(SyntaxFactory).Assembly;
    _ = Assembly.Load(new AssemblyName("Microsoft.CodeAnalysis.Features"));
    _ = Assembly.Load(new AssemblyName("Microsoft.CodeAnalysis.CSharp.Features"));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ERR] Failed to force load C# services: {ex.Message}");
}

// Env ROSLYN_MCP_WORKSPACE stays as a secondary mechanism: SDK pin for MSBuild.Locator and the
// search root for search_code. The primary way to specify the workspace is the RoslynMcp.jsonc
// config (`workspace-path` key, merged below).
// Do not set CurrentDirectory to AppContext.BaseDirectory: that points at bin/Release/.../win-x64 and breaks
// tools that default to Environment.CurrentDirectory (SearchCode, etc.).
// `dotnet run` normally keeps cwd at the project folder; published/self-contained runs may differ — use env below.
ApplyOptionalWorkspaceRootFromEnvironment();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();

// Config merge (1:1 with TfsMcp/MCP): RoslynMcp.jsonc next to the exe + in cwd; the later (cwd)
// file overrides the earlier one. `optional: false` only when the file exists.
var exeConfigPath = Path.Combine(AppContext.BaseDirectory, "RoslynMcp.jsonc");
var projectConfigPath = Path.Combine(Directory.GetCurrentDirectory(), "RoslynMcp.jsonc");

var exeConfigLoaded = File.Exists(exeConfigPath);
var projectConfigLoaded = File.Exists(projectConfigPath);
var hasAnyConfig = exeConfigLoaded || projectConfigLoaded;

// Remember which config files were actually loaded (for get_mcp_server_info diagnostics).
var loadedFromPaths = new List<string>();
if (exeConfigLoaded)
{
    builder.Configuration.AddJsonFile(exeConfigPath, optional: false, reloadOnChange: false);
    loadedFromPaths.Add(exeConfigPath);
}
if (projectConfigLoaded)
{
    builder.Configuration.AddJsonFile(projectConfigPath, optional: false, reloadOnChange: false);
    loadedFromPaths.Add(projectConfigPath);
}

var workspaceConfig = new WorkspaceConfig(builder.Configuration, loadedFromPaths);
builder.Services.AddSingleton(workspaceConfig);

var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "mcp-.log");
builder.Services.AddSerilog((_, configuration) =>
{
    configuration
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.File(
            path: logPath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true);
});

// MCP stdio transport + tools (see RoslynMcpServiceCollectionExtensions).
builder.Services
    .AddRoslynMcpServerTools()
    .WithPrompts<BasicPrompts>();

var host = builder.Build();

// Startup log: which config files were found (pattern MCP\Program.cs:67-76).
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

if (exeConfigLoaded)
    startupLogger.LogInformation("Config loaded: {Path}", exeConfigPath);

if (projectConfigLoaded)
    startupLogger.LogInformation("Config loaded: {Path}", projectConfigPath);

if (!hasAnyConfig)
    startupLogger.LogWarning("No config files found (RoslynMcp.jsonc neither next to the executable nor in the current directory)");

startupLogger.LogInformation(
    "Config: workspace-path={WorkspacePath}, configuration={Configuration}, platform={Platform}, target-framework={TargetFramework}, max-results={MaxResults}, preview={Preview}",
    workspaceConfig.WorkspacePath, workspaceConfig.Configuration, workspaceConfig.Platform,
    workspaceConfig.TargetFramework, workspaceConfig.MaxResults, workspaceConfig.Preview);

await host.RunAsync();

static void ApplyOptionalWorkspaceRootFromEnvironment()
{
    var raw = Environment.GetEnvironmentVariable("ROSLYN_MCP_WORKSPACE");
    if (string.IsNullOrWhiteSpace(raw))
    {
        return;
    }

    try
    {
        var full = Path.GetFullPath(raw.Trim());
        if (Directory.Exists(full))
        {
            Directory.SetCurrentDirectory(full);
            Console.Error.WriteLine($"[RoslynMcp] ROSLYN_MCP_WORKSPACE: cwd = {full}");
        }
        else
        {
            Console.Error.WriteLine($"[RoslynMcp] WARN: ROSLYN_MCP_WORKSPACE directory not found: {full}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[RoslynMcp] WARN: ROSLYN_MCP_WORKSPACE invalid ({raw}): {ex.Message}");
    }
}
