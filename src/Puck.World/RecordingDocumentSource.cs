using Puck.Recording.Document;

namespace Puck.World;

/// <summary>The loaded recording document plus its on-disk origin. The recording document (<c>puck.recording.v1</c>) is
/// HOST-scope data — like the storage host-section, it describes an operation the running world performs rather than the
/// world's own state — so it is resolved once at boot and held for the capture verbs. <see cref="SourcePath"/> is
/// <see langword="null"/> when the document is the baked <see cref="RecordingDocument.CreateDefault"/> (no file, or a
/// rejected file).</summary>
/// <param name="Document">The active recording document.</param>
/// <param name="SourcePath">The file the document loaded from, or <see langword="null"/> when baked/fallback.</param>
internal sealed record RecordingDocumentSource(RecordingDocument Document, string? SourcePath);

/// <summary>
/// Resolves the recording document at boot: a <c>--recording &lt;path&gt;</c> argument (or the checked-in
/// <c>Assets/recordings/default.recording.json</c> beside the executable), loaded and validated through the document's
/// own <see cref="RecordingDocumentSerialization.TryLoad"/>. ANY failure — missing file, parse error, schema mismatch,
/// or a validation error — falls back LOUDLY to the baked <see cref="RecordingDocument.CreateDefault"/>. Boot always
/// prints exactly one <c>[recording] document:</c> line naming the resolved path (or the baked-default reason).
/// </summary>
internal static class RecordingDocumentLoader {
    /// <summary>The default recording file, resolved against <see cref="AppContext.BaseDirectory"/> when no
    /// <c>--recording</c> path is supplied.</summary>
    public static readonly string DefaultRelativePath = Path.Combine(path1: "Assets", path2: "recordings", path3: "default.recording.json");

    /// <summary>Loads the active recording document, honoring an optional explicit path and falling back loudly to the
    /// baked default on any failure. Prints the one-line boot origin to <see cref="Console.Error"/>.</summary>
    /// <param name="explicitPath">The <c>--recording</c> path, or <see langword="null"/>/empty for the default file.</param>
    /// <returns>The active document and its origin.</returns>
    public static RecordingDocumentSource Load(string? explicitPath) {
        var path = (string.IsNullOrWhiteSpace(value: explicitPath)
            ? Path.Combine(path1: AppContext.BaseDirectory, path2: DefaultRelativePath)
            : Path.GetFullPath(path: explicitPath));

        if (TryLoadFile(path: path, document: out var loaded, reason: out var reason)) {
            Console.Error.WriteLine(value: $"[recording] document: {path}");

            return new RecordingDocumentSource(Document: loaded!, SourcePath: path);
        }

        Console.Error.WriteLine(value: $"[recording] document: baked default ({reason})");

        return new RecordingDocumentSource(Document: RecordingDocument.CreateDefault(), SourcePath: null);
    }

    // Read → validate through the document's own loader. A missing file is the common no-arg case (no default asset
    // shipped) and yields a quiet-ish reason; every other failure surfaces the loader's one-line reason. A broad catch
    // is deliberate — a load boundary with a safe baked fallback, mirroring WorldDefinitionLoader.
    private static bool TryLoadFile(string path, out RecordingDocument? document, out string reason) {
        document = null;

        if (!File.Exists(path: path)) {
            reason = $"no file at {path}";

            return false;
        }

        try {
            return RecordingDocumentSerialization.TryLoad(utf8Json: File.ReadAllBytes(path: path), document: out document, reason: out reason);
        } catch (Exception exception) {
            reason = $"{path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
}
