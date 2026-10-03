using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Text;

namespace Puck.World.Client;

/// <summary>
/// The glyph decals one world's text screens draw (<see cref="WorldScreenTextDecal"/>): one provider for every engine
/// screen slot, each answering the cells of the text its screen shows this frame through the world's own font catalog
/// and the ink colors bound in the world's own state mirror, or <see langword="null"/> for a screen showing no text. The
/// boot world's presentation and every world shown through a screen draw their text through one of these.
/// <para>A provider bakes a screen's cells once and keeps them while the text, the catalog and the bound colors hold;
/// <see cref="Invalidate"/> rebakes every screen. A slot is kept for every engine screen index, not only the screens
/// declared now, so a removed text screen clears its decal rather than leaving its last text on a later screen.</para>
/// </summary>
public sealed class WorldScreenDecals {
    private readonly Func<PackedFontAtlasCatalog?> m_catalog;
    private readonly WorldBakedColors m_colors;
    private readonly Dictionary<int, Func<SdfScreenDecalFrame?>> m_providers = [];
    private readonly Dictionary<int, (WorldScreenSource.Text Text, PackedFontAtlasCatalog Catalog, SdfScreenDecalFrame Frame)> m_cache = [];
    private readonly Func<int, WorldScreenSource.Text?> m_textAt;

    /// <summary>Initializes a new instance of the <see cref="WorldScreenDecals"/> class.</summary>
    /// <param name="textAt">The text a screen shows now, by screen index, or <see langword="null"/> for none.</param>
    /// <param name="catalog">The world's resolved font catalog, or <see langword="null"/> while it has none.</param>
    /// <param name="colors">The ink colors bound in the world's state mirror, which a moved binding rebakes.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldScreenDecals(Func<int, WorldScreenSource.Text?> textAt, Func<PackedFontAtlasCatalog?> catalog, WorldBakedColors colors) {
        ArgumentNullException.ThrowIfNull(argument: textAt);
        ArgumentNullException.ThrowIfNull(argument: catalog);
        ArgumentNullException.ThrowIfNull(argument: colors);

        m_textAt = textAt;
        m_catalog = catalog;
        m_colors = colors;

        for (var index = 0; (index < SdfProgramBuilder.MaxScreenSurfaces); index++) {
            var screen = index;

            m_providers[index] = () => Resolve(index: screen);
        }
    }

    /// <summary>Gets the provider of every engine screen slot, keyed by screen index.</summary>
    public IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>> Providers => m_providers;

    /// <summary>Rebakes every screen's decal at its next frame: a delivery may have changed its text.</summary>
    public void Invalidate() {
        m_cache.Clear();
        m_colors.Begin();
    }

    // This frame's cells of a screen's text, baked once and kept while its text, catalog and bound colors hold.
    private SdfScreenDecalFrame? Resolve(int index) {
        if (
            (m_textAt(arg: index) is not { } text) ||
            (m_catalog() is not { } catalog)
        ) {
            _ = m_cache.Remove(key: index);

            return null;
        }

        if (m_colors.TryTakeMove()) {
            m_cache.Clear();
            m_colors.Begin();
        }
        if (
            m_cache.TryGetValue(
                key: index,
                value: out var cached
            ) &&
            ReferenceEquals(
                objA: cached.Text,
                objB: text
            ) &&
            ReferenceEquals(
                objA: cached.Catalog,
                objB: catalog
            )
        ) {
            return cached.Frame;
        }

        var frame = WorldScreenTextDecal.Bake(
            catalog: catalog,
            colors: m_colors,
            text: text
        );

        m_cache[index] = (Text: text, Catalog: catalog, Frame: frame);

        return frame;
    }
}
