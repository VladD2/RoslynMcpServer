using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// <c>add_member</c> (plan §1.2 / §7 «AddMemberTests»): <see cref="AstModificationHelper.AddMemberAsync"/> is
/// public and takes a <see cref="Document"/>, so the tests run directly on an in-memory
/// <see cref="AdhocWorkspace"/> (fast, no MSBuild load).
/// Insertion position — verified against the real Roslyn 5.9 <c>DocumentEditor.AddMember</c>: the member is
/// appended after the class's last member (inside the enclosing <c>#region</c> when the last member is inside
/// one); there is no grouping by member kind.
/// </summary>
public sealed class AddMemberTests
{
    // Same MEF host services the production <c>SolutionManager</c> uses, so <c>DocumentEditor</c>
    // (Microsoft.CodeAnalysis.Features) is available in the adhoc workspace.
    private static readonly HostServices HostServices = MefHostServices.Create(new[]
    {
        typeof(Workspace).Assembly,
        typeof(CSharpFormattingOptions).Assembly,
        Assembly.Load(new AssemblyName("Microsoft.CodeAnalysis.Features")),
        Assembly.Load(new AssemblyName("Microsoft.CodeAnalysis.CSharp.Features")),
    });

    // Members deliberately interleave kinds (field, field, property, method, field).
    private const string Source = """
        namespace AddNs
        {
            public class Widget
            {
                private int _count;

                private string _name = string.Empty;

                public string Label { get; set; } = string.Empty;

                public void Spin()
                {
                }

                private bool _dirty;
            }
        }
        """;

    // The class's last member is inside a #region.
    private const string LastMemberInRegionSource = """
        namespace AddNs
        {
            public class Regioned
            {
                #region Fields
                private int _a;
                #endregion

                #region Methods
                public void M1()
                {
                }
                #endregion
            }
        }
        """;

    // A region in the middle, the class's last member outside any region.
    private const string MiddleRegionSource = """
        namespace AddNs
        {
            public class Mid
            {
                #region Fields
                private int _a;
                #endregion

                public void M1()
                {
                }
            }
        }
        """;

    [Fact]
    public async Task AddMember_method_inserted_after_last_member_inside_class()
    {
        var result = await AddMemberAsync(Source, "Widget", "public void Restart() { }");

        var members = GetClassMembers(result, "Widget");
        Assert.Equal(["_count", "_name", "Label", "Spin", "_dirty", "Restart"], members.Select(MemberName).ToArray());
    }

    [Fact]
    public async Task AddMember_field_inserted_after_last_member_inside_class()
    {
        var result = await AddMemberAsync(Source, "Widget", "private int _extra;");

        var members = GetClassMembers(result, "Widget");
        Assert.Equal(6, members.Count);
        var last = Assert.IsType<FieldDeclarationSyntax>(members[^1]);
        Assert.Equal("_extra", last.Declaration.Variables[0].Identifier.Text);
    }

    [Fact]
    public async Task AddMember_property_inserted_after_last_member_inside_class()
    {
        var result = await AddMemberAsync(Source, "Widget", "public int Level { get; set; }");

        // The existing property `Label` sits in the middle of the member list; the new property is still
        // appended after the last member (Roslyn DocumentEditor has no kind-based grouping).
        var members = GetClassMembers(result, "Widget");
        Assert.Equal(6, members.Count);
        var last = Assert.IsType<PropertyDeclarationSyntax>(members[^1]);
        Assert.Equal("Level", last.Identifier.Text);
    }

    [Fact]
    public async Task AddMember_last_member_in_region_inserts_inside_that_region()
    {
        var result = await AddMemberAsync(LastMemberInRegionSource, "Regioned", "private int _b;");

        var regionStart = result.IndexOf("#region Methods", StringComparison.Ordinal);
        var regionEnd = result.IndexOf("#endregion", regionStart, StringComparison.Ordinal);
        var member = result.IndexOf("_b", StringComparison.Ordinal);
        Assert.True(
            regionStart >= 0 && regionEnd > regionStart && member > regionStart && member < regionEnd,
            $"expected `_b` inside the Methods region (before #endregion):\n{result}");
    }

    [Fact]
    public async Task AddMember_last_member_outside_region_inserts_at_end_not_into_region()
    {
        var result = await AddMemberAsync(MiddleRegionSource, "Mid", "private int _b;");

        var regionEnd = result.IndexOf("#endregion", StringComparison.Ordinal);
        var lastMember = result.IndexOf("M1", StringComparison.Ordinal);
        var member = result.IndexOf("_b", StringComparison.Ordinal);
        Assert.True(
            member > lastMember && member > regionEnd,
            $"expected `_b` after the last member, outside the region:\n{result}");
    }

    [Fact]
    public async Task AddMember_unsupported_kind_event_reports_node_type_and_supported_list()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AddMemberAsync(Source, "Widget", "public event EventHandler? Changed;"));

        Assert.Contains("EventFieldDeclarationSyntax", ex.Message);
        Assert.Contains("method, property, or field", ex.Message);
    }

    [Fact]
    public async Task AddMember_unsupported_kind_constructor_reports_node_type_and_supported_list()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AddMemberAsync(Source, "Widget", "public Widget() { }"));

        Assert.Contains("ConstructorDeclarationSyntax", ex.Message);
        Assert.Contains("method, property, or field", ex.Message);
    }

    [Fact]
    public async Task AddMember_unsupported_kind_nested_type_reports_node_type_and_supported_list()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AddMemberAsync(Source, "Widget", "public class Nested { }"));

        Assert.Contains("ClassDeclarationSyntax", ex.Message);
        Assert.Contains("method, property, or field", ex.Message);
    }

    /// <summary>Runs <see cref="AstModificationHelper.AddMemberAsync"/> on an adhoc document and returns the formatted result text.</summary>
    private static async Task<string> AddMemberAsync(string source, string className, string memberSource)
    {
        using var workspace = new AdhocWorkspace(HostServices);
        var projectInfo = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "P", "P", LanguageNames.CSharp);
        var project = workspace.AddProject(projectInfo);
        project = project.WithMetadataReferences(new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        });
        var document = project.AddDocument("A.cs", SourceText.From(source), filePath: "A.cs");
        if (!workspace.TryApplyChanges(document.Project.Solution))
        {
            throw new InvalidOperationException("TryApplyChanges failed for test workspace.");
        }

        var newDocument = await AstModificationHelper.AddMemberAsync(document, className, memberSource, CancellationToken.None);
        var root = await newDocument.GetSyntaxRootAsync()
            ?? throw new InvalidOperationException("Could not obtain the updated syntax tree.");
        return root.ToFullString();
    }

    private static IReadOnlyList<MemberDeclarationSyntax> GetClassMembers(string text, string className)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var classDecl = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.Text == className);
        return classDecl.Members;
    }

    private static string MemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        FieldDeclarationSyntax f => f.Declaration.Variables[0].Identifier.Text,
        _ => member.GetType().Name
    };
}
