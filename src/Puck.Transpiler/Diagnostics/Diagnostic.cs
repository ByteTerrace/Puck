using System.Globalization;

namespace Puck.World.Transpiler.Diagnostics;

/// <summary>Represents a compiler diagnostic message with location and severity.</summary>
/// <param name="Code">The unique diagnostic identifier (e.g. PUCK001).</param>
/// <param name="Message">The human-readable diagnostic message.</param>
/// <param name="Severity">The severity level (Error, Warning, Information).</param>
/// <param name="Span">The source span where the diagnostic applies.</param>
public sealed record Diagnostic(string Code, string Message, DiagnosticSeverity Severity, SourceSpan Span) {
    /// <summary>Creates an error diagnostic.</summary>
    public static Diagnostic Error(string code, string message, SourceSpan span) =>
        new(code, message, DiagnosticSeverity.Error, span);

    /// <summary>Creates a warning diagnostic.</summary>
    public static Diagnostic Warning(string code, string message, SourceSpan span) =>
        new(code, message, DiagnosticSeverity.Warning, span);

    /// <summary>Creates an informational diagnostic.</summary>
    public static Diagnostic Info(string code, string message, SourceSpan span) =>
        new(code, message, DiagnosticSeverity.Information, span);

    /// <summary>Formats the diagnostic in standard compiler format: "file(line,col): severity code: message".</summary>
    public string Format(string? filePath = null) {
        var prefix = string.IsNullOrEmpty(filePath) ? "" : $"{filePath}";
        var loc = Span.Line > 0 ? $"({Span.Line},{Span.Column})" : "";
        var sev = Severity.ToString().ToLowerInvariant();
        return string.IsNullOrEmpty(prefix)
            ? string.Format(CultureInfo.InvariantCulture, "{0}: {1} {2}: {3}", loc, sev, Code, Message).TrimStart(':', ' ')
            : string.Format(CultureInfo.InvariantCulture, "{0}{1}: {2} {3}: {4}", prefix, loc, sev, Code, Message);
    }

    /// <inheritdoc/>
    public override string ToString() => Format();
}
