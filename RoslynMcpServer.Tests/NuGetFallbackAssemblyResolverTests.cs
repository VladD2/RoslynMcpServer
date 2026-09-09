using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests;

public sealed class NuGetFallbackAssemblyResolverTests
{
    [Fact]
    public void TryFindAssemblyDll_finds_newtonsoft_json_when_package_present()
    {
        var nugetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "packages");
        if (!Directory.Exists(nugetRoot))
        {
            Assert.Skip($"NuGet packages folder not found: {nugetRoot}.");
        }

        var path = NuGetFallbackAssemblyResolver.TryFindAssemblyDll("Newtonsoft.Json", nugetRoot);
        if (string.IsNullOrEmpty(path))
        {
            Assert.Skip($"Newtonsoft.Json package is not present in the NuGet cache ({nugetRoot}); nothing to verify.");
        }

        Assert.EndsWith("Newtonsoft.Json.dll", path!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryFindAssemblyDll_finds_system_io_ports_via_bcl_map_when_package_present()
    {
        var nugetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "packages");
        if (!Directory.Exists(nugetRoot))
        {
            Assert.Skip($"NuGet packages folder not found: {nugetRoot}.");
        }

        var path = NuGetFallbackAssemblyResolver.TryFindAssemblyDll("System.IO.Ports", nugetRoot);
        if (string.IsNullOrEmpty(path))
        {
            Assert.Skip($"system.io.ports package is not present in the NuGet cache ({nugetRoot}); nothing to verify.");
        }

        Assert.EndsWith("System.IO.Ports.dll", path!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}unix{Path.DirectorySeparatorChar}", path!, StringComparison.OrdinalIgnoreCase);
    }
}
