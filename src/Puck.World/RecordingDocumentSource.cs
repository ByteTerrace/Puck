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
/// own <see cref="RecordingDocumentSerialization.TryLoad"/>.
/// </summary>
/// <remarks>An EXPLICIT <c>--recording</c> path is an assertion — absent, unreadable, or invalid, it fails the boot with
/// a named reason and a non-zero exit, exactly as <see cref="WorldDefinitionLoader"/> treats <c>--world</c>. Only the
/// implicit default path falls back to the baked <see cref="RecordingDocument.CreateDefault"/>, and it says so.</remarks>
internal static class RecordingDocumentLoader {
    /// <summary>The default recording file, resolved against <see cref="AppContext.BaseDirectory"/> when no
    /// <c>--recording</c> path is supplied.</summary>
    public static readonly string DefaultRelativePath = Path.Combine(path1: "Assets", path2: "recordings", path3: "default.recording.json");

    /// <summary>Resolves the active recording document. An explicit path that will not load is a boot failure; the
    /// implicit default path falls back loudly to the baked document.</summary>
    /// <param name="explicitPath">The <c>--recording</c> path, or <see langword="null"/>/empty for the default file.</param>
    /// <param name="source">The resolved document and its origin, when this returns <see langword="true"/>.</param>
    /// <param name="failure">The one-line boot-failure message, or empty on success.</param>
    /// <returns><see langword="true"/> when the boot may proceed.</returns>
    public static bool TryResolve(string? explicitPath, out RecordingDocumentSource source, out string failure) {
        var explicitly = !string.IsNullOrWhiteSpace(value: explicitPath);
        var path = (explicitly ? Path.GetFullPath(path: explicitPath!) : Path.Combine(path1: AppContext.BaseDirectory, path2: DefaultRelativePath));

        if (TryLoadFile(path: path, document: out var loaded, reason: out var reason)) {
            Console.Error.WriteLine(value: $"[recording] document: {path}");

            source = new RecordingDocumentSource(Document: loaded!, SourcePath: path);
            failure = string.Empty;

            return true;
        }

        if (explicitly) {
            source = new RecordingDocumentSource(Document: RecordingDocument.CreateDefault(), SourcePath: null);
            failure = $"[recording] --recording {reason}";

            return false;
        }

        Console.Error.WriteLine(value: $"[recording] document: baked default ({reason})");

        source = new RecordingDocumentSource(Document: RecordingDocument.CreateDefault(), SourcePath: null);
        failure = string.Empty;

        return true;
    }

    // Read → validate through the document's own loader, naming the three failure classes apart: an ABSENT file, an
    // UNREADABLE file, and an INVALID document. A broad catch is deliberate — a load boundary must never throw.
    private static bool TryLoadFile(string path, out RecordingDocument? document, out string reason) {
        document = null;

        if (!File.Exists(path: path)) {
            reason = $"no file at {path}";

            return false;
        }

        byte[] utf8Json;

        try {
            utf8Json = File.ReadAllBytes(path: path);
        } catch (Exception exception) {
            reason = $"cannot read {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        try {
            if (RecordingDocumentSerialization.TryLoad(utf8Json: utf8Json, document: out document, reason: out var loadReason)) {
                reason = loadReason;

                return true;
            }

            reason = $"{path} is not a valid recording document: {loadReason}";

            return false;
        } catch (Exception exception) {
            reason = $"{path} is not a valid recording document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
}
