using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests;

public sealed class TestDiscoveryHelperTests
{
    [Fact]
    public async Task ListTestsJson_FactMethod_FindsIt()
    {
        using var workspace = new AdhocWorkspace();
        var coreReference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "P1", "P1", LanguageNames.CSharp));
        project = project.WithMetadataReferences(new[] { coreReference });

        // ListTestsJsonAsync skips documents whose FilePath is null; AdhocWorkspace only sets FilePath
        // when the DocumentId carries a file Uri (a bare name via AddDocument leaves it empty).
        var filePath = Path.Combine(Path.GetTempPath(), "TestDiscoveryHelperTests.cs");
        var document = project.AddDocument("Tests.cs", SourceText.From(
            """
            using System;

            [AttributeUsage(AttributeTargets.Method)]
            public class FactAttribute : Attribute { }

            public class MyTests
            {
                [Fact]
                public void DoesThing() { }

                public void Helper() { }
            }
            """), filePath: filePath);

        if (!workspace.TryApplyChanges(document.Project.Solution))
        {
            throw new InvalidOperationException("TryApplyChanges failed.");
        }

        var json = await TestDiscoveryHelper.ListTestsJsonAsync(workspace.CurrentSolution, 200, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var count = doc.RootElement.GetProperty("count").GetInt32();

        Assert.Equal(1, count);
        var test = doc.RootElement.GetProperty("tests")[0];
        Assert.Equal("DoesThing", test.GetProperty("methodName").GetString());
        Assert.Equal("MyTests", test.GetProperty("className").GetString());
        // FQN must be fully qualified (Namespace.Class.Method), not just the method name.
        Assert.Equal("MyTests.DoesThing", test.GetProperty("fullyQualifiedName").GetString());
    }

    [Fact]
    public async Task ListTestsJson_namespacedClass_fullyQualifiedNameIncludesNamespace()
    {
        using var workspace = new AdhocWorkspace();
        var coreReference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "P1", "P1", LanguageNames.CSharp));
        project = project.WithMetadataReferences(new[] { coreReference });

        var filePath = Path.Combine(Path.GetTempPath(), "TestDiscoveryHelperNamespaced.cs");
        var document = project.AddDocument("Namespaced.cs", SourceText.From(
            """
            using System;

            namespace Acme.Tests;

            [AttributeUsage(AttributeTargets.Method)]
            public class FactAttribute : Attribute { }

            public class WidgetTests
            {
                [Fact]
                public void Parses() { }
            }
            """), filePath: filePath);

        if (!workspace.TryApplyChanges(document.Project.Solution))
        {
            throw new InvalidOperationException("TryApplyChanges failed.");
        }

        var json = await TestDiscoveryHelper.ListTestsJsonAsync(workspace.CurrentSolution, 200, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var test = doc.RootElement.GetProperty("tests")[0];

        Assert.Equal("Parses", test.GetProperty("methodName").GetString());
        Assert.Equal("Acme.Tests.WidgetTests.Parses", test.GetProperty("fullyQualifiedName").GetString());
    }
}
