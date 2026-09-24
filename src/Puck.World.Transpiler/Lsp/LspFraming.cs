using System.Globalization;
using System.Text;

namespace Puck.World.Transpiler.Lsp;

/// <summary>The Language Server Protocol's base framing: every JSON-RPC message is a header block, ended by an empty
/// line and carrying a <c>Content-Length</c> field, followed by exactly that many bytes of UTF-8 body.</summary>
/// <remarks>The one implementation of the framing: <see cref="PuckLanguageServer"/> reads and writes through it, and
/// so does any client that drives the server over its streams.</remarks>
public static class LspFraming {
    private const string ContentLength = "Content-Length";

    /// <summary>Reads one framed message.</summary>
    /// <param name="stream">The stream to read from; it is left positioned after the message's last body
    /// byte.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>The message body, or <see langword="null"/> when the stream ends before a message begins.</returns>
    /// <exception cref="InvalidDataException">The stream ends inside a message, or its header carries no positive,
    /// readable <c>Content-Length</c>.</exception>
    public static async Task<string?> ReadAsync(Stream stream, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        var header = new List<byte>();

        while (
            (header.Count < 4) ||
            (header[^4] != '\r') ||
            (header[^3] != '\n') ||
            (header[^2] != '\r') ||
            (header[^1] != '\n')
        ) {
            var next = stream.ReadByte();

            if (next == -1) {
                return ((header.Count == 0)
                    ? null
                    : throw new InvalidDataException(message: "The stream ends inside a message header.")
                );
            }
            header.Add(item: ((byte)next));
        }

        var length = 0;

        foreach (var field in Encoding.ASCII.GetString(bytes: [.. header]).Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: "\r\n"
        )) {
            var colon = field.IndexOf(value: ':');

            if (
                (colon > 0) &&
                field[..colon].Trim().Equals(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: ContentLength
                ) &&
                !int.TryParse(
                    provider: CultureInfo.InvariantCulture,
                    result: out length,
                    s: field[(colon + 1)..].Trim(),
                    style: NumberStyles.None
                )
            ) {
                throw new InvalidDataException(message: $"A message header carries an unreadable {ContentLength}: '{field}'.");
            }
        }
        if (length <= 0) {
            throw new InvalidDataException(message: $"A message header carries no positive {ContentLength}.");
        }

        var body = new byte[length];

        try {
            await stream.ReadExactlyAsync(
                buffer: body,
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (EndOfStreamException error) {
            throw new InvalidDataException(
                innerException: error,
                message: $"The stream ends inside a message body that declared {length} bytes."
            );
        }

        return Encoding.UTF8.GetString(bytes: body);
    }
    /// <summary>Writes <paramref name="json"/> as one framed message and flushes the stream.</summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="json">The message body, one JSON-RPC object as text.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once the message is flushed.</returns>
    public static async Task WriteAsync(Stream stream, string json, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: stream);
        ArgumentNullException.ThrowIfNull(argument: json);

        var body = Encoding.UTF8.GetBytes(s: json);

        await stream.WriteAsync(
            buffer: Encoding.ASCII.GetBytes(s: $"{ContentLength}: {body.Length}\r\n\r\n"),
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        await stream.WriteAsync(
            buffer: body,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }
}
