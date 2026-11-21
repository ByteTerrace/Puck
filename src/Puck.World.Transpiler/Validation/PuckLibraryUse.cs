using Puck.Abstractions;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Validation;

/// <summary>Which declarations of the module libraries in a set of sources some source of the set reads: a library's
/// top-level <c>let</c>s and templates are for its importers, so <see cref="PuckLinter.Lint"/> never reports one
/// unused inside the library, and a directory lint reports the ones no source of the directory reads
/// (<see cref="PuckDiagnosticCodes.LintUnusedLet"/>).</summary>
public sealed class PuckLibraryUse {
    // Keyed by the library's path key (PuckPaths.Key), so two spellings of one file are one library.
    private readonly Dictionary<(string Library, string Name), (string Path, SourceSpan Span, bool Template)> m_declared = [];
    private readonly HashSet<(string Library, string Name)> m_read = [];

    /// <summary>Adds one source: a library's own declarations, and every library declaration the source reads, its own
    /// declarations reading one another included.</summary>
    /// <param name="document">The parsed source with the declarations its imports brought in.</param>
    /// <param name="sourcePath">The file the source came from.</param>
    /// <param name="library">Whether the source is a module library.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="sourcePath"/> is
    /// <see langword="null"/>.</exception>
    public void Add(DocumentNode document, string sourcePath, bool library) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: sourcePath);

        var own = PuckPaths.Key(path: sourcePath);
        var read = PuckLinter.ReferencedNames(document: document);

        foreach (var statement in document.Statements) {
            var (name, template, definedIn) = statement switch {
                LetNode let => (let.Name, false, let.DefinitionPath),
                TemplateNode declared => (declared.Name, true, declared.DefinitionPath),
                _ => ((string?)null, false, null),
            };

            if (name is null) {
                continue;
            }
            var key = ((definedIn is null) ? own : PuckPaths.Key(path: definedIn));

            // An aliased import names a declaration `alias.name`; the library declares the last segment.
            if (read.Contains(item: name)) {
                _ = m_read.Add(item: (key, QualifiedName.Parse(text: name).Last));
            }
            if (library && (definedIn is null)) {
                _ = m_declared.TryAdd(key: (key, name), value: (PuckPaths.Normalize(path: sourcePath), statement.Span, template));
            }
        }
    }
    /// <summary>Returns a finding for each library declaration no added source reads, against the library that
    /// declares it.</summary>
    /// <returns>The findings, by library and declaration.</returns>
    public IEnumerable<Diagnostic> Unused() =>
        m_declared
            .Where(predicate: entry => !m_read.Contains(item: entry.Key))
            .OrderBy(keySelector: static entry => entry.Key.Library, comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static entry => entry.Value.Span.Offset)
            .Select(selector: static entry => new Diagnostic(
                Code: PuckDiagnosticCodes.LintUnusedLet,
                Message: (entry.Value.Template
                    ? $"Template '{entry.Key.Name}' is declared by this library but no source linted with it instantiates it"
                    : $"Constant '{entry.Key.Name}' is declared by this library but no source linted with it reads it"),
                Severity: DiagnosticSeverity.Information,
                Span: entry.Value.Span
            ) { SourcePath = entry.Value.Path });
}
