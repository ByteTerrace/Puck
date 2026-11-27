using System.Globalization;

namespace Puck.Transpiler.Diagnostics;

/// <summary>A related source location for a diagnostic (for example, the call site that expanded an imported
/// template or the import statement that pulled in a library).</summary>
/// <param name="SourcePath">The source file that owns <paramref name="Span"/>, or <see langword="null"/> for the compilation root.</param>
/// <param name="Span">The source span in that file.</param>
/// <param name="Message">Why this location relates to the diagnostic.</param>
public sealed record DiagnosticRelated(string? SourcePath, SourceSpan Span, string Message);
/// <summary>Represents a compiler diagnostic message with location and severity.</summary>
/// <param name="Code">The unique diagnostic identifier (e.g. PUCK001).</param>
/// <param name="Message">The human-readable diagnostic message.</param>
/// <param name="Severity">The severity level (Error, Warning, Information).</param>
/// <param name="Span">The source span where the diagnostic applies.</param>
public sealed record Diagnostic(string Code, string Message, DiagnosticSeverity Severity, SourceSpan Span) {
    /// <summary>Gets the source that owns <see cref="Span"/> when it differs from the compilation root.</summary>
    public string? SourcePath { get; init; }
    /// <summary>Gets an optional related location (such as the use site in the importing file) that led to this diagnostic.</summary>
    public DiagnosticRelated? Related { get; init; }

    /// <inheritdoc/>
    public bool Equals(Diagnostic? other) => (
        (other is not null) &&
        string.Equals(a: Code, b: other.Code, comparisonType: StringComparison.Ordinal) &&
        string.Equals(a: Message, b: other.Message, comparisonType: StringComparison.Ordinal) &&
        (Severity == other.Severity) &&
        (Span == other.Span) &&
        string.Equals(a: SourcePath, b: other.SourcePath, comparisonType: StringComparison.Ordinal)
    );
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        value1: StringComparer.Ordinal.GetHashCode(obj: Code),
        value2: StringComparer.Ordinal.GetHashCode(obj: Message),
        value3: Severity,
        value4: Span,
        value5: ((SourcePath is null) ? 0 : StringComparer.Ordinal.GetHashCode(obj: SourcePath))
    );

    /// <summary>Creates an error diagnostic.</summary>
    public static Diagnostic Error(string code, string message, SourceSpan span) =>
        new(
            Code: code,
            Message: message,
            Severity: DiagnosticSeverity.Error,
            Span: span
        );
    /// <summary>Formats the diagnostic in standard compiler format: "file(line,col): severity code: message".</summary>
    public string Format(string? filePath = null) {
        filePath = (SourcePath ?? filePath);
        var prefix = (string.IsNullOrEmpty(value: filePath)
            ? ""
            : $"{filePath}"
        );
        var loc = ((Span.Line > 0)
            ? $"({Span.Line},{Span.Column})"
            : ""
        );
        var sev = Severity.ToString().ToLowerInvariant();

        return (string.IsNullOrEmpty(value: prefix)
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1} {2}: {3}",
                loc,
                sev,
                Code,
                Message
            ).TrimStart(
                ':',
                ' '
            )
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0}{1}: {2} {3}: {4}",
                prefix,
                loc,
                sev,
                Code,
                Message
            )
        );
    }
    /// <summary>Lists the errors among <paramref name="diagnostics"/>, one indented line each with its file, line and
    /// column, so a message about a source that failed can carry what to fix instead of telling the author to go
    /// and look.</summary>
    /// <param name="diagnostics">The diagnostics of the source.</param>
    /// <param name="filePath">The source's path, as the author would open it.</param>
    /// <param name="limit">The most errors listed; the rest are counted.</param>
    /// <returns>The lines, joined by newlines; empty when there is no error.</returns>
    public static string Summarize(IEnumerable<Diagnostic> diagnostics, string? filePath = null, int limit = 3) {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var errors = diagnostics.Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)).ToArray();
        var text = string.Join(separator: "\n", values: errors.Take(count: limit).Select(selector: diagnostic => $"  {diagnostic.Format(filePath: filePath)}"));

        return ((errors.Length > limit)
            ? $"{text}\n  and {(errors.Length - limit).ToString(provider: CultureInfo.InvariantCulture)} more in the same source."
            : text
        );
    }
    /// <summary>Creates an informational diagnostic.</summary>
    public static Diagnostic Info(string code, string message, SourceSpan span) =>
        new(
            Code: code,
            Message: message,
            Severity: DiagnosticSeverity.Information,
            Span: span
        );
    /// <inheritdoc/>
    public override string ToString() => Format();
    /// <summary>Creates a warning diagnostic.</summary>
    public static Diagnostic Warning(string code, string message, SourceSpan span) =>
        new(
            Code: code,
            Message: message,
            Severity: DiagnosticSeverity.Warning,
            Span: span
        );
}
