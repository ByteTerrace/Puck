using System.Security.Cryptography;
using Puck.Text;

namespace Puck.Overlays;

/// <summary>
/// The font-atlas seam every overlay glyph consumer shares — the console panel, the binding bar, and the editor
/// HUD: loads the pre-baked MTSDF atlas set (<see cref="MonoFont"/>, the uniform-grid console/terminal voice
/// <see cref="OverlayGlyphSdfPack"/> requires, and the <see cref="UiFont"/> family — tight-packed with GPOS
/// kerning) from a caller-supplied assets root. Loads a prepacked atlas and degrades LOUDLY if absent
/// (<see cref="IsAvailable"/> is <see langword="false"/>, one clear message to <see cref="Console.Error"/>), never
/// a raster fallback. The caller owns any runtime fallback atlas, supplied through the <c>monoFallback</c>
/// constructor seam.
/// </summary>
/// <remarks>
/// Each property loads once, lazily, on first access; results — including <see langword="null"/> — are cached so
/// neither the PNG decode nor the JSON parse re-runs.
/// </remarks>
public sealed class OverlayGlyphAtlasSet {
    // The bake packs EVERY font's glyphs into this one image (the one-GPU-texture law); each atlas JSON is a view of
    // it. KEEP IN SYNC with COMBINED_PNG_NAME in tools/font-atlas/bake.py (the combined atlas image name).
    private const string CombinedImageName = "puck-fonts-mtsdf.png";
    // The mono voice's layout view (the overlay pack's source) and the prepacked overlay artifact written beside it.
    private const string MonoFontName = "jetbrains-mono-regular";
    private const string OverlayPackName = "overlay-glyphs.pack";

    private readonly string m_fontsDirectory;
    private readonly Lazy<FontAtlasImageData?> m_combinedImage;
    private readonly Lazy<FontAtlas?> m_monoFont;
    private readonly Lazy<FontAtlas?> m_uiFont;
    private readonly Lazy<FontAtlas?> m_uiFontMedium;
    private readonly Lazy<FontAtlas?> m_uiFontSemiBold;

    /// <summary>Initializes a new instance of the <see cref="OverlayGlyphAtlasSet"/> class over a pre-baked
    /// font-atlas assets root.</summary>
    /// <param name="fontsDirectory">The directory holding the combined MTSDF PNG and each face's layout JSON — the
    /// output of the font-atlas bake pipeline (<c>tools/font-atlas</c>).</param>
    /// <param name="monoFallback">Invoked at most once, only when the pre-baked mono atlas is absent, to supply a
    /// caller-owned fallback atlas (e.g. a runtime GDI+ build); <see langword="null"/> (the default) means no
    /// fallback is available — a missing pre-baked atlas then leaves <see cref="MonoFont"/> <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="fontsDirectory"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public OverlayGlyphAtlasSet(string fontsDirectory, Func<FontAtlas?>? monoFallback = null) {
        if (string.IsNullOrWhiteSpace(value: fontsDirectory)) {
            throw new ArgumentException(message: "A fonts directory must be provided.", paramName: nameof(fontsDirectory));
        }

