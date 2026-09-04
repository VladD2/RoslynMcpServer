using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpServer.Services;

/// <summary>
/// LSP-style position helpers: converts a 1-based line/column position (same as <c>get_code_fixes</c> /
/// <c>get_diagnostics_for_file</c> output) into a zero-based text offset and resolves the symbol at a position.
/// </summary>
public static class SourcePositionHelper
{
    /// <summary>
    /// Resolves the symbol at a zero-based <paramref name="offset"/>: the referenced symbol when the position is on
    /// a usage, the declared symbol when the position is on a declaration name. Returns null when no symbol is
    /// found at the position.
    /// </summary>
    public static ISymbol? GetSymbolAtPosition(
        SyntaxNode root,
        SemanticModel semanticModel,
        int offset,
        CancellationToken cancellationToken)
    {
        var token = root.FindToken(offset);
        var parent = token.Parent;
        if (parent is null)
        {
            return null;
        }

        var symbol = semanticModel.GetSymbolInfo(parent, cancellationToken).Symbol;
        if (symbol is not null)
        {
            return symbol;
        }

        if (parent is MemberDeclarationSyntax member)
        {
            symbol = semanticModel.GetDeclaredSymbol(member, cancellationToken);
            if (symbol is not null)
            {
                return symbol;
            }
        }

        if (parent is VariableDeclaratorSyntax variable)
        {
            return semanticModel.GetDeclaredSymbol(variable, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Converts 1-based <paramref name="line"/>/<paramref name="column"/> into a zero-based offset in
    /// <paramref name="text"/>. Returns an error message (and offset 0) when the position is out of range.
    /// </summary>
    public static (int Offset, string? Error) ToOffset(SourceText text, int line, int column)
    {
        if (line < 1)
        {
            return (0, $"`line` must be >= 1 (got {line}).");
        }

        if (column < 1)
        {
            return (0, $"`column` must be >= 1 (got {column}).");
        }

        if (line > text.Lines.Count)
        {
            return (0, $"`line` {line} is out of range (file has {text.Lines.Count} lines).");
        }

        var lineText = text.Lines[line - 1];
        var end = lineText.End;
        // TextLine.End includes the line-break sequence; exclude it so the visible line length is reported.
        if (end > lineText.Start && text[end - 1] == '\n')
        {
            end--;
        }

        if (end > lineText.Start && text[end - 1] == '\r')
        {
            end--;
        }

        var lineLength = end - lineText.Start;
        if (column - 1 > lineLength)
        {
            return (0, $"`column` {column} is out of range (line {line} has {lineLength} characters).");
        }

        return (lineText.Start + (column - 1), null);
    }
}
