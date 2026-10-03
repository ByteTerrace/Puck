using Puck.Assets;
using Puck.SdfVm;
using Puck.Text;

namespace Puck.World.Client;

/// <summary>Resolves a delivered world's hash-pinned font assets, beside the world's own document, and exposes their one
/// packed GPU atlas: the boot world's, or a world shown through a screen, which draws its own text with its own
/// fonts.</summary>
public sealed class WorldTextCatalog {
    private readonly FontAtlasSourceResolver m_resolver = new(assetSource: new FileSystemAssetSource());
    // The directory a delivered definition's font assets resolve beside, or null for a definition that names none.
    private readonly Func<WorldDefinition, string?> m_directory;

    private TextFontCatalogDefinition? m_definition;
    private string? m_resolvedDirectory;

    /// <summary>Initializes a new instance of the <see cref="WorldTextCatalog"/> class for the booted world, whose font
    /// assets resolve beside the document it booted from.</summary>
    /// <param name="source">The booted document and its path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public WorldTextCatalog(WorldDefinitionSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_directory = _ => WorldDocumentPaths.DirectoryOf(documentPath: source.SourcePath);
    }
    /// <summary>Initializes a new instance of the <see cref="WorldTextCatalog"/> class for a world delivered from
    /// another authority, whose font assets resolve beside its own document
    /// (<see cref="WorldDefinition.DocumentDirectory"/>).</summary>
    public WorldTextCatalog() => m_directory = static definition => definition.DocumentDirectory;

    private static void PreflightContent(WorldDefinition definition, PackedFontAtlasCatalog catalog) {
        foreach (var creation in definition.Creations) {
            foreach (var run in (creation.Document.TextRuns ?? [])) {
                var atlas = catalog.Resolve(name: run.Font);

                foreach (var rune in run.Text.EnumerateRunes()) {
                    if (
                        (rune.Value is '\r' or '\n') ||
                        atlas.TryGetGlyph(
                        unicode: rune.Value,
                        glyph: out _
                    )
                    ) {
                        continue;
                    }

                    var fontName = (run.Font ?? catalog.DefaultFont);

                    throw new InvalidDataException(message: $"Creation '{creation.Id}' text font '{fontName}' does not contain authored scalar U+{rune.Value:X} in its generated subset.");
                }
            }
        }

        foreach (var screen in definition.Screens) {
            PreflightScreenText(
                catalog: catalog,
                source: screen.Source,
                subject: $"Screen {screen.Index}"
            );

            foreach (var entry in (screen.Magazine?.Entries ?? [])) {
                PreflightScreenText(
                    catalog: catalog,
                    source: entry,
                    subject: $"Screen {screen.Index} magazine entry"
                );
            }
        }

        foreach (var placement in definition.Placements) {
            foreach (var face in (placement.FaceSources ?? [])) {
                PreflightScreenText(
                    catalog: catalog,
                    source: face.Source,
                    subject: $"Placement '{placement.Id}' face '{face.Face}'"
                );
            }
        }
    }
    // A decal cell renders blank for a scalar outside the generated subset — the same silent gap the creation-run
    // preflight above refuses, so a text screen's lines cross the same gate (whitespace advances no glyph and is
    // exempt, exactly as the decal bake skips it).
    private static void PreflightScreenText(PackedFontAtlasCatalog catalog, WorldScreenSource source, string subject) {
        if (source is not WorldScreenSource.Text text) {
            return;
        }

        var atlas = catalog.Resolve(name: text.Font);

        foreach (var line in text.Lines) {
            foreach (var rune in line.EnumerateRunes()) {
                if (
                    System.Text.Rune.IsWhiteSpace(value: rune) ||
                    atlas.TryGetGlyph(
                    unicode: rune.Value,
                    glyph: out _
                )
                ) {
                    continue;
                }

                var fontName = (text.Font ?? catalog.DefaultFont);

                throw new InvalidDataException(message: $"{subject} text font '{fontName}' does not contain authored scalar U+{rune.Value:X} in its generated subset.");
            }
        }
    }
    private PackedFontAtlasCatalog Resolve(WorldDefinition definition, string? directory) {
        var text = (definition.Text ?? throw new ArgumentException(
            message: "The world declares no text catalog.",
            paramName: nameof(definition)
        ));
        var catalog = m_resolver.ResolveCatalog(
            basePath: (directory ?? throw new ArgumentException(
                message: "The world's document names no directory its font assets resolve beside.",
                paramName: nameof(definition)
            )),
            definition: text
        );

        PreflightContent(
            catalog: catalog,
            definition: definition
        );

        return catalog;
    }

    /// <summary>Reconciles a newly delivered definition against the directory its font assets resolve beside.</summary>
    /// <param name="definition">The delivered definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The definition declares a text catalog and names no directory its font
    /// assets resolve beside, or a font asset row is invalid.</exception>
    /// <exception cref="InvalidDataException">A font asset fails its pin, or an authored text holds a scalar its font's
    /// generated subset lacks.</exception>
    public void Reconcile(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var text = definition.Text;
        var directory = m_directory(arg: definition);

        if (text is null) {
            Catalog = null;
            GlyphAtlas = null;
            m_definition = null;
            m_resolvedDirectory = directory;

            return;
        }

        if (
            ReferenceEquals(
            objA: text,
            objB: m_definition
        ) &&
            string.Equals(
            a: directory,
            b: m_resolvedDirectory,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            // The expensive atlas is catalog-identity cached, but a definition revision may change creation runs,
            // screen rows, or creation-face overrides while preserving the exact same text catalog record. Re-run
            // content coverage so a live mutation cannot turn an unselected scalar into a silently blank glyph.
            PreflightContent(
                definition: definition,
                catalog: Catalog!
            );

            return;
        }

        var catalog = Resolve(
            definition: definition,
            directory: directory
        );

        Catalog = catalog;
        GlyphAtlas = new SdfGlyphAtlas(
            Rgba: catalog.ImageData.RgbaPixels.ToArray(),
            Width: ((uint)catalog.ImageData.Width),
            Height: ((uint)catalog.ImageData.Height)
        );
        m_definition = text;
        m_resolvedDirectory = directory;
    }
    /// <summary>Preflights a candidate catalog without changing the live binding.</summary>
    public bool TryValidate(WorldDefinition definition, string origin, out string reason) {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: origin);

        if (definition.Text is null) {
            reason = string.Empty;

            return true;
        }

        try {
            _ = Resolve(
                definition: definition,
                directory: WorldDocumentPaths.DirectoryOf(documentPath: origin)
            );
            reason = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is ArgumentException or InvalidDataException or IOException or KeyNotFoundException or UnauthorizedAccessException or NotSupportedException or OverflowException)) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }

    /// <summary>Gets the resolved logical font catalog, or null for a world declaring no text fonts.</summary>
    public PackedFontAtlasCatalog? Catalog { get; private set; }
    /// <summary>Gets the single packed texture the SDF engine binds.</summary>
    public SdfGlyphAtlas? GlyphAtlas { get; private set; }
}
