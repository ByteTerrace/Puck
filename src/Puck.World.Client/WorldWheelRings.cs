using Puck.Commands;
using Puck.Overlays;

namespace Puck.World.Client;

/// <summary>
/// One seat's radial rings as the overlay draws them: each sector's label and icon, and the hub's label, read from the
/// wheel's authored label and icon rows through the seat's routed <see cref="WorldStateMirror"/>. The rows are ordinary
/// live state, so renaming or re-iconing a sector on screen is a state write; a sector's own id is the cell key, and
/// <see cref="HubLabelKey"/> is the one reserved key.
/// <para>The rings are rebuilt only when the wheel, the mirror, or the mirror's installed document changes, or a
/// mirror slot one of the wheel's own cells reads changes; a state delivery that moves any other row leaves them as
/// they are. <see cref="Builds"/> counts the rebuilds.</para>
/// </summary>
/// <param name="resolveIcon">Resolves an icon name read from the icon row to atlas content;
/// <see cref="OverlayResolvedGlyph.None"/> for a name the table does not carry, or <see langword="null"/>.</param>
public sealed class WorldWheelRings(Func<string?, OverlayResolvedGlyph> resolveIcon) {
    /// <summary>The label-row cell the hub reads while nothing is hovered — what releasing now does. The one reserved
    /// key in a wheel's label row; every other key is a sector id.</summary>
    public const string HubLabelKey = "cancel";

    private readonly List<int> m_bound = [];
    private readonly WorldStateCells m_cells = new();
    private readonly Func<string?, OverlayResolvedGlyph> m_resolveIcon = (resolveIcon ?? throw new ArgumentNullException(paramName: nameof(resolveIcon)));

    private int m_builtAt;
    private int m_installs;
    private WorldStateMirror? m_mirror;

    private OverlayWheelRing[] m_rings = [];

    private BindingWheelView? m_wheel;

    /// <summary>Gets how many times the rings were rebuilt rather than answered from the cache.</summary>
    public int Builds { get; private set; }

    /// <summary>Gets the hub's label as of the last <see cref="Resolve"/>: the label row's <see cref="HubLabelKey"/>
    /// cell, or empty when the row or the cell is absent.</summary>
    public string HubLabel { get; private set; } = string.Empty;

    /// <summary>Returns the rings for a wheel read through a mirror, rebuilding them only when the wheel, the mirror,
    /// the mirror's installed document, or one of the slots the last build read changed.</summary>
    /// <param name="wheel">The presented wheel.</param>
    /// <param name="mirror">The seat's routed state mirror, or <see langword="null"/> before the seat has one; every
    /// cell then reads absent, so a label falls back to its sector's command.</param>
    /// <returns>The rings, innermost first, each sector at its wheel index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="wheel"/> is <see langword="null"/>.</exception>
    public OverlayWheelRing[] Resolve(BindingWheelView wheel, WorldStateMirror? mirror) {
        ArgumentNullException.ThrowIfNull(argument: wheel);

        if (Stale(
            mirror: mirror,
            wheel: wheel
        )) {
            Build(
                mirror: mirror,
                wheel: wheel
            );
        }

        return m_rings;
    }

    private void Build(BindingWheelView wheel, WorldStateMirror? mirror) {
        m_bound.Clear();
        m_mirror = mirror;
        m_wheel = wheel;

        var rings = new OverlayWheelRing[wheel.Rings.Count];

        for (var ringIndex = 0; (ringIndex < rings.Length); ringIndex++) {
            var ring = wheel.Rings[ringIndex];
            var sectors = new OverlayWheelSector[ring.Sectors.Count];

            for (var sectorIndex = 0; (sectorIndex < sectors.Length); sectorIndex++) {
                var sector = ring.Sectors[sectorIndex];

                sectors[sectorIndex] = new OverlayWheelSector(
                    Icon: m_resolveIcon(arg: SectorCell(
                        rowReference: wheel.IconRow,
                        sector: sector
                    )),
                    Label: (SectorCell(
                        rowReference: wheel.LabelRow,
                        sector: sector
                    ) ?? sector.Command)
                );
            }

            rings[ringIndex] = new OverlayWheelRing(
                Label: (ring.Label ?? ring.PageId),
                Sectors: sectors
            );
        }

        m_rings = rings;
        HubLabel = (Cell(
            key: HubLabelKey,
            rowReference: wheel.LabelRow
        ) ?? string.Empty);
        m_installs = (mirror?.Installs ?? 0);
        m_builtAt = (mirror?.Revision ?? 0);
        Builds++;
    }
    // A row's keyed cell as text; its slot is noted so the next resolve can ask whether it moved.
    private string? Cell(string? rowReference, string key) {
        var read = m_cells.TryText(
            key: key,
            mirror: m_mirror,
            rowReference: rowReference,
            slot: out var slot,
            text: out var text
        );

        if (slot >= 0) {
            m_bound.Add(item: slot);
        }

        return (read
            ? text
            : null
        );
    }
    private string? SectorCell(string? rowReference, BindingWheelSectorView sector) => ((sector.Id is { Length: > 0 } sectorId)
        ? Cell(
            key: sectorId,
            rowReference: rowReference
        )
        : null
    );
    private bool Stale(BindingWheelView wheel, WorldStateMirror? mirror) {
        if (
            (Builds == 0) ||
            !ReferenceEquals(
            objA: wheel,
            objB: m_wheel
        ) ||
            !ReferenceEquals(
            objA: mirror,
            objB: m_mirror
        )
        ) {
            return true;
        }

        if (mirror is null) {
            return false;
        }

        if (mirror.Installs != m_installs) {
            return true;
        }

        foreach (var slot in m_bound) {
            if (mirror.Changed(slot: slot) > m_builtAt) {
                return true;
            }
        }

        return false;
    }
}
