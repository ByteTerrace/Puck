using System.Security.Cryptography;
using Puck.Text;

namespace Puck.Overlays;

/// <summary>
/// Loads the font atlases overlay glyph consumers share — the console panel, the binding bar, and the editor HUD —
/// from a caller-supplied assets root, including the pre-baked MTSDF mono atlas (<see cref="MonoFont"/>) that
/// <see cref="OverlayGlyphSdfPack"/> requires for its uniform-grid console/terminal glyphs. When the pre-baked atlas
/// is absent, availability degrades to <see langword="false"/> with one message to <see cref="Console.Error"/>
/// rather than falling back to a raster font; the caller supplies its own runtime fallback atlas through the
/// <c>monoFallback</c> constructor parameter.
/// </summary>
/// <remarks>
/// Each property loads once, lazily, on first access; results — including <see langword="null"/> — are cached so
/// neither the PNG decode nor the JSON parse re-runs.
/// </remarks>
public sealed class OverlayGlyphAtlasSet {
    // The bake packs EVERY font's glyphs into this one image (the one-GPU-texture law); each atlas JSON is a view of
    // it. KEEP IN SYNC with the committed fixed-UI image name under Puck.Text/Assets/Fonts.
    private const string CombinedImageName = "puck-fonts-mtsdf.png";
    // The mono voice's layout view (the overlay pack's source) and the prepacked overlay artifact written beside it.
    private const string MonoFontName = "jetbrains-mono-regular";
    private const string OverlayPackName = "overlay-glyphs.pack";

    private readonly Lazy<FontAtlasImageData?> m_combinedImage;
    private readonly string m_fontsDirectory;
    private readonly Lazy<FontAtlas?> m_monoFont;

    /// <summary>Initializes a new instance of the <see cref="OverlayGlyphAtlasSet"/> class over a pre-baked
    /// font-atlas assets root.</summary>
    /// <param name="fontsDirectory">The directory holding the combined MTSDF PNG and each face's layout JSON — the
    /// committed fixed-UI oracle atlas under <c>Puck.Text/Assets/Fonts</c>.</param>
    /// <param name="monoFallback">Invoked at most once, only when the pre-baked mono atlas is absent, to supply a
    /// caller-owned fallback atlas (e.g. a runtime GDI+ build); <see langword="null"/> (the default) means no
    /// fallback is available — a missing pre-baked atlas then leaves <see cref="MonoFont"/> <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="fontsDirectory"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public OverlayGlyphAtlasSet(string fontsDirectory, Func<FontAtlas?>? monoFallback = null) {
        if (string.IsNullOrWhiteSpace(value: fontsDirectory)) {
            throw new ArgumentException(
                message: "A fonts directory must be provided.",
                paramName: nameof(fontsDirectory)
            );
        }

