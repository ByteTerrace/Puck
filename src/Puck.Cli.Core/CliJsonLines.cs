using System.Text.Json.Nodes;

namespace Puck.Cli;

/// <summary>
/// The one <c>--json</c> contract (<see cref="CliOptions.Json"/>): while a verb holds an instance, its results go to
/// standard output as one JSON object per line, and every text line it prints goes to standard error instead, so a
/// caller that polls the verb parses standard output without filtering it. Disposing the instance restores standard
/// output. A verb run without <c>--json</c> holds none and prints its text as usual.
/// </summary>
public sealed class CliJsonLines : IDisposable {
    private readonly TextWriter m_records;
    private bool m_disposed;

    private CliJsonLines(TextWriter records) {
        m_records = records;
        Console.SetOut(newOut: Console.Error);
    }

    /// <summary>Opens the JSON-lines output when <paramref name="json"/> is set.</summary>
    /// <param name="json">Whether the caller passed <c>--json</c>.</param>
    /// <returns>The open output, which the caller disposes when its results are written; <see langword="null"/> when
    /// <paramref name="json"/> is <see langword="false"/>.</returns>
    public static CliJsonLines? Open(bool json) => (json ? new CliJsonLines(records: Console.Out) : null);
    /// <summary>Writes one result record as one line of standard output.</summary>
    /// <param name="record">The record; its members are camel-case names.</param>
    /// <exception cref="ObjectDisposedException">The output was disposed.</exception>
    public void Write(JsonObject record) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        m_records.WriteLine(value: record.ToJsonString());
        m_records.Flush();
    }
    /// <summary>Restores standard output to the records' writer.</summary>
    public void Dispose() {
        if (!m_disposed) {
            m_disposed = true;
            Console.SetOut(newOut: m_records);
        }
    }
}
