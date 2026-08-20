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
    private readonly Dictionary<string, string> m_badgeDefaultIconByButton = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<(string Button, string Family), string> m_badgeOverrideIconByButtonFamily = [];
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
            if (badge?.Button is not { Length: > 0 } button) {
                continue;
            }

            m_badgeDefaultIconByButton[button] = (badge.Icon ?? string.Empty);

            foreach (var over in badge.Overrides) {
                if (over?.Family is { Length: > 0 } family) {
                    m_badgeOverrideIconByButtonFamily[(button, family)] = (over.Icon ?? string.Empty);
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
    // The one badge lookup by physical-button NAME, family override checked before the row's default — shared so a
    // flag button and a pseudo-button (a trigger, which has no GamepadButtons flag) resolve identically.
    private OverlayResolvedGlyph ResolveBadgeByName(string buttonName, GamepadType family) {
        if (m_badgeOverrideIconByButtonFamily.TryGetValue(
            key: (buttonName, family.ToString()),
            value: out var overrideIcon
        )) {
            return ResolveIcon(name: overrideIcon);
        }

        return (m_badgeDefaultIconByButton.TryGetValue(
            key: buttonName,
            value: out var icon
        )
            ? ResolveIcon(name: icon)
            : OverlayResolvedGlyph.None
        );
    }

    /// <summary>Resolves a physical button's badge content for a connected controller family (the family override
    /// seam, checked before the row's default icon).</summary>
    /// <param name="button">The physical button (one flag).</param>
    /// <param name="family">The connected controller family.</param>
    /// <returns>The resolved content, or <see cref="OverlayResolvedGlyph.None"/> when the button carries no badge
    /// row.</returns>
    public OverlayResolvedGlyph ResolveBadge(GamepadButtons button, GamepadType family) =>
        ResolveBadgeByName(
            buttonName: button.ToString(),
            family: family
        );
    /// <summary>Resolves a modifier's provider-neutral input source id (the two triggers and the two shoulders —
    /// the only sources a binding profile's modifier chord ever names) to its badge content, through the SAME
    /// family-aware badge lookup <see cref="ResolveBadge(GamepadButtons, GamepadType)"/> reads (a trigger has no
    /// <see cref="GamepadButtons"/> flag of its own — an analog axis — so it is addressed by the pseudo-button
    /// names <c>LeftTrigger</c>/<c>RightTrigger</c> a badge row's <c>button</c> field may also carry).</summary>
    /// <param name="source">The input source id.</param>
    /// <param name="family">The connected controller family.</param>
    /// <returns>The resolved content, or <see cref="OverlayResolvedGlyph.None"/> when unresolved.</returns>
    public OverlayResolvedGlyph ResolveModifierSource(string source, GamepadType family) {
        var buttonName = source switch {
            Puck.Input.InputSources.Gamepad.LeftTrigger => "LeftTrigger",
            Puck.Input.InputSources.Gamepad.RightTrigger => "RightTrigger",
            Puck.Input.InputSources.Gamepad.LeftShoulder => nameof(GamepadButtons.LeftShoulder),
            Puck.Input.InputSources.Gamepad.RightShoulder => nameof(GamepadButtons.RightShoulder),
            _ => null,
        };

        return ((buttonName is null)
            ? OverlayResolvedGlyph.None
            : ResolveBadgeByName(
                buttonName: buttonName,
                family: family
            )
        );
    }
}
