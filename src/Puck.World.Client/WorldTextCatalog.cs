using Puck.Assets;
using Puck.SdfVm;
using Puck.Text;

namespace Puck.World.Client;

/// <summary>Resolves the delivered world's hash-pinned font assets and exposes their one packed GPU atlas.</summary>
public sealed class WorldTextCatalog(WorldDefinitionSource source) {
    private readonly FontAtlasSourceResolver m_resolver = new(assetSource: new FileSystemAssetSource());
    private readonly WorldDefinitionSource m_source = (source ?? throw new ArgumentNullException(paramName: nameof(source)));

    private TextFontCatalogDefinition? m_definition;
    private string? m_origin;

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
    private PackedFontAtlasCatalog Resolve(WorldDefinition definition, string origin) {
        var text = (definition.Text ?? throw new ArgumentException(
            message: "The world declares no text catalog.",
            paramName: nameof(definition)
        ));
        var basePath = ((Path.GetDirectoryName(path: origin) is { Length: > 0 } directory)
            ? directory
            : AppContext.BaseDirectory
        );
        var catalog = m_resolver.ResolveCatalog(
            basePath: basePath,
            definition: text
        );

        PreflightContent(
            catalog: catalog,
            definition: definition
        );

        return catalog;
    }

    /// <summary>Reconciles a newly delivered definition against the tracked document origin.</summary>
    public void Reconcile(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var text = definition.Text;
        var origin = m_source.SourcePath;

        if (text is null) {
            Catalog = null;
            GlyphAtlas = null;
            m_definition = null;
            m_origin = origin;

            return;
        }

        if (
            ReferenceEquals(
            objA: text,
            objB: m_definition
        ) &&
            string.Equals(
            a: origin,
            b: m_origin,
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
            origin: origin
        );

        Catalog = catalog;
        GlyphAtlas = new SdfGlyphAtlas(
            Rgba: catalog.ImageData.RgbaPixels,
            Width: ((uint)catalog.ImageData.Width),
            Height: ((uint)catalog.ImageData.Height)
        );
        m_definition = text;
        m_origin = origin;
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
                origin: origin
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
