namespace Puck.World;

/// <summary>How a refusal names a document and a failure without the host's own paths: a document by the file name
/// it resolved to, or by the reference its author wrote, and a storage failure by its kind. A resolved path, the
/// directory a catalog lives in and an exception message that quotes either are what the host knows and the player
/// does not, so no refusal or narration carries them.</summary>
public static class WorldDocumentLabel {
    /// <summary>Names the document at <paramref name="path"/> by its file name alone.</summary>
    /// <param name="path">A resolved document path, with either separator.</param>
    /// <returns>The file name, or <c>document</c> when the path names none.</returns>
    public static string Of(string path) {
        var name = path.AsSpan();
        var cut = name.LastIndexOfAny(
            value0: '/',
            value1: '\\'
        );

        if (cut >= 0) {
            name = name[(cut + 1)..];
        }

        return ((name.Length > 0)
            ? name.ToString()
            : "document");
    }
    /// <summary>Names the kind of a storage or composition failure without the path its message carries.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>A short kind such as <c>access denied</c>, never a path.</returns>
    public static string Failure(Exception exception) => exception switch {
        UnauthorizedAccessException => "access denied",
        System.Text.Json.JsonException => "the document is malformed",
        PathTooLongException => "the path is too long",
        DirectoryNotFoundException => "directory not found",
        FileNotFoundException => "file not found",
        ArgumentException or NotSupportedException => "the path cannot be formed",
        // The Win32 sharing and lock violations, and the two disk-full codes; every other I/O failure is named plainly.
        IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) } => "file in use by another process",
        IOException { HResult: unchecked((int)0x80070027) or unchecked((int)0x80070070) } => "no space left on the device",
        _ => "storage error",
    };
}
