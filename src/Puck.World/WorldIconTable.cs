using Puck.Input.Devices;
using Puck.Overlays;

namespace Puck.World;

/// <summary>
/// Resolves the boot document's authored icon table (<c>icons.icons</c> + <c>icons.badges</c>) into atlas-ready
/// content: this is the ONE place an icon NAME, a font id, or a codepoint is known — <c>Puck.Overlays</c> sees only
/// the resulting <see cref="OverlayResolvedGlyph"/> indices (see the icons-are-authored-data charter: the engine
/// owns zero icons). <see cref="ExtraCodePoints"/> is the caller-owned, deterministically ordered codepoint list
/// <c>OverlayGlyphAtlasSet.LoadOverlayPack</c> bakes into the shared atlas AFTER the ASCII block — this table's own
/// index assignment (<see cref="OverlayGlyphSdfPack.AsciiGlyphCount"/> + position in that list) is the single source
/// of truth both the pack builder and this table's name resolution read.
/// </summary>
/// <remarks>
/// One instance per booted world, built once from the boot document — the same boot-time-fixed contract
/// <c>WorldOverlayCapacity.FromSchema()</c> already carries for HUD/bar capacity; a live <c>world.load</c> to a
/// document with a different icon repertoire keeps resolving against THIS table (icon names it does not carry
/// resolve to <see cref="OverlayResolvedGlyph.None"/>, a blank plate, never a crash).
/// </remarks>
public sealed class WorldIconTable {
    private readonly Dictionary<string, string> m_badgeDefaultIconBySource = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Family), string> m_badgeOverrideIconBySourceFamily = [];
    private readonly Dictionary<string, OverlayResolvedGlyph> m_icons = new(comparer: StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="WorldIconTable"/> class from the boot document's
    /// <c>icons</c> section (absence resolves every name to <see cref="OverlayResolvedGlyph.None"/>).</summary>
    /// <param name="definition">The boot document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public WorldIconTable(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var codePoints = new List<int>();
        var codePointIndex = new Dictionary<int, int>();

        foreach (var row in definition.Icons.Icons) {
            if (row?.Name is not { Length: > 0 } name) {
                continue;
            }

            if (
                (row.Glyph is { } glyphRef) &&
                WorldIconGlyphRef.TryResolveCodePoint(
                glyph: glyphRef.Glyph,
                codePoint: out var codePoint
            )
            ) {
                if (!codePointIndex.TryGetValue(
                    key: codePoint,
                    value: out var index
                )) {
                    index = codePoints.Count;
                    codePoints.Add(item: codePoint);
                    codePointIndex[codePoint] = index;
                }

                m_icons[name] = new OverlayResolvedGlyph(
                    Glyph0: ((ushort)((OverlayGlyphSdfPack.AsciiGlyphCount + index) + 1)),
                    Glyph1: 0,
                    IsLabel: false
                );
            } else if (row.Label is { Length: > 0 } label) {
                m_icons[name] = new OverlayResolvedGlyph(
                    Glyph0: AsciiIndex(character: label[0]),
                    Glyph1: ((label.Length > 1)
                    ? AsciiIndex(character: label[1])
                    : ((ushort)0)),
                    IsLabel: true
                );
            }
        }

        ExtraCodePoints = codePoints;

        foreach (var badge in definition.Icons.Badges) {
            if (badge?.Source is not { Length: > 0 } source) {
                continue;
            }

            m_badgeDefaultIconBySource[source] = (badge.Icon ?? string.Empty);

            foreach (var over in badge.Overrides) {
                if (over?.Family is { Length: > 0 } family) {
                    m_badgeOverrideIconBySourceFamily[(source, family)] = (over.Icon ?? string.Empty);
                }
            }
        }
    }

    /// <summary>Gets the appended codepoint list, in the order this table assigns them atlas indices — the exact
    /// list <c>OverlayGlyphAtlasSet.LoadOverlayPack</c> must be called with.</summary>
    public IReadOnlyList<int> ExtraCodePoints { get; }

    private static ushort AsciiIndex(char character) {
        var index = OverlayGlyphSdfPack.GlyphIndex(codePoint: character);

        return ((index >= 0)
            ? ((ushort)(index + 1))
            : ((ushort)0)
        );
    }

    /// <summary>Resolves an icon name (e.g. <c>action.jump</c>, <c>edit.next</c>) to its atlas content.</summary>
    /// <param name="name">The icon name, or <see langword="null"/>/empty.</param>
    /// <returns>The resolved content, or <see cref="OverlayResolvedGlyph.None"/> when unresolved.</returns>
    public OverlayResolvedGlyph ResolveIcon(string? name) =>
        (((name is { Length: > 0 }) && m_icons.TryGetValue(
            key: name,
            value: out var resolved
        ))
            ? resolved
            : OverlayResolvedGlyph.None
        );

    /// <summary>Resolves a physical control's badge content for a connected controller family (the family override
    /// seam, checked before the row's default icon). The ONE badge door: a bar slot, a modifier indicator, and a
    /// chord hint all arrive here with the same input source id, and a control the badge table carries no row for
    /// simply draws no badge — authoring the row is what makes it badgeable.</summary>
    /// <param name="source">The physical control's input source id.</param>
    /// <param name="family">The connected controller family.</param>
    /// <returns>The resolved content, or <see cref="OverlayResolvedGlyph.None"/> when the source carries no badge
    /// row.</returns>
    public OverlayResolvedGlyph ResolveBadge(string source, GamepadType family) {
        if (m_badgeOverrideIconBySourceFamily.TryGetValue(
            key: (source, family.ToString()),
            value: out var overrideIcon
        )) {
            return ResolveIcon(name: overrideIcon);
        }

        return (m_badgeDefaultIconBySource.TryGetValue(
            key: source,
            value: out var icon
        )
            ? ResolveIcon(name: icon)
            : OverlayResolvedGlyph.None
        );
    }
}
