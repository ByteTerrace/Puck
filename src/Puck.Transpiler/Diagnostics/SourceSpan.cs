namespace Puck.World.Transpiler.Diagnostics;

/// <summary>Represents a span of source text with 1-indexed line and column coordinates.</summary>
/// <param name="Offset">0-based character offset from start of file.</param>
/// <param name="Length">Length of the span in characters.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Column">1-based column number.</param>
public readonly record struct SourceSpan(int Offset, int Length, int Line, int Column) {
    /// <summary>An empty span standing for a whole-document finding with no location of its own. Its
    /// <see cref="Line"/> is 0, which renderers read as "file-level": no line/column, and no quoted source line.</summary>
    public static readonly SourceSpan None = new(0, 0, 0, 0);

    /// <summary>Combines two spans into a single bounding span.</summary>
    public static SourceSpan Combine(SourceSpan start, SourceSpan end) {
        var offset = Math.Min(start.Offset, end.Offset);
        var endOffset = Math.Max(start.Offset + start.Length, end.Offset + end.Length);
        return new SourceSpan(offset, Math.Max(0, endOffset - offset), start.Line, start.Column);
    }

    /// <inheritdoc/>
    public override string ToString() => $"({Line},{Column})";
}
