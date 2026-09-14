using System.Collections;
using System.Globalization;
using System.Text;

namespace Puck.Transpiler.Diagnostics;

/// <summary>Accumulates diagnostics across compilation stages and formats high-fidelity compiler output.</summary>
public sealed class DiagnosticBag : IReadOnlyList<Diagnostic> {
    private readonly List<Diagnostic> m_diagnostics = [];

    /// <summary>Gets the count of diagnostics in the bag.</summary>
    public int Count => m_diagnostics.Count;
    /// <summary>Gets whether any error-level diagnostics have been reported.</summary>
    public bool HasErrors => m_diagnostics.Exists(match: d => (d.Severity == DiagnosticSeverity.Error));
    /// <summary>Gets whether any warning-level diagnostics have been reported.</summary>
    public bool HasWarnings => m_diagnostics.Exists(match: d => (d.Severity == DiagnosticSeverity.Warning));

    /// <summary>Gets the diagnostic at the specified index.</summary>
    public Diagnostic this[int index] => m_diagnostics[index];

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Adds a range of diagnostics to the bag.</summary>
    public void AddRange(IEnumerable<Diagnostic> diagnostics) {
        ArgumentNullException.ThrowIfNull(diagnostics);
        m_diagnostics.AddRange(collection: diagnostics);
    }
    /// <summary>Renders all diagnostics into a formatted multi-line string with source context excerpts.</summary>
    /// <param name="sourceText">Optional full source text for extracting context lines and squiggles.</param>
    /// <param name="filePath">Optional file path to display in diagnostic headings.</param>
    /// <returns>Formatted compiler report.</returns>
    public string FormatReport(string? sourceText = null, string? filePath = null) {
        if (m_diagnostics.Count == 0) {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var lines = ((sourceText is not null)
            ? sourceText.Split(
                options: StringSplitOptions.None,
                separator: ["\r\n", "\r", "\n"]
            )
            : null
        );

        for (var i = 0; (i < m_diagnostics.Count); i++) {
            var diag = m_diagnostics[i];

            sb.AppendLine(value: diag.Format(filePath: filePath));

            if (
                (lines is not null) &&
                (diag.Span.Line > 0) &&
                (diag.Span.Line <= lines.Length)
            ) {
                var lineIdx = (diag.Span.Line - 1);
                var sourceLine = lines[lineIdx];
                var lineNumStr = diag.Span.Line.ToString(provider: CultureInfo.InvariantCulture);
                var padding = new string(
                    c: ' ',
                    count: lineNumStr.Length
                );

                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {lineNumStr} | {sourceLine}"
                );

                var col = Math.Max(
                    val1: 1,
                    val2: diag.Span.Column
                );
                var colIndent = new string(
                    c: ' ',
                    count: (col - 1)
                );
                var length = Math.Max(
                    val1: 1,
                    val2: Math.Min(
                        val1: diag.Span.Length,
                        val2: Math.Max(
                            val1: 1,
                            val2: ((sourceLine.Length - col) + 1)
                        )
                    )
                );
                var squiggles = new string(
                    c: '^',
                    count: length
                );

                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {padding} | {colIndent}{squiggles}"
                );
            }
        }

        return sb.ToString();
    }
    /// <inheritdoc/>
    public IEnumerator<Diagnostic> GetEnumerator() => m_diagnostics.GetEnumerator();
    /// <summary>Reports an existing diagnostic instance.</summary>
    public void Report(Diagnostic diagnostic) {
        ArgumentNullException.ThrowIfNull(diagnostic);
        m_diagnostics.Add(item: diagnostic);
    }
    /// <summary>Reports an error diagnostic.</summary>
    public void ReportError(string code, string message, SourceSpan span) =>
        Report(diagnostic: Diagnostic.Error(
            code: code,
            message: message,
            span: span
        ));
    /// <summary>Reports an informational diagnostic.</summary>
    public void ReportInfo(string code, string message, SourceSpan span) =>
        Report(diagnostic: Diagnostic.Info(
            code: code,
            message: message,
            span: span
        ));
    /// <summary>Reports an informational diagnostic.</summary>
    public void ReportInformation(string code, string message, SourceSpan span) =>
        Report(diagnostic: Diagnostic.Info(
            code: code,
            message: message,
            span: span
        ));
    /// <summary>Reports a warning diagnostic.</summary>
    public void ReportWarning(string code, string message, SourceSpan span) =>
        Report(diagnostic: Diagnostic.Warning(
            code: code,
            message: message,
            span: span
        ));
}
