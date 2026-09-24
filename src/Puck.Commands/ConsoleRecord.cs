namespace Puck.Commands;

/// <summary>
/// Frames one record—a command's result or one narration—on a line-oriented stream: the record's first line starts
/// at column zero, and every further non-empty line of the same record is indented by <see cref="ContinuationIndent"/>.
/// A reader that sees only lines therefore tells where one record ends and the next begins: a non-empty line that
/// does not start with whitespace opens a record, and an indented line continues the record open on the same stream.
/// <para>A multi-line result, such as <c>world.state</c>'s row dump, and two back-to-back one-line results of the same
/// verb would otherwise be the same run of lines. A record stays on the one stream it was written to, so a reader
/// pairs a continuation with the record open on its own stream, never across the two.</para>
/// </summary>
public static class ConsoleRecord {
    /// <summary>The prefix written before each non-empty line of a record after its first.</summary>
    public const string ContinuationIndent = "  ";

    /// <summary>Returns whether a line read from a framed stream opens no record of its own.</summary>
    /// <param name="line">The line, without its terminator.</param>
    /// <returns><see langword="true"/> when the line is empty or starts with whitespace.</returns>
    public static bool IsContinuation(ReadOnlySpan<char> line) =>
        (line.IsEmpty || char.IsWhiteSpace(c: line[0]));
    /// <summary>Writes one record, each line terminated, with every non-empty line after the first indented.</summary>
    /// <param name="writer">The stream's writer.</param>
    /// <param name="record">The record's text, whose lines may be separated by any line ending.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> or <paramref name="record"/> is <see langword="null"/>.</exception>
    public static void Write(TextWriter writer, string record) {
        ArgumentNullException.ThrowIfNull(argument: writer);
        ArgumentNullException.ThrowIfNull(argument: record);

        if (record.AsSpan().IndexOfAny(
            value0: '\n',
            value1: '\r'
        ) < 0) {
            writer.WriteLine(value: record);

            return;
        }

        // Whole-line string writes only: a wrapping writer that tags each line (the silo's narration writer) sees
        // every line of the record through the one overload it overrides.
        var first = true;

        foreach (var line in record.AsSpan().EnumerateLines()) {
            writer.WriteLine(value: ((first || line.IsEmpty)
                ? line.ToString()
                : string.Concat(
                    str0: ContinuationIndent,
                    str1: line
                )));
            first = false;
        }
    }
}
