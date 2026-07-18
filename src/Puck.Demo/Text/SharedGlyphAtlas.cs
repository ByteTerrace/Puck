using Puck.Text;

namespace Puck.Demo.Text;

/// <summary>
/// The ONE font-atlas seam shared across every text tier — the world-glyph op, the camera-rig diegetic UI, and the 2D
/// overlay surfaces (the on-screen console and the binding bar). Two fonts ride the seam: <see cref="MonoFont"/> (the
/// console/terminal voice, a uniform-grid atlas the overlay cell pack requires) and <see cref="UiFont"/> (the UI
/// grotesque, tight-packed with GPOS kerning baked into its table). Each resolves PRE-BAKED first — a committed
/// MTSDF JSON+PNG under Assets/Fonts, produced by the font-atlas bake pipeline (<c>tools/font-atlas</c>; true
/// multi-channel field, kerning restored) — and only the mono
/// falls back to the runtime GDI+ exact-EDT build (<see cref="GlyphAtlasBuilder.TryBuild"/>) when no file ships. A
/// single memoized load hands the SAME <see cref="FontAtlas"/> instance to every consumer: identical glyph metrics and
/// the identical distance field everywhere, whether marched as world geometry or sampled as a screen-space decal.
/// </summary>
/// <remarks>
/// Loads run once, lazily, on first access; results — including nulls — are cached so neither the PNG decode nor the
/// exact-EDT transform re-runs. Off-Windows with no committed atlas every property is
/// <see langword="null"/> and each consumer declares no text and falls back.
/// </remarks>
internal static class SharedGlyphAtlas {
    private static readonly Lazy<FontAtlas?> s_monoFont = new(
        valueFactory: static () => (TryLoadPrebaked(name: "jetbrains-mono-regular") ?? GlyphAtlasBuilder.TryBuild()),
        isThreadSafe: true
    );
    private static readonly Lazy<FontAtlas?> s_uiFont = new(
        valueFactory: static () => TryLoadPrebaked(name: "inter-regular"),
        isThreadSafe: true
    );
    private static readonly Lazy<FontAtlas?> s_uiFontMedium = new(
        valueFactory: static () => TryLoadPrebaked(name: "inter-medium"),
        isThreadSafe: true
    );
    private static readonly Lazy<FontAtlas?> s_uiFontSemiBold = new(
        valueFactory: static () => TryLoadPrebaked(name: "inter-semibold"),
        isThreadSafe: true
    );

    /// <summary>Whether the shared atlas is available — the cheap availability probe a coupling-ceilinged caller uses to
    /// decide whether to compose a glyph consumer, WITHOUT naming <see cref="FontAtlas"/>.</summary>
    public static bool IsAvailable => (s_monoFont.Value is not null);

    /// <summary>The console/terminal voice: the committed uniform-grid MTSDF mono atlas, or the runtime GDI+ exact-EDT
    /// build when no file ships, or <see langword="null"/> when neither resolves.</summary>
    public static FontAtlas? MonoFont => s_monoFont.Value;

    /// <summary>The UI grotesque (regular weight): pre-baked ONLY — tight-packed MTSDF with GPOS kerning in its table;
    /// <see langword="null"/> when the committed atlas is absent (consumers fall back to <see cref="MonoFont"/>).</summary>
    public static FontAtlas? UiFont => s_uiFont.Value;

    /// <summary>The UI grotesque's medium weight (pre-baked only; <see langword="null"/> when absent).</summary>
    public static FontAtlas? UiFontMedium => s_uiFontMedium.Value;

    /// <summary>The UI grotesque's semi-bold weight (pre-baked only; <see langword="null"/> when absent).</summary>
    public static FontAtlas? UiFontSemiBold => s_uiFontSemiBold.Value;

    // The bake packs EVERY font's glyphs into this one image (the one-GPU-texture law); each atlas JSON is a view of
    // it. KEEP IN SYNC with tools/font-atlas/manifest.json's output image name.
    private const string CombinedImageName = "puck-fonts-mtsdf.png";

    private static readonly Lazy<FontAtlasImageData?> s_combinedImage = new(
        valueFactory: static () => TryDecodeCombinedImage(),
        isThreadSafe: true
    );

    // Loads a committed atlas (a JSON view of the ONE combined PNG) from the demo's output assets; null
    // when the files are absent or unreadable (a missing atlas is a fallback condition, never a crash — the demo must
    // boot from a bare checkout that predates the baked assets). The combined PNG decodes ONCE (memoized) and every
    // atlas shares the SAME FontAtlasImageData instance: every demo consumer (the overlay cell pack, the world-glyph
    // upload, the decal bake) reads the pixels, and one image → one upload.
    private static FontAtlas? TryLoadPrebaked(string name) {
        var fontsDirectory = Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Fonts");
        var jsonPath = Path.Combine(path1: fontsDirectory, path2: (name + ".json"));

        if ((!File.Exists(path: jsonPath)) || (s_combinedImage.Value is not { } imageData)) {
            return null;
        }

        try {
            return new FontAtlasLoader().Load(
                atlasIdentifier: jsonPath,
                imageData: imageData,
                imageIdentifier: Path.Combine(path1: fontsDirectory, path2: CombinedImageName),
                jsonContent: File.ReadAllBytes(path: jsonPath)
            );
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or NotSupportedException)) {
            Console.Error.WriteLine(value: $"[text] prebaked atlas '{name}' failed to load ({exception.Message}); falling back.");

            return null;
        }
    }
    private static FontAtlasImageData? TryDecodeCombinedImage() {
        var imagePath = Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Fonts", path4: CombinedImageName);

        if (!File.Exists(path: imagePath)) {
            return null;
        }

        try {
            return new FontAtlasImageDataLoader().Load(imageIdentifier: imagePath, pngBytes: File.ReadAllBytes(path: imagePath));
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or NotSupportedException)) {
            Console.Error.WriteLine(value: $"[text] combined font image failed to decode ({exception.Message}); falling back.");

            return null;
        }
    }
}
