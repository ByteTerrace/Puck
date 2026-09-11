namespace Puck.World.Transpiler.Parsing;

/// <summary>Exception thrown when parsing a Puck authoring document fails.</summary>
public sealed class PuckParseException : Exception {
    /// <summary>Gets the 0-based character offset within the source text where the error occurred.</summary>
    public int Offset { get; }

    /// <summary>Gets the 1-based line number in the source text.</summary>
    public int Line { get; }

    /// <summary>Gets the 1-based column number in the source text.</summary>
    public int Column { get; }

    /// <summary>Initializes a new instance of the <see cref="PuckParseException"/> class.</summary>
    /// <param name="message">The error message describing the syntax violation.</param>
    /// <param name="offset">The character offset in source text.</param>
    /// <param name="line">The 1-based line number.</param>
    /// <param name="column">The 1-based column number.</param>
    /// <param name="innerException">The optional inner exception.</param>
    public PuckParseException(string message, int offset = 0, int line = 1, int column = 1, Exception? innerException = null)
        : base(message: $"{message} at line {line}, column {column} (offset {offset})", innerException: innerException) {
        Offset = offset;
        Line = line;
        Column = column;
    }
}