        m_fontsDirectory = fontsDirectory;
        m_combinedImage = new Lazy<FontAtlasImageData?>(valueFactory: TryDecodeCombinedImage, isThreadSafe: true);
        m_monoFont = new Lazy<FontAtlas?>(valueFactory: () => (TryLoadPrebaked(name: MonoFontName) ?? TryLoadFallback(fallback: monoFallback)), isThreadSafe: true);
        m_uiFont = new Lazy<FontAtlas?>(valueFactory: () => TryLoadPrebaked(name: "inter-regular"), isThreadSafe: true);
        m_uiFontMedium = new Lazy<FontAtlas?>(valueFactory: () => TryLoadPrebaked(name: "inter-medium"), isThreadSafe: true);
        m_uiFontSemiBold = new Lazy<FontAtlas?>(valueFactory: () => TryLoadPrebaked(name: "inter-semibold"), isThreadSafe: true);
    }

    /// <summary>Whether the mono atlas resolved (pre-baked or fallback) — the cheap availability probe a
    /// coupling-ceilinged caller uses to decide whether to compose a glyph consumer, without naming
    /// <see cref="FontAtlas"/>.</summary>
    public bool IsAvailable => (m_monoFont.Value is not null);

    /// <summary>The console/terminal voice: the committed uniform-grid MTSDF mono atlas, the constructor-supplied
    /// fallback when the pre-baked file is absent, or <see langword="null"/> when neither resolves.</summary>
    public FontAtlas? MonoFont => m_monoFont.Value;

    /// <summary>The UI grotesque (regular weight): pre-baked ONLY — tight-packed MTSDF with GPOS kerning baked into
    /// its table; <see langword="null"/> when the committed atlas is absent (consumers fall back to
    /// <see cref="MonoFont"/>).</summary>
    public FontAtlas? UiFont => m_uiFont.Value;

    /// <summary>The UI grotesque's medium weight (pre-baked only; <see langword="null"/> when absent).</summary>
    public FontAtlas? UiFontMedium => m_uiFontMedium.Value;

    /// <summary>The UI grotesque's semi-bold weight (pre-baked only; <see langword="null"/> when absent).</summary>
    public FontAtlas? UiFontSemiBold => m_uiFontSemiBold.Value;

    /// <summary>
    /// Loads the overlay glyph pack through the prepacked artifact beside the atlas (<c>overlay-glyphs.pack</c>):
    /// a warm start reads the ~1.4 MiB finished pack and NEVER decodes the ~79 MiB combined PNG (the combined MTSDF
    /// decode held ≥150 MiB transient to retain 1.4 MiB, so the prepacked path is mandatory); a cold or rebaked
    /// start builds the pack from <see cref="MonoFont"/> once and persists it, keyed by the SHA-256 of the source
    /// PNG + mono layout JSON bytes. Returns <see langword="null"/> exactly when
    /// <see cref="OverlayGlyphSdfPack.TryCreate"/> would (no usable atlas), preserving the loud-degradation
    /// contract.
    /// </summary>
    /// <remarks>A cold SDF bake is dramatically slower and far heavier than loading the prepacked artifact; warm
    /// startup uses the committed pack, and the loaded pack is bit-identical to the built one.</remarks>
    public OverlayGlyphSdfPack? LoadOverlayPack() {
        var imagePath = Path.Combine(path1: m_fontsDirectory, path2: CombinedImageName);
        var jsonPath = Path.Combine(path1: m_fontsDirectory, path2: (MonoFontName + ".json"));

        if (!File.Exists(path: imagePath) || !File.Exists(path: jsonPath)) {
            // No committed atlas: the ordinary path (which may resolve a caller-supplied fallback) decides loudly.
            return OverlayGlyphSdfPack.TryCreate(monoFont: MonoFont);
        }

        Span<byte> pngHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> jsonHash = stackalloc byte[SHA256.HashSizeInBytes];

        try {
            using (var image = File.OpenRead(path: imagePath)) {
                _ = SHA256.HashData(source: image, destination: pngHash);
            }

            _ = SHA256.HashData(source: File.ReadAllBytes(path: jsonPath), destination: jsonHash);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine(value: $"[Puck.Overlays] could not key the overlay glyph pack ({exception.Message}); building it from the atlas.");

            return OverlayGlyphSdfPack.TryCreate(monoFont: MonoFont);
        }

        var packPath = Path.Combine(path1: m_fontsDirectory, path2: OverlayPackName);

        if (OverlayGlyphSdfPack.TryReadPack(path: packPath, pngHash: pngHash, jsonHash: jsonHash) is { } cached) {
            return cached;
        }

        var built = OverlayGlyphSdfPack.TryCreate(monoFont: MonoFont);

        built?.WritePack(path: packPath, pngHash: pngHash, jsonHash: jsonHash);

        return built;
    }

    private FontAtlas? TryLoadFallback(Func<FontAtlas?>? fallback) {
        if (fallback is null) {
            Console.Error.WriteLine(value: $"[Puck.Overlays] pre-baked glyph atlas 'jetbrains-mono-regular' is missing under '{m_fontsDirectory}' and no fallback was supplied; overlay text degrades to blank until the atlas is rebaked (see tools/font-atlas).");

            return null;
        }

        return fallback();
    }

    // Loads a committed atlas (a JSON view of the ONE combined PNG) from the configured assets root; null when the
    // files are absent or unreadable. The combined PNG decodes ONCE (memoized) and every atlas shares the SAME
    // FontAtlasImageData instance: every consumer (the overlay cell pack, a future decal bake) reads the pixels, and
    // one image means one upload.
    private FontAtlas? TryLoadPrebaked(string name) {
        var jsonPath = Path.Combine(path1: m_fontsDirectory, path2: (name + ".json"));

        if ((!File.Exists(path: jsonPath)) || (m_combinedImage.Value is not { } imageData)) {
            return null;
        }

        try {
            return new FontAtlasLoader().Load(
                atlasIdentifier: jsonPath,
                imageData: imageData,
                imageIdentifier: Path.Combine(path1: m_fontsDirectory, path2: CombinedImageName),
                jsonContent: File.ReadAllBytes(path: jsonPath)
            );
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or NotSupportedException)) {
            Console.Error.WriteLine(value: $"[Puck.Overlays] pre-baked atlas '{name}' failed to load ({exception.Message}).");

            return null;
        }
    }
    private FontAtlasImageData? TryDecodeCombinedImage() {
        var imagePath = Path.Combine(path1: m_fontsDirectory, path2: CombinedImageName);

        if (!File.Exists(path: imagePath)) {
            return null;
        }

        try {
            return new FontAtlasImageDataLoader().Load(imageIdentifier: imagePath, pngBytes: File.ReadAllBytes(path: imagePath));
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or NotSupportedException)) {
            Console.Error.WriteLine(value: $"[Puck.Overlays] combined font image '{imagePath}' failed to decode ({exception.Message}).");

            return null;
        }
    }
}
