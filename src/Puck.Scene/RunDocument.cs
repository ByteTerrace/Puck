using System.Text.Json;
using Puck.SdfVm;

namespace Puck.Scene;

/// <summary>
/// The parse -> validate -> build front door for run documents. <see cref="Parse"/> deserializes UTF-8 bytes through
/// the source-generation context (the only sanctioned, reflection-free path) and then runs the thick
/// <see cref="RunDocumentValidator"/>, so any returned document is guaranteed buildable. <see cref="CreateFrameSource"/>
/// compiles a document's scene + viewports into the <see cref="JsonSdfFrameSource"/> a producer node consumes.
/// </summary>
public static class RunDocument {
    private static readonly JsonSerializerOptions s_options = new(options: PuckSceneJsonContext.Default.Options) {
        // Tolerate the $type / shape / op discriminator appearing after other members in a polymorphic object.
        AllowOutOfOrderMetadataProperties = true,
    };

    // The source-gen-backed options every entry point shares (also the basis for schema export).
    internal static JsonSerializerOptions Options => s_options;

    /// <summary>Deserializes and validates a document from its UTF-8 JSON bytes.</summary>
    /// <param name="utf8Json">The document's UTF-8 JSON.</param>
    /// <param name="bounds">The renderability envelope; defaults to <see cref="ShapeBounds.Default"/>.</param>
    /// <returns>The validated document.</returns>
    /// <exception cref="JsonException">The JSON was malformed, carried an unknown member, or named an unknown shape/op/enum value.</exception>
    /// <exception cref="NotSupportedException">A polymorphic object omitted (or mis-cased) its <c>$type</c>/<c>shape</c>/<c>op</c> discriminator.</exception>
    /// <exception cref="RunDocumentValidationException">The document deserialized but failed a semantic invariant.</exception>
    public static PuckRunDocument Parse(ReadOnlySpan<byte> utf8Json, ShapeBounds? bounds = null) {
        // Deserialize through s_options, whose only TypeInfoResolver is the source-gen context: a type the context
        // does not know throws rather than silently falling back to runtime reflection.
        var document = JsonSerializer.Deserialize<PuckRunDocument>(options: s_options, utf8Json: utf8Json);

        if (document is null) {
            throw new RunDocumentValidationException(errors: ["document: the JSON deserialized to null"]);
        }

        RunDocumentValidator.Validate(bounds: bounds, document: document);

        return document;
    }

    /// <summary>Reads, deserializes, and validates a document from a JSON file.</summary>
    /// <param name="path">The path to the document.</param>
    /// <param name="bounds">The renderability envelope; defaults to <see cref="ShapeBounds.Default"/>.</param>
    /// <returns>The validated document.</returns>
    /// <exception cref="IOException">The file could not be read (also <see cref="UnauthorizedAccessException"/> for a directory, <see cref="ArgumentException"/> for an empty path).</exception>
    /// <exception cref="JsonException">The JSON was malformed, carried an unknown member, or named an unknown shape/op/enum value.</exception>
    /// <exception cref="NotSupportedException">A polymorphic object omitted (or mis-cased) its discriminator.</exception>
    /// <exception cref="RunDocumentValidationException">The document deserialized but failed a semantic invariant.</exception>
    public static PuckRunDocument Load(string path, ShapeBounds? bounds = null) {
        return Parse(bounds: bounds, utf8Json: File.ReadAllBytes(path: path));
    }

    /// <summary>Compiles a validated document's scene + viewports into a frame source.</summary>
    /// <param name="document">The validated document.</param>
    /// <returns>The frame source a producer node drives.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static JsonSdfFrameSource CreateFrameSource(PuckRunDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return CreateFrameSource(program: SceneBuilder.Build(scene: document.Scene), viewports: document.Viewports);
    }

    /// <summary>Compiles an explicit viewport list over an already-built program into a frame source — the seam for
    /// rendering an injected program (e.g. a differential-fuzzing program) through the standard data-driven viewports
    /// rather than a document scene.</summary>
    /// <param name="viewports">The viewports whose cameras + regions drive the render.</param>
    /// <param name="program">The pre-built scene program to render.</param>
    /// <returns>The frame source a producer node drives.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewports"/> or <paramref name="program"/> is <see langword="null"/>.</exception>
    public static JsonSdfFrameSource CreateFrameSource(IReadOnlyList<Viewport> viewports, SdfProgram program) {
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: program);

        var (cameras, regions, liveCameraSlots) = ViewportBuilder.Build(viewports: viewports);

        return new JsonSdfFrameSource(cameras: cameras, liveCameraSlots: liveCameraSlots, program: program, regions: regions);
    }
}
