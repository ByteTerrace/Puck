using Puck.Text;

namespace Puck.Overlays;

/// <summary>
/// The font-atlas seam every overlay glyph consumer shares — the console panel, the binding bar, and the editor
/// HUD: loads the pre-baked MTSDF atlas set (<see cref="MonoFont"/>, the uniform-grid console/terminal voice
/// <see cref="OverlayGlyphSdfPack"/> requires, and the <see cref="UiFont"/> family — tight-packed with GPOS
/// kerning) from a caller-supplied assets root. Lifted from <c>Puck.Demo.Text.SharedGlyphAtlas</c>: the static
/// per-process singleton hardcoded to the demo's own <c>Assets/Fonts</c> path, and its GDI+ runtime fallback
/// (<c>GlyphAtlasBuilder</c>), both stay in Demo — a missing pre-baked atlas here is a LOUD degradation
/// (<see cref="IsAvailable"/> is <see langword="false"/>, one clear message to <see cref="Console.Error"/>), never
/// a raster fallback. A caller that owns its own fallback atlas (Demo's GDI+ build, or any other source) supplies
/// it through the <c>monoFallback</c> constructor seam instead.
/// </summary>
/// <remarks>
/// Each property loads once, lazily, on first access; results — including <see langword="null"/> — are cached so
/// neither the PNG decode nor the JSON parse re-runs.
/// </remarks>
public sealed class OverlayGlyphAtlasSet {
    // The bake packs EVERY font's glyphs into this one image (the one-GPU-texture law); each atlas JSON is a view of
    // it. KEEP IN SYNC with tools/font-atlas/manifest.json's output image name.
    private const string CombinedImageName = "puck-fonts-mtsdf.png";

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
        m_monoFont = new Lazy<FontAtlas?>(valueFactory: () => (TryLoadPrebaked(name: "jetbrains-mono-regular") ?? TryLoadFallback(fallback: monoFallback)), isThreadSafe: true);
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
