using System.ComponentModel;
using ModelContextProtocol.Server;

internal sealed class BasicPrompts
{
    [McpServerPrompt]
    [Description("Returns a compact prompt for C# refactoring tasks.")]
    public string RefactoringAssistantPrompt(
        [Description("Optional task focus")] string? focus = null)
    {
        if (string.IsNullOrWhiteSpace(focus))
        {
            return @"### RULE: NO MANUAL INSTRUCTIONS
1. YOU ARE AN AUTONOMOUS SYSTEM. NEVER write manual instructions like 'Open this file and add this code'. Make all changes yourself using the host tools (read/write/edit/bash) for full-file reads, writes, and edits.
2. When editing XML files (like .csproj) with the host write/edit tools, be EXTREMELY careful with JSON/XML escaping. Ensure all quotes are properly escaped. Do not truncate the file.
3. If a build fails due to missing references, DO NOT rewrite C# files. Use `add_package_reference` or edit the `.csproj` with the host write/edit tools after verifying packages via `search_nuget_registry`.
4. For C# insertions use `add_member` (method/property/field) — not a manual host patch. For method bodies use `get_method_body` + `update_method_body`. For bug context use `get_call_graph`. Verify server binary with `get_mcp_server_info` after publish.";
        }

        return $"You are a senior C# refactoring assistant. Focus area: {focus}. Preserve behavior, keep changes minimal, and prioritize compile-safe updates.";
    }
}
