using Microsoft.Build.Locator;
using RoslynMcpServer.Diagnostics;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Process-wide MSBuild registration guard for tests that load a real <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/>.
/// <see cref="MSBuildLocator"/> throws on a second registration, and xunit runs test classes in parallel, so every
/// fixture must go through this single lock. Uses the same entry point <c>Program.cs</c> uses at startup
/// (<see cref="MsBuildBootstrapper.Register"/>).
/// </summary>
internal static class MsBuildTestRegistration
{
    private static readonly object Lock = new();

    public static void EnsureRegistered()
    {
        lock (Lock)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MsBuildBootstrapper.Register();
            }
        }
    }
}
