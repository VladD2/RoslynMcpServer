using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Config;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tools;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// v1.3.1: a mixed C++/C# solution must not produce a false "Workspace Load Failed" — a <c>.vcxproj</c>
/// entry yields the non-blocking "Cannot open project ... because the file extension '.vcxproj' is not
/// associated with a language" diagnostic, the C# project still loads, and the response is a success with
/// the diagnostic visible in the (capped) diagnostics section. Reproduces the kav GuiTestAppBl.sln bug
/// without the monorepo. Slow (real MSBuild): one project tree per class (<c>IClassFixture</c>), the same
/// pattern as <see cref="WorkspaceToolsRestoreTests"/>.
/// </summary>
public sealed class WorkspaceMixedProjectLoadTests : IClassFixture<WorkspaceMixedProjectLoadTests.MixedFixture>
{
    private readonly MixedFixture _fixture;

    public WorkspaceMixedProjectLoadTests(MixedFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LoadWorkspace_mixed_solution_with_vcxproj_reports_success_not_failure()
    {
        var config = _fixture.CreateConfig(workspacePath: null);
        var manager = new SolutionManager(NullLogger<SolutionManager>.Instance, config);
        try
        {
            var tools = new WorkspaceTools(manager, config, NullLogger<WorkspaceTools>.Instance);

            var result = await tools.LoadWorkspace(_fixture.SolutionPath);

            Assert.Contains("Successfully loaded workspace", result, StringComparison.Ordinal);
            Assert.DoesNotContain("Workspace Load Failed", result, StringComparison.Ordinal);
            Assert.Contains("- App [Library]", result, StringComparison.Ordinal);
            // Locale-independent: the diagnostic sentence is localized by the MSBuild culture, but the
            // offending project path is embedded verbatim in every culture.
            Assert.Contains("Native.vcxproj", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.ClearWorkspaceAsync();
        }
    }

    /// <summary>
    /// One temporary solution: <c>App.sln</c> with a real SDK <c>App/App.csproj</c> (net10.0) and a fake
    /// C++ <c>Native/Native.vcxproj</c> (empty text file — only the extension matters: the C++ project type
    /// is not registered in a .NET-hosted MSBuild, so the content is never read).
    /// </summary>
    public sealed class MixedFixture : IDisposable
    {
        private const string CsprojTemplate = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """;

        // Project type GUIDs from a regular Visual Studio .sln: C# (SDK-style) and C++ (Win32).
        private const string CSharpProjectTypeGuid = "9A19103F-16F7-4668-BE54-9A1E7A4F7556";

        private const string CppProjectTypeGuid = "8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942";

        private const string AppProjectGuid = "11111111-2222-3333-4444-555555555555";

        private const string NativeProjectGuid = "66666666-7777-8888-9999-000000000000";

        public string Root { get; }

        public string SolutionPath { get; }

        public MixedFixture()
        {
            MsBuildTestRegistration.EnsureRegistered();

            Root = Path.Combine(Path.GetTempPath(), "RoslynMcpMixedWorkspaceTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "App"));
            Directory.CreateDirectory(Path.Combine(Root, "Native"));
            SolutionPath = Path.Combine(Root, "App.sln");

            File.WriteAllText(Path.Combine(Root, "App", "App.csproj"), CsprojTemplate);
            File.WriteAllText(Path.Combine(Root, "App", "A.cs"), """
                namespace AppNs
                {
                    public class AppThing
                    {
                    }
                }
                """);

            // A fake C++ project: MSBuildWorkspace rejects it by extension, before reading the content.
            File.WriteAllText(Path.Combine(Root, "Native", "Native.vcxproj"), string.Empty);

            File.WriteAllText(SolutionPath, $"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                VisualStudioVersion = 17.0.31903.59
                MinimumVisualStudioVersion = 10.0.40219.1
                Project("{CSharpProjectTypeGuid}") = "App", "App\App.csproj", "{AppProjectGuid}"
                EndProject
                Project("{CppProjectTypeGuid}") = "Native", "Native\Native.vcxproj", "{NativeProjectGuid}"
                EndProject
                Global
                    GlobalSection(SolutionConfigurationPlatforms) = preSolution
                        Debug|Any CPU = Debug|Any CPU
                        Release|Any CPU = Release|Any CPU
                    EndGlobalSection
                    GlobalSection(ProjectConfigurationPlatforms) = postSolution
                        {AppProjectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                        {AppProjectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU
                        {AppProjectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU
                        {AppProjectGuid}.Release|Any CPU.Build.0 = Release|Any CPU
                        {NativeProjectGuid}.Debug|Any CPU.ActiveCfg = Debug|Win32
                        {NativeProjectGuid}.Debug|Any CPU.Build.0 = Debug|Win32
                        {NativeProjectGuid}.Release|Any CPU.ActiveCfg = Release|Win32
                        {NativeProjectGuid}.Release|Any CPU.Build.0 = Release|Win32
                    EndGlobalSection
                    GlobalSection(SolutionProperties) = preSolution
                        HideSolutionNode = FALSE
                    EndGlobalSection
                EndGlobal
                """);
        }

        public WorkspaceConfig CreateConfig(string? workspacePath)
        {
            var values = new Dictionary<string, string?>();
            if (workspacePath is not null)
            {
                values["workspace-path"] = workspacePath;
            }

            return new WorkspaceConfig(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