        m_fontsDirectory = fontsDirectory;
        m_combinedImage = new Lazy<FontAtlasImageData?>(
            isThreadSafe: true,
            valueFactory: TryDecodeCombinedImage
        );
        m_monoFont = new Lazy<FontAtlas?>(
            valueFactory: () => (TryLoadPrebaked(name: MonoFontName) ?? TryLoadFallback(fallback: monoFallback)),
            isThreadSafe: true
        );
    }

    /// <summary>Gets a value indicating whether the mono atlas resolved, from either the pre-baked file or the
    /// constructor-supplied fallback.</summary>
    public bool IsAvailable => (m_monoFont.Value is not null);
    /// <summary>Gets the console/terminal mono atlas: the committed uniform-grid MTSDF atlas, the
    /// constructor-supplied fallback when the pre-baked file is absent, or <see langword="null"/> when neither
    /// resolves.</summary>
    public FontAtlas? MonoFont => m_monoFont.Value;

    private FontAtlasImageData? TryDecodeCombinedImage() {
        var imagePath = Path.Combine(
            path1: m_fontsDirectory,
            path2: CombinedImageName
        );

        if (!File.Exists(path: imagePath)) {
            return null;
        }

        try {
            return new FontAtlasImageDataLoader().Load(
                imageIdentifier: imagePath,
                pngBytes: File.ReadAllBytes(path: imagePath)
            );
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or NotSupportedException)) {
            Console.Error.WriteLine(value: $"[Puck.Overlays] combined font image '{imagePath}' failed to decode ({exception.Message}).");

            return null;
        }
    }
    private FontAtlas? TryLoadFallback(Func<FontAtlas?>? fallback) {
        if (fallback is null) {
            Console.Error.WriteLine(value: $"[Puck.Overlays] pre-baked glyph atlas 'jetbrains-mono-regular' is missing under '{m_fontsDirectory}' and no fallback was supplied; overlay text degrades to blank until the fixed-UI atlas assets are restored.");

            return null;
        }

        return fallback();
    }
    // Loads a committed atlas (a JSON view of the ONE combined PNG) from the configured assets root; null when the
    // files are absent or unreadable. The combined PNG decodes ONCE (memoized) and every atlas shares the SAME
    // FontAtlasImageData instance: every consumer (the overlay cell pack, a future decal bake) reads the pixels, and
    // one image means one upload.
    private FontAtlas? TryLoadPrebaked(string name) {
        var jsonPath = Path.Combine(
            path1: m_fontsDirectory,
            path2: $"{name}.json"
        );

        if (
            (!File.Exists(path: jsonPath)) ||
            (m_combinedImage.Value is not { } imageData)
        ) {
            return null;
        }

        try {
            return new FontAtlasLoader().Load(
                atlasIdentifier: jsonPath,
                imageData: imageData,
                imageIdentifier: Path.Combine(
                    path1: m_fontsDirectory,
                    path2: CombinedImageName
                ),
                jsonContent: File.ReadAllBytes(path: jsonPath)
            );
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or NotSupportedException)) {
            Console.Error.WriteLine(value: $"[Puck.Overlays] pre-baked atlas '{name}' failed to load ({exception.Message}).");

            return null;
        }
    }

    /// <summary>
    /// Loads the overlay glyph pack from the prepacked artifact beside the atlas (<c>overlay-glyphs.pack</c>) when
    /// possible: a warm start with the SAME <paramref name="extraCodePoints"/> reads the ~1.4-MiB-and-up finished
    /// pack instead of decoding the ~79 MiB combined PNG, whose full MTSDF decode holds upward of 150 MiB transient
    /// to produce it. A cold start, a rebaked atlas, or a DIFFERENT appended codepoint list (a world with a
    /// different icon repertoire) builds the pack from <see cref="MonoFont"/> once, persists it (overwriting a
    /// cached pack keyed to a different repertoire), and keys it by the SHA-256 of the source PNG bytes, the mono
    /// layout JSON bytes, AND the codepoint list. Returns <see langword="null"/> exactly when
    /// <see cref="OverlayGlyphSdfPack.TryCreate"/> would (no usable atlas).
    /// </summary>
    /// <param name="extraCodePoints">Additional codepoints appended after the ASCII block, in caller order — an
    /// authored world's icon repertoire (see <c>Puck.World.WorldIconTable</c>); <see langword="null"/> or empty
    /// bakes the ASCII-only pack.</param>
    /// <remarks>A cold SDF bake is dramatically slower and far heavier than loading the prepacked artifact; a warm
    /// start against the same repertoire uses the committed pack, and the loaded pack is bit-identical to the built
    /// one.</remarks>
    public OverlayGlyphSdfPack? LoadOverlayPack(IReadOnlyList<int>? extraCodePoints = null) {
        var imagePath = Path.Combine(
            path1: m_fontsDirectory,
            path2: CombinedImageName
        );
        var jsonPath = Path.Combine(
            path1: m_fontsDirectory,
            path2: $"{MonoFontName}.json"
        );

        if (
            !File.Exists(path: imagePath) ||
            !File.Exists(path: jsonPath)
        ) {
            // No committed atlas: the ordinary path (which may resolve a caller-supplied fallback) decides loudly.
            return OverlayGlyphSdfPack.TryCreate(
                extraCodePoints: extraCodePoints,
                monoFont: MonoFont
            );
        }

        Span<byte> pngHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> jsonHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> extraHash = stackalloc byte[SHA256.HashSizeInBytes];

        try {
            using (var image = File.OpenRead(path: imagePath)) {
                _ = SHA256.HashData(
                    destination: pngHash,
                    source: image
                );
            }

            _ = SHA256.HashData(
                source: File.ReadAllBytes(path: jsonPath),
                destination: jsonHash
            );
            HashExtraCodePoints(
                codePoints: extraCodePoints,
                destination: extraHash
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[Puck.Overlays] could not key the overlay glyph pack ({exception.Message}); building it from the atlas.");

            return OverlayGlyphSdfPack.TryCreate(
                extraCodePoints: extraCodePoints,
                monoFont: MonoFont
            );
        }

        var packPath = Path.Combine(
            path1: m_fontsDirectory,
            path2: OverlayPackName
        );

        if (OverlayGlyphSdfPack.TryReadPack(
            extraHash: extraHash,
            jsonHash: jsonHash,
            path: packPath,
            pngHash: pngHash
        ) is { } cached) {
            return cached;
        }

        var built = OverlayGlyphSdfPack.TryCreate(
            extraCodePoints: extraCodePoints,
            monoFont: MonoFont
        );

        built?.WritePack(
            extraHash: extraHash,
            jsonHash: jsonHash,
            path: packPath,
            pngHash: pngHash
        );

        return built;
    }
    // The appended codepoint list's key contribution — plain int32 little-endian, in caller order, so two callers
    // requesting the same repertoire in the same order (the ordinary case: one WorldIconTable per boot) hash equal.
    private static void HashExtraCodePoints(IReadOnlyList<int>? codePoints, Span<byte> destination) {
        if ((codePoints is not { Count: > 0 })) {
            destination.Clear();

            return;
        }

        Span<byte> bytes = stackalloc byte[(codePoints.Count * sizeof(int))];

        for (var index = 0; (index < codePoints.Count); index++) {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                destination: bytes[(index * sizeof(int))..],
                value: codePoints[index]
            );
        }

        _ = SHA256.HashData(
            destination: destination,
            source: bytes
        );
    }
}
