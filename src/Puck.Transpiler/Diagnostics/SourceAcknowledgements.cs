using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Diagnostics;

/// <summary>The <c>allow</c> lines of one compiled source and the sources it imported, each covering the span of the
/// statement written after it (<see cref="StatementNode.Allows"/>). A warning or note whose code an <c>allow</c>
/// names, and whose span starts inside the statement that <c>allow</c> covers in the same file, is acknowledged: the
/// author keeps what it reports on purpose, so it is not reported. An error is never acknowledged.</summary>
public sealed class SourceAcknowledgements {
    private static readonly StringComparer PathComparer = (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly List<Range> m_ranges = [];

    private readonly record struct Range(string? Path, string Code, int Start, int End);

    /// <summary>Gets whether no <c>allow</c> line was found.</summary>
    public bool IsEmpty => (m_ranges.Count == 0);

    private static string? Normalize(string? path) => ((path is null) ? null : Path.GetFullPath(path: path).Replace(oldChar: '\\', newChar: '/'));

    /// <summary>Reads every <c>allow</c> line in <paramref name="document"/>, at any depth.</summary>
    /// <param name="document">The parsed source, with the declarations its imports brought in.</param>
    /// <param name="sourcePath">The file <paramref name="document"/> was parsed from, or <see langword="null"/> for a
    /// buffer; a declaration an import brought in is read against its own <c>DefinitionPath</c>.</param>
    /// <returns>The acknowledgements.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static SourceAcknowledgements Of(DocumentNode document, string? sourcePath) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var acknowledgements = new SourceAcknowledgements();

        acknowledgements.Add(document: document, sourcePath: sourcePath);

        return acknowledgements;
    }
    /// <summary>Adds every <c>allow</c> line in another source's <paramref name="document"/>.</summary>
    /// <param name="document">The parsed source.</param>
    /// <param name="sourcePath">The file <paramref name="document"/> was parsed from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public void Add(DocumentNode document, string? sourcePath) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var root = Normalize(path: sourcePath);

        foreach (var statement in document.Statements) {
            var path = (statement switch {
                LetNode { DefinitionPath: { } defined } => Normalize(path: defined),
                TemplateNode { DefinitionPath: { } defined } => Normalize(path: defined),
                _ => root,
            });

            Walk(node: statement, path: path);
        }
    }
    private void Walk(SyntaxNode node, string? path) {
        if (node is StatementNode { Allows.Count: > 0 } statement) {
            foreach (var allow in statement.Allows) {
                m_ranges.Add(item: new Range(Code: allow.Code, End: (statement.Offset + statement.Length), Path: path, Start: statement.Offset));
            }
        }
        foreach (var child in SyntaxWalk.Children(node: node)) {
            Walk(node: child, path: path);
        }
    }
    /// <summary>Returns whether an <c>allow</c> line acknowledges <paramref name="diagnostic"/>.</summary>
    /// <param name="diagnostic">The diagnostic, its span read in its <see cref="Diagnostic.SourcePath"/> or, when
    /// that is <see langword="null"/>, in <paramref name="rootPath"/>.</param>
    /// <param name="rootPath">The compiled source's own file, or <see langword="null"/> for a buffer.</param>
    /// <returns><see langword="true"/> for a warning or note, with a span, that an <c>allow</c> of its code covers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
    public bool Acknowledges(Diagnostic diagnostic, string? rootPath) {
        ArgumentNullException.ThrowIfNull(argument: diagnostic);

        if ((m_ranges.Count == 0) || (diagnostic.Severity == DiagnosticSeverity.Error) || (diagnostic.Span.Line <= 0)) {
            return false;
        }

        var path = Normalize(path: (diagnostic.SourcePath ?? rootPath));
        var offset = diagnostic.Span.Offset;

        foreach (var range in m_ranges) {
            if (
                string.Equals(a: range.Code, b: diagnostic.Code, comparisonType: StringComparison.Ordinal) &&
                ((range.Path is null) ? (path is null) : ((path is not null) && PathComparer.Equals(x: range.Path, y: path))) &&
                (offset >= range.Start) &&
                (offset < range.End)
            ) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Removes from <paramref name="diagnostics"/> every diagnostic an <c>allow</c> line acknowledges.</summary>
    /// <param name="diagnostics">The diagnostics to filter in place.</param>
    /// <param name="rootPath">The compiled source's own file, or <see langword="null"/> for a buffer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is <see langword="null"/>.</exception>
    public void Apply(DiagnosticBag diagnostics, string? rootPath) {
        ArgumentNullException.ThrowIfNull(argument: diagnostics);

        if (m_ranges.Count > 0) {
            diagnostics.RemoveAll(match: diagnostic => Acknowledges(diagnostic: diagnostic, rootPath: rootPath));
        }
    }
}
